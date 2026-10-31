using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SwAgent.Plugins.Hooks;
using Xunit;
using static SwAgent.Tests.Plugins.RuntimeTestKit;

namespace SwAgent.Tests.Plugins
{
    /// <summary>Plugin hooks run for real (node fake hooks in tests/fixtures/hooks) through hooks.fire and the session.</summary>
    public class HookRunnerTests
    {
        private static async Task<JObject> Fire(RuntimeHost host, string ev, JObject extra = null)
        {
            var p = new JObject { ["event"] = ev, ["threadId"] = "t1", ["docKey"] = "doc" };
            foreach (var kv in extra ?? new JObject()) p[kv.Key] = kv.Value;
            return await host.FireAsync(p);
        }

        [NodeFact]
        public async Task Context_plain_text_and_the_stdin_fields_reach_the_panel()
        {
            var plugin = Plugin("ctx", Hooks(HookEvents.UserPromptSubmit, Group(null, Cmd(Hook("context")), Cmd(Hook("plain")), Cmd(Hook("stdin")))));
            using (var host = Runtime(out _, plugin))
            {
                var r = await Fire(host, HookEvents.UserPromptSubmit, new JObject { ["prompt"] = "make it 5 mm thicker", ["turn_id"] = "never sent" });
                Assert.False((bool)r["block"]);
                Assert.True((bool)r["continue"]);
                var contexts = ((JArray)r["additionalContext"]).Select(c => (string)c).ToList();
                Assert.Contains("context from UserPromptSubmit", contexts);
                Assert.Contains("plain words from the hook", contexts);
                var stdin = JObject.Parse(contexts.Single(c => c.StartsWith("{")));
                Assert.Equal(host.Session.Id, (string)stdin["session_id"]);
                Assert.Equal("UserPromptSubmit", (string)stdin["hook_event_name"]);
                Assert.Equal("make it 5 mm thicker", (string)stdin["prompt"]);
                Assert.Equal("default", (string)stdin["permission_mode"]);
                Assert.Equal(Session.ProjectDir, (string)stdin["cwd"]);
                Assert.Equal(host.Session.TranscriptPath, (string)stdin["transcript_path"]);
                Assert.Null(stdin["turn_id"]); // hook.mjs would take a turn_id for Codex
            }
        }

        [NodeFact]
        public async Task Hooks_get_the_session_and_plugin_environment()
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_TEST_SECRET_B", "sk-test");
            Environment.SetEnvironmentVariable("OPENAI_TEST_SECRET_B", "sk-proj-test");
            try
            {
                var plugin = Plugin("envy", Hooks(HookEvents.Stop, Group(null, Cmd(Hook("env")))),
                    env: new Dictionary<string, string> { ["AGENT_BRIDGE_NAME"] = "SolidWorks Agent - ${SOLIDWORKS_AGENT_DEVICE}" });
                using (var host = Runtime(out _, plugin))
                {
                    var r = await Fire(host, HookEvents.Stop);
                    var env = JObject.Parse((string)r["additionalContext"][0]);
                    Assert.Equal(host.Session.Id, (string)env["CLAUDE_CODE_SESSION_ID"]);
                    Assert.Equal(FixtureDir, (string)env["CLAUDE_PLUGIN_ROOT"]);
                    Assert.Equal(plugin.DataDir, (string)env["CLAUDE_PLUGIN_DATA"]);
                    Assert.Equal(Session.ProjectDir, (string)env["CLAUDE_PROJECT_DIR"]);
                    Assert.Equal("1", (string)env["SOLIDWORKS_AGENT"]);
                    Assert.Equal(Process.GetCurrentProcess().Id.ToString(), (string)env["SOLIDWORKS_AGENT_PID"]);
                    Assert.Equal("SolidWorks Agent - " + Environment.MachineName, (string)env["AGENT_BRIDGE_NAME"]);
                    Assert.Equal(JTokenType.Null, env["ANTHROPIC_TEST_SECRET_B"].Type);
                    Assert.Equal(JTokenType.Null, env["OPENAI_TEST_SECRET_B"].Type);
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("ANTHROPIC_TEST_SECRET_B", null);
                Environment.SetEnvironmentVariable("OPENAI_TEST_SECRET_B", null);
            }
        }

