using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json.Linq;
using SwAgent.Infrastructure;

namespace SwAgent.Plugins
{
    /// <summary>
    /// Where a marketplace or a plugin comes from (design 3, "Marketplaces"): a folder inside the marketplace ("./path"), a
    /// local folder, GitHub (owner/repo), a git URL (optionally a sub-folder of it: git-subdir), or an https URL (a zip for a
    /// plugin, a marketplace.json for a marketplace).
    /// </summary>
    internal sealed class PluginSource
    {
        public enum SourceKind { Relative, Folder, GitHub, Git, GitSubdir, Url }

        public SourceKind Kind;

        /// <summary>Relative: the path inside the marketplace. Folder: the absolute folder.</summary>
        public string Path;

        /// <summary>GitHub: "owner/repo".</summary>
        public string Repo;

        /// <summary>Git, GitSubdir and Url: the URL.</summary>
        public string Url;

        /// <summary>Optional branch, tag or commit.</summary>
        public string Ref;

        /// <summary>GitSubdir: the folder inside the repository ("plugin").</summary>
        public string SubPath;

        private static readonly Regex GitHubRepo = new Regex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$");
        private static readonly Regex GitHubUrl = new Regex(@"^(?:https?://|ssh://git@|git@)(?:www\.)?github\.com[/:]([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+?)(?:\.git)?/?$", RegexOptions.IgnoreCase);

        /// <summary>"owner/repo" when the source is on github.com (also a git or git-subdir URL there: those can be fetched as a zip without git).</summary>
        public string GitHubRepoName
        {
            get
            {
                if (Kind == SourceKind.GitHub) return Repo;
                if ((Kind == SourceKind.Git || Kind == SourceKind.GitSubdir) && GitHubUrl.Match(Url ?? "") is Match m && m.Success)
                    return m.Groups[1].Value + "/" + m.Groups[2].Value;
                return null;
            }
        }

        /// <summary>A plugin entry's "source" in a marketplace.json. Throws with a readable message for anything else.</summary>
        public static PluginSource FromEntry(JToken source)
        {
            if (source == null || source.Type == JTokenType.Null) throw new InvalidOperationException("The plugin has no source.");
            if (source.Type == JTokenType.String)
            {
                var s = ((string)source).Trim();
                if (s.StartsWith("./") || s.StartsWith(".\\") || s == ".") return Relative(s);
                if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("git@") || s.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                    return new PluginSource { Kind = SourceKind.Git, Url = s };
                if (GitHubRepo.IsMatch(s)) return new PluginSource { Kind = SourceKind.GitHub, Repo = s };
                throw new InvalidOperationException($"Unsupported plugin source \"{s}\": use \"./folder\", \"owner/repo\" or a git URL.");
            }
            if (!(source is JObject o)) throw new InvalidOperationException("The plugin source must be a string or an object.");
            var kind = PluginManifest.Str(o["source"]) ?? "";
            var r = PluginManifest.Str(o["ref"]);
            switch (kind)
            {
                case "github":
                    var repo = PluginManifest.Str(o["repo"]) ?? "";
                    if (!GitHubRepo.IsMatch(repo)) throw new InvalidOperationException($"GitHub source: \"{repo}\" is not owner/repo.");
                    return new PluginSource { Kind = SourceKind.GitHub, Repo = repo, Ref = r };
                case "git":
                    return new PluginSource { Kind = SourceKind.Git, Url = RequireUrl(o), Ref = r };
                case "git-subdir":
                    var sub = PluginManifest.Str(o["path"]) ?? "";
                    if (!SafeRelative(sub)) throw new InvalidOperationException($"git-subdir source: \"{sub}\" must be a relative folder inside the repository.");
                    return new PluginSource { Kind = SourceKind.GitSubdir, Url = RequireUrl(o), Ref = r, SubPath = sub };
                case "url":
                    var url = RequireUrl(o);
                    if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("url source: only https:// zips are supported.");
                    return new PluginSource { Kind = SourceKind.Url, Url = url };
                default:
                    throw new InvalidOperationException($"Unsupported plugin source kind \"{kind}\" (supported: github, git, git-subdir, url, or a \"./folder\").");
            }
        }

