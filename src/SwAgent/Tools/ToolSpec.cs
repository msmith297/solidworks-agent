using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwAgent.Infrastructure;

namespace SwAgent.Tools
{
    /// <summary>
    /// One input of a tool, shown as a native control in its PropertyManager page. Saved in the tool's inputs.json.
    /// </summary>
    public sealed class ToolInputSpec
    {
        [ToolParam("Identifier the macro / preview / agent use, e.g. \"faces\", \"offset\".", Required = true)]
        public string Id { get; set; }

        [ToolParam("selection | number | integer | checkbox | choice (dropdown) | options (radio buttons) | text (a note the user types or dictates) | " +
                   "files (images, PDFs or 3D models the user adds) | size (2 or 3 length boxes, see dims) | info (a read-only line, see text).", Required = true)]
        public string Type { get; set; }

        [ToolParam("Caption shown above the control, e.g. \"Faces to offset\". Converted units get their symbol appended.", Required = true)]
        public string Label { get; set; }

        [ToolParam("Tooltip with a short explanation.")]
        public string Tip { get; set; }

        [ToolParam("The tool can't run without it (default true for selection, false otherwise).")]
        public bool? Required { get; set; }

        [ToolParam("Default value: a number in the input's spec unit (mm, degrees, MPa, degC, kg, kN, cP, min), true/false, the default choice, " +
                   "or for size the values separated by commas (\"600,400,150\" mm).")]
        public string Default { get; set; }

        [ToolParam("number/integer/size: minimum, in the input's spec unit.")]
        public double? Min { get; set; }

        [ToolParam("number/integer/size: maximum, in the input's spec unit.")]
        public double? Max { get; set; }

        [ToolParam("number: length (entered in the document's units, default value in mm) | angle (degrees) | none | percent | pressure (MPa) | " +
                   "temperature (degC) | mass (kg) | force (kN) | tonnage (kN) | viscosity (cP) | time (min). The page shows the document's system " +
                   "(psi, degF, lb, lbf, US ton for inch documents); macros get SI: m, rad, Pa, degC, kg, N, Pa*s, s, percent 0-100.")]
        public string Unit { get; set; }

        [ToolParam("number: units the user can pick next to the box, remembered, e.g. [\"psi\",\"bar\",\"MPa\",\"kgf/cm2\"] or [\"US ton\",\"t\",\"kN\"]. " +
                   "Omit to follow the document.")]
        public string[] UnitChoices { get; set; }

        [ToolParam("selection: what can be picked - any of face, edge, vertex, feature, body, component, plane, sketch, sketch_segment, point.")]
        public string[] Filter { get; set; }

        [ToolParam("selection: allow several entities (default true). files: allow several files (default true).")]
        public bool? Multiple { get; set; }

        [ToolParam("choice / options: the entries.")]
        public string[] Choices { get; set; }

        [ToolParam("files: what may be added - any of image, pdf, model (STL, STEP, 3MF, IGES, SLDPRT...).")]
        public string[] Accept { get; set; }

        [ToolParam("Native SolidWorks picture next to the control: distance, depth, width, thickness, radius, diameter, angle, face, edge, vertex, " +
                   "face_edge, component, profile.")]
        public string Icon { get; set; }

        [ToolParam("Group box caption to put this input in (inputs with the same group share a box). Default: Selections / Parameters.")]
        public string Group { get; set; }

        [ToolParam("The first input of a group decides whether the group starts expanded (default true).")]
        public bool? GroupExpanded { get; set; }

        [ToolParam("choice: entries from a JSON file in the tool folder: \"data/grades.json#rows\" (a top-level key holding an array of row " +
                   "objects; without #, the file itself is the array).")]
        public string ChoicesFrom { get; set; }

        [ToolParam("choice with choices_from: the row field shown as the entry (default \"name\"). Entries are distinct, in file order.")]
        public string ChoiceKey { get; set; }

        [ToolParam("choice: the id of an earlier choice/options input whose value filters this one's rows (see row_filter).")]
        public string DependsOn { get; set; }

        [ToolParam("choice with choices_from: row field -> pattern, e.g. {\"family\": \"{mold_material}\", \"use\": \"plate\"}. {id} is that input's " +
                   "current value (after value_map); case-insensitive; an array field matches when it contains the pattern; \"*\" matches anything.")]
        public Dictionary<string, string> RowFilter { get; set; }

        [ToolParam("choice: maps another input's shown value before it goes into row_filter, e.g. {\"Tool steel\": \"steel\"}.")]
        public Dictionary<string, string> ValueMap { get; set; }

