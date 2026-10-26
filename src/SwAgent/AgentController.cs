using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Chat;
using SwAgent.Infrastructure;
using SwAgent.ModelFiles;
using SwAgent.Plugins.Hooks;
using SwAgent.Plugins.LocalLink;
using SwAgent.Scripting;
using SwAgent.Tools;
using SwAgent.UI;
using SwAgent.Voice;
using SwAgent.Watch;
using Xarial.XCad.Documents;
using Xarial.XCad.SolidWorks;
using Xarial.XCad.SolidWorks.Documents;

namespace SwAgent
{
    /// <summary>
    /// Glue between SolidWorks documents, their chats, the tool registry and the web UI.
    /// Everything here runs on the SolidWorks UI thread.
    /// </summary>
    internal sealed class AgentController : IDisposable
    {
        private readonly ISwApplication _app;
        private readonly ToolRegistry _tools;
        private readonly ScriptHost _scripts;
        private readonly Dictionary<string, ChatDocumentHandler> _docs = new Dictionary<string, ChatDocumentHandler>();
        private readonly ChatDocument _scratch = ChatDocument.CreateScratch();

        /// <summary>The user's chats, not tied to documents (they follow the work from file to file).</summary>
        private readonly WorkspaceChats _workspace = new WorkspaceChats();
        private readonly Timer _selectionTimer = new Timer { Interval = 150 };
        private readonly Timer _themeTimer = new Timer { Interval = 2500 };
        private string _lastTheme;
        private readonly Timer _activeTimer = new Timer { Interval = 1000 };
        private readonly Timer _updateTimer = new Timer { Interval = 6 * 60 * 60 * 1000 };

        /// <summary>Update feed (GitHub Releases); checked at start-up and every few hours.</summary>
        internal Updater Updates { get; } = new Updater();

        /// <summary>Set once the install-after-exit helper has been started, so shutdown doesn't start it twice.</summary>
        internal bool UpdateHelperStarted { get; private set; }

        /// <summary>Auto-update: on unless the user turned it off (null = not asked yet, treated as off).</summary>
        internal bool AutoUpdate => (bool?)_settings["autoUpdate"] == true;

        /// <summary>The panel's settings (settings.json), also before the panel has loaded.</summary>
        internal JObject Settings => _settings;

        /// <summary>Plugin hooks and MCP servers (Plugins/Hooks, Mcp); its plugins come from the plugin core (PluginManager).</summary>
        internal RuntimeHost Runtime { get; }
        private string _lastActiveKey;
        private ChatDocumentHandler _selectionSource;
        private WebBridge _bridge;
        private JObject _settings = SettingsStore.LoadSettings();

        /// <summary>Tool inputs as a form in the chat panel (ToolRunner opens them here unless the user chose the page).</summary>
        private readonly ToolFormBridge _forms;
        private readonly VoiceController _voice;
        private WatchRecorder _watch;

        /// <summary>What SolidWorks was running when it closed unexpectedly last time (CrashGuard), until dismissed.</summary>
        internal JObject Crash { get; set; }

        /// <summary>True when tools changed since SolidWorks started: the ribbon only picks them up after a restart.</summary>
        internal Func<bool> RibbonStale { get; set; }
        private bool _agentRunning;

        /// <summary>The Claude MCP / Codex MCP plugins' local link (pipe server, tickets); made when the panel first attaches.</summary>
        private LinkHost _link;

        public AgentController(ISwApplication app, ToolRegistry tools, ScriptHost scripts)
        {
            _app = app;
            _tools = tools;
            _scripts = scripts;
            Runtime = new RuntimeHost(Plugins.PluginManager.Instance, Plugins.Hooks.Session.Open(), (name, data) => _bridge?.Emit(name, data));
            Runtime.ApplySettings(_settings);
            _forms = new ToolFormBridge(app, scripts);
            // Push-to-talk voice messages and the markup pen.
            _voice = new VoiceController(app, () => _settings, ChatKeyOf);
            _selectionTimer.Tick += Safe.Tick("Selection update", () =>
            {
                _selectionTimer.Stop();
                if (_selectionSource != null) EmitSelection(_selectionSource);
            });

            // SolidWorks raises no event when the interface theme changes, so poll the (cheap) preferences.
            _themeTimer.Tick += Safe.Tick("Theme poll", () =>
            {
                var theme = ReadTheme();
                var json = theme?.ToString(Newtonsoft.Json.Formatting.None);
                if (json == null || json == _lastTheme) return;
                _lastTheme = json;
                _bridge?.Emit("theme.changed", theme);
                ThemeChanged?.Invoke(theme);
            });
            _themeTimer.Start();

            Updates.Changed += () => Safe.Run("Update notice", () =>
            {
                _bridge?.Emit("update.changed", Updates.ToJson());
                // Auto-update: fetch new versions in the background; they install when SolidWorks closes.
                if (AutoUpdate && Updates.Status == Updater.State.Available) _ = Updates.DownloadAsync();
            });
            _updateTimer.Tick += Safe.Tick("Update check", () => _ = Updates.CheckAsync());
            _updateTimer.Start();
            var firstCheck = new Timer { Interval = 15000 }; // after start-up has settled
            firstCheck.Tick += Safe.Tick("First update check", () =>
            {
                firstCheck.Stop();
                firstCheck.Dispose();
                _ = Updates.CheckAsync();
            });
            firstCheck.Start();

            // DocumentActivated isn't raised for File > New (and can be missed during start-up): follow the
            // active document by polling too.
            _activeTimer.Tick += (s, e) =>
            {
                try
                {
                    var handler = HandlerFor(_app.Documents.Active);
                    var key = handler?.Chat.Key;
                    if (key == null || key == _lastActiveKey) return;
                    _lastActiveKey = key;
                    _bridge?.Emit("doc.activated", DocInfo(handler));
                    EmitSelection(handler);
                }
                catch (Exception ex)
                {
                    Log.Warn("Active document poll failed: " + ex.Message);
                }
            };
            _activeTimer.Start();
        }