        private static PluginSource Relative(string s)
        {
            if (!SafeRelative(s)) throw new InvalidOperationException($"\"{s}\" must stay inside the marketplace folder.");
            return new PluginSource { Kind = SourceKind.Relative, Path = s };
        }

        private static string RequireUrl(JObject o)
        {
            var url = PluginManifest.Str(o["url"]);
            if (url == null) throw new InvalidOperationException("The source has no url.");
            if (!(url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                  url.StartsWith("git@") || url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("file://", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"\"{url}\" is not a URL git can fetch.");
            return url;
        }

        /// <summary>A relative path with no ".." segments and no root.</summary>
        internal static bool SafeRelative(string s)
        {
            if (string.IsNullOrWhiteSpace(s) || System.IO.Path.IsPathRooted(s) || s.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0) return false;
            return !s.Replace('\\', '/').Split('/').Any(p => p == "..");
        }

        /// <summary>
        /// What the user typed to add a marketplace: a local folder, "owner/repo", an https URL of a marketplace.json, or a git
        /// URL (https, ssh, file://).
        /// </summary>
        public static PluginSource FromUserInput(string input)
        {
            var s = (input ?? "").Trim().Trim('"');
            if (s.Length == 0) throw new InvalidOperationException("Enter a GitHub owner/repo, a git or https URL, or a folder.");
            if (System.IO.Path.IsPathRooted(s) && !s.StartsWith("//"))
            {
                if (!Directory.Exists(s)) throw new InvalidOperationException($"The folder \"{s}\" does not exist.");
                return new PluginSource { Kind = SourceKind.Folder, Path = System.IO.Path.GetFullPath(s) };
            }
            if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && s.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                return new PluginSource { Kind = SourceKind.Url, Url = s };
            if (GitHubRepo.IsMatch(s)) return new PluginSource { Kind = SourceKind.GitHub, Repo = s };
            if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("git@") || s.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                return GitHubUrl.Match(s) is Match m && m.Success
                    ? new PluginSource { Kind = SourceKind.GitHub, Repo = m.Groups[1].Value + "/" + m.Groups[2].Value }
                    : new PluginSource { Kind = SourceKind.Git, Url = s };
            }
            throw new InvalidOperationException($"\"{s}\" is not a folder, owner/repo or URL.");
        }

        public JObject ToJson()
        {
            var o = new JObject();
            switch (Kind)
            {
                case SourceKind.Relative: o["source"] = "path"; o["path"] = Path; break;
                case SourceKind.Folder: o["source"] = "directory"; o["path"] = Path; break;
                case SourceKind.GitHub: o["source"] = "github"; o["repo"] = Repo; break;
                case SourceKind.Git: o["source"] = "git"; o["url"] = Url; break;
                case SourceKind.GitSubdir: o["source"] = "git-subdir"; o["url"] = Url; o["path"] = SubPath; break;
                case SourceKind.Url: o["source"] = "url"; o["url"] = Url; break;
            }
            if (Ref != null) o["ref"] = Ref;
            return o;
        }

        public static PluginSource FromJson(JObject o)
        {
            switch ((string)o?["source"])
            {
                case "path": return new PluginSource { Kind = SourceKind.Relative, Path = (string)o["path"] };
                case "directory": return new PluginSource { Kind = SourceKind.Folder, Path = (string)o["path"] };
                default: return FromEntry(o);
            }
        }

        public override string ToString()
        {
            switch (Kind)
            {
                case SourceKind.Relative:
                case SourceKind.Folder: return Path;
                case SourceKind.GitHub: return Repo + (Ref != null ? "@" + Ref : "");
                case SourceKind.GitSubdir: return Url + " (" + SubPath + ")" + (Ref != null ? " @" + Ref : "");
                default: return Url + (Ref != null ? " @" + Ref : "");
            }
        }
    }

