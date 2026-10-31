using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwAgent.Infrastructure;
using SwAgent.Tools;

namespace SwAgent.Plugins
{
    /// <summary>
    /// The agent's plugin tools: skills (use_skill, read_plugin_file, design 4.3) and the plugin creator's tools (design 5.3).
    /// They work on files only - never on the model - and never grant trust: a plugin the agent writes or enables with hooks
    /// or MCP servers runs nothing until the user approves it in the Plugins screen.
    /// </summary>
    public sealed class PluginTools
    {
        /// <summary>The add-in's plugins; tests use their own.</summary>
        internal Func<PluginManager> Manager = () => PluginManager.Instance;

        private const int MaxRead = 200000;

        public sealed class NoInput
        {
        }

        public sealed class SkillInput
        {
            [ToolParam("The skill: \"plugin:skill\" from the skills index (or just \"skill\" when only one plugin has it).", Required = true)]
            public string Name { get; set; }
        }

        public sealed class FileInput
        {
            [ToolParam("The plugin's name, e.g. \"agent-bridge\".", Required = true)]
            public string Plugin { get; set; }

            [ToolParam("The file's path relative to the plugin folder, e.g. \"skills/connect/reference.md\".", Required = true)]
            public string Path { get; set; }
        }

        public sealed class PluginInput
        {
            [ToolParam("The plugin's name (or name@marketplace).", Required = true)]
            public string Plugin { get; set; }
        }

        public sealed class CreateInput
        {
            [ToolParam("Id: 2-48 lowercase letters, digits and dashes, e.g. \"mold-shop-tools\".", Required = true)]
            public string Name { get; set; }

            [ToolParam("One or two sentences: what the plugin gives the user.", Required = true)]
            public string Description { get; set; }

            [ToolParam("Name shown in the store, e.g. \"Mold Shop Tools\". Default: the id.")]
            public string DisplayName { get; set; }

            [ToolParam("Store category, e.g. \"Molding & Toolmaking\" or \"Collaboration\".")]
            public string Category { get; set; }
        }

        public sealed class WriteInput
        {
            [ToolParam("The local plugin's name (made with plugin_create).", Required = true)]
            public string Plugin { get; set; }

            [ToolParam("Path relative to the plugin folder: e.g. \"commands/check-mold.md\", \"skills/mold-rules/SKILL.md\", \"agents/reviewer.md\", " +
                       "\"hooks/hooks.json\", \".mcp.json\", \"README.md\", \"tools/<tool>/TOOL.md\". Markdown components need front matter " +
                       "(--- description: ... ---); JSON must be valid.", Required = true)]
            public string Path { get; set; }

            [ToolParam("The whole file content.", Required = true)]
            public string Content { get; set; }
        }

        public sealed class AddToolInput
        {
            [ToolParam("The local plugin's name.", Required = true)]
            public string Plugin { get; set; }

            [ToolParam("One of the user's own tools or workflows (list_user_tools), e.g. \"flash-grooves\".", Required = true)]
            public string Tool { get; set; }

            [ToolParam("true moves the tool into the plugin (it is then only \"plugin:tool\"); default false copies it.")]
            public bool? Move { get; set; }

            [ToolParam("true ships the user's saved profiles of the tool with it (locked for whoever installs the plugin). Ask first: they " +
                       "may hold the shop's own values.")]
            public bool? Profiles { get; set; }
        }

        public sealed class EnableInput
        {
            [ToolParam("The plugin's name (or name@marketplace).", Required = true)]
            public string Plugin { get; set; }

            [ToolParam("true to enable, false to disable.", Required = true)]
            public bool Enabled { get; set; }
        }