        public void AttachBridge(WebBridge bridge)
        {
            _bridge = bridge;

            bridge.Register("app.getState", p => GetState());
            // The agent's drawings on the view (SceneOverlay): the panel shows a Clear button and clears on the next message.
            bridge.Register("overlay.clear", p =>
            {
                UI.SceneOverlay.ClearAll();
                return null;
            });
            if (!_overlaySubscribed)
            {
                _overlaySubscribed = true; // the bridge is attached again when the panel reloads
                UI.SceneOverlay.Changed += count => Safe.Run("Overlay notice", () => _bridge?.Emit("overlay.changed", new JObject { ["count"] = count }));
            }
            // The panel needs the user to see it (a tool run asks a question or needs approval).
            bridge.Register("app.showPane", p =>
            {
                ShowPaneRequested?.Invoke();
                return null;
            });
            bridge.Register("settings.save", p =>
            {
                _settings = (JObject)p;
                SettingsStore.SaveSettings(_settings);
                _voice.ApplySettings();
                Plugins.PluginSettings.Apply(_settings); // before the runtime: Runnable() reads pluginHooks / pluginMcp from it
                Runtime.ApplySettings(_settings);
                return null;
            });
            bridge.Register("crash.dismiss", p =>
            {
                Crash = null;
                return null;
            });
            bridge.RegisterAsync("app.saveAndRestart", async p => await SaveAndRestart((bool?)p?["saveDocuments"] == true, p?["panel"] as JObject));
            bridge.Register("userTools.run", p =>
            {
                // From the panel (e.g. right after creating a tool, before the ribbon has its button).
                _ = RunToolAsync((string)p["name"], null);
                return null;
            });
            _forms.Attach(bridge);
            bridge.Register("update.status", p => Updates.ToJson());
            bridge.RegisterAsync("update.check", async p =>
            {
                await Updates.CheckAsync();
                return Updates.ToJson();
            });
            bridge.Register("update.unsavedDocuments", p => UnsavedDocuments());
            bridge.RegisterAsync("update.restartAndInstall", async p => await RestartAndInstall((bool?)p?["saveDocuments"] == true, p?["panel"] as JObject));
            // { provider? }: without one, Anthropic (older panels).
            bridge.Register("apiKey.get", p => SettingsStore.ApiKeyGetRpc(p));
            bridge.Register("apiKey.set", p => SettingsStore.ApiKeySetRpc(p));

            bridge.Register("chat.load", p =>
            {
                if ((string)p["docKey"] == WorkspaceChats.Key)
                    return new JObject { ["docKey"] = WorkspaceChats.Key, ["docId"] = WorkspaceChats.Key, ["recovered"] = false, ["data"] = _workspace.Data };
                // An open document's own chats (saved inside the file by older versions): the panel imports them.
                var chat = ResolveChat((string)p["docKey"]);
                return new JObject
                {
                    ["docKey"] = chat.Key,
                    ["docId"] = chat.DocId,
                    ["recovered"] = chat.Recovered,
                    ["data"] = chat.Data,
                };
            });
            bridge.Register("chat.save", p =>
            {
                Runtime.OnChatSaved(p["data"] as JObject); // the transcript hooks read
                if ((string)p["docKey"] == WorkspaceChats.Key)
                {
                    _workspace.Update((JObject)p["data"]);
                    return new JObject { ["revision"] = _workspace.Revision };
                }
                var chat = ResolveChat((string)p["docKey"]);
                chat.Update((JObject)p["data"]);
                if (!chat.IsScratch && ((bool?)_settings["markDocumentDirty"] ?? true))
                {
                    try
                    {
                        chat.Doc?.Model?.SetSaveFlag(); // so "save changes?" also protects the conversation
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("SetSaveFlag failed: " + ex.Message);
                    }
                }
                return new JObject { ["revision"] = chat.Revision };
            });

            bridge.Register("tools.list", p => Runtime.MergeTools(_tools.Definitions()));
            bridge.RegisterAsync("tools.run", async p =>
            {
                // MCP tools run in their server's process, never in SolidWorks: no busy gate, overlay or fault check.
                if (RuntimeHost.IsMcp((string)p["name"]))
                    return (object)await Runtime.CallAsync((string)p["name"], p["input"] as JObject, (string)p["threadId"], (string)p["toolUseId"]);
                var doc = DocumentFor((string)p["docKey"]);
                var refs = (p["refs"] as JObject)?.Properties().ToDictionary(x => x.Name, x => (string)x.Value);
                return (object)await RunToolAsync((string)p["name"], p["input"] as JObject, doc, (string)p["threadId"], (string)p["toolUseId"], refs, (string)p["tool"]);
            });
            // "Undo everything the agent did since this message" (offered when a message is edited or resent).
            bridge.Register("history.undoable", p =>
            {
                var model = DocumentFor((string)p["docKey"])?.Model;
                return new JObject { ["changes"] = UndoLedger.Undoable(model, ToolUseIds(p)) };
            });
            bridge.Register("history.undoSince", p =>
            {
                var model = DocumentFor((string)p["docKey"])?.Model;
                if (model == null) return new JObject { ["ok"] = false, ["message"] = "The document isn't open." };
                var (ok, steps, message) = UndoLedger.UndoTo(model, ToolUseIds(p));
                return new JObject { ["ok"] = ok, ["steps"] = steps, ["message"] = message };
            });
            bridge.Register("tools.cancel", p =>
            {
                _scripts.Cancel((string)p["threadId"]);
                Runtime.Cancel((string)p["threadId"]);
                return null;
            });
            bridge.Register("session.reset", p =>
            {
                _scripts.Reset((string)p["threadId"]);
                return null;
            });

            bridge.Register("selection.get", p =>
            {
                var key = (string)p["docKey"];
                return _docs.TryGetValue(key ?? "", out var h) ? SelectionPayload(h) : null;
            });
            bridge.Register("selection.snapshot", p =>
            {
                var doc = DocumentFor((string)p["docKey"]);
                if (doc?.Model == null) return null;
                // ask_selection reads up to 500 entities (a message's context block, the default 40); "total" lets the
                // panel say when the list was cut.
                var max = Math.Max(1, Math.Min(500, (int?)p["max"] ?? 40));
                var snap = SelectionTools.Snapshot(_app.Sw, doc.Model, max);
                snap["total"] = SelectedCount(doc.Model);
                return snap;
            });
            _voice.AttachBridge(bridge);
            Runtime.Attach(bridge); // hooks.fire, session.*, mcp.*; starts the MCP servers the first time
            AttachLink(bridge); // localLink.*; the Claude/Codex pipe follows the link plugins
            bridge.Register("agent.activity", p =>
            {
                // The UI reports when a run starts and ends; silence SolidWorks' rebuild-error beeps in between.
                _agentRunning = (bool?)p["running"] == true;
                if (_watch != null) _watch.AgentBusy = _agentRunning;
                if ((bool?)p["running"] == true)
                {
                    if ((bool?)_settings["muteErrorSounds"] ?? true) SoundMuter.Begin();
                }
                else
                {
                    SoundMuter.End();
                }
                return null;
            });
            // Files for the chat: picked, dropped on the panel, or a tool's files input. 3D models are described
            // (views + measurements) separately, so the composer can show the file at once and fill it in when ready.
            bridge.RegisterAsync("files.pick", async p => await PickFiles());
            bridge.RegisterAsync("files.describe", async p => (object)await FileAttachments.EntryAsync(_app.Sw, (string)p["path"]));
            bridge.RegisterAsync("files.entries", async p =>
            {
                var list = new JArray();
                foreach (var path in (p["paths"] as JArray ?? new JArray()).Select(x => (string)x))
                {
                    list.Add(await FileAttachments.EntryAsync(_app.Sw, path, describeModels: false));
                }
                return list;
            });
            bridge.FilesDropped += paths => bridge.Emit("files.dropped", new JObject { ["paths"] = new JArray(paths) });

            // "Watch me": record what the user does so the agent can turn it into a tool or workflow.
            bridge.RegisterAsync("watch.start", async p =>
            {
                var doc = DocumentFor((string)p["docKey"]);
                var model = doc?.Model ?? throw new InvalidOperationException("Open a part or assembly to record what you do in it.");
                StopWatch();
                var key = HandlerFor(doc)?.Chat.Key ?? (string)p["docKey"];
                _watch = await WatchRecorder.StartAsync(_app.Sw, model, key);
                _watch.AgentBusy = _agentRunning;
                _watch.Changed += status => _bridge?.Emit("watch.log", status);
                return _watch.Status();
            });
            bridge.RegisterAsync("watch.stop", async p =>
            {
                var w = _watch ?? throw new InvalidOperationException("Watch me isn't recording.");
                _watch = null;
                return await w.StopAsync();
            });
            bridge.Register("watch.cancel", p =>
            {
                StopWatch();
                return null;
            });
            bridge.Register("watch.status", p => _watch?.Status() ?? new JObject { ["active"] = false });

            bridge.Register("userTools.list", p => new JArray(UserTools.ListTools()));
            bridge.Register("userTools.delete", p =>
            {
                UserTools.Delete((string)p["name"]);
                return null;
            });
            bridge.Register("userTools.openFolder", p =>
            {
                Process.Start(new ProcessStartInfo(UserTools.Root) { UseShellExecute = true });
                return null;
            });
            bridge.Register("userTools.setValue", p =>
            {
                // Only the settings the user may change from the panel. A built-in tool's go to builtin-settings.json
                // (its folder is replaced on every install), and a null value resets it to what the tool ships with; a new
                // category marks the ribbon stale until a restart.
                var key = (string)p["key"];
                if (key != "intelligence" && key != "confirm" && key != "category") throw new InvalidOperationException("Not a tool setting: " + key);
                UserTools.SetValue((string)p["name"], key, (string)p["value"]);
                return null;
            });
            bridge.Register("userTools.setIcon", p =>
            {
                UserTools.SetIconPng((string)p["name"], Convert.FromBase64String((string)p["png"]));
                return null;
            });
            UserTools.Changed -= OnUserToolsChanged;
            UserTools.Changed += OnUserToolsChanged;
            Plugins.PluginsBridge.Attach(bridge); // plugins.* RPCs: store, marketplaces, commands, skills, agents
            KnowledgeBridge.Attach(bridge); // knowledge.index (helpers and API notes for the system prompt), knowledge.changed
            Memory.MemoryBridge.Attach(bridge); // memory.* (standing instructions: prompt, Options, Undo), userTools.setHidden
            bridge.Register("doc.activate", p =>
            {
                var model = DocumentFor((string)p["docKey"])?.Model;
                if (model != null)
                {
                    var errors = 0;
                    _app.Sw.ActivateDoc3(model.GetTitle(), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref errors);
                }
                return null;
            });
            bridge.Register("app.openExternal", p =>
            {
                ChatPaneControl.OpenExternal((string)p["url"]);
                return null;
            });
            bridge.Register("app.openLogs", p =>
            {
                Process.Start(new ProcessStartInfo(Paths.LogDir) { UseShellExecute = true });
                return null;
            });
        }

