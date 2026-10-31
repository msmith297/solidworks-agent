using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using SwAgent.Infrastructure;

namespace SwAgent.Plugins
{
    /// <summary>
    /// Keeps %LOCALAPPDATA%\SwAgent\local-mcp (the copy Claude Desktop and Codex run) equal to the local-mcp folder shipped next to
    /// the add-in (docs/plugins/local-mcp.md 6.2). A changed file is renamed aside (*.old-&lt;n&gt;: Windows allows that for a running
    /// exe) and the new one copied in; a running exe keeps its old code until the client restarts it. Old files are deleted
    /// once nothing uses them. Files only: runs at add-in start (background) and before every client config write.
    /// </summary>
    internal sealed class LocalMcpDeployer
    {
        public const string ExeName = "SolidWorksAgent.LocalMcp.exe";

        private static readonly object Gate = new object();

        /// <summary>The shipped folder (local-mcp next to SwAgent.dll).</summary>
        public readonly string Source;

        /// <summary>The per-user copy (Paths.LocalMcpDir).</summary>
        public readonly string Target;

        public LocalMcpDeployer(string source = null, string target = null)
        {
            Source = source ?? Path.Combine(Paths.AddInDir, "local-mcp");
            Target = target ?? Paths.LocalMcpDir;
        }

        public string Exe => Path.Combine(Target, ExeName);

        /// <summary>Bring the copy up to date. Returns false (and logs) when there is nothing shipped to copy.</summary>
        public bool Ensure()
        {
            lock (Gate)
            {
                if (!File.Exists(Path.Combine(Source, ExeName)))
                {
                    Log.Warn($"The local MCP program isn't next to the add-in ({Source}); Claude MCP and Codex MCP can't start it.");
                    return File.Exists(Exe);
                }
                Directory.CreateDirectory(Target);
                CleanOld();
                var copied = 0;
                foreach (var file in Directory.GetFiles(Source))
                {
                    var dest = Path.Combine(Target, Path.GetFileName(file));
                    if (File.Exists(dest))
                    {
                        if (Same(file, dest)) continue;
                        var aside = Aside(dest);
                        File.Move(dest, aside);
                        TryDelete(aside);
                    }
                    File.Copy(file, dest);
                    copied++;
                }
                if (copied > 0) Log.Info($"Local MCP program updated in {Target} ({copied} file(s))");
                return true;
            }
        }

        /// <summary>Delete *.old-* files nothing uses any more.</summary>
        public void CleanOld()
        {
            if (!Directory.Exists(Target)) return;
            foreach (var f in Directory.GetFiles(Target, "*.old-*")) TryDelete(f);
        }

        private static string Aside(string dest)
        {
            for (var n = 1; ; n++)
            {
                var p = dest + ".old-" + n;
                if (!File.Exists(p)) return p;
            }
        }

        private static bool Same(string a, string b)
        {
            var va = System.Diagnostics.FileVersionInfo.GetVersionInfo(a).FileVersion;
            var vb = System.Diagnostics.FileVersionInfo.GetVersionInfo(b).FileVersion;
            if (va != vb) return false;
            using (var sha = SHA256.Create())
            {
                byte[] Hash(string f)
                {
                    using (var s = File.OpenRead(f)) return sha.ComputeHash(s);
                }
                return Hash(a).SequenceEqual(Hash(b));
            }
        }

        private static void TryDelete(string f)
        {
            try
            {
                File.Delete(f);
            }
            catch (IOException)
            {
                // In use (a client still runs the old exe): deleted next time.
            }
            catch (UnauthorizedAccessException)
            {
                // Same.
            }
        }
    }
}
