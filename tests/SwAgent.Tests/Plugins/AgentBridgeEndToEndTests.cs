using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SwAgent.Plugins;
using SwAgent.Plugins.Hooks;
using Xunit;
using static SwAgent.Tests.Plugins.RuntimeTestKit;

namespace SwAgent.Tests.Plugins
{
    /// <summary>Runs only where node is installed and a local clone of the agent-bridge repository exists.</summary>
    public sealed class AgentBridgeRepoFactAttribute : FactAttribute
    {
        public AgentBridgeRepoFactAttribute()
        {
            if (RuntimeTestKit.Node == null) Skip = "node isn't installed.";
            else if (AgentBridgeEndToEndTests.LocalRepo == null) Skip = "No local agent-bridge clone (Claude Code's marketplace copy) on this PC; set AGENT_BRIDGE_REPO_DIR.";
        }
    }

    /// <summary>
    /// The first plugin end to end, without the network: the built-in marketplace's agent-bridge entry exactly as shipped
    /// (solidworks-agent env, installWith, hideSkills), with only its source pointed at a local clone of the agent-bridge repository
    /// (git-subdir over file://, or a folder copy when git is missing). It installs through PluginManager with the trust
    /// step, then the real runtime starts its MCP server and fires its hooks, unpaired: AGENT_BRIDGE_HOME is an empty folder
    /// and the hub URL a closed local port, so mcp.mjs must answer initialize and tools/list and hook.mjs must exit 0 silently.
    /// </summary>
    [Collection("Plugins")]
    public class AgentBridgeEndToEndTests
    {
        /// <summary>A local agent-bridge repository (plugin/client/mcp.mjs inside), or null.</summary>
        public static string LocalRepo
        {
            get
            {
                var set = Environment.GetEnvironmentVariable("AGENT_BRIDGE_REPO_DIR");
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var candidates = !string.IsNullOrEmpty(set)
                    ? new[] { set }
                    : new[] { ".claude-second", ".claude" }.Select(c => Path.Combine(home, c, "plugins", "marketplaces", "agent-bridge"));
                return candidates.FirstOrDefault(d => File.Exists(Path.Combine(d, "plugin", "client", "mcp.mjs")) && File.Exists(Path.Combine(d, "plugin", ".claude-plugin", "plugin.json")));
            }
        }

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "plugins", ".claude-plugin", "marketplace.json"))) d = d.Parent;
            return d?.FullName ?? throw new InvalidOperationException("The repository's plugins folder wasn't found above the test output.");
        }

        /// <summary>The plugin core's runnable plugins, with the test's agent-bridge sandbox added to every plugin's env.</summary>
        private sealed class Sandboxed : IPluginRuntimeSource
        {
            private readonly IPluginRuntimeSource _inner;
            private readonly IDictionary<string, string> _env;

            public Sandboxed(IPluginRuntimeSource inner, IDictionary<string, string> env)
            {
                _inner = inner;
                _env = env;
                _inner.Changed += () => Changed?.Invoke();
            }

            public IReadOnlyList<PluginRuntime> Runnable()
            {
                var list = _inner.Runnable();
                foreach (var p in list)
                    foreach (var kv in _env) p.Env[kv.Key] = kv.Value;
                return list;
            }

            public event Action Changed;
        }

        [AgentBridgeRepoFact]
        public async Task The_built_in_entry_installs_from_a_local_copy_and_its_server_and_hooks_run_unpaired()
        {
            var repo = LocalRepo;
            var home = TempDir();
            using (var env = new PluginTestEnv("bridge-test"))
            {
                ExecutableResolver.NodeVersionOverride = null; // the real node must be 18 or newer
                var useGit = Fetcher.Git != null;

                // The shipped marketplace.json, renamed, with agent-bridge's source pointed at the local clone.
                var market = JObject.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "plugins", ".claude-plugin", "marketplace.json")));
                market["name"] = "bridge-test";
                var entry = market["plugins"].OfType<JObject>().Single(p => (string)p["name"] == "agent-bridge");
                Assert.Equal("git-subdir", (string)entry["source"]["source"]);
                Assert.Equal("plugin", (string)entry["source"]["path"]);
                if (useGit)
                {
                    entry["source"]["url"] = new Uri(repo).AbsoluteUri; // file:///C:/...: a local clone, never github.com
                }
                else
                {
                    CopyDir(Path.Combine(repo, "plugin"), Path.Combine(env.Market, "agent-bridge"));
                    entry["source"] = "./agent-bridge";
                }
                PluginTestEnv.WriteJson(Path.Combine(env.Market, ".claude-plugin", "marketplace.json"), market);
                CopyDir(Path.Combine(RepoRoot(), "plugins", "agent-bridge-solidworks"), Path.Combine(env.Market, "agent-bridge-solidworks"));
                env.Pm.AddMarketplace(env.Market);

                // Discover lists it with its companion, before anything is installed.
                var listed = env.Pm.PluginsJson().OfType<JObject>().Single(p => (string)p["id"] == "agent-bridge@bridge-test");
                Assert.False((bool)listed["installed"]);
                Assert.Equal(new[] { "agent-bridge-solidworks@bridge-test" }, listed["installWith"].Select(t => (string)t));

                // Install: it runs programs, so the first call only asks; nothing is installed until the user approves.
                var ask = env.Pm.Install("agent-bridge@bridge-test");
                Assert.NotNull(ask.NeedsTrust);
                Assert.Null(env.Pm.Find("agent-bridge@bridge-test"));
                Assert.Contains(ask.NeedsTrust["mcp"], t => ((string)t).Contains("mcp.mjs"));
                Assert.Contains(ask.NeedsTrust["hooks"], t => ((string)t).Contains("hook.mjs") && ((string)t).Contains("--listen"));
                var done = env.Pm.Install("agent-bridge@bridge-test", ask.Hash);
                Assert.True(done.Ok);
                Assert.Contains("agent-bridge-solidworks@bridge-test", done.AlsoInstalled);

                // The subfolder's contents sit directly in the version folder (lib.mjs reports a hex folder as its version).
                var installed = env.Pm.Find("agent-bridge@bridge-test");
                Assert.True(File.Exists(Path.Combine(installed.Root, "client", "mcp.mjs")));
                Assert.True(File.Exists(Path.Combine(installed.Root, "agent-bridge.defaults.json")));
                if (useGit) Assert.Matches(new Regex("^[0-9a-f]{7,40}$"), Path.GetFileName(installed.Root));
                var json = env.Pm.PluginJson(installed);
                Assert.True((bool)json["installed"] && (bool)json["enabled"] && (bool)json["trusted"]);
                Assert.Contains(json["notes"], n => ((string)n).Contains("PermissionRequest"));

                // Components: the Claude Code-only skills are hidden, the overlay's skill and /bridge are there.
                var skills = SkillIndex.All(env.Pm).Select(s => s.Id).ToList();
                Assert.DoesNotContain(skills, s => s.EndsWith(":revive-session") || s.EndsWith(":agent-bridge-plugin-update") || s.EndsWith(":server-setup"));
                Assert.Contains("agent-bridge-solidworks:solidworks-bridge", skills);
                Assert.Contains(CommandIndex.All(env.Pm), c => c.Id == "agent-bridge-solidworks:bridge");

                // What the runtime gets: the entry's env, expanded.
                var rt = env.Pm.Runnable().Single(p => p.Name == "agent-bridge");
                Assert.Equal("SolidWorks Agent - " + Environment.MachineName, rt.Env["AGENT_BRIDGE_NAME"]);
                Assert.Equal(Process.GetCurrentProcess().Id.ToString(), rt.Env["AGENT_BRIDGE_OWNER_PID"]);
                Assert.True(rt.McpServers.ContainsKey("agent-bridge"));
                Assert.False(rt.Env.ContainsKey("ANTHROPIC_API_KEY"));

                // The real runtime over the plugin core, sandboxed: an empty bridge home and a closed local port as the hub.
                var source = new Sandboxed(env.Pm, new Dictionary<string, string>
                {
                    ["AGENT_BRIDGE_HOME"] = home,
                    ["AGENT_BRIDGE_URL"] = "http://127.0.0.1:9",
                    ["AGENT_BRIDGE_TOKEN"] = null,
                });
                var events = new ConcurrentQueue<string>();
                var finished = new ConcurrentQueue<(string template, int code, string stdout, string stderr)>();
                using (var host = new RuntimeHost(source, Session.ForTests(Guid.NewGuid().ToString()), (n, d) => events.Enqueue(n))
                {
                    TerminateJobOnShutdown = false, // tests share the add-in job
                    McpJsonPath = Path.Combine(TempDir(), "mcp.json"),
                })
                {
                    host.Hooks.Finished += (spec, code, o, e) => finished.Enqueue((spec.Template, code, o, e));
                    host.Start();
                    await host.SyncAsync();

                    // mcp.mjs answered initialize and tools/list, started directly by the add-in (its ppid is our pid).
                    Assert.True(await Until(() => host.Mcp.Status().OfType<JObject>().Any(s => (string)s["server"] == "agent-bridge" && (string)s["state"] != "starting"), TimeSpan.FromSeconds(60)),
                        host.Mcp.Status().ToString());
                    var status = host.Mcp.Status().OfType<JObject>().Single(s => (string)s["server"] == "agent-bridge");
                    Assert.True((string)status["state"] == "running", status.ToString());
                    Assert.True((int)status["toolCount"] > 0);
                    Assert.Equal("agent-bridge", (string)status["plugin"]);
                    var pid = host.Mcp.ProcessIdOf("agent-bridge");
                    Assert.NotNull(pid);
                    Assert.Equal(Process.GetCurrentProcess().Id, ParentOf(pid.Value));
                    var tools = host.MergeTools(new JArray()).Select(t => (string)t["name"]).ToList();
                    Assert.Contains("mcp__plugin_agent-bridge_agent-bridge__send_message", tools);
                    Assert.Contains("mcp__plugin_agent-bridge_agent-bridge__list_peers", tools);
                    Assert.All(tools, t => Assert.Matches(new Regex("^[a-zA-Z0-9_-]{1,64}$"), t));

                    // The panel's events, then SessionStart from its first session.info.
                    foreach (var ev in new[]
                             {
                                 new JObject { ["event"] = "UserPromptSubmit", ["threadId"] = "t", ["prompt"] = "hello" },
                                 new JObject
                                 {
                                     ["event"] = "PostToolUse", ["threadId"] = "t", ["tool_name"] = "mcp__plugin_agent-bridge_agent-bridge__list_peers", ["tool_input"] = new JObject(),
                                     ["tool_response"] = new JObject { ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = "no peers" }) },
                                 },
                                 new JObject { ["event"] = "PostToolUse", ["threadId"] = "t", ["tool_name"] = "get_selection", ["tool_input"] = new JObject() },
                                 new JObject { ["event"] = "Stop", ["threadId"] = "t", ["stop_hook_active"] = false },
                             })
                    {
                        var r = await host.FireAsync(ev);
                        Assert.False((bool)r["block"], r.ToString());
                        Assert.True((bool)r["continue"]);
                        Assert.Empty((JArray)r["additionalContext"]);
                        Assert.Empty((JArray)r["systemMessages"]);
                    }
                    var info = host.Info();
                    Assert.Contains("SessionStart", ((JObject)info["hookEvents"]).Properties().Select(p => p.Name));
                    Assert.Contains(info["mcpInstructions"], i => (string)i["plugin"] == "agent-bridge");

                    // UserPromptSubmit, PostToolUse (once: hook.mjs skips its own tools but still runs), PostToolUse,
                    // Stop, Stop --listen and SessionStart --listen: all exit 0 with nothing to say, nothing wakes.
                    Assert.True(await Until(() => finished.Count >= 6 && host.Hooks.BackgroundCount == 0, TimeSpan.FromSeconds(120)),
                        $"finished {finished.Count}, background {host.Hooks.BackgroundCount}");
                    Assert.All(finished, f =>
                    {
                        Assert.Equal(0, f.code);
                        Assert.Equal("", f.stdout.Trim());
                        Assert.Equal("", f.stderr.Trim());
                    });
                    Assert.Equal(2, finished.Count(f => f.template.Contains("--listen")));
                    Assert.Empty(host.Session.PendingWakes());
                    Assert.DoesNotContain("session.wake", events);
                }
            }
            // Unpaired, the hooks write nothing in the bridge home.
            Assert.Empty(Directory.GetFiles(home, "*", SearchOption.AllDirectories));
            Fetcher.TryDelete(home);
        }

        private static void CopyDir(string from, string to)
        {
            foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
                if (!dir.Substring(from.Length).Replace('\\', '/').Contains("/.git")) Directory.CreateDirectory(to + dir.Substring(from.Length));
            Directory.CreateDirectory(to);
            foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
                if (!file.Substring(from.Length).Replace('\\', '/').Contains("/.git/")) File.Copy(file, to + file.Substring(from.Length));
        }
    }
}
