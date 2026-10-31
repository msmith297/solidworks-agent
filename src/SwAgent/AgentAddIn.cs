using System;
using System.Runtime.InteropServices;
using SwAgent.Chat;
using SwAgent.Infrastructure;
using SwAgent.Scripting;
using SwAgent.Tools;
using SwAgent.UI;
using Xarial.XCad.Base.Attributes;
using Xarial.XCad.SolidWorks;
using Xarial.XCad.SolidWorks.UI;
using Xarial.XCad.UI;
using Xarial.XCad.UI.Commands;
using Xarial.XCad.UI.TaskPane;

namespace SwAgent
{
    /// <summary>
    /// The SolidWorks add-in. Loaded in-process by SLDWORKS.exe through COM (see scripts/register.ps1).
    /// </summary>
    [ComVisible(true)]
    [Guid("6D2B1C1E-4A0F-4F1E-9C7B-3E5A8D2F7B41")]
    [Title("SolidWorks Agent")]
    [Summary("SolidWorks Agent - AI design agent for SOLIDWORKS: chat about your model and let it build and edit it.")]
    [Icon(typeof(Icons), nameof(Icons.Agent))]
    public class AgentAddIn : SwAddInEx
    {
        static AgentAddIn()
        {
            // Must run before any Roslyn / System.Memory type is touched.
            AssemblyResolver.Install();
        }

        internal static AgentAddIn Instance { get; private set; }

        internal AgentController Controller { get; private set; }

        private ISwTaskPane<ChatPaneControl> _pane;
        private AgentRibbon _ribbon;

        /// <summary>
        /// Every agent tool the add-in offers, registered once at connect (the tests build the same registry: a built-in
        /// tool's `requires` must name tools registered here).
        /// </summary>
        internal static ToolRegistry BuildTools(out ScriptHost scripts)
        {
            var tools = new ToolRegistry();
            scripts = new ScriptHost(tools);
            tools.RegisterAll(new ModelTools());
            tools.RegisterAll(new ViewTools());
            tools.RegisterAll(new ApiTools());
            tools.RegisterAll(new SelectionTools());
            tools.RegisterAll(new HistoryTools());
            tools.RegisterAll(new UserTools());
            tools.RegisterAll(new LibraryTools());
            tools.RegisterAll(scripts);
            tools.RegisterAll(new ToolTester(scripts));
            tools.RegisterAll(new OverlayTools());
            tools.RegisterAll(new DesignSnapshot());
            tools.RegisterAll(new RebuildTools());
            tools.RegisterAll(new MoldTools());
            tools.RegisterAll(new Plugins.PluginTools());
            tools.RegisterAll(new Plugins.CreatorTools()); // plugin creator: check_prerequisites, plugin_try_mcp
            tools.RegisterAll(new KnowledgeTools(scripts));
            tools.RegisterAll(new Memory.MemoryTools());
            tools.RegisterAll(new ForkTools());
            tools.RegisterAll(new ProfileTools());
            CompileAdvisor.Extra = UserHelpers.LastBuild; // compile errors can point at the helpers the agent saved
            return tools;
        }

