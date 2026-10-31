using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SwAgent.Plugins.Hooks;
using Xunit;
using static SwAgent.Tests.Plugins.RuntimeTestKit;

namespace SwAgent.Tests.Plugins
{
    /// <summary>Runs only where the real agent-bridge plugin is installed for Claude Code (and node is present).</summary>
    public sealed class AgentBridgeFactAttribute : FactAttribute
    {
        public AgentBridgeFactAttribute()
        {
            if (RuntimeTestKit.Node == null) Skip = "node isn't installed.";
            else if (AgentBridgeHookTests.PluginDir == null) Skip = "The agent-bridge plugin isn't in a Claude Code plugin cache on this PC (or set AGENT_BRIDGE_PLUGIN_DIR).";
        }
    }

    /// <summary>
    /// The real agent-bridge hooks (client/hook.mjs, with the plugin's own hooks.json) run through the runtime, unpaired:
    /// AGENT_BRIDGE_HOME is an empty folder and the hub URL a closed local port, so every hook must exit 0 silently and
    /// nothing reaches the network. This proves the plumbing - command expansion, cmd.exe quoting, stdin, exit codes,
    /// async hooks - against the program the first plugin really runs.
    /// </summary>
    public class AgentBridgeHookTests
    {
        /// <summary>The newest installed agent-bridge plugin folder (client/hook.mjs + hooks/hooks.json), or null.</summary>
        public static string PluginDir
        {
            get
            {
                var set = Environment.GetEnvironmentVariable("AGENT_BRIDGE_PLUGIN_DIR");
                if (!string.IsNullOrEmpty(set)) return File.Exists(Path.Combine(set, "client", "hook.mjs")) ? set : null;
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return new[] { ".claude-second", ".claude" }
                    .Select(c => Path.Combine(home, c, "plugins", "cache", "agent-bridge", "agent-bridge"))
                    .Where(Directory.Exists)
                    .SelectMany(Directory.GetDirectories)
                    .Where(d => File.Exists(Path.Combine(d, "client", "hook.mjs")) && File.Exists(Path.Combine(d, "hooks", "hooks.json")))
                    .OrderByDescending(Directory.GetLastWriteTimeUtc)
                    .FirstOrDefault();
            }
        }

        [AgentBridgeFact]
        public async Task The_real_hooks_run_unpaired_and_exit_0_silently()
        {
            var dir = PluginDir;
            var home = TempDir();
            var hooks = (JObject)JObject.Parse(File.ReadAllText(Path.Combine(dir, "hooks", "hooks.json")))["hooks"];
            var plugin = Plugin("agent-bridge", hooks, env: new Dictionary<string, string>
            {
                ["AGENT_BRIDGE_HOME"] = home,
                ["AGENT_BRIDGE_URL"] = "http://127.0.0.1:9", // a closed local port: never the real hub
                ["AGENT_BRIDGE_TOKEN"] = null,
                ["AGENT_BRIDGE_NAME"] = "SolidWorks Agent - ${SOLIDWORKS_AGENT_DEVICE}",
                ["AGENT_BRIDGE_OWNER_PID"] = "${SOLIDWORKS_AGENT_PID}",
            });
            plugin.Root = dir;
            var finished = new ConcurrentQueue<(string ev, string template, int code, string stdout, string stderr)>();
            using (var host = Runtime(out var events, plugin))
            {
                host.Hooks.Finished += (spec, code, o, e) => finished.Enqueue((null, spec.Template, code, o, e));

                // The panel's events, with the stdin each carries.
                var prompt = await host.FireAsync(new JObject { ["event"] = "UserPromptSubmit", ["threadId"] = "t", ["prompt"] = "hello" });
                var post = await host.FireAsync(new JObject
                {
                    ["event"] = "PostToolUse", ["threadId"] = "t", ["tool_name"] = "get_selection", ["tool_input"] = new JObject(),
                    ["tool_response"] = new JObject { ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = "nothing selected" }) },
                });
                var stop = await host.FireAsync(new JObject { ["event"] = "Stop", ["threadId"] = "t", ["stop_hook_active"] = false });
                foreach (var r in new[] { prompt, post, stop })
                {
                    Assert.False((bool)r["block"]);
                    Assert.True((bool)r["continue"]);
                    Assert.Empty((JArray)r["additionalContext"]);
                    Assert.Empty((JArray)r["systemMessages"]);
                }

                // SessionStart (async --listen) fires from the panel's first session.info, once the servers are up.
                host.Start();
                await host.SyncAsync();
                var info = host.Info();
                // Every event the plugin declares that the runtime knows (newer agent-bridge builds add PreToolUse,
                // matched to Claude Code's AskUserQuestion/ExitPlanMode, which never fires for SolidWorks Agent tools).
                Assert.Equal(hooks.Properties().Select(p => p.Name).Where(HookEvents.All.Contains).OrderBy(x => x),
                    ((JObject)info["hookEvents"]).Properties().Select(p => p.Name).Where(n => n != "PermissionRequest").OrderBy(x => x));
                Assert.Subset(new HashSet<string>(((JObject)info["hookEvents"]).Properties().Select(p => p.Name)),
                    new HashSet<string> { "UserPromptSubmit", "PostToolUse", "Stop", "SessionStart" });

                // UserPromptSubmit, PostToolUse, Stop, Stop --listen (async) and SessionStart --listen (async).
                Assert.True(await Until(() => finished.Count >= 5 && host.Hooks.BackgroundCount == 0, TimeSpan.FromSeconds(90)),
                    $"finished {finished.Count}, background {host.Hooks.BackgroundCount}");
                Assert.Equal(5, finished.Count);
                Assert.All(finished, f =>
                {
                    Assert.Equal(0, f.code);
                    Assert.Equal("", f.stdout.Trim());
                    Assert.Equal("", f.stderr.Trim());
                });
                Assert.Equal(2, finished.Count(f => f.template.Contains("--listen")));
                Assert.Empty(host.Session.PendingWakes());
                Assert.DoesNotContain(events, e => e.name == "session.wake");
            }
            // Unpaired, hook.mjs writes nothing (no unbound markers, no listener locks).
            Assert.Empty(Directory.GetFileSystemEntries(home));
            Directory.Delete(home, true);
        }
    }
}
