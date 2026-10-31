using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SwAgent.Infrastructure;

namespace SwAgent.Tools
{
    /// <summary>
    /// User-created tools: reusable procedures the user builds with the agent ("Add mounting holes",
    /// "Create compression mold", ...). Each tool is a folder in %APPDATA%\SwAgent\tools\{name}\ holding a TOOL.md
    /// (front matter + step-by-step instructions, verified code snippets, design rules) and optional reference
    /// files. Like a skill: the agent sees a one-line index of every tool and loads the full instructions
    /// with read_user_tool only when it needs them.
    /// </summary>
    public sealed class UserTools
    {
        public static readonly string[] Icons = { "macro", "part", "assembly", "drawing", "tree", "select", "camera", "rebuild", "gear", "book", "markup", "shield" };

        private static readonly Regex NamePattern = new Regex("^[a-z0-9][a-z0-9-]{1,47}$");

        /// <summary>A plugin's tool: "&lt;plugin&gt;:&lt;tool&gt;" (the colon is allowed only there; design 4.1).</summary>
        private static readonly Regex PluginToolPattern = new Regex("^([a-z0-9][a-z0-9-]{1,47}):([a-z0-9][a-z0-9-]{1,47})$");

        private const string CategoryHelp =
            "Optional category: tools that share one get their own box on the SolidWorks Agent ribbon tab and their own section in the Workflows menu, " +
            "e.g. \"Molding & Toolmaking\". Reuse an existing category name (see list_user_tools) rather than inventing a near-duplicate.";

        /// <summary>
        /// API steps per turn: the default, a long tool's default (front matter long_run without max_steps), and the range
        /// a tool's max_steps is clamped to (design 2.1.1; the panel's lib/toolRun.ts has the same numbers).
        /// </summary>
        public const int DefaultMaxSteps = 200, LongRunMaxSteps = 600, MaxStepsMin = 50, MaxStepsCap = 1000;

        /// <summary>Folders under Root that hold the tools' own data (remembered values, shop overrides of built-in data, saved profiles), not tools.</summary>
        public static readonly HashSet<string> ReservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "last-inputs", "builtin-data", "profiles" };

        /// <summary>A front-matter value without surrounding quotes ("Compression Mold" or 'x'), trimmed; null when absent or blank.</summary>
        internal static string FrontValue(IDictionary<string, string> fm, string key)
        {
            if (fm == null || !fm.TryGetValue(key, out var v) || string.IsNullOrWhiteSpace(v)) return null;
            return Unquote(v.Trim());
        }

        private static string Unquote(string s) =>
            s.Length >= 2 && (s[0] == '"' && s[s.Length - 1] == '"' || s[0] == '\'' && s[s.Length - 1] == '\'') ? s.Substring(1, s.Length - 2) : s;

        /// <summary>A category as stored: one line, at most 48 characters; "" for none.</summary>
        public static string CleanCategory(string category)
        {
            var c = OneLine(category);
            return c.Length > 48 ? c.Substring(0, 48).TrimEnd() : c;
        }

        /// <summary>The tool's category from its (effective) front matter, or null when it has none.</summary>
        internal static string CategoryOf(IDictionary<string, string> fm)
        {
            var c = CleanCategory(FrontValue(fm, "category") ?? "");
            return c.Length == 0 ? null : c;
        }

        /// <summary>Front matter <c>long_run: true</c>: a long, phased tool (more steps per turn, a longer prompt cache, sticky run settings).</summary>
        internal static bool LongRunOf(IDictionary<string, string> fm) =>
            new[] { "true", "yes", "1" }.Contains((FrontValue(fm, "long_run") ?? "").ToLowerInvariant());

        /// <summary>
        /// API steps per turn for a tool run: <c>max_steps</c> (clamped to 50..1000), else 600 for a long tool, else null
        /// (the panel's default, 200). The one parser for this: ToolSpec should call it too.
        /// </summary>
        internal static int? MaxStepsOf(IDictionary<string, string> fm)
        {
            if (int.TryParse(FrontValue(fm, "max_steps") ?? "", System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n))
                return Math.Max(MaxStepsMin, Math.Min(MaxStepsCap, n));
            return LongRunOf(fm) ? LongRunMaxSteps : (int?)null;
        }

