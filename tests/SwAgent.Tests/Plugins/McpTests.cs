using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SwAgent.Plugins;
using SwAgent.Plugins.Hooks;
using Xunit;
using static SwAgent.Tests.Plugins.RuntimeTestKit;

namespace SwAgent.Tests.Plugins
{
    /// <summary>MCP servers for real: tests/fixtures/mcp-echo.mjs, a stdio server in node, run by the plugin runtime.</summary>
    public class McpTests
    {
        private const string Echo = "mcp__plugin_echoer_echo__echo";

        private static PluginRuntime EchoPlugin(string name = "echoer") => Plugin(name, mcp: new Dictionary<string, JObject>
        {
            ["echo"] = new JObject
            {
                ["command"] = "node",
                ["args"] = new JArray("${CLAUDE_PLUGIN_ROOT}/mcp-echo.mjs"),
                ["env"] = new JObject { ["EXTRA"] = "pid ${SOLIDWORKS_AGENT_PID}" },
            },
        });

        private static async Task<RuntimeHost> Started(params PluginRuntime[] plugins)
        {
            var host = Runtime(out _, plugins);
            host.Start();
            await host.SyncAsync();
            await host.Mcp.WhenStarted(TimeSpan.FromSeconds(60));
            return host;
        }

        private static string Text(JObject result) => string.Join("\n", ((JArray)result["content"]).Where(c => (string)c["type"] == "text").Select(c => (string)c["text"]));

        private static string State(RuntimeHost host, string server = "echo") => (string)host.Mcp.Status().FirstOrDefault(s => (string)s["server"] == server)?["state"];

        [NodeFact]
        public async Task A_plugin_server_starts_its_tools_join_the_list_and_calls_work()
        {
            using (var host = await Started(EchoPlugin()))
            {
                Assert.Equal("running", State(host));
                var status = (JObject)host.Mcp.Status().Single();
                Assert.Equal("echoer", (string)status["plugin"]);
                Assert.Equal(7, (int)status["toolCount"]);

                var tools = host.MergeTools(new JArray(new JObject { ["name"] = "get_selection" }));
                Assert.Equal("get_selection", (string)tools[0]["name"]);
                var echo = (JObject)tools.Single(t => (string)t["name"] == Echo);
                Assert.Equal("Echoes its text.", (string)echo["description"]);
                Assert.Null(echo["input_schema"]["$schema"]);
                var image = tools.Single(t => (string)t["name"] == "mcp__plugin_echoer_echo__image");
                Assert.Null(image["input_schema"]["anyOf"]);
                Assert.Equal("object", (string)image["input_schema"]["type"]);

                var r = await host.CallAsync(Echo, new JObject { ["text"] = "hello ✓" }, "t1", "tu1");
                Assert.False((bool)r["isError"]);
                Assert.Equal("hello ✓", Text(r));

                var info = host.Info();
                var instructions = (JObject)((JArray)info["mcpInstructions"]).Single();
                Assert.Equal("echo", (string)instructions["server"]);
                Assert.Equal("Echo server: use echo to repeat text.", (string)instructions["text"]);
            }
        }

        [NodeFact]
        public async Task The_server_is_a_direct_child_named_solidworks_and_answered_properly()
        {
            using (var host = await Started(EchoPlugin()))
            {
                var who = JObject.Parse(Text(await host.CallAsync("mcp__plugin_echoer_echo__whoami", null, "t1", "tu")));
                // agent-bridge's mcp.mjs reports process.ppid as agent_pid, and hook.mjs binds through it: it must be
                // SolidWorks (here the test process), not a cmd.exe wrapper.
                Assert.Equal(Process.GetCurrentProcess().Id, (int)who["ppid"]);
                Assert.Equal("solidworks-agent", (string)who["clientName"]);
                Assert.True((bool)who["initialized"]);
                Assert.Equal(Session.ProjectDir.TrimEnd('\\'), ((string)who["cwd"]).TrimEnd('\\'), StringComparer.OrdinalIgnoreCase);
                Assert.Equal(host.Session.Id, (string)who["env"]["CLAUDE_CODE_SESSION_ID"]);
                Assert.Equal("1", (string)who["env"]["SOLIDWORKS_AGENT"]);
                Assert.Equal("pid " + Process.GetCurrentProcess().Id, (string)who["env"]["EXTRA"]);
                Assert.Equal(FixtureDir, (string)who["env"]["CLAUDE_PLUGIN_ROOT"]);
                // The server's own requests got answers: ping a result, roots/list "method not found".
                Assert.NotNull(who["answers"]["q-ping"]["result"]);
                Assert.Equal(-32601, (int)who["answers"]["q-roots"]["error"]);
            }
        }

