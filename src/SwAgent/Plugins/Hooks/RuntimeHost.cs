using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwAgent.Infrastructure;
using SwAgent.Mcp;
using SwAgent.Tools;
using SwAgent.UI;

namespace SwAgent.Plugins.Hooks
{
    /// <summary>
    /// The plugin runtime: hooks and MCP servers of the runnable plugins (IPluginRuntimeSource) and of the user's mcp.json,
    /// the session they share, and the panel's RPCs for them:
    ///   hooks.fire(event, ...)  the panel-side events (UserPromptSubmit, PreToolUse, PostToolUse, Stop)
    ///   session.info({ panel? }) { sessionId, hookEvents, mcpInstructions, sessionContext, sessionNotices, heldWakes };
    ///                           panel is the page's own id: a new one means the panel was reloaded (PanelReloaded)
    ///   session.takeWakes()     wakes not yet confirmed;  session.ackWake(id) confirms one
    ///   mcp.status(), mcp.restart(plugin, server)
    /// and events tools.changed, session.wake (a nudge: the wake itself is fetched with takeWakes) and session.changed.
    /// SessionStart fires once, after the panel's first session.info and once the MCP servers answered initialize (or 45 s);
    /// SessionEnd at shutdown. Nothing here touches SolidWorks; MCP tools reach the agent through tools.run.
    /// </summary>
    internal sealed class RuntimeHost : IDisposable
    {
        private readonly Action<string, object> _emit;
        private readonly object _sync = new object();
        private readonly SemaphoreSlim _syncGate = new SemaphoreSlim(1, 1);
        private IPluginRuntimeSource _source;
        private IReadOnlyList<PluginRuntime> _plugins = new PluginRuntime[0];
        private bool _hooksOn = true;
        private bool _mcpOn = true;
        private bool _started;
        private bool _sessionStartQueued;
        private bool _shutDown;
        private JArray _sessionContext = new JArray();

        /// <summary>The id the panel page sends with session.info: another one means a reloaded page.</summary>
        private string _panel;

        /// <summary>SessionStart systemMessage and exit-2 text: for the user (the panel's notice bar), never the model.</summary>
        private JArray _sessionNotices = new JArray();

        /// <summary>Set when the first sync after Start has put the MCP servers in place (SessionStart waits for them).</summary>
        private readonly TaskCompletionSource<bool> _firstSync = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private FileSystemWatcher _mcpJsonWatcher;
        private Timer _mcpJsonDebounce;

        public RuntimeHost(IPluginRuntimeSource source, Session session, Action<string, object> emit)
        {
            Session = session;
            _emit = emit ?? ((n, d) => { });
            Mcp = new McpHost(session);
            Hooks = new HookRunner(session);
            Mcp.Changed += () =>
            {
                _emit("tools.changed", null);
                _emit("session.changed", null);
            };
            Hooks.Rewake += OnRewake;
            UseSource(source);
        }

        public Session Session { get; }

        public McpHost Mcp { get; }

        public HookRunner Hooks { get; }

        /// <summary>No plugins (until the plugin core provides its source).</summary>
        public static IPluginRuntimeSource NoPlugins { get; } = new EmptySource();

        /// <summary>User-level MCP servers: %APPDATA%\SwAgent\mcp.json ({ "mcpServers": { ... } }, as in Claude Code).</summary>
        internal string McpJsonPath { get; set; } = Paths.McpConfigFile;

        /// <summary>Switches to another plugin source (the plugin core's, once it is loaded).</summary>
        public void UseSource(IPluginRuntimeSource source)
        {
            lock (_sync)
            {
                if (_source != null) _source.Changed -= OnSourceChanged;
                _source = source ?? NoPlugins;
                _source.Changed += OnSourceChanged;
            }
            OnSourceChanged();
        }

        private void OnSourceChanged() => _ = Task.Run(SyncAsync);