    /// <summary>One plugin listed in a marketplace.json.</summary>
    internal sealed class MarketplaceEntry
    {
        public string Name;
        public string Description;
        public string Version;
        public string Category;
        public string[] Keywords = new string[0];
        public JToken RawSource;

        /// <summary>The parsed source, or null when SourceError says why it can't be used.</summary>
        public PluginSource Source;
        public string SourceError;

        /// <summary>The entry's "solidworksAgent" object: env (merged into the plugin's env), installWith, hideSkills.</summary>
        public JObject SolidWorksAgent = new JObject();

        public JObject Json;
    }

    /// <summary>A .claude-plugin/marketplace.json: { name, owner, plugins: [ { name, source, description, version, category, keywords } ] }.</summary>
    internal sealed class MarketplaceManifest
    {
        public string Name;

        /// <summary>
        /// The name to show ("SolidWorks Agent" for the built-in "solidworks-agent"): the top-level solidworksAgent.displayName, trimmed, at most 48
        /// characters; else null. The id (Name) stays what installed plugin ids use.
        /// </summary>
        public string DisplayName;
        public string Owner;
        public string Description;
        public List<MarketplaceEntry> Plugins = new List<MarketplaceEntry>();
        public List<string> Findings = new List<string>();

        public static string PathIn(string root) => Path.Combine(root, ".claude-plugin", "marketplace.json");

        public static MarketplaceManifest Load(string root) => Parse(File.ReadAllText(PathIn(root)));

        public static MarketplaceManifest Parse(string json)
        {
            var j = JObject.Parse(json);
            var m = new MarketplaceManifest
            {
                Name = PluginManifest.Str(j["name"]),
                Owner = j["owner"] is JObject o ? PluginManifest.Str(o["name"]) : PluginManifest.Str(j["owner"]),
                Description = PluginManifest.Str(j["description"]) ?? PluginManifest.Str(j["metadata"]?["description"]),
                DisplayName = DisplayNameOf(((j["solidworksAgent"] ?? j["unify3d"]) as JObject)?["displayName"]),
            };
            if (m.Name == null || !PluginManifest.NamePattern.IsMatch(m.Name)) throw new InvalidOperationException($"The marketplace name \"{m.Name}\" must be 2-48 characters: lowercase letters, digits and dashes.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in (j["plugins"] as JArray ?? new JArray()).OfType<JObject>())
            {
                var e = new MarketplaceEntry
                {
                    Json = p,
                    Name = PluginManifest.Str(p["name"]),
                    Description = PluginManifest.Str(p["description"]) ?? "",
                    Version = PluginManifest.Str(p["version"]),
                    Category = PluginManifest.Str(p["category"]),
                    Keywords = (p["keywords"] as JArray ?? p["tags"] as JArray)?.Select(k => (string)k).Where(k => !string.IsNullOrWhiteSpace(k)).ToArray() ?? new string[0],
                    RawSource = p["source"],
                    SolidWorksAgent = (p["solidworksAgent"] ?? p["unify3d"]) as JObject ?? new JObject(),
                };
                if (e.Name == null || !PluginManifest.NamePattern.IsMatch(e.Name))
                {
                    m.Findings.Add($"Plugin \"{e.Name}\" skipped: names are 2-48 characters, lowercase letters, digits and dashes.");
                    continue;
                }
                if (!seen.Add(e.Name))
                {
                    m.Findings.Add($"Plugin \"{e.Name}\" is listed twice; the first entry is used.");
                    continue;
                }
                try
                {
                    e.Source = PluginSource.FromEntry(e.RawSource);
                }
                catch (InvalidOperationException ex)
                {
                    e.SourceError = ex.Message;
                }
                m.Plugins.Add(e);
            }
            return m;
        }

        private static string DisplayNameOf(JToken t)
        {
            var s = t?.Type == JTokenType.String ? ((string)t).Trim() : null;
            return string.IsNullOrEmpty(s) || s.Length > 48 ? null : s;
        }
    }

