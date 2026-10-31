using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SwAgent.Plugins;
using Xunit;

namespace SwAgent.Tests.Plugins
{
    /// <summary>plugin.json, hooks and MCP servers read the way Claude Code reads them; front matter of the Markdown components.</summary>
    [Collection("Plugins")]
    public class PluginManifestTests
    {
        [Fact]
        public void The_real_agent_bridge_plugin_reads_with_its_mcp_server_hooks_and_skills()
        {
            var m = PluginManifest.Load(Path.Combine(PluginTestEnv.AgentBridgeFixture, "plugin"));
            Assert.Empty(m.Findings);
            Assert.Equal("agent-bridge", m.Name);
            Assert.Null(m.Version); // no version: the cache folder uses the commit
            Assert.Equal("Agent Bridge", m.DisplayName);
            Assert.Equal("Martin Smith", m.Author);
            Assert.Contains("mcp", m.Keywords);

            var server = m.McpServers["agent-bridge"];
            Assert.Equal("node", (string)server["command"]);
            Assert.Equal("${CLAUDE_PLUGIN_ROOT}/client/mcp.mjs", (string)server["args"][0]);

            Assert.Equal(new[] { "UserPromptSubmit", "PermissionRequest", "PostToolUse", "Stop", "SessionStart" }, m.Hooks.Properties().Select(p => p.Name).ToArray());
            Assert.Equal("*", (string)m.Hooks["PostToolUse"][0]["matcher"]);
            Assert.Equal("", (string)m.Hooks["UserPromptSubmit"][0]["matcher"]);
            var stop = (JArray)m.Hooks["Stop"][0]["hooks"];
            Assert.Equal(2, stop.Count);
            Assert.Equal("node \"${CLAUDE_PLUGIN_ROOT}/client/hook.mjs\"", (string)stop[0]["command"]);
            Assert.Equal(30, (double)stop[0]["timeout"]);
            Assert.Null(stop[0]["async"]);
            Assert.True((bool)stop[1]["async"]);
            Assert.True((bool)stop[1]["asyncRewake"]);
            Assert.Equal("agent-bridge:", (string)stop[1]["rewakeMessage"]);
            Assert.Equal(604800, (double)stop[1]["timeout"]);
            Assert.True((bool)m.Hooks["SessionStart"][0]["hooks"][0]["asyncRewake"]);

            Assert.True(m.HasRuntime);
            Assert.Equal(6, m.HookCommands().Count());
            Assert.Matches("^[0-9a-f]{64}$", m.TrustHash());
            Assert.Equal(m.TrustHash(), PluginManifest.Load(Path.Combine(PluginTestEnv.AgentBridgeFixture, "plugin")).TrustHash());

            var skills = SkillIndex.For(m);
            Assert.Equal(new[] { "agent-bridge-plugin-update", "connect", "join", "revive-session", "server-setup" }, skills.Select(s => s.Name).ToArray());
            Assert.StartsWith("Connect this computer", skills.Single(s => s.Name == "connect").Description);
            // the marketplace entry can hide the Claude Code-only ones
            Assert.Equal(3, SkillIndex.For(m, new[] { "revive-session", "agent-bridge-plugin-update" }).Count);
        }

        [Fact]
        public void The_real_agent_bridge_marketplace_lists_its_plugin_folder()
        {
            var market = MarketplaceManifest.Load(PluginTestEnv.AgentBridgeFixture);
            Assert.Equal("agent-bridge", market.Name);
            Assert.Equal("Martin Smith", market.Owner);
            var entry = Assert.Single(market.Plugins);
            Assert.Equal("agent-bridge", entry.Name);
            Assert.Equal(PluginSource.SourceKind.Relative, entry.Source.Kind);
            Assert.Equal("./plugin", entry.Source.Path);
        }

        [Fact]
        public void Trust_hash_changes_with_the_hooks_and_mcp_servers_only()
        {
            using (var env = new PluginTestEnv(makeInstance: false))
            {
                var dir = env.AddPlugin("hooked");
                Assert.Equal("", PluginManifest.Load(dir).TrustHash()); // nothing to run: nothing to approve
                PluginTestEnv.Write(Path.Combine(dir, "hooks", "hooks.json"), PluginTestEnv.Hooks("node a.mjs"));
                var h1 = PluginManifest.Load(dir).TrustHash();
                PluginTestEnv.Write(Path.Combine(dir, "README.md"), "docs change nothing");
                Assert.Equal(h1, PluginManifest.Load(dir).TrustHash());
                PluginTestEnv.Write(Path.Combine(dir, "hooks", "hooks.json"), PluginTestEnv.Hooks("node b.mjs"));
                var h2 = PluginManifest.Load(dir).TrustHash();
                Assert.NotEqual(h1, h2);
                PluginTestEnv.WriteJson(Path.Combine(dir, ".mcp.json"), new JObject { ["mcpServers"] = new JObject { ["x"] = new JObject { ["command"] = "node", ["args"] = new JArray("x.mjs") } } });
                Assert.NotEqual(h2, PluginManifest.Load(dir).TrustHash());
            }
        }