        [ToolParam("choice with choices_from: target input id -> dotted path in the selected row, e.g. {\"shrink_pct\": \"shrinkPct.typ\"}; " +
                   "picking an entry fills those (still editable) fields. Values are in the target's spec unit, or add |unit for data stored " +
                   "in another unit (\"moldingPressurePsi.typ|psi\"). A null value hides the target (n/a).")]
        public Dictionary<string, string> Fills { get; set; }

        [ToolParam("number/size: a checkbox (captioned with the label) switches the value on or off; off means \"not set\" (Inputs.Limit is null).")]
        public bool? Optional { get; set; }

        [ToolParam("number/size with optional: whether the checkbox starts on (default true).")]
        public bool? Enabled { get; set; }

        [ToolParam("Show this input only while other inputs have one of the listed values, e.g. {\"material\": [\"Aluminium\"]}. Hidden inputs " +
                   "are left out of what the macro and the agent get.")]
        public Dictionary<string, string[]> ShowWhen { get; set; }

        [ToolParam("number/integer/choice/options/checkbox/size: the default becomes the value last confirmed with OK on this PC.")]
        public bool? Remember { get; set; }

        [ToolParam("size: 2 or 3 length boxes (width, depth, thickness; default 3).")]
        public int? Dims { get; set; }

        [ToolParam("info: the line's text. {id} is an input's value, {id.field.sub} a field of that choice's selected row, and " +
                   "{...|temperature} (or another unit or symbol) shows a value given in that unit in the document's system.")]
        public string Text { get; set; }
    }

    /// <summary>A saved tool as the runner sees it: TOOL.md front matter + inputs.json + macro.cs + preview.cs.</summary>
    internal sealed class ToolSpec
    {
        public string Name;
        public string Title;
        public string Description;
        public string Kind;

        /// <summary>An agent finishes the job (otherwise the macro is the whole tool).</summary>
        public bool Agent;

        /// <summary>
        /// "agent: hybrid" (older: fallback): the macro runs first and does the job when it fits; when it meets a case it
        /// can't handle it calls NeedsAgent(...) (or fails) and the agent takes over with that context - adapts the code,
        /// finishes the job, then improves the macro so the next run handles it. Agent is true too, for that.
        /// </summary>
        public bool AgentFallback;

        /// <summary>fast | balanced | high | max: how much intelligence the agent part needs.</summary>
        public string Intelligence;

        /// <summary>"missing": skip the PropertyManager page when everything required is already selected. "always": always show it.</summary>
        public string Confirm;

        /// <summary>Context captured for the agent: view, selection, model, history, history_full.</summary>
        public HashSet<string> Context;

        public List<ToolInputSpec> Inputs;

        /// <summary>Parts paused after SolidWorks closed while running them ("preview", "macro") - see CrashGuard.</summary>
        public HashSet<string> Paused;
        public string Macro;
        public string Preview;

        /// <summary>The tool's folder (the user's, or the built-in one): data files named by ChoicesFrom live here.</summary>
        public string Dir;

        /// <summary>"category:": a box on the SolidWorks Agent tab and a section in the Workflows menu; null when not set.</summary>
        public string Category;

        /// <summary>"ribbon_title:": a shorter button caption than the title; null when not set.</summary>
        public string RibbonTitle;

        /// <summary>"long_run: true": a long, phased tool (more steps, a longer prompt-cache lifetime in the panel).</summary>
        public bool LongRun;

        /// <summary>"max_steps:": the agent's step budget for a run: default 200 (600 for long runs), clamped to 50-1000.</summary>
        public int MaxSteps = DefaultMaxSteps;

        /// <summary>"requires:": agent tools the tool needs (an older add-in without them refuses to run it).</summary>
        public string[] Requires = new string[0];

        public const int DefaultMaxSteps = UserTools.DefaultMaxSteps, LongRunMaxSteps = UserTools.LongRunMaxSteps, MinSteps = UserTools.MaxStepsMin, MaxStepsCap = UserTools.MaxStepsCap;

        public static readonly string[] Tiers = { "fast", "balanced", "high", "max" };