        [NodeFact]
        public async Task List_changed_refetches_the_tools_and_tells_the_panel()
        {
            var host = Runtime(out var events, EchoPlugin());
            using (host)
            {
                host.Start();
                await host.SyncAsync();
                await host.Mcp.WhenStarted(TimeSpan.FromSeconds(60));
                Assert.DoesNotContain(host.Mcp.Definitions(), t => (string)t["name"] == "mcp__plugin_echoer_echo__echo2");
                var before = events.Count(e => e.name == "tools.changed");
                await host.CallAsync(Echo, new JObject { ["text"] = "x" }, "t1", "tu");
                Assert.True(await Until(() => host.Mcp.Definitions().Any(t => (string)t["name"] == "mcp__plugin_echoer_echo__echo2"), TimeSpan.FromSeconds(15)));
                Assert.True(events.Count(e => e.name == "tools.changed") > before);
            }
        }

        [NodeFact]
        public async Task Images_errors_cancel_and_timeout()
        {
            using (var host = await Started(EchoPlugin()))
            {
                var image = await host.CallAsync("mcp__plugin_echoer_echo__image", null, "t1", "tu");
                Assert.Equal("image", (string)image["content"][1]["type"]);
                Assert.Equal("image/png", (string)image["content"][1]["source"]["media_type"]);

                var fail = await host.CallAsync("mcp__plugin_echoer_echo__fail", null, "t1", "tu");
                Assert.True((bool)fail["isError"]);
                Assert.Equal("it went wrong", Text(fail));

                var slow = host.CallAsync("mcp__plugin_echoer_echo__slow", null, "t-slow", "tu");
                await Task.Delay(300);
                Assert.False(slow.IsCompleted);
                host.Cancel("t-other");
                await Task.Delay(200);
                Assert.False(slow.IsCompleted);
                host.Cancel("t-slow");
                var cancelled = await slow;
                Assert.True((bool)cancelled["isError"]);
                Assert.Equal("Cancelled.", Text(cancelled));
                var cancels = "[]";
                for (var i = 0; i < 100 && cancels == "[]"; i++)
                {
                    await Task.Delay(100);
                    cancels = Text(await host.CallAsync("mcp__plugin_echoer_echo__cancels", null, "t1", "tu"));
                }
                Assert.NotEqual("[]", cancels); // the server got notifications/cancelled

                host.Mcp.CallTimeout = TimeSpan.FromSeconds(1);
                var timedOut = await host.CallAsync("mcp__plugin_echoer_echo__slow", null, "t1", "tu");
                Assert.True((bool)timedOut["isError"]);
                Assert.Contains("timed out", Text(timedOut));

                var unknown = await host.CallAsync("mcp__plugin_nobody_x__y", null, "t1", "tu");
                Assert.True((bool)unknown["isError"]);
            }
        }

