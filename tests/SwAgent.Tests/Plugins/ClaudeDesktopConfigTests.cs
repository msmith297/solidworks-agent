using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwAgent.Infrastructure;
using SwAgent.Plugins;
using Xunit;

namespace SwAgent.Tests.Plugins
{
    /// <summary>
    /// Fake client folders under the test data folder (SWAGENT_DATA_DIR): AppData, LocalAppData and a home folder that stand in
    /// for the user's. Tests never reach the user's real Claude Desktop or Codex folders.
    /// </summary>
    internal sealed class ClientRoots : IDisposable
    {
        public readonly string Dir;
        public string AppData => Path.Combine(Dir, "AppData");
        public string LocalAppData => Path.Combine(Dir, "LocalAppData");
        public string Home => Path.Combine(Dir, "Home");
        public string ClaudeDir => Path.Combine(AppData, "Claude");
        public string ClaudeConfig => Path.Combine(ClaudeDir, "claude_desktop_config.json");
        public string CodexHome => Path.Combine(Home, ".codex");
        public string CodexConfig => Path.Combine(CodexHome, "config.toml");

        public ClientRoots()
        {
            Dir = Path.Combine(Paths.LocalDir, "client-tests", Guid.NewGuid().ToString("N").Substring(0, 10));
            Directory.CreateDirectory(AppData);
            Directory.CreateDirectory(LocalAppData);
            Directory.CreateDirectory(Home);
        }

        public ClientLocator Locator(string codexHome = null) => new ClientLocator(AppData, LocalAppData, Home, codexHome);

        public void Dispose() => Fetcher.TryDelete(Dir);

