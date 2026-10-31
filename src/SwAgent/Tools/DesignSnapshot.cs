using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Infrastructure;
using SwAgent.ModelFiles;

namespace SwAgent.Tools
{
    /// <summary>
    /// Smart Rebuild's memory: a snapshot of what a design IS before a change far back in its history (every feature,
    /// what it references, what it produced, its dimensions; every body's size; the inserted customer parts), and a
    /// comparison after the change that says exactly what broke, what silently changed and where to start repairing.
    /// Snapshots are JSON files next to the logs, so a long rebuild can refer back to them at any point.
    /// </summary>
    public sealed class DesignSnapshot
    {
        public sealed class SnapshotInput
        {
            [ToolParam("Name for this snapshot (default \"baseline\"). Take one before changing anything.")]
            public string Name { get; set; }

            [ToolParam("Snapshot the last SAVED version of the document instead of the one in memory - for when the change has already been " +
                       "made but not saved. A temporary copy of the file is opened hidden, read and closed; the open document isn't touched.")]
            public bool? FromSavedFile { get; set; }
        }

        public sealed class CompareInput
        {
            [ToolParam("The snapshot to compare against (default \"baseline\").")]
            public string Baseline { get; set; }

            [ToolParam("Force a full rebuild first (default true).")]
            public bool? Rebuild { get; set; }
        }

        public sealed class ReplaceInput
        {
            [ToolParam("The Insert Part / derived part feature to point at another file (its name in the tree).", Required = true)]
            public string Feature { get; set; }

            [ToolParam("Full path of the new part file.", Required = true)]
            public string Path { get; set; }
        }

        [AgentTool("snapshot_design",
            "Record the design as it is now - every feature (type, parents, sketch plane / face it sits on, dimensions, the faces it " +
            "produced, errors), every body (volume, area, bounding box), inserted parts and equations - as a named snapshot. Smart " +
            "Rebuild takes one before the change so it knows what each feature meant. Returns a summary.")]
        public ToolResult Snapshot(ToolContext ctx, SnapshotInput input)
        {
            var model = ctx.RequireModel();
            var snap = input.FromSavedFile == true ? TakeFromSavedFile(ctx.Sw, model) : Take(ctx.Sw, model);
            if (snap == null) return ToolResult.Error("The document has never been saved, so there is no earlier version to read.");
            var name = Safe(string.IsNullOrWhiteSpace(input.Name) ? "baseline" : input.Name);
            var path = FileFor(model, name);
            File.WriteAllText(path, snap.ToString());
            var features = (JArray)snap["features"];
            var bodies = (JArray)snap["bodies"];
            var sb = new StringBuilder();
            sb.AppendLine($"Snapshot '{name}' saved: {features.Count} features, {bodies.Count} bodies, {((JArray)snap["insertedParts"]).Count} inserted part(s).");
            foreach (var p in (JArray)snap["insertedParts"]) sb.AppendLine($"  inserted part: {p["feature"]} <- {p["path"]}");
            foreach (var b in bodies) sb.AppendLine($"  body '{b["name"]}': volume {Mm3(b["volume"])} mm^3, size {Size(b["box"])} mm");
            var broken = features.Where(f => (int?)f["error"] > 0).Select(f => (string)f["name"]).ToList();
            if (broken.Count > 0) sb.AppendLine("  already broken before the change: " + string.Join(", ", broken));
            return ToolResult.Ok(sb.ToString().TrimEnd());
        }

        [AgentTool("compare_design",
            "After a change: rebuild and compare the design with a snapshot. Lists, in tree order: features with rebuild errors or " +
            "warnings (with SolidWorks' What's Wrong), features that silently changed (lost or gained faces, moved sketch planes, " +
            "changed dimensions), missing or new features, and bodies whose volume or size changed. The first entry is where repair starts.")]
        public ToolResult Compare(ToolContext ctx, CompareInput input)
        {
            var model = ctx.RequireModel();
            var name = Safe(string.IsNullOrWhiteSpace(input.Baseline) ? "baseline" : input.Baseline);
            var path = FileFor(model, name);
            if (!File.Exists(path)) return ToolResult.Error($"No snapshot '{name}' for this document - take one with snapshot_design first.");
            var before = JObject.Parse(File.ReadAllText(path));
            if (input.Rebuild != false) UI.SceneOverlay.QuietRebuild(() => model.ForceRebuild3(false)); // a check: what's drawn stays
            var after = Take(ctx.Sw, model);
            return ToolResult.Ok(Diff(before, after));
        }