        [AgentTool("use_skill",
            "Load a skill from an enabled plugin (the skills index in your instructions lists them): its SKILL.md instructions and the list of its " +
            "other files. Use it when a task matches a skill's description, before doing the task. Read the skill's other files with read_plugin_file.")]
        public ToolResult UseSkill(ToolContext ctx, SkillInput input)
        {
            var pm = Manager();
            var skill = SkillIndex.Resolve(pm, input.Name);
            var plugin = pm.Find(skill.Plugin);
            var text = File.ReadAllText(Path.Combine(skill.Dir, "SKILL.md"));
            if (plugin != null) text = pm.BuildRuntime(plugin).Expand(text);
            var root = plugin?.Root ?? skill.Dir;
            var others = Directory.GetFiles(skill.Dir, "*", SearchOption.AllDirectories)
                .Where(f => !string.Equals(Path.GetFileName(f), "SKILL.md", StringComparison.OrdinalIgnoreCase) || Path.GetDirectoryName(f) != skill.Dir)
                .Select(f => f.Substring(root.Length + 1).Replace('\\', '/')).Take(100).ToList();
            var sb = new StringBuilder();
            sb.AppendLine($"Skill {skill.Id} (plugin \"{skill.Plugin}\")").AppendLine().Append(text.Trim()).AppendLine();
            // The user's standing instructions for an installed plugin's skill live outside it (docs/agent-memory.md).
            var notes = plugin?.Local == true ? null : Memory.MemoryStore.Instance.SkillOverlayText(skill.Id);
            if (notes != null) sb.AppendLine().AppendLine("## " + notes);
            if (others.Count > 0)
                sb.AppendLine().AppendLine("---").AppendLine($"Other files of this skill (read_plugin_file with plugin \"{skill.Plugin}\"): {string.Join(", ", others)}");
            return ToolResult.Ok(sb.ToString());
        }

        [AgentTool("read_plugin_file", "Read one file of an installed plugin (a skill's reference file, a command, a tool's file), by its path inside the plugin.")]
        public ToolResult ReadFile(ToolContext ctx, FileInput input)
        {
            var p = Plugin(input.Plugin);
            var path = UserTools.SafePath(p.Root, input.Path);
            if (!File.Exists(path)) return ToolResult.Error($"No file '{input.Path}' in plugin '{p.Name}'.");
            var info = new FileInfo(path);
            if (info.Length > MaxRead) return ToolResult.Error($"'{input.Path}' is {info.Length / 1024} KB; files over {MaxRead / 1000} KB aren't read.");
            var bytes = File.ReadAllBytes(path);
            if (bytes.Take(8000).Contains((byte)0)) return ToolResult.Error($"'{input.Path}' is a binary file.");
            return ToolResult.Ok(new UTF8Encoding(false).GetString(bytes).TrimStart('\uFEFF'));
        }

        public sealed class BridgeFileInput
        {
            [ToolParam("The attachment's path as the message gives it (under .agent-bridge\\inbox\\<message id>\\), or relative to that inbox folder, e.g. \"4114\\notes.md\".", Required = true)]
            public string Path { get; set; }
        }

        /// <summary>agent-bridge's inbox: where its files tool saves attachments (AGENT_BRIDGE_HOME, else %USERPROFILE%\.agent-bridge).</summary>
        internal static string BridgeInbox
        {
            get
            {
                var home = Environment.GetEnvironmentVariable("AGENT_BRIDGE_HOME");
                if (string.IsNullOrWhiteSpace(home)) home = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agent-bridge");
                return System.IO.Path.Combine(home, "inbox");
            }
        }