        /// <summary>
        /// Settings (settings.json, also before the panel has loaded): pluginHooks (default on), pluginMcp (on),
        /// pluginWakesPerHour (20).
        /// </summary>
        public void ApplySettings(JObject settings)
        {
            lock (_sync)
            {
                _hooksOn = (bool?)settings?["pluginHooks"] ?? true;
                _mcpOn = (bool?)settings?["pluginMcp"] ?? true;
            }
            var perHour = settings?["pluginWakesPerHour"];
            Session.AutomaticPerHour = perHour != null && (perHour.Type == JTokenType.Integer || perHour.Type == JTokenType.Float) ? Math.Max(0, (int)perHour) : 20;
            _ = Task.Run(SyncAsync);
        }

        /// <summary>Registers the RPCs (again on every panel reload); starts the servers only the first time.</summary>
        public void Attach(WebBridge bridge)
        {
            bridge.RegisterAsync("hooks.fire", async p => await FireAsync(p as JObject ?? new JObject()));
            bridge.Register("session.info", p => Info(p?["panel"]?.Type == JTokenType.String ? (string)p["panel"] : null));
            bridge.Register("session.takeWakes", p => Session.PendingWakes());
            bridge.Register("session.ackWake", p => new JObject { ["ok"] = Session.AckWake((string)p?["id"]) });
            bridge.Register("mcp.status", p => Mcp.Status());
            bridge.Register("mcp.restart", p => new JObject { ["ok"] = Mcp.Restart((string)p?["plugin"], (string)p?["server"]) });
            Start();
        }

        /// <summary>Starts the MCP servers and watches mcp.json (once).</summary>
        public void Start()
        {
            lock (_sync)
            {
                if (_started || _shutDown) return;
                _started = true;
            }
            try
            {
                _mcpJsonDebounce = new Timer(_ => _ = SyncAsync(), null, Timeout.Infinite, Timeout.Infinite);
                _mcpJsonWatcher = new FileSystemWatcher(Path.GetDirectoryName(McpJsonPath), Path.GetFileName(McpJsonPath)) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
                FileSystemEventHandler changed = (s, e) => _mcpJsonDebounce?.Change(500, Timeout.Infinite);
                _mcpJsonWatcher.Changed += changed;
                _mcpJsonWatcher.Created += changed;
                _mcpJsonWatcher.Deleted += changed;
                _mcpJsonWatcher.Renamed += (s, e) => _mcpJsonDebounce?.Change(500, Timeout.Infinite);
                _mcpJsonWatcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                Log.Warn("Watching mcp.json failed: " + ex.Message);
            }
            _ = Task.Run(SyncAsync);
        }

        /// <summary>Re-reads the plugins and mcp.json and makes the running servers match; kills hooks of plugins that left.</summary>
        public async Task SyncAsync()
        {
            await _syncGate.WaitAsync().ConfigureAwait(false);
            try
            {
                IPluginRuntimeSource source;
                bool started, mcpOn, hooksOn;
                lock (_sync)
                {
                    if (_shutDown) return;
                    source = _source;
                    started = _started;
                    mcpOn = _mcpOn;
                    hooksOn = _hooksOn;
                }
                IReadOnlyList<PluginRuntime> plugins;
                try
                {
                    plugins = source.Runnable() ?? new PluginRuntime[0];
                }
                catch (Exception ex)
                {
                    Log.Warn("Reading the runnable plugins failed: " + ex.Message);
                    plugins = new PluginRuntime[0];
                }
                IReadOnlyList<PluginRuntime> before;
                lock (_sync)
                {
                    before = _plugins;
                    _plugins = plugins;
                }
                // Hooks still running (listeners) for a plugin that may no longer run, or with hooks switched off, stop now.
                var keep = new HashSet<string>(plugins.Select(p => p.Id + "|" + p.TrustHash + "|" + p.Root));
                Hooks.KillWhere(p => !hooksOn || !keep.Contains(p.Id + "|" + p.TrustHash + "|" + p.Root));
                if (started)
                {
                    Mcp.Sync(mcpOn ? plugins : new PluginRuntime[0], mcpOn ? ReadUserServers() : null);
                    _firstSync.TrySetResult(true);
                }
                if (!ReferenceEquals(before, plugins)) _emit("session.changed", null);
            }
            finally
            {
                _syncGate.Release();
            }
        }

