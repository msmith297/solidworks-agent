using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Infrastructure;
using SwAgent.Tools;
using SwAgent.UI;

namespace SwAgent.Params
{
    /// <summary>
    /// Virtual parameters (docs/virtual-parameters.md): vparams (list, set, delete) and show_vparams (the table in the panel).
    /// The values describe the model; the agent changes the geometry itself and then records the values here.
    /// </summary>
    public sealed class VParamTools
    {
        public sealed class LinkInput
        {
            [ToolParam("The part's file title, e.g. \"Rail Collar.SLDPRT\".", Required = true)]
            public string Part { get; set; }

            [ToolParam("The part's parameter name.", Required = true)]
            public string Param { get; set; }
        }

        public sealed class ParamInput
        {
            [ToolParam("The name the user sees, plain words: \"Wall thickness\", \"Top diameter\", \"Screw count\".", Required = true)]
            public string Name { get; set; }

            [ToolParam("The value as the user would type it, without the unit: \"2.5\", \"8\", \"ACME\". Required for a new one.")]
            public string Value { get; set; }

            [ToolParam("length | angle | count | number | text | choice. Default length.")]
            public string Type { get; set; }

            [ToolParam("mm, in, deg, %, ... (none for counts and text). Default mm for a length, deg for an angle.")]
            public string Unit { get; set; }

            [ToolParam("What it controls, in a short line the user can read in the table.")]
            public string Note { get; set; }

            [ToolParam("The features it controls, by their exact feature-tree names (\"Shell1\", \"Collar Extrude\"), or dimensions " +
                       "(\"D1@Sketch1\"). Faces of these features select it with the P key. Replaces the list.")]
            public string[] Drives { get; set; }

            [ToolParam("A choice's allowed values.")]
            public string[] Options { get; set; }

            [ToolParam("Assembly parameters only: the parts' parameters it sets. Setting this one sets those to the same value.")]
            public LinkInput[] Links { get; set; }

            [ToolParam("A new name for it.")]
            public string Rename { get; set; }
        }

        public sealed class VParamsInput
        {
            [ToolParam("list | set | delete", Required = true)]
            public string Action { get; set; }

            [ToolParam("Whose parameters: a file title (\"Rail Collar.SLDPRT\"), a component name (\"Rail Collar-1\") or a path. " +
                       "Default the active document; list on an assembly also lists its parts'.")]
            public string Document { get; set; }

            [ToolParam("set: the parameters to create or update (matched by name; only the fields given change).")]
            public ParamInput[] Params { get; set; }

            [ToolParam("delete: the names to remove.")]
            public string[] Names { get; set; }
        }

        [AgentTool("vparams",
            "The virtual parameters of a part or assembly: named design values the user can change in a table (\"Wall thickness 2.5 mm\", " +
            "\"Hole count 8\"), stored in the file. list shows them; set creates or updates them after you've made the model match; " +
            "delete removes them. They don't drive geometry by themselves: you change the model, then record the values.")]
        public ToolResult VParams(ToolContext ctx, VParamsInput input)
        {
            var sw = ctx.Sw;
            var model = Resolve(sw, ctx.Model, input.Document, out var label);
            var doc = VParamDocument.For(model);
            if (doc == null) return ToolResult.Error(model == null ? $"No open document matches \"{input.Document}\"." : $"{label} isn't ready yet; try again.");
            switch ((input.Action ?? "").Trim().ToLowerInvariant())
            {
                case "list":
                    return ToolResult.Ok(List(model, doc, label));
                case "set":
                    return Set(sw, model, doc, label, input.Params ?? new ParamInput[0]);
                case "delete":
                {
                    var names = input.Names ?? new string[0];
                    var gone = names.Select(n => doc.Find(n)).Where(p => p != null).ToList();
                    if (gone.Count == 0) return ToolResult.Error($"{label} has none of those. Its parameters: {string.Join(", ", doc.Params.Select(p => p.Name))}.");
                    doc.Params.RemoveAll(gone.Contains);
                    doc.Commit(gone.Select(p => p.Name));
                    return ToolResult.Ok($"Deleted from {label}: {string.Join(", ", gone.Select(p => p.Name))}.");
                }
                default:
                    return ToolResult.Error("action is list, set or delete.");
            }
        }