        public void SendCommand(string name, object args = null) => _bridge?.Emit("command", new JObject
        {
            ["name"] = name,
            ["args"] = args == null ? null : JToken.FromObject(args),
        });

        // ---- document lifecycle (called by ChatDocumentHandler / xCAD events) ----

        public void OnChatReadFromFile(ChatDocumentHandler handler) => Safe.Run("Chat read", () => _bridge?.Emit("doc.chatChanged", new JObject { ["key"] = handler.Chat.Key }));

        public void OnDocumentOpened(ChatDocumentHandler handler) => _docs[handler.Chat.Key] = handler;

        public void OnDocumentClosed(ChatDocumentHandler handler) => Safe.Run("Document closed", () =>
        {
            if (handler.Chat == null) return;
            _docs.Remove(handler.Chat.Key);
            _closedDocIds[handler.Chat.Key] = handler.Chat.DocId;
            if (_selectionSource == handler) _selectionSource = null;
            _forms.DocumentClosed(handler.Chat.Doc?.Model);
            if (_watch?.DocKey == handler.Chat.Key)
            {
                StopWatch();
                _bridge?.Emit("watch.ended", new JObject { ["reason"] = "The document was closed, so Watch me stopped recording." });
            }
            _bridge?.Emit("doc.closed", new JObject { ["key"] = handler.Chat.Key });
        });

