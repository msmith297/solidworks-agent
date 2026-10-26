using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwAgent.Infrastructure;
using Unify3D.LocalLink;

namespace SwAgent.Plugins
{
    /// <summary>
    /// The Claude MCP and Codex MCP plugins' link to another app (docs/plugins/local-mcp.md 7): an MCP server named "unify3d" in
    /// Claude Desktop's or Codex's config that runs Unify3D's own exe. PluginManager calls Register / Unregister only on the
    /// user's clicks (install, turn on, turn off, uninstall, Repair, update) - never at start-up and never from the agent - and
    /// Status for the Plugins screen. Files only: no SolidWorks.
    /// </summary>
    internal sealed class ClientLinks
    {
        public const string ServerName = "unify3d";

        public readonly ClientLocator Locator;
        public readonly LocalMcpDeployer Deployer;
        private readonly string _dataRoot;

        /// <param name="dataRoot">plugins\data: each plugin's backups\, link-seen.json (the add-in's) and link-written.json (ours).</param>
        public ClientLinks(ClientLocator locator, LocalMcpDeployer deployer, string dataRoot)
        {
            Locator = locator;
            Deployer = deployer;
            _dataRoot = dataRoot;
        }

        /// <summary>The program the client runs: always Unify3D's own; nothing in a marketplace can change it.</summary>
        public string Exe => Deployer.Exe;

        public static string[] Args(string client) => new[] { "--client", client };

        /// <summary>
        /// The client a plugin registers in: its marketplace entry's unify3d.registerMcpIn, honoured only in the built-in marketplace
        /// and only for a known client; else null.
        /// </summary>
        public static string ClientOf(string marketplace, JObject unify3d)
        {
            var v = unify3d?["registerMcpIn"]?.Type == JTokenType.String ? (string)unify3d["registerMcpIn"] : null;
            if (v == null || marketplace != PluginManager.BuiltInMarketplace) return null;
            return LinkProtocol.Clients.Contains(v) ? v : null;
        }

        /// <summary>The entry sets registerMcpIn (honoured or not).</summary>
        public static bool Declares(JObject unify3d) => unify3d?["registerMcpIn"] != null;

        public const string NotHonoured = "registerMcpIn is only used by the built-in Unify3D marketplace.";

        /// <summary>An entry named "unify3d" is Unify3D's when it runs Unify3D.LocalMcp.exe with --client &lt;client&gt;.</summary>
        public static bool IsOwned(string command, IEnumerable<string> args, string client)
        {
            if (string.IsNullOrWhiteSpace(command) || args == null) return false;
            string file;
            try
            {
                file = Path.GetFileName(command.Trim().Trim('"'));
            }
            catch (ArgumentException)
            {
                return false;
            }
            if (!string.Equals(file, LocalMcpDeployer.ExeName, StringComparison.OrdinalIgnoreCase)) return false;
            var list = args.ToList();
            for (var i = 0; i < list.Count - 1; i++)
            {
                if (list[i] == "--client" && list[i + 1] == client) return true;
            }
            return false;
        }

        public ClientLocation Locate(string client) => Locator.Locate(client);

        /// <summary>SHA-256 over registerMcpIn, the resolved config path, the exe path and args: a change asks the user again.</summary>
        public string LinkHash(string client)
        {
            var loc = Locate(client);
            var text = string.Join("\n", client, loc.ConfigPath ?? "", Exe, string.Join(" ", Args(client)));
            return Sha(text);
        }

        /// <summary>The trust hash of a link plugin: its manifest's (usually "") with the link's.</summary>
        public string Combine(string manifestHash, string client) => Sha((manifestHash ?? "") + "\n" + LinkHash(client));