        /// <summary>mcp.json's servers, or null when there is none (a broken file is logged and ignored).</summary>
        internal JObject ReadUserServers()
        {
            try
            {
                if (!File.Exists(McpJsonPath)) return null;
                return JObject.Parse(File.ReadAllText(McpJsonPath))["mcpServers"] as JObject;
            }
            catch (Exception ex)
            {
                Log.Warn("mcp.json couldn't be read: " + ex.Message);
                return null;
            }
        }

        private IReadOnlyList<PluginRuntime> HookPlugins
        {
            get
            {
                lock (_sync) return _hooksOn && !_shutDown ? _plugins : new PluginRuntime[0];
            }
        }

        // ---- session ----

        /// <summary>
        /// session.info. The first call (the panel is ready) queues SessionStart: it waits for the MCP servers' initialize
        /// (agent-bridge's server registers this session with its hub there), at most 45 s.
        /// </summary>
        public JObject Info(string panel = null)
        {
            var queue = false;
            var reloaded = false;
            lock (_sync)
            {
                if (!_sessionStartQueued && !_shutDown)
                {
                    _sessionStartQueued = true;
                    queue = true;
                }
                if (panel != null && panel != _panel)
                {
                    reloaded = _panel != null;
                    _panel = panel;
                }
            }
            if (queue) _ = Task.Run(FireSessionStartAsync);
            if (reloaded)
            {
                Log.Info("The panel was reloaded");
                PanelReloaded?.Invoke();
            }
            JArray context, notices;
            lock (_sync)
            {
                context = new JArray(_sessionContext);
                notices = new JArray(_sessionNotices);
            }
            return new JObject
            {
                ["sessionId"] = Session.Id,
                ["hookEvents"] = HookRunner.Matchers(HookPlugins),
                ["mcpInstructions"] = Mcp.Instructions(),
                ["sessionContext"] = context,
                ["sessionNotices"] = notices,
                ["heldWakes"] = Session.HeldCount,
            };
        }

        private async Task FireSessionStartAsync()
        {
            try
            {
                var waited = System.Diagnostics.Stopwatch.StartNew();
                await Task.WhenAny(_firstSync.Task, Task.Delay(TimeSpan.FromSeconds(45))).ConfigureAwait(false);
                await Mcp.WhenStarted(TimeSpan.FromSeconds(Math.Max(1, 45 - waited.Elapsed.TotalSeconds))).ConfigureAwait(false);
                var specs = Hooks.Select(HookPlugins, HookEvents.SessionStart, "startup");
                if (specs.Count == 0) return;
                Session.WriteTranscript(null);
                var outcome = await Hooks.RunAsync(specs, HookEvents.SessionStart, new JObject { ["source"] = "startup" }).ConfigureAwait(false);
                lock (_sync)
                {
                    foreach (var c in outcome.AdditionalContext) _sessionContext.Add(c);
                    foreach (var m in outcome.SystemMessages) _sessionNotices.Add(m);
                }
                if (outcome.AdditionalContext.Count + outcome.SystemMessages.Count > 0) _emit("session.changed", null);
            }
            catch (Exception ex)
            {
                Log.Warn("SessionStart hooks failed: " + ex.Message);
            }
        }

        // ---- hooks.fire ----

        /// <summary>The largest tool_response (serialized) passed to PostToolUse hooks.</summary>
        internal const int MaxToolResponseChars = 100 * 1024;

