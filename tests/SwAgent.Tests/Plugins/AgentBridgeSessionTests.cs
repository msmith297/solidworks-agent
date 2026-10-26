using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SwAgent.Plugins;
using SwAgent.Plugins.Hooks;
using Xunit;

namespace SwAgent.Tests.Plugins
{
    /// <summary>
    /// One agent-bridge connection per SolidWorks: the PC's session id reaches agent-bridge as AGENT_BRIDGE_SESSION_ID
    /// (so the hub keeps one peer and chat across restarts), and a built-in entry's env applies to an existing install
    /// without waiting for a plugin update.
    /// </summary>
    [Collection("Plugins")]
    public class AgentBridgeSessionTests
    {
        private static void BuiltInMarket(PluginTestEnv env, JObject entryEnv)
        {
            var root = Path.Combine(env.Dir, "builtin");
            PluginTestEnv.WriteJson(Path.Combine(root, "envy", ".claude-plugin", "plugin.json"), new JObject { ["name"] = "envy", ["version"] = "1.0.0", ["description"] = "envy plugin" });
            PluginTestEnv.WriteJson(Path.Combine(root, ".claude-plugin", "marketplace.json"), new JObject
            {
                ["name"] = PluginManager.BuiltInMarketplace,
                ["owner"] = new JObject { ["name"] = "Tests" },
                ["plugins"] = new JArray(new JObject
                {
                    ["name"] = "envy",
                    ["source"] = "./envy",
                    ["description"] = "envy plugin",
                    ["unify3d"] = new JObject { ["env"] = entryEnv },
                }),
            });
        }

        [Fact]
        public void The_built_in_entry_env_applies_to_an_existing_install_and_carries_the_session_id()
        {
            using (var env = new PluginTestEnv())
            {
                BuiltInMarket(env, new JObject { ["AGENT_BRIDGE_NAME"] = "old" });
                var id = "envy@" + PluginManager.BuiltInMarketplace;
                var ask = env.Pm.Install(id);
                Assert.True(ask.Ok || ask.Hash != null, "install starts");
                if (!ask.Ok) Assert.True(env.Pm.Install(id, ask.Hash).Ok);
                Assert.Equal("old", env.Pm.Runnable().Single(p => p.Name == "envy").Env["AGENT_BRIDGE_NAME"]);

                // A newer add-in ships a changed entry: no update of the plugin needed.
                BuiltInMarket(env, new JObject
                {
                    ["AGENT_BRIDGE_NAME"] = "new",
                    ["AGENT_BRIDGE_SESSION_ID"] = "${CLAUDE_CODE_SESSION_ID}",
                    ["AGENT_BRIDGE_LISTEN"] = "always",
                });
                var rt = env.Pm.Runnable().Single(p => p.Name == "envy");
                Assert.Equal("new", rt.Env["AGENT_BRIDGE_NAME"]);

                // What the hooks and MCP servers get: the PC's session id, the same for every chat.
                var session = Session.ForTests(Guid.NewGuid().ToString());
                var child = session.EnvFor(rt);
                Assert.Equal(session.Id, child["AGENT_BRIDGE_SESSION_ID"]);
                Assert.Equal(session.Id, child["CLAUDE_CODE_SESSION_ID"]);
                Assert.Equal("always", child["AGENT_BRIDGE_LISTEN"]);
            }
        }
    }
}