    /// <summary>
    /// Fetches marketplaces and plugins: git.exe when it is installed (shallow clone; update = fetch + hard reset), else a
    /// GitHub zip from codeload.github.com (any github.com git or git-subdir URL counts as GitHub), local folders by copy,
    /// https by one HttpClient (TLS 1.2, User-Agent). Never touches SolidWorks; runs on background threads.
    /// </summary>
    internal static class Fetcher
    {
        private static readonly Lazy<HttpClient> Http = new Lazy<HttpClient>(() =>
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SolidWorks-Agent-Plugins");
            return client;
        });

        /// <summary>Tests: false pretends git isn't installed (the zip path).</summary>
        internal static bool? GitOverride;

        private static string _git;

        /// <summary>git.exe from PATH (the registry's, so a Git install after SolidWorks started counts: a miss is looked up again), or null.</summary>
        public static string Git
        {
            get
            {
                if (GitOverride == false) return null;
                if (_git == null || !File.Exists(_git)) _git = ExecutableResolver.Find("git");
                return _git;
            }
        }

        /// <summary>
        /// Fetch a source into a new folder dest (which must not exist). Returns the commit sha when known. Relative sources
        /// resolve against marketplaceRoot and must stay inside it.
        /// </summary>
        public static string FetchTo(PluginSource source, string marketplaceRoot, string dest, CancellationToken cancel = default(CancellationToken))
        {
            switch (source.Kind)
            {
                case PluginSource.SourceKind.Relative:
                {
                    if (marketplaceRoot == null) throw new InvalidOperationException("A \"./folder\" source needs a marketplace folder; this marketplace was added from a single marketplace.json.");
                    var from = Path.GetFullPath(Path.Combine(marketplaceRoot, source.Path));
                    if (!PluginManifest.IsInside(marketplaceRoot, from)) throw new InvalidOperationException($"\"{source.Path}\" points outside the marketplace.");
                    if (!Directory.Exists(from)) throw new InvalidOperationException($"The marketplace has no folder \"{source.Path}\".");
                    CopyDirectory(from, dest);
                    return GitHead(marketplaceRoot);
                }
                case PluginSource.SourceKind.Folder:
                    if (!Directory.Exists(source.Path)) throw new InvalidOperationException($"The folder \"{source.Path}\" does not exist.");
                    CopyDirectory(source.Path, dest);
                    return GitHead(source.Path);
                case PluginSource.SourceKind.Url:
                {
                    var tmp = TempDir(dest);
                    try
                    {
                        var zip = Path.Combine(tmp, "download.zip");
                        Download(source.Url, zip, cancel);
                        var x = Path.Combine(tmp, "x");
                        ExtractZip(zip, x);
                        MoveDirectory(SingleRoot(x), dest);
                        return null;
                    }
                    finally
                    {
                        TryDelete(tmp);
                    }
                }
                default:
                    return FetchRepo(source, dest, cancel);
            }
        }

        /// <summary>github, git and git-subdir: clone (git) or the GitHub zip (no git), then keep the sub-folder for git-subdir.</summary>
        private static string FetchRepo(PluginSource source, string dest, CancellationToken cancel)
        {
            var tmp = TempDir(dest);
            try
            {
                var repo = Path.Combine(tmp, "repo");
                string sha;
                var gh = source.GitHubRepoName;
                if (Git != null)
                {
                    var url = source.Kind == PluginSource.SourceKind.GitHub ? $"https://github.com/{source.Repo}.git" : source.Url;
                    sha = Clone(url, source.Ref, repo, cancel);
                }
                else if (gh != null)
                {
                    sha = DownloadGitHub(gh, source.Ref, repo, cancel);
                }
                else
                {
                    throw new InvalidOperationException("This plugin is in a git repository and Git isn't installed. Install Git for Windows (https://git-scm.com), then try again.");
                }
                var from = repo;
                if (source.Kind == PluginSource.SourceKind.GitSubdir)
                {
                    from = Path.GetFullPath(Path.Combine(repo, source.SubPath));
                    if (!PluginManifest.IsInside(repo, from) || !Directory.Exists(from)) throw new InvalidOperationException($"The repository has no folder \"{source.SubPath}\".");
                }
                RemoveGitFolder(from);
                MoveDirectory(from, dest);
                return sha;
            }
            finally
            {
                TryDelete(tmp);
            }
        }

