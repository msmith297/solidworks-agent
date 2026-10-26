using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using SwAgent.Plugins;
using Tomlyn;
using Tomlyn.Model;
using Xunit;

namespace SwAgent.Tests.Plugins
{
    /// <summary>docs/plugins/local-mcp.md C3: [mcp_servers.unify3d] in Codex's config.toml, on temp copies; Tomlyn checks the result.</summary>
    [Collection("Plugins")]
    public class CodexConfigTests
    {
        private const string Exe = @"C:\Users\someone\AppData\Local\SwAgent\local-mcp\Unify3D.LocalMcp.exe";

        /// <summary>An anonymised file shaped like the real one: every construct Codex and agent-bridge write.</summary>
        internal const string Realistic =
            "model = \"gpt-5-codex\"\n" +
            "model_reasoning_effort = \"high\"\n" +
            "plugin_hooks = true # agent-bridge\n" +
            "notes = \"\"\"\n" +
            "a multi-line \"quoted\" note\n" +
            "with ''' inside and a [fake.header]\n" +
            "\"\"\"\n" +
            "literal_path = 'C:\\Users\\someone\\work'\n" +
            "list = [\n" +
            "  \"a\", # first\n" +
            "  \"b\",\n" +
            "]\n" +
            "inline = { a = 1, b = \"two\" }\n" +
            "\n" +
            "[projects.'C:\\Users\\someone\\Projects\\demo']\n" +
            "trust_level = \"trusted\"\n" +
            "\n" +
            "[hooks.state.\"agent-bridge@agent-bridge\"]\n" +
            "enabled = true\n" +
            "\n" +
            "[plugins.\"agent-bridge@agent-bridge\"]\n" +
            "enabled = true\n" +
            "\n" +
            "[mcp_servers.node_repl]\n" +
            "command = 'C:\\Program Files\\nodejs\\node.exe'\n" +
            "args = [\"repl.js\"]\n" +
            "\n" +
            "[mcp_servers.node_repl.env]\n" +
            "NODE_OPTIONS = \"--max-old-space-size=4096\"\n" +
            "\n" +
            "# >>> agent-bridge (managed by agent-bridge/install.mjs; edits inside are overwritten) >>>\n" +
            "[mcp_servers.agent-bridge]\n" +
            "command = \"node\"\n" +
            "args = ['C:\\Users\\someone\\.agent-bridge\\client\\mcp.mjs']\n" +
            "# <<< agent-bridge <<<\n";

        private static bool Edit(string path, Func<string, string> change, string backups) =>
            ClientConfigFile.Edit(path, change, CodexConfig.Check, backups, CodexConfig.Label);

        private static JToken J(object o)
        {
            switch (o)
            {
                case TomlTable t:
                    return new JObject(t.Select(kv => new JProperty(kv.Key, J(kv.Value))));
                case TomlTableArray ta:
                    return new JArray(ta.Select(J));
                case string s:
                    return s;
                case IEnumerable e:
                    return new JArray(e.Cast<object>().Select(J));
                case null:
                    return JValue.CreateNull();
                default:
                    return new JValue(o.ToString());
            }
        }

        private static JObject Toml(string text) => (JObject)J(Tomlyn.Toml.ToModel(text));

        [Fact]
        public void Add_appends_one_block_and_remove_gives_back_the_original_bytes()
        {
            var after = CodexConfig.Add(Realistic, Exe, true);
            Assert.StartsWith(Realistic, after); // every original line byte-identical
            Assert.Equal(Realistic + "\n" + CodexConfig.Block(Exe, "\n"), after);
            CodexConfig.Check(Realistic, after);

            var a = Toml(after);
            var entry = (JObject)a["mcp_servers"]["unify3d"];
            Assert.Equal(Exe, (string)entry["command"]);
            Assert.Equal(new[] { "--client", "codex" }, entry["args"].Values<string>());
            Assert.Equal("20", (string)entry["startup_timeout_sec"]);
            Assert.Equal("120", (string)entry["tool_timeout_sec"]);
            ((JObject)a["mcp_servers"]).Remove("unify3d");
            Assert.True(JToken.DeepEquals(Toml(Realistic), a), "the parsed file is the old one plus mcp_servers.unify3d");

            Assert.Equal(after, CodexConfig.Add(after, Exe, true)); // idempotent
            Assert.Equal(LinkEntryState.Present, CodexConfig.Inspect(after));
            Assert.Equal(LinkEntryState.Missing, CodexConfig.Inspect(Realistic));

            var removed = CodexConfig.Remove(after, out var conflict);
            Assert.False(conflict);
            Assert.Equal(Realistic, removed);
            Assert.Equal(Realistic, CodexConfig.Remove(Realistic, out _));
        }