        public sealed class ShowInput
        {
            [ToolParam("Whose table to open: a file title or component name. Default the active document.")]
            public string Document { get; set; }

            [ToolParam("Names to highlight in the table, e.g. the ones you're about to explain or change.")]
            public string[] Highlight { get; set; }
        }

        [AgentTool("show_vparams",
            "Open the virtual parameter table in the panel, where the user can see and edit the values (Apply sends you their changes). " +
            "Use it when they ask to see the parameters, or to point at the ones you're explaining.")]
        public ToolResult Show(ToolContext ctx, ShowInput input)
        {
            var model = Resolve(ctx.Sw, ctx.Model, input.Document, out var label);
            if (model == null) return ToolResult.Error($"No open document matches \"{input.Document}\".");
            AgentAddIn.Instance?.ShowParameters(new JObject
            {
                ["document"] = input.Document,
                ["highlight"] = new JArray(input.Highlight ?? new string[0]),
            });
            return ToolResult.Ok($"The table of {label} is open in the panel.");
        }

        // ---- list / set ----

        private static string List(IModelDoc2 model, VParamDocument doc, string label)
        {
            var sb = new StringBuilder();
            Describe(sb, label, doc.Params);
            if (model.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                var none = new List<string>();
                foreach (var g in VParamTable.Parts(model))
                {
                    var d = VParamDocument.For(g.Model);
                    if (d == null || d.Params.Count == 0) none.Add(g.Title);
                    else Describe(sb, $"{g.Title} ({string.Join(", ", g.Components)})", d.Params);
                }
                if (none.Count > 0) sb.AppendLine("Parts with none yet: " + string.Join(", ", none));
            }
            return sb.ToString().TrimEnd();
        }

        private static void Describe(StringBuilder sb, string label, List<VParam> list)
        {
            sb.AppendLine(list.Count == 0 ? $"{label}: none yet" : $"{label}:");
            foreach (var p in list)
            {
                sb.Append($"  - {p.Name} = {p.Display} [{p.Type}]");
                if (p.Options.Count > 0) sb.Append($" options: {string.Join(" | ", p.Options)}");
                if (p.Drives.Count > 0) sb.Append($" drives: {string.Join(", ", p.Drives)}");
                if (p.Links.Count > 0) sb.Append($" sets: {string.Join(", ", p.Links.Select(l => $"{l.Part} / {l.Param}"))}");
                if (p.Note.Length > 0) sb.Append($" - {p.Note}");
                sb.AppendLine();
            }
        }

        private static readonly string[] Types = { "length", "angle", "count", "number", "text", "choice" };

        private static ToolResult Set(ISldWorks sw, IModelDoc2 model, VParamDocument doc, string label, ParamInput[] items)
        {
            if (items.Length == 0) return ToolResult.Error("set needs params.");
            var isAssembly = model.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY;
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var lines = new List<string>();
            var removed = new List<string>();
            var pushes = new List<(VLink Link, VParam From)>();
            foreach (var it in items)
            {
                var name = (it.Name ?? "").Trim();
                if (name.Length == 0) return ToolResult.Error("Every parameter needs a name.");
                var p = doc.Find(name);
                var isNew = p == null;
                if (isNew)
                {
                    if (string.IsNullOrWhiteSpace(it.Value)) return ToolResult.Error($"\"{name}\" is new: give it a value.");
                    p = new VParam { Name = name };
                    doc.Params.Add(p);
                }
                if (it.Type != null)
                {
                    var t = it.Type.Trim().ToLowerInvariant();
                    if (!Types.Contains(t)) return ToolResult.Error($"type is one of {string.Join(", ", Types)}.");
                    p.Type = t;
                }
                if (it.Value != null) p.Value = it.Value.Trim();
                if (it.Unit != null) p.Unit = it.Unit.Trim();
                else if (isNew) p.Unit = p.Type == "length" ? "mm" : p.Type == "angle" ? "deg" : "";
                if (it.Note != null) p.Note = it.Note.Trim();
                if (it.Drives != null) p.Drives = it.Drives.Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d.Trim()).Distinct().ToList();
                if (it.Options != null) p.Options = it.Options.Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o.Trim()).ToList();
                if (it.Links != null)
                {
                    if (!isAssembly) return ToolResult.Error("links are for an assembly's parameters.");
                    p.Links = it.Links.Where(l => !string.IsNullOrWhiteSpace(l?.Part) && !string.IsNullOrWhiteSpace(l.Param))
                        .Select(l => new VLink { Part = l.Part.Trim(), Param = l.Param.Trim() }).ToList();
                }
                if (p.Type == "choice" && p.Options.Count > 0 && !p.Options.Contains(p.Value, StringComparer.OrdinalIgnoreCase))
                    return ToolResult.Error($"\"{p.Value}\" isn't one of {name}'s options ({string.Join(", ", p.Options)}).");
                if (!string.IsNullOrWhiteSpace(it.Rename) && it.Rename.Trim() != p.Name)
                {
                    if (doc.Find(it.Rename) != null) return ToolResult.Error($"{label} already has a parameter named \"{it.Rename.Trim()}\".");
                    removed.Add(p.Name);
                    p.Name = it.Rename.Trim();
                }
                p.Updated = today;
                lines.Add($"{p.Name} = {p.Display}{(isNew ? " (new)" : "")}");
                if (it.Value != null || it.Unit != null) pushes.AddRange(p.Links.Select(l => (l, p)));
            }
            doc.Commit(removed);

