using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SwAgent.Plugins;
using SwAgent.Plugins.Hooks;
using Xunit;
using static SwAgent.Tests.Plugins.RuntimeTestKit;

namespace SwAgent.Tests.Plugins
{
    /// <summary>
    /// A plugin updated while its background hook runs (agent-bridge's listener, started by SessionStart or Stop) gets
    /// that hook from the new version: the old one is stopped and SessionStart runs again ("resume"). Without this the
    /// agent showed as listening but never heard a message after an update at start-up.
    /// </summary>
    [Collection("Plugins")]
    public class HookRearmTests
    {
        private static PluginRuntime Listener(string pids, string trust)
        {
            var p = Plugin("listener", Hooks(HookEvents.SessionStart, Group(null, Cmd(Hook("sleep", pids), async: true, timeout: 600))));
            p.TrustHash = trust; // a new hash or folder is what an update looks like to the runtime
            return p;
        }

        [NodeFact]
        public async Task An_updated_plugin_starts_its_session_hooks_again()
        {
            var dir = TempDir();
            var pids = Path.Combine(dir, "pids.txt");
            var source = new FixedSource(new[] { Listener(pids, "v1") });
            using (var host = new RuntimeHost(source, Session.ForTests(Guid.NewGuid().ToString()), null) { TerminateJobOnShutdown = false, McpJsonPath = Path.Combine(dir, "mcp.json") })
            {
                host.Start();
                await host.SyncAsync();
                host.Info(); // the panel is ready: SessionStart (startup)
                Assert.True(await Until(() => File.Exists(pids) && File.ReadAllText(pids).Contains(" "), TimeSpan.FromSeconds(30)));
                var first = File.ReadAllText(pids).Split(' ').Select(int.Parse).ToArray();
                File.Delete(pids);

                source.Set(new[] { Listener(pids, "v2") }); // updated
                Assert.True(await Until(() => !Alive(first[0]), TimeSpan.FromSeconds(15)), "the old version's listener stops");
                Assert.True(await Until(() => File.Exists(pids) && File.ReadAllText(pids).Contains(" "), TimeSpan.FromSeconds(30)), "the new version's listener starts");
                var second = File.ReadAllText(pids).Split(' ').Select(int.Parse).ToArray();
                Assert.NotEqual(first[0], second[0]);
                Assert.True(Alive(second[0]));
                Assert.Equal(1, host.Hooks.BackgroundCount);

                // Disabled: stopped, not started again.
                File.Delete(pids);
                source.Set(new PluginRuntime[0]);
                Assert.True(await Until(() => !Alive(second[0]), TimeSpan.FromSeconds(15)));
                await Task.Delay(1500);
                Assert.False(File.Exists(pids));
            }
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
