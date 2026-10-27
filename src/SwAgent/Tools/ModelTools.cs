using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SwAgent.Tools
{
    /// <summary>Read-mostly tools that describe the model to the agent.</summary>
    public sealed class ModelTools
    {
        private static readonly HashSet<string> NoiseFeatureTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "HistoryFolder", "SelectionSetFolder", "SensorFolder", "DocsFolder", "DetailCabinet", "CommentsFolder",
            "FavoriteFolder", "InkMarkupFolder", "EnvFolder", "EqnFolder", "MaterialFolder", "MBD3DViewFolder",
            "ProfileFeatureFolder", "LiveSectionFolder",
        };

        public sealed class NoInput
        {
        }

        public sealed class FeatureTreeInput
        {
            [ToolParam("Maximum number of top-level features to list (default 400).")]
            public int? MaxFeatures { get; set; }

            [ToolParam("Include sub-features such as sketches under extrusions and mates under the mate group (default true).")]
            public bool? IncludeSubFeatures { get; set; }
        }

        public sealed class UndoInput
        {
            [ToolParam("Number of undo steps (default 1). Each execute_csharp call is recorded as a single undo step.")]
            public int? Steps { get; set; }
        }

        public sealed class RebuildInput
        {
            [ToolParam("true = force rebuild of everything (Ctrl+Q); false = regular rebuild of changed features (default false).")]
            public bool? Force { get; set; }
        }

        [AgentTool("get_model_context",
            "Summary of the document this chat belongs to: name, path, type, units, configurations, material, " +
            "mass properties, bounding box, component or sheet counts, feature count and current selection. Call this first.")]
        public ToolResult GetModelContext(ToolContext ctx, NoInput input)
        {
            var model = ctx.RequireModel();
            var sb = new StringBuilder();
            var type = (swDocumentTypes_e)model.GetType();
            sb.AppendLine($"Title: {model.GetTitle()}");
            sb.AppendLine($"Path: {(string.IsNullOrEmpty(model.GetPathName()) ? "(not saved yet)" : model.GetPathName())}");
            sb.AppendLine($"Type: {type.ToString().Replace("swDoc", "")}");
            sb.AppendLine($"SolidWorks: {ctx.Sw.RevisionNumber()}");

            Try(() =>
            {
                var unit = (swLengthUnit_e)model.Extension.GetUserPreferenceInteger(
                    (int)swUserPreferenceIntegerValue_e.swUnitsLinear, (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified);
                sb.AppendLine($"Document length unit: {unit.ToString().Replace("sw", "")} (the API always uses meters and radians)");
            });

            Try(() =>
            {
                var active = (IConfiguration)model.GetActiveConfiguration();
                var names = (model.GetConfigurationNames() as string[]) ?? new string[0];
                sb.AppendLine($"Active configuration: {active?.Name} ({names.Length} total: {string.Join(", ", names.Take(20))}{(names.Length > 20 ? ", ..." : "")})");
            });

            Try(() => sb.AppendLine($"Feature count: {model.GetFeatureCount()}"));
            Try(() =>
            {
                var eq = model.GetEquationMgr();
                if (eq != null && eq.GetCount() > 0) sb.AppendLine($"Equations/global variables: {eq.GetCount()}");
            });

            if (type == swDocumentTypes_e.swDocPART)
            {
                var part = (IPartDoc)model;
                Try(() =>
                {
                    var material = part.GetMaterialPropertyName2("", out var db);
                    sb.AppendLine($"Material: {(string.IsNullOrEmpty(material) ? "(none)" : material + (string.IsNullOrEmpty(db) ? "" : $" [{db}]"))}");
                });
                Try(() =>
                {
                    var bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
                    var surfaces = part.GetBodies2((int)swBodyType_e.swSheetBody, true) as object[];
                    sb.AppendLine($"Bodies: {bodies?.Length ?? 0} solid, {surfaces?.Length ?? 0} surface");
                });
                Try(() =>
                {
                    if (part.GetPartBox(true) is double[] box && box.Length == 6)
                    {
                        sb.AppendLine("Bounding box (mm): " +
                                      $"X {Mm(box[0])}..{Mm(box[3])}, Y {Mm(box[1])}..{Mm(box[4])}, Z {Mm(box[2])}..{Mm(box[5])} " +
                                      $"(size {Mm(box[3] - box[0])} x {Mm(box[4] - box[1])} x {Mm(box[5] - box[2])})");
                    }
                });
            }

            if (type == swDocumentTypes_e.swDocPART || type == swDocumentTypes_e.swDocASSEMBLY)
            {
                Try(() =>
                {
                    var status = 0;
                    if (model.Extension.GetMassProperties2(1, out status, false) is double[] mp && mp.Length >= 6 && status == 0)
                    {
                        sb.AppendLine($"Mass: {mp[5]:0.####} kg, volume: {mp[3] * 1e9:0.##} mm^3, surface area: {mp[4] * 1e6:0.##} mm^2, " +
                                      $"center of mass (mm): ({Mm(mp[0])}, {Mm(mp[1])}, {Mm(mp[2])})");
                    }
                });
            }

            if (type == swDocumentTypes_e.swDocASSEMBLY)
            {
                Try(() =>
                {
                    var asm = (IAssemblyDoc)model;
                    sb.AppendLine($"Components: {asm.GetComponentCount(true)} top-level, {asm.GetComponentCount(false)} total");
                });
            }

            if (type == swDocumentTypes_e.swDocDRAWING)
            {
                Try(() =>
                {
                    var drw = (IDrawingDoc)model;
                    var sheets = (drw.GetSheetNames() as string[]) ?? new string[0];
                    sb.AppendLine($"Sheets: {string.Join(", ", sheets)} (current: {((ISheet)drw.GetCurrentSheet())?.GetName()})");
                });
            }

            var sel = DescribeSelection(model, 10);
            sb.AppendLine(sel.Count == 0 ? "Selection: (nothing selected)" : "Selection:\n  " + string.Join("\n  ", sel));
            return ToolResult.Ok(sb.ToString().TrimEnd());
        }

        [AgentTool("get_feature_tree",
            "FeatureManager design tree of the chat's document: every feature with its type name, suppression and " +
            "rebuild errors, plus sub-features (sketches, mates). Use the exact names with SelectByID2 or FeatureByName.")]
        public ToolResult GetFeatureTree(ToolContext ctx, FeatureTreeInput input)
        {
            var model = ctx.RequireModel();
            var max = input.MaxFeatures ?? 400;
            var includeSub = input.IncludeSubFeatures ?? true;
            var sb = new StringBuilder();
            var count = 0;
            var feat = (IFeature)model.FirstFeature();
            while (feat != null && count < max)
            {
                if (!NoiseFeatureTypes.Contains(feat.GetTypeName2()))
                {
                    count++;
                    AppendFeature(sb, feat, 0, includeSub);
                }
                feat = (IFeature)feat.GetNextFeature();
            }
            if (feat != null) sb.AppendLine($"... (truncated at {max} features)");
            return ToolResult.Ok(sb.Length == 0 ? "(empty feature tree)" : sb.ToString().TrimEnd());
        }

        [AgentTool("get_selection",
            "Entities currently selected in SolidWorks (by the user or by code): selection type, owning feature/component, " +
            "geometry facts (face area and surface type, edge type) and the pick point in mm. Index i maps to " +
            "SelectionManager.GetSelectedObject6(i, -1).")]
        public ToolResult GetSelection(ToolContext ctx, NoInput input)
        {
            var model = ctx.RequireModel();
            var items = DescribeSelection(model, 100);
            return ToolResult.Ok(items.Count == 0 ? "Nothing is selected." : string.Join("\n", items));
        }

        [AgentTool("list_open_documents", "All documents open in SolidWorks with type, path, visibility and unsaved-changes state, and which ones you opened (with when you last used them).")]
        public ToolResult ListOpenDocuments(ToolContext ctx, NoInput input)
        {
            var sb = new StringBuilder();
            var docs = ctx.Sw.GetDocuments() as object[] ?? new object[0];
            var active = ctx.Sw.ActiveDoc as IModelDoc2;
            var now = DocTracker.Now();
            foreach (var d in docs.OfType<IModelDoc2>())
            {
                var t = ((swDocumentTypes_e)d.GetType()).ToString().Replace("swDoc", "");
                var mine = d.Visible ? DocTracker.Get(d.GetTitle()) : null;
                var who = mine == null ? "" : $"(opened by you, last used {Math.Max(0, (int)(now - mine.LastUsed).TotalMinutes)} min ago) ";
                sb.AppendLine($"{(ReferenceEquals(d, active) ? "* " : "  ")}{d.GetTitle()} [{t}] {(d.Visible ? "" : "(hidden/component) ")}{(d.GetSaveFlag() ? "(unsaved changes) " : "")}{who}{d.GetPathName()}");
            }
            return ToolResult.Ok(sb.Length == 0 ? "No documents are open." : sb.ToString().TrimEnd());
        }

        public sealed class CloseDocumentsInput
        {
            [ToolParam("Titles (as list_open_documents shows them) or full paths of the document windows to close.")]
            public string[] Documents { get; set; }

            [ToolParam("Also close every document you opened that you haven't worked in for idle_minutes, except the active one.")]
            public bool? Idle { get; set; }

            [ToolParam("Minutes without use that count as idle (default 10).")]
            public int? IdleMinutes { get; set; }

            [ToolParam("Save changes before closing (default true). false discards them: only when the user asked for that.")]
            public bool? Save { get; set; }

            [ToolParam("true only when the user asked to close documents they opened themselves; otherwise those are left open.")]
            public bool? UserAsked { get; set; }
        }

        [AgentTool("close_documents", "Save and close document windows you no longer need, to keep SolidWorks light on long jobs and big assemblies: parts you finished, " +
            "references you looked at. Closing a part's window doesn't remove it from an open assembly. Never closes documents the user opened unless they asked " +
            "(user_asked), never closes a never-saved new document (save it with a file name first), and keeps anything whose save failed open.")]
        public ToolResult CloseDocuments(ToolContext ctx, CloseDocumentsInput input)
        {
            var sw = ctx.Sw ?? throw new ToolException("SolidWorks isn't available.");
            var open = (sw.GetDocuments() as object[] ?? new object[0]).OfType<IModelDoc2>().Where(d => d.Visible).ToList();
            var active = sw.ActiveDoc as IModelDoc2;
            var targets = new List<IModelDoc2>();
            var notes = new List<string>();
            foreach (var name in input.Documents ?? new string[0])
            {
                var d = open.FirstOrDefault(x => string.Equals(x.GetTitle(), name, StringComparison.OrdinalIgnoreCase) ||
                                                 string.Equals(x.GetPathName(), name, StringComparison.OrdinalIgnoreCase));
                if (d == null) notes.Add($"{name}: not open.");
                else if (!targets.Contains(d)) targets.Add(d);
            }
            if (input.Idle == true)
            {
                var idleFor = TimeSpan.FromMinutes(Math.Max(1, input.IdleMinutes ?? (int)DocTracker.IdleAfter.TotalMinutes));
                var now = DocTracker.Now();
                foreach (var d in open)
                {
                    var mine = DocTracker.Get(d.GetTitle());
                    if (mine != null && !ReferenceEquals(d, active) && now - mine.LastUsed >= idleFor && !targets.Contains(d)) targets.Add(d);
                }
            }
            if (targets.Count == 0 && notes.Count == 0) return ToolResult.Ok("Nothing to close: name the documents, or use idle: true.");

            var save = input.Save != false;
            var closed = new List<string>();
            foreach (var d in targets)
            {
                var title = d.GetTitle();
                if (DocTracker.Get(title) == null && input.UserAsked != true)
                {
                    notes.Add($"{title}: left open - the user opened it. Close it only if they ask (user_asked: true).");
                    continue;
                }
                var dirty = d.GetSaveFlag();
                if (dirty)
                {
                    if (save)
                    {
                        if (string.IsNullOrEmpty(d.GetPathName()))
                        {
                            notes.Add($"{title}: left open - it has never been saved. Save it with a file name first (SaveAs), then close it.");
                            continue;
                        }
                        int errors = 0, warnings = 0;
                        if (!d.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errors, ref warnings) || errors != 0)
                        {
                            notes.Add($"{title}: left open - saving failed (error {errors}).");
                            continue;
                        }
                    }
                }
                sw.CloseDoc(title);
                closed.Add(title + (dirty && !save ? " (changes discarded)" : ""));
            }
            var remaining = DocTracker.Snapshot(sw);
            DocTracker.Record(remaining, remaining); // forget what was closed
            var sb = new StringBuilder();
            sb.AppendLine(closed.Count > 0 ? $"Closed {closed.Count}: {string.Join(", ", closed)}." : "Closed nothing.");
            foreach (var n in notes) sb.AppendLine(n);
            sb.Append($"{remaining?.Count ?? 0} document window(s) still open.");
            return ToolResult.Ok(sb.ToString());
        }

        [AgentTool("undo", "Undo the last change(s) in the chat's document. Each execute_csharp call is one undo step named 'Agent: ...'.")]
        public ToolResult Undo(ToolContext ctx, UndoInput input)
        {
            var model = ctx.RequireModel();
            var steps = Math.Max(1, input.Steps ?? 1);
            var before = FeatureDiff.Take(model);
            model.EditUndo2(steps);
            model.GraphicsRedraw2();
            var changes = FeatureDiff.Describe(before, FeatureDiff.Take(model));
            return ToolResult.Ok($"Undid {steps} step(s). " + (changes ?? "The feature tree didn't change.") +
                                 " Compare with what your cell created: anything it added that is still listed in the tree was not undone.");
        }

        [AgentTool("rebuild", "Rebuild the chat's document and report features with rebuild errors or warnings afterwards.")]
        public ToolResult Rebuild(ToolContext ctx, RebuildInput input)
        {
            var model = ctx.RequireModel();
            var ok = input.Force == true ? model.ForceRebuild3(false) : model.EditRebuild3();
            var problems = new List<string>();
            var feat = (IFeature)model.FirstFeature();
            while (feat != null)
            {
                var code = feat.GetErrorCode2(out var isWarning);
                if (code != 0) problems.Add($"{feat.Name}: {(isWarning ? "warning" : "error")} {(swFeatureError_e)code}");
                feat = (IFeature)feat.GetNextFeature();
            }
            return ToolResult.Ok($"Rebuild {(ok ? "succeeded" : "reported failure")}." +
                                 (problems.Count == 0 ? " No feature errors." : "\n" + string.Join("\n", problems)));
        }

        private static void AppendFeature(StringBuilder sb, IFeature feat, int depth, bool includeSub)
        {
            var typeName = feat.GetTypeName2();
            var line = new StringBuilder();
            line.Append(new string(' ', depth * 2)).Append(feat.Name).Append(" [").Append(typeName).Append(']');

            Try(() =>
            {
                if (feat.IsSuppressed2((int)swInConfigurationOpts_e.swThisConfiguration, null) is bool[] s && s.Length > 0 && s[0])
                {
                    line.Append(" (suppressed)");
                }
            });
            Try(() =>
            {
                var code = feat.GetErrorCode2(out var isWarning);
                if (code != 0) line.Append($" ({(isWarning ? "warning" : "ERROR")}: {(swFeatureError_e)code})");
            });
            if (typeName == "Reference")
            {
                Try(() =>
                {
                    if (feat.GetSpecificFeature2() is IComponent2 comp) line.Append($" -> {comp.GetPathName()}");
                });
            }
            sb.AppendLine(line.ToString());

            if (!includeSub || depth >= 2) return;
            var sub = (IFeature)feat.GetFirstSubFeature();
            var n = 0;
            while (sub != null && n++ < 200)
            {
                AppendFeature(sb, sub, depth + 1, includeSub);
                sub = (IFeature)sub.GetNextSubFeature();
            }
        }

        /// <summary>One line per selected entity. Shared with the UI's selection chips.</summary>
        internal static List<string> DescribeSelection(IModelDoc2 model, int max)
        {
            var result = new List<string>();
            if (!(model?.SelectionManager is ISelectionMgr sm)) return result;
            var count = sm.GetSelectedObjectCount2(-1);
            for (var i = 1; i <= count && i <= max; i++)
            {
                var idx = i;
                var line = new StringBuilder($"[{idx}] ");
                Try(() =>
                {
                    var type = (swSelectType_e)sm.GetSelectedObjectType3(idx, -1);
                    line.Append(type.ToString().Replace("swSel", ""));
                    var obj = sm.GetSelectedObject6(idx, -1);
                    line.Append(DescribeObject(obj));
                    if (sm.GetSelectedObjectsComponent4(idx, -1) is IComponent2 comp) line.Append($" in component '{comp.Name2}'");
                    if (sm.GetSelectionPoint2(idx, -1) is double[] pt && pt.Length >= 3)
                    {
                        line.Append($" @ ({Mm(pt[0])}, {Mm(pt[1])}, {Mm(pt[2])}) mm");
                    }
                });
                result.Add(line.ToString());
            }
            if (count > max) result.Add($"... and {count - max} more");
            return result;
        }

        /// <summary>Short, chip-sized labels for the UI ("Face · Boss-Extrude1").</summary>
        internal static List<(string Type, string Label)> SelectionChips(IModelDoc2 model, int max)
        {
            var chips = new List<(string, string)>();
            if (!(model?.SelectionManager is ISelectionMgr sm)) return chips;
            var count = sm.GetSelectedObjectCount2(-1);
            for (var i = 1; i <= count && i <= max; i++)
            {
                try
                {
                    var type = SelectionTools.Pretty((swSelectType_e)sm.GetSelectedObjectType3(i, -1));
                    var obj = sm.GetSelectedObject6(i, -1);
                    string label = null;
                    switch (obj)
                    {
                        case IFeature f: label = f.Name; break;
                        case IFace2 face: label = (face.GetFeature() as IFeature)?.Name; break;
                        case IComponent2 c: label = c.Name2; break;
                    }
                    if (label == null && sm.GetSelectedObjectsComponent4(i, -1) is IComponent2 owner) label = owner.Name2;
                    chips.Add((type, label ?? ""));
                }
                catch
                {
                    chips.Add(("Entity", ""));
                }
            }
            return chips;
        }

        private static string DescribeObject(object obj)
        {
            switch (obj)
            {
                case IFeature f:
                    return $" '{f.Name}' [{f.GetTypeName2()}]";
                case IFace2 face:
                {
                    var sb = new StringBuilder();
                    Try(() =>
                    {
                        var surf = (ISurface)face.GetSurface();
                        var kind = surf.IsPlane() ? "planar" : surf.IsCylinder() ? "cylindrical" : surf.IsCone() ? "conical" :
                            surf.IsSphere() ? "spherical" : surf.IsTorus() ? "toroidal" : "freeform";
                        sb.Append($" ({kind}");
                        if (surf.IsCylinder() && surf.CylinderParams is double[] cp && cp.Length >= 7)
                        {
                            sb.Append($", radius {Mm(cp[6])} mm");
                        }
                        sb.Append($", area {face.GetArea() * 1e6:0.##} mm^2)");
                    });
                    Try(() =>
                    {
                        if (face.GetFeature() is IFeature owner) sb.Append($" of feature '{owner.Name}'");
                    });
                    return sb.ToString();
                }
                case IEdge edge:
                {
                    var sb = new StringBuilder();
                    Try(() =>
                    {
                        var curve = (ICurve)edge.GetCurve();
                        sb.Append(curve.IsLine() ? " (line" : curve.IsCircle() ? " (circular" : " (curve");
                        if (curve.IsCircle() && curve.CircleParams is double[] c && c.Length >= 7) sb.Append($", radius {Mm(c[6])} mm");
                        var p = edge.GetCurveParams3();
                        if (p != null) sb.Append($", length {Mm(curve.GetLength3(p.UMinValue, p.UMaxValue))} mm");
                        sb.Append(")");
                    });
                    return sb.ToString();
                }
                case IVertex _:
                    return "";
                case IComponent2 comp:
                    return $" '{comp.Name2}' -> {comp.GetPathName()}";
                default:
                    return "";
            }
        }

        internal static string Mm(double meters) => (meters * 1000).ToString("0.###", CultureInfo.InvariantCulture);

        private static void Try(Action a)
        {
            try
            {
                a();
            }
            catch
            {
                // Individual facts are best-effort; one failing property must not hide the rest.
            }
        }
    }
}
