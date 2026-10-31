using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Newtonsoft.Json.Linq;
using SwAgent.Plugins;
using Xunit;

namespace SwAgent.Tests.Plugins
{
    /// <summary>Runs only when git.exe is installed (local file:// repositories; nothing is fetched from the network).</summary>
    public sealed class GitFactAttribute : FactAttribute
    {
        public GitFactAttribute()
        {
            if (ExecutableResolver.Find("git") == null) Skip = "git.exe is not installed.";
        }
    }

    /// <summary>marketplace.json and every plugin source kind; fetching from folders, zips and local git repositories only.</summary>
    [Collection("Plugins")]
    public class MarketplaceTests
    {
        [Fact]
        public void Every_source_kind_parses_and_bad_entries_say_why()
        {
            var market = MarketplaceManifest.Parse(new JObject
            {
                ["name"] = "kinds",
                ["owner"] = new JObject { ["name"] = "O" },
                ["plugins"] = new JArray
                {
                    new JObject { ["name"] = "rel", ["source"] = "./plugins/rel", ["version"] = "1.2.0", ["category"] = "Tools", ["keywords"] = new JArray("a", "b") },
                    new JObject { ["name"] = "short-gh", ["source"] = "owner/repo" },
                    new JObject { ["name"] = "gh", ["source"] = new JObject { ["source"] = "github", ["repo"] = "owner/repo", ["ref"] = "v2" } },
                    new JObject { ["name"] = "git", ["source"] = new JObject { ["source"] = "git", ["url"] = "https://gitlab.example/x/y.git" } },
                    new JObject
                    {
                        ["name"] = "agent-bridge",
                        ["source"] = new JObject { ["source"] = "git-subdir", ["url"] = "https://github.com/msmith297/agent-bridge.git", ["path"] = "plugin" },
                        ["solidworksAgent"] = new JObject { ["installWith"] = new JArray("agent-bridge-solidworks"), ["env"] = new JObject { ["AGENT_BRIDGE_NAME"] = "x" } },
                    },
                    new JObject { ["name"] = "zip", ["source"] = new JObject { ["source"] = "url", ["url"] = "https://example.invalid/p.zip" } },
                    new JObject { ["name"] = "plain-http", ["source"] = new JObject { ["source"] = "url", ["url"] = "http://example.invalid/p.zip" } },
                    new JObject { ["name"] = "npm", ["source"] = new JObject { ["source"] = "npm", ["package"] = "x" } },
                    new JObject { ["name"] = "escape", ["source"] = "./../other" },
                    new JObject { ["name"] = "subdir-escape", ["source"] = new JObject { ["source"] = "git-subdir", ["url"] = "https://github.com/a/b", ["path"] = "../x" } },
                    new JObject { ["name"] = "Bad Name", ["source"] = "./x" },
                    new JObject { ["name"] = "rel", ["source"] = "./dup" },
                },
            }.ToString());

            Assert.Equal("kinds", market.Name);
            var byName = market.Plugins.ToDictionary(p => p.Name);
            Assert.Equal(PluginSource.SourceKind.Relative, byName["rel"].Source.Kind);
            Assert.Equal("1.2.0", byName["rel"].Version);
            Assert.Equal(new[] { "a", "b" }, byName["rel"].Keywords);
            Assert.Equal(PluginSource.SourceKind.GitHub, byName["short-gh"].Source.Kind);
            Assert.Equal("owner/repo", byName["gh"].Source.GitHubRepoName);
            Assert.Equal("v2", byName["gh"].Source.Ref);
            Assert.Equal(PluginSource.SourceKind.Git, byName["git"].Source.Kind);
            Assert.Null(byName["git"].Source.GitHubRepoName); // not GitHub: needs git.exe
            var ab = byName["agent-bridge"];
            Assert.Equal(PluginSource.SourceKind.GitSubdir, ab.Source.Kind);
            Assert.Equal("plugin", ab.Source.SubPath);
            Assert.Equal("msmith297/agent-bridge", ab.Source.GitHubRepoName); // fetchable as a zip without git
            Assert.Equal("agent-bridge-solidworks", (string)ab.SolidWorksAgent["installWith"][0]);
            Assert.Equal(PluginSource.SourceKind.Url, byName["zip"].Source.Kind);
            Assert.Contains("https", byName["plain-http"].SourceError);
            Assert.Contains("npm", byName["npm"].SourceError);
            Assert.NotNull(byName["escape"].SourceError);
            Assert.NotNull(byName["subdir-escape"].SourceError);
            Assert.False(byName.ContainsKey("Bad Name"));
            Assert.Equal("./plugins/rel", byName["rel"].Source.Path); // the first "rel" wins
            Assert.Equal(2, market.Findings.Count);

            Assert.Throws<InvalidOperationException>(() => MarketplaceManifest.Parse("{ \"name\": \"Not Kebab\", \"plugins\": [] }"));
            // a source round-trips through installed.json
            var back = PluginSource.FromJson(ab.Source.ToJson());
            Assert.Equal(ab.Source.Url, back.Url);
            Assert.Equal("plugin", back.SubPath);
        }

