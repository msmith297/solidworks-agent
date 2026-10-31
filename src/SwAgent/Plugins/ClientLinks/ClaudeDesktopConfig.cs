using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SolidWorksAgent.LocalLink;

namespace SwAgent.Plugins
{
    /// <summary>The state of the "solidworks-agent" entry in a client's config.</summary>
    internal enum LinkEntryState
    {
        Missing,
        Present,
        Conflict,
        Unreadable,
    }

    /// <summary>
    /// mcpServers.solidworks-agent in Claude Desktop's claude_desktop_config.json (docs/plugins/local-mcp.md 7.3). The app writes its own
    /// settings into the same file, so every other key stays as it was: dates and numbers are read as written (no date parsing,
    /// decimals), key order is kept, 2-space indent, the file's line ending. Invalid JSON is never overwritten. Pure text in, text
    /// out: ClientConfigFile does the file work.
    /// </summary>
    internal static class ClaudeDesktopConfig
    {
        public const string Label = "Claude Desktop";

        /// <summary>The entry SolidWorks Agent owns, and only this.</summary>
        public static JObject Entry(string exe) => new JObject
        {
            ["command"] = exe,
            ["args"] = new JArray("--client", LinkProtocol.ClaudeDesktop),
        };

        public static string ConflictText =>
            "Claude Desktop already has an MCP server named \"solidworks-agent\" that SolidWorks Agent didn't add. Rename or remove it in Claude Desktop's settings, then try again.";

        /// <summary>The file as JSON ({} when empty or missing). Throws the "isn't valid JSON" text otherwise.</summary>
        public static JObject Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new JObject();
            try
            {
                using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Decimal })
                {
                    var token = JToken.Load(reader);
                    while (reader.Read())
                    {
                        if (reader.TokenType != JsonToken.Comment) throw new JsonReaderException("Additional text after the JSON object.", reader.Path, reader.LineNumber, reader.LinePosition, null);
                    }
                    if (token is JObject o) return o;
                    throw new JsonReaderException("The settings file isn't a JSON object.", "", 1, 1, null);
                }
            }
            catch (JsonReaderException ex)
            {
                throw new ClientConfigException(
                    $"Claude Desktop's settings file isn't valid JSON (line {ex.LineNumber}). SolidWorks Agent won't change it. Fix it (Claude Desktop > Settings > Developer > Edit Config), then try again.");
            }
        }

        public static LinkEntryState Inspect(string text)
        {
            JObject root;
            try
            {
                root = Parse(text);
            }
            catch (ClientConfigException)
            {
                return LinkEntryState.Unreadable;
            }
            if (root["mcpServers"] != null && !(root["mcpServers"] is JObject)) return LinkEntryState.Unreadable;
            var e = root["mcpServers"]?["solidworks-agent"];
            if (e == null) return LinkEntryState.Missing;
            return Owned(e) ? LinkEntryState.Present : LinkEntryState.Conflict;
        }

        private static bool Owned(JToken e) =>
            e is JObject o && ClientLinks.IsOwned((string)o["command"], (o["args"] as JArray)?.Select(a => a.Type == JTokenType.String ? (string)a : null), LinkProtocol.ClaudeDesktop);

        /// <summary>
        /// Add (or refresh) SolidWorks Agent's entry. With addIfMissing false only an entry SolidWorks Agent owns is refreshed. Returns the text
        /// unchanged when nothing needs to change. A foreign "solidworks-agent" throws the conflict text.
        /// </summary>
        public static string Add(string text, string exe, bool addIfMissing)
        {
            var root = Parse(text);
            if (root["mcpServers"] != null && !(root["mcpServers"] is JObject))
                throw new ClientConfigException("Claude Desktop's settings file has an \"mcpServers\" value that isn't an object. SolidWorks Agent won't change it.");
            var servers = root["mcpServers"] as JObject;
            var current = servers?["solidworks-agent"];
            var entry = Entry(exe);
            if (current != null)
            {
                if (!Owned(current)) throw new ClientConfigException(ConflictText);
                if (JToken.DeepEquals(current, entry)) return text;
                servers["solidworks-agent"] = entry; // keeps its place
            }
            else
            {
                if (!addIfMissing) return text;
                if (servers == null) root.Add("mcpServers", servers = new JObject());
                servers.Add("solidworks-agent", entry);
            }
            return Serialize(root, text);
        }

        /// <summary>The file has an mcpServers key (false for invalid JSON).</summary>
        public static bool HasServers(string text)
        {
            try
            {
                return Parse(text)["mcpServers"] != null;
            }
            catch (ClientConfigException)
            {
                return false;
            }
        }

        /// <summary>
        /// Remove SolidWorks Agent's entry, and mcpServers when it was the last one and dropEmptyServers (SolidWorks Agent added mcpServers itself).
        /// A foreign one stays: conflict is set.
        /// </summary>
        public static string Remove(string text, out bool conflict, bool dropEmptyServers = true)
        {
            conflict = false;
            var root = Parse(text);
            if (!(root["mcpServers"] is JObject servers) || servers["solidworks-agent"] == null) return text;
            if (!Owned(servers["solidworks-agent"]))
            {
                conflict = true;
                return text;
            }
            servers.Remove("solidworks-agent");
            if (!servers.HasValues && dropEmptyServers) root.Remove("mcpServers");
            return Serialize(root, text);
        }

        /// <summary>Before and after must be equal except for mcpServers.solidworks-agent (JToken.DeepEquals on everything else).</summary>
        public static void Check(string before, string after)
        {
            JObject Normal(string t)
            {
                var o = Parse(t);
                if (o["mcpServers"] is JObject s)
                {
                    s.Remove("solidworks-agent");
                    if (!s.HasValues) o.Remove("mcpServers");
                }
                return o;
            }
            if (!JToken.DeepEquals(Normal(before), Normal(after))) throw new ClientConfigException("Something besides the solidworks-agent entry would change.");
        }

        /// <summary>2-space indent, the original file's line ending and final newline.</summary>
        private static string Serialize(JObject root, string original)
        {
            var sw = new StringWriter { NewLine = "\n" };
            using (var w = new JsonTextWriter(sw) { Formatting = Formatting.Indented, Indentation = 2, IndentChar = ' ', DateFormatHandling = DateFormatHandling.IsoDateFormat })
            {
                root.WriteTo(w);
            }
            var text = sw.ToString();
            var eol = ClientConfigFile.EolOf(original);
            if (eol != "\n") text = text.Replace("\n", eol);
            if (original.EndsWith("\n")) text += eol;
            return text;
        }
    }
}
