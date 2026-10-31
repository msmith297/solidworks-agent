using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SwAgent.Infrastructure;
using SwAgent.Plugins;
using Xunit;

namespace SwAgent.Tests.Plugins
{
    /// <summary>Install, trust, enable, options, update and uninstall in a throw-away plugins folder (SWAGENT_DATA_DIR).</summary>
    [Collection("Plugins")]
    public class PluginManagerTests
    {
        [Fact]
        public void The_default_plugins_folder_is_under_the_test_data_folder()
        {
            Assert.Contains(Environment.GetEnvironmentVariable("SWAGENT_DATA_DIR"), Paths.PluginsDir);
            Assert.Contains(Environment.GetEnvironmentVariable("SWAGENT_DATA_DIR"), Paths.PackagesDir);
            Assert.StartsWith(Paths.RoamingDir, Paths.McpConfigFile);
            Assert.StartsWith(Paths.PluginsDir, Paths.PluginSessionFile);
            Assert.StartsWith(Paths.LocalDir, Paths.PluginTranscriptDir);
            Assert.True(File.Exists(Path.Combine(Paths.AddInDir, "builtin-plugins", ".claude-plugin", "marketplace.json")), "plugins/ ships as builtin-plugins");
            Assert.Equal("solidworks-agent", MarketplaceManifest.Load(Path.Combine(Paths.AddInDir, "builtin-plugins")).Name);
        }

        [Fact]
        public void A_plugin_without_hooks_installs_enabled_into_its_version_folder_and_uninstalls()
        {
            using (var env = new PluginTestEnv())
            {
                var src = env.AddPlugin("plain");
                PluginTestEnv.Write(Path.Combine(src, "README.md"), "# Plain\nRead me.");
                env.Pm.AddMarketplace(env.Market);
                var r = env.Pm.Install("plain@test-market");
                Assert.True(r.Ok);
                Assert.Null(r.NeedsTrust);
                Assert.Equal(Path.Combine(env.Pm.CacheDir, "plain", "1.0.0"), r.Plugin.Root);
                Assert.True(r.Plugin.Enabled);
                var json = env.Pm.PluginJson(r.Plugin);
                Assert.Equal("plain@test-market", (string)json["id"]);
                Assert.False((bool)json["trust"]["needed"]);
                Assert.True((bool)json["readOnly"]);
                Assert.True((bool)env.Pm.MarketplacesJson().Single(m => (string)m["name"] == "test-market")["plugins"][0]["installed"]);
                var installed = JObject.Parse(File.ReadAllText(env.Pm.InstalledFile));
                Assert.Equal("1.0.0", (string)installed["plugins"]["plain@test-market"]["version"]);
                Assert.Equal("./plain", (string)installed["plugins"]["plain@test-market"]["source"]["path"]);
                Assert.True((bool)installed["plugins"]["plain@test-market"]["enabled"]);

                Assert.Throws<InvalidOperationException>(() => env.Pm.Install("missing@test-market"));
                Assert.Throws<InvalidOperationException>(() => env.Pm.Install("Bad Id"));

                env.Pm.Uninstall("plain@test-market");
                Assert.False(Directory.Exists(r.Plugin.Root));
                Assert.Empty(env.Pm.Installed());
            }
        }