        [NodeFact]
        public async Task A_crashed_server_restarts_and_restart_by_hand_works()
        {
            using (var host = await Started(EchoPlugin()))
            {
                var first = host.Mcp.ProcessIdOf("echo");
                await host.CallAsync("mcp__plugin_echoer_echo__crash", null, "t1", "tu");
                Assert.True(await Until(() => State(host) == "running" && host.Mcp.ProcessIdOf("echo") != first, TimeSpan.FromSeconds(30)));
                var back = await host.CallAsync(Echo, new JObject { ["text"] = "back" }, "t1", "tu");
                Assert.False((bool)back["isError"], back.ToString());

                var second = host.Mcp.ProcessIdOf("echo");
                Assert.True(host.Mcp.Restart("echoer", "echo"));
                Assert.True(await Until(() => State(host) == "running" && host.Mcp.ProcessIdOf("echo") != second, TimeSpan.FromSeconds(30)));
                Assert.True(await Until(() => !Alive(second.Value), TimeSpan.FromSeconds(10)));
                Assert.False(host.Mcp.Restart("echoer", "nope"));
            }
        }

        [NodeFact]
        public async Task Disabling_the_plugin_or_the_setting_stops_its_server()
        {
            var source = new FixedSource(new[] { EchoPlugin() });
            using (var host = new RuntimeHost(source, Session.ForTests("mcp-off"), null) { TerminateJobOnShutdown = false, McpJsonPath = Path.Combine(TempDir(), "mcp.json") })
            {
                host.Start();
                await host.SyncAsync();
                await host.Mcp.WhenStarted(TimeSpan.FromSeconds(60));
                var pid = host.Mcp.ProcessIdOf("echo").Value;

                host.ApplySettings(new JObject { ["pluginMcp"] = false });
                Assert.True(await Until(() => host.Mcp.Status().Count == 0 && !Alive(pid), TimeSpan.FromSeconds(15)));
                Assert.Empty(host.Mcp.Definitions());

                host.ApplySettings(new JObject());
                Assert.True(await Until(() => State(host) == "running", TimeSpan.FromSeconds(60)));
                pid = host.Mcp.ProcessIdOf("echo").Value;
                source.Set(new PluginRuntime[0]);
                Assert.True(await Until(() => host.Mcp.Status().Count == 0 && !Alive(pid), TimeSpan.FromSeconds(15)));
            }
        }

        [NodeFact]
        public async Task User_servers_come_from_mcp_json_with_short_names()
        {
            var host = Runtime(out _);
            using (host)
            {
                File.WriteAllText(host.McpJsonPath, new JObject
                {
                    ["mcpServers"] = new JObject
                    {
                        ["sim"] = new JObject { ["command"] = "node", ["args"] = new JArray(Path.Combine(FixtureDir, "mcp-echo.mjs")) },
                        ["web"] = new JObject { ["type"] = "http", ["url"] = "http://127.0.0.1:9/mcp" },
                    },
                }.ToString());
                host.Start();
                await host.SyncAsync();
                await host.Mcp.WhenStarted(TimeSpan.FromSeconds(60));
                Assert.Equal("running", State(host, "sim"));
                Assert.Null((string)host.Mcp.Status().First(s => (string)s["server"] == "sim")["plugin"]);
                // a remote server is tried too: nothing listens on port 9
                Assert.Contains("can't reach http://127.0.0.1:9/mcp", (string)host.Mcp.Status().First(s => (string)s["server"] == "web")["error"]);
                Assert.Equal("ok", Text(await host.CallAsync("mcp__sim__echo", new JObject { ["text"] = "ok" }, "t", "tu")));

                // Editing mcp.json is picked up (the file is watched).
                File.WriteAllText(host.McpJsonPath, "{ \"mcpServers\": {} }");
                Assert.True(await Until(() => host.Mcp.Status().Count == 0, TimeSpan.FromSeconds(15)));
            }
        }

        [NodeFact]
        public async Task Session_end_closes_stdin_first_and_then_nothing_is_left()
        {
            var host = await Started(EchoPlugin());
            var pid = host.Mcp.ProcessIdOf("echo").Value;
            var watch = Stopwatch.StartNew();
            host.Dispose();
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
            Assert.True(await Until(() => !Alive(pid), TimeSpan.FromSeconds(5)));
            Assert.Empty(host.Mcp.Status());
        }
    }
}
