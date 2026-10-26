using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SwAgent.Infrastructure;

namespace SwAgent.Plugins
{
    /// <summary>A client config change that was refused; the message is the plain sentence for the user. Nothing was written.</summary>
    internal sealed class ClientConfigException : InvalidOperationException
    {
        public ClientConfigException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// One safe edit of another app's config file (docs/plugins/local-mcp.md 7.3): read the bytes, change the text, check that only
    /// Unify3D's entry changed, back the original up into Unify3D's own data folder (newest 5), write a temp file next to it with
    /// the same BOM, and replace the file in one step - again from the start (up to 3 times) when the app wrote it meanwhile.
    /// Backups may hold other servers' keys: they are never logged and never go into a chat.
    /// </summary>
    internal static class ClientConfigFile
    {
        public const int KeepBackups = 5;
        private static readonly byte[] Bom = { 0xEF, 0xBB, 0xBF };
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        /// <summary>Tests: runs after the temp file is written, before the target's hash is checked again.</summary>
        internal static Action<string> BeforeReplace;

        /// <summary>The file's line ending: CRLF when its first line ends with CRLF, else LF.</summary>
        public static string EolOf(string text)
        {
            var i = text.IndexOf('\n');
            return i > 0 && text[i - 1] == '\r' ? "\r\n" : "\n";
        }

        /// <summary>The file's text without its BOM ("" when it doesn't exist), for read-only checks.</summary>
        public static string ReadText(string path, string label)
        {
            if (!File.Exists(path)) return "";
            return Decode(File.ReadAllBytes(path), path, label, out _);
        }

        /// <summary>
        /// Apply <paramref name="change"/> to the file's text (without BOM; "" for a missing file). <paramref name="check"/> gets
        /// (before, after) and throws <see cref="ClientConfigException"/> when the change isn't safe. Returns true when the file
        /// was written, false when the change left it as it was.
        /// </summary>
        /// <param name="backupDir">Unify3D's own folder for backups (plugins\data\&lt;plugin&gt;\backups); never next to the client's file.</param>
        /// <param name="label">"Claude Desktop" / "Codex", for the messages.</param>
        public static bool Edit(string path, Func<string, string> change, Action<string, string> check, string backupDir, string label)
        {
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                var bytes = File.Exists(path) ? ReadBytes(path, label) : null;
                var hash = Hash(bytes);
                var before = bytes == null ? "" : Decode(bytes, path, label, out _);
                var bom = bytes != null && StartsWithBom(bytes);
                var after = change(before);
                if (after == before) return false;
                try
                {
                    check(before, after);
                }
                catch (ClientConfigException ex)
                {
                    Log.Warn($"Unify3D's change to {path} was refused: {ex.Message}");
                    throw new ClientConfigException($"Unify3D couldn't change {path} safely; nothing was changed.");
                }

                var tmp = path + ".unify3d-tmp";
                try
                {
                    var body = Utf8.GetBytes(after);
                    File.WriteAllBytes(tmp, bom ? Bom.Concat(body).ToArray() : body);
                    BeforeReplace?.Invoke(path);
                    if (Hash(File.Exists(path) ? ReadBytes(path, label) : null) != hash)
                    {
                        Log.Info($"{path} changed while Unify3D was editing it; reading it again (attempt {attempt})");
                        TryDelete(tmp);
                        continue;
                    }
                    if (bytes != null) Backup(bytes, path, backupDir);
                    AtomicFile.Replace(tmp, path);
                    return true;
                }
                catch (ClientConfigException)
                {
                    TryDelete(tmp);
                    throw;
                }
                catch (IOException ex)
                {
                    TryDelete(tmp);
                    Log.Warn($"Writing {path} failed: {ex.Message}");
                    throw new ClientConfigException($"Couldn't write {path}: it is in use. Close {label} and press Repair.");
                }
                catch (UnauthorizedAccessException ex)
                {
                    TryDelete(tmp);
                    Log.Warn($"Writing {path} failed: {ex.Message}");
                    throw new ClientConfigException($"Couldn't write {path}: Windows denied access. Check the file isn't read-only, then press Repair.");
                }
            }
            throw new ClientConfigException($"Couldn't write {path}: {label} kept changing it. Close {label} and press Repair.");
        }

        private static byte[] ReadBytes(string path, string label)
        {
            try
            {
                return File.ReadAllBytes(path);
            }
            catch (IOException)
            {
                throw new ClientConfigException($"Couldn't read {path}: it is in use. Close {label} and press Repair.");
            }
            catch (UnauthorizedAccessException)
            {
                throw new ClientConfigException($"Couldn't read {path}: Windows denied access.");
            }
        }

        private static bool StartsWithBom(byte[] b) => b.Length >= 3 && b[0] == Bom[0] && b[1] == Bom[1] && b[2] == Bom[2];

        private static string Decode(byte[] bytes, string path, string label, out bool bom)
        {
            bom = StartsWithBom(bytes);
            try
            {
                return Utf8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
            }
            catch (DecoderFallbackException)
            {
                throw new ClientConfigException($"{label}'s settings file {path} isn't UTF-8 text. Unify3D won't change it.");
            }
        }

        private static string Hash(byte[] bytes)
        {
            if (bytes == null) return "missing";
            using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(bytes));
        }

        /// <summary>backups\&lt;file&gt;-&lt;yyyyMMdd-HHmmss-fff&gt;, the newest 5 kept.</summary>
        private static void Backup(byte[] bytes, string path, string backupDir)
        {
            Directory.CreateDirectory(backupDir);
            var name = Path.GetFileName(path) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            var file = Path.Combine(backupDir, name);
            for (var n = 1; File.Exists(file); n++) file = Path.Combine(backupDir, name + "-" + n);
            File.WriteAllBytes(file, bytes);
            var prefix = Path.GetFileName(path) + "-";
            var old = Directory.GetFiles(backupDir, prefix + "*")
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f)).ThenByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
                .Skip(KeepBackups).ToList();
            foreach (var f in old) TryDelete(f);
        }

        private static void TryDelete(string file)
        {
            try
            {
                if (File.Exists(file)) File.Delete(file);
            }
            catch (IOException)
            {
                // Left behind; harmless.
            }
            catch (UnauthorizedAccessException)
            {
                // Same.
            }
        }
    }
}
