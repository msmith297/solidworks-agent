using System;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SwAgent.Plugins.LocalLink;
using SwAgent.Tests.Plugins;
using Xunit;

namespace SwAgent.Tests.LocalLink
{
    /// <summary>The pipe itself (docs/plugins/local-mcp.md 10.1 A3, A4): its ACL, and one SolidWorks serving at a time.</summary>
    [Collection("Plugins")]
    public class LinkServerTests
    {
        /// <summary>Answers hello and echoes requests (the policy is LinkHost's, tested in LinkHostTests).</summary>
        private sealed class Echo : ILinkHandler
        {
            public readonly string Name;

            public Echo(string name)
            {
                Name = name;
            }

            public JObject Hello(JObject p, LinkPeer peer)
            {
                peer.Client = (string)p["client"];
                return new JObject { ["server"] = Name, ["pid"] = peer.ProcessId };
            }

            public Task<JObject> HandleAsync(LinkPeer peer, string method, JObject p, CancellationToken cancel) =>
                Task.FromResult(new JObject { ["server"] = Name, ["method"] = method });
        }

        [Fact]
        public void The_pipe_allows_the_current_user_denies_network_and_nothing_else()
        {
            var name = "Unify3D.Test." + Guid.NewGuid().ToString("N");
            using (var pipe = LinkServer.Create(name))
            {
                var acl = pipe.GetAccessControl();
                var user = WindowsIdentity.GetCurrent().User;
                Assert.Equal(user, acl.GetOwner(typeof(SecurityIdentifier)));
                var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<System.IO.Pipes.PipeAccessRule>().ToList();
                Assert.Equal(2, rules.Count);
                var allow = Assert.Single(rules, r => r.AccessControlType == AccessControlType.Allow);
                Assert.Equal(user, allow.IdentityReference);
                Assert.False(allow.IsInherited);
                Assert.True(allow.PipeAccessRights.HasFlag(System.IO.Pipes.PipeAccessRights.ReadWrite));
                Assert.True(allow.PipeAccessRights.HasFlag(System.IO.Pipes.PipeAccessRights.CreateNewInstance));
                Assert.False(allow.PipeAccessRights.HasFlag(System.IO.Pipes.PipeAccessRights.ChangePermissions));
                var deny = Assert.Single(rules, r => r.AccessControlType == AccessControlType.Deny);
                Assert.Equal(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), deny.IdentityReference);
                Assert.Equal(System.IO.Pipes.PipeAccessRights.FullControl, deny.PipeAccessRights & System.IO.Pipes.PipeAccessRights.FullControl);
            }
        }

        [Fact]
        public async Task One_server_serves_and_a_second_takes_over_when_it_closes()
        {
            var pipe = "Unify3D.Test." + Guid.NewGuid().ToString("N");
            var mutex = @"Local\Unify3D.Test." + Guid.NewGuid().ToString("N");
            var first = new LinkServer(new Echo("first"), pipe, mutex);
            var second = new LinkServer(new Echo("second"), pipe, mutex);
            try
            {
                first.Start();
                Assert.True(await RuntimeTestKit.Until(() => first.Serving, TimeSpan.FromSeconds(10)));
                second.Start();
                await Task.Delay(500);
                Assert.False(second.Serving, "the mutex keeps the second SolidWorks from serving");
                using (var c = new PipeClient(pipe))
                {
                    var hello = c.Call(1, "hello", new JObject { ["client"] = "claude-desktop" });
                    Assert.Equal("first", (string)hello["result"]["server"]);
                    Assert.Equal(System.Diagnostics.Process.GetCurrentProcess().Id, (int)hello["result"]["pid"]);
                    Assert.Equal("status", (string)c.Call(2, "status", new JObject())["result"]["method"]);
                }

                first.Dispose();
                Assert.True(await RuntimeTestKit.Until(() => second.Serving, TimeSpan.FromSeconds(35)), "the second takes over");
                using (var c = new PipeClient(pipe))
                {
                    Assert.Equal("second", (string)c.Call(1, "hello", new JObject())["result"]["server"]);
                }
            }
            finally
            {
                first.Dispose();
                second.Dispose();
            }
        }

        [Fact]
        public async Task An_idle_connection_closes_and_stopping_closes_open_ones()
        {
            var pipe = "Unify3D.Test." + Guid.NewGuid().ToString("N");
            var server = new LinkServer(new Echo("s"), pipe, @"Local\Unify3D.Test." + Guid.NewGuid().ToString("N")) { ReadTimeout = TimeSpan.FromMilliseconds(500) };
            try
            {
                server.Start();
                Assert.True(await RuntimeTestKit.Until(() => server.Serving, TimeSpan.FromSeconds(10)));
                using (var c = new PipeClient(pipe))
                {
                    c.Call(1, "hello", new JObject());
                    Assert.Null(c.ReadLine(5000)); // nothing sent for 500 ms: closed
                }
                server.ReadTimeout = TimeSpan.FromSeconds(30);
                using (var c = new PipeClient(pipe))
                {
                    c.Call(1, "hello", new JObject());
                    server.Stop();
                    Assert.False(server.Serving);
                    Assert.Null(c.ReadLine(5000));
                }
            }
            finally
            {
                server.Dispose();
            }
        }
    }
}