        public override void OnConnect()
        {
            Instance = this;
            Log.Info($"SolidWorks Agent {GetType().Assembly.GetName().Version} connecting to SolidWorks {Application.Sw.RevisionNumber()}");
            Log.Prune();
            ChatDocument.PruneBackups();
            SoundMuter.RestoreNow(); // if SolidWorks crashed while the agent was working
            var crash = CrashGuard.Recover(); // ...or while a tool's preview / macro or an agent cell was running
            Safe.InstallLastChance();

            try
            {
                var tools = BuildTools(out var scripts);
                Controller = new AgentController(Application, tools, scripts);
                if (crash != null)
                {
                    // Pause the part of the tool that crashed SolidWorks so clicking it again can't repeat it; the
                    // panel offers to have the agent find and fix the cause.
                    var tool = (string)crash["tool"];
                    var kind = (string)crash["kind"];
                    if (tool != null && (kind == "preview" || kind == "macro" || kind == "page") && UserTools.IsBuiltIn(tool))
                    {
                        // a built-in tool is read-only: a pause written to builtin-settings.json could never be cleared (edit_user_tool
                        // refuses built-ins), so it isn't persisted - the crash card still reports it
                        crash["builtIn"] = true;
                        Log.Warn($"Built-in tool '{tool}' was running ({kind}) when SolidWorks closed; not paused (built-in tools are read-only).");
                    }
                    else if (tool != null && (kind == "preview" || kind == "macro" || kind == "page"))
                    {
                        try
                        {
                            // A crash while the tool's page was open: its live preview is the likely cause.
                            var part = kind == "page" ? "preview" : kind;
                            UserTools.SetValue(tool, "paused", part);
                            crash["paused"] = part;
                        }
                        catch (Exception ex)
                        {
                            Log.Warn("Pausing the crashed tool failed: " + ex.Message);
                        }
                    }
                    Controller.Crash = crash;
                }

                ResumeSession.RestoreWhenReady(Application.Sw); // after "Save and restart": reopen where the user left off
                Application.Documents.RegisterHandler(() => new ChatDocumentHandler());
                Application.Documents.DocumentActivated += doc => Controller.OnDocumentActivated(doc);

                // Right-click menus come from xCAD; the ribbon tab, menu and toolbar are AgentRibbon.
                CommandManager.AddContextMenu<FaceContextCommands_e>().CommandClick += _ => AskAboutSelection();
                CommandManager.AddContextMenu<EdgeContextCommands_e>().CommandClick += _ => AskAboutSelection();
                CommandManager.AddContextMenu<FeatureContextCommands_e>().CommandClick += _ => AskAboutSelection();

                // One "SolidWorks Agent" tab: panel commands and the user's tools. Built at start-up only (see AgentRibbon).
                _ribbon = new AgentRibbon(((Xarial.XCad.SolidWorks.UI.Commands.ISwCommandManager)CommandManager).CmdMgr);
                _ribbon.Rebuild();
                Controller.RibbonStale = _ribbon.IsStale;
                Controller.ShowPaneRequested += () => Safe.Run("Show the pane", ShowPane);

                _pane = this.CreateTaskPaneWinForm<ChatPaneControl>(new TaskPaneSpec
                {
                    Title = "SolidWorks Agent",
                    Tooltip = "SolidWorks Agent",
                    Icon = new BaseImage(Icons.Agent),
                });
                if (_pane.IsControlCreated) InitPane(_pane.Control);
                else _pane.ControlCreated += InitPane;

                Log.Info("SolidWorks Agent connected");
            }
            catch (Exception ex)
            {
                Log.Error("SolidWorks Agent failed to start", ex);
                throw;
            }
        }

        private void InitPane(ChatPaneControl control)
        {
            var theme = Controller.ReadTheme();
            if (theme?["pmBg"] != null) control.SetBackground((string)theme["pmBg"]);
            Controller.ThemeChanged += t =>
            {
                if (t?["pmBg"] != null) control.SetBackground((string)t["pmBg"]);
            };
            control.BridgeReady += () => Controller.AttachBridge(control.Bridge);
            control.Initialize();
        }

        /// <summary>Callback for every SolidWorks Agent ribbon/menu/toolbar command (SolidWorks calls it by name; see AgentRibbon).</summary>
        public void OnAgentCommand(string arg)
        {
            try
            {
                var tool = _ribbon?.ToolFor(arg);
                if (tool != null)
                {
                    // Tools run in place, like a native command: no panel, unless the agent has to step in.
                    _ = Controller.RunToolAsync(tool, ShowPane);
                    return;
                }
                ShowPane();
                switch (arg)
                {
                    case "chat":
                        Controller.SendCommand("focus");
                        break;
                    case "newchat":
                        Controller.SendCommand("newChat");
                        break;
                    case "ask":
                        AskAboutSelection();
                        break;
                    case "view":
                        Controller.SendCommand("attachView");
                        break;
                    case "newtool":
                        Controller.SendCommand("newTool");
                        break;
                    case "options":
                        Controller.SendCommand("openSettings");
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"SolidWorks Agent command '{arg}' failed", ex);
            }
        }

        /// <summary>Enable state: tools and document commands need an open document.</summary>
        public int OnAgentEnable(string arg)
        {
            try
            {
                var needsDoc = arg.StartsWith("t") || arg == "ask" || arg == "view";
                return !needsDoc || Application.Sw.ActiveDoc != null ? 1 : 0;
            }
            catch
            {
                return 1;
            }
        }

        private void AskAboutSelection()
        {
            ShowPane();
            Controller.SendCommand("askAboutSelection");
        }

        private void ShowPane()
        {
            try
            {
                // An unpinned Task Pane collapses on the next click in the graphics area. Opening the Agent
                // from the ribbon is an explicit request to keep it visible.
                if (!Application.Sw.TaskPaneIsPinned) Application.Sw.TaskPaneIsPinned = true;
                _pane?.TaskPaneView?.ShowView();
            }
            catch (Exception ex)
            {
                Log.Warn("Could not show the task pane: " + ex.Message);
            }
        }

        public override void OnDisconnect()
        {
            Log.Info("SolidWorks Agent disconnecting");
            SoundMuter.RestoreNow();
            Controller?.InstallPendingUpdateOnExit();
            _ribbon?.Dispose();
            Controller?.Dispose();
            Instance = null;
        }
    }
}