        public void OnDocumentActivated(IXDocument doc) => Safe.Run("Document activated", () =>
        {
            var handler = HandlerFor(doc);
            if (handler == null) return;
            _bridge?.Emit("doc.activated", DocInfo(handler));
            EmitSelection(handler);
        });

        public void OnDocumentSaved(ChatDocumentHandler handler)
        {
            // Title/path change after Save As; refresh once SolidWorks has finished the save.
            var t = new Timer { Interval = 700 };
            t.Tick += Safe.Tick("Refresh after save", () =>
            {
                t.Stop();
                t.Dispose();
                if (_docs.ContainsKey(handler.Chat.Key)) _bridge?.Emit("doc.updated", DocInfo(handler));
            });
            t.Start();
        }

        public void OnSelectionChanged(ChatDocumentHandler handler)
        {
            _selectionSource = handler;
            _selectionTimer.Stop();
            _selectionTimer.Start();
        }

        // ---- helpers ----

        // ---- tools (ribbon buttons / hotkeys) ----

        /// <summary>Run one of the user's Tools (ribbon button / hotkey); see ToolRunner.</summary>
        public Task RunToolAsync(string name, Action showPane) =>
            (_toolRunner ?? (_toolRunner = new ToolRunner(_app, _tools, _scripts, SendCommand, Status, _forms, () => _settings))).RunAsync(name, showPane);

        private ToolRunner _toolRunner;

