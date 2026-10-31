using System;
using System.IO;
using Newtonsoft.Json.Linq;
using SwAgent.Plugins;
using Xunit;

namespace SwAgent.Tests.Plugins
{
    /// <summary>The product was called Unify3D: what an existing PC has on disk keeps working under the new name.</summary>
    public class RenameMigrationTests : IDisposable
    {
        private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "sw-agent-rename-" + Guid.NewGuid().ToString("N"))).FullName;

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException)
            {
            }
        }

        private const string Old = @"{
  ""plugins"": {
    ""agent-bridge@unify3d"": {
      ""name"": ""agent-bridge"",
      ""marketplace"": ""unify3d"",
      ""enabled"": true,
      ""trustedHash"": ""abc"",
      ""unify3d"": {
        ""env"": { ""AGENT_BRIDGE_NAME"": ""Unify3D SolidWorks - ${UNIFY3D_DEVICE}"", ""AGENT_BRIDGE_OWNER_PID"": ""${UNIFY3D_PID}"" },
        ""installWith"": [ ""agent-bridge-unify3d"" ]
      }
    }
  },
  ""marketplaces"": {},
  ""trash"": []
}";

        [Fact]
        public void An_old_installed_file_moves_to_the_new_ids_once_and_keeps_its_approval()
        {
            var file = Path.Combine(_root, "installed.json");
            File.WriteAllText(file, Old);
            Directory.CreateDirectory(Path.Combine(_root, "data", "agent-bridge-unify3d"));

            _ = new PluginManager(_root, Path.Combine(_root, "builtin"));

            var plugin = (JObject)JObject.Parse(File.ReadAllText(file))["plugins"]["agent-bridge@solidworks-agent"];
            Assert.NotNull(plugin);
            Assert.Equal("solidworks-agent", (string)plugin["marketplace"]);
            Assert.True((bool)plugin["enabled"]);
            Assert.Equal("abc", (string)plugin["trustedHash"]);
            Assert.Equal("SolidWorks Agent - ${SOLIDWORKS_AGENT_DEVICE}", (string)plugin["solidworksAgent"]["env"]["AGENT_BRIDGE_NAME"]);
            Assert.Equal("agent-bridge-solidworks", (string)plugin["solidworksAgent"]["installWith"][0]);
            Assert.DoesNotContain("nify3", File.ReadAllText(file), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(Old, File.ReadAllText(file + ".before-rename"));
            Assert.True(Directory.Exists(Path.Combine(_root, "data", "agent-bridge-solidworks")));

            var after = File.ReadAllText(file);
            _ = new PluginManager(_root, Path.Combine(_root, "builtin"));
            Assert.Equal(after, File.ReadAllText(file));
        }

        [Fact]
        public void A_plugin_with_the_old_extension_block_still_loads_it()
        {
            var dir = Directory.CreateDirectory(Path.Combine(_root, "old-plugin", ".claude-plugin")).Parent.FullName;
            File.WriteAllText(Path.Combine(dir, ".claude-plugin", "plugin.json"), @"{ ""name"": ""old-plugin"", ""unify3d"": { ""category"": ""Molds"" } }");
            Assert.Equal("Molds", (string)PluginManifest.Load(dir).SolidWorksAgent["category"]);
        }
    }
}
