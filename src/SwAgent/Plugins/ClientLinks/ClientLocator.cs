using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SwAgent.Infrastructure;
using Unify3D.LocalLink;

namespace SwAgent.Plugins
{
    /// <summary>Where a client keeps its config, or why it can't be used.</summary>
    internal sealed class ClientLocation
    {
        public string Client;
        public bool Found;

        /// <summary>The config file (claude_desktop_config.json / config.toml) when found.</summary>
        public string ConfigPath;

        /// <summary>When not found: the plain sentence for the user (docs/plugins/local-mcp.md 7.2).</summary>
        public string Note;
    }

    /// <summary>
    /// Finds Claude Desktop's and Codex's config files (docs/plugins/local-mcp.md 7.2). Every root comes from the constructor, so
    /// tests pass temp folders; <see cref="Default"/> uses the real ones, except under SWAGENT_DATA_DIR (every test run), where it
    /// uses &lt;test root&gt;\Clients\... and can never reach the user's %APPDATA%\Claude or ~\.codex. Files only: no SolidWorks.
    /// </summary>
    internal sealed class ClientLocator
    {
        private static readonly Regex ClaudePackage = new Regex("^Claude_[A-Za-z0-9]{13}$");

        public readonly string AppData;
        public readonly string LocalAppData;
        public readonly string UserProfile;
        public readonly string CodexHomeEnv;

        /// <summary>Whether the client's process runs (a status hint only). Tests replace it.</summary>
        public Func<string, bool> IsRunning;

        public ClientLocator(string appData, string localAppData, string userProfile, string codexHomeEnv, Func<string, bool> isRunning = null)
        {
            AppData = appData;
            LocalAppData = localAppData;
            UserProfile = userProfile;
            CodexHomeEnv = codexHomeEnv;
            IsRunning = isRunning ?? (_ => false);
        }

        /// <summary>The real folders, or the test root's Clients\ folders when SWAGENT_DATA_DIR is set.</summary>
        public static ClientLocator Default
        {
            get
            {
                var test = Paths.TestRoot;
                if (test != null)
                {
                    var root = Path.Combine(test, "Clients");
                    return new ClientLocator(Path.Combine(root, "AppData"), Path.Combine(root, "LocalAppData"), Path.Combine(root, "Home"), null);
                }
                return new ClientLocator(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    CodexHomeFromEnvironment(),
                    ProcessRuns);
            }
        }

        /// <summary>CODEX_HOME of this process, else the user's (HKCU\Environment): SolidWorks may have started before it was set.</summary>
        private static string CodexHomeFromEnvironment()
        {
            var v = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (!string.IsNullOrWhiteSpace(v)) return Environment.ExpandEnvironmentVariables(v);
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey("Environment"))
                {
                    var r = key?.GetValue("CODEX_HOME") as string;
                    return string.IsNullOrWhiteSpace(r) ? null : Environment.ExpandEnvironmentVariables(r);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Reading CODEX_HOME from the registry failed: " + ex.Message);
                return null;
            }
        }

        private static bool ProcessRuns(string client)
        {
            var names = client == LinkProtocol.Codex ? new[] { "Codex", "codex" } : new[] { "claude" };
            foreach (var n in names)
            {
                var found = Process.GetProcessesByName(n);
                var any = found.Length > 0;
                foreach (var p in found) p.Dispose();
                if (any) return true;
            }
            return false;
        }

        public ClientLocation Locate(string client) => client == LinkProtocol.Codex ? LocateCodex() : LocateClaude();

        /// <summary>
        /// %APPDATA%\Claude (A), or the MSIX package's LocalCache\Roaming\Claude (B) when B's config.json is newer than A's (or A has
        /// none); the package folder is matched as Claude_&lt;13 chars&gt;, so the publisher hash isn't hard-coded.
        /// </summary>
        public ClientLocation LocateClaude()
        {
            var a = Path.Combine(AppData, "Claude");
            var packages = Path.Combine(LocalAppData, "Packages");
            var packageDirs = Directory.Exists(packages)
                ? Directory.GetDirectories(packages, "Claude_*").Where(d => ClaudePackage.IsMatch(Path.GetFileName(d))).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList()
                : new System.Collections.Generic.List<string>();
            var aState = Path.Combine(a, "config.json");
            var aTime = File.Exists(aState) ? File.GetLastWriteTimeUtc(aState) : (DateTime?)null;
            string chosen = null;
            DateTime best = DateTime.MinValue;
            foreach (var pkg in packageDirs)
            {
                var b = Path.Combine(pkg, "LocalCache", "Roaming", "Claude");
                var bState = Path.Combine(b, "config.json");
                if (!File.Exists(bState)) continue;
                var bTime = File.GetLastWriteTimeUtc(bState);
                if ((aTime == null || bTime > aTime.Value) && bTime > best)
                {
                    chosen = b;
                    best = bTime;
                }
            }
            if (chosen == null && Directory.Exists(a)) chosen = a;
            if (chosen != null)
                return new ClientLocation { Client = LinkProtocol.ClaudeDesktop, Found = true, ConfigPath = Path.Combine(chosen, "claude_desktop_config.json") };
            var installed = packageDirs.Count > 0 || Directory.Exists(Path.Combine(LocalAppData, "AnthropicClaude"));
            return new ClientLocation
            {
                Client = LinkProtocol.ClaudeDesktop,
                Note = installed
                    ? "Claude Desktop is installed but hasn't been opened yet. Open it once, then try again."
                    : "Claude Desktop isn't installed on this PC. Get it from claude.ai/download, open it once, then try again.",
            };
        }

        /// <summary>CODEX_HOME when it is set and exists, else ~\.codex; installed when that folder exists.</summary>
        public ClientLocation LocateCodex()
        {
            var home = !string.IsNullOrWhiteSpace(CodexHomeEnv) && Directory.Exists(CodexHomeEnv) ? CodexHomeEnv : Path.Combine(UserProfile, ".codex");
            if (!Directory.Exists(home))
                return new ClientLocation { Client = LinkProtocol.Codex, Note = "Codex isn't installed on this PC (no .codex folder). Install Codex and start it once, then try again." };
            return new ClientLocation { Client = LinkProtocol.Codex, Found = true, ConfigPath = Path.Combine(home, "config.toml") };
        }
    }
}