        [Fact]
        public void What_the_user_types_to_add_a_marketplace()
        {
            using (var env = new PluginTestEnv(makeInstance: false))
            {
                Assert.Equal(PluginSource.SourceKind.Folder, PluginSource.FromUserInput(env.Market).Kind);
                Assert.Equal("msmith297/agent-bridge", PluginSource.FromUserInput("msmith297/agent-bridge").Repo);
                Assert.Equal("msmith297/agent-bridge", PluginSource.FromUserInput("https://github.com/msmith297/agent-bridge.git").Repo);
                Assert.Equal(PluginSource.SourceKind.Url, PluginSource.FromUserInput("https://example.invalid/marketplace.json").Kind);
                Assert.Equal(PluginSource.SourceKind.Git, PluginSource.FromUserInput("https://gitlab.example/a/b.git").Kind);
                Assert.Equal(PluginSource.SourceKind.Git, PluginSource.FromUserInput("file:///C:/repos/market").Kind);
                Assert.Throws<InvalidOperationException>(() => PluginSource.FromUserInput(Path.Combine(env.Dir, "missing")));
                Assert.Throws<InvalidOperationException>(() => PluginSource.FromUserInput("just words"));
            }
        }

        [Fact]
        public void Zips_extract_into_their_folder_only()
        {
            using (var env = new PluginTestEnv(makeInstance: false))
            {
                var good = Path.Combine(env.Dir, "good.zip");
                using (var z = ZipFile.Open(good, ZipArchiveMode.Create))
                {
                    WriteEntry(z, "repo-abc123/.claude-plugin/plugin.json", "{ \"name\": \"zipped\" }");
                    WriteEntry(z, "repo-abc123/plugin/README.md", "hi");
                }
                var x = Path.Combine(env.Dir, "x");
                Fetcher.ExtractZip(good, x);
                var root = Fetcher.SingleRoot(x); // codeload's "repo-sha/" folder
                Assert.EndsWith("repo-abc123", root);
                Assert.Equal("zipped", PluginManifest.Load(root).Name);

                var evil = Path.Combine(env.Dir, "evil.zip");
                using (var z = ZipFile.Open(evil, ZipArchiveMode.Create)) WriteEntry(z, "../escaped.txt", "nope");
                var ex = Assert.Throws<InvalidOperationException>(() => Fetcher.ExtractZip(evil, Path.Combine(env.Dir, "y")));
                Assert.Contains("outside", ex.Message);
                Assert.False(File.Exists(Path.Combine(env.Dir, "escaped.txt")));
            }
        }

        private static void WriteEntry(ZipArchive z, string name, string text)
        {
            using (var w = new StreamWriter(z.CreateEntry(name).Open())) w.Write(text);
        }

        [Fact]
        public void A_git_source_without_git_is_refused_unless_it_is_on_github()
        {
            using (var env = new PluginTestEnv(makeInstance: false))
            {
                Fetcher.GitOverride = false;
                var src = PluginSource.FromEntry(new JObject { ["source"] = "git", ["url"] = "https://gitlab.example/x/y.git" });
                var ex = Assert.Throws<InvalidOperationException>(() => Fetcher.FetchTo(src, null, Path.Combine(env.Dir, "out")));
                Assert.Contains("Git", ex.Message);
                Assert.False(Directory.Exists(Path.Combine(env.Dir, "out")));
            }
        }