        /// <summary>
        /// hooks.fire({ event, threadId, docKey, prompt?, tool_name?, tool_input?, tool_response?, stop_hook_active?,
        /// unify3d_document? }) -> { block, reason, additionalContext[], systemMessages[], continue, stopReason,
        /// permissionDecision?, permissionDecisionReason? }.
        /// </summary>
        public async Task<JObject> FireAsync(JObject p)
        {
            var ev = (string)p["event"];
            if (!HookEvents.PanelFired.Contains(ev)) throw new InvalidOperationException($"hooks.fire: '{ev}' isn't an event the panel fires.");
            var input = new JObject();
            string matchValue = null;
            switch (ev)
            {
                case HookEvents.UserPromptSubmit:
                    input["prompt"] = (string)p["prompt"] ?? "";
                    break;
                case HookEvents.PreToolUse:
                case HookEvents.PostToolUse:
                    matchValue = (string)p["tool_name"] ?? "";
                    input["tool_name"] = matchValue;
                    input["tool_input"] = p["tool_input"]?.DeepClone() ?? new JObject();
                    if (ev == HookEvents.PostToolUse) input["tool_response"] = CapResponse(p["tool_response"]);
                    break;
                case HookEvents.Stop:
                    input["stop_hook_active"] = (bool?)p["stop_hook_active"] == true;
                    break;
            }
            if (p["unify3d_document"] != null) input["unify3d_document"] = p["unify3d_document"].DeepClone();

            var specs = Hooks.Select(HookPlugins, ev, matchValue);
            var outcome = new HookOutcome();
            if (specs.Count > 0)
            {
                Session.WriteTranscript((string)p["threadId"]);
                outcome = await Hooks.RunAsync(specs, ev, input).ConfigureAwait(false);
            }

            if (ev == HookEvents.Stop && outcome.Block && !Session.TryCountAutomatic(DateTime.UtcNow))
            {
                // A Stop hook continuing the turn is an automatic turn too: past the hour's allowance the turn ends.
                outcome.Block = false;
                outcome.Reasons.Clear();
                outcome.SystemMessages.Add($"Plugins wanted to continue the turn automatically, but the limit of {Session.AutomaticPerHour} automatic turns per hour is reached. Send a message to continue.");
            }
            if (ev == HookEvents.UserPromptSubmit && !outcome.Block)
            {
                // Wakes held back by the guard: their mail is already handed to this session, so they go with this prompt.
                var held = Session.TakeHeld();
                for (var i = held.Count - 1; i >= 0; i--) outcome.AdditionalContext.Insert(0, held[i].Text);
                if (held.Count > 0) outcome.SystemMessages.Add($"{held.Count} plugin message(s) that arrived while automatic turns were paused are included with this message.");
            }
            return outcome.ToJson();
        }

        /// <summary>tool_response for PostToolUse: text blocks only (images become "[image]"), about 100 KB at most.</summary>
        internal static JObject CapResponse(JToken response)
        {
            var content = new JArray();
            var left = MaxToolResponseChars;
            foreach (var block in (response?["content"] as JArray ?? new JArray()).OfType<JObject>())
            {
                var text = (string)block["type"] == "text" ? (string)block["text"] ?? "" : "[" + ((string)block["type"] ?? "content") + "]";
                if (left <= 0) break;
                if (text.Length > left) text = text.Substring(0, left) + "... (cut)";
                left -= text.Length;
                content.Add(new JObject { ["type"] = "text", ["text"] = text });
            }
            return new JObject { ["content"] = content, ["isError"] = (bool?)response?["isError"] == true };
        }

        /// <summary>A reloaded panel page called session.info: whatever the old page was running is gone.</summary>
        public event Action PanelReloaded;

        // ---- wakes ----

        /// <summary>
        /// A message from Claude Desktop or Codex over the local link (LinkHost, already counted against the hour's
        /// allowance): queued for the panel, which delivers it into the open chat like any wake. False once SolidWorks is
        /// closing.
        /// </summary>
        public bool QueueLinkWake(Session.Wake wake)
        {
            lock (_sync)
            {
                if (_shutDown) return false;
            }
            Session.AddLinkWake(wake);
            Log.Info($"Wake from {wake.From} (ticket {wake.Ticket}) queued for the panel");
            _emit("session.wake", wake.ToJson());
            return true;
        }