        [Fact]
        public void Hooks_and_mcp_servers_need_the_users_approval_of_exactly_this_hash()
        {
            using (var env = new PluginTestEnv())
            {
                var src = env.AddPlugin("hooked");
                PluginTestEnv.Write(Path.Combine(src, "hooks", "hooks.json"), PluginTestEnv.Hooks("node \"${CLAUDE_PLUGIN_ROOT}/hook.mjs\" --listen", asyncRewake: true));
                env.Pm.AddMarketplace(env.Market);

                var ask = env.Pm.Install("hooked@test-market");
                Assert.False(ask.Ok);
                Assert.NotNull(ask.Hash);
                var line = (string)Assert.Single(ask.NeedsTrust["hooks"]);
                Assert.StartsWith("Stop: node \"", line);
                Assert.Contains("hook.mjs\" --listen", line);
                Assert.Empty(env.Pm.Installed()); // nothing installed until approved

                Assert.False(env.Pm.Install("hooked@test-market", "0000").Ok); // a wrong hash is no approval
                var ok = env.Pm.Install("hooked@test-market", ask.Hash);
                Assert.True(ok.Ok);
                var rt = Assert.Single(env.Pm.Runnable());
                Assert.Equal("hooked@test-market", rt.Id);
                Assert.Equal(ask.Hash, rt.TrustHash);
                Assert.Equal("node \"" + ok.Plugin.Root + "/hook.mjs\" --listen", rt.Expand((string)rt.Hooks["Stop"][0]["hooks"][0]["command"]));

                // disabled: nothing runs; enabling again needs no new approval (same hash)
                env.Pm.Enable("hooked@test-market", false);
                Assert.Empty(env.Pm.Runnable());
                Assert.True(env.Pm.Enable("hooked@test-market", true).Ok);
                Assert.Single(env.Pm.Runnable());

                // the settings switch hooks off without touching trust
                PluginSettings.Apply(new JObject { ["pluginHooks"] = false });
                Assert.Empty(env.Pm.Runnable().Single().Hooks.Properties());
                PluginSettings.Apply(new JObject());
                Assert.NotEmpty(env.Pm.Runnable().Single().Hooks.Properties());
            }
        }

        [Fact]
        public void Node_18_or_newer_is_checked_for_node_hooks()
        {
            using (var env = new PluginTestEnv())
            {
                var src = env.AddPlugin("noded");
                PluginTestEnv.WriteJson(Path.Combine(src, ".mcp.json"), new JObject { ["mcpServers"] = new JObject { ["s"] = new JObject { ["command"] = "node", ["args"] = new JArray("s.mjs") } } });
                env.Pm.AddMarketplace(env.Market);
                ExecutableResolver.NodeVersionOverride = () => null;
                Assert.Contains("nodejs.org", Assert.Throws<InvalidOperationException>(() => env.Pm.Install("noded@test-market")).Message);
                ExecutableResolver.NodeVersionOverride = () => new Version(16, 20, 0);
                Assert.Contains("18 or newer", Assert.Throws<InvalidOperationException>(() => env.Pm.Install("noded@test-market")).Message);
                ExecutableResolver.NodeVersionOverride = () => new Version(18, 0, 0);
                Assert.NotNull(env.Pm.Install("noded@test-market").NeedsTrust);
            }
        }

        [Fact]
        public void Agent_bridge_installs_with_its_overlay_its_entry_env_and_without_the_hidden_skills()
        {
            using (var env = new PluginTestEnv("solidworks-agent-test"))
            {
                // The built-in marketplace's entry for agent-bridge (design critique 3 and 24), pointing at the fixture.
                Fetcher.CopyDirectory(Path.Combine(PluginTestEnv.AgentBridgeFixture, "plugin"), Path.Combine(env.Market, "agent-bridge"));
                env.AddPlugin("agent-bridge-solidworks", entryExtra: null);
                var file = Path.Combine(env.Market, ".claude-plugin", "marketplace.json");
                var market = JObject.Parse(File.ReadAllText(file));
                ((JArray)market["plugins"]).Insert(0, new JObject
                {
                    ["name"] = "agent-bridge",
                    ["source"] = "./agent-bridge",
                    ["solidworksAgent"] = new JObject
                    {
                        ["env"] = new JObject { ["AGENT_BRIDGE_NAME"] = "SolidWorks Agent - ${SOLIDWORKS_AGENT_DEVICE}", ["AGENT_BRIDGE_OWNER_PID"] = "${SOLIDWORKS_AGENT_PID}" },
                        ["installWith"] = new JArray("agent-bridge-solidworks"),
                        ["hideSkills"] = new JArray("revive-session", "agent-bridge-plugin-update", "server-setup"),
                    },
                });
                PluginTestEnv.WriteJson(file, market);
                env.Pm.AddMarketplace(env.Market);

                var ask = env.Pm.Install("agent-bridge@solidworks-agent-test");
                Assert.Equal(6, ask.NeedsTrust["hooks"].Count());
                Assert.Contains("client/mcp.mjs", (string)Assert.Single(ask.NeedsTrust["mcp"]));
                var r = env.Pm.Install("agent-bridge@solidworks-agent-test", ask.Hash);
                Assert.True(r.Ok);
                Assert.Equal(new[] { "agent-bridge-solidworks@solidworks-agent-test" }, r.AlsoInstalled);
                Assert.NotNull(env.Pm.Find("agent-bridge-solidworks"));
                // no version anywhere and no git: a 12-hex content hash names the folder
                Assert.Matches("^[0-9a-f]{12}$", r.Plugin.Record.Version);

                var rt = env.Pm.Runnable().Single(x => x.Name == "agent-bridge");
                Assert.Equal("SolidWorks Agent - " + Environment.MachineName, rt.Env["AGENT_BRIDGE_NAME"]);
                Assert.Equal(Process.GetCurrentProcess().Id.ToString(), rt.Env["AGENT_BRIDGE_OWNER_PID"]);
                Assert.Equal(r.Plugin.Root, rt.Env["CLAUDE_PLUGIN_ROOT"]);
                Assert.Equal("1", rt.Env["SOLIDWORKS_AGENT"]);
                Assert.True(Directory.Exists(rt.DataDir));
                Assert.Equal(Path.Combine(env.Pm.DataRoot, "agent-bridge"), rt.DataDir);
                Assert.Equal(r.Plugin.Root + "/client/mcp.mjs", rt.Expand((string)rt.McpServers["agent-bridge"]["args"][0]));

                Assert.Equal(new[] { "agent-bridge:connect", "agent-bridge:join" }, SkillIndex.All(env.Pm).Select(s => s.Id).ToArray());
                var notes = env.Pm.PluginJson(r.Plugin)["notes"];
                Assert.Contains("PermissionRequest", (string)Assert.Single(notes));
            }
        }

