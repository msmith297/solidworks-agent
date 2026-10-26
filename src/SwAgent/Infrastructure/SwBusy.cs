using System;
using System.Collections.Generic;
using SolidWorks.Interop.sldworks;

namespace SwAgent.Infrastructure
{
    /// <summary>
    /// The agent's tool calls wait while the USER is in a command (review finding: a long unattended mold run fired
    /// geometry calls and feature edits whenever the model answered, the pattern that crashed SolidWorks before). Tools that
    /// never touch the model run at once.
    ///
    /// Only a command the user started blocks: AgentController records SolidWorks' command state when each agent tool call
    /// ends (AgentLeft), and the next call waits only while the state differs from that. On 2026-10-20 the gate blocked a
    /// mold run for good after the agent's own failed cut left a sketch behind ("SolidWorks is in a command", with nothing
    /// on the user's screen): state the agent itself left is the agent's to use or clean up, never a reason to stop it.
    /// </summary>
    internal static class SwBusyGate
    {
        public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        /// <summary>Tools that never read or change the SolidWorks model (pure calculation, files, the API docs).</summary>
        public static readonly HashSet<string> ModelFreeTools = new HashSet<string>(StringComparer.Ordinal)
        {
            "search_sw_api", "get_sw_api_type", "mold_calc", "rebuild_journal", "list_user_tools", "read_user_tool", "save_user_tool",
            "edit_user_tool", "clear_overlay", "design_library", "list_open_documents", "get_selection",
            // plugins: skills, plugin files and the plugin creator
            "use_skill", "read_plugin_file", "list_plugins", "read_plugin", "read_bridge_file",
            // the agent's own knowledge: API notes and helper files (test_helper runs a cell, so it isn't here)
            "save_api_note", "list_api_notes", "save_helper", "read_helper", "list_helpers", "delete_helper",
            // the plugin creator's checks (plugin_try_mcp is a plugin_ tool)
            "check_prerequisites",
            // standing instructions and the user's copies of read-only tools (docs/agent-memory.md)
            "remember", "list_memory", "forget", "fork_tool", "compare_fork",
            // tool profiles (run_tool opens a tool's inputs, so it isn't here)
            "list_tool_profiles", "save_tool_profile",
        };

        /// <summary>
        /// MCP tools (mcp__...) run in their server's process and never touch the model; the plugin creator's tools
        /// (plugin_create, plugin_write_file, ...) only write plugin files.
        /// </summary>
        public static bool ModelFree(string tool) => tool != null &&
            (ModelFreeTools.Contains(tool) || tool.StartsWith("mcp__", StringComparison.Ordinal) || tool.StartsWith("plugin_", StringComparison.Ordinal));

        /// <summary>SolidWorks' command state when the agent's last model tool call ended (null before the first one).</summary>
        public static string AgentLeft { get; set; }

        /// <summary>Must the agent wait? Only for a blocking state that the agent's own last call didn't leave behind.</summary>
        public static bool MustWait(SwCommandState now, string agentLeft) => now.Blocking && now.Key != agentLeft;

        /// <summary>Polls `busy` every 250 ms (yielding the UI thread); false when it is still busy after `timeout`.</summary>
        public static async System.Threading.Tasks.Task<bool> WaitAsync(Func<bool> busy, TimeSpan timeout, int pollMs = 250)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (busy())
            {
                if (watch.Elapsed >= timeout) return false;
                await System.Threading.Tasks.Task.Delay(pollMs);
            }
            return true;
        }
    }

    /// <summary>What SolidWorks says it is doing: the running command (GetRunningCommandInfo) and the sketch being edited.</summary>
    internal sealed class SwCommandState
    {
        public int Id;
        public string Title;
        public bool UiActive;
        /// <summary>Name of the sketch open for editing in the active document, or null.</summary>
        public string Sketch;
        /// <summary>Reading the state failed (logged); treated as not blocking rather than blocking forever.</summary>
        public string Error;

        /// <summary>
        /// Something the user can see and must finish: a PropertyManager page or dialog (UI active or a title), or a sketch
        /// being edited. A bare command id with nothing open is not enough: SolidWorks can keep reporting one after a
        /// command (or a failed API feature call) is over.
        /// </summary>
        public bool Blocking => Error == null && (UiActive || !string.IsNullOrEmpty(Title) || Sketch != null);

        /// <summary>Any sign of a command, the bare id included (for timers that must stay well clear of commands).</summary>
        public bool AnySignal => Error != null || Id > 0 || Blocking;

        /// <summary>Identity of the state, to tell the agent's own leftovers from something the user started.</summary>
        public string Key => $"{Id}|{Title}|{UiActive}|{Sketch}";

        public string Describe()
        {
            if (Error != null) return "SolidWorks' command state couldn't be read (" + Error + ")";
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(Title)) parts.Add($"'{Title}' is open");
            else if (UiActive) parts.Add("a PropertyManager page or dialog is open");
            if (Sketch != null) parts.Add($"sketch '{Sketch}' is open for editing");
            if (Id > 0) parts.Add($"command {Id}");
            return parts.Count == 0 ? "no command" : string.Join(", ", parts);
        }
    }

    internal static class SwBusy
    {
        public static SwCommandState State(ISldWorks sw)
        {
            var s = new SwCommandState();
            try
            {
                sw.GetRunningCommandInfo(out var id, out var title, out var uiActive);
                s.Id = id;
                s.Title = title;
                s.UiActive = uiActive;
            }
            catch (Exception ex)
            {
                s.Error = ex.Message;
                return s;
            }
            try
            {
                if (sw.ActiveDoc is IModelDoc2 model && model.SketchManager?.ActiveSketch is ISketch sketch)
                    s.Sketch = (sketch as IFeature)?.Name ?? "unnamed";
            }
            catch (Exception ex)
            {
                Log.Warn("Reading the active sketch failed: " + ex.Message);
            }
            return s;
        }

        /// <summary>For timers and polls that read the model: any sign of a command (or doubt) means not now.</summary>
        public static bool InCommand(ISldWorks sw) => State(sw).AnySignal;
    }
}
