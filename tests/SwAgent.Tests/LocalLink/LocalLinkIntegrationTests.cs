using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SwAgent.Plugins;
using SwAgent.Plugins.Hooks;
using SwAgent.Plugins.LocalLink;
using SwAgent.Tests.Plugins;
using Tomlyn.Model;
using Unify3D.LocalLink;
using Xunit;

namespace SwAgent.Tests.LocalLink
{
    /// <summary>
    /// docs/plugins/local-mcp.md I1, both tracks together: the repository's built-in marketplace (a copy), Claude MCP and Codex MCP
    /// installed through the trust step into fake client folders, the real Unify3D.LocalMcp.exe started exactly as the written
    /// config entry says, the real LinkServer + LinkHost on a test pipe, and a fake panel that answers each wake. Never the
    /// user's pipe, mutex or real Claude Desktop / Codex folders.
    /// </summary>
    [Collection("Plugins")]
    public class LocalLinkIntegrationTests
    {
        private const string Claude = "claude-mcp@unify3d", Codex = "codex-mcp@unify3d";

        private const string CodexOriginal =
            "# My Codex settings\r\n" +
            "model = \"gpt-6.1\"\r\n" +
            "\r\n" +
            "[mcp_servers.other] # a server of my own\r\n" +
            "command = \"node\"\r\n" +
            "args = [\"C:\\\\tools\\\\server.js\"]\r\n" +
            "\r\n" +
            "# keep this comment\r\n" +
            "[profiles.fast]\r\n" +
            "model = \"gpt-6.1-mini\"\r\n";

        /// <summary>Plugins over a copy of plugins/, fake client folders, the built exe deployed to a temp per-user folder.</summary>
        private sealed class Kit : IDisposable
        {
            public readonly PluginTestEnv Plugins = new PluginTestEnv();
            public readonly ClientRoots Roots = new ClientRoots();
            public readonly LocalMcpDeployer Deployer;
            public readonly ThreadContext Ui = new ThreadContext();
            public readonly ConcurrentQueue<JObject> Taken = new ConcurrentQueue<JObject>();
            public readonly ConcurrentQueue<int> ToolThreads = new ConcurrentQueue<int>();
            public readonly string PipeName = "Unify3D.Test." + Guid.NewGuid().ToString("N");
            public RuntimeHost Runtime;
            public LinkHost Host;
            private readonly CancellationTokenSource _stop = new CancellationTokenSource();
            private Task _panel;

            public PluginManager Pm => Plugins.Pm;

            public Kit()
            {
                Copy(Path.Combine(ClientLinkPluginTests.RepoRoot(), "plugins"), Pm.BuiltInRoot);
                Deployer = new LocalMcpDeployer(LocalMcpDeployerTests.BuiltDir(), Path.Combine(Roots.Dir, "local-mcp"));
                ClientRoots.Write(Roots.ClaudeConfig, ClaudeDesktopConfigTests.Realistic);
                ClientRoots.Write(Roots.CodexConfig, CodexOriginal);
                Pm.Links = new ClientLinks(Roots.Locator(), Deployer, Pm.DataRoot);
            }

            /// <summary>Install through the trust dialog, as the panel does.</summary>
            public void Install(string id)
            {
                var ask = Pm.Install(id);
                Assert.NotNull(ask.NeedsTrust);
                Assert.True(Pm.Install(id, ask.Hash).Ok);
            }

            /// <summary>The add-in side: LinkHost on the test pipe, and a panel that takes each wake and answers it.</summary>
            public void StartLink(string answer)
            {
                Runtime = RuntimeTestKit.Runtime(out _);
                Host = new LinkHost(Pm, Runtime.Session, Runtime.QueueLinkWake, LinkHost.UiRunner(Ui), ReadStatus, RunTool, "0.2.0")
                {
                    PipeName = PipeName,
                    MutexName = @"Local\Unify3D.Test." + Guid.NewGuid().ToString("N"),
                };
                Host.Start();
                Assert.True(RuntimeTestKit.Until(() => Host.Server?.Serving == true, TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(), "the test pipe serves");
                Host.OnPanel(new JObject { ["chat"] = new JObject { ["title"] = "Bushing", ["busy"] = false } });
                _panel = Task.Run(async () =>
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        foreach (var w in Runtime.Session.PendingWakes().OfType<JObject>())
                        {
                            if (!Runtime.Session.AckWake((string)w["id"])) continue;
                            Taken.Enqueue(w);
                            var tickets = new JArray((string)w["ticket"]);
                            Host.OnState(new JObject { ["tickets"] = tickets, ["state"] = "working" });
                            await Task.Delay(300).ConfigureAwait(false); // the turn
                            Host.OnAnswer(new JObject { ["tickets"] = tickets, ["threadId"] = "t1", ["state"] = "done", ["text"] = answer });
                        }
                        await Task.Delay(30).ConfigureAwait(false);
                    }
                });
            }