        public static ToolSpec Load(string name)
        {
            var dir = UserTools.DirOf(name); // the user's tool, or a built-in one
            if (dir == null) return null;
            var fm = UserTools.EffectiveFrontMatter(name, dir);
            string Get(string key, string fallback = null) => fm.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : fallback;

            var spec = new ToolSpec
            {
                Name = name,
                Dir = dir,
                Title = Get("title", name),
                Description = Get("description", ""),
                Kind = Get("kind", "workflow"),
                Macro = ReadOptional(Path.Combine(dir, "macro.cs")),
                Preview = ReadOptional(Path.Combine(dir, "preview.cs")),
                Inputs = LoadInputs(dir),
            };
            ApplyRunSettings(spec, fm);
            // A tool without a macro can only work through the agent.
            spec.Agent = spec.Macro == null || !string.Equals(Get("agent", "true"), "false", StringComparison.OrdinalIgnoreCase);
            var agentValue = Get("agent", "true").ToLowerInvariant();
            spec.AgentFallback = spec.Macro != null && (agentValue == "hybrid" || agentValue == "fallback");
            spec.Intelligence = Tiers.Contains(Get("intelligence")) ? Get("intelligence") : "balanced";
            spec.Confirm = Get("confirm", "missing") == "always" ? "always" : "missing";
            spec.Paused = new HashSet<string>((Get("paused", "") ?? "").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries));
            spec.Context = new HashSet<string>((Get("context", "view,selection,model,history") ?? "")
                .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim().ToLowerInvariant()));
            return spec;
        }

        /// <summary>
        /// category, ribbon_title, long_run, max_steps and requires from the (effective) front matter - one parser for the page,
        /// the ribbon, the tool index and the panel: UserTools.CategoryOf / LongRunOf / MaxStepsOf / RequiresOf.
        /// </summary>
        internal static void ApplyRunSettings(ToolSpec spec, IDictionary<string, string> fm)
        {
            fm = fm ?? new Dictionary<string, string>();
            spec.Category = UserTools.CategoryOf(fm);
            spec.RibbonTitle = UserTools.FrontValue(fm, "ribbon_title");
            spec.LongRun = UserTools.LongRunOf(fm);
            spec.MaxSteps = UserTools.MaxStepsOf(fm) ?? DefaultMaxSteps;
            spec.Requires = UserTools.RequiresOf(fm);
        }

        public static List<ToolInputSpec> LoadInputs(string dir)
        {
            var path = Path.Combine(dir, "inputs.json");
            if (!File.Exists(path)) return new List<ToolInputSpec>();
            List<ToolInputSpec> inputs;
            try
            {
                inputs = JsonConvert.DeserializeObject<List<ToolInputSpec>>(File.ReadAllText(path)) ?? new List<ToolInputSpec>();
            }
            catch (Exception ex)
            {
                Log.Warn($"Bad inputs.json in {dir}: {ex.Message}");
                return new List<ToolInputSpec>();
            }
            try
            {
                foreach (var warning in CheckInputs(dir, inputs)) Log.Warn($"inputs.json in {dir}: {warning}");
            }
            catch (Exception ex)
            {
                Log.Warn($"Checking inputs.json in {dir} failed: {ex.Message}");
            }
            return inputs;
        }

        /// <summary>
        /// Mistakes in the newer page options that still load (the page ignores what it can't use): a DependsOn that is missing
        /// or comes later, a ChoicesFrom file that doesn't exist, a Fills/ShowWhen/RowFilter id that doesn't exist, unknown
        /// units. Logged by LoadInputs; tests call it directly.
        /// </summary>
        internal static List<string> CheckInputs(string dir, IList<ToolInputSpec> inputs)
        {
            var warnings = new List<string>();
            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < inputs.Count; i++)
            {
                var id = inputs[i]?.Id;
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (index.ContainsKey(id)) warnings.Add($"'{id}' is declared twice");
                else index[id] = i;
            }
            for (var i = 0; i < inputs.Count; i++)
            {
                var s = inputs[i];
                if (s == null || string.IsNullOrWhiteSpace(s.Id)) continue;
                var type = Type(s);
                if (!string.IsNullOrWhiteSpace(s.DependsOn))
                {
                    if (!index.TryGetValue(s.DependsOn, out var p)) warnings.Add($"'{s.Id}' depends on '{s.DependsOn}', which doesn't exist");
                    else if (p >= i) warnings.Add($"'{s.Id}' depends on '{s.DependsOn}', which must be declared before it");
                }
                if (!string.IsNullOrWhiteSpace(s.ChoicesFrom))
                {
                    var file = ToolData.FileOf(s.ChoicesFrom);
                    if (!ToolData.IsSafeRelative(file)) warnings.Add($"'{s.Id}' choices_from '{s.ChoicesFrom}' must be a path inside the tool folder");
                    else if (dir != null && !File.Exists(Path.Combine(dir, file))) warnings.Add($"'{s.Id}' choices_from file '{file}' doesn't exist");
                }
                foreach (var target in (s.Fills ?? new Dictionary<string, string>()).Keys)
                    if (!index.ContainsKey(target)) warnings.Add($"'{s.Id}' fills '{target}', which doesn't exist");
                foreach (var key in (s.ShowWhen ?? new Dictionary<string, string[]>()).Keys)
                    if (!index.ContainsKey(key)) warnings.Add($"'{s.Id}' show_when names '{key}', which doesn't exist");
                foreach (var pattern in (s.RowFilter ?? new Dictionary<string, string>()).Values)
                    foreach (var reference in ToolData.References(pattern))
                    {
                        if (!index.TryGetValue(reference, out var p)) warnings.Add($"'{s.Id}' row_filter names '{{{reference}}}', which doesn't exist");
                        else if (p == i) warnings.Add($"'{s.Id}' row_filter names itself ('{{{reference}}}')");
                        else if (p > i) warnings.Add($"'{s.Id}' row_filter names '{{{reference}}}', which must be declared before it");
                    }
                if ((type == "number") && s.Unit != null && !Units.IsKnown(s.Unit))
                    warnings.Add($"'{s.Id}' has an unknown unit '{s.Unit}' (shown as a plain number)");
                if (s.UnitChoices != null && Units.Choices(s.Unit, s.UnitChoices).Count != s.UnitChoices.Length)
                    warnings.Add($"'{s.Id}' unit_choices has entries that aren't {Units.Quantity(s.Unit)} units: {string.Join(", ", s.UnitChoices)}");
                if (type == "size" && s.Dims != null && s.Dims != 2 && s.Dims != 3) warnings.Add($"'{s.Id}' dims must be 2 or 3");
                if (type == "info" && string.IsNullOrWhiteSpace(s.Text)) warnings.Add($"'{s.Id}' is an info line without text");
            }
            foreach (var cycle in DependencyCycles(inputs)) warnings.Add($"choices depend on each other in a circle: {cycle} (the page can't order them)");
            return warnings;
        }

        /// <summary>Circles among choices' DependsOn and RowFilter references ("a -> b -> a"), each reported once.</summary>
        internal static List<string> DependencyCycles(IList<ToolInputSpec> inputs)
        {
            var parents = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in inputs ?? new List<ToolInputSpec>())
            {
                if (s == null || string.IsNullOrWhiteSpace(s.Id) || parents.ContainsKey(s.Id)) continue;
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(s.DependsOn)) set.Add(s.DependsOn.Trim());
                foreach (var pattern in (s.RowFilter ?? new Dictionary<string, string>()).Values)
                    foreach (var r in ToolData.References(pattern)) set.Add(r);
                set.Remove(s.Id); // a self-reference is reported on its own
                parents[s.Id] = set;
            }
            var cycles = new List<string>();
            var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // 1 on the path, 2 done
            var path = new List<string>();
            void Visit(string id)
            {
                state[id] = 1;
                path.Add(id);
                foreach (var p in parents[id])
                {
                    if (!parents.ContainsKey(p)) continue;
                    state.TryGetValue(p, out var st);
                    if (st == 0) Visit(p);
                    else if (st == 1)
                    {
                        var loop = path.Skip(path.FindLastIndex(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase))).ToList();
                        if (loop.Any(reported.Add)) cycles.Add(string.Join(" -> ", loop.Concat(new[] { p })));
                    }
                }
                path.RemoveAt(path.Count - 1);
                state[id] = 2;
            }
            foreach (var id in parents.Keys.ToList())
                if (!state.ContainsKey(id)) Visit(id);
            return cycles;
        }

        public static void SaveInputs(string dir, IEnumerable<ToolInputSpec> inputs)
        {
            var list = (inputs ?? Enumerable.Empty<ToolInputSpec>()).Where(i => !string.IsNullOrWhiteSpace(i?.Id)).ToList();
            var path = Path.Combine(dir, "inputs.json");
            if (list.Count == 0)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            File.WriteAllText(path, JsonConvert.SerializeObject(list, Formatting.Indented, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));
        }

        public static bool IsRequired(ToolInputSpec i) => i.Required ?? Type(i) == "selection";

        public static string Type(ToolInputSpec i) => (i.Type ?? "").Trim().ToLowerInvariant();

        private static string ReadOptional(string path) => File.Exists(path) ? File.ReadAllText(path) : null;
    }
}