        /// <summary>
        /// Runs one of the add-in's own tools (not an MCP one) for the panel's tools.run or the local link, on the UI thread:
        /// a stale overlay goes first, nothing touches the model after a NativeFault, and a model tool waits for a command the
        /// user started to end (SwBusyGate) before it runs; what it leaves behind is recorded for the next call.
        /// </summary>
        internal async Task<JObject> RunToolAsync(string name, JObject input, ISwDocument doc, string threadId, string toolUseId, IReadOnlyDictionary<string, string> refs, string runTool)
        {
            var ctx = new ToolContext(_app, doc, threadId, _tools, refs) { ToolUseId = toolUseId, RunTool = runTool };
            // A drawing the model has moved on from (the overlay saw a change) goes now, at a safe moment.
            Safe.Run("Clear a stale overlay", UI.SceneOverlay.ClearIfStale);
            // After a fault inside SolidWorks (NativeFault) its memory may be damaged: no more model work until a restart.
            if (NativeFault.Tripped != null && !SwBusyGate.ModelFree(name))
                return ToolResult.Error(NativeFault.RestartAdvice).ToJson();
            // A tool that touches the model never runs while the user is in a command (a sketch edit, an open
            // PropertyManager page, a rebuild): a geometry call then can crash SolidWorks (SwBusy, SceneOverlay). Wait for
            // the command to end; the add-in's own tool page doesn't count.
            var touchesModel = !SwBusyGate.ModelFree(name) && _app.Sw != null;
            if (touchesModel)
            {
                // Only a command the user started since the agent's last call blocks (SwBusyGate): what the agent left
                // behind itself - a sketch it opened, a failed feature's leftovers - is the agent's to use or clean up.
                SwCommandState state = null;
                var waited = await SwBusyGate.WaitAsync(() =>
                {
                    state = SwBusy.State(_app.Sw);
                    return SwBusyGate.MustWait(state, SwBusyGate.AgentLeft) && !ToolPage.AnyOpen;
                }, SwBusyGate.Timeout);
                if (!waited)
                {
                    Log.Info($"Tool {name} not run: SolidWorks busy for {SwBusyGate.Timeout.TotalSeconds:0} s ({state?.Describe()}; the agent's last call left: {SwBusyGate.AgentLeft ?? "nothing"})");
                    return ToolResult.Error($"SolidWorks is in a command the user started: {state?.Describe()}. Nothing was run. " +
                        "Tell the user exactly that (what is open) and ask them to finish or close it, then try again.").ToJson();
                }
            }
            var result = await _tools.InvokeAsync(name, input, ctx);
            if (touchesModel) Safe.Run("Record the command state", () => SwBusyGate.AgentLeft = SwBusy.State(_app.Sw).Key);
            return result.ToJson();
        }

        // ---- the local link (Claude MCP / Codex MCP plugins) ----

        /// <summary>
        /// The link's panel RPCs (again on every panel reload); the first time, the LinkHost itself. Its SolidWorks work is
        /// posted to this (the UI) thread's SynchronizationContext, never run on a pipe thread.
        /// </summary>
        private void AttachLink(WebBridge bridge)
        {
            if (_link == null)
            {
                _link = new LinkHost(Plugins.PluginManager.Instance, Runtime.Session, Runtime.QueueLinkWake, LinkHost.UiRunner(System.Threading.SynchronizationContext.Current),
                    LinkStatus, RunLinkToolAsync, Updates.CurrentVersion.ToString(3));
                Runtime.PanelReloaded += _link.PanelReloaded;
                _link.Start();
            }
            _link.Attach(bridge);
        }