        /// <summary>Front matter <c>requires: a, b</c> (or <c>[a, b]</c>, separated by commas, spaces or semicolons, quotes
        /// allowed): agent tools the tool needs from the add-in.</summary>
        internal static string[] RequiresOf(IDictionary<string, string> fm) =>
            (FrontValue(fm, "requires") ?? "").Trim('[', ']')
                .Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => Unquote(s.Trim())).Where(s => s.Length > 0).Distinct().ToArray();

        /// <summary>
        /// Front matter "agent:" value for a run style: hardcoded -> false (macro only), agent -> true (the agent writes the
        /// code each run), hybrid -> hybrid (macro first; the agent takes over what it hands on, then improves it).
        /// Older names: instant = hardcoded, smart = agent, built / fallback = hybrid.
        /// </summary>
        private static string AgentValue(string runStyle, bool? agent, bool hasMacro)
        {
            switch ((runStyle ?? "").Trim().ToLowerInvariant().Replace(" ", "").Replace("-", ""))
            {
                case "hybrid":
                case "built":
                case "fallback":
                    return hasMacro ? "hybrid" : "true";
                case "hardcoded":
                case "instant":
                    return hasMacro ? "false" : "true";
                case "agent":
                case "agentintheloop":
                case "smart":
                    return "true";
            }
            return agent == false && hasMacro ? "false" : "true";
        }

        /// <summary>The user's own tools and workflows (%APPDATA%\SwAgent\tools): created and changed by the agent.</summary>
        public static string Root => Directory.CreateDirectory(Path.Combine(Paths.RoamingDir, "tools")).FullName;

        /// <summary>
        /// Built-in SolidWorks Agent tools: the same folder layout as the user's (TOOL.md, inputs.json, macro.cs, icons), shipped with
        /// the add-in in builtin-tools next to SwAgent.dll (source: tools/builtin in the repository). Read-only: they are
        /// replaced by every install, so the agent never edits them and their names can't be used for the user's tools.
        /// Per-PC settings for them (e.g. the intelligence level) go in the user's tools folder (builtin-settings.json).
        /// </summary>
        public static string BuiltInRoot => Path.Combine(Paths.AddInDir, "builtin-tools");

        private static string BuiltInSettingsPath => Path.Combine(Root, "builtin-settings.json");

        /// <summary>
        /// The folder of a tool: the user's, else a built-in one, else - for "plugin:tool" - the tool of an enabled plugin;
        /// null if there is none. The one lookup the ribbon, the palette, ToolRunner and read_user_tool share.
        /// </summary>
        public static string DirOf(string name)
        {
            name = (name ?? "").Trim().ToLowerInvariant();
            var plugin = PluginToolPattern.Match(name);
            if (plugin.Success)
            {
                try
                {
                    return Plugins.PluginManager.Instance.ToolDir(plugin.Groups[1].Value, plugin.Groups[2].Value);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Plugin tool '{name}' lookup failed: {ex.Message}");
                    return null;
                }
            }
            if (!NamePattern.IsMatch(name)) return null;
            var user = Path.Combine(Root, name);
            if (Directory.Exists(user) && File.Exists(Path.Combine(user, "TOOL.md"))) return user;
            var builtIn = Path.Combine(BuiltInRoot, name);
            return Directory.Exists(builtIn) && File.Exists(Path.Combine(builtIn, "TOOL.md")) ? builtIn : null;
        }

        /// <summary>
        /// A read-only tool: built in (shipped with the add-in) or from an installed plugin. Tools of the user's own plugins
        /// (plugins\local) stay editable. Kept for the existing callers; same as IsReadOnly.
        /// </summary>
        public static bool IsBuiltIn(string name) => IsReadOnly(name);

        /// <summary>The tool can't be changed or deleted: built in, or from an installed (not local) plugin.</summary>
        public static bool IsReadOnly(string name) => IsReadOnlyDir(DirOf(name));

        /// <summary>A tool folder that is replaced by installs and updates: builtin-tools, or a plugin's cache folder.</summary>
        internal static bool IsReadOnlyDir(string dir)
        {
            if (dir == null) return false;
            if (dir.StartsWith(BuiltInRoot, StringComparison.OrdinalIgnoreCase)) return true;
            try
            {
                return Plugins.PluginManager.Instance.IsReadOnlyDir(dir);
            }
            catch (Exception ex)
            {
                Log.Warn("Plugin folder check failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>A tool of a plugin ("plugin:tool"), enabled or not.</summary>
        public static bool IsPluginTool(string name) => PluginToolPattern.IsMatch((name ?? "").Trim().ToLowerInvariant());

        /// <summary>
        /// The tool's name as a file or folder name (last-inputs\&lt;key&gt;.json, builtin-data\&lt;key&gt;, builtin-settings.json keys):
        /// "plugin:tool" becomes "plugin+tool" - a colon would make an NTFS alternate data stream, and '+' can't occur in a name.
        /// </summary>
        public static string StorageKey(string name) => (name ?? "").Trim().ToLowerInvariant().Replace(':', '+');

        /// <summary>Tell the panel and the ribbon that tools changed (plugins enabled, installed, updated or edited).</summary>
        internal static void NotifyChanged() => Changed?.Invoke();

        private static bool BuiltInExists(string name) => File.Exists(Path.Combine(BuiltInRoot, name, "TOOL.md"));

        /// <summary>The user's settings for built-in tools: { name: { key: value } }.</summary>
        internal static Dictionary<string, string> BuiltInSettings(string name)
        {
            try
            {
                if (File.Exists(BuiltInSettingsPath) && JObject.Parse(File.ReadAllText(BuiltInSettingsPath))[StorageKey(name)] is JObject o)
                    return o.Properties().ToDictionary(p => p.Name, p => (string)p.Value, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Log.Warn("builtin-settings.json unreadable: " + ex.Message);
            }
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Front matter of a tool with the user's settings applied (read-only tools keep those outside their folder).</summary>
        internal static Dictionary<string, string> EffectiveFrontMatter(string name, string dir)
        {
            var fm = ReadFrontMatter(File.ReadAllText(Path.Combine(dir, "TOOL.md")));
            if (IsReadOnlyDir(dir))
            {
                foreach (var kv in BuiltInSettings(name)) fm[kv.Key] = kv.Value;
            }
            return fm;
        }

        /// <summary>Raised after a tool is saved or deleted (the UI refreshes its palette and the agent's index).</summary>
        public static event Action Changed;

        public sealed class ToolFile
        {
            [ToolParam("Relative path inside the tool folder, e.g. \"reference/mold-base.cs\" or \"rules.md\".", Required = true)]
            public string Path { get; set; }

            [ToolParam("Full text content of the file.", Required = true)]
            public string Content { get; set; }
        }

        public sealed class NoInput
        {
        }

        public sealed class ReadInput
        {
            [ToolParam("Tool name (the id from the user tools index).", Required = true)]
            public string Name { get; set; }

            [ToolParam("Optional extra file inside the tool folder to read instead of TOOL.md.")]
            public string File { get; set; }
        }

        public sealed class SaveInput
        {
            [ToolParam("Id: lowercase letters, digits and dashes, e.g. \"add-mounting-holes\". Saving an existing id replaces that tool.", Required = true)]
            public string Name { get; set; }

            [ToolParam("Short title shown on the button / in the menu, e.g. \"Add mounting holes\".", Required = true)]
            public string Title { get; set; }

            [ToolParam("\"workflow\" (default): a larger, multi-step procedure run from the Workflows menu in the chat, e.g. building a whole mold. " +
                       "\"tool\": a small, fast command that extends manual modelling - a button on the SolidWorks Agent ribbon tab the user can put on a hotkey; " +
                       "give it a macro so it runs instantly.")]
            public string Kind { get; set; }

            [ToolParam("Tools only: the verified C# that does the whole job (same globals as execute_csharp: swApp, Model, Print, mm(), deg(), ...). " +
                       "It runs directly when the button is clicked - no AI round trip, one undo step - so it must read its inputs from the live " +
                       "selection (Model.SelectionManager) or use fixed defaults, never Ref(\"S1\"). For a wrong or missing selection call " +
                       "Fail(\"Select a planar face first\") (shown to the user in the status bar). End with a short result string, e.g. \"Added 4 holes\".")]
            public string Macro { get; set; }

            [ToolParam("Workflows: a model id from Options (e.g. claude-sonnet-5-5 or gpt-6.1-sol), or omit to use the intelligence tier.")]
            public string Model { get; set; }

            [ToolParam("Tools: the inputs of its PropertyManager page (shown on the left like a native command). Omit for tools that just act on the selection.")]
            public ToolInputSpec[] Inputs { get; set; }

            [ToolParam("Tools: run style - hardcoded | agent | hybrid. hardcoded: the macro is the whole tool; a case it wasn't written for stops " +
                       "with its Fail message. agent: agent in the loop - the agent does the job each run from TOOL.md's skill, writing code from the " +
                       "context. hybrid: the macro runs first and does the job when it fits; for a case it can't handle it calls NeedsAgent(\"why\") " +
                       "and the agent takes over - adapts the code, finishes, then improves the macro so the tool learns.")]
            public string RunStyle { get; set; }

            [ToolParam("Older form of run_style: true = smart, false = instant. Prefer run_style.")]
            public bool? Agent { get; set; }

            [ToolParam("Tools with an agent: how much intelligence the job needs - fast (simple, mechanical), balanced (default), high (careful geometry work) or max (hardest reasoning). " +
                       "Maps to the user's provider (e.g. Claude Haiku / Sonnet / Opus, or GPT-6 Luna / 6.1 Sol / 6 Astra); the user can change it in the tool's page.")]
            public string Intelligence { get; set; }

            [ToolParam("Tools: \"missing\" (default) skips the PropertyManager page when everything required is already selected and runs at once; " +
                       "\"always\" shows it every time (when the user normally adjusts values).")]
            public string Confirm { get; set; }

            [ToolParam("Tools with an agent: context captured silently for it - any of view (screenshot of the current view), selection, model " +
                       "(units, material, size), history (feature tree with parameters), history_full (also sketch geometry). Default view, selection, model, history. " +
                       "Prefer a little too much over too little.")]
            public string[] Context { get; set; }

            [ToolParam("Tools: C# that draws a live holographic preview while the page is open: build temporary bodies from Inputs " +
                       "(IModeler, IBody2.Copy2/Operations2...) and pass them to Preview(body). Runs on every input change, so it must be fast " +
                       "and must not change the model. Omit when a preview wouldn't be fast or useful.")]
            public string Preview { get; set; }

            [ToolParam("One or two sentences: what the tool does and what the user should select or provide before running it.", Required = true)]
            public string Description { get; set; }

            [ToolParam("Icon: macro, part, assembly, drawing, tree, select, camera, rebuild, gear, book, markup or shield (default macro).")]
            public string Icon { get; set; }

            [ToolParam(CategoryHelp)]
            public string Category { get; set; }

            [ToolParam("The full instructions in Markdown: inputs and how to get them (selection [S1], questions to ask), defaults, " +
                       "the procedure step by step with the exact API calls / C# snippets you verified, design rules and checks, " +
                       "and what to report. Write it so a future run can follow it without this conversation.", Required = true)]
            public string Instructions { get; set; }

            [ToolParam("Optional extra reference files saved alongside TOOL.md (templates, longer code, design tables).")]
            public ToolFile[] Files { get; set; }

            [ToolParam("An icon designed for this tool, as SVG markup: viewBox=\"0 0 16 16\", drawn to read clearly at 16-20 px next to SolidWorks' own " +
                       "commands - a simple pictogram of what the tool makes or does (e.g. a mold block with a cavity, a plate with holes), " +
                       "in the SolidWorks Agent look: brushed steel fills as top-to-bottom gradients (#F4F5F6 to #9AA0A6), " +
                       "graphite for dark parts and details (#55595F to #1C1F22), 0.75-1 px graphite outlines (#26292D), a small white highlight " +
                       "at most - monochrome metal, no blue or other colour accents, no text, no thin details, transparent background. " +
                       "Shown on the tool's ribbon button and in the Tools menu.")]
            public string IconSvg { get; set; }
        }

        public sealed class Replacement
        {
            [ToolParam("Exact text currently in TOOL.md (must occur exactly once).", Required = true)]
            public string Find { get; set; }

            [ToolParam("Text to put in its place.", Required = true)]
            public string Replace { get; set; }
        }

        public sealed class EditInput
        {
            [ToolParam("Tool name (the id from the user tools index).", Required = true)]
            public string Name { get; set; }

            [ToolParam("Markdown to add to the end of the instructions, e.g. a new rule or note under its own heading.")]
            public string Append { get; set; }

            [ToolParam("Targeted changes to the existing instructions, applied in order.")]
            public Replacement[] Replacements { get; set; }

            [ToolParam("New one-line description (only if what the tool does changed).")]
            public string Description { get; set; }

            [ToolParam("New button title.")]
            public string Title { get; set; }

            [ToolParam(CategoryHelp + " An empty string removes the category.")]
            public string Category { get; set; }

            [ToolParam("Tools only: the verified C# that does the whole job (same globals as execute_csharp: swApp, Model, Print, mm(), deg(), ...). " +
                       "It runs directly when the button is clicked - no AI round trip, one undo step - so it must read its inputs from the live " +
                       "selection (Model.SelectionManager) or use fixed defaults, never Ref(\"S1\"). For a wrong or missing selection call " +
                       "Fail(\"Select a planar face first\") (shown to the user in the status bar). End with a short result string, e.g. \"Added 4 holes\"." + " Replaces the tool's whole macro.")]
            public string Macro { get; set; }

            [ToolParam("Tools: replace the inputs of its PropertyManager page (shown on the left like a native command). Omit for tools that just act on the selection.")]
            public ToolInputSpec[] Inputs { get; set; }

            [ToolParam("Tools: run style - hardcoded | agent | hybrid. hardcoded: the macro is the whole tool; a case it wasn't written for stops " +
                       "with its Fail message. agent: agent in the loop - the agent does the job each run from TOOL.md's skill, writing code from the " +
                       "context. hybrid: the macro runs first and does the job when it fits; for a case it can't handle it calls NeedsAgent(\"why\") " +
                       "and the agent takes over - adapts the code, finishes, then improves the macro so the tool learns.")]
            public string RunStyle { get; set; }

            [ToolParam("Older form of run_style: true = smart, false = instant. Prefer run_style.")]
            public bool? Agent { get; set; }

            [ToolParam("Tools with an agent: how much intelligence the job needs - fast (simple, mechanical), balanced (default), high (careful geometry work) or max (hardest reasoning). " +
                       "Maps to the user's provider (e.g. Claude Haiku / Sonnet / Opus, or GPT-6 Luna / 6.1 Sol / 6 Astra); the user can change it in the tool's page.")]
            public string Intelligence { get; set; }

            [ToolParam("Tools: \"missing\" (default) skips the PropertyManager page when everything required is already selected and runs at once; " +
                       "\"always\" shows it every time (when the user normally adjusts values).")]
            public string Confirm { get; set; }

            [ToolParam("Tools with an agent: context captured silently for it - any of view (screenshot of the current view), selection, model " +
                       "(units, material, size), history (feature tree with parameters), history_full (also sketch geometry). Default view, selection, model, history. " +
                       "Prefer a little too much over too little.")]
            public string[] Context { get; set; }

            [ToolParam("Tools: C# that draws a live holographic preview while the page is open: build temporary bodies from Inputs " +
                       "(IModeler, IBody2.Copy2/Operations2...) and pass them to Preview(body). Runs on every input change, so it must be fast " +
                       "and must not change the model. Omit when a preview wouldn't be fast or useful.")]
            public string Preview { get; set; }

            [ToolParam("An icon designed for this tool, as SVG markup: viewBox=\"0 0 16 16\", drawn to read clearly at 16-20 px next to SolidWorks' own " +
                       "commands - a simple pictogram of what the tool makes or does (e.g. a mold block with a cavity, a plate with holes), " +
                       "in the SolidWorks Agent look: brushed steel fills as top-to-bottom gradients (#F4F5F6 to #9AA0A6), " +
                       "graphite for dark parts and details (#55595F to #1C1F22), 0.75-1 px graphite outlines (#26292D), a small white highlight " +
                       "at most - monochrome metal, no blue or other colour accents, no text, no thin details, transparent background. " +
                       "Shown on the tool's ribbon button and in the Tools menu.")]
            public string IconSvg { get; set; }
        }

        [AgentTool("list_user_tools", "List the user's saved workflows and tools with their descriptions.")]
        public ToolResult List(ToolContext ctx, NoInput input)
        {
            var tools = ListTools();
            return ToolResult.Ok(tools.Count == 0
                ? "The user has no saved tools yet."
                : string.Join("\n", tools.Select(t =>
                    $"- {t["name"]} ({((bool?)t["builtIn"] == true ? "built-in " : "")}{t["kind"]}{((string)t["category"] is string c ? ", " + c : "")}): {t["title"]} - {t["description"]}")));
        }

        [AgentTool("read_user_tool",
            "Load a workflow's or tool's full instructions (TOOL.md) and its file list, or one of its files (e.g. macro.cs). Read it before " +
            "running it, and before editing it.")]
        public ToolResult Read(ToolContext ctx, ReadInput input)
        {
            var dir = ToolDir(input.Name, mustExist: true, forWrite: false);
            var target = string.IsNullOrWhiteSpace(input.File) ? Path.Combine(dir, "TOOL.md") : SafePath(dir, input.File);
            if (!File.Exists(target)) return ToolResult.Error($"No file '{input.File}' in tool '{input.Name}'.");
            var others = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                .Select(f => f.Substring(dir.Length + 1).Replace('\\', '/'))
                .Where(f => f != "TOOL.md" && f != ToolForks.RecordFile)
                .ToList();
            var text = File.ReadAllText(target);
            if (string.IsNullOrWhiteSpace(input.File))
            {
                // A read-only tool's standing instructions live outside its folder (agent-knowledge\tool-notes): they come with it.
                var notes = IsReadOnly(input.Name) ? Memory.MemoryStore.Instance.OverlayText(input.Name) : null;
                if (notes != null) text = text.TrimEnd() + "\n\n## " + notes;
                if (others.Count > 0) text += "\n\n---\nExtra files in this tool: " + string.Join(", ", others);
                var source = IsReadOnly(input.Name) ? null : ToolForks.SourceOfDir(dir);
                if (source != null && ToolForks.SourceChanged(dir))
                    text += $"\n\n(This is the user's copy of {source}, which changed since it was copied: compare_fork shows how. Offer it; don't merge unasked.)";
            }
            if (IsReadOnly(input.Name))
                text += $"\n\n({(IsPluginTool(input.Name) ? "Tool of an installed plugin" : "Built-in SolidWorks Agent tool")}: read-only, but never a dead end. A behaviour rule " +
                        "for it goes in remember(tool:" + input.Name.Trim().ToLowerInvariant() + "); a real change (inputs, steps, data, code) goes in the user's own copy: fork_tool.)";
            return ToolResult.Ok(text);
        }

        [AgentTool("save_user_tool",
            "Create or update one of the user's workflows or tools (kind). Only save when the user asked for one (or agreed to save one). " +
            "Before saving, settle with the user what the inputs are and what should be fixed vs. adjustable.")]
        public ToolResult Save(ToolContext ctx, SaveInput input)
        {
            var name = (input.Name ?? "").Trim().ToLowerInvariant();
            if (!NamePattern.IsMatch(name)) return ToolResult.Error("Name must be 2-48 characters: lowercase letters, digits and dashes.");
            if (BuiltInExists(name)) return ToolResult.Error($"'{name}' is a built-in SolidWorks Agent tool. Make the user's own copy with fork_tool (it keeps the data, page and code), then change that.");
            if (ReservedNames.Contains(name)) return ToolResult.Error($"'{name}' is reserved for the tools' own settings folder. Choose another name.");
            var icon = Icons.Contains(input.Icon) ? input.Icon : "macro";
            var kind = string.Equals((input.Kind ?? "").Trim(), "tool", StringComparison.OrdinalIgnoreCase) ? "tool" : "workflow";
            var dir = Directory.CreateDirectory(Path.Combine(Root, name)).FullName;
            var md = Path.Combine(dir, "TOOL.md");
            var created = File.Exists(md) ? ReadFrontMatter(File.ReadAllText(md)).TryGetValue("created", out var c) ? c : Now() : Now();
            // Rewriting the instructions keeps the user's standing instructions (remember) unless the new text has its own,
            // and a copy keeps where it came from (fork_tool).
            var standing = File.Exists(md) ? StandingSection(File.ReadAllText(md)) : null;
            var forkedFrom = File.Exists(md) ? FrontValue(ReadFrontMatter(File.ReadAllText(md)), "forked_from") : null;

            var sb = new StringBuilder();
            sb.AppendLine("---");
            sb.AppendLine($"name: {name}");
            sb.AppendLine($"title: {OneLine(input.Title)}");
            sb.AppendLine($"description: {OneLine(input.Description)}");
            sb.AppendLine($"kind: {kind}");
            sb.AppendLine($"icon: {icon}");
            var category = CleanCategory(input.Category);
            if (category.Length > 0) sb.AppendLine($"category: {category}");
            if (!string.IsNullOrWhiteSpace(input.Model)) sb.AppendLine($"model: {OneLine(input.Model)}");
            if (kind == "tool")
            {
                sb.AppendLine($"agent: {AgentValue(input.RunStyle, input.Agent, !string.IsNullOrWhiteSpace(input.Macro))}");
                sb.AppendLine($"intelligence: {(ToolSpec.Tiers.Contains(input.Intelligence) ? input.Intelligence : "balanced")}");
                sb.AppendLine($"confirm: {(input.Confirm == "always" ? "always" : "missing")}");
                if (input.Context?.Length > 0) sb.AppendLine($"context: {string.Join(", ", input.Context.Select(c => c.Trim().ToLowerInvariant()))}");
            }
            if (forkedFrom != null) sb.AppendLine($"forked_from: {forkedFrom}");
            sb.AppendLine($"created: {created}");
            sb.AppendLine($"updated: {Now()}");
            sb.AppendLine("---");
            sb.AppendLine();
            sb.Append(input.Instructions.Trim()).AppendLine();
            if (standing != null && StandingSection(input.Instructions) == null) sb.AppendLine().Append(standing).AppendLine();
            File.WriteAllText(md, sb.ToString(), new UTF8Encoding(false));

            WriteIcon(dir, input.IconSvg, iconChanged: !string.IsNullOrWhiteSpace(input.IconSvg) || input.Icon != null);
            WriteMacro(dir, input.Macro);
            WritePreview(dir, input.Preview);
            ToolSpec.SaveInputs(dir, input.Inputs);

            foreach (var f in input.Files ?? new ToolFile[0])
            {
                var path = SafePath(dir, f.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, f.Content ?? "", new UTF8Encoding(false));
            }
            Log.Info($"Saved user tool '{name}'");
            Changed?.Invoke();
            var hasIcon = File.Exists(Path.Combine(dir, "icon.svg"));
            if (kind == "tool")
            {
                var hasMacro = File.Exists(Path.Combine(dir, "macro.cs"));
                return ToolResult.Ok($"Saved tool '{name}' ({input.Title}). It is a button on the 'SolidWorks Agent' ribbon tab{(hasIcon ? " with its icon" : " (default icon - pass icon_svg)")}; " +
                                     "the user can assign it a hotkey (Tools > Customize > Keyboard, category SolidWorks Agent). " +
                                     (hasMacro ? "Clicking it runs the macro directly on the current selection." : "It has no macro yet, so each click asks the agent - add a verified macro to make it instant."));
            }
            return ToolResult.Ok($"Saved workflow '{name}' ({input.Title}). It is in the Workflows menu of the SolidWorks Agent chat panel{(hasIcon ? ", with its icon" : "")}.");
        }

        [AgentTool("edit_user_tool",
            "Make a targeted change to an existing workflow or tool - add a note or rule, replace specific passages, or replace a tool's macro - " +
            "without rewriting the rest. Prefer this over save_user_tool for small changes. Read it first.")]
        public ToolResult Edit(ToolContext ctx, EditInput input)
        {
            var dir = ToolDir(input.Name, mustExist: true);
            var md = Path.Combine(dir, "TOOL.md");
            if (!File.Exists(md)) throw new ToolException($"Tool '{input.Name}' has no TOOL.md.");
            if (string.IsNullOrWhiteSpace(input.Append) && (input.Replacements?.Length ?? 0) == 0 && string.IsNullOrWhiteSpace(input.Description) &&
                string.IsNullOrWhiteSpace(input.Title) && string.IsNullOrWhiteSpace(input.IconSvg) && string.IsNullOrWhiteSpace(input.Macro) &&
                string.IsNullOrWhiteSpace(input.Preview) && input.Inputs == null && input.Agent == null && input.RunStyle == null && input.Intelligence == null &&
                input.Confirm == null && input.Context == null && input.Category == null)
                throw new ToolException("Nothing to change: pass append, replacements, description, title, category, icon_svg, macro, preview, inputs or a tool setting.");
            if (!string.IsNullOrWhiteSpace(input.IconSvg)) WriteIcon(dir, input.IconSvg, iconChanged: true);
            WriteMacro(dir, input.Macro);
            WritePreview(dir, input.Preview);
            if (input.Inputs != null) ToolSpec.SaveInputs(dir, input.Inputs);

            var text = File.ReadAllText(md).Replace("\r\n", "\n");
            var lines = text.Split('\n');
            var end = lines.Length > 0 && lines[0].Trim() == "---" ? Array.FindIndex(lines, 1, l => l.Trim() == "---") : -1;
            var header = end > 0 ? lines.Take(end + 1).ToList() : new List<string>();
            var body = end > 0 ? string.Join("\n", lines.Skip(end + 1)) : text;

            foreach (var r in input.Replacements ?? new Replacement[0])
            {
                if (string.IsNullOrEmpty(r.Find)) throw new ToolException("A replacement has empty find text.");
                var find = r.Find.Replace("\r\n", "\n");
                var at = body.IndexOf(find, StringComparison.Ordinal);
                if (at < 0) throw new ToolException($"Text not found in the tool (nothing was changed): \"{Shorten(find)}\"");
                if (body.IndexOf(find, at + 1, StringComparison.Ordinal) >= 0) throw new ToolException($"Text occurs more than once; include more around it (nothing was changed): \"{Shorten(find)}\"");
                body = body.Substring(0, at) + (r.Replace ?? "").Replace("\r\n", "\n") + body.Substring(at + find.Length);
            }
            if (!string.IsNullOrWhiteSpace(input.Append)) body = body.TrimEnd() + "\n\n" + input.Append.Replace("\r\n", "\n").Trim() + "\n";

            if (header.Count > 0)
            {
                SetHeader(header, "updated", Now());
                if (!string.IsNullOrWhiteSpace(input.Description)) SetHeader(header, "description", OneLine(input.Description));
                if (!string.IsNullOrWhiteSpace(input.Title)) SetHeader(header, "title", OneLine(input.Title));
                if (input.Category != null) SetHeader(header, "category", CleanCategory(input.Category));
                if (input.RunStyle != null || input.Agent != null) SetHeader(header, "agent", AgentValue(input.RunStyle, input.Agent, true));
                if (ToolSpec.Tiers.Contains(input.Intelligence)) SetHeader(header, "intelligence", input.Intelligence);
                if (input.Confirm == "always" || input.Confirm == "missing") SetHeader(header, "confirm", input.Confirm);
                if (input.Context?.Length > 0) SetHeader(header, "context", string.Join(", ", input.Context.Select(c => c.Trim().ToLowerInvariant())));
                // A new preview or macro replaces the one that crashed SolidWorks: un-pause it.
                var paused = header.FirstOrDefault(l => l.StartsWith("paused:", StringComparison.OrdinalIgnoreCase));
                if (paused != null)
                {
                    var left = paused.Substring(7).Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                        .Where(p => !(p == "preview" && !string.IsNullOrWhiteSpace(input.Preview)) && !(p == "macro" && !string.IsNullOrWhiteSpace(input.Macro)));
                    SetHeader(header, "paused", string.Join(", ", left));
                }
            }
            File.WriteAllText(md, (header.Count > 0 ? string.Join("\n", header) + "\n" : "") + body, new UTF8Encoding(false));
            Log.Info($"Edited user tool '{input.Name}'");
            Changed?.Invoke();
            return ToolResult.Ok($"Updated tool '{input.Name}' ({md}).");
        }

        /// <summary>Save (or replace) a tool's own icon. The PNG the ribbon uses is re-rendered by the panel.</summary>
        private static void WriteIcon(string dir, string svg, bool iconChanged)
        {
            if (!string.IsNullOrWhiteSpace(svg))
            {
                svg = svg.Trim();
                if (!svg.StartsWith("<svg", StringComparison.OrdinalIgnoreCase) || !svg.EndsWith("</svg>", StringComparison.OrdinalIgnoreCase))
                    throw new ToolException("icon_svg must be a single <svg ...>...</svg> element.");
                if (svg.Length > 20000) throw new ToolException("icon_svg is too large (keep it under 20,000 characters).");
                if (Regex.IsMatch(svg, @"<script|<foreignObject|\son\w+\s*=|(href|src)\s*=\s*[""'](?!#|data:image/)", RegexOptions.IgnoreCase))
                    throw new ToolException("icon_svg may only contain drawing elements (no scripts, event handlers or external links).");
                File.WriteAllText(Path.Combine(dir, "icon.svg"), svg, new UTF8Encoding(false));
            }
            if (iconChanged)
            {
                var png = Path.Combine(dir, "icon.png");
                if (File.Exists(png)) File.Delete(png);
            }
        }

        private static string ReadIconSvg(string dir)
        {
            var path = Path.Combine(dir, "icon.svg");
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch
            {
                return null;
            }
        }

        private static void WritePreview(string dir, string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return;
            File.WriteAllText(Path.Combine(dir, "preview.cs"), code.Replace("\r\n", "\n").Trim() + "\n", new UTF8Encoding(false));
        }

        /// <summary>Front matter of a TOOL.md (public for ToolSpec).</summary>
        public static Dictionary<string, string> FrontMatter(string text) => ReadFrontMatter(text);

        /// <summary>
        /// Change one front-matter value of a saved tool (e.g. its intelligence, from the tool's page). For a built-in tool
        /// the value is the user's override in builtin-settings.json: null (reset), or the value the tool ships with,
        /// removes the override, so the shipped value - and any later change to it - applies again.
        /// </summary>
        public static void SetValue(string name, string key, string value)
        {
            var isCategory = string.Equals(key, "category", StringComparison.OrdinalIgnoreCase);
            if (IsReadOnly(name))
            {
                var storageKey = StorageKey(name);
                var shipped = ReadFrontMatter(File.ReadAllText(Path.Combine(DirOf(name), "TOOL.md")));
                shipped.TryGetValue(key, out var original);
                var clean = value == null ? null : isCategory ? CleanCategory(value) : OneLine(value);
                var same = clean != null && (isCategory ? CleanCategory(original) == clean : original != null && OneLine(original) == clean);
                var all = File.Exists(BuiltInSettingsPath) ? JObject.Parse(File.ReadAllText(BuiltInSettingsPath)) : new JObject();
                var mine = all[storageKey] as JObject ?? new JObject();
                if (clean == null || same) mine.Remove(key);
                else mine[key] = clean; // "" for a category: the user chose none
                if (mine.HasValues) all[storageKey] = mine;
                else all.Remove(storageKey);
                File.WriteAllText(BuiltInSettingsPath, all.ToString(Newtonsoft.Json.Formatting.Indented));
                Changed?.Invoke();
                return;
            }
            // Front matter is one "key: value" line each; a category from the panel is free text.
            value = isCategory ? CleanCategory(value) : OneLine(value);
            var md = Path.Combine(ToolDir(name, mustExist: true), "TOOL.md");
            var lines = File.ReadAllText(md).Replace("\r\n", "\n").Split('\n').ToList();
            var end = lines.Count > 0 && lines[0].Trim() == "---" ? lines.FindIndex(1, l => l.Trim() == "---") : -1;
            if (end < 0) return;
            var header = lines.Take(end + 1).ToList();
            SetHeader(header, key, value);
            File.WriteAllText(md, string.Join("\n", header.Concat(lines.Skip(end + 1))), new UTF8Encoding(false));
            Changed?.Invoke();
        }

        private static void WriteMacro(string dir, string macro)
        {
            if (string.IsNullOrWhiteSpace(macro)) return;
            File.WriteAllText(Path.Combine(dir, "macro.cs"), macro.Replace("\r\n", "\n").Trim() + "\n", new UTF8Encoding(false));
        }

        /// <summary>A tool's macro, or null if it has none.</summary>
        public static string ReadMacro(string name)
        {
            var path = Path.Combine(ToolDir(name, mustExist: true, forWrite: false), "macro.cs");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        /// <summary>The panel rendered the tool's icon to PNG for the ribbon.</summary>
        public static void SetIconPng(string name, byte[] png)
        {
            var dir = ToolDir(name, mustExist: true);
            if (png == null || png.Length < 8 || png[0] != 0x89 || png[1] != (byte)'P') throw new ToolException("Not a PNG image.");
            File.WriteAllBytes(Path.Combine(dir, "icon.png"), png);
            Changed?.Invoke();
        }

        /// <summary>The "## Standing instructions" section of a TOOL.md (heading to the next # or ## heading), or null.</summary>
        internal static string StandingSection(string text)
        {
            var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
            var at = Array.FindIndex(lines, l => Regex.IsMatch(l, @"^##\s+Standing instructions\s*$", RegexOptions.IgnoreCase));
            if (at < 0) return null;
            var end = Array.FindIndex(lines, at + 1, l => l.StartsWith("# ") || l.StartsWith("## "));
            return string.Join("\n", lines.Skip(at).Take((end < 0 ? lines.Length : end) - at)).TrimEnd();
        }

        internal static void SetHeader(List<string> header, string key, string value)
        {
            var i = header.FindIndex(1, l => l.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(value) && string.Equals(key, "category", StringComparison.OrdinalIgnoreCase))
            {
                if (i > 0) header.RemoveAt(i); // no category: no empty line left behind
                return;
            }
            if (i > 0) header[i] = $"{key}: {value}";
            else header.Insert(header.Count - 1, $"{key}: {value}");
        }

        private static string Shorten(string s) => s.Length > 80 ? s.Substring(0, 80) + "..." : s;

        /// <summary>Index used by the UI (palette + the agent's system prompt).</summary>
        public static List<JObject> ListTools()
        {
            var result = new List<JObject>();
            var builtIn = Directory.Exists(BuiltInRoot) ? Directory.GetDirectories(BuiltInRoot) : new string[0];
            var dirs = builtIn.OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .Concat(Directory.GetDirectories(Root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                    .Where(d => !BuiltInExists(Path.GetFileName(d)))) // a built-in name can't be a user tool
                .Select(d => (Name: Path.GetFileName(d), Dir: d, Plugin: (string)null))
                .Concat(PluginTools());
            foreach (var (name, dir, plugin) in dirs)
            {
                var md = Path.Combine(dir, "TOOL.md");
                if (!File.Exists(md)) continue;
                try
                {
                    var fm = EffectiveFrontMatter(name, dir);
                    var readOnly = IsReadOnlyDir(dir);
                    var forkedFrom = readOnly ? null : ToolForks.SourceOfDir(dir);
                    result.Add(new JObject
                    {
                        ["name"] = name,
                        ["builtIn"] = dir.StartsWith(BuiltInRoot, StringComparison.OrdinalIgnoreCase),
                        // A plugin's tool ("plugin:tool"): read-only unless the plugin is the user's own (plugins\local).
                        ["plugin"] = plugin,
                        ["readOnly"] = readOnly,
                        ["title"] = fm.TryGetValue("title", out var t) ? t : name,
                        ["description"] = fm.TryGetValue("description", out var d) ? d : "",
                        ["icon"] = fm.TryGetValue("icon", out var i) && Icons.Contains(i) ? i : "macro",
                        ["kind"] = fm.TryGetValue("kind", out var k) && k == "tool" ? "tool" : "workflow",
                        ["hasMacro"] = File.Exists(Path.Combine(dir, "macro.cs")),
                        ["model"] = fm.TryGetValue("model", out var m) ? m : null,
                        ["agent"] = !fm.TryGetValue("agent", out var ag) || ag != "false" || !File.Exists(Path.Combine(dir, "macro.cs")),
                        ["runStyle"] = !File.Exists(Path.Combine(dir, "macro.cs")) ? "agent" : ag == "false" ? "hardcoded" : ag == "hybrid" || ag == "fallback" ? "hybrid" : "agent",
                        ["intelligence"] = fm.TryGetValue("intelligence", out var tier) && ToolSpec.Tiers.Contains(tier) ? tier : "balanced",
                        ["hasInputs"] = File.Exists(Path.Combine(dir, "inputs.json")),
                        ["hasPreview"] = File.Exists(Path.Combine(dir, "preview.cs")),
                        ["paused"] = fm.TryGetValue("paused", out var pz) ? pz : "",
                        ["updated"] = fm.TryGetValue("updated", out var u) ? u : File.GetLastWriteTimeUtc(md).ToString("o"),
                        ["iconSvg"] = ReadIconSvg(dir),
                        ["hasPng"] = File.Exists(Path.Combine(dir, "icon.png")),
                        // Grouping on the ribbon and in the Workflows menu, the ribbon's shorter button title, and long runs.
                        ["category"] = CategoryOf(fm),
                        // A built-in's shipped category, so the panel can offer "Reset to default" after the user changed it.
                        ["categoryDefault"] = readOnly
                            ? CategoryOf(ReadFrontMatter(File.ReadAllText(md)))
                            : null,
                        ["ribbonTitle"] = fm.TryGetValue("ribbon_title", out var rt) && !string.IsNullOrWhiteSpace(rt) ? OneLine(rt) : null,
                        ["longRun"] = LongRunOf(fm),
                        ["maxSteps"] = MaxStepsOf(fm),
                        ["requires"] = new JArray(RequiresOf(fm)),
                        // docs/agent-memory.md: kept off the ribbon and palette, has the user's standing instructions, a copy's original.
                        ["hidden"] = readOnly && FrontValue(fm, "hidden") == "true",
                        ["hasNotes"] = Memory.MemoryStore.Instance.HasNotes(name, dir, readOnly),
                        ["forkedFrom"] = forkedFrom,
                        ["sourceChanged"] = forkedFrom != null && ToolForks.SourceChanged(dir),
                        // Saved settings of its inputs (the tool index lists them; run_tool / --profile load one).
                        ["profiles"] = new JArray(ToolProfiles.Names(name, toolDir: dir)),
                    });
                }
                catch (Exception ex)
                {
                    Log.Warn($"Skipping unreadable tool {dir}: {ex.Message}");
                }
            }
            return result;
        }

        /// <summary>The tools of enabled plugins, named "plugin:tool" (tools with C# only once the user approved the plugin).</summary>
        private static IEnumerable<(string Name, string Dir, string Plugin)> PluginTools()
        {
            try
            {
                var pm = Plugins.PluginManager.Instance;
                return pm.EnabledPlugins()
                    .SelectMany(p => pm.UsableTools(p).Select(t => (p.Name + ":" + t.Name, t.Dir, p.Name))).ToList();
            }
            catch (Exception ex)
            {
                Log.Warn("Plugin tools unavailable: " + ex.Message);
                return new List<(string, string, string)>();
            }
        }

        /// <summary>Deletes one of the user's tools and its saved profiles (a later tool of that name starts without them).</summary>
        public static void Delete(string name)
        {
            var dir = ToolDir(name, mustExist: true);
            name = name.Trim().ToLowerInvariant();
            if (ReservedNames.Contains(name) || !File.Exists(Path.Combine(dir, "TOOL.md"))) throw new ToolException($"'{name}' is not a tool (it has no TOOL.md); nothing was deleted.");
            Directory.Delete(dir, recursive: true);
            ToolProfiles.RemoveAll(name);
            Log.Info($"Deleted user tool '{name}'");
            Changed?.Invoke();
        }

        private static string ToolDir(string name, bool mustExist, bool forWrite = true)
        {
            name = (name ?? "").Trim().ToLowerInvariant();
            if (PluginToolPattern.IsMatch(name))
            {
                // A plugin's tool: editable only in the user's own plugins (plugins\local); never created here.
                var pluginDir = DirOf(name) ?? throw new ToolException($"No tool named '{name}' (is its plugin enabled?). Use list_user_tools to see what exists.");
                if (forWrite && IsReadOnlyDir(pluginDir))
                    throw new ToolException($"'{name}' belongs to an installed plugin and can't be changed in place. For a behaviour rule use remember(tool:{name}); " +
                                            "for a real change make the user's own copy with fork_tool, then change that.");
                return pluginDir;
            }
            if (!NamePattern.IsMatch(name)) throw new ToolException($"'{name}' is not a valid tool name.");
            if (IsBuiltIn(name))
            {
                if (forWrite)
                    throw new ToolException($"'{name}' is a built-in SolidWorks Agent tool and can't be changed in place. For a behaviour rule use remember(tool:{name}); " +
                                            "for a real change make the user's own copy with fork_tool, then change that.");
                return DirOf(name);
            }
            var dir = Path.Combine(Root, name);
            if (mustExist && !Directory.Exists(dir)) throw new ToolException($"No tool named '{name}'. Use list_user_tools to see what exists.");
            return dir;
        }

        /// <summary>Resolve a relative path inside a tool folder; rejects anything that escapes it.</summary>
        internal static string SafePath(string dir, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new ToolException("File paths must be relative to the tool folder.");
            var full = Path.GetFullPath(Path.Combine(dir, relative));
            if (!full.StartsWith(Path.GetFullPath(dir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ToolException("File path escapes the tool folder.");
            return full;
        }

        private static Dictionary<string, string> ReadFrontMatter(string text)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var lines = text.Replace("\r\n", "\n").Split('\n');
            if (lines.Length == 0 || lines[0].Trim() != "---") return result;
            for (var i = 1; i < lines.Length && lines[i].Trim() != "---"; i++)
            {
                var colon = lines[i].IndexOf(':');
                if (colon > 0) result[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
            }
            return result;
        }

        private static string OneLine(string s) => Regex.Replace(s ?? "", @"\s+", " ").Trim();

        private static string Now() => DateTime.UtcNow.ToString("o");
    }
}