        [NodeFact]
        public async Task Exit_2_blocks_with_stderr_and_pre_tool_use_decisions_combine()
        {
            var plugin = Plugin("gate", new JObject
            {
                [HookEvents.UserPromptSubmit] = new JArray(Group(null, Cmd(Hook("block2")))),
                [HookEvents.PreToolUse] = new JArray(
                    Group("execute_csharp", Cmd(Hook("ask"))),
                    Group("execute_.*", Cmd(Hook("allow"))),
                    Group("delete_everything", Cmd(Hook("deny")))),
                [HookEvents.Stop] = new JArray(Group(null, Cmd(Hook("exit1")), Cmd(Hook("system")), Cmd(Hook("stop")))),
            });
            using (var host = Runtime(out _, plugin))
            {
                var prompt = await Fire(host, HookEvents.UserPromptSubmit, new JObject { ["prompt"] = "x" });
                Assert.True((bool)prompt["block"]);
                Assert.Equal("blocked: ünïcode reason", (string)prompt["reason"]); // UTF-8 both ways

                var ask = await Fire(host, HookEvents.PreToolUse, new JObject { ["tool_name"] = "execute_csharp", ["tool_input"] = new JObject { ["code"] = "1" } });
                Assert.Equal("ask", (string)ask["permissionDecision"]);
                Assert.Equal("ask reason", (string)ask["permissionDecisionReason"]);
                Assert.False((bool)ask["block"]);

                var deny = await Fire(host, HookEvents.PreToolUse, new JObject { ["tool_name"] = "delete_everything" });
                Assert.Equal("deny", (string)deny["permissionDecision"]);
                Assert.True((bool)deny["block"]);

                var none = await Fire(host, HookEvents.PreToolUse, new JObject { ["tool_name"] = "get_selection" });
                Assert.Null((string)none["permissionDecision"]);

                var stop = await Fire(host, HookEvents.Stop);
                Assert.False((bool)stop["block"]); // exit 1 is only logged
                Assert.False((bool)stop["continue"]);
                Assert.Equal("stop now", (string)stop["stopReason"]);
                Assert.Equal(new[] { "a notice for the user" }, ((JArray)stop["systemMessages"]).Select(m => (string)m));
            }
        }

