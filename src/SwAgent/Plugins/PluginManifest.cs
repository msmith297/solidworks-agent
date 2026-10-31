using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SwAgent.Plugins
{
    /// <summary>
    /// A plugin folder read the way Claude Code reads it (design 2): .claude-plugin/plugin.json, the default component
    /// folders (commands, skills, agents, hooks/hooks.json, .mcp.json) plus the extra paths plugin.json names, and the
    /// SolidWorks Agent additions (tools/ and the "solidworksAgent" object). Problems are collected in Findings rather than thrown, so a
    /// plugin with a broken hook still shows in the store. No SolidWorks types: tested headless.
    /// </summary>
    internal sealed class PluginManifest
    {
        /// <summary>Plugin and marketplace names (Claude Code's kebab-case; also safe as a folder name).</summary>
        public static readonly Regex NamePattern = new Regex("^[a-z0-9][a-z0-9-]{1,47}$");

        /// <summary>The hook events Claude Code defines (SolidWorks Agent fires the ones in design 4.5).</summary>
        public static readonly string[] HookEvents =
        {
            "PreToolUse", "PostToolUse", "UserPromptSubmit", "Stop", "SubagentStop", "SessionStart", "SessionEnd",
            "Notification", "PreCompact", "PermissionRequest",
        };

        public string Root;
        public JObject Json = new JObject();
        public string Name;
        public string Version;
        public string DisplayName;
        public string Description;
        public string Author;
        public string[] Keywords = new string[0];

        /// <summary>Normalized hooks (see PluginRuntime.Hooks); empty object when none.</summary>
        public JObject Hooks = new JObject();

        /// <summary>MCP servers by name, as written.</summary>
        public Dictionary<string, JObject> McpServers = new Dictionary<string, JObject>(StringComparer.Ordinal);

        /// <summary>userConfig options by key: { type, title, description, default, sensitive }.</summary>
        public Dictionary<string, JObject> UserConfig = new Dictionary<string, JObject>(StringComparer.Ordinal);

        /// <summary>plugin.json "solidworks-agent" (tools, category, icon, minAddInVersion, env).</summary>
        public JObject SolidWorksAgent = new JObject();

        /// <summary>Folders (and single .md files) commands, agents and skills are read from.</summary>
        public List<string> CommandPaths = new List<string>(), AgentPaths = new List<string>(), SkillPaths = new List<string>();

        /// <summary>The folder of SolidWorks Agent tools (default ./tools).</summary>
        public string ToolsPath;

        /// <summary>Problems found while reading: "error: ..." blocks a use of the part, "warning: ..." doesn't.</summary>
        public List<string> Findings = new List<string>();

        public bool HasRuntime => Hooks.Properties().Any() || McpServers.Count > 0;

        /// <summary>
        /// Its tools carry C# (macro.cs, preview.cs, ...) or it ships helpers (helpers/*.cs): code that runs inside SolidWorks, so it
        /// needs the user's approval too.
        /// </summary>
        public bool HasToolCode => ToolCodeFiles().Any();

        /// <summary>
        /// Every .cs file in its tool folders and its helpers folder (ordered): code that runs in SolidWorks when a tool runs or
        /// previews, or when an agent cell calls a helper.
        /// </summary>
        public IEnumerable<string> ToolCodeFiles() =>
            Tools().SelectMany(t => Directory.GetFiles(t.Dir, "*.cs", SearchOption.AllDirectories)).Concat(HelperFiles()).OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

        /// <summary>The folder of C# helpers for agent cells (default ./helpers, next to tools/; plugin.json solidworksAgent.helpers overrides).</summary>
        public string HelpersPath;

        /// <summary>
        /// Its helpers: helpers/&lt;ClassName&gt;.cs, each a public static class in namespace SwAgent.UserHelpers (compiled with the
        /// agent's own helpers once the plugin is enabled and approved; see Scripting/UserHelpers.cs).
        /// </summary>
        public IEnumerable<string> HelperFiles() =>
            HelpersPath != null && Directory.Exists(HelpersPath)
                ? Directory.GetFiles(HelpersPath, "*.cs", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                : Enumerable.Empty<string>();

        public static string ManifestPath(string root) => Path.Combine(root, ".claude-plugin", "plugin.json");

        /// <summary>Read a plugin folder. Never throws for content problems (see Findings); null when the folder is missing.</summary>
        public static PluginManifest Load(string root)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return null;
            var m = new PluginManifest { Root = Path.GetFullPath(root) };
            var path = ManifestPath(root);
            if (File.Exists(path))
            {
                try
                {
                    m.Json = JObject.Parse(File.ReadAllText(path));
                }
                catch (Exception ex)
                {
                    m.Findings.Add("error: .claude-plugin/plugin.json is not valid JSON: " + ex.Message);
                }
            }
            else
            {
                m.Findings.Add("error: .claude-plugin/plugin.json is missing.");
            }
            var j = m.Json;
            m.Name = Str(j["name"]) ?? Path.GetFileName(m.Root.TrimEnd('\\', '/'));
            if (!NamePattern.IsMatch(m.Name ?? "")) m.Findings.Add($"error: name \"{m.Name}\" must be 2-48 characters: lowercase letters, digits and dashes.");
            m.Version = Str(j["version"]);
            m.DisplayName = Str(j["displayName"]);
            m.Description = Str(j["description"]) ?? "";
            m.Author = j["author"] is JObject a ? Str(a["name"]) : Str(j["author"]);
            m.Keywords = (j["keywords"] as JArray)?.Select(k => (string)k).Where(k => !string.IsNullOrWhiteSpace(k)).ToArray() ?? new string[0];
            m.SolidWorksAgent = (j["solidworksAgent"] ?? j["unify3d"]) as JObject ?? new JObject();

            m.CommandPaths = ComponentPaths(m, "commands", "commands");
            m.AgentPaths = ComponentPaths(m, "agents", "agents");
            m.SkillPaths = ComponentPaths(m, "skills", "skills");
            m.ToolsPath = Str(m.SolidWorksAgent["tools"]) is string tp ? m.Resolve(tp, "solidworksAgent.tools") : Path.Combine(m.Root, "tools");
            m.HelpersPath = Str(m.SolidWorksAgent["helpers"]) is string hp ? m.Resolve(hp, "solidworksAgent.helpers") : Path.Combine(m.Root, "helpers");

            // Hooks: hooks/hooks.json, plus plugin.json "hooks" (a path, a list of paths or an inline object).
            var hookFile = Path.Combine(m.Root, "hooks", "hooks.json");
            if (File.Exists(hookFile)) m.MergeHooks(m.ReadJson(hookFile, "hooks/hooks.json"), "hooks/hooks.json");
            foreach (var spec in Many(j["hooks"]))
            {
                if (spec is JObject inline) m.MergeHooks(inline, "plugin.json hooks");
                else if (Str(spec) is string p && m.Resolve(p, "hooks") is string full)
                {
                    if (!string.Equals(full, Path.GetFullPath(hookFile), StringComparison.OrdinalIgnoreCase)) m.MergeHooks(m.ReadJson(full, p), p);
                }
            }

            // MCP servers: .mcp.json, plus plugin.json "mcpServers" (an object, a path or a list of either).
            var mcpFile = Path.Combine(m.Root, ".mcp.json");
            if (File.Exists(mcpFile)) m.MergeMcp(m.ReadJson(mcpFile, ".mcp.json"), ".mcp.json");
            foreach (var spec in Many(j["mcpServers"]))
            {
                if (spec is JObject inline) m.MergeMcp(inline, "plugin.json mcpServers");
                else if (Str(spec) is string p && m.Resolve(p, "mcpServers") is string full)
                {
                    if (!string.Equals(full, Path.GetFullPath(mcpFile), StringComparison.OrdinalIgnoreCase)) m.MergeMcp(m.ReadJson(full, p), p);
                }
            }

            if (j["userConfig"] is JObject uc)
            {
                foreach (var p in uc.Properties())
                {
                    if (!(p.Value is JObject opt))
                    {
                        m.Findings.Add($"error: userConfig.{p.Name} must be an object ({{ type, title, description, default, sensitive }}).");
                        continue;
                    }
                    if (!Regex.IsMatch(p.Name, "^[A-Za-z_][A-Za-z0-9_]*$")) m.Findings.Add($"error: userConfig key \"{p.Name}\": use letters, digits and underscores.");
                    else m.UserConfig[p.Name] = opt;
                }
            }
            else if (j["userConfig"] != null) m.Findings.Add("error: userConfig must be an object.");
            return m;
        }

        /// <summary>The paths of a component kind: the default folder plus plugin.json's extra ones (string or array).</summary>
        private static List<string> ComponentPaths(PluginManifest m, string key, string defaultFolder)
        {
            var list = new List<string> { Path.Combine(m.Root, defaultFolder) };
            foreach (var spec in Many(m.Json[key]))
            {
                if (Str(spec) is string p && m.Resolve(p, key) is string full && !list.Contains(full, StringComparer.OrdinalIgnoreCase)) list.Add(full);
            }
            return list;
        }

        /// <summary>A path from plugin.json, relative to the plugin root and inside it; null (with a finding) otherwise.</summary>
        public string Resolve(string relative, string what)
        {
            if (string.IsNullOrWhiteSpace(relative)) return null;
            var r = relative.Replace("${CLAUDE_PLUGIN_ROOT}", ".").Replace("${SOLIDWORKS_AGENT_PLUGIN_ROOT}", ".");
            if (Path.IsPathRooted(r))
            {
                Findings.Add($"error: {what}: \"{relative}\" must be a relative path inside the plugin.");
                return null;
            }
            var full = Path.GetFullPath(Path.Combine(Root, r));
            if (!IsInside(Root, full))
            {
                Findings.Add($"error: {what}: \"{relative}\" points outside the plugin folder.");
                return null;
            }
            return full;
        }

        /// <summary>The path is the folder itself or inside it (after normalising).</summary>
        public static bool IsInside(string folder, string path)
        {
            var f = Path.GetFullPath(folder).TrimEnd('\\', '/');
            var p = Path.GetFullPath(path).TrimEnd('\\', '/');
            return string.Equals(f, p, StringComparison.OrdinalIgnoreCase) || p.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private JObject ReadJson(string path, string label)
        {
            try
            {
                return File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : Missing(label);
            }
            catch (Exception ex)
            {
                Findings.Add($"error: {label} is not valid JSON: {ex.Message}");
                return null;
            }
        }

        private JObject Missing(string label)
        {
            Findings.Add($"error: {label} does not exist.");
            return null;
        }

        /// <summary>Add a hooks file's events: { "hooks": { Event: [...] } } (the file form) or { Event: [...] } (inline).</summary>
        private void MergeHooks(JObject source, string label)
        {
            if (source == null) return;
            var events = source["hooks"] as JObject ?? source;
            foreach (var ev in events.Properties())
            {
                if (ev.Name == "description") continue;
                if (!HookEvents.Contains(ev.Name))
                {
                    Findings.Add($"warning: {label}: unknown hook event \"{ev.Name}\" (ignored).");
                    continue;
                }
                if (!(ev.Value is JArray groups))
                {
                    Findings.Add($"error: {label}: {ev.Name} must be a list of {{ matcher, hooks }} groups.");
                    continue;
                }
                var target = Hooks[ev.Name] as JArray ?? (JArray)(Hooks[ev.Name] = new JArray());
                foreach (var g in groups)
                {
                    if (!(g is JObject group) || !(group["hooks"] is JArray hooks))
                    {
                        Findings.Add($"error: {label}: a {ev.Name} entry has no \"hooks\" list.");
                        continue;
                    }
                    var clean = new JArray();
                    foreach (var h in hooks)
                    {
                        var hook = h as JObject;
                        var type = Str(hook?["type"]) ?? "command";
                        if (hook == null || type != "command" || string.IsNullOrWhiteSpace(Str(hook["command"])))
                        {
                            Findings.Add(type != "command"
                                ? $"warning: {label}: {ev.Name} hook of type \"{type}\" is not supported (only \"command\"); it is skipped."
                                : $"error: {label}: a {ev.Name} hook has no command.");
                            continue;
                        }
                        var n = new JObject { ["type"] = "command", ["command"] = Str(hook["command"]) };
                        if (hook["timeout"] != null)
                        {
                            if (hook["timeout"].Type == JTokenType.Integer || hook["timeout"].Type == JTokenType.Float) n["timeout"] = (double)hook["timeout"];
                            else Findings.Add($"error: {label}: {ev.Name} hook timeout must be a number of seconds.");
                        }
                        if ((bool?)Bool(hook["async"]) == true) n["async"] = true;
                        if ((bool?)Bool(hook["asyncRewake"]) == true) n["asyncRewake"] = true;
                        if (Str(hook["rewakeMessage"]) is string rm) n["rewakeMessage"] = rm;
                        clean.Add(n);
                    }
                    if (clean.Count == 0) continue;
                    target.Add(new JObject { ["matcher"] = Str(group["matcher"]) ?? "", ["hooks"] = clean });
                }
                if (target.Count == 0) Hooks.Remove(ev.Name);
            }
        }

        /// <summary>Add MCP servers: { "mcpServers": { name: {...} } } or { name: {...} }.</summary>
        private void MergeMcp(JObject source, string label)
        {
            if (source == null) return;
            var servers = source["mcpServers"] as JObject ?? source;
            foreach (var s in servers.Properties())
            {
                if (!(s.Value is JObject server))
                {
                    Findings.Add($"error: {label}: MCP server \"{s.Name}\" must be an object.");
                    continue;
                }
                if (!Regex.IsMatch(s.Name, "^[A-Za-z0-9_-]{1,64}$"))
                {
                    Findings.Add($"error: {label}: MCP server name \"{s.Name}\": use letters, digits, '-' and '_'.");
                    continue;
                }
                var type = Str(server["type"]) ?? (server["url"] != null ? "http" : "stdio");
                if (type == "stdio" && string.IsNullOrWhiteSpace(Str(server["command"])))
                {
                    Findings.Add($"error: {label}: MCP server \"{s.Name}\" has no command.");
                    continue;
                }
                if (type != "stdio" && string.IsNullOrWhiteSpace(Str(server["url"])))
                {
                    Findings.Add($"error: {label}: MCP server \"{s.Name}\" ({type}) has no url.");
                    continue;
                }
                if (type != "stdio" && type != "http" && type != "sse") Findings.Add($"error: {label}: MCP server \"{s.Name}\": type \"{type}\" (use stdio, http or sse).");
                if (server["headers"] != null && !(server["headers"] is JObject)) Findings.Add($"error: {label}: MCP server \"{s.Name}\": headers must be an object.");
                if (server["args"] != null && !(server["args"] is JArray)) Findings.Add($"error: {label}: MCP server \"{s.Name}\": args must be a list.");
                if (server["env"] != null && !(server["env"] is JObject)) Findings.Add($"error: {label}: MCP server \"{s.Name}\": env must be an object.");
                if (McpServers.ContainsKey(s.Name)) Findings.Add($"warning: {label}: MCP server \"{s.Name}\" is defined twice; the later one wins.");
                McpServers[s.Name] = (JObject)server.DeepClone();
            }
        }

        /// <summary>
        /// The trust hash (design critique 16): what the user approves before it runs - the hooks, the MCP servers and, with
        /// toolCode, the C# of its tools (by content). "" when the plugin has none of them. extraFiles (a local plugin's other
        /// files, which its hooks may run) are hashed in too. A plugin without tool code hashes exactly as before tool code
        /// counted, so approvals given then still hold.
        /// </summary>
        public string TrustHash(IEnumerable<string> extraFiles = null, bool toolCode = true)
        {
            var code = toolCode ? ToolCodeFiles().ToList() : new List<string>();
            if (!HasRuntime && code.Count == 0) return "";
            var sb = new StringBuilder();
            sb.Append(Canonical(Hooks)).Append('\n');
            sb.Append(Canonical(new JObject(McpServers.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => new JProperty(k.Key, k.Value))))).Append('\n');
            foreach (var f in (extraFiles ?? Enumerable.Empty<string>()).OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) AppendFile(sb, f);
            if (code.Count > 0)
            {
                sb.Append("tool code\n");
                foreach (var f in code) AppendFile(sb, f);
            }
            using (var sha = SHA256.Create())
                return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())));
        }

        private void AppendFile(StringBuilder sb, string f)
        {
            sb.Append(f.Substring(Root.Length).Replace('\\', '/')).Append(':');
            try
            {
                using (var sha = SHA256.Create())
                    sb.Append(Hex(sha.ComputeHash(File.ReadAllBytes(f))));
            }
            catch (IOException)
            {
                sb.Append('?');
            }
            sb.Append('\n');
        }

        /// <summary>The tools whose C# runs in SolidWorks, "plugin:tool (macro.cs, preview.cs)" (for the trust dialog).</summary>
        public IEnumerable<string> ToolCodeSummary() =>
            Tools().Select(t => (t.Name, Files: Directory.GetFiles(t.Dir, "*.cs", SearchOption.AllDirectories)
                    .Select(f => f.Substring(t.Dir.Length).TrimStart('\\', '/').Replace('\\', '/')).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()))
                .Where(t => t.Files.Count > 0)
                .Select(t => $"{Name}:{t.Name} ({string.Join(", ", t.Files)})")
                .Concat(HelperFiles().Any() ? new[] { $"{Name} helpers for agent code ({string.Join(", ", HelperFiles().Select(Path.GetFileName))})" } : new string[0]);

        /// <summary>JSON with object keys sorted, so the same content always hashes the same.</summary>
        internal static string Canonical(JToken t)
        {
            switch (t)
            {
                case JObject o:
                    return "{" + string.Join(",", o.Properties().OrderBy(p => p.Name, StringComparer.Ordinal)
                        .Select(p => JsonConvert.ToString(p.Name) + ":" + Canonical(p.Value))) + "}";
                case JArray a:
                    return "[" + string.Join(",", a.Select(Canonical)) + "]";
                default:
                    return t?.ToString(Formatting.None) ?? "null";
            }
        }

        internal static string Hex(byte[] bytes) => string.Concat(bytes.Select(b => b.ToString("x2")));

        /// <summary>Every hook command line, "Event: command" (for the trust dialog).</summary>
        public IEnumerable<(string Event, string Command)> HookCommands() =>
            Hooks.Properties().SelectMany(ev => ((JArray)ev.Value).SelectMany(g => (JArray)g["hooks"]).Select(h => (ev.Name, (string)h["command"])));

        /// <summary>
        /// The tool folders under ToolsPath that have a TOOL.md: (tool name, folder). A folder's profiles\*.json are profiles that
        /// ship with the tool, locked (ToolProfiles.ShippedDir); they are data, not code, so they don't count in the trust hash.
        /// </summary>
        public IEnumerable<(string Name, string Dir)> Tools()
        {
            if (ToolsPath == null || !Directory.Exists(ToolsPath)) yield break;
            foreach (var d in Directory.GetDirectories(ToolsPath).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(d).ToLowerInvariant();
                if (NamePattern.IsMatch(name) && File.Exists(Path.Combine(d, "TOOL.md"))) yield return (name, d);
            }
        }

        internal static IEnumerable<JToken> Many(JToken t) => t == null || t.Type == JTokenType.Null ? Enumerable.Empty<JToken>() : t is JArray a ? a : new[] { t };

        internal static string Str(JToken t) => t != null && (t.Type == JTokenType.String || t.Type == JTokenType.Integer || t.Type == JTokenType.Float) && !string.IsNullOrWhiteSpace((string)t) ? ((string)t).Trim() : null;

        private static bool? Bool(JToken t)
        {
            if (t == null) return null;
            if (t.Type == JTokenType.Boolean) return (bool)t;
            return t.Type == JTokenType.String ? (bool?)string.Equals((string)t, "true", StringComparison.OrdinalIgnoreCase) : null;
        }
    }
}