        private static string Sha(string text)
        {
            using (var sha = SHA256.Create()) return PluginManifest.Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(text)));
        }

        /// <summary>The trust dialog's line: what file changes and what it runs.</summary>
        public string TrustLine(string client)
        {
            var loc = Locate(client);
            return $"{LinkProtocol.Label(client)}: adds the MCP server \"{ServerName}\" to {loc.ConfigPath}, which runs {Exe} {string.Join(" ", Args(client))}";
        }

        private string DataDir(string plugin) => Path.Combine(_dataRoot, plugin);

        /// <summary>The entry in the client's config now.</summary>
        public LinkEntryState Inspect(string client, string path)
        {
            string text;
            try
            {
                text = ClientConfigFile.ReadText(path, LinkProtocol.Label(client));
            }
            catch (ClientConfigException)
            {
                return LinkEntryState.Unreadable;
            }
            return client == LinkProtocol.Codex ? CodexConfig.Inspect(text) : ClaudeDesktopConfig.Inspect(text);
        }

        /// <summary>
        /// Before install or turning on: the client must be there and its "unify3d" free (or already Unify3D's). Throws the plain
        /// text otherwise; writes nothing.
        /// </summary>
        public ClientLocation Preflight(string client)
        {
            var loc = Locate(client);
            if (!loc.Found) throw new ClientConfigException(loc.Note);
            switch (Inspect(client, loc.ConfigPath))
            {
                case LinkEntryState.Conflict:
                    throw new ClientConfigException(client == LinkProtocol.Codex ? CodexConfig.ConflictText : ClaudeDesktopConfig.ConflictText);
                case LinkEntryState.Unreadable:
                    throw new ClientConfigException(UnreadableText(client, loc.ConfigPath));
            }
            return loc;
        }

        private static string UnreadableText(string client, string path)
        {
            if (client == LinkProtocol.Codex)
            {
                try
                {
                    return CodexConfig.UnreadableReason(ClientConfigFile.ReadText(path, CodexConfig.Label));
                }
                catch (ClientConfigException ex)
                {
                    return ex.Message;
                }
            }
            try
            {
                ClaudeDesktopConfig.Parse(ClientConfigFile.ReadText(path, ClaudeDesktopConfig.Label));
                return $"Unify3D couldn't read {path}.";
            }
            catch (ClientConfigException ex)
            {
                return ex.Message;
            }
        }

        /// <summary>
        /// Add (or refresh) the "unify3d" entry. addIfMissing false (an update) only refreshes an entry Unify3D owns. Copies the
        /// exe into place first. Throws ClientConfigException with the plain text when the client is missing, the name is taken,
        /// or the file can't be changed safely.
        /// </summary>
        public bool Register(string client, string plugin, bool addIfMissing)
        {
            var loc = Locate(client);
            if (!loc.Found) throw new ClientConfigException(loc.Note);
            Deployer.Ensure();
            var exe = Exe;
            bool? createdServers = null; // Claude Desktop: Unify3D added mcpServers itself (so removing the entry removes it too)
            var written = ClientConfigFile.Edit(loc.ConfigPath,
                text =>
                {
                    if (client == LinkProtocol.Codex) return CodexConfig.Add(text, exe, addIfMissing);
                    var after = ClaudeDesktopConfig.Add(text, exe, addIfMissing);
                    // A new entry sets it; a refreshed one keeps what the first install recorded.
                    createdServers = ClaudeDesktopConfig.Inspect(text) == LinkEntryState.Missing && after != text ? !ClaudeDesktopConfig.HasServers(text) : (bool?)null;
                    return after;
                },
                client == LinkProtocol.Codex ? (Action<string, string>)CodexConfig.Check : ClaudeDesktopConfig.Check,
                Path.Combine(DataDir(plugin), "backups"),
                LinkProtocol.Label(client));
            if (written)
            {
                Written(plugin, loc.ConfigPath, createdServers ?? ReadFlag(plugin, CreatedServersKey));
                Log.Info($"{plugin}: added the MCP server \"{ServerName}\" to {loc.ConfigPath}");
            }
            return written;
        }

        /// <summary>Remove Unify3D's entry and nothing else. Returns a warning for the user when that didn't happen, else null.</summary>
        public string Unregister(string client, string plugin)
        {
            var loc = Locate(client);
            if (!loc.Found) return null; // nothing to remove
            var label = LinkProtocol.Label(client);
            var conflict = false;
            var dropServers = ReadFlag(plugin, CreatedServersKey); // an mcpServers the user had stays, even when empty
            try
            {
                var written = ClientConfigFile.Edit(loc.ConfigPath,
                    text => client == LinkProtocol.Codex ? CodexConfig.Remove(text, out conflict) : ClaudeDesktopConfig.Remove(text, out conflict, dropServers),
                    client == LinkProtocol.Codex ? (Action<string, string>)CodexConfig.Check : ClaudeDesktopConfig.Check,
                    Path.Combine(DataDir(plugin), "backups"),
                    label);
                if (written)
                {
                    Written(plugin, loc.ConfigPath);
                    Log.Info($"{plugin}: removed the MCP server \"{ServerName}\" from {loc.ConfigPath}");
                }
            }
            catch (ClientConfigException ex)
            {
                return $"{ex.Message} Remove \"{ServerName}\" from {loc.ConfigPath} by hand.";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Warn($"{plugin}: removing \"{ServerName}\" from {loc.ConfigPath} failed: {ex.Message}");
                return $"Couldn't change {loc.ConfigPath}. Remove \"{ServerName}\" from it by hand.";
            }
            return conflict ? $"{label}'s MCP server \"{ServerName}\" in {loc.ConfigPath} wasn't added by Unify3D, so it was left as it is." : null;
        }

        /// <summary>link-written.json's flag: Unify3D added Claude Desktop's mcpServers object along with its entry.</summary>
        internal const string CreatedServersKey = "createdMcpServers";

        private bool ReadFlag(string plugin, string key)
        {
            try
            {
                var f = Path.Combine(DataDir(plugin), "link-written.json");
                return File.Exists(f) && (bool?)JObject.Parse(File.ReadAllText(f))[key] == true;
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException || ex is InvalidCastException || ex is ArgumentException)
            {
                return false;
            }
        }

        private void Written(string plugin, string configPath, bool createdServers = false)
        {
            try
            {
                var dir = Directory.CreateDirectory(DataDir(plugin)).FullName;
                var file = Path.Combine(dir, "link-written.json");
                var tmp = file + ".tmp";
                var o = new JObject { ["writtenAt"] = DateTime.UtcNow.ToString("o"), ["configPath"] = configPath };
                if (createdServers) o[CreatedServersKey] = true;
                File.WriteAllText(tmp, o.ToString(Formatting.Indented), new UTF8Encoding(false));
                AtomicFile.Replace(tmp, file);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Warn($"{plugin}: link-written.json not saved: {ex.Message}");
            }
        }

        private DateTime? ReadTime(string plugin, string file, string key)
        {
            try
            {
                var f = Path.Combine(DataDir(plugin), file);
                if (!File.Exists(f)) return null;
                var v = JObject.Parse(File.ReadAllText(f))[key];
                if (v == null || v.Type == JTokenType.Null) return null;
                if (v.Type == JTokenType.Date) return ((DateTime)v).ToUniversalTime();
                return DateTime.TryParse((string)v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AdjustToUniversal, out var t) ? t.ToUniversalTime() : (DateTime?)null;
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>
        /// The Plugins screen's link status (7.6): { client, label, found, configPath, entry, running, lastSeen, writtenAt, note }.
        /// Read-only: never writes the client's file.
        /// </summary>
        public JObject Status(string client, string plugin, bool enabled, DateTime? now = null)
        {
            var label = LinkProtocol.Label(client);
            var loc = Locate(client);
            var o = new JObject
            {
                ["client"] = client,
                ["label"] = label,
                ["found"] = loc.Found,
                ["configPath"] = loc.ConfigPath,
            };
            if (!loc.Found)
            {
                o["entry"] = "missing";
                o["note"] = loc.Note;
                return o;
            }
            var entry = Inspect(client, loc.ConfigPath);
            var seen = ReadTime(plugin, "link-seen.json", "lastSeen");
            var written = ReadTime(plugin, "link-written.json", "writtenAt");
            o["entry"] = entry.ToString().ToLowerInvariant();
            o["running"] = Locator.IsRunning(client);
            o["lastSeen"] = seen?.ToString("o");
            o["writtenAt"] = written?.ToString("o");
            var where = client == LinkProtocol.Codex ? "Codex's config.toml" : "Claude Desktop's settings";
            string note;
            if (entry == LinkEntryState.Conflict) note = client == LinkProtocol.Codex ? CodexConfig.ConflictText : ClaudeDesktopConfig.ConflictText;
            else if (entry == LinkEntryState.Unreadable) note = UnreadableText(client, loc.ConfigPath);
            else if (!enabled && entry == LinkEntryState.Present)
                note = $"Off, but the \"{ServerName}\" entry is still in {where} ({loc.ConfigPath}). Remove it there by hand, or turn the plugin on and off again.";
            else if (!enabled) note = $"Off: {label} can't reach Unify3D. Turn it on to add the \"{ServerName}\" entry to {where}.";
            else if (entry == LinkEntryState.Missing)
                note = $"The \"{ServerName}\" entry isn't in {where} any more (it was removed or the file was replaced). Press Repair to add it again.";
            else if (seen == null || written != null && seen < written)
                note = client == LinkProtocol.Codex
                    ? "Added to Codex. Restart Codex (or start a new CLI session) to load it. It shows Connected once Codex first uses it."
                    : "Added to Claude Desktop. Quit Claude Desktop (from the tray icon) and open it again to load it. It shows Connected once Claude Desktop first uses it.";
            else note = $"Connected: {label} last used it {Ago(seen.Value, now ?? DateTime.UtcNow)}.";
            o["note"] = note;
            return o;
        }

        private static string Ago(DateTime t, DateTime now)
        {
            var d = now - t;
            if (d.TotalMinutes < 1) return "just now";
            if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min ago";
            if (d.TotalHours < 48) return $"{(int)d.TotalHours} h ago";
            return $"{(int)d.TotalDays} days ago";
        }
    }
}
