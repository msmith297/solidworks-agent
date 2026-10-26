using System.IO;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SwAgent.Tests.Plugins
{
    /// <summary>The Installed tab's Update button is on only when the source offers a newer version (plugins.checkUpdates).</summary>
    [Collection("Plugins")]
    public class PluginUpdateCheckTests
    {
        [Fact]
        public void A_newer_listed_version_is_an_update_and_the_same_one_is_not()
        {
            using (var env = new PluginTestEnv())
            {
                env.AddPlugin("tidy", entryExtra: new JObject { ["version"] = "1.0.0" });
                env.Pm.AddMarketplace(env.Market);
                var id = "tidy@test-market";
                var ask = env.Pm.Install(id);
                if (!ask.Ok) Assert.True(env.Pm.Install(id, ask.Hash).Ok);

                Assert.Null(env.Pm.AvailableUpdate(id));

                // The marketplace lists a newer version.
                var file = Path.Combine(env.Market, ".claude-plugin", "marketplace.json");
                var market = JObject.Parse(File.ReadAllText(file));
                market["plugins"][0]["version"] = "1.1.0";
                PluginTestEnv.WriteJson(file, market);
                env.Pm.UpdateMarketplace("test-market");
                Assert.Equal("1.1.0", env.Pm.AvailableUpdate(id));

                Assert.Null(env.Pm.AvailableUpdate("nothing@test-market"));
            }
        }
    }
}
