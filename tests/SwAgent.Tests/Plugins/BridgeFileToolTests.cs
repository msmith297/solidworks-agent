using System;
using System.IO;
using System.Linq;
using SwAgent.Infrastructure;
using SwAgent.Plugins;
using Xunit;

namespace SwAgent.Tests.Plugins
{
    /// <summary>
    /// read_bridge_file: a peer's attachment is read without C# (so without an approval card), and nothing outside
    /// agent-bridge's inbox can be read this way.
    /// </summary>
    [Collection("Plugins")]
    public class BridgeFileToolTests
    {
        [Fact]
        public void Reads_inbox_attachments_and_refuses_everything_else()
        {
            var home = Path.Combine(Paths.LocalDir, "bridge-file-tests", Guid.NewGuid().ToString("N").Substring(0, 8));
            var previous = Environment.GetEnvironmentVariable("AGENT_BRIDGE_HOME");
            Environment.SetEnvironmentVariable("AGENT_BRIDGE_HOME", home);
            try
            {
                var file = Path.Combine(home, "inbox", "4114", "HANDOFF.md");
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllText(file, "# Handoff\nD1-D8");
                File.WriteAllText(Path.Combine(home, "secret.txt"), "not for peers");
                var tools = new PluginTools();
                string Text(SwAgent.Tools.ToolResult r) => string.Concat(r.Content.Select(c => (string)c["text"]));

                var ok = tools.ReadBridgeFile(null, new PluginTools.BridgeFileInput { Path = file });
                Assert.False(ok.IsError);
                Assert.Contains("D1-D8", Text(ok));
                Assert.False(tools.ReadBridgeFile(null, new PluginTools.BridgeFileInput { Path = @"4114\HANDOFF.md" }).IsError);

                Assert.True(tools.ReadBridgeFile(null, new PluginTools.BridgeFileInput { Path = @"..\secret.txt" }).IsError);
                Assert.True(tools.ReadBridgeFile(null, new PluginTools.BridgeFileInput { Path = Path.Combine(home, "secret.txt") }).IsError);
                Assert.True(tools.ReadBridgeFile(null, new PluginTools.BridgeFileInput { Path = Path.Combine(home, "inbox-other", "x.txt") }).IsError);
                Assert.True(tools.ReadBridgeFile(null, new PluginTools.BridgeFileInput { Path = @"C:\Windows\win.ini" }).IsError);
                Assert.Contains("read_bridge_file", Infrastructure.SwBusyGate.ModelFreeTools);
            }
            finally
            {
                Environment.SetEnvironmentVariable("AGENT_BRIDGE_HOME", previous);
                try { Directory.Delete(home, true); } catch { }
            }
        }
    }
}
