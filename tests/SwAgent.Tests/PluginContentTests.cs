using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwAgent.Tools;
using Xunit;

namespace SwAgent.Tests
{
    /// <summary>
    /// The plugin content that ships with the add-in: the built-in "unify3d" marketplace (plugins/ in the repository), the
    /// agent-bridge-unify3d overlay and the create-plugin workflow. Schema-level checks on the files themselves, so they
    /// hold whatever the manifest parser (track A) accepts beyond them.
    /// </summary>
    public class PluginContentTests
    {
        /// <summary>Plugin and marketplace names (docs/plugins/design.md section 2).</summary>
        private static readonly Regex PluginName = new Regex("^[a-z0-9][a-z0-9-]{1,47}$");

        private static readonly Regex Semver = new Regex(@"^\d+\.\d+\.\d+([-+][0-9A-Za-z.-]+)?$");

        /// <summary>The variables the runtime expands (authoring.md section 4), besides ${user_config.*} and environment variables.</summary>
        // CLAUDE_CODE_SESSION_ID: set and expanded by Session.EnvFor (AgentBridgeSessionTests).
        private static readonly string[] Variables = { "CLAUDE_PLUGIN_ROOT", "UNIFY3D_PLUGIN_ROOT", "CLAUDE_PLUGIN_DATA", "UNIFY3D_DEVICE", "UNIFY3D_PID", "CLAUDE_CODE_SESSION_ID" };

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "plugins", ".claude-plugin", "marketplace.json"))) d = d.Parent;
            Assert.True(d != null, "plugins/.claude-plugin/marketplace.json not found above the test folder");
            return d.FullName;
        }

        private static string PluginsDir => Path.Combine(RepoRoot(), "plugins");

        /// <summary>Strict JSON: no comments, no trailing commas, no duplicate keys (Claude Code reads these files too).</summary>
        private static JObject ReadJson(string path)
        {
            Assert.True(File.Exists(path), path + " is missing");
            var text = File.ReadAllText(path);
            Assert.False(text.Length > 0 && text[0] == '﻿', path + " starts with a BOM");
            using (var reader = new JsonTextReader(new StringReader(text)))
            {
                var settings = new JsonLoadSettings { CommentHandling = CommentHandling.Load, DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error };
                var json = JObject.Load(reader, settings);
                Assert.False(json.DescendantsAndSelf().Any(t => t.Type == JTokenType.Comment), path + " has comments");
                return json;
            }
        }

        private static JObject Marketplace() => ReadJson(Path.Combine(PluginsDir, ".claude-plugin", "marketplace.json"));

        private static JObject Entry(string name) => Marketplace()["plugins"].OfType<JObject>().Single(p => (string)p["name"] == name);

        [Fact]
        public void The_built_in_marketplace_lists_agent_bridge_first_and_its_overlay()
        {
            var m = Marketplace();
            Assert.Equal("unify3d", (string)m["name"]); // the id in installed ids such as agent-bridge@unify3d
            Assert.Equal("Unify3D", (string)m["unify3d"]["displayName"]); // what the Plugins screen shows
            Assert.False(string.IsNullOrWhiteSpace((string)m["owner"]?["name"]));
            var plugins = m["plugins"].OfType<JObject>().ToList();
            Assert.Equal(m["plugins"].Count(), plugins.Count);
            Assert.Equal(new[] { "agent-bridge", "agent-bridge-unify3d", "claude-mcp", "codex-mcp" }, plugins.Select(p => (string)p["name"]));

            foreach (var p in plugins)
            {
                var name = (string)p["name"];
                Assert.Matches(PluginName, name);
                Assert.False(string.IsNullOrWhiteSpace((string)p["description"]), name + ": description");
                Assert.False(string.IsNullOrWhiteSpace((string)p["category"]), name + ": category");
                Assert.All(p["keywords"]?.Values<string>() ?? Enumerable.Empty<string>(), k => Assert.False(string.IsNullOrWhiteSpace(k)));
                AssertSource(p["source"], name);
                if (p["version"] != null) Assert.Matches(Semver, (string)p["version"]);
            }
        }

        /// <summary>One of the source kinds the design freezes (section 3, Marketplaces).</summary>
        private static void AssertSource(JToken source, string plugin)
        {
            if (source.Type == JTokenType.String)
            {
                var rel = (string)source;
                Assert.StartsWith("./", rel);
                Assert.DoesNotContain("..", rel);
                return;
            }
            var o = Assert.IsType<JObject>(source);
            switch ((string)o["source"])
            {
                case "github":
                    Assert.Matches("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", (string)o["repo"]);
                    break;
                case "git":
                case "url":
                    Assert.StartsWith("https://", (string)o["url"]);
                    break;
                case "git-subdir":
                    Assert.StartsWith("https://", (string)o["url"]);
                    var path = (string)o["path"];
                    Assert.False(string.IsNullOrWhiteSpace(path) || path.Contains("..") || Path.IsPathRooted(path), plugin + ": git-subdir path");
                    break;
                default:
                    Assert.Fail($"{plugin}: unknown source kind '{o["source"]}'");
                    break;
            }
        }

        [Fact]
        public void Agent_bridge_is_the_real_plugin_by_reference_with_the_unify3d_env()
        {
            var e = Entry("agent-bridge");
            Assert.True(JToken.DeepEquals(
                new JObject { ["source"] = "git-subdir", ["url"] = "https://github.com/msmith297/agent-bridge.git", ["path"] = "plugin" },
                e["source"]), "agent-bridge comes from its own repository, nothing vendored");
            Assert.False(Directory.Exists(Path.Combine(PluginsDir, "agent-bridge")), "no plugins/agent-bridge folder: only the marketplace entry");

            var u = (JObject)e["unify3d"];
            var env = (JObject)u["env"];
            Assert.Equal("Unify3D SolidWorks - ${UNIFY3D_DEVICE}", (string)env["AGENT_BRIDGE_NAME"]);
            Assert.Equal("${UNIFY3D_PID}", (string)env["AGENT_BRIDGE_OWNER_PID"]);
            // One connection per SolidWorks: the PC's session id (agent-bridge keeps one peer and chat for it), always listening.
            Assert.Equal("${CLAUDE_CODE_SESSION_ID}", (string)env["AGENT_BRIDGE_SESSION_ID"]);
            Assert.Equal("always", (string)env["AGENT_BRIDGE_LISTEN"]);
            // Never point agent-bridge at another hub or identity from here; never pass a key.
            Assert.All(env.Properties(), p =>
            {
                Assert.Matches("^[A-Z_][A-Z0-9_]*$", p.Name);
                Assert.Equal(JTokenType.String, p.Value.Type);
                Assert.DoesNotContain(p.Name, new[] { "AGENT_BRIDGE_URL", "AGENT_BRIDGE_TOKEN", "AGENT_BRIDGE_HOME", "ANTHROPIC_API_KEY", "CLAUDE_CODE_SESSION_ID" });
            });

            // The overlay is installed with it, and is in the same marketplace.
            var names = new HashSet<string>(Marketplace()["plugins"].Select(p => (string)p["name"]));
            Assert.Equal(new[] { "agent-bridge-unify3d" }, u["installWith"].Values<string>());
            Assert.All(u["installWith"].Values<string>(), n => Assert.Contains(n, names));

            // Its Claude Code-only skills are hidden (critique 24).
            Assert.Equal(new[] { "revive-session", "agent-bridge-plugin-update", "server-setup" }, u["hideSkills"].Values<string>());
            Assert.All(u["hideSkills"].Values<string>(), s => Assert.Matches(PluginName, s));
        }

        [Fact]
        public void Every_placeholder_in_the_marketplace_is_one_the_runtime_expands()
        {
            var text = File.ReadAllText(Path.Combine(PluginsDir, ".claude-plugin", "marketplace.json"));
            var used = Regex.Matches(text, @"\$\{([^}]*)\}").Cast<Match>().Select(x => x.Groups[1].Value).ToList();
            Assert.NotEmpty(used);
            Assert.All(used, v => Assert.True(Variables.Contains(v) || v.StartsWith("user_config.", StringComparison.Ordinal), "unknown variable ${" + v + "}"));
        }

        [Fact]
        public void The_overlay_plugin_parses_and_matches_its_marketplace_entry()
        {
            var entry = Entry("agent-bridge-unify3d");
            var dir = Path.GetFullPath(Path.Combine(PluginsDir, ((string)entry["source"]).Substring(2)));
            var manifest = ReadJson(Path.Combine(dir, ".claude-plugin", "plugin.json"));

            Assert.Equal("agent-bridge-unify3d", (string)manifest["name"]);
            Assert.Matches(Semver, (string)manifest["version"]);
            Assert.Equal((string)entry["version"], (string)manifest["version"]);
            Assert.False(string.IsNullOrWhiteSpace((string)manifest["displayName"]));
            Assert.False(string.IsNullOrWhiteSpace((string)manifest["description"]));
            Assert.False(string.IsNullOrWhiteSpace((string)manifest["author"]?["name"]));

            var u = (JObject)manifest["unify3d"];
            Assert.Equal((string)entry["category"], (string)u["category"]);
            var icon = (string)u["icon"];
            Assert.StartsWith("./", icon);
            AssertPng(Path.Combine(dir, icon.Substring(2)));

            // No programs of its own: no hooks, no MCP servers, so no trust prompt and no env (critique 3).
            Assert.Null(manifest["hooks"]);
            Assert.Null(manifest["mcpServers"]);
            Assert.Null(u["env"]);
            Assert.False(File.Exists(Path.Combine(dir, "hooks", "hooks.json")));
            Assert.False(File.Exists(Path.Combine(dir, ".mcp.json")));
            Assert.True(File.Exists(Path.Combine(dir, "README.md")));
        }

        [Theory]
        [InlineData("claude-mcp", "Claude MCP", "claude-desktop", "Claude Desktop")]
        [InlineData("codex-mcp", "Codex MCP", "codex", "Codex")]
        public void The_link_plugins_register_in_their_app_and_run_nothing_themselves(string name, string displayName, string client, string app)
        {
            var entry = Entry(name);
            Assert.Equal("./" + name, (string)entry["source"]);
            Assert.Equal("Connections", (string)entry["category"]);
            Assert.True(JToken.DeepEquals(new JObject { ["registerMcpIn"] = client }, entry["unify3d"]), "only registerMcpIn: the command is always Unify3D's own exe");
            Assert.Contains("\"unify3d\"", (string)entry["description"]);

            var dir = Path.Combine(PluginsDir, name);
            var manifest = ReadJson(Path.Combine(dir, ".claude-plugin", "plugin.json"));
            Assert.Equal(name, (string)manifest["name"]);
            Assert.Equal(displayName, (string)manifest["displayName"]);
            Assert.Equal((string)entry["version"], (string)manifest["version"]);
            Assert.Equal("Unify3D", (string)manifest["author"]["name"]);
            var option = manifest["userConfig"]["allowModelRead"];
            Assert.Equal("boolean", (string)option["type"]);
            Assert.True((bool)option["default"]);
            Assert.Equal($"Let {app} read the open model", (string)option["title"]);
            Assert.Equal("Connections", (string)manifest["unify3d"]["category"]);
            AssertPng(Path.Combine(dir, "icon.png"));
            Assert.True(File.Exists(Path.Combine(dir, "icon.svg")));
            Assert.True(File.Exists(Path.Combine(dir, "README.md")));

            // No components: no hooks, MCP servers, skills, commands, agents or tools of its own.
            Assert.Null(manifest["hooks"]);
            Assert.Null(manifest["mcpServers"]);
            Assert.Equal(new[] { ".claude-plugin" }, Directory.GetDirectories(dir).Select(Path.GetFileName));
            Assert.False(File.Exists(Path.Combine(dir, ".mcp.json")));
        }

        [Fact]
        public void The_overlay_has_the_unify3d_bridge_skill_and_the_bridge_command()
        {
            var dir = Path.Combine(PluginsDir, "agent-bridge-unify3d");

            var skills = Directory.GetDirectories(Path.Combine(dir, "skills"));
            Assert.Equal(new[] { "unify3d-bridge" }, skills.Select(Path.GetFileName));
            foreach (var s in skills)
            {
                var fm = FrontMatterOf(Path.Combine(s, "SKILL.md"));
                Assert.Equal(Path.GetFileName(s), fm["name"]);
                Assert.True(fm["description"].Length >= 40 && fm["description"].Length <= 1024, "a skill's description says when to use it");
            }

            var commands = Directory.GetFiles(Path.Combine(dir, "commands"), "*.md");
            Assert.Equal(new[] { "bridge" }, commands.Select(Path.GetFileNameWithoutExtension));
            foreach (var c in commands)
            {
                Assert.Matches(PluginName, Path.GetFileNameWithoutExtension(c));
                var fm = FrontMatterOf(c);
                Assert.False(string.IsNullOrWhiteSpace(fm["description"]));
                Assert.True(fm.ContainsKey("argument-hint"));
                var body = File.ReadAllText(c);
                Assert.Contains("$ARGUMENTS", body);
                Assert.DoesNotContain("!`", body); // shell lines are not run in Unify3D
            }
        }

        [Fact]
        public void The_bridge_texts_name_tools_that_exist()
        {
            var dir = Path.Combine(PluginsDir, "agent-bridge-unify3d");
            var text = File.ReadAllText(Path.Combine(dir, "skills", "unify3d-bridge", "SKILL.md")) + File.ReadAllText(Path.Combine(dir, "commands", "bridge.md"));

            // agent-bridge's MCP tools (client/mcp.mjs), under Claude Code's plugin-server name.
            Assert.Contains("mcp__plugin_agent-bridge_agent-bridge__", text);
            foreach (var t in new[] { "list_peers", "send_message", "check_inbox", "listen", "set_profile", "connect", "files" })
                Assert.Contains("`" + t + "`", text);

            // The add-in's own tools the skill tells the agent to use or to hold back on.
            var registered = new HashSet<string>(AgentAddIn.BuildTools(out _).Names);
            foreach (var t in new[]
            {
                "list_open_documents", "get_model_context", "get_feature_tree", "describe_model_history", "get_selection", "capture_views",
                "analyze_design", "execute_csharp", "rebuild", "undo", "save_working_copy", "replace_inserted_part", "checkpoint",
                "mold_bodies", "mold_record", "list_user_tools", "read_user_tool",
            })
            {
                Assert.Contains("`" + t + "`", text);
                Assert.Contains(t, registered);
            }
        }

        [Fact]
        public void Create_plugin_is_a_built_in_workflow_in_the_plugins_category()
        {
            const string name = "create-plugin";
            Assert.True(File.Exists(Path.Combine(UserTools.BuiltInRoot, name, "TOOL.md")), "builtin-tools is copied next to the add-in");
            var spec = ToolSpec.Load(name);
            Assert.NotNull(spec);
            Assert.Equal("workflow", spec.Kind);
            Assert.Equal("Plugins", spec.Category);
            Assert.True(UserTools.IsBuiltIn(name));
            Assert.Empty(spec.Inputs); // a workflow: no page

            var listed = UserTools.ListTools().Single(t => (string)t["name"] == name);
            Assert.Equal("workflow", (string)listed["kind"]);
            Assert.Equal("gear", (string)listed["icon"]);
            var svg = (string)listed["iconSvg"];
            Assert.NotNull(svg);
            // The same rules save_user_tool applies to an agent-made icon.
            Assert.StartsWith("<svg", svg.Trim());
            Assert.EndsWith("</svg>", svg.Trim());
            Assert.DoesNotMatch(new Regex(@"<script|<foreignObject|\son\w+\s*=|(href|src)\s*=\s*[""'](?!#|data:image/)", RegexOptions.IgnoreCase), svg);
            AssertPng(Path.Combine(UserTools.BuiltInRoot, name, "icon.png"));

            // The design's plugin tools (5.3). Once the add-in registers plugin tools (track A), every one it needs must exist.
            var tools = new[] { "plugin_create", "plugin_write_file", "plugin_add_tool", "plugin_validate", "plugin_enable", "plugin_package", "check_prerequisites", "plugin_try_mcp" };
            Assert.Equal(tools, spec.Requires);
            var md = File.ReadAllText(Path.Combine(UserTools.BuiltInRoot, name, "TOOL.md"));
            foreach (var t in tools.Concat(new[] { "list_plugins", "read_plugin" })) Assert.Contains("`" + t, md);
            var registered = new HashSet<string>(AgentAddIn.BuildTools(out _).Names);
            if (registered.Contains("plugin_create"))
                Assert.All(tools.Concat(new[] { "list_plugins", "read_plugin" }), t => Assert.Contains(t, registered));
            Assert.All(new[] { "list_user_tools", "read_user_tool", "edit_user_tool" }, t => Assert.Contains(t, registered));
        }

        private static Dictionary<string, string> FrontMatterOf(string path)
        {
            Assert.True(File.Exists(path), path + " is missing");
            var text = File.ReadAllText(path);
            Assert.StartsWith("---", text);
            var fm = UserTools.FrontMatter(text);
            Assert.True(fm.Count > 0, path + ": no front matter");
            return fm;
        }

        private static void AssertPng(string path)
        {
            Assert.True(File.Exists(path), path + " is missing");
            var b = File.ReadAllBytes(path);
            Assert.True(b.Length > 24 && b[0] == 0x89 && b[1] == (byte)'P' && b[2] == (byte)'N' && b[3] == (byte)'G', path + " is not a PNG");
            int Be(int i) => (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];
            Assert.Equal(128, Be(16)); // width
            Assert.Equal(128, Be(20)); // height
        }
    }
}