        [Fact]
        public void A_table_codex_put_between_the_markers_survives_removal()
        {
            var after = CodexConfig.Add(Realistic, Exe, true);
            var inserted = after.Replace(CodexConfig.EndMarker, "[projects.'D:\\later']\ntrust_level = \"trusted\"\n" + CodexConfig.EndMarker);
            var removed = CodexConfig.Remove(inserted, out _);
            var t = Toml(removed);
            Assert.Null(t["mcp_servers"]["unify3d"]);
            Assert.Equal("trusted", (string)t["projects"]["D:\\later"]["trust_level"]);
            Assert.Contains("[projects.'D:\\later']\ntrust_level = \"trusted\"\n", removed);
            Assert.StartsWith(Realistic, removed);

            // Our own subtables (an env Codex or the user added) go with the entry.
            var withEnv = after.Replace(CodexConfig.EndMarker, "[mcp_servers.unify3d.env]\nX = \"1\"\n" + CodexConfig.EndMarker);
            Assert.Equal(LinkEntryState.Present, CodexConfig.Inspect(withEnv));
            Assert.Equal(Realistic, CodexConfig.Remove(withEnv, out _));
        }

        [Theory]
        [InlineData("[mcp_servers.unify3d]\ncommand = \"node\"\nargs = [\"x.js\"]\n")]
        [InlineData("[mcp_servers.\"unify3d\"]\ncommand = 'C:\\x\\Unify3D.LocalMcp.exe'\nargs = [\"--client\", \"codex\"]\n")]
        [InlineData("mcp_servers.unify3d.command = \"node\"\n")]
        [InlineData("[mcp_servers]\nunify3d = { command = \"node\" }\n")]
        [InlineData("[mcp_servers]\nunify3d.command = \"node\"\n")]
        [InlineData("[mcp_servers.unify3d]\ncommand = 'C:\\x\\Unify3D.LocalMcp.exe'\nargs = [\"--client\", \"claude-desktop\"]\n")]
        public void Any_other_unify3d_is_a_conflict_and_stays(string foreign)
        {
            // A root key must come before the first table; the rest go at the end.
            var text = foreign.StartsWith("mcp_servers.") ? foreign + Realistic : Realistic + "\n" + foreign;
            Assert.Equal(LinkEntryState.Conflict, CodexConfig.Inspect(text));
            var ex = Assert.Throws<ClientConfigException>(() => CodexConfig.Add(text, Exe, true));
            Assert.Equal(CodexConfig.ConflictText, ex.Message);
            Assert.Equal(text, CodexConfig.Remove(text, out var conflict));
            Assert.True(conflict);
        }

        [Fact]
        public void A_file_that_cant_be_read_safely_is_never_changed()
        {
            using (var roots = new ClientRoots())
            {
                var broken = Realistic + "bad = \"unclosed\n";
                ClientRoots.Write(roots.CodexConfig, broken);
                Assert.Equal(LinkEntryState.Unreadable, CodexConfig.Inspect(broken));
                var ex = Assert.Throws<ClientConfigException>(() => Edit(roots.CodexConfig, t => CodexConfig.Add(t, Exe, true), Path.Combine(roots.Dir, "b")));
                Assert.Equal(CodexConfig.UnreadableText, ex.Message);
                Assert.Equal(broken, File.ReadAllText(roots.CodexConfig));
                Assert.Equal(LinkEntryState.Unreadable, CodexConfig.Inspect("x = '''never closed\n"));
                Assert.Equal(LinkEntryState.Unreadable, CodexConfig.Inspect("x = [1, 2\n"));
            }
        }