        [Fact]
        public void Manifest_paths_inline_hooks_mcp_files_and_problems()
        {
            using (var env = new PluginTestEnv(makeInstance: false))
            {
                var dir = env.AddPlugin("shapes", new JObject
                {
                    ["name"] = "shapes",
                    ["commands"] = new JArray("./extra-commands", "../outside"),
                    ["hooks"] = new JObject
                    {
                        ["PreToolUse"] = new JArray(new JObject { ["matcher"] = "mcp__.*", ["hooks"] = new JArray(new JObject { ["type"] = "command", ["command"] = "echo pre" }) }),
                        ["Teleport"] = new JArray(),
                    },
                    ["mcpServers"] = "./servers.json",
                    ["userConfig"] = new JObject { ["api_key"] = new JObject { ["type"] = "string", ["sensitive"] = true }, ["bad key"] = new JObject() },
                    ["solidworksAgent"] = new JObject { ["tools"] = "./my-tools", ["category"] = "Molding" },
                });
                PluginTestEnv.WriteJson(Path.Combine(dir, "servers.json"), new JObject
                {
                    ["sim"] = new JObject { ["command"] = "node", ["args"] = new JArray("${CLAUDE_PLUGIN_ROOT}/sim.mjs"), ["env"] = new JObject { ["KEY"] = "${user_config.api_key}" } },
                    ["remote"] = new JObject { ["type"] = "http", ["url"] = "https://example.invalid/mcp" },
                    ["broken"] = new JObject { ["args"] = new JArray() },
                });
                PluginTestEnv.Write(Path.Combine(dir, "extra-commands", "go.md"), "---\ndescription: Go\n---\nGo $ARGUMENTS");
                PluginTestEnv.Write(Path.Combine(dir, "commands", "sub", "deep.md"), "Deep command body");
                PluginTestEnv.AddTool(dir, "ignored"); // tools/ is not its tools folder: solidworksAgent.tools is
                PluginTestEnv.Write(Path.Combine(dir, "my-tools", "cut-groove", "TOOL.md"), "---\ntitle: Cut groove\n---\nx");

                var m = PluginManifest.Load(dir);
                Assert.Equal(new[] { "mcp__.*" }, ((JArray)m.Hooks["PreToolUse"]).Select(g => (string)g["matcher"]));
                Assert.Null(m.Hooks["Teleport"]);
                Assert.Contains(m.Findings, f => f.Contains("unknown hook event \"Teleport\""));
                Assert.Contains(m.Findings, f => f.Contains("\"../outside\" points outside"));
                Assert.Equal(new[] { "sim", "remote" }, m.McpServers.Keys.ToArray());
                Assert.Contains(m.Findings, f => f.Contains("\"broken\" has no command"));
                Assert.Equal(new[] { "api_key" }, m.UserConfig.Keys.ToArray());
                Assert.Contains(m.Findings, f => f.Contains("bad key"));
                Assert.Equal(new[] { "cut-groove" }, m.Tools().Select(t => t.Name).ToArray());

                var commands = CommandIndex.For(m);
                Assert.Equal(new[] { "sub:deep", "go" }, commands.Select(c => c.Name).ToArray());
                Assert.Equal("shapes:go", commands.Single(c => c.Name == "go").Id);
                Assert.Equal("Deep command body", commands.Single(c => c.Name == "sub:deep").Description);
            }
        }

        [Fact]
        public void Front_matter_reads_plain_quoted_block_and_list_values()
        {
            var fm = FrontMatter.Parse(
                "---\n" +
                "name: reviewer\n" +
                "description: \"Reviews: \\\"carefully\\\"\"\n" +
                "argument-hint: '[file] [--fast]'\n" +
                "tools: Read, Grep, mold_calc\n" +
                "skills: [a, \"b, c\"]\n" +
                "model: sonnet\n" +
                "notes: |\n" +
                "  line one\n" +
                "    indented\n" +
                "folded: >\n" +
                "  one\n" +
                "  two\n" +
                "items:\n" +
                "  - x\n" +
                "  - 'y'\n" +
                "long: starts here\n" +
                "  and continues\n" +
                "---\n" +
                "\n" +
                "You are a reviewer.\n");
            Assert.True(fm.Present);
            Assert.Empty(fm.Errors);
            Assert.Equal("reviewer", fm["name"]);
            Assert.Equal("Reviews: \"carefully\"", fm["description"]);
            Assert.Equal("[file] [--fast]", fm["argument-hint"]);
            Assert.Equal(new[] { "Read", "Grep", "mold_calc" }, fm.List("tools"));
            Assert.Equal(new[] { "a", "b, c" }, fm.List("skills"));
            Assert.Equal("line one\n  indented", fm["notes"]);
            Assert.Equal("one two", fm["folded"]);
            Assert.Equal(new[] { "x", "y" }, fm.List("items"));
            Assert.Equal("starts here and continues", fm["long"]);
            Assert.Equal("You are a reviewer.\n", fm.Body);
            Assert.Null(fm.List("missing"));

            var open = FrontMatter.Parse("---\ndescription: x\nno end");
            Assert.False(open.Present);
            Assert.NotEmpty(open.Errors);
            var none = FrontMatter.Parse("Just a body");
            Assert.False(none.Present);
            Assert.Equal("Just a body", none.Body);
        }
    }
}
