using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SwAgent.Infrastructure;
using SwAgent.Plugins;
using Xunit;

namespace SwAgent.Tests.Plugins
{
    /// <summary>
    /// docs/plugins/local-mcp.md C4 and C6: Claude MCP and Codex MCP through PluginManager (install, trust, turn on/off, uninstall,
    /// Repair, status), over a copy of the repository's built-in marketplace and fake client folders. Never the real configs.
    /// </summary>
    [Collection("Plugins")]
    public class ClientLinkPluginTests
    {
        private const string Claude = "claude-mcp@unify3d", Codex = "codex-mcp@unify3d";

        internal static string RepoRoot()
        {
            var d = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "plugins", ".claude-plugin", "marketplace.json"))) d = d.Parent;
            Assert.True(d != null, "plugins/.claude-plugin/marketplace.json not found above the test folder");
            return d.FullName;
        }

        /// <summary>A plugin manager whose built-in marketplace is a copy of plugins/, and whose client links use fake folders.</summary>
        private sealed class Env : IDisposable
        {
            public readonly PluginTestEnv Plugins;
            public readonly ClientRoots Roots;
            public readonly LocalMcpDeployer Deployer;
            public PluginManager Pm => Plugins.Pm;

            public Env(bool claude = true, bool codex = true)
            {
                Plugins = new PluginTestEnv();
                Roots = new ClientRoots();
                CopyDirectory(Path.Combine(RepoRoot(), "plugins"), Pm.BuiltInRoot);
                var shipped = Path.Combine(Roots.Dir, "shipped");
                ClientRoots.Write(Path.Combine(shipped, LocalMcpDeployer.ExeName), "not really an exe");
                Deployer = new LocalMcpDeployer(shipped, Path.Combine(Roots.Dir, "local-mcp"));
                if (claude) ClientRoots.Write(Path.Combine(Roots.ClaudeDir, "config.json"), "{}");
                if (codex) Directory.CreateDirectory(Roots.CodexHome);
                Pm.Links = new ClientLinks(Roots.Locator(), Deployer, Pm.DataRoot);
            }

            public string Exe => Deployer.Exe;

            public JObject ClaudeJson => JObject.Parse(File.ReadAllText(Roots.ClaudeConfig));

            /// <summary>Install through the trust dialog, as the panel does.</summary>
            public PluginManager.Outcome Install(string id)
            {
                var first = Pm.Install(id);
                Assert.NotNull(first.NeedsTrust);
                return Pm.Install(id, first.Hash);
            }

            public void Dispose()
            {
                Plugins.Dispose();
                Roots.Dispose();
            }
        }

        private static void CopyDirectory(string from, string to)
        {
            foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(dir.Replace(from, to));
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories)) File.Copy(f, f.Replace(from, to), true);
        }

        [Fact]
        public void Install_asks_first_with_the_file_and_the_program_then_writes_the_entry()
        {
            using (var env = new Env())
            {
                var ask = env.Pm.Install(Claude);
                Assert.False(ask.Ok);
                Assert.NotNull(ask.Hash);
                var line = (string)ask.NeedsTrust["links"].Single();
                Assert.Equal($"Claude Desktop: adds the MCP server \"unify3d\" to {env.Roots.ClaudeConfig}, which runs {env.Exe} --client claude-desktop", line);
                Assert.Empty(ask.NeedsTrust["hooks"]);
                Assert.Empty(ask.NeedsTrust["mcp"]);
                Assert.False(File.Exists(env.Roots.ClaudeConfig), "nothing written before the user agrees");
                Assert.Null(env.Pm.Find(Claude));

                var done = env.Pm.Install(Claude, ask.Hash);
                Assert.True(done.Ok);
                var entry = env.ClaudeJson["mcpServers"]["unify3d"];
                Assert.Equal(env.Exe, (string)entry["command"]);
                Assert.Equal(new[] { "--client", "claude-desktop" }, entry["args"].Values<string>());
                Assert.True(File.Exists(env.Exe), "the program was copied to the per-user folder");
                var p = env.Pm.Find(Claude);
                Assert.True(p.Enabled);
                Assert.True(env.Pm.IsTrusted(p));
                Assert.Equal("claude-desktop", (string)p.Record.Unify3d["registerMcpIn"]);

                // The panel's view: the restart note until Claude Desktop connects.
                var json = env.Pm.PluginJson(p);
                Assert.Equal("Unify3D", (string)json["marketplaceDisplayName"]);
                Assert.Equal("present", (string)json["link"]["entry"]);
                Assert.Equal(env.Roots.ClaudeConfig, (string)json["link"]["configPath"]);
                Assert.Equal("Added to Claude Desktop. Quit Claude Desktop (from the tray icon) and open it again to load it.", (string)json["link"]["note"]);
                Assert.True((bool)json["trusted"]);
            }
        }

        [Fact]
        public void Off_removes_on_adds_without_a_dialog_and_a_new_config_path_asks_again()
        {
            using (var env = new Env())
            {
                env.Install(Claude);
                var off = env.Pm.Enable(Claude, false);
                Assert.True(off.Ok);
                Assert.Empty(off.Warnings);
                Assert.Null(env.ClaudeJson["mcpServers"]);
                Assert.StartsWith("Off: Claude Desktop can't reach Unify3D.", (string)env.Pm.PluginJson(env.Pm.Find(Claude))["link"]["note"]);

                var on = env.Pm.Enable(Claude, true);
                Assert.True(on.Ok);
                Assert.Null(on.NeedsTrust);
                Assert.NotNull(env.ClaudeJson["mcpServers"]["unify3d"]);

                // Claude Desktop now keeps its data in its MSIX package: another file, so the user is asked again.
                env.Pm.Enable(Claude, false);
                var b = Path.Combine(env.Roots.LocalAppData, "Packages", "Claude_abcdefghijklm", "LocalCache", "Roaming", "Claude");
                ClientRoots.Write(Path.Combine(b, "config.json"), "{}");
                File.SetLastWriteTimeUtc(Path.Combine(env.Roots.ClaudeDir, "config.json"), DateTime.UtcNow.AddHours(-1));
                Assert.False((bool)env.Pm.PluginJson(env.Pm.Find(Claude))["trusted"]);
                var again = env.Pm.Enable(Claude, true);
                Assert.NotNull(again.NeedsTrust);
                Assert.Contains(Path.Combine(b, "claude_desktop_config.json"), (string)again.NeedsTrust["links"][0]);
                Assert.False(File.Exists(Path.Combine(b, "claude_desktop_config.json")));
                Assert.True(env.Pm.Enable(Claude, true, again.Hash).Ok);
                Assert.NotNull(JObject.Parse(File.ReadAllText(Path.Combine(b, "claude_desktop_config.json")))["mcpServers"]["unify3d"]);
            }
        }

        [Fact]
        public void Uninstall_removes_the_entry_and_the_files_and_a_locked_file_becomes_a_warning()
        {
            using (var env = new Env())
            {
                env.Install(Claude);
                var root = env.Pm.Find(Claude).Root;
                Assert.Empty(env.Pm.Uninstall(Claude));
                Assert.Null(env.ClaudeJson["mcpServers"]);
                Assert.False(Directory.Exists(root));
                Assert.Null(env.Pm.Find(Claude));

                env.Install(Claude);
                using (new FileStream(env.Roots.ClaudeConfig, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var warnings = env.Pm.Uninstall(Claude);
                    var w = Assert.Single(warnings);
                    Assert.Contains(env.Roots.ClaudeConfig, w);
                    Assert.Contains("by hand", w);
                }
                Assert.Null(env.Pm.Find(Claude));
                Assert.NotNull(env.ClaudeJson["mcpServers"]["unify3d"]); // left, as the warning says
            }
        }

        [Fact]
        public void Install_without_the_app_or_with_a_foreign_entry_installs_nothing()
        {
            using (var env = new Env(claude: false, codex: false))
            {
                var ex = Assert.Throws<ClientConfigException>(() => env.Pm.Install(Claude));
                Assert.Equal("Claude Desktop isn't installed on this PC. Get it from claude.ai/download, open it once, then try again.", ex.Message);
                ex = Assert.Throws<ClientConfigException>(() => env.Pm.Install(Codex));
                Assert.Equal("Codex isn't installed on this PC (no .codex folder). Install Codex and start it once, then try again.", ex.Message);
                Assert.Null(env.Pm.Find(Claude));
                Assert.Null(env.Pm.Find(Codex));

                // Discover shows why, and that disables Install.
                var listed = env.Pm.PluginsJson().OfType<JObject>().Single(x => (string)x["id"] == Claude);
                Assert.Equal("Claude Desktop isn't installed on this PC. Get it from claude.ai/download, open it once, then try again.", (string)listed["error"]);
                Assert.False((bool)listed["link"]["found"]);
                Assert.False((bool)listed["trusted"]);
            }
            using (var env = new Env())
            {
                var foreign = "{\n  \"mcpServers\": {\n    \"unify3d\": {\n      \"command\": \"node\"\n    }\n  }\n}";
                ClientRoots.Write(env.Roots.ClaudeConfig, foreign);
                var ex = Assert.Throws<ClientConfigException>(() => env.Pm.Install(Claude));
                Assert.Equal(ClaudeDesktopConfig.ConflictText, ex.Message);
                Assert.Null(env.Pm.Find(Claude));
                Assert.Equal(foreign, File.ReadAllText(env.Roots.ClaudeConfig));
            }
        }

        [Fact]
        public void The_agent_cant_turn_a_link_plugin_on_or_off()
        {
            using (var env = new Env())
            {
                env.Install(Claude);
                var ex = Assert.Throws<InvalidOperationException>(() => env.Pm.Enable(Claude, false, fromAgent: true));
                Assert.Equal("Claude MCP changes Claude Desktop's settings, so only the user can turn it on or off in Plugins.", ex.Message);
                Assert.Throws<InvalidOperationException>(() => env.Pm.Enable(Claude, true, fromAgent: true));
                Assert.True(env.Pm.Find(Claude).Enabled);
                Assert.NotNull(env.ClaudeJson["mcpServers"]["unify3d"]);
            }
        }

        [Fact]
        public void RegisterMcpIn_outside_the_built_in_marketplace_is_an_ordinary_plugin()
        {
            using (var env = new Env())
            {
                env.Plugins.AddPlugin("sneaky", entryExtra: new JObject { ["unify3d"] = new JObject { ["registerMcpIn"] = "claude-desktop" } });
                env.Pm.AddMarketplace(env.Plugins.Market);
                var r = env.Pm.Install("sneaky@test-market");
                Assert.True(r.Ok);
                Assert.Null(r.NeedsTrust);
                Assert.Contains(ClientLinks.NotHonoured, r.Warnings);
                Assert.False(File.Exists(env.Roots.ClaudeConfig));
                var json = env.Pm.PluginJson(env.Pm.Find("sneaky@test-market"));
                Assert.Null(json["link"]);
                Assert.Contains(ClientLinks.NotHonoured, json["notes"].Values<string>());
                env.Pm.Enable("sneaky@test-market", false);
                env.Pm.Uninstall("sneaky@test-market");
                Assert.False(File.Exists(env.Roots.ClaudeConfig));
            }
        }

        [Fact]
        public void The_status_notes_missing_restart_connected_and_repair()
        {
            using (var env = new Env())
            {
                env.Install(Claude);
                string Note() => (string)env.Pm.PluginJson(env.Pm.Find(Claude))["link"]["note"];
                Assert.StartsWith("Added to Claude Desktop.", Note());

                // The add-in writes link-seen.json when Claude Desktop connects.
                var seen = Path.Combine(env.Pm.DataRoot, "claude-mcp", "link-seen.json");
                void Seen(DateTime t) => ClientRoots.Write(seen, new JObject { ["lastSeen"] = t.ToString("o"), ["exe"] = "0.2.0", ["clientInfo"] = "claude-ai 3.6556.2" }.ToString());
                var seenAt = DateTime.UtcNow.AddSeconds(30);
                Seen(seenAt);
                Assert.Equal("Connected: Claude Desktop last used it just now.", Note());
                var status = env.Pm.Links.Status("claude-desktop", "claude-mcp", true, seenAt.AddMinutes(7).AddSeconds(5));
                Assert.Equal("Connected: Claude Desktop last used it 7 min ago.", (string)status["note"]);
                Seen(DateTime.UtcNow.AddHours(-1));

                // Claude Desktop replaced the file: the entry is gone; Repair puts it back.
                ClientRoots.Write(env.Roots.ClaudeConfig, "{\n  \"preferences\": {}\n}");
                Assert.Equal("The \"unify3d\" entry isn't in Claude Desktop's settings any more (it was removed or the file was replaced). Press Repair to add it again.", Note());
                Assert.Equal("missing", (string)env.Pm.PluginJson(env.Pm.Find(Claude))["link"]["entry"]);
                env.Pm.RepairLink(Claude);
                Assert.NotNull(env.ClaudeJson["mcpServers"]["unify3d"]);
                Assert.NotNull(env.ClaudeJson["preferences"]);
                Assert.StartsWith("Added to Claude Desktop.", Note()); // written after it was last seen

                // An update refreshes but never re-adds; the start-up auto-update never writes.
                ClientRoots.Write(env.Roots.ClaudeConfig, "{}");
                var u = env.Pm.Update(Claude);
                Assert.True(u.Ok);
                Assert.Equal("{}", File.ReadAllText(env.Roots.ClaudeConfig));
                Assert.True(env.Pm.IsTrusted(env.Pm.Find(Claude)), "an update keeps the approval");
            }
        }

        [Fact]
        public void Codex_install_appends_the_block_and_uninstall_restores_the_bytes()
        {
            using (var env = new Env())
            {
                ClientRoots.Write(env.Roots.CodexConfig, CodexConfigTests.Realistic);
                var ask = env.Pm.Install(Codex);
                Assert.Contains($"Codex: adds the MCP server \"unify3d\" to {env.Roots.CodexConfig}, which runs {env.Exe} --client codex", ask.NeedsTrust["links"].Values<string>());
                Assert.True(env.Pm.Install(Codex, ask.Hash).Ok);
                Assert.Equal(CodexConfigTests.Realistic + "\n" + CodexConfig.Block(env.Exe, "\n"), File.ReadAllText(env.Roots.CodexConfig));
                Assert.Equal("Added to Codex. Restart Codex (or start a new CLI session) to load it.", (string)env.Pm.PluginJson(env.Pm.Find(Codex))["link"]["note"]);
                Assert.Empty(env.Pm.Uninstall(Codex));
                Assert.Equal(CodexConfigTests.Realistic, File.ReadAllText(env.Roots.CodexConfig));
                Assert.NotEmpty(Directory.GetFiles(Path.Combine(env.Pm.DataRoot, "codex-mcp", "backups"), "config.toml-*"));
            }
        }

        // ---------------------------------------------------------------- C6: tests can't reach the real configs

        [Fact]
        public void Under_SWAGENT_DATA_DIR_the_default_locator_and_links_stay_in_the_test_root()
        {
            var root = Paths.TestRoot;
            Assert.False(string.IsNullOrEmpty(root), "tests run with SWAGENT_DATA_DIR (test.runsettings)");
            bool Under(string p) => p != null && Path.GetFullPath(p).StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
            var loc = ClientLocator.Default;
            Assert.True(Under(loc.AppData) && Under(loc.LocalAppData) && Under(loc.UserProfile), "every client root is under the test root");
            Assert.Null(loc.CodexHomeEnv);
            Assert.False(loc.IsRunning("claude-desktop"));
            var claude = loc.LocateClaude();
            Assert.True(claude.ConfigPath == null || Under(claude.ConfigPath));
            var codex = loc.LocateCodex();
            Assert.True(codex.ConfigPath == null || Under(codex.ConfigPath));
            Assert.True(Under(Paths.LocalMcpDir));
            var pm = new PluginManager(Path.Combine(root, "guard-plugins"), Path.Combine(root, "guard-builtin"));
            Assert.True(Under(pm.Links.Locator.AppData) && Under(pm.Links.Locator.UserProfile) && Under(pm.Links.Exe));
        }

        [Fact]
        public void No_test_names_the_real_client_folders()
        {
            var tests = Path.Combine(RepoRoot(), "tests", "SwAgent.Tests");
            var self = Path.GetFullPath(Path.Combine(tests, "Plugins", "ClientLinkPluginTests.cs"));
            var real = new Regex(@"(SpecialFolder\.(ApplicationData|LocalApplicationData|UserProfile)|GetEnvironmentVariable\(""(APPDATA|LOCALAPPDATA|USERPROFILE|HOME)""\)).*(""Claude""|\.codex)|%APPDATA%\\+Claude|~[\\/]\.codex|GetEnvironmentVariable\(""CODEX_HOME""\)");
            var hits = Directory.GetFiles(tests, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                .Where(f => !string.Equals(Path.GetFullPath(f), self, StringComparison.OrdinalIgnoreCase))
                .SelectMany(f => File.ReadAllLines(f).Select((l, i) => (f, i, l)))
                .Where(x => real.IsMatch(x.l))
                .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}")
                .ToList();
            Assert.Empty(hits);
        }
    }
}