        [NodeFact]
        public async Task A_batch_file_hook_and_identical_commands_run_once()
        {
            var finished = new ConcurrentBag<string>();
            var refuse = Path.Combine(FixtureDir, "hooks", "refuse.cmd");
            var plugin = Plugin("dup", Hooks(HookEvents.PostToolUse,
                Group("*", Cmd(Hook("context"))),
                Group("get_.*", Cmd(Hook("context"))),
                Group(null, Cmd("\"" + refuse + "\""))));
            using (var host = Runtime(out _, plugin))
            {
                host.Hooks.Finished += (spec, code, o, e) => finished.Add(spec.Template);
                var r = await Fire(host, HookEvents.PostToolUse, new JObject
                {
                    ["tool_name"] = "get_selection",
                    ["tool_input"] = new JObject(),
                    ["tool_response"] = new JObject { ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = "Face<1>" }) },
                });
                Assert.Single((JArray)r["additionalContext"], c => (string)c == "context from PostToolUse");
                Assert.Equal(2, finished.Count);
                Assert.True((bool)r["block"]);
                Assert.Equal("refused by a batch hook", (string)r["reason"]);
            }
        }

        [NodeFact]
        public async Task A_hook_past_its_timeout_is_killed_with_its_whole_tree()
        {
            var dir = TempDir();
            var pids = Path.Combine(dir, "pids.txt");
            var plugin = Plugin("slow", Hooks(HookEvents.Stop, Group(null, Cmd(Hook("sleep", pids), timeout: 3))));
            using (var host = Runtime(out _, plugin))
            {
                var watch = Stopwatch.StartNew();
                var r = await Fire(host, HookEvents.Stop);
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20));
                Assert.False((bool)r["block"]);
                var ids = File.ReadAllText(pids).Split(' ').Select(int.Parse).ToArray();
                // The hook (node under cmd.exe) and the child it started are gone, not just cmd.exe.
                Assert.True(await Until(() => !Alive(ids[0]) && !Alive(ids[1]), TimeSpan.FromSeconds(10)));
            }
            Directory.Delete(dir, true);
        }

        [NodeFact]
        public async Task Hooks_run_under_exactly_one_shell_so_solidworks_is_the_grandparent()
        {
            var dir = TempDir();
            var file = Path.Combine(dir, "ppid.txt");
            var plugin = Plugin("tree", Hooks(HookEvents.Stop, Group(null, Cmd(Hook("ppid", file), async: true))));
            using (var host = Runtime(out _, plugin))
            {
                await Fire(host, HookEvents.Stop);
                Assert.True(await Until(() => File.Exists(file) && File.ReadAllText(file).Contains(" "), TimeSpan.FromSeconds(15)));
                var ids = File.ReadAllText(file).Split(' ').Select(int.Parse).ToArray();
                // Git Bash's bash.exe as in Claude Code, or cmd.exe without Git for Windows (PluginReviewTests runs both).
                Assert.Equal(HookShell.Bash == null ? "cmd" : "bash", Process.GetProcessById(ids[1]).ProcessName);
                Assert.Equal(Process.GetCurrentProcess().Id, ParentOf(ids[1]));
            }
            Directory.Delete(dir, true);
        }

        [NodeFact]
        public async Task An_async_rewake_hook_wakes_through_the_csharp_queue_and_the_guard_holds_the_rest()
        {
            var plugin = Plugin("bridge", Hooks(HookEvents.Stop,
                Group(null, Cmd(Hook("rewake"), async: true, rewake: true, rewakeMessage: "agent-bridge:"))));
            using (var host = Runtime(out var events, plugin))
            {
                host.Session.AutomaticPerHour = 1;
                var r = await Fire(host, HookEvents.Stop);
                Assert.False((bool)r["block"]); // the turn didn't wait for it

                Assert.True(await Until(() => events.Any(e => e.name == "session.wake"), TimeSpan.FromSeconds(15)));
                var wakes = host.Session.PendingWakes();
                var wake = (JObject)wakes.Single();
                Assert.Equal($"agent-bridge: New messages for {host.Session.Id}", (string)wake["text"]);
                Assert.Equal("Stop", (string)wake["source"]);
                Assert.Equal("bridge", (string)wake["plugin"]);
                // A wake stays queued until the panel confirms it (a reload in between loses nothing).
                Assert.Single(host.Session.PendingWakes());
                Assert.True(host.Session.AckWake((string)wake["id"]));
                Assert.Empty(host.Session.PendingWakes());

                // The hour's allowance (1) is used: the next wake is held and rides along with the user's next prompt.
                await Fire(host, HookEvents.Stop);
                Assert.True(await Until(() => host.Session.HeldCount == 1, TimeSpan.FromSeconds(15)));
                Assert.Empty(host.Session.PendingWakes());
                Assert.Equal(1, (int)host.Info()["heldWakes"]);
                var prompt = await Fire(host, HookEvents.UserPromptSubmit, new JObject { ["prompt"] = "hi" });
                Assert.Equal($"agent-bridge: New messages for {host.Session.Id}", (string)prompt["additionalContext"][0]);
                Assert.Single((JArray)prompt["systemMessages"]);
                Assert.Equal(0, host.Session.HeldCount);
            }
        }

        [NodeFact]
        public async Task Stop_hook_continuations_count_against_the_hourly_guard()
        {
            var plugin = Plugin("loop", Hooks(HookEvents.Stop, Group(null, Cmd(Hook("jsonblock")))));
            using (var host = Runtime(out _, plugin))
            {
                host.Session.AutomaticPerHour = 2;
                Assert.True((bool)(await Fire(host, HookEvents.Stop))["block"]);
                Assert.True((bool)(await Fire(host, HookEvents.Stop, new JObject { ["stop_hook_active"] = true }))["block"]);
                var third = await Fire(host, HookEvents.Stop, new JObject { ["stop_hook_active"] = true });
                Assert.False((bool)third["block"]);
                Assert.Contains("automatic turns per hour", (string)third["systemMessages"][0]);
            }
        }

        [NodeFact]
        public async Task Session_info_lists_matchers_and_session_start_fires_once()
        {
            var dir = TempDir();
            var file = Path.Combine(dir, "ppid.txt");
            var plugin = Plugin("start", new JObject
            {
                [HookEvents.SessionStart] = new JArray(Group("startup", Cmd(Hook("context"))), Group("resume", Cmd(Hook("ppid", file)))),
                [HookEvents.PostToolUse] = new JArray(Group("mcp__.*", Cmd(Hook("context")))),
            });
            using (var host = Runtime(out var events, plugin))
            {
                host.Start();
                await host.SyncAsync();
                var info = host.Info();
                Assert.Equal(host.Session.Id, (string)info["sessionId"]);
                Assert.Equal(new[] { "mcp__.*" }, ((JArray)info["hookEvents"]["PostToolUse"]).Select(m => (string)m));
                Assert.Null(info["hookEvents"]["Stop"]);
                Assert.True(await Until(() => ((JArray)host.Info()["sessionContext"]).Count == 1, TimeSpan.FromSeconds(60)));
                Assert.Equal("context from SessionStart", (string)host.Info()["sessionContext"][0]);
                Assert.Contains(events, e => e.name == "session.changed");
                await Task.Delay(500);
                Assert.Single((JArray)host.Info()["sessionContext"]); // once per session, not per call
                Assert.False(File.Exists(file)); // the "resume" matcher didn't match "startup"
            }
            Directory.Delete(dir, true);
        }

        [NodeFact]
        public async Task Hooks_are_off_with_the_setting_and_after_session_end()
        {
            var plugin = Plugin("off", Hooks(HookEvents.UserPromptSubmit, Group(null, Cmd(Hook("context")))));
            using (var host = Runtime(out _, plugin))
            {
                host.ApplySettings(new JObject { ["pluginHooks"] = false });
                Assert.Empty((JArray)(await Fire(host, HookEvents.UserPromptSubmit))["additionalContext"]);
                Assert.Empty((JObject)host.Info()["hookEvents"]);
                host.ApplySettings(new JObject());
                Assert.Single((JArray)(await Fire(host, HookEvents.UserPromptSubmit))["additionalContext"]);
                host.Shutdown();
                Assert.Empty((JArray)(await Fire(host, HookEvents.UserPromptSubmit))["additionalContext"]);
            }
        }

        [NodeFact]
        public async Task Session_end_kills_background_hooks()
        {
            var dir = TempDir();
            var pids = Path.Combine(dir, "pids.txt");
            var plugin = Plugin("bg", Hooks(HookEvents.Stop, Group(null, Cmd(Hook("sleep", pids), async: true, rewake: true, timeout: 604800))));
            using (var host = Runtime(out _, plugin))
            {
                await Fire(host, HookEvents.Stop);
                Assert.True(await Until(() => File.Exists(pids) && File.ReadAllText(pids).Contains(" "), TimeSpan.FromSeconds(15)));
                var ids = File.ReadAllText(pids).Split(' ').Select(int.Parse).ToArray();
                Assert.Equal(1, host.Hooks.BackgroundCount);
                var watch = Stopwatch.StartNew();
                host.Shutdown();
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(6));
                Assert.True(await Until(() => !Alive(ids[0]) && !Alive(ids[1]), TimeSpan.FromSeconds(10)));
            }
            Directory.Delete(dir, true);
        }

        [NodeFact]
        public async Task Hooks_of_a_plugin_that_may_no_longer_run_are_killed()
        {
            var dir = TempDir();
            var pids = Path.Combine(dir, "pids.txt");
            var plugin = Plugin("gone", Hooks(HookEvents.Stop, Group(null, Cmd(Hook("sleep", pids), async: true, timeout: 600))));
            var source = new FixedSource(new[] { plugin });
            using (var host = new RuntimeHost(source, Session.ForTests("gone"), null) { TerminateJobOnShutdown = false })
            {
                await host.SyncAsync();
                await host.FireAsync(new JObject { ["event"] = "Stop", ["threadId"] = "t" });
                Assert.True(await Until(() => File.Exists(pids) && File.ReadAllText(pids).Contains(" "), TimeSpan.FromSeconds(15)));
                var ids = File.ReadAllText(pids).Split(' ').Select(int.Parse).ToArray();
                source.Set(new SwAgent.Plugins.PluginRuntime[0]); // disabled
                Assert.True(await Until(() => !Alive(ids[0]) && !Alive(ids[1]), TimeSpan.FromSeconds(10)));
            }
            Directory.Delete(dir, true);
        }

        [Fact]
        public async Task The_panel_can_fire_only_its_own_events()
        {
            using (var host = Runtime(out _))
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => host.FireAsync(new JObject { ["event"] = "SessionEnd" }));
                var nothing = await host.FireAsync(new JObject { ["event"] = "Stop" });
                Assert.False((bool)nothing["block"]);
            }
        }
    }
}