        [AgentTool("replace_inserted_part",
            "Point an Insert Part (derived part) feature at another part file - e.g. the customer's revised part - then rebuild. " +
            "Take a snapshot first; afterwards compare_design shows what the new part broke or changed.")]
        public ToolResult Replace(ToolContext ctx, ReplaceInput input)
        {
            var model = ctx.RequireModel();
            if (!File.Exists(input.Path)) return ToolResult.Error("No file at " + input.Path);
            var feature = (IFeature)((IPartDoc)model).FeatureByName(input.Feature);
            if (feature == null) return ToolResult.Error($"No feature named '{input.Feature}'.");
            if (!(feature.GetDefinition() is IDerivedPartFeatureData data))
                return ToolResult.Error($"'{input.Feature}' ({feature.GetTypeName2()}) isn't an Insert Part / derived part feature.");
            var old = data.PathName;
            data.PathName = input.Path;
            if (!feature.ModifyDefinition(data, model, null))
                return ToolResult.Error($"SolidWorks didn't accept the new file for '{input.Feature}' (still {old}).");
            model.ForceRebuild3(false);
            return ToolResult.Ok($"'{input.Feature}' now uses {input.Path} (was {old}). Rebuilt - run compare_design to see what changed.");
        }

        /// <summary>The last saved version: copied to a temp file (the original is open), opened hidden, read, closed.</summary>
        private static JObject TakeFromSavedFile(ISldWorks sw, IModelDoc2 model)
        {
            var path = model.GetPathName();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "SolidWorksAgent-snapshot-" + Guid.NewGuid().ToString("N"))).FullName;
            // A different name: SolidWorks won't open two documents with the same name, and the close below must never hit the user's.
            var copy = Path.Combine(dir, Path.GetFileNameWithoutExtension(path) + " (saved version " + Guid.NewGuid().ToString("N").Substring(0, 8) + ")" + Path.GetExtension(path));
            File.Copy(path, copy);
            var type = model.GetType();
            IModelDoc2 saved = null;
            IDisposable hidden = null;
            try
            {
                // the one gate for every hidden open (critique B6): no two hidden opens interleave, visibility always comes back
                hidden = HiddenDocuments.Scope(sw);
                int errors = 0, warnings = 0;
                saved = sw.OpenDoc6(copy, type, (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly), "", ref errors, ref warnings);
                if (saved == null) throw new InvalidOperationException($"SolidWorks couldn't open the saved version ({(swFileLoadError_e)errors}).");
                if (ReferenceEquals(saved, model) || !string.Equals(saved.GetPathName(), copy, StringComparison.OrdinalIgnoreCase))
                {
                    saved = null; // not our temporary copy - never close it
                    throw new InvalidOperationException("SolidWorks returned the open document instead of the saved copy.");
                }
                var snap = Take(sw, saved);
                snap["path"] = path;
                snap["source"] = "last saved version";
                return snap;
            }
            finally
            {
                try
                {
                    if (saved != null) sw.CloseDoc(saved.GetTitle()); // the temp copy: its unique name makes the title unique too
                }
                finally
                {
                    hidden?.Dispose();
                }
                try
                {
                    Directory.Delete(dir, true);
                }
                catch
                {
                    // Leave it to the temp-folder cleanup.
                }
            }
        }

        // ---------- taking a snapshot ----------

        internal static JObject Take(ISldWorks sw, IModelDoc2 model)
        {
            var features = new JArray();
            var inserted = new JArray();
            var index = 0;
            var wrong = WhatsWrong(model);
            for (var f = (IFeature)model.FirstFeature(); f != null; f = (IFeature)f.GetNextFeature())
            {
                var type = f.GetTypeName2();
                if (Folders.Contains(type)) continue;
                AddFeature(features, inserted, f, type, ++index, wrong, null);
                // Sketches and other sub-features (absorbed) carry most of the references.
                for (var sub = (IFeature)f.GetFirstSubFeature(); sub != null; sub = (IFeature)sub.GetNextSubFeature())
                    AddFeature(features, inserted, sub, sub.GetTypeName2(), index, wrong, f.Name);
            }
            return new JObject
            {
                ["document"] = model.GetTitle(),
                ["path"] = model.GetPathName(),
                ["at"] = DateTime.Now.ToString("o"),
                ["features"] = features,
                ["bodies"] = Bodies(model),
                ["insertedParts"] = inserted,
                ["equations"] = Equations(model),
            };
        }

        private static readonly HashSet<string> Folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "HistoryFolder", "SelectionSetFolder", "SensorFolder", "DocsFolder", "DetailCabinet", "CommentsFolder", "FavoriteFolder",
            "InkMarkupFolder", "EnvFolder", "MBD3DViewFolder", "LiveSectionFolder", "SurfaceBodyFolder", "SolidBodyFolder",
            "MaterialFolder", "EqnFolder", "OriginProfileFeature", "DetailCabinet",
        };

        private static void AddFeature(JArray features, JArray inserted, IFeature f, string type, int index, Dictionary<string, string> wrong, string owner)
        {
            if (features.Any(x => (string)x["name"] == f.Name)) return; // shared sketches appear under several features
            var o = new JObject { ["index"] = index, ["name"] = f.Name, ["type"] = type };
            if (owner != null) o["owner"] = owner;
            try
            {
                var code = f.GetErrorCode2(out var isWarning);
                if (code != 0)
                {
                    o["error"] = code;
                    o["errorName"] = Enum.GetName(typeof(swFeatureError_e), code) ?? code.ToString();
                    o["warning"] = isWarning;
                }
                if (wrong.TryGetValue(f.Name, out var w)) o["whatsWrong"] = w;
                var sup = f.IsSuppressed2((int)swInConfigurationOpts_e.swThisConfiguration, null) as bool[];
                if (sup != null && sup.Length > 0 && sup[0]) o["suppressed"] = true;
                var parents = ((object[])f.GetParents() ?? new object[0]).Cast<IFeature>().Select(p => p.Name).ToList();
                if (parents.Count > 0) o["parents"] = new JArray(parents);
                var dims = new JArray();
                for (var dd = f.GetFirstDisplayDimension(); dd != null; dd = f.GetNextDisplayDimension(dd))
                {
                    var dim = (IDimension)((IDisplayDimension)dd).GetDimension2(0);
                    if (dim == null) continue;
                    var v = (double[])dim.GetSystemValue3((int)swInConfigurationOpts_e.swThisConfiguration, null);
                    dims.Add(new JObject { ["name"] = dim.FullName, ["value"] = v != null && v.Length > 0 ? Math.Round(v[0], 9) : (double?)null });
                }
                if (dims.Count > 0) o["dims"] = dims;
                if (f.GetSpecificFeature2() is ISketch sketch) o["sketchOn"] = SketchPlane(sketch);
                if (f.GetDefinition() is IDerivedPartFeatureData derived)
                {
                    o["insertedPart"] = derived.PathName;
                    inserted.Add(new JObject { ["feature"] = f.Name, ["path"] = derived.PathName });
                }
                var faces = ((object[])f.GetFaces() ?? new object[0]).Cast<IFace2>().ToList();
                if (faces.Count > 0)
                {
                    o["faces"] = faces.Count;
                    o["faceArea"] = Math.Round(faces.Sum(x => x.GetArea()), 10);
                    var bodies = faces.Select(x => ((IBody2)x.GetBody())?.Name).Where(n => n != null).Distinct().ToList();
                    o["bodies"] = new JArray(bodies);
                }
            }
            catch (Exception ex)
            {
                o["readError"] = ex.Message;
            }
            features.Add(o);
        }

        /// <summary>What a sketch sits on: a plane feature by name, or a face described by its geometry and owner.</summary>
        private static JObject SketchPlane(ISketch sketch)
        {
            try
            {
                var type = 0;
                var entity = sketch.GetReferenceEntity(ref type);
                switch (entity)
                {
                    case IFace2 face:
                    {
                        var n = (double[])face.Normal;
                        var box = (double[])face.GetBox();
                        var p = (double[])face.GetClosestPointOn((box[0] + box[3]) / 2, (box[1] + box[4]) / 2, (box[2] + box[5]) / 2);
                        return new JObject
                        {
                            ["kind"] = "face",
                            ["feature"] = ((IFeature)face.GetFeature())?.Name,
                            ["body"] = ((IBody2)face.GetBody())?.Name,
                            ["normal"] = new JArray(n.Select(x => Math.Round(x, 6))),
                            ["point"] = p == null ? null : new JArray(p.Take(3).Select(x => Math.Round(x, 8))),
                            ["area"] = Math.Round(face.GetArea(), 10),
                        };
                    }
                    case IFeature plane:
                        return new JObject { ["kind"] = "plane", ["feature"] = plane.Name };
                    case IRefPlane _:
                        return new JObject { ["kind"] = "plane" };
                    case null:
                        return new JObject { ["kind"] = "none" };
                    default:
                        return new JObject { ["kind"] = ((swSelectType_e)type).ToString() };
                }
            }
            catch
            {
                return null;
            }
        }

        private static JArray Bodies(IModelDoc2 model)
        {
            var result = new JArray();
            if (!(model is IPartDoc part)) return result;
            foreach (IBody2 b in (object[])part.GetBodies2((int)swBodyType_e.swSolidBody, false) ?? new object[0])
            {
                var m = (double[])b.GetMassProperties(1.0);
                var box = (double[])b.GetBodyBox();
                result.Add(new JObject
                {
                    ["name"] = b.Name,
                    ["volume"] = m != null && m.Length > 3 ? Math.Round(m[3], 12) : (double?)null,
                    ["area"] = m != null && m.Length > 4 ? Math.Round(m[4], 10) : (double?)null,
                    ["box"] = box == null ? null : new JArray(box.Select(x => Math.Round(x, 8))),
                    ["faces"] = b.GetFaceCount(),
                });
            }
            return result;
        }

        private static JArray Equations(IModelDoc2 model)
        {
            var result = new JArray();
            try
            {
                var eq = model.GetEquationMgr();
                for (var i = 0; eq != null && i < eq.GetCount(); i++) result.Add(eq.Equation[i]);
            }
            catch
            {
                // No equations.
            }
            return result;
        }

        private static Dictionary<string, string> WhatsWrong(IModelDoc2 model)
        {
            var result = new Dictionary<string, string>();
            try
            {
                if (!model.Extension.GetWhatsWrong(out var features, out var codes, out var warnings)) return result;
                var fs = (object[])features ?? new object[0];
                var cs = (int[])codes ?? new int[0];
                var ws = (bool[])warnings ?? new bool[0];
                for (var i = 0; i < fs.Length; i++)
                {
                    var name = (fs[i] as IFeature)?.Name;
                    if (name == null) continue;
                    var code = i < cs.Length ? cs[i] : 0;
                    var text = (i < ws.Length && ws[i] ? "warning: " : "error: ") + (Enum.GetName(typeof(swFeatureError_e), code) ?? code.ToString());
                    result[name] = result.TryGetValue(name, out var prev) ? prev + "; " + text : text;
                }
            }
            catch
            {
                // What's Wrong unavailable: the per-feature error codes still tell.
            }
            return result;
        }

        // ---------- comparing ----------

        internal static string Diff(JObject before, JObject after)
        {
            var sb = new StringBuilder();
            var b = ((JArray)before["features"]).OfType<JObject>().ToDictionary(f => (string)f["name"]);
            var a = ((JArray)after["features"]).OfType<JObject>().ToList();
            var lines = new List<(int Index, string Text)>();

            foreach (var f in a)
            {
                var name = (string)f["name"];
                var idx = (int)f["index"];
                var label = $"{name} [{f["type"]}]" + (f["owner"] != null ? $" (in {f["owner"]})" : "");
                if (!b.TryGetValue(name, out var old))
                {
                    lines.Add((idx, $"NEW {label}"));
                    continue;
                }
                var notes = new List<string>();
                if ((int?)f["error"] > 0 && (int?)old["error"] != (int?)f["error"])
                    notes.Add(((bool?)f["warning"] == true ? "WARNING " : "ERROR ") + f["errorName"] + (f["whatsWrong"] != null ? $" ({f["whatsWrong"]})" : ""));
                else if (f["whatsWrong"] != null && (string)old["whatsWrong"] != (string)f["whatsWrong"]) notes.Add("What's Wrong: " + f["whatsWrong"]);
                if ((int?)old["error"] > 0 && !((int?)f["error"] > 0)) notes.Add("error cleared");
                if ((bool?)f["suppressed"] != (bool?)old["suppressed"]) notes.Add((bool?)f["suppressed"] == true ? "now suppressed" : "no longer suppressed");
                int of = (int?)old["faces"] ?? 0, nf = (int?)f["faces"] ?? 0;
                if (of > 0 && nf == 0) notes.Add($"produces no faces any more (had {of}) - it no longer does anything");
                else if (of != nf) notes.Add($"faces {of} -> {nf}");
                double oa = (double?)old["faceArea"] ?? 0, na = (double?)f["faceArea"] ?? 0;
                if (oa > 0 && na > 0 && Math.Abs(na - oa) / oa > 0.02) notes.Add($"face area {(na - oa) / oa:+0%;-0%}");
                var ob = string.Join(",", (old["bodies"] as JArray ?? new JArray()).Select(x => (string)x));
                var nb = string.Join(",", (f["bodies"] as JArray ?? new JArray()).Select(x => (string)x));
                if (ob != nb && (ob.Length > 0 || nb.Length > 0)) notes.Add($"bodies [{ob}] -> [{nb}]");
                var sketchNote = SketchChange(old["sketchOn"] as JObject, f["sketchOn"] as JObject);
                if (sketchNote != null) notes.Add(sketchNote);
                foreach (var d in (f["dims"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    var od = (old["dims"] as JArray ?? new JArray()).OfType<JObject>().FirstOrDefault(x => (string)x["name"] == (string)d["name"]);
                    if (od != null && (double?)od["value"] is double ov && (double?)d["value"] is double nv && Math.Abs(ov - nv) > 1e-9)
                        notes.Add($"{d["name"]} {Mm(ov)} -> {Mm(nv)}");
                }
                if ((string)old["insertedPart"] != (string)f["insertedPart"]) notes.Add($"inserted part {old["insertedPart"]} -> {f["insertedPart"]}");
                if (notes.Count > 0) lines.Add((idx, $"{label}: {string.Join("; ", notes)}"));
            }
            foreach (var gone in b.Values.Where(o => a.All(f => (string)f["name"] != (string)o["name"])))
                lines.Add(((int)gone["index"], $"MISSING {gone["name"]} [{gone["type"]}] - was in the design"));

            var errors = a.Count(f => (int?)f["error"] > 0 && (bool?)f["warning"] != true);
            var warnings = a.Count(f => (int?)f["error"] > 0 && (bool?)f["warning"] == true);
            sb.AppendLine($"Compared with snapshot taken {before["at"]}: {errors} feature(s) with errors, {warnings} with warnings, {lines.Count} change(s).");
            if (lines.Count > 0)
            {
                sb.AppendLine("Features, in tree order (repair from the top):");
                foreach (var l in lines.OrderBy(x => x.Index).Take(120)) sb.AppendLine($"  {l.Index}. {l.Text}");
                if (lines.Count > 120) sb.AppendLine($"  ... {lines.Count - 120} more");
            }

            sb.AppendLine("Bodies:");
            var bb = ((JArray)before["bodies"]).OfType<JObject>().ToList();
            var ab = ((JArray)after["bodies"]).OfType<JObject>().ToList();
            foreach (var body in ab)
            {
                var old = bb.FirstOrDefault(x => (string)x["name"] == (string)body["name"]);
                if (old == null)
                {
                    sb.AppendLine($"  NEW body '{body["name"]}': volume {Mm3(body["volume"])} mm^3, size {Size(body["box"])} mm");
                    continue;
                }
                double ov = (double?)old["volume"] ?? 0, nv = (double?)body["volume"] ?? 0;
                var change = ov > 0 ? (nv - ov) / ov : 0;
                var sizeNote = Size(old["box"]) == Size(body["box"]) ? "same size" : $"size {Size(old["box"])} -> {Size(body["box"])} mm";
                sb.AppendLine($"  '{body["name"]}': volume {(Math.Abs(change) < 1e-4 ? "unchanged" : $"{change:+0.0%;-0.0%}")}, {sizeNote}" +
                              ((int?)old["faces"] != (int?)body["faces"] ? $", faces {old["faces"]} -> {body["faces"]}" : ""));
            }
            foreach (var gone in bb.Where(o => ab.All(x => (string)x["name"] != (string)o["name"])))
                sb.AppendLine($"  MISSING body '{gone["name"]}' (volume {Mm3(gone["volume"])} mm^3) - a feature that made or kept it failed, or it merged into another body");
            return sb.ToString().TrimEnd();
        }

        private static string SketchChange(JObject old, JObject now)
        {
            if (old == null && now == null) return null;
            if ((string)old?["kind"] == "face" && (string)now?["kind"] != "face") return $"sketch lost its face (was on a face of {old["feature"]}); now {now?["kind"] ?? "nothing"}";
            if ((string)old?["kind"] == "face" && (string)now?["kind"] == "face")
            {
                var notes = new List<string>();
                if ((string)old["feature"] != (string)now["feature"]) notes.Add($"now on a face of {now["feature"]} (was {old["feature"]})");
                var on = (old["normal"] as JArray)?.Select(x => (double)x).ToArray();
                var nn = (now["normal"] as JArray)?.Select(x => (double)x).ToArray();
                if (on != null && nn != null && on[0] * nn[0] + on[1] * nn[1] + on[2] * nn[2] < 0.9999) notes.Add("its face turned");
                var op = (old["point"] as JArray)?.Select(x => (double)x).ToArray();
                var np = (now["point"] as JArray)?.Select(x => (double)x).ToArray();
                if (op != null && np != null && on != null)
                {
                    var shift = (np[0] - op[0]) * on[0] + (np[1] - op[1]) * on[1] + (np[2] - op[2]) * on[2];
                    if (Math.Abs(shift) > 1e-6) notes.Add($"its face moved {Mm(shift)} along its normal");
                }
                return notes.Count > 0 ? "sketch plane: " + string.Join(", ", notes) : null;
            }
            return (string)old?["kind"] != (string)now?["kind"] ? $"sketch plane {old?["kind"] ?? "none"} -> {now?["kind"] ?? "none"}" : null;
        }

        private static string Mm(double meters) => (meters * 1000).ToString("0.###") + " mm";
        private static string Mm3(JToken v) => v == null || v.Type == JTokenType.Null ? "?" : ((double)v * 1e9).ToString("0");

        private static string Size(JToken box)
        {
            var b = (box as JArray)?.Select(x => (double)x).ToArray();
            return b == null || b.Length < 6 ? "?" : $"{(b[3] - b[0]) * 1000:0.##} x {(b[4] - b[1]) * 1000:0.##} x {(b[5] - b[2]) * 1000:0.##}";
        }

        private static string FileFor(IModelDoc2 model, string name)
        {
            var doc = Safe(Path.GetFileNameWithoutExtension(model.GetPathName()) is string s && s.Length > 0 ? s : model.GetTitle());
            var dir = Directory.CreateDirectory(Path.Combine(Paths.LocalDir, "snapshots", doc)).FullName;
            return Path.Combine(dir, name + ".json");
        }

        private static string Safe(string s) => new string(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
    }
}