            var sb = new StringBuilder($"Set on {label}: {string.Join("; ", lines)}.");
            // An assembly parameter sets its parts' parameters (the file of each part, not a link between files).
            var pushed = new List<string>();
            foreach (var group in pushes.GroupBy(x => x.Link.Part, StringComparer.OrdinalIgnoreCase))
            {
                var part = VParamTable.Parts(model).FirstOrDefault(g => string.Equals(g.Title, group.Key, StringComparison.OrdinalIgnoreCase) ||
                                                                         g.Components.Contains(group.Key, StringComparer.OrdinalIgnoreCase));
                var target = part == null ? null : VParamDocument.For(part.Model);
                if (target == null)
                {
                    sb.Append($" Couldn't set {group.Key}: it isn't loaded in the assembly.");
                    continue;
                }
                foreach (var (link, from) in group)
                {
                    var tp = target.Find(link.Param);
                    if (tp == null)
                    {
                        tp = new VParam { Name = link.Param, Type = from.Type, Unit = from.Unit, Note = $"Set by {from.Name} in {label}" };
                        target.Params.Add(tp);
                    }
                    tp.Value = from.Value;
                    tp.Unit = from.Unit;
                    tp.Updated = today;
                    pushed.Add($"{part.Title} / {tp.Name} = {tp.Display}");
                }
                target.Commit();
            }
            if (pushed.Count > 0) sb.Append($" Also set: {string.Join("; ", pushed)}.");
            sb.Append(" These record values: make sure the model matches each one.");
            return ToolResult.Ok(sb.ToString());
        }

        // ---- documents ----

        /// <summary>The document a name means: a file title or path, a component of the active assembly, or (no name) the active one.</summary>
        internal static IModelDoc2 Resolve(ISldWorks sw, IModelDoc2 fallback, string name, out string label)
        {
            var active = sw?.ActiveDoc as IModelDoc2 ?? fallback;
            label = Title(active);
            if (string.IsNullOrWhiteSpace(name)) return active;
            name = name.Trim();
            if (active != null)
            {
                if (Matches(active, name)) return active;
                if (active.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY)
                {
                    var part = VParamTable.Parts(active).FirstOrDefault(g => string.Equals(g.Title, name, StringComparison.OrdinalIgnoreCase) ||
                                                                             string.Equals(Path.GetFileNameWithoutExtension(g.Title), name, StringComparison.OrdinalIgnoreCase) ||
                                                                             g.Components.Contains(name, StringComparer.OrdinalIgnoreCase));
                    if (part != null)
                    {
                        label = part.Title;
                        return part.Model;
                    }
                }
            }
            foreach (var d in ((sw?.GetDocuments() as object[]) ?? new object[0]).OfType<IModelDoc2>())
            {
                if (!Matches(d, name)) continue;
                label = Title(d);
                return d;
            }
            label = name;
            return null;
        }

        private static bool Matches(IModelDoc2 d, string name)
        {
            try
            {
                var path = d.GetPathName();
                return string.Equals(d.GetTitle(), name, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(path, name, StringComparison.OrdinalIgnoreCase) ||
                       (!string.IsNullOrEmpty(path) && (string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase) ||
                                                        string.Equals(Path.GetFileNameWithoutExtension(path), name, StringComparison.OrdinalIgnoreCase)));
            }
            catch
            {
                return false;
            }
        }

        internal static string Title(IModelDoc2 d)
        {
            try
            {
                var path = d?.GetPathName();
                return string.IsNullOrEmpty(path) ? d?.GetTitle() ?? "" : Path.GetFileName(path);
            }
            catch
            {
                return "";
            }
        }
    }

    /// <summary>
    /// The panel's table (vparams.table): the document's parameters and, in an assembly, each part's, filtered to what the
    /// selection touches when asked (the P key). The event vparams.changed refreshes it.
    /// </summary>
    internal static class VParamTable
    {
        internal sealed class Part
        {
            public string Title;
            public string Path;
            public IModelDoc2 Model;
            public List<string> Components = new List<string>();
        }

        /// <summary>The distinct part and sub-assembly files of an assembly (all levels), in tree order; suppressed ones are skipped.</summary>
        public static List<Part> Parts(IModelDoc2 assembly)
        {
            var list = new List<Part>();
            if (!(assembly is IAssemblyDoc asm)) return list;
            foreach (var c in ((asm.GetComponents(false) as object[]) ?? new object[0]).OfType<IComponent2>())
            {
                IModelDoc2 m;
                string name;
                try
                {
                    m = c.GetModelDoc2() as IModelDoc2;
                    name = c.Name2;
                }
                catch
                {
                    continue;
                }
                if (m == null) continue;
                var path = m.GetPathName();
                var key = string.IsNullOrEmpty(path) ? m.GetTitle() : path;
                var part = list.FirstOrDefault(p => string.Equals(p.Path, key, StringComparison.OrdinalIgnoreCase));
                if (part == null) list.Add(part = new Part { Title = VParamTools.Title(m), Path = key, Model = m });
                part.Components.Add(ShortName(name));
            }
            return list;
        }

        /// <summary>"Sub-1/Rail Collar-1" -> "Rail Collar-1".</summary>
        private static string ShortName(string name) => name?.Split('/').Last() ?? "";

        private static string Key(IModelDoc2 m)
        {
            var path = m?.GetPathName();
            return string.IsNullOrEmpty(path) ? m?.GetTitle() ?? "" : path;
        }

        public static void Attach(WebBridge bridge, Func<ISldWorks> sw)
        {
            bridge.Register("vparams.table", p => Build(sw(), (string)p?["document"], (bool?)p?["selection"] == true));
        }

        private static bool _subscribed;

        public static void Subscribe(Action<JObject> emit)
        {
            if (_subscribed) return;
            _subscribed = true;
            VParamDocument.Changed += d => emit(new JObject { ["document"] = VParamTools.Title(d.Model) });
        }

        public static JObject Build(ISldWorks sw, string document, bool fromSelection)
        {
            var model = VParamTools.Resolve(sw, null, document, out var label);
            if (model == null) return new JObject { ["document"] = null, ["groups"] = new JArray() };
            var isAssembly = model.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY;
            var parts = isAssembly ? Parts(model) : new List<Part>();
            string selLabel = null;
            var filter = fromSelection ? SelectionFilter(model, out selLabel) : null;
            var groups = new JArray();
            var own = VParamDocument.For(model)?.Params ?? new List<VParam>();

            // The parts first, so the assembly's own can match through their links; the assembly's own are listed first.
            var partGroups = new List<JObject>();
            var matchedParams = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // "title|name"
            foreach (var part in parts)
            {
                var list = VParamDocument.For(part.Model)?.Params ?? new List<VParam>();
                HashSet<string> features = null;
                var selected = filter != null && filter.TryGetValue(Key(part.Model), out features);
                var rows = new JArray(list.Select(v =>
                {
                    var hit = selected && Hits(v, features);
                    if (hit) matchedParams.Add(part.Title + "|" + v.Name);
                    var o = v.ToJson();
                    o["match"] = hit;
                    return o;
                }));
                if (list.Count == 0 && !selected) continue;
                partGroups.Add(new JObject
                {
                    ["scope"] = "part",
                    ["title"] = part.Title,
                    ["document"] = part.Title,
                    ["components"] = new JArray(part.Components.Distinct()),
                    ["selected"] = selected,
                    ["params"] = rows,
                });
            }
            HashSet<string> ownFeatures = null;
            var ownSelected = filter != null && filter.TryGetValue(Key(model), out ownFeatures);
            groups.Add(new JObject
            {
                ["scope"] = isAssembly ? "assembly" : "part",
                ["title"] = label,
                ["document"] = label,
                ["components"] = new JArray(),
                ["selected"] = ownSelected,
                ["params"] = new JArray(own.Select(v =>
                {
                    var o = v.ToJson();
                    o["match"] = (ownSelected && Hits(v, ownFeatures)) || v.Links.Any(l => matchedParams.Contains(l.Part + "|" + l.Param));
                    return o;
                })),
            });
            foreach (var g in partGroups) groups.Add(g);
            return new JObject
            {
                ["document"] = label,
                ["kind"] = isAssembly ? "assembly" : "part",
                ["filtered"] = filter != null,
                ["selection"] = filter != null ? selLabel : null,
                ["groups"] = groups,
            };
        }

        /// <summary>Whether a parameter drives one of the selected features (null: the whole document or component was selected).</summary>
        private static bool Hits(VParam v, HashSet<string> features)
        {
            if (features == null) return true;
            foreach (var d in v.Drives)
            {
                if (features.Contains(d)) return true;
                var at = d.Split('@'); // "D1@Boss-Extrude1" or "D1@Sketch1@Part.Part"
                if (at.Length > 1 && features.Contains(at[1])) return true;
            }
            return false;
        }

        /// <summary>
        /// The selected faces, edges, features and components as file key -> feature names (null: all of that file). In an
        /// assembly, a face of a component belongs to the component's file. Null when nothing is selected.
        /// </summary>
        private static Dictionary<string, HashSet<string>> SelectionFilter(IModelDoc2 model, out string label)
        {
            label = null;
            var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var sm = model.SelectionManager as ISelectionMgr;
            var count = sm?.GetSelectedObjectCount2(-1) ?? 0;
            if (count == 0) return null;
            var kinds = new List<string>();

            void Add(string key, string feature)
            {
                if (!result.TryGetValue(key, out var set)) result[key] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (set == null) return; // already the whole file
                if (feature == null) result[key] = null;
                else set.Add(feature);
            }

            for (var i = 1; i <= count; i++)
            {
                try
                {
                    var type = (swSelectType_e)sm.GetSelectedObjectType3(i, -1);
                    var obj = sm.GetSelectedObject6(i, -1);
                    var comp = sm.GetSelectedObjectsComponent4(i, -1) as IComponent2;
                    var key = comp != null ? Key(comp.GetModelDoc2() as IModelDoc2) : Key(model);
                    switch (type)
                    {
                        case swSelectType_e.swSelFACES:
                            Add(key, (obj as IFace2)?.GetFeature() is IFeature f ? f.Name : null);
                            kinds.Add("face");
                            break;
                        case swSelectType_e.swSelEDGES:
                            var faces = ((obj as IEdge)?.GetTwoAdjacentFaces2() as object[]) ?? new object[0];
                            foreach (var face in faces.OfType<IFace2>())
                                if (face.GetFeature() is IFeature ef) Add(key, ef.Name);
                            kinds.Add("edge");
                            break;
                        case swSelectType_e.swSelCOMPONENTS:
                            Add(Key((obj as IComponent2)?.GetModelDoc2() as IModelDoc2), null);
                            kinds.Add("component");
                            break;
                        default:
                            if (obj is IFeature feat)
                            {
                                Add(key, feat.Name);
                                kinds.Add("feature");
                            }
                            else
                            {
                                Add(key, null);
                                kinds.Add("item");
                            }
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Log.Info("Reading a selected item for the v params failed: " + ex.Message);
                }
            }
            label = string.Join(", ", kinds.GroupBy(k => k).Select(g => g.Count() == 1 ? $"1 {g.Key}" : $"{g.Count()} {g.Key}s"));
            return result.Count == 0 ? null : result;
        }
    }
}