        [Fact]
        public void OpenAI_keys_expand_to_nothing_but_a_plugin_may_give_its_server_its_own_OPENAI_API_KEY()
        {
            var saved = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-user-openai-key-not-real");
            try
            {
                using (var env = new PluginTestEnv())
                {
                    env.AddPlugin("gpt-tools", new JObject
                    {
                        ["name"] = "gpt-tools",
                        ["version"] = "1.0.0",
                        ["userConfig"] = new JObject { ["k"] = new JObject { ["type"] = "string", ["sensitive"] = true } },
                        ["mcpServers"] = new JObject
                        {
                            ["gpt"] = new JObject
                            {
                                ["command"] = "gpt.exe",
                                ["args"] = new JArray("--org", "${OPENAI_ORG_ID}"),
                                ["env"] = new JObject { ["OPENAI_API_KEY"] = "${user_config.k}", ["LEAK"] = "${OPENAI_API_KEY}", ["ANTHROPIC_API_KEY"] = "x" },
                            },
                        },
                    });
                    env.Pm.AddMarketplace(env.Market);
                    var ask = env.Pm.Install("gpt-tools@test-market");
                    env.Pm.Install("gpt-tools@test-market", ask.Hash);
                    env.Pm.SetConfig("gpt-tools@test-market", new JObject { ["k"] = "plugin-own-key-value" });

                    var rt = env.Pm.Runnable().Single();
                    var server = rt.McpServers["gpt"];
                    Assert.Equal("plugin-own-key-value", (string)server["env"]["OPENAI_API_KEY"]); // the plugin's own secret
                    Assert.Equal("", (string)server["env"]["LEAK"]); // never the user's
                    Assert.Null(server["env"]["ANTHROPIC_API_KEY"]); // still blocked
                    Assert.Equal("", rt.Expand("${OPENAI_API_KEY}"));
                    Assert.Equal("", rt.Expand("${openai_org_id}"));
                    Assert.False(rt.Env.ContainsKey("OPENAI_API_KEY"));
                    Assert.Equal("", PluginManager.Expand("${OPENAI_API_KEY}", new System.Collections.Generic.Dictionary<string, string>(), new System.Collections.Generic.Dictionary<string, (string Value, bool Sensitive)>(), withSecrets: true));
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("OPENAI_API_KEY", saved);
            }
        }

        [Fact]
        public void Options_sensitive_ones_encrypted_and_never_on_command_lines()
        {
            using (var env = new PluginTestEnv())
            {
                env.AddPlugin("configured", new JObject
                {
                    ["name"] = "configured",
                    ["version"] = "1.0.0",
                    ["userConfig"] = new JObject
                    {
                        ["region"] = new JObject { ["type"] = "string", ["title"] = "Region", ["default"] = "eu" },
                        ["token"] = new JObject { ["type"] = "string", ["sensitive"] = true },
                    },
                    ["mcpServers"] = new JObject
                    {
                        ["api"] = new JObject { ["command"] = "api.exe", ["args"] = new JArray("--token", "${user_config.token}", "--region", "${user_config.region}"), ["env"] = new JObject { ["TOKEN"] = "${user_config.token}", ["LEAK"] = "${ANTHROPIC_API_KEY}" } },
                    },
                });
                env.Pm.AddMarketplace(env.Market);
                var ask = env.Pm.Install("configured@test-market");
                env.Pm.Install("configured@test-market", ask.Hash);
                env.Pm.SetConfig("configured@test-market", new JObject { ["token"] = "s3cret-value", ["region"] = "us" });
                Assert.Throws<InvalidOperationException>(() => env.Pm.SetConfig("configured@test-market", new JObject { ["nope"] = "x" }));

                Assert.DoesNotContain("s3cret-value", File.ReadAllText(env.Pm.InstalledFile));
                var secrets = Directory.GetFiles(Path.Combine(env.Pm.Root, "secrets"));
                Assert.DoesNotContain("s3cret-value", File.ReadAllText(Assert.Single(secrets)));

                var rt = env.Pm.Runnable().Single();
                Assert.Equal("s3cret-value", rt.Env["CLAUDE_PLUGIN_OPTION_TOKEN"]);
                Assert.Equal("us", rt.Env["CLAUDE_PLUGIN_OPTION_REGION"]);
                Assert.Equal("", rt.Expand("${user_config.token}")); // command lines never get the secret
                Assert.Equal("us", rt.Expand("${user_config.region}"));
                Assert.Equal("s3cret-value", (string)rt.McpServers["api"]["env"]["TOKEN"]); // env does
                Assert.Equal("", (string)rt.McpServers["api"]["env"]["LEAK"]);
                Assert.False(rt.Env.ContainsKey("ANTHROPIC_API_KEY"));
                // ${ENV} is left for the program's own environment (Session.Expand), never this process's.
                Assert.Equal("${SWAGENT_SURELY_UNSET_VAR:-fallback}", rt.Expand("${SWAGENT_SURELY_UNSET_VAR:-fallback}"));
                Assert.Equal("fallback", SwAgent.Plugins.Hooks.Session.Expand(rt, "${SWAGENT_SURELY_UNSET_VAR:-fallback}", rt.Env));

                var json = env.Pm.PluginJson(env.Pm.Find("configured"));
                Assert.True((bool)json["userConfig"]["token"]["sensitive"]);
                Assert.Equal(new JObject { ["set"] = true }, json["config"]["token"]); // the value is never sent back to the panel
                Assert.DoesNotContain("s3cret", json.ToString());
                Assert.Equal("us", (string)json["config"]["region"]);

                env.Pm.Uninstall("configured@test-market");
                Assert.Empty(Directory.GetFiles(Path.Combine(env.Pm.Root, "secrets")));
            }
        }

        [Fact]
        public void The_agent_can_enable_but_never_trust_and_a_changed_local_plugin_stops_running()
        {
            using (var env = new PluginTestEnv())
            {
                var local = Path.Combine(env.Pm.LocalDir, "mine");
                PluginTestEnv.WriteJson(Path.Combine(local, ".claude-plugin", "plugin.json"), new JObject { ["name"] = "mine", ["description"] = "x" });
                PluginTestEnv.Write(Path.Combine(local, "hooks", "hooks.json"), PluginTestEnv.Hooks("node run.mjs"));
                var p = env.Pm.Find("mine@local");
                Assert.NotNull(p);
                Assert.False(p.Enabled);

                var agent = env.Pm.Enable("mine@local", true, fromAgent: true);
                Assert.True(agent.Ok);
                Assert.NotEmpty(agent.Warnings);
                Assert.True(env.Pm.Find("mine").Enabled);
                Assert.Empty(env.Pm.Runnable()); // enabled, not trusted: nothing runs

                var panel = env.Pm.Enable("mine@local", true);
                Assert.NotNull(panel.NeedsTrust);
                Assert.True(env.Pm.Enable("mine@local", true, panel.Hash).Ok);
                Assert.Single(env.Pm.Runnable());

                // any file of a local plugin with hooks is part of what was approved (its hooks may run it)
                PluginTestEnv.Write(Path.Combine(local, "run.mjs"), "console.log('changed')");
                env.Pm.Invalidate();
                Assert.Empty(env.Pm.Runnable());
                Assert.False((bool)env.Pm.PluginJson(env.Pm.Find("mine"))["trust"]["approved"]);

                env.Pm.Uninstall("mine@local");
                Assert.False(Directory.Exists(local));
                Assert.Single(Directory.GetDirectories(Path.Combine(env.Pm.Root, "trash"))); // the user's work is kept
            }
        }

        [Fact]
        public void Updates_replace_the_version_folder_and_ask_again_when_hooks_change()
        {
            using (var env = new PluginTestEnv())
            {
                var src = env.AddPlugin("evolving");
                env.Pm.AddMarketplace(env.Market);
                var v1 = env.Pm.Install("evolving@test-market");
                Assert.True(env.Pm.Update("evolving@test-market").UpToDate);

                PluginTestEnv.WriteJson(Path.Combine(src, ".claude-plugin", "plugin.json"), new JObject { ["name"] = "evolving", ["version"] = "1.1.0" });
                var v11 = env.Pm.Update("evolving@test-market");
                Assert.True(v11.Ok);
                Assert.Equal("1.1.0", v11.Plugin.Record.Version);
                Assert.False(Directory.Exists(v1.Plugin.Root));

                // 1.2.0 adds a hook: auto-update leaves it for the user, a manual update asks
                PluginTestEnv.WriteJson(Path.Combine(src, ".claude-plugin", "plugin.json"), new JObject { ["name"] = "evolving", ["version"] = "1.2.0" });
                PluginTestEnv.Write(Path.Combine(src, "hooks", "hooks.json"), PluginTestEnv.Hooks("node new.mjs"));
                var auto = env.Pm.Update("evolving@test-market", autoUpdate: true);
                Assert.NotNull(auto.NeedsTrust);
                Assert.Equal("1.1.0", env.Pm.Find("evolving").Record.Version);
                Assert.Equal("1.2.0", (string)env.Pm.PluginJson(env.Pm.Find("evolving"))["pendingUpdate"]);
                var ask = env.Pm.Update("evolving@test-market");
                Assert.NotNull(ask.NeedsTrust);
                var done = env.Pm.Update("evolving@test-market", ask.Hash);
                Assert.True(done.Ok);
                Assert.Equal("1.2.0", done.Plugin.Record.Version);
                Assert.Single(env.Pm.Runnable());
                Assert.Null((string)env.Pm.PluginJson(env.Pm.Find("evolving"))["pendingUpdate"]);
            }
        }

        [Fact]
        public void Removing_a_marketplace_uninstalls_its_plugins_and_names_stay_unique()
        {
            using (var env = new PluginTestEnv())
            {
                env.AddPlugin("shared-name");
                env.Pm.AddMarketplace(env.Market);
                env.Pm.Install("shared-name@test-market");

                var other = Path.Combine(env.Dir, "other");
                PluginTestEnv.WriteJson(Path.Combine(other, ".claude-plugin", "marketplace.json"), new JObject
                {
                    ["name"] = "other-market",
                    ["plugins"] = new JArray(new JObject { ["name"] = "shared-name", ["source"] = "./p" }),
                });
                PluginTestEnv.WriteJson(Path.Combine(other, "p", ".claude-plugin", "plugin.json"), new JObject { ["name"] = "shared-name" });
                env.Pm.AddMarketplace(other);
                Assert.Contains("already installed", Assert.Throws<InvalidOperationException>(() => env.Pm.Install("shared-name@other-market")).Message);

                env.Pm.RemoveMarketplace("test-market");
                Assert.Empty(env.Pm.Installed());
                Assert.True(env.Pm.Install("shared-name@other-market").Ok);
            }
        }
    }
}