        public static void Write(string path, string text, bool bom = false)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var body = new UTF8Encoding(false).GetBytes(text);
            File.WriteAllBytes(path, bom ? new byte[] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray() : body);
        }
    }

    /// <summary>docs/plugins/local-mcp.md C2: mcpServers.unify3d in claude_desktop_config.json, on temp copies.</summary>
    [Collection("Plugins")] // ClientConfigFile.BeforeReplace is shared
    public class ClaudeDesktopConfigTests
    {
        private const string Exe = @"C:\Users\someone\AppData\Local\SwAgent\local-mcp\Unify3D.LocalMcp.exe";

        /// <summary>Shaped like the live file: the app's own big preferences object, ISO dates, decimals, unicode, another server with env.</summary>
        internal const string Realistic = "{\n" +
            "  \"preferences\": {\n" +
            "    \"quickEntryShortcut\": \"Ctrl+Alt+Space\",\n" +
            "    \"menuBarEnabled\": false,\n" +
            "    \"lastWindowState\": {\n" +
            "      \"x\": 120,\n" +
            "      \"y\": 80.5,\n" +
            "      \"scale\": 1.50\n" +
            "    },\n" +
            "    \"updatedAt\": \"2026-10-26T15:20:31.123Z\",\n" +
            "    \"big\": 12345678901234567890,\n" +
            "    \"name\": \"Zo\u00eb \u2713 \u65e5\u672c\",\n" +
            "    \"list\": [\n" +
            "      1,\n" +
            "      2.0,\n" +
            "      \"x\",\n" +
            "      null,\n" +
            "      true\n" +
            "    ],\n" +
            "    \"empty\": {},\n" +
            "    \"none\": []\n" +
            "  },\n" +
            "  \"mcpServers\": {\n" +
            "    \"other\": {\n" +
            "      \"command\": \"node\",\n" +
            "      \"args\": [\n" +
            "        \"C:\\\\tools\\\\server.js\"\n" +
            "      ],\n" +
            "      \"env\": {\n" +
            "        \"API_KEY\": \"test-value-not-a-secret\"\n" +
            "      }\n" +
            "    }\n" +
            "  }\n" +
            "}";

        private static JObject Parse(string text)
        {
            using (var r = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Decimal })
                return JObject.Load(r);
        }

        private static bool Edit(string path, Func<string, string> change, string backups) =>
            ClientConfigFile.Edit(path, change, ClaudeDesktopConfig.Check, backups, ClaudeDesktopConfig.Label);

        private static string Add(string path, string backups)
        {
            Edit(path, t => ClaudeDesktopConfig.Add(t, Exe, true), backups);
            return File.ReadAllText(path);
        }

        private static string Remove(string path, string backups)
        {
            Edit(path, t => ClaudeDesktopConfig.Remove(t, out _), backups);
            return File.ReadAllText(path);
        }

        [Fact]
        public void Add_changes_only_the_unify3d_entry_and_keeps_order_dates_and_numbers()
        {
            using (var roots = new ClientRoots())
            {
                var backups = Path.Combine(roots.Dir, "backups");
                ClientRoots.Write(roots.ClaudeConfig, Realistic);
                var after = Add(roots.ClaudeConfig, backups);

                var a = Parse(after);
                var b = Parse(Realistic);
                Assert.True(JToken.DeepEquals(ClaudeDesktopConfig.Entry(Exe), a["mcpServers"]["unify3d"]));
                Assert.Equal(new[] { "preferences", "mcpServers" }, a.Properties().Select(p => p.Name));
                Assert.Equal(new[] { "other", "unify3d" }, ((JObject)a["mcpServers"]).Properties().Select(p => p.Name));
                ((JObject)a["mcpServers"]).Remove("unify3d");
                Assert.True(JToken.DeepEquals(b, a), "everything else is unchanged");
                Assert.Contains("\"updatedAt\": \"2026-10-26T15:20:31.123Z\"", after);
                Assert.Contains("\"scale\": 1.50", after);
                Assert.Contains("12345678901234567890", after);
                Assert.Contains("Zo\u00eb \u2713 \u65e5\u672c", after);
                Assert.DoesNotContain("\r\n", after);

                // Again: nothing to do, nothing written.
                var stamp = File.GetLastWriteTimeUtc(roots.ClaudeConfig);
                Assert.False(Edit(roots.ClaudeConfig, t => ClaudeDesktopConfig.Add(t, Exe, true), backups));
                Assert.Equal(stamp, File.GetLastWriteTimeUtc(roots.ClaudeConfig));
                Assert.Equal(after, File.ReadAllText(roots.ClaudeConfig));

                // Remove: the original again, semantically (here even byte for byte: the fixture is written the same way).
                var removed = Remove(roots.ClaudeConfig, backups);
                Assert.True(JToken.DeepEquals(b, Parse(removed)));
                Assert.Equal(Realistic, removed);
            }
        }

        [Fact]
        public void A_file_without_mcpServers_round_trips_and_an_empty_or_missing_file_gets_a_new_object()
        {
            using (var roots = new ClientRoots())
            {
                var backups = Path.Combine(roots.Dir, "backups");
                const string noServers = "{\n  \"preferences\": {\n    \"sidebarMode\": \"chat\"\n  }\n}\n";
                ClientRoots.Write(roots.ClaudeConfig, noServers);
                var after = Add(roots.ClaudeConfig, backups);
                Assert.EndsWith("}\n", after);
                Assert.Equal(new[] { "preferences", "mcpServers" }, Parse(after).Properties().Select(p => p.Name));
                Assert.Equal(noServers, Remove(roots.ClaudeConfig, backups));

                // Removing from a file that never had it changes nothing.
                Assert.False(Edit(roots.ClaudeConfig, t => ClaudeDesktopConfig.Remove(t, out _), backups));

                ClientRoots.Write(roots.ClaudeConfig, "");
                Assert.True(JToken.DeepEquals(new JObject { ["mcpServers"] = new JObject { ["unify3d"] = ClaudeDesktopConfig.Entry(Exe) } }, Parse(Add(roots.ClaudeConfig, backups))));
                Assert.Equal("{}", Remove(roots.ClaudeConfig, backups));

                File.Delete(roots.ClaudeConfig);
                Assert.Equal("unify3d", Parse(Add(roots.ClaudeConfig, backups))["mcpServers"].Cast<JProperty>().Single().Name);
            }
        }

        [Fact]
        public void A_foreign_unify3d_or_invalid_json_is_refused_and_the_file_is_untouched()
        {
            using (var roots = new ClientRoots())
            {
                var backups = Path.Combine(roots.Dir, "backups");
                var foreign = "{\n  \"mcpServers\": {\n    \"unify3d\": {\n      \"command\": \"node\",\n      \"args\": [\"mine.js\"]\n    }\n  }\n}";
                ClientRoots.Write(roots.ClaudeConfig, foreign);
                var ex = Assert.Throws<ClientConfigException>(() => Edit(roots.ClaudeConfig, t => ClaudeDesktopConfig.Add(t, Exe, true), backups));
                Assert.Equal(ClaudeDesktopConfig.ConflictText, ex.Message);
                Assert.Equal(LinkEntryState.Conflict, ClaudeDesktopConfig.Inspect(foreign));
                var conflict = false;
                Assert.False(Edit(roots.ClaudeConfig, t => ClaudeDesktopConfig.Remove(t, out conflict), backups));
                Assert.True(conflict);
                Assert.Equal(foreign, File.ReadAllText(roots.ClaudeConfig));

                // The same exe for the other client is someone else's entry too.
                Assert.Equal(LinkEntryState.Conflict, ClaudeDesktopConfig.Inspect(
                    new JObject { ["mcpServers"] = new JObject { ["unify3d"] = new JObject { ["command"] = Exe, ["args"] = new JArray("--client", "codex") } } }.ToString()));

                const string broken = "{\n  \"preferences\": {\n    \"a\": 1,,\n  }\n}";
                ClientRoots.Write(roots.ClaudeConfig, broken);
                ex = Assert.Throws<ClientConfigException>(() => Edit(roots.ClaudeConfig, t => ClaudeDesktopConfig.Add(t, Exe, true), backups));
                Assert.StartsWith("Claude Desktop's settings file isn't valid JSON (line 3).", ex.Message);
                Assert.Equal(LinkEntryState.Unreadable, ClaudeDesktopConfig.Inspect(broken));
                Assert.Equal(broken, File.ReadAllText(roots.ClaudeConfig));
                Assert.False(Directory.Exists(backups), "nothing backed up when nothing was written");
                Assert.False(File.Exists(roots.ClaudeConfig + ".unify3d-tmp"));
            }
        }

        [Fact]
        public void BOM_and_CRLF_are_kept()
        {
            using (var roots = new ClientRoots())
            {
                var backups = Path.Combine(roots.Dir, "backups");
                var crlf = "{\r\n  \"preferences\": {\r\n    \"a\": true\r\n  }\r\n}\r\n";
                ClientRoots.Write(roots.ClaudeConfig, crlf, bom: true);
                Add(roots.ClaudeConfig, backups);
                var bytes = File.ReadAllBytes(roots.ClaudeConfig);
                Assert.True(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "BOM kept");
                var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
                Assert.DoesNotContain("\n", text.Replace("\r\n", ""));
                Assert.Contains("unify3d", text);
                Remove(roots.ClaudeConfig, backups);
                Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(crlf)), File.ReadAllBytes(roots.ClaudeConfig));
            }
        }

        [Fact]
        public void A_change_by_the_app_between_read_and_replace_is_read_again_and_kept()
        {
            using (var roots = new ClientRoots())
            {
                var backups = Path.Combine(roots.Dir, "backups");
                ClientRoots.Write(roots.ClaudeConfig, Realistic);
                var calls = 0;
                ClientConfigFile.BeforeReplace = path =>
                {
                    if (calls++ > 0) return;
                    var o = Parse(File.ReadAllText(path));
                    o["preferences"]["menuBarEnabled"] = true; // "Claude Desktop" saves its settings meanwhile
                    File.WriteAllText(path, o.ToString(Formatting.Indented));
                };
                try
                {
                    Assert.True(Edit(roots.ClaudeConfig, t => ClaudeDesktopConfig.Add(t, Exe, true), backups));
                }
                finally
                {
                    ClientConfigFile.BeforeReplace = null;
                }
                Assert.Equal(2, calls);
                var o2 = Parse(File.ReadAllText(roots.ClaudeConfig));
                Assert.True((bool)o2["preferences"]["menuBarEnabled"], "the app's change survives");
                Assert.NotNull(o2["mcpServers"]["unify3d"]);
                Assert.False(File.Exists(roots.ClaudeConfig + ".unify3d-tmp"));
            }
        }

        [Fact]
        public void Each_write_backs_up_the_original_in_the_data_folder_and_keeps_five()
        {
            using (var roots = new ClientRoots())
            {
                var backups = Path.Combine(roots.Dir, "data", "claude-mcp", "backups");
                ClientRoots.Write(roots.ClaudeConfig, Realistic);
                for (var i = 0; i < 4; i++)
                {
                    Add(roots.ClaudeConfig, backups);
                    Remove(roots.ClaudeConfig, backups);
                }
                var files = Directory.GetFiles(backups);
                Assert.Equal(ClientConfigFile.KeepBackups, files.Length);
                Assert.All(files, f => Assert.StartsWith("claude_desktop_config.json-", Path.GetFileName(f)));
                Assert.Empty(Directory.GetFiles(roots.ClaudeDir).Where(f => f != roots.ClaudeConfig)); // nothing next to the client's file
            }
        }

        [Fact]
        public void The_locator_prefers_a_newer_MSIX_copy_and_explains_a_missing_app()
        {
            using (var roots = new ClientRoots())
            {
                var loc = roots.Locator();
                // Nothing at all.
                var none = loc.LocateClaude();
                Assert.False(none.Found);
                Assert.Equal("Claude Desktop isn't installed on this PC. Get it from claude.ai/download, open it once, then try again.", none.Note);

                // The package exists but was never opened.
                var package = Path.Combine(roots.LocalAppData, "Packages", "Claude_abcdefghijklm");
                Directory.CreateDirectory(package);
                Assert.Equal("Claude Desktop is installed but hasn't been opened yet. Open it once, then try again.", loc.LocateClaude().Note);

                // This PC's layout: only the AppData Claude folder (candidate A).
                ClientRoots.Write(Path.Combine(roots.ClaudeDir, "config.json"), "{}");
                File.SetLastWriteTimeUtc(Path.Combine(roots.ClaudeDir, "config.json"), DateTime.UtcNow.AddHours(-1));
                var a = loc.LocateClaude();
                Assert.True(a.Found);
                Assert.Equal(roots.ClaudeConfig, a.ConfigPath);

                // An MSIX build that keeps its data in the package, used more recently.
                var b = Path.Combine(package, "LocalCache", "Roaming", "Claude");
                ClientRoots.Write(Path.Combine(b, "config.json"), "{}");
                Assert.Equal(Path.Combine(b, "claude_desktop_config.json"), loc.LocateClaude().ConfigPath);

                // A folder that only looks like the package is ignored.
                Directory.CreateDirectory(Path.Combine(roots.LocalAppData, "Packages", "Claude_short", "LocalCache", "Roaming", "Claude"));
                Assert.Equal(Path.Combine(b, "claude_desktop_config.json"), loc.LocateClaude().ConfigPath);
            }
        }
    }
}