        [Fact]
        public void Empty_missing_CRLF_and_BOM_files()
        {
            using (var roots = new ClientRoots())
            {
                var backups = Path.Combine(roots.Dir, "backups");
                Assert.Equal(CodexConfig.Block(Exe, "\n"), CodexConfig.Add("", Exe, true));
                Assert.Equal("", CodexConfig.Remove(CodexConfig.Add("", Exe, true), out _));

                // Missing: created with the block.
                Directory.CreateDirectory(roots.CodexHome);
                Assert.True(Edit(roots.CodexConfig, t => CodexConfig.Add(t, Exe, true), backups));
                Assert.Equal(CodexConfig.Block(Exe, "\n"), File.ReadAllText(roots.CodexConfig));
                Assert.False(Directory.Exists(backups), "nothing to back up");

                // CRLF with a BOM: the block uses CRLF, the BOM stays, removal gives the same bytes.
                var crlf = Realistic.Replace("\n", "\r\n");
                ClientRoots.Write(roots.CodexConfig, crlf, bom: true);
                Assert.True(Edit(roots.CodexConfig, t => CodexConfig.Add(t, Exe, true), backups));
                var bytes = File.ReadAllBytes(roots.CodexConfig);
                Assert.Equal(0xEF, bytes[0]);
                var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
                Assert.EndsWith(CodexConfig.Block(Exe, "\r\n"), text);
                Assert.True(Edit(roots.CodexConfig, t => CodexConfig.Remove(t, out _), backups));
                Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(crlf)), File.ReadAllBytes(roots.CodexConfig));

                // A file without a final newline gets one before the block.
                var noNewline = "model = \"x\"";
                var added = CodexConfig.Add(noNewline, Exe, true);
                Assert.StartsWith("model = \"x\"\n\n" + CodexConfig.StartMarker, added);
                CodexConfig.Check(noNewline, added);
            }
        }

        [Fact]
        public void A_path_with_a_quote_is_written_as_a_basic_string()
        {
            const string odd = @"C:\Users\O'Brien\AppData\Local\SwAgent\local-mcp\Unify3D.LocalMcp.exe";
            var block = CodexConfig.Block(odd, "\n");
            Assert.Contains("command = \"C:\\\\Users\\\\O'Brien\\\\", block);
            Assert.Equal(odd, (string)Toml(block)["mcp_servers"]["unify3d"]["command"]);
            Assert.Contains("command = '" + Exe + "'", CodexConfig.Block(Exe, "\n"));
            Assert.Equal(Exe, (string)Toml(CodexConfig.Block(Exe, "\n"))["mcp_servers"]["unify3d"]["command"]);
        }

        [Fact]
        public void A_moved_exe_refreshes_only_the_command_line()
        {
            var after = CodexConfig.Add(Realistic, Exe, true);
            const string moved = @"D:\Profiles\someone\AppData\Local\SwAgent\local-mcp\Unify3D.LocalMcp.exe";
            var refreshed = CodexConfig.Add(after, moved, false);
            Assert.Equal(after.Replace("command = '" + Exe + "'", "command = '" + moved + "'"), refreshed);
            CodexConfig.Check(after, refreshed);
            // Not re-added when missing (an update only refreshes).
            Assert.Equal(Realistic, CodexConfig.Add(Realistic, moved, false));
        }

        [Fact]
        public void The_check_refuses_a_change_to_another_table()
        {
            var bad = Realistic.Replace("trust_level = \"trusted\"", "trust_level = \"untrusted\"");
            Assert.Throws<ClientConfigException>(() => CodexConfig.Check(Realistic, bad));
        }

        [Fact]
        public void The_locator_honours_CODEX_HOME_and_explains_a_missing_codex()
        {
            using (var roots = new ClientRoots())
            {
                var missing = roots.Locator().LocateCodex();
                Assert.False(missing.Found);
                Assert.Equal("Codex isn't installed on this PC (no .codex folder). Install Codex and start it once, then try again.", missing.Note);

                Directory.CreateDirectory(roots.CodexHome);
                Assert.Equal(roots.CodexConfig, roots.Locator().LocateCodex().ConfigPath);

                var custom = Path.Combine(roots.Dir, "codex-home");
                Assert.Equal(roots.CodexConfig, roots.Locator(custom).LocateCodex().ConfigPath); // set but missing: the default
                Directory.CreateDirectory(custom);
                Assert.Equal(Path.Combine(custom, "config.toml"), roots.Locator(custom).LocateCodex().ConfigPath);
            }
        }
    }
}
