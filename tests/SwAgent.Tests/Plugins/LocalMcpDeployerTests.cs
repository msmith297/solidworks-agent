using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using SwAgent.Plugins;
using Xunit;

namespace SwAgent.Tests.Plugins
{
    /// <summary>docs/plugins/local-mcp.md C7: the per-user copy of Unify3D.LocalMcp.exe, also while a client runs it.</summary>
    [Collection("Plugins")]
    public class LocalMcpDeployerTests
    {
        /// <summary>bin\&lt;configuration&gt;\net48\local-mcp of the add-in, built with it (SwAgent.csproj references SwAgent.LocalMcp).</summary>
        internal static string BuiltDir()
        {
            var config = AppDomain.CurrentDomain.BaseDirectory.IndexOf(@"\bin\Release\", StringComparison.OrdinalIgnoreCase) >= 0 ? "Release" : "Debug";
            var dir = Path.Combine(ClientLinkPluginTests.RepoRoot(), "src", "SwAgent", "bin", config, "net48", "local-mcp");
            Assert.True(File.Exists(Path.Combine(dir, LocalMcpDeployer.ExeName)), $"{dir} has no {LocalMcpDeployer.ExeName}: build src/SwAgent first");
            return dir;
        }

        [Fact]
        public void Ensure_copies_then_swaps_a_running_exe_aside_and_cleans_up_after_it_exits()
        {
            using (var roots = new ClientRoots())
            {
                var shipped = Path.Combine(roots.Dir, "shipped");
                Directory.CreateDirectory(shipped);
                foreach (var f in Directory.GetFiles(BuiltDir())) File.Copy(f, Path.Combine(shipped, Path.GetFileName(f)));
                var target = Path.Combine(roots.Dir, "local-mcp");
                var d = new LocalMcpDeployer(shipped, target);

                Assert.True(d.Ensure());
                Assert.Equal(Directory.GetFiles(shipped).Select(Path.GetFileName).OrderBy(x => x), Directory.GetFiles(target).Select(Path.GetFileName).OrderBy(x => x));
                var stamp = File.GetLastWriteTimeUtc(d.Exe);
                Assert.True(d.Ensure()); // nothing changed: nothing copied
                Assert.Equal(stamp, File.GetLastWriteTimeUtc(d.Exe));

                var psi = new ProcessStartInfo(d.Exe, "--client codex")
                {
                    UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                };
                psi.EnvironmentVariables["UNIFY3D_LINK_PIPE"] = "Unify3D.Test." + Guid.NewGuid().ToString("N");
                var running = Process.Start(psi);
                try
                {
                    Assert.False(running.WaitForExit(500), "the copy runs, as under Claude Desktop");
                    using (var s = File.Open(Path.Combine(shipped, LocalMcpDeployer.ExeName), FileMode.Append)) s.Write(new byte[] { 1, 2, 3, 4 }, 0, 4); // "a new version"
                    Assert.True(d.Ensure());
                    Assert.Equal(File.ReadAllBytes(Path.Combine(shipped, LocalMcpDeployer.ExeName)), File.ReadAllBytes(d.Exe));
                    Assert.True(File.Exists(d.Exe + ".old-1"), "the running exe was renamed aside");
                }
                finally
                {
                    try
                    {
                        running.StandardInput.Close();
                        if (!running.WaitForExit(5000)) running.Kill();
                        running.WaitForExit(5000);
                    }
                    finally
                    {
                        running.Dispose();
                    }
                }
                Assert.True(d.Ensure());
                Assert.Empty(Directory.GetFiles(target, "*.old-*"));
            }
        }

        [Fact]
        public void Nothing_shipped_is_not_an_error()
        {
            using (var roots = new ClientRoots())
            {
                var d = new LocalMcpDeployer(Path.Combine(roots.Dir, "none"), Path.Combine(roots.Dir, "local-mcp"));
                Assert.False(d.Ensure());
                Assert.False(Directory.Exists(Path.Combine(roots.Dir, "local-mcp")));
            }
        }
    }
}