        /// <summary>The newest commit of a repository source without fetching it (git ls-remote, or the GitHub API); null when unknown.</summary>
        public static string RemoteSha(PluginSource source, CancellationToken cancel = default(CancellationToken))
        {
            try
            {
                var gh = source.GitHubRepoName;
                if (Git != null && source.Kind != PluginSource.SourceKind.Url && source.Kind != PluginSource.SourceKind.Relative && source.Kind != PluginSource.SourceKind.Folder)
                {
                    var url = source.Kind == PluginSource.SourceKind.GitHub ? $"https://github.com/{source.Repo}.git" : source.Url;
                    var output = RunGit(null, cancel, "ls-remote", url, source.Ref ?? "HEAD");
                    var line = output.Split('\n').FirstOrDefault(l => l.Trim().Length > 0);
                    return line == null ? null : line.Split('\t')[0].Trim();
                }
                if (gh != null) return GitHubSha(gh, source.Ref, cancel);
            }
            catch (Exception ex)
            {
                Log.Warn($"Checking {source} for updates failed: {ex.Message}");
            }
            return null;
        }

        /// <summary>A marketplace into dest: a git clone (or GitHub zip), a folder copy, or one marketplace.json.</summary>
        public static string FetchMarketplace(PluginSource source, string dest, CancellationToken cancel = default(CancellationToken))
        {
            if (source.Kind == PluginSource.SourceKind.Url)
            {
                var tmp = TempDir(dest);
                try
                {
                    var file = MarketplaceManifest.PathIn(tmp);
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    Download(source.Url, file, cancel);
                    MarketplaceManifest.Load(tmp); // throws when it isn't one
                    MoveDirectory(tmp, dest);
                    return null;
                }
                finally
                {
                    TryDelete(tmp);
                }
            }
            if (source.Kind == PluginSource.SourceKind.Folder || source.Kind == PluginSource.SourceKind.Relative)
                throw new InvalidOperationException("Local marketplaces are read in place.");
            if (Git != null)
            {
                var url = source.Kind == PluginSource.SourceKind.GitHub ? $"https://github.com/{source.Repo}.git" : source.Url;
                return Clone(url, source.Ref, dest, cancel);
            }
            return FetchRepo(source, dest, cancel);
        }

        /// <summary>Update a cloned marketplace in place (fetch + hard reset to the ref); false when it isn't a git clone.</summary>
        public static bool TryPull(string dir, string gitRef, CancellationToken cancel = default(CancellationToken))
        {
            if (Git == null || !Directory.Exists(Path.Combine(dir, ".git"))) return false;
            RunGit(dir, cancel, "fetch", "--depth", "1", "origin", gitRef ?? "HEAD");
            RunGit(dir, cancel, "reset", "--hard", "FETCH_HEAD");
            return true;
        }

        /// <summary>The commit a folder's git checkout is at, or null.</summary>
        public static string GitHead(string dir)
        {
            try
            {
                if (Git == null || !Directory.Exists(Path.Combine(dir, ".git"))) return null;
                return RunGit(dir, CancellationToken.None, "rev-parse", "HEAD").Trim();
            }
            catch
            {
                return null;
            }
        }