        private void OnRewake(PluginRuntime plugin, string ev, string text)
        {
            lock (_sync)
            {
                if (_shutDown) return;
            }
            var wake = new Session.Wake { Id = Guid.NewGuid().ToString("N"), Text = text, Source = ev, Plugin = plugin?.Name };
            if (Session.AddWake(wake, DateTime.UtcNow))
            {
                Log.Info($"Wake from {wake.Plugin} ({ev}) queued for the panel");
                _emit("session.wake", wake.ToJson());
            }
            else
            {
                Log.Info($"Wake from {wake.Plugin} ({ev}) held: {Session.AutomaticPerHour} automatic turns per hour reached");
                _emit("session.changed", null);
            }
        }

        // ---- tools ----

        public static bool IsMcp(string name) => McpHost.IsMcp(name);

        /// <summary>tools.list: the add-in's tools plus the MCP servers' tools.</summary>
        public JArray MergeTools(JArray builtIn)
        {
            var all = new JArray(builtIn);
            foreach (var t in Mcp.Definitions()) all.Add(t);
            return all;
        }

        /// <summary>tools.run for an mcp__ tool: no busy gate (it never touches the model), cancellable per thread.</summary>
        public async Task<JObject> CallAsync(string name, JObject input, string threadId, string toolUseId)
        {
            var result = await Mcp.CallAsync(name, input, threadId).ConfigureAwait(false);
            return result.ToJson();
        }

        public void Cancel(string threadId) => Mcp.Cancel(threadId);

        public void OnChatSaved(JObject data) => Session.OnChatSaved(data);

        // ---- shutdown ----

        /// <summary>
        /// SessionEnd, best effort within about 3 s, without needing the UI thread: SessionEnd hooks run while the MCP
        /// servers' stdin closes (agent-bridge unregisters from its hub), then every plugin process is killed.
        /// </summary>
        public void Shutdown(string reason = "other")
        {
            IReadOnlyList<PluginRuntime> plugins;
            lock (_sync)
            {
                if (_shutDown) return;
                plugins = _hooksOn ? _plugins : new PluginRuntime[0];
                _shutDown = true; // no more syncs, wakes or SessionStart
            }
            try
            {
                var end = Task.Run(async () =>
                {
                    var specs = Hooks.Select(plugins, HookEvents.SessionEnd, null);
                    var hooks = specs.Count == 0 ? Task.CompletedTask : Hooks.RunAsync(specs.Select(s => Shorter(s, 3)).ToList(), HookEvents.SessionEnd, new JObject { ["reason"] = reason });
                    await Task.WhenAll(hooks, Mcp.StopAllAsync(TimeSpan.FromSeconds(2.5))).ConfigureAwait(false);
                });
                end.Wait(TimeSpan.FromSeconds(3.5));
            }
            catch (Exception ex)
            {
                Log.Warn("SessionEnd failed: " + ex.Message);
            }
            Hooks.KillAll();
            if (TerminateJobOnShutdown) Jobs.TerminateAll();
        }

        /// <summary>Also kill whatever plugin processes are left in the add-in's job (off in tests, which share that job).</summary>
        internal bool TerminateJobOnShutdown { get; set; } = true;

        private static HookRunner.Spec Shorter(HookRunner.Spec s, int seconds)
        {
            s.TimeoutSeconds = Math.Min(s.TimeoutSeconds, seconds);
            return s;
        }

        public void Dispose()
        {
            Shutdown();
            lock (_sync)
            {
                if (_source != null) _source.Changed -= OnSourceChanged;
            }
            try
            {
                _mcpJsonWatcher?.Dispose();
                _mcpJsonDebounce?.Dispose();
            }
            catch
            {
                // Best effort.
            }
            Mcp.Dispose();
            Session.Dispose();
        }

        private sealed class EmptySource : IPluginRuntimeSource
        {
            public IReadOnlyList<PluginRuntime> Runnable() => new PluginRuntime[0];

            public event Action Changed
            {
                add { }
                remove { }
            }
        }
    }
}
