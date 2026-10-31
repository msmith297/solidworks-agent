using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SwAgent.Plugins;
using Xunit;

namespace SwAgent.Tests.Plugins
{
    /// <summary>docs/plugins/local-mcp.md C5: the built-in marketplace shows as "SolidWorks Agent"; ids stay "solidworks-agent".</summary>
    [Collection("Plugins")]
    public class MarketplaceDisplayNameTests
    {
        private static void BuiltIn(PluginTestEnv env, JObject extra = null)
        {
            var m = new JObject { ["name"] = "solidworks-agent", ["owner"] = new JObject { ["name"] = "SolidWorks Agent" }, ["plugins"] = new JArray() };
            if (extra != null) m.Merge(extra);
            PluginTestEnv.WriteJson(Path.Combine(env.Pm.BuiltInRoot, ".claude-plugin", "marketplace.json"), m);
        }

        [Fact]
        public void The_manifest_field_is_trimmed_and_at_most_48_characters()
        {
            MarketplaceManifest Parse(JToken v) => MarketplaceManifest.Parse(new JObject { ["name"] = "m-1", ["solidworksAgent"] = new JObject { ["displayName"] = v } }.ToString());
            Assert.Equal("Acme Tools", Parse("  Acme Tools ").DisplayName);
            Assert.Null(Parse(new string('x', 49)).DisplayName);
            Assert.Equal(new string('x', 48), Parse(new string('x', 48)).DisplayName);
            Assert.Null(Parse(" ").DisplayName);
            Assert.Null(Parse(3).DisplayName);
            Assert.Null(MarketplaceManifest.Parse("{\"name\":\"m-1\",\"solidworksAgent\":\"x\"}").DisplayName);
            Assert.Equal("m-1", Parse("Acme").Name); // the id never changes
        }

        [Fact]
        public void Marketplaces_carry_display_names_with_fallbacks()
        {
            using (var env = new PluginTestEnv())
            {
                // An older builtin-plugins copy without the field still shows "SolidWorks Agent".
                BuiltIn(env);
                var json = env.Pm.MarketplacesJson().OfType<JObject>().ToDictionary(m => (string)m["name"]);
                Assert.Equal("SolidWorks Agent", (string)json["solidworks-agent"]["displayName"]);
                Assert.Equal("Your plugins", (string)json["local"]["displayName"]);

                BuiltIn(env, new JObject { ["solidworksAgent"] = new JObject { ["displayName"] = "SolidWorks Agent Store" } });
                Assert.Equal("SolidWorks Agent Store", (string)env.Pm.MarketplacesJson().OfType<JObject>().Single(m => (string)m["name"] == "solidworks-agent")["displayName"]);

                // An added marketplace: its own display name, else its id.
                env.Pm.AddMarketplace(env.Market);
                Assert.Equal("test-market", (string)env.Pm.MarketplacesJson().OfType<JObject>().Single(m => (string)m["name"] == "test-market")["displayName"]);
                var file = Path.Combine(env.Market, ".claude-plugin", "marketplace.json");
                var market = JObject.Parse(File.ReadAllText(file));
                market["solidworksAgent"] = new JObject { ["displayName"] = "Test Market" };
                PluginTestEnv.WriteJson(file, market);
                Assert.Equal("Test Market", (string)env.Pm.MarketplacesJson().OfType<JObject>().Single(m => (string)m["name"] == "test-market")["displayName"]);
            }
        }

        [Fact]
        public void Plugins_carry_the_marketplace_display_name_and_old_ids_still_load()
        {
            using (var env = new PluginTestEnv())
            {
                BuiltIn(env, new JObject { ["solidworksAgent"] = new JObject { ["displayName"] = "SolidWorks Agent" } });
                env.AddPlugin("listed");

                // An installed.json from before the display name: agent-bridge@solidworks-agent.
                var cache = Path.Combine(env.Pm.CacheDir, "agent-bridge", "abc123");
                PluginTestEnv.WriteJson(Path.Combine(cache, ".claude-plugin", "plugin.json"), new JObject { ["name"] = "agent-bridge", ["version"] = "abc123" });
                PluginTestEnv.WriteJson(env.Pm.InstalledFile, new JObject
                {
                    ["plugins"] = new JObject
                    {
                        ["agent-bridge@solidworks-agent"] = new JObject { ["name"] = "agent-bridge", ["marketplace"] = "solidworks-agent", ["path"] = cache, ["enabled"] = true, ["version"] = "abc123" },
                    },
                });
                env.Pm.AddMarketplace(env.Market); // a Mutate: reads that file back, so the old record must survive it
                var p = env.Pm.Find("agent-bridge@solidworks-agent");
                Assert.NotNull(p);
                Assert.Equal("agent-bridge@solidworks-agent", p.Id);

                var list = env.Pm.PluginsJson().OfType<JObject>().ToDictionary(x => (string)x["id"]);
                Assert.Equal("solidworks-agent", (string)list["agent-bridge@solidworks-agent"]["marketplace"]);
                Assert.Equal("SolidWorks Agent", (string)list["agent-bridge@solidworks-agent"]["marketplaceDisplayName"]);
                Assert.Equal("test-market", (string)list["listed@test-market"]["marketplaceDisplayName"]);
                Assert.Equal("SolidWorks Agent", (string)env.Pm.PluginJson(p)["marketplaceDisplayName"]);

                Assert.Equal(("agent-bridge", "solidworks-agent"), PluginManager.ParseId("agent-bridge@solidworks-agent"));
                Assert.Equal(("agent-bridge", "solidworks-agent"), PluginManager.ParseId("Agent-Bridge@SolidWorks-Agent"));
            }
        }
    }
}