        private static string Clone(string url, string gitRef, string dest, CancellationToken cancel)
        {
            var args = new List<string> { "clone", "--depth", "1", "--quiet" };
            // A commit sha can't be cloned with --branch: clone the default branch, then fetch that commit.
            var isSha = gitRef != null && Regex.IsMatch(gitRef, "^[0-9a-f]{40}$");
            if (gitRef != null && !isSha) args.AddRange(new[] { "--branch", gitRef });
            args.Add(url);
            args.Add(dest);
            RunGit(null, cancel, args.ToArray());
            if (isSha)
            {
                RunGit(dest, cancel, "fetch", "--depth", "1", "origin", gitRef);
                RunGit(dest, cancel, "checkout", "--quiet", gitRef);
            }
            return RunGit(dest, cancel, "rev-parse", "HEAD").Trim();
        }

        /// <summary>git without prompts, and without the user's AI provider keys (SecretEnv) in its environment.</summary>
        internal static ProcessStartInfo GitStartInfo(string git, string workDir, string[] args)
        {
            var psi = new ProcessStartInfo(git, string.Join(" ", args.Select(QuoteArg)))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = workDir ?? Path.GetTempPath(),
            };
            psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            psi.EnvironmentVariables["GCM_INTERACTIVE"] = "never";
            psi.EnvironmentVariables["GIT_ASKPASS"] = "";
            SecretEnv.Scrub(psi.EnvironmentVariables);
            return psi;
        }

        /// <summary>Run git without prompts (GIT_TERMINAL_PROMPT=0) and return stdout; throws with stderr on failure.</summary>
        internal static string RunGit(string workDir, CancellationToken cancel, params string[] args)
        {
            var git = Git ?? throw new InvalidOperationException("Git isn't installed.");
            var psi = GitStartInfo(git, workDir, args);
            using (var p = Process.Start(psi))
            {
                var stdout = p.StandardOutput.ReadToEndAsync();
                var stderr = p.StandardError.ReadToEndAsync();
                var deadline = DateTime.UtcNow.AddMinutes(5);
                while (!p.WaitForExit(200))
                {
                    if (cancel.IsCancellationRequested || DateTime.UtcNow > deadline)
                    {
                        try
                        {
                            p.Kill();
                        }
                        catch
                        {
                            // Already gone.
                        }
                        throw new OperationCanceledException("git " + args.FirstOrDefault() + " was stopped.");
                    }
                }
                p.WaitForExit();
                if (p.ExitCode != 0) throw new InvalidOperationException($"git {args.FirstOrDefault()} failed: {stderr.Result.Trim()}");
                return stdout.Result;
            }
        }

        /// <summary>Windows command-line quoting for one argument.</summary>
        internal static string QuoteArg(string a)
        {
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
            var sb = new StringBuilder("\"");
            var slashes = 0;
            foreach (var c in a)
            {
                if (c == '\\') slashes++;
                else if (c == '"')
                {
                    sb.Append('\\', slashes * 2 + 1).Append('"');
                    slashes = 0;
                    continue;
                }
                else slashes = 0;
                sb.Append(c);
            }
            sb.Append('\\', slashes).Append('"');
            return sb.ToString();
        }

        /// <summary>A GitHub repository as a zip from codeload (public repositories; no credentials are ever sent).</summary>
        private static string DownloadGitHub(string repo, string gitRef, string dest, CancellationToken cancel)
        {
            var sha = GitHubSha(repo, gitRef, cancel);
            var tmp = TempDir(dest);
            try
            {
                var zip = Path.Combine(tmp, "repo.zip");
                Download($"https://codeload.github.com/{repo}/zip/{Uri.EscapeDataString(sha ?? gitRef ?? "HEAD")}", zip, cancel);
                var x = Path.Combine(tmp, "x");
                ExtractZip(zip, x);
                MoveDirectory(SingleRoot(x), dest);
                return sha;
            }
            finally
            {
                TryDelete(tmp);
            }
        }