        /// <summary>The link's status, on the UI thread: SolidWorks' revision and the active document.</summary>
        private JObject LinkStatus()
        {
            JObject document = null;
            try
            {
                var model = _app.Documents.Active?.Model;
                if (model != null)
                {
                    var type = (swDocumentTypes_e)model.GetType();
                    document = new JObject
                    {
                        ["title"] = model.GetTitle(),
                        ["type"] = type == swDocumentTypes_e.swDocPART ? "part" : type == swDocumentTypes_e.swDocASSEMBLY ? "assembly" : type == swDocumentTypes_e.swDocDRAWING ? "drawing" : "other",
                        ["path"] = string.IsNullOrEmpty(model.GetPathName()) ? null : model.GetPathName(),
                    };
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Link status: reading the active document failed: " + ex.Message);
            }
            return new JObject { ["solidWorks"] = SafeRevision(), ["document"] = document };
        }

        /// <summary>A read-only model tool for the link, on the UI thread, on the active document (null: none open and it needs one).</summary>
        private Task<JObject> RunLinkToolAsync(string tool, JObject input, bool needsDocument)
        {
            var doc = _app.Documents.Active;
            if (doc == null && needsDocument) return Task.FromResult<JObject>(null);
            return RunToolAsync(tool, input ?? new JObject(), doc, null, null, null, null);
        }

        private void Status(string text)
        {
            try
            {
                ((IFrame)_app.Sw.Frame())?.SetStatusBarText(text);
            }
            catch
            {
                // Cosmetic only.
            }
        }

        private void StopWatch()
        {
            _watch?.Dispose();
            _watch = null;
        }

        /// <summary>Open-file dialog for chat attachments; returns entries (3D models still to be described).</summary>
        private async Task<object> PickFiles()
        {
            string[] paths;
            using (var dlg = new OpenFileDialog
            {
                Title = "Attach files for the agent",
                Multiselect = true,
                Filter = "Images, PDFs, drawings and 3D models|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.pdf;*.slddrw;*.stl;*.3mf;*.obj;*.step;*.stp;*.iges;*.igs;*.x_t;*.x_b;*.sldprt;*.sldasm|" +
                         "SolidWorks drawings, attached as PDF (*.slddrw)|*.slddrw|" +
                         "3D models (*.stl;*.3mf;*.obj;*.step;*.stp;*.iges;*.igs;*.x_t;*.x_b;*.sldprt;*.sldasm)|*.stl;*.3mf;*.obj;*.step;*.stp;*.iges;*.igs;*.x_t;*.x_b;*.sldprt;*.sldasm|" +
                         "Images (*.png;*.jpg;*.jpeg;*.gif;*.webp)|*.png;*.jpg;*.jpeg;*.gif;*.webp|PDF documents (*.pdf)|*.pdf|All files (*.*)|*.*",
            })
            {
                var owner = new Win32Window(new IntPtr(((IFrame)_app.Sw.Frame()).GetHWndx64()));
                if (dlg.ShowDialog(owner) != DialogResult.OK) return new JArray();
                paths = dlg.FileNames;
            }
            var list = new JArray();
            foreach (var path in paths) list.Add(await FileAttachments.EntryAsync(_app.Sw, path, describeModels: false));
            return list;
        }

        private sealed class Win32Window : IWin32Window
        {
            public Win32Window(IntPtr handle) => Handle = handle;

            public IntPtr Handle { get; }
        }

        // ---- updates ----

        /// <summary>Open documents with unsaved changes; those never saved can't be saved without asking for a name.</summary>
        private JArray UnsavedDocuments()
        {
            var list = new JArray();
            foreach (var doc in _app.Documents)
            {
                try
                {
                    var model = ((Xarial.XCad.SolidWorks.Documents.ISwDocument)doc).Model;
                    if (model == null || !model.GetSaveFlag()) continue;
                    var path = model.GetPathName();
                    list.Add(new JObject { ["title"] = model.GetTitle(), ["path"] = path, ["untitled"] = string.IsNullOrEmpty(path) });
                }
                catch
                {
                    // A document closing mid-enumeration; ignore.
                }
            }
            return list;
        }

        /// <summary>
        /// Download (if needed), save open documents, start the install helper and close SolidWorks; the helper
        /// installs and starts SolidWorks again. Returns { ok } or { ok: false, reason, unsaved }.
        /// </summary>
        private async Task<JObject> RestartAndInstall(bool saveDocuments, JObject panel)
        {
            if (Updates.Status == Updater.State.Available && !await Updates.DownloadAsync())
                return new JObject { ["ok"] = false, ["reason"] = Updates.Error };
            if (Updates.Status != Updater.State.Ready)
                return new JObject { ["ok"] = false, ["reason"] = Updates.Error ?? "No update is ready to install." };

            var unsaved = UnsavedDocuments();
            if (unsaved.Any(d => (bool)d["untitled"]))
                return new JObject { ["ok"] = false, ["reason"] = "Some documents have never been saved. Save them (File > Save As) first.", ["unsaved"] = unsaved };
            if (unsaved.Count > 0 && !saveDocuments)
                return new JObject { ["ok"] = false, ["reason"] = "unsaved", ["unsaved"] = unsaved };

            foreach (var d in unsaved)
            {
                var model = (IModelDoc2)_app.Sw.GetOpenDocumentByName((string)d["path"]);
                int errors = 0, warnings = 0;
                if (model != null && !model.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errors, ref warnings))
                    return new JObject { ["ok"] = false, ["reason"] = $"Couldn't save {d["title"]} ({(swFileSaveError_e)errors}). Save it yourself, then try again." };
            }

            ResumeSession.Save(_app.Sw, panel); // the installer starts SolidWorks again; pick up where the user left off
            if (!Updates.LaunchInstaller(restartSolidWorks: true))
                return new JObject { ["ok"] = false, ["reason"] = Updates.Error };
            UpdateHelperStarted = true;

            // Close SolidWorks after this reply has reached the panel.
            var exit = new Timer { Interval = 500 };
            exit.Tick += (s, e) =>
            {
                exit.Stop();
                Log.Info("Closing SolidWorks to install the update");
                _app.Sw.ExitApp();
            };
            exit.Start();
            return new JObject { ["ok"] = true };
        }

        /// <summary>
        /// Save open documents and restart SolidWorks (new tools only reach the ribbon at start-up). Everything is
        /// reopened where the user left off (ResumeSession). Returns { ok } or { ok: false, reason, unsaved } like RestartAndInstall.
        /// </summary>
        private Task<JObject> SaveAndRestart(bool saveDocuments, JObject panel)
        {
            var unsaved = UnsavedDocuments();
            if (unsaved.Any(d => (bool)d["untitled"]))
                return Task.FromResult(new JObject { ["ok"] = false, ["reason"] = "Some documents have never been saved. Save them (File > Save As) first.", ["unsaved"] = unsaved });
            if (unsaved.Count > 0 && !saveDocuments)
                return Task.FromResult(new JObject { ["ok"] = false, ["reason"] = "unsaved", ["unsaved"] = unsaved });

            foreach (var d in unsaved)
            {
                var model = (IModelDoc2)_app.Sw.GetOpenDocumentByName((string)d["path"]);
                int errors = 0, warnings = 0;
                if (model != null && !model.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errors, ref warnings))
                    return Task.FromResult(new JObject { ["ok"] = false, ["reason"] = $"Couldn't save {d["title"]} ({(swFileSaveError_e)errors}). Save it yourself, then try again." });
            }