        [Fact]
        public void Folder_marketplaces_are_read_in_place_and_reserved_names_refused()
        {
            using (var env = new PluginTestEnv())
            {
                env.AddPlugin("alpha");
                var m = env.Pm.AddMarketplace(env.Market);
                Assert.Equal("test-market", m.Name);
                Assert.Equal(env.Market, m.Root);
                Assert.Equal(new[] { "solidworks-agent", "local", "test-market" }, env.Pm.Marketplaces().Select(x => x.Name).ToArray());
                Assert.Throws<InvalidOperationException>(() => env.Pm.AddMarketplace(env.Market)); // already added

                // a plugin added to the folder shows at once
                env.AddPlugin("beta");
                var listed = env.Pm.MarketplacesJson().Single(x => (string)x["name"] == "test-market")["plugins"].Select(p => (string)p["id"]).ToArray();
                Assert.Equal(new[] { "alpha@test-market", "beta@test-market" }, listed);

                var reserved = Path.Combine(env.Dir, "reserved");
                PluginTestEnv.WriteJson(Path.Combine(reserved, ".claude-plugin", "marketplace.json"), new JObject { ["name"] = "local", ["plugins"] = new JArray() });
                Assert.Throws<InvalidOperationException>(() => env.Pm.AddMarketplace(reserved));
                Assert.Throws<InvalidOperationException>(() => env.Pm.RemoveMarketplace("solidworks-agent"));
                env.Pm.RemoveMarketplace("test-market");
                Assert.True(Directory.Exists(env.Market)); // the user's folder is never deleted
                Assert.Equal(2, env.Pm.Marketplaces().Count);
            }
        }

        [GitFact]
        public void A_git_marketplace_clones_installs_by_commit_and_updates()
        {
            using (var env = new PluginTestEnv())
            {
                env.AddPlugin("gitty", new JObject { ["name"] = "gitty", ["description"] = "no version" });
                Git(env.Market, "init", "-q");
                Git(env.Market, "-c", "user.email=t@t", "-c", "user.name=t", "add", "-A");
                Git(env.Market, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-q", "-m", "one");
                var url = "file:///" + env.Market.Replace('\\', '/');

                var m = env.Pm.AddMarketplace(url);
                Assert.Equal("test-market", m.Name);
                Assert.True(Directory.Exists(Path.Combine(env.Pm.MarketplacesDir, "test-market", ".git")));

                var r = env.Pm.Install("gitty@test-market");
                Assert.True(r.Ok);
                var sha = Fetcher.GitHead(Path.Combine(env.Pm.MarketplacesDir, "test-market"));
                Assert.Equal(sha.Substring(0, 12), r.Plugin.Record.Version); // no version: the commit names the cache folder
                Assert.Matches("^[0-9a-f]{7,40}$", Path.GetFileName(r.Plugin.Root));

                // a new commit in the marketplace: update the marketplace, then the plugin
                PluginTestEnv.Write(Path.Combine(env.Market, "gitty", "skills", "s", "SKILL.md"), "---\nname: s\ndescription: d\n---\nx");
                Git(env.Market, "-c", "user.email=t@t", "-c", "user.name=t", "add", "-A");
                Git(env.Market, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-q", "-m", "two");
                env.Pm.UpdateMarketplace("test-market");
                var u = env.Pm.Update("gitty@test-market");
                Assert.True(u.Ok);
                Assert.False(u.UpToDate);
                Assert.NotEqual(r.Plugin.Root, u.Plugin.Root);
                Assert.False(Directory.Exists(r.Plugin.Root));
                Assert.Single(SkillIndex.For(u.Plugin.Manifest));
            }
        }

        private static void Git(string dir, params string[] args) => Fetcher.RunGit(dir, System.Threading.CancellationToken.None, args);
    }
}