            private static JObject ReadStatus() => new JObject
            {
                ["solidWorks"] = "2026 SP1.0",
                ["document"] = new JObject { ["title"] = "bushing.SLDPRT", ["type"] = "part", ["path"] = @"C:\models\bushing.SLDPRT" },
            };

            private Task<JObject> RunTool(string tool, JObject input, bool needsDocument)
            {
                ToolThreads.Enqueue(Thread.CurrentThread.ManagedThreadId);
                return Task.FromResult(new JObject
                {
                    ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = $"{tool}: bushing.SLDPRT, 3 features" }),
                    ["isError"] = false,
                });
            }

            /// <summary>The exe as Claude Desktop would start it: command and args from the written claude_desktop_config.json.</summary>
            public McpProcess ClaudeDesktopStarts(string pipe)
            {
                var entry = JObject.Parse(File.ReadAllText(Roots.ClaudeConfig))["mcpServers"]["unify3d"];
                return new McpProcess(pipe, (string)entry["command"], string.Join(" ", entry["args"].Values<string>()));
            }

            /// <summary>The exe as Codex would start it: command and args from the written config.toml.</summary>
            public McpProcess CodexStarts(string pipe)
            {
                var entry = (TomlTable)((TomlTable)Tomlyn.Toml.ToModel(File.ReadAllText(Roots.CodexConfig))["mcp_servers"])["unify3d"];
                return new McpProcess(pipe, (string)entry["command"], string.Join(" ", ((TomlArray)entry["args"]).Cast<string>()));
            }

            public void Dispose()
            {
                _stop.Cancel();
                try
                {
                    _panel?.Wait(2000);
                }
                catch (AggregateException)
                {
                    // Test over.
                }
                Host?.Dispose();
                Runtime?.Dispose();
                Ui.Dispose();
                Plugins.Dispose();
                Roots.Dispose();
            }
        }

        private static void Copy(string from, string to)
        {
            foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(dir.Replace(from, to));
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories)) File.Copy(f, f.Replace(from, to), true);
        }

        private static string Text(JObject response) => (string)response["result"]["content"][0]["text"];

        private static JObject Init(McpProcess mcp)
        {
            var init = mcp.Call(0, "initialize", new JObject
            {
                ["protocolVersion"] = "2025-11-25",
                ["capabilities"] = new JObject(),
                ["clientInfo"] = new JObject { ["name"] = "claude-ai", ["version"] = "3.6556.2" },
            });
            mcp.Send(new JObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });
            return init;
        }

        [Fact]
        public void Claude_Desktop_asks_over_stdio_and_gets_the_panels_answer()
        {
            using (var k = new Kit())
            {
                k.Install(Claude);
                k.StartLink("Fillet added: 2 mm on the four top edges.");
                using (var mcp = k.ClaudeDesktopStarts(k.PipeName))
                {
                    Assert.Equal("unify3d", (string)Init(mcp)["result"]["serverInfo"]["name"]);

                    // ask_unify3d: a wake from Claude Desktop into the open chat, the panel's answer back over stdio.
                    var ask = mcp.Tool(1, "ask_unify3d", new JObject { ["message"] = "Add a 2 mm fillet to the top edges." });
                    Assert.False((bool)ask["result"]["isError"]);
                    Assert.Equal("Fillet added: 2 mm on the four top edges.", Text(ask));
                    Assert.Equal("done", (string)ask["result"]["structuredContent"]["state"]);
                    var wake = Assert.Single(k.Taken);
                    Assert.Equal("Claude Desktop", (string)wake["from"]);
                    Assert.Equal("claude-mcp", (string)wake["plugin"]);
                    Assert.Equal((string)ask["result"]["structuredContent"]["ticket"], (string)wake["ticket"]);
                    Assert.Contains("<message>\nAdd a 2 mm fillet to the top edges.\n</message>", (string)wake["text"]);

                    // A short wait gives a ticket; get_unify3d_answer picks the answer up later.
                    var quick = mcp.Tool(2, "ask_unify3d", new JObject { ["message"] = "And the bottom edges?", ["wait_seconds"] = 0 });
                    var ticket = (string)quick["result"]["structuredContent"]["ticket"];
                    Assert.NotEqual("done", (string)quick["result"]["structuredContent"]["state"]);
                    Assert.Contains($"get_unify3d_answer with ticket \"{ticket}\"", Text(quick));
                    Assert.True(RuntimeTestKit.Until(() => k.Host.Tickets.Find(ticket)?.State == LinkTickets.Done, TimeSpan.FromSeconds(10)).GetAwaiter().GetResult());
                    var later = mcp.Tool(3, "get_unify3d_answer", new JObject { ["ticket"] = ticket });
                    Assert.Equal("Fillet added: 2 mm on the four top edges.", Text(later));

                    // Status and a read-only model tool; the tool ran on the (fake) SolidWorks UI thread.
                    var status = Text(mcp.Tool(4, "unify3d_status"));
                    Assert.Contains("SolidWorks 2026 SP1.0 is running with Unify3D Agent 0.2.0.", status);
                    Assert.Contains("Active document: bushing.SLDPRT (part).", status);
                    Assert.Contains("Open chat: \"Bushing\" (idle).", status);
                    Assert.Equal("get_model_context: bushing.SLDPRT, 3 features", Text(mcp.Tool(5, "unify3d_model_context")));
                    Assert.All(k.ToolThreads, t => Assert.Equal(k.Ui.ThreadId, t));
                }

                // The add-in saw Claude Desktop: the Plugins screen says connected.
                var link = k.Pm.PluginJson(k.Pm.Find(Claude))["link"];
                Assert.StartsWith("Connected: Claude Desktop last used it", (string)link["note"]);

                // Off: the entry leaves Claude Desktop's file and the pipe closes, so the exe says SolidWorks can't be reached.
                Assert.True(k.Pm.Enable(Claude, false).Ok);
                Assert.Equal(ClaudeDesktopConfigTests.Realistic, File.ReadAllText(k.Roots.ClaudeConfig));
                Assert.True(RuntimeTestKit.Until(() => k.Host.Server == null, TimeSpan.FromSeconds(10)).GetAwaiter().GetResult());
            }
        }

        [Fact]
        public void Without_the_add_in_status_says_SolidWorks_isnt_running()
        {
            using (var k = new Kit())
            {
                k.Install(Codex);
                using (var mcp = k.CodexStarts("Unify3D.Test.nobody-serves-" + Guid.NewGuid().ToString("N")))
                {
                    Init(mcp);
                    var status = mcp.Tool(1, "unify3d_status");
                    Assert.Contains("SolidWorks isn't running", Text(status));
                    var ask = mcp.Tool(2, "ask_unify3d", new JObject { ["message"] = "Hello?" });
                    Assert.True((bool)ask["result"]["isError"]);
                    Assert.Contains("SolidWorks isn't running", Text(ask));
                }
            }
        }

        [Fact]
        public void Install_adds_exactly_one_unify3d_entry_and_uninstall_restores_each_file()
        {
            using (var k = new Kit())
            {
                k.Install(Claude);
                k.Install(Codex);
                Assert.True(File.Exists(k.Deployer.Exe), "the exe was deployed to the per-user folder");

                var claude = JObject.Parse(File.ReadAllText(k.Roots.ClaudeConfig));
                Assert.Equal(new[] { "other", "unify3d" }, ((JObject)claude["mcpServers"]).Properties().Select(p => p.Name));
                Assert.Equal(k.Deployer.Exe, (string)claude["mcpServers"]["unify3d"]["command"]);
                Assert.Equal(new[] { "--client", LinkProtocol.ClaudeDesktop }, claude["mcpServers"]["unify3d"]["args"].Values<string>());
                Assert.Equal("Ctrl+Alt+Space", (string)claude["preferences"]["quickEntryShortcut"]);

                var codexText = File.ReadAllText(k.Roots.CodexConfig);
                Assert.StartsWith(CodexOriginal, codexText);
                var servers = (TomlTable)Tomlyn.Toml.ToModel(codexText)["mcp_servers"];
                Assert.Equal(new[] { "other", "unify3d" }, servers.Keys.OrderBy(x => x));
                Assert.Equal(k.Deployer.Exe, (string)((TomlTable)servers["unify3d"])["command"]);
                Assert.Equal(new[] { "--client", LinkProtocol.Codex }, ((TomlArray)((TomlTable)servers["unify3d"])["args"]).Cast<string>());
                Assert.Equal(1, codexText.Split(new[] { "[mcp_servers.unify3d]" }, StringSplitOptions.None).Length - 1);

                Assert.Empty(k.Pm.Uninstall(Claude));
                Assert.Empty(k.Pm.Uninstall(Codex));
                Assert.Equal(ClaudeDesktopConfigTests.Realistic, File.ReadAllText(k.Roots.ClaudeConfig));
                Assert.Equal(CodexOriginal, File.ReadAllText(k.Roots.CodexConfig));
            }
        }

        [Fact]
        public void The_built_in_marketplace_shows_as_Unify3D_and_ids_stay_at_unify3d()
        {
            using (var k = new Kit())
            {
                var market = k.Pm.MarketplacesJson().OfType<JObject>().Single(m => (string)m["name"] == PluginManager.BuiltInMarketplace);
                Assert.Equal("unify3d", PluginManager.BuiltInMarketplace);
                Assert.Equal("Unify3D", (string)market["displayName"]);

                k.Install(Claude);
                var all = k.Pm.PluginsJson().OfType<JObject>().Where(p => (string)p["marketplace"] == "unify3d").ToList();
                foreach (var id in new[] { Claude, Codex, "agent-bridge@unify3d" })
                {
                    var p = all.Single(x => (string)x["id"] == id);
                    Assert.Equal("Unify3D", (string)p["marketplaceDisplayName"]);
                }
                Assert.Equal("Claude MCP", (string)all.Single(x => (string)x["id"] == Claude)["displayName"]);
                Assert.Equal("Codex MCP", (string)all.Single(x => (string)x["id"] == Codex)["displayName"]);
            }
        }
    }
}