            string reopen = null;
            try
            {
                reopen = ((IModelDoc2)_app.Sw.ActiveDoc)?.GetPathName();
            }
            catch
            {
                // Nothing to reopen.
            }
            ResumeSession.Save(_app.Sw, panel);
            if (!RestartHelper.Launch(reopen)) return Task.FromResult(new JObject { ["ok"] = false, ["reason"] = "Couldn't start the restart helper." });

            var exit = new Timer { Interval = 500 };
            exit.Tick += (s, e) =>
            {
                exit.Stop();
                Log.Info("Restarting SolidWorks");
                _app.Sw.ExitApp();
            };
            exit.Start();
            return Task.FromResult(new JObject { ["ok"] = true });
        }

        /// <summary>At shutdown: with auto-update on, a downloaded update installs now (without restarting SolidWorks).</summary>
        public void InstallPendingUpdateOnExit()
        {
            if (UpdateHelperStarted || !AutoUpdate || Updates.Status != Updater.State.Ready) return;
            UpdateHelperStarted = Updates.LaunchInstaller(restartSolidWorks: false);
        }

        private JObject GetState()
        {
            var active = HandlerFor(_app.Documents.Active);
            var state = new JObject
            {
                ["version"] = Updates.CurrentVersion.ToString(3),
                ["update"] = Updates.ToJson(),
                ["crash"] = Crash,
                ["ribbonStale"] = RibbonStale?.Invoke() ?? false,
                ["solidworksVersion"] = SafeRevision(),
                ["settings"] = _settings,
                ["scratchKey"] = ChatDocument.ScratchKey,
                ["activeDoc"] = active == null ? null : DocInfo(active),
                ["docs"] = new JArray(_docs.Values.Where(IsVisible).Select(DocInfo)),
                ["logFile"] = Log.CurrentFile,
                ["swTheme"] = ReadTheme(),
                ["resume"] = ResumeSession.TakePanel(),
                // When this SolidWorks session started: restart offers made before it are done.
                ["sessionStart"] = Process.GetCurrentProcess().StartTime.ToUniversalTime().ToString("o"),
            };
            state.Merge(SettingsStore.KeyState()); // apiKeySource (Anthropic's, for older panels) and apiKeySources
            return state;
        }

        private string SafeRevision()
        {
            try
            {
                return _app.Sw.RevisionNumber();
            }
            catch
            {
                return null;
            }
        }

        private ChatDocument ResolveChat(string docKey)
        {
            if (string.IsNullOrEmpty(docKey) || docKey == ChatDocument.ScratchKey) return _scratch;
            if (_docs.TryGetValue(docKey, out var h)) return h.Chat;
            if (Reopened(docKey) is ChatDocumentHandler again) return again.Chat;
            throw new InvalidOperationException("That document is no longer open.");
        }

        private static HashSet<string> ToolUseIds(JToken p) =>
            new HashSet<string>((p["toolUseIds"] as JArray)?.Select(t => (string)t) ?? Enumerable.Empty<string>());

        /// <summary>The document tools act on: the chat's own document, or the active one for a scratch chat.</summary>
        private ISwDocument DocumentFor(string docKey)
        {
            // Workspace chats act on the active document.
            if (string.IsNullOrEmpty(docKey) || docKey == ChatDocument.ScratchKey || docKey == WorkspaceChats.Key) return _app.Documents.Active;
            if (_docs.TryGetValue(docKey, out var h)) return h.Chat.Doc;
            if (Reopened(docKey) is ChatDocumentHandler again) return again.Chat.Doc;
            throw new InvalidOperationException("The document for this chat has been closed.");
        }

        private ChatDocumentHandler HandlerFor(IXDocument doc)
        {
            if (doc == null) return null;
            try
            {
                return _app.Documents.GetHandler<ChatDocumentHandler>(doc);
            }
            catch
            {
                return _docs.Values.FirstOrDefault(h => h.Chat.Doc.Equals(doc));
            }
        }

        private static bool IsVisible(ChatDocumentHandler h)
        {
            try
            {
                return h.Chat.Doc.Model.Visible;
            }
            catch
            {
                return false;
            }
        }

        private static JObject DocInfo(ChatDocumentHandler h)
        {
            var info = new JObject { ["key"] = h.Chat.Key };
            try
            {
                var model = h.Chat.Doc.Model;
                info["title"] = model.GetTitle();
                info["path"] = model.GetPathName();
                info["type"] = (swDocumentTypes_e)model.GetType() == swDocumentTypes_e.swDocPART ? "part"
                    : (swDocumentTypes_e)model.GetType() == swDocumentTypes_e.swDocASSEMBLY ? "assembly"
                    : (swDocumentTypes_e)model.GetType() == swDocumentTypes_e.swDocDRAWING ? "drawing" : "other";
            }
            catch (Exception ex)
            {
                info["title"] = "(unavailable)";
                Log.Warn("DocInfo failed: " + ex.Message);
            }
            return info;
        }