        [AgentTool("read_bridge_file", "Read a file another agent attached to an agent-bridge message (saved under the .agent-bridge\\inbox folder). Read-only and never asks the user, so use it instead of C# to read attachments. Text files and images; other files only by name and size.")]
        public ToolResult ReadBridgeFile(ToolContext ctx, BridgeFileInput input)
        {
            var inbox = System.IO.Path.GetFullPath(BridgeInbox).TrimEnd('\\') + "\\";
            string path;
            try
            {
                path = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(input.Path ?? "") ? input.Path : System.IO.Path.Combine(inbox, input.Path ?? ""));
            }
            catch (Exception ex)
            {
                return ToolResult.Error($"'{input.Path}' isn't a valid path: {ex.Message}");
            }
            // Only agent-bridge's own attachments: a peer must not be able to have any other file read without the user.
            if (!path.StartsWith(inbox, StringComparison.OrdinalIgnoreCase)) return ToolResult.Error($"Only files in the agent-bridge inbox ({inbox}) can be read this way.");
            if (!File.Exists(path)) return ToolResult.Error($"No file '{path}'. Download attachments first with agent-bridge's files tool.");
            var info = new FileInfo(path);
            var ext = info.Extension.ToLowerInvariant();
            if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".gif" || ext == ".webp")
            {
                if (info.Length > 5_000_000) return ToolResult.Error($"'{info.Name}' is {info.Length / 1024} KB; images over 5 MB aren't read.");
                var media = ext == ".png" ? "image/png" : ext == ".gif" ? "image/gif" : ext == ".webp" ? "image/webp" : "image/jpeg";
                return new ToolResult().Text($"{info.Name} ({info.Length / 1024} KB)").Image(Convert.ToBase64String(File.ReadAllBytes(path)), media);
            }
            if (info.Length > MaxRead) return ToolResult.Error($"'{info.Name}' is {info.Length / 1024} KB; text files over {MaxRead / 1000} KB aren't read.");
            var bytes = File.ReadAllBytes(path);
            if (bytes.Take(8000).Contains((byte)0)) return ToolResult.Ok($"'{info.Name}' is a binary file ({info.Length / 1024} KB); it can't be shown as text.");
            return ToolResult.Ok(new UTF8Encoding(false).GetString(bytes).TrimStart('﻿'));
        }

        [AgentTool("list_plugins", "List the installed plugins (enabled or not, with what they contain) and how many more the marketplaces offer.")]
        public ToolResult List(ToolContext ctx, NoInput input)
        {
            var pm = Manager();
            var sb = new StringBuilder();
            var installed = pm.Installed();
            if (installed.Count == 0) sb.AppendLine("No plugins are installed.");
            foreach (var p in installed)
            {
                var j = pm.PluginJson(p);
                var c = (JObject)j["components"];
                var parts = c.Properties().Where(x => (int)x.Value > 0).Select(x => $"{x.Value} {x.Name}");
                var trust = (bool)j["trust"]["approved"] ? "" : " - hooks/MCP not approved by the user yet";
                sb.AppendLine($"- {p.Name} ({p.Id}, {(p.Enabled ? "enabled" : "disabled")}{(p.Local ? ", local: editable" : "")}{trust}): {p.Manifest.Description} [{string.Join(", ", parts)}]");
            }
            var available = pm.Marketplaces().Where(m => m.Manifest != null).SelectMany(m => m.Manifest.Plugins.Select(e => e.Name + "@" + m.Name))
                .Where(id => installed.All(p => p.Id != id)).ToList();
            if (available.Count > 0) sb.AppendLine($"Not installed, in the marketplaces: {string.Join(", ", available)} (the user installs them from the Plugins screen).");
            return ToolResult.Ok(sb.ToString().TrimEnd());
        }

        [AgentTool("read_plugin", "Show an installed plugin: its plugin.json, its components (tools, commands, skills, agents, hooks, MCP servers) and its files.")]
        public ToolResult Read(ToolContext ctx, PluginInput input)
        {
            var pm = Manager();
            var p = Plugin(input.Plugin);
            var m = p.Manifest;
            var sb = new StringBuilder();
            sb.AppendLine($"Plugin {p.Id} ({(p.Enabled ? "enabled" : "disabled")}, {(p.Local ? "local: editable with plugin_write_file" : "installed: read-only")}), folder {p.Root}");
            sb.AppendLine().AppendLine("plugin.json:").AppendLine(m.Json.ToString(Formatting.Indented));
            void Section(string title, IEnumerable<string> lines)
            {
                var l = lines.ToList();
                if (l.Count == 0) return;
                sb.AppendLine().AppendLine(title + ":");
                foreach (var x in l) sb.AppendLine("- " + x);
            }
            Section("Tools", m.Tools().Select(t => $"{p.Name}:{t.Name}"));
            Section("Commands", CommandIndex.For(m).Select(c => $"/{c.Id} - {c.Description}"));
            Section("Skills", SkillIndex.For(m, PluginManager.HiddenSkills(p)).Select(s => $"{s.Id} - {s.Description}"));
            Section("Agents", AgentIndex.For(m).Select(a => $"{a.Id} - {a.Description}"));
            Section("Hooks", m.HookCommands().Select(h => $"{h.Event}: {h.Command}"));
            Section("MCP servers", m.McpServers.Select(kv => $"{kv.Key}: {kv.Value.ToString(Formatting.None)}"));
            Section("Problems", m.Findings);
            var files = Directory.Exists(p.Root)
                ? Directory.GetFiles(p.Root, "*", SearchOption.AllDirectories).Select(f => f.Substring(p.Root.Length + 1).Replace('\\', '/'))
                    .Where(f => !f.StartsWith(".git/")).ToList()
                : new List<string>();
            Section("Files" + (files.Count > 200 ? $" (first 200 of {files.Count})" : ""), files.Take(200));
            if (!(bool)pm.PluginJson(p)["trust"]["approved"]) sb.AppendLine().AppendLine("Its hooks/MCP servers are not approved: they don't run until the user approves them in Plugins.");
            return ToolResult.Ok(sb.ToString().TrimEnd());
        }

        [AgentTool("plugin_create",
            "Create one of the user's own plugins (plugins\\local\\<name>, the \"local\" marketplace) with its .claude-plugin/plugin.json. Then add files with " +
            "plugin_write_file and the user's tools with plugin_add_tool, check it with plugin_validate and enable it with plugin_enable. Only when the user asked for a plugin.")]
        public ToolResult Create(ToolContext ctx, CreateInput input)
        {
            var pm = Manager();
            var name = (input.Name ?? "").Trim().ToLowerInvariant();
            if (!PluginManifest.NamePattern.IsMatch(name)) return ToolResult.Error("Name must be 2-48 characters: lowercase letters, digits and dashes.");
            var existing = pm.Find(name);
            if (existing != null) return ToolResult.Error($"A plugin named '{name}' already exists ({existing.Id}). Choose another name or edit that one.");
            var dir = Path.Combine(pm.LocalDir, name);
            if (Directory.Exists(dir)) return ToolResult.Error($"plugins\\local\\{name} already exists.");
            var json = new JObject
            {
                ["name"] = name,
                ["version"] = "0.1.0",
                ["displayName"] = string.IsNullOrWhiteSpace(input.DisplayName) ? name : OneLine(input.DisplayName),
                ["description"] = OneLine(input.Description),
            };
            var author = Environment.UserName;
            if (!string.IsNullOrWhiteSpace(author)) json["author"] = new JObject { ["name"] = author };
            if (!string.IsNullOrWhiteSpace(input.Category)) json["solidworksAgent"] = new JObject { ["category"] = UserTools.CleanCategory(input.Category) };
            Directory.CreateDirectory(Path.Combine(dir, ".claude-plugin"));
            File.WriteAllText(PluginManifest.ManifestPath(dir), json.ToString(Formatting.Indented) + "\n", new UTF8Encoding(false));
            pm.Invalidate();
            Log.Info($"Created local plugin '{name}'");
            return ToolResult.Ok($"Created plugin '{name}' in {dir} (disabled). Add its parts with plugin_write_file / plugin_add_tool, then plugin_validate and plugin_enable.");
        }

        [AgentTool("plugin_write_file",
            "Create or replace one file in one of the user's own (local) plugins: a command, skill, agent, README, hooks/hooks.json, .mcp.json, plugin.json or a " +
            "tool file. Installed plugins are read-only. Explain to the user what hooks and MCP servers do before adding them: the user must approve them before they run.")]
        public ToolResult WriteFile(ToolContext ctx, WriteInput input)
        {
            var pm = Manager();
            var p = LocalPlugin(input.Plugin);
            var rel = (input.Path ?? "").Replace('\\', '/').Trim();
            var path = UserTools.SafePath(p.Root, rel);
            if (rel.Split('/').Any(s => s.Equals(".git", StringComparison.OrdinalIgnoreCase))) return ToolResult.Error("Files under .git can't be written.");
            var content = (input.Content ?? "").Replace("\r\n", "\n");
            var problems = CheckContent(rel, content);
            if (problems.Count > 0) return ToolResult.Error($"Not written - fix these first:\n- {string.Join("\n- ", problems)}");
            var before = pm.CurrentHash(p);
            var wasTrusted = before == "" || before == p.Record.TrustedHash;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content, new UTF8Encoding(false));
            pm.Invalidate();
            if (rel.StartsWith(ToolsFolder(p) + "/", StringComparison.OrdinalIgnoreCase)) UserTools.NotifyChanged();
            var after = pm.CurrentHash(pm.Find(p.Id) ?? p);
            var note = "";
            if (after != "" && after != before)
                note = wasTrusted && before != ""
                    ? " This changed what the plugin runs: its hooks and MCP servers are stopped until the user approves them again in Plugins."
                    : " The plugin has hooks or MCP servers: they run only after the user approves them in Plugins (they will be asked when it is enabled).";
            return ToolResult.Ok($"Wrote {rel} in plugin '{p.Name}'.{note}");
        }

        [AgentTool("plugin_add_tool", "Copy (or move) one of the user's own tools or workflows into one of their local plugins, so it ships with the plugin as \"plugin:tool\".")]
        public ToolResult AddTool(ToolContext ctx, AddToolInput input)
        {
            var pm = Manager();
            var p = LocalPlugin(input.Plugin);
            var tool = (input.Tool ?? "").Trim().ToLowerInvariant();
            if (!PluginManifest.NamePattern.IsMatch(tool)) return ToolResult.Error($"'{input.Tool}' is not a tool name (list_user_tools shows them).");
            var from = Path.Combine(UserTools.Root, tool);
            if (UserTools.ReservedNames.Contains(tool) || !File.Exists(Path.Combine(from, "TOOL.md")))
                return ToolResult.Error(UserTools.IsBuiltIn(tool) ? $"'{tool}' is a built-in tool; only the user's own tools can go into a plugin." : $"The user has no tool '{tool}'.");
            var toolsDir = p.Manifest.ToolsPath ?? Path.Combine(p.Root, "tools");
            if (!PluginManifest.IsInside(p.Root, toolsDir)) return ToolResult.Error("The plugin's solidworksAgent.tools folder is outside the plugin.");
            var to = Path.Combine(toolsDir, tool);
            if (Directory.Exists(to)) return ToolResult.Error($"The plugin already has a tool '{tool}'. Delete it with edit_user_tool / plugin_write_file first, or use another name.");
            Fetcher.CopyDirectory(from, to);
            var id = p.Name + ":" + tool;
            ToolProfiles.Retarget(to, id); // profiles in the tool folder (e.g. a fork's) name the plugin's tool
            var mine = ToolProfiles.List(tool).Where(x => !x.Locked).Select(x => x.Name).ToList();
            var shipped = input.Profiles == true ? ToolProfiles.Ship(tool, to, id) : new List<string>();
            // Moved: the user keeps their settings (the default, the last used) and the profiles that didn't ship for the plugin's
            // tool; a default naming a shipped profile now opens that (locked) one.
            if (input.Move == true) ToolProfiles.CopyAll(tool, id, except: shipped);
            if (input.Move == true) UserTools.Delete(tool);
            pm.Invalidate();
            UserTools.NotifyChanged();
            var note = shipped.Count > 0 ? $" It ships the profile{(shipped.Count == 1 ? "" : "s")} {string.Join(", ", shipped)}, locked for whoever installs it."
                : mine.Count > 0 && input.Profiles == null ? $" The user has saved profiles of it ({string.Join(", ", mine)}): profiles: true ships them; ask first." : "";
            return ToolResult.Ok($"{(input.Move == true ? "Moved" : "Copied")} tool '{tool}' into plugin '{p.Name}'. When the plugin is enabled it is \"{id}\"" +
                                 (input.Move == true ? "." : "; the user's own copy stays too.") + note);
        }

        [AgentTool("plugin_validate", "Check a plugin: plugin.json, names, front matter of commands, skills and agents, hooks and MCP server definitions, tools. Returns the problems found.")]
        public ToolResult Validate(ToolContext ctx, PluginInput input)
        {
            var p = Plugin(input.Plugin);
            var findings = Validate(p);
            return ToolResult.Ok(findings.Count == 0
                ? $"Plugin '{p.Name}': no problems found."
                : $"Plugin '{p.Name}': {findings.Count(f => f.StartsWith("error"))} error(s), {findings.Count(f => f.StartsWith("warning"))} warning(s):\n- " + string.Join("\n- ", findings));
        }

        [AgentTool("plugin_enable",
            "Enable or disable an installed plugin (only when the user asked). Enabling never approves its hooks or MCP servers: the user does that in the Plugins screen.")]
        public ToolResult Enable(ToolContext ctx, EnableInput input)
        {
            var pm = Manager();
            var p = Plugin(input.Plugin);
            if (input.Enabled)
            {
                var errors = Validate(p).Where(f => f.StartsWith("error")).ToList();
                if (errors.Count > 0) return ToolResult.Error($"Not enabled - '{p.Name}' has errors:\n- " + string.Join("\n- ", errors));
            }
            var r = pm.Enable(p.Id, input.Enabled, trustHash: null, fromAgent: true);
            UserTools.NotifyChanged();
            return ToolResult.Ok($"Plugin '{p.Name}' is {(input.Enabled ? "enabled" : "disabled")}." + (r.Warnings.Count > 0 ? " " + string.Join(" ", r.Warnings) : ""));
        }

        [AgentTool("plugin_package",
            "Package one of the user's local plugins to share it: a zip and a copy in Documents\\SolidWorks Agent Plugins, with a marketplace.json there, so that folder can be " +
            "added as a marketplace (or shared) on another PC.")]
        public ToolResult Package(ToolContext ctx, PluginInput input)
        {
            var p = LocalPlugin(input.Plugin);
            var errors = Validate(p).Where(f => f.StartsWith("error")).ToList();
            if (errors.Count > 0) return ToolResult.Error($"Not packaged - fix these first:\n- " + string.Join("\n- ", errors));
            var (zip, folder) = Package(p, Paths.PackagesDir);
            return ToolResult.Ok($"Packaged '{p.Name}': {zip}. The folder {folder} is now a marketplace: on another PC, add it (or a copy, or a git repository of it) " +
                                 "under Plugins > Marketplaces and install from there.");
        }

        // ---------------------------------------------------------------- helpers (internal: tested headless)

        private PluginManager.LoadedPlugin Plugin(string name) =>
            Manager().Find(name) ?? throw new ToolException($"No installed plugin '{name}'. list_plugins shows them.");

        private PluginManager.LoadedPlugin LocalPlugin(string name)
        {
            var p = Plugin(name);
            if (!p.Local) throw new ToolException($"'{p.Name}' is an installed plugin ({p.Id}) and read-only. Only the user's own plugins (plugin_create) can be changed.");
            return p;
        }

        private static string ToolsFolder(PluginManager.LoadedPlugin p) =>
            (p.Manifest.ToolsPath ?? Path.Combine(p.Root, "tools")).Substring(p.Root.Length).Trim('\\', '/').Replace('\\', '/');

        /// <summary>Checks before a write: JSON parses; component Markdown has front matter that parses (with a description).</summary>
        internal static List<string> CheckContent(string rel, string content)
        {
            var problems = new List<string>();
            if (rel.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var token = JToken.Parse(content);
                    if (!(token is JObject)) problems.Add("The file must hold a JSON object.");
                    else if (rel.Equals(".claude-plugin/plugin.json", StringComparison.OrdinalIgnoreCase) && !PluginManifest.NamePattern.IsMatch((string)token["name"] ?? ""))
                        problems.Add("plugin.json needs \"name\": 2-48 lowercase letters, digits and dashes.");
                }
                catch (JsonException ex)
                {
                    problems.Add("Not valid JSON: " + ex.Message);
                }
            }
            else if (IsComponentMarkdown(rel))
            {
                var fm = FrontMatter.Parse(content);
                problems.AddRange(fm.Errors);
                if (!fm.Present) problems.Add("Start the file with front matter: a '---' line, \"description: ...\" (and the other keys), then '---'.");
                else if (fm.Get("description") == null && !rel.EndsWith("TOOL.md", StringComparison.OrdinalIgnoreCase)) problems.Add("The front matter needs a description.");
            }
            return problems;
        }

        private static bool IsComponentMarkdown(string rel)
        {
            var r = rel.Replace('\\', '/');
            if (!r.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return false;
            return r.StartsWith("commands/") || r.StartsWith("agents/") || r.EndsWith("/SKILL.md", StringComparison.OrdinalIgnoreCase) && r.StartsWith("skills/") ||
                   r.EndsWith("/TOOL.md", StringComparison.OrdinalIgnoreCase);
        }

        private static readonly string[] OptionTypes = { "string", "number", "boolean", "directory", "file" };

        /// <summary>plugin_validate's findings ("error: ..." / "warning: ..."), for any installed plugin.</summary>
        /// <summary>What in a hook command only bash understands (cmd.exe, the fallback without Git for Windows, would fail).</summary>
        internal static List<string> BashOnly(string command)
        {
            var found = new List<string>();
            var c = command ?? "";
            if (Regex.IsMatch(c, @"\$[A-Za-z_]")) found.Add("$NAME without braces");
            if (Regex.IsMatch(c, @"\$\{[^}]*:-")) found.Add("${NAME:-default}");
            if (c.Contains("$(") || c.Contains("`")) found.Add("command substitution");
            if (c.Contains("/dev/null")) found.Add("/dev/null");
            if (Regex.IsMatch(c, @"\.sh\b", RegexOptions.IgnoreCase)) found.Add("a .sh script");
            if (c.Contains("'")) found.Add("single quotes");
            return found;
        }

        internal static List<string> Validate(PluginManager.LoadedPlugin p)
        {
            var m = p.Manifest;
            var f = new List<string>(m.Findings);
            if (p.Local && m.Name != p.Name) f.Add($"error: plugin.json name \"{m.Name}\" must match the folder name \"{p.Name}\".");
            if (m.Version != null && !Regex.IsMatch(m.Version, @"^\d+\.\d+\.\d+([-+][0-9A-Za-z.-]+)?$")) f.Add($"warning: version \"{m.Version}\" is not semantic (1.2.3).");
            if (string.IsNullOrWhiteSpace(m.Description)) f.Add("warning: plugin.json has no description (the store shows it).");

            foreach (var c in CommandIndex.For(m))
            {
                f.AddRange(c.Front.Errors.Select(e => $"error: commands/{c.Name}.md: {e}"));
                if (c.Front.Get("description") == null) f.Add($"warning: command /{c.Id} has no description in its front matter (the slash menu shows it).");
            }
            foreach (var path in m.SkillPaths.Where(Directory.Exists))
            {
                foreach (var dir in File.Exists(Path.Combine(path, "SKILL.md")) ? new[] { path } : Directory.GetDirectories(path))
                {
                    var file = Path.Combine(dir, "SKILL.md");
                    var label = "skills/" + Path.GetFileName(dir) + "/SKILL.md";
                    if (!File.Exists(file))
                    {
                        f.Add($"warning: skills/{Path.GetFileName(dir)} has no SKILL.md (ignored).");
                        continue;
                    }
                    var fm = FrontMatter.Parse(File.ReadAllText(file));
                    f.AddRange(fm.Errors.Select(e => $"error: {label}: {e}"));
                    if (!fm.Present || fm.Get("description") == null) f.Add($"error: {label} needs front matter with a description (the agent picks skills by it).");
                    if (fm.Get("name") is string n && !PluginManifest.NamePattern.IsMatch(n)) f.Add($"error: {label}: name \"{n}\" must be lowercase letters, digits and dashes.");
                }
            }
            foreach (var a in AgentIndex.For(m))
            {
                f.AddRange(a.Front.Errors.Select(e => $"error: agent {a.Name}: {e}"));
                if (!a.Front.Present || a.Front.Get("description") == null) f.Add($"error: agent {a.Name} needs front matter with a description.");
                if (string.IsNullOrWhiteSpace(a.Front.Body)) f.Add($"error: agent {a.Name} has no system prompt (the text after the front matter).");
            }
            var agentFiles = m.AgentPaths.Where(Directory.Exists).SelectMany(d => Directory.GetFiles(d, "*.md"));
            foreach (var file in agentFiles.Where(x => AgentIndex.For(m).All(a => !string.Equals(a.File, x, StringComparison.OrdinalIgnoreCase))))
                f.Add($"error: agents/{Path.GetFileName(file)}: its name must be 2-48 lowercase letters, digits and dashes (front matter name, or the file name).");
            if (Directory.Exists(m.ToolsPath))
            {
                foreach (var dir in Directory.GetDirectories(m.ToolsPath))
                {
                    var name = Path.GetFileName(dir);
                    if (!PluginManifest.NamePattern.IsMatch(name)) f.Add($"error: tools/{name}: tool names are 2-48 lowercase letters, digits and dashes.");
                    else if (!File.Exists(Path.Combine(dir, "TOOL.md"))) f.Add($"error: tools/{name} has no TOOL.md.");
                    else
                    {
                        var fm = UserTools.FrontMatter(File.ReadAllText(Path.Combine(dir, "TOOL.md")));
                        if (!fm.ContainsKey("title") || !fm.ContainsKey("description")) f.Add($"warning: tools/{name}/TOOL.md should have title and description in its front matter.");
                        var inputs = Path.Combine(dir, "inputs.json");
                        if (File.Exists(inputs))
                        {
                            try
                            {
                                JArray.Parse(File.ReadAllText(inputs));
                                f.AddRange(ToolSpec.CheckInputs(dir, ToolSpec.LoadInputs(dir)).Select(w => $"warning: tools/{name}/inputs.json: {w}"));
                                // profiles\*.json ship with the tool, locked (docs/tool-profiles.md)
                                f.AddRange(ToolProfiles.CheckShipped(dir, m.Name + ":" + name));
                            }
                            catch (JsonException ex)
                            {
                                f.Add($"error: tools/{name}/inputs.json is not a valid JSON list: {ex.Message}");
                            }
                        }
                    }
                }
            }
            foreach (var kv in m.UserConfig)
            {
                var type = PluginManifest.Str(kv.Value["type"]) ?? "string";
                if (!OptionTypes.Contains(type)) f.Add($"error: userConfig.{kv.Key}: type \"{type}\" (use string, number, boolean, directory or file).");
            }
            // Secrets go to plugin processes as env (CLAUDE_PLUGIN_OPTION_<KEY>), never on a command line others can read.
            var secrets = m.UserConfig.Where(kv => (bool?)kv.Value["sensitive"] == true).Select(kv => "${user_config." + kv.Key + "}").ToList();
            foreach (var (ev, command) in m.HookCommands())
            {
                if (secrets.Any(s => command.Contains(s))) f.Add($"error: {ev} hook puts a sensitive option on its command line; read it from the env variable CLAUDE_PLUGIN_OPTION_<KEY> instead.");
                if (ev == "PermissionRequest") f.Add("warning: PermissionRequest hooks don't run in SolidWorks Agent (its own approval cards are used).");
                else if (!new[] { "PreToolUse", "PostToolUse", "UserPromptSubmit", "Stop", "SessionStart", "SessionEnd" }.Contains(ev))
                    f.Add($"warning: {ev} hooks don't run in SolidWorks Agent.");
                var bash = BashOnly(command);
                if (bash.Count > 0)
                    f.Add($"warning: {ev} hook \"{command}\" needs Git Bash ({string.Join(", ", bash)}): it runs as in Claude Code where Git for Windows is " +
                          "installed, but fails with cmd.exe on a PC without it. Use \"${CLAUDE_PLUGIN_ROOT}\" in double quotes and a node script to work everywhere.");
            }
            foreach (var kv in m.McpServers)
            {
                var line = (PluginManifest.Str(kv.Value["command"]) ?? "") + " " + string.Join(" ", (kv.Value["args"] as JArray ?? new JArray()).Select(a => (string)a));
                if (secrets.Any(s => line.Contains(s))) f.Add($"error: MCP server \"{kv.Key}\" puts a sensitive option in its command or args; pass it in \"env\" instead.");
                if (secrets.Any(s => (PluginManifest.Str(kv.Value["url"]) ?? "").Contains(s)))
                    f.Add($"warning: MCP server \"{kv.Key}\" puts a sensitive option in its url; send it in \"headers\" if the server accepts that (urls end up in logs and proxies).");
            }
            return f.Distinct().ToList();
        }

        /// <summary>
        /// Package a local plugin into folder (a marketplace): &lt;name&gt;\ (a copy), &lt;name&gt;-&lt;version&gt;.zip, and
        /// .claude-plugin\marketplace.json listing every plugin packaged there.
        /// </summary>
        internal static (string Zip, string Folder) Package(PluginManager.LoadedPlugin p, string folder)
        {
            var m = p.Manifest;
            var version = Regex.IsMatch(m.Version ?? "", @"^[A-Za-z0-9][A-Za-z0-9._+-]{0,63}$") ? m.Version : "0.1.0";
            var copy = Path.Combine(folder, p.Name);
            if (Directory.Exists(copy) && !Fetcher.TryDelete(copy)) throw new ToolException($"{copy} is in use; close it and try again.");
            Fetcher.CopyDirectory(p.Root, copy);
            var zip = Path.Combine(folder, $"{p.Name}-{version}.zip");
            if (File.Exists(zip)) File.Delete(zip);
            ZipFile.CreateFromDirectory(copy, zip, CompressionLevel.Optimal, includeBaseDirectory: false);

            var marketFile = MarketplaceManifest.PathIn(folder);
            JObject market;
            try
            {
                market = File.Exists(marketFile) ? JObject.Parse(File.ReadAllText(marketFile)) : null;
            }
            catch (JsonException)
            {
                market = null;
            }
            if (market == null)
            {
                var owner = Regex.Replace((Environment.UserName ?? "").ToLowerInvariant(), "[^a-z0-9-]", "-").Trim('-');
                var name = PluginManifest.NamePattern.IsMatch(owner + "-plugins") ? owner + "-plugins" : "shared-plugins";
                market = new JObject { ["name"] = name, ["owner"] = new JObject { ["name"] = Environment.UserName }, ["plugins"] = new JArray() };
            }
            var plugins = market["plugins"] as JArray ?? (JArray)(market["plugins"] = new JArray());
            foreach (var old in plugins.OfType<JObject>().Where(x => (string)x["name"] == p.Name).ToList()) old.Remove();
            var entry = new JObject { ["name"] = p.Name, ["source"] = "./" + p.Name, ["description"] = m.Description, ["version"] = version };
            if (PluginManifest.Str(m.SolidWorksAgent["category"]) is string category) entry["category"] = category;
            if (m.Keywords.Length > 0) entry["keywords"] = new JArray(m.Keywords);
            plugins.Add(entry);
            Directory.CreateDirectory(Path.GetDirectoryName(marketFile));
            File.WriteAllText(marketFile, market.ToString(Formatting.Indented) + "\n", new UTF8Encoding(false));
            Log.Info($"Packaged plugin {p.Name} {version} to {zip}");
            return (zip, folder);
        }

        private static string OneLine(string s) => Regex.Replace(s ?? "", @"\s+", " ").Trim();
    }
}