        /// <summary>The commit sha of a ref through the GitHub API; null when it can't be read (rate limit, offline).</summary>
        private static string GitHubSha(string repo, string gitRef, CancellationToken cancel)
        {
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/commits/{Uri.EscapeDataString(gitRef ?? "HEAD")}"))
                {
                    req.Headers.Accept.ParseAdd("application/vnd.github.sha");
                    using (var res = Http.Value.SendAsync(req, cancel).GetAwaiter().GetResult())
                    {
                        if (!res.IsSuccessStatusCode) return null;
                        var text = res.Content.ReadAsStringAsync().GetAwaiter().GetResult().Trim();
                        return Regex.IsMatch(text, "^[0-9a-f]{40}$") ? text : null;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"GitHub commit lookup for {repo} failed: {ex.Message}");
                return null;
            }
        }

        private static void Download(string url, string file, CancellationToken cancel)
        {
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Only https:// downloads are allowed.");
            using (var res = Http.Value.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel).GetAwaiter().GetResult())
            {
                if (res.StatusCode == HttpStatusCode.NotFound)
                    throw new InvalidOperationException($"{url} was not found (a private repository can't be fetched without Git).");
                res.EnsureSuccessStatusCode();
                using (var s = res.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                using (var f = File.Create(file))
                    s.CopyTo(f);
            }
        }

        /// <summary>Extract a zip, refusing entries that would land outside dest (Zip Slip) and absurd sizes.</summary>
        public static void ExtractZip(string zipPath, string dest)
        {
            var root = Path.GetFullPath(Directory.CreateDirectory(dest).FullName);
            long total = 0;
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var e in zip.Entries)
                {
                    var target = Path.GetFullPath(Path.Combine(root, e.FullName.Replace('/', Path.DirectorySeparatorChar)));
                    if (!PluginManifest.IsInside(root, target)) throw new InvalidOperationException($"The zip has an entry outside its folder: \"{e.FullName}\".");
                    total += e.Length;
                    if (total > 500L * 1024 * 1024) throw new InvalidOperationException("The zip expands to more than 500 MB.");
                    if (e.FullName.EndsWith("/") || e.FullName.EndsWith("\\"))
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    e.ExtractToFile(target, overwrite: true);
                }
            }
        }

        /// <summary>The folder a zip unpacked to: its single top folder (codeload's "repo-sha/"), else the folder itself.</summary>
        internal static string SingleRoot(string dir)
        {
            var dirs = Directory.GetDirectories(dir);
            return dirs.Length == 1 && Directory.GetFiles(dir).Length == 0 ? dirs[0] : dir;
        }

        internal static void CopyDirectory(string from, string to, Func<string, bool> skip = null)
        {
            from = Path.GetFullPath(from);
            Directory.CreateDirectory(to);
            foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            {
                var rel = dir.Substring(from.Length + 1);
                if (IsGitPath(rel) || skip?.Invoke(rel) == true) continue;
                Directory.CreateDirectory(Path.Combine(to, rel));
            }
            foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                var rel = file.Substring(from.Length + 1);
                if (IsGitPath(rel) || skip?.Invoke(rel) == true) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(to, rel)));
                File.Copy(file, Path.Combine(to, rel), overwrite: true);
            }
        }

        private static bool IsGitPath(string rel) => rel == ".git" || rel.StartsWith(".git" + Path.DirectorySeparatorChar);

        /// <summary>Move a folder (copy + delete across volumes).</summary>
        internal static void MoveDirectory(string from, string to)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(to)));
            try
            {
                Directory.Move(from, to);
            }
            catch (IOException)
            {
                CopyDirectory(from, to);
                TryDelete(from);
            }
        }

        private static void RemoveGitFolder(string dir)
        {
            var git = Path.Combine(dir, ".git");
            if (Directory.Exists(git)) TryDelete(git);
        }

        /// <summary>A scratch folder next to dest (same volume, so the final move is a rename).</summary>
        private static string TempDir(string dest)
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(dest));
            return Directory.CreateDirectory(Path.Combine(parent, ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8))).FullName;
        }

        /// <summary>Delete a folder, clearing read-only flags (git objects are read-only); false when something is in use.</summary>
        internal static bool TryDelete(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return true;
                foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(dir, recursive: true);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not delete {dir}: {ex.Message}");
                return false;
            }
        }
    }
}