        private void EmitSelection(ChatDocumentHandler handler)
        {
            _bridge?.Emit("selection.changed", SelectionPayload(handler));
            Safe.Run("Voice selection", () => _voice.OnSelectionChanged(handler.Chat.Doc.Model));
        }

        /// <summary>The chat key of an open document's model (voice messages go to that document's chat).</summary>
        private string ChatKeyOf(IModelDoc2 model)
        {
            foreach (var h in _docs.Values)
            {
                try
                {
                    var m = h.Chat.Doc.Model;
                    if (ReferenceEquals(m, model) || (m.GetTitle() == model.GetTitle() && m.GetPathName() == model.GetPathName())) return h.Chat.Key;
                }
                catch
                {
                    // A closing document.
                }
            }
            return null;
        }

        private static JObject SelectionPayload(ChatDocumentHandler h)
        {
            var items = new JArray();
            try
            {
                foreach (var (type, label) in ModelTools.SelectionChips(h.Chat.Doc.Model, 30))
                {
                    items.Add(new JObject { ["type"] = type, ["label"] = label });
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Selection summary failed: " + ex.Message);
            }
            // The chips stop at 30; the total tells an ask_selection card how many are really selected.
            return new JObject { ["docKey"] = h.Chat.Key, ["items"] = items, ["total"] = SelectedCount(h.Chat.Doc.Model) };
        }

        /// <summary>How many entities are selected in a document (0 when it can't be read).</summary>
        private static int SelectedCount(IModelDoc2 model)
        {
            try
            {
                return model?.SelectionManager is ISelectionMgr sm ? Math.Max(0, sm.GetSelectedObjectCount2(-1)) : 0;
            }
            catch
            {
                return 0; // a closing document
            }
        }

        /// <summary>Raised when the SolidWorks interface theme or panel colour changes.</summary>
        public event Action<JObject> ThemeChanged;

        /// <summary>The panel asked to be shown (AgentAddIn shows and pins the task pane).</summary>
        public event Action ShowPaneRequested;

        /// <summary>
        /// The SolidWorks interface theme (swInterfaceBrightnessTheme_e: 0 Light, 1 Medium, 2 Dark, 3 Medium Light)
        /// and the FeatureManager / PropertyManager background colours as #RRGGBB.
        /// </summary>
        public JObject ReadTheme()
        {
            try
            {
                var sw = _app.Sw;
                return new JObject
                {
                    ["theme"] = sw.GetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swSystemColorsBackground),
                    ["treeBg"] = ToHex(sw.GetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swSystemColorsTreeViewBackground)),
                    ["pmBg"] = ToHex(sw.GetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swPropertyManagerColorBackground)),
                };
            }
            catch (Exception ex)
            {
                Log.Warn("Reading the SolidWorks theme failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>COLORREF (0x00BBGGRR) to #RRGGBB.</summary>
        internal static string ToHex(int colorRef) => $"#{colorRef & 0xFF:X2}{(colorRef >> 8) & 0xFF:X2}{(colorRef >> 16) & 0xFF:X2}";

        private void OnUserToolsChanged()
        {
            _bridge?.Emit("userTools.changed", new JArray(UserTools.ListTools()));
            _bridge?.Emit("ribbon.stale", new JObject { ["stale"] = RibbonStale?.Invoke() ?? false });
        }

        private bool _overlaySubscribed;

        /// <summary>Chat keys of closed documents -> the document id stored in the file (see Reopened).</summary>
        private readonly Dictionary<string, string> _closedDocIds = new Dictionary<string, string>();

        /// <summary>
        /// A chat whose document was closed and opened again (e.g. a Smart Rebuild checkpoint restore): the reopened file
        /// has a new chat key but the same document id, so a running conversation keeps working on it.
        /// </summary>
        private ChatDocumentHandler Reopened(string oldKey)
        {
            if (string.IsNullOrEmpty(oldKey) || !_closedDocIds.TryGetValue(oldKey, out var docId) || string.IsNullOrEmpty(docId)) return null;
            return _docs.Values.FirstOrDefault(h => h.Chat?.DocId == docId);
        }

        public void Dispose()
        {
            Safe.Run("Clear the overlay", UI.SceneOverlay.ClearAll);
            Safe.Run("Close the tool form", () => _forms.CancelPending("SolidWorks is closing."));
            StopWatch();
            Safe.Run("Stop voice", _voice.Dispose);
            Safe.Run("Save chats", _workspace.Flush);
            Safe.Run("Stop the local link", () => _link?.Dispose());
            _themeTimer.Dispose();
            _activeTimer.Dispose();
            _updateTimer.Dispose();
            _selectionTimer.Dispose();
            Safe.Run("End the plugin session", Runtime.Dispose); // SessionEnd, then every plugin process is stopped
        }
    }
}
