using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Scripting;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Infrastructure;
using SwAgent.Scripting;
using SwAgent.UI;
using Xarial.XCad.SolidWorks;

namespace SwAgent.Tools
{
    /// <summary>
    /// A tool's inputs as a form in the chat panel (setting "toolInputs": "panel", the default) instead of its PropertyManager
    /// page. The form logic is the page's own ToolForm (defaults, remembered values, data choices that cascade, fills, units,
    /// Optional, ShowWhen, validation); this keeps what the page keeps in its controls - the entities picked for selection
    /// inputs, the files, the intelligence tier - and turns everything into the state the panel draws. No SolidWorks types:
    /// tested headless. ToolFormBridge does the SolidWorks side.
    /// </summary>
    internal sealed class ToolFormSession
    {
        public readonly string FormId;
        public readonly ToolSpec Spec;
        public readonly ToolForm Form;
        public readonly bool Imperial;

        /// <summary>The agent's intelligence for this run (agent tools only; the page's "Intelligence" dropdown).</summary>
        public string Tier;

        /// <summary>preview.cs: compiled and ready, following every change (else on demand), and what to tell the user.</summary>
        public bool PreviewAvailable;
        public bool PreviewLive = true;
        public string PreviewMessage;

        /// <summary>A note at the top of the form (a pick that found nothing, an entity that no longer exists).</summary>
        public string Message;

        /// <summary>The form's profiles (load, save, update, markers); null keeps the form without a Profile row.</summary>
        public ProfileSession Profiles;

        private readonly Dictionary<string, List<ToolInputs.SelectedItem>> _selections = new Dictionary<string, List<ToolInputs.SelectedItem>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> _files = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        public ToolFormSession(ToolSpec spec, ToolForm form, bool imperial, string formId = null)
        {
            Spec = spec;
            Form = form;
            Imperial = imperial;
            FormId = formId ?? Guid.NewGuid().ToString("N").Substring(0, 12);
            Tier = spec?.Intelligence ?? "balanced";
        }

        /// <summary>
        /// The form for a tool: its data, the document's unit system and the values remembered on this PC. Bad data never stops
        /// the form: a damaged remembered file is the likely cause (it is only rewritten on OK), so try again without it, keeping
        /// the data choices, then fall back to the plain defaults. The page and the panel both start here.
        /// </summary>
        internal static ToolForm CreateForm(ToolSpec spec, bool imperial, string rememberDir = null)
        {
            try
            {
                return new ToolForm(spec, ToolData.For(spec), imperial, RememberStore.Load(spec.Name, rememberDir));
            }
            catch (Exception ex)
            {
                Log.Error($"Tool '{spec.Name}': reading its page data failed", ex);
            }
            try
            {
                return new ToolForm(spec, ToolData.For(spec), imperial);
            }
            catch (Exception ex)
            {
                Log.Error($"Tool '{spec.Name}': reading its data files failed", ex);
                return new ToolForm(spec, ToolData.None, imperial);
            }
        }

        // ---- selections and files (what the page keeps in its controls) ----

        public IReadOnlyList<ToolInputs.SelectedItem> Selected(string id) =>
            id != null && _selections.TryGetValue(id, out var list) ? list : (IReadOnlyList<ToolInputs.SelectedItem>)new ToolInputs.SelectedItem[0];

        public IReadOnlyList<string> Files(string id) => id != null && _files.TryGetValue(id, out var list) ? list : (IReadOnlyList<string>)new string[0];

        /// <summary>A selection input takes these entities (one only when it isn't Multiple).</summary>
        public void SetSelection(string id, IEnumerable<ToolInputs.SelectedItem> items)
        {
            var f = Field(id, "selection");
            var list = (items ?? Enumerable.Empty<ToolInputs.SelectedItem>()).Where(x => x != null).ToList();
            if (f.Spec.Multiple == false) list = list.Take(1).ToList();
            _selections[f.Id] = list;
        }

        /// <summary>
        /// A selection box that follows the SolidWorks selection, as a PropertyManager box does: what was just picked is
        /// added after what it holds, each entity once (by persistent reference, else the same object); a box for one
        /// entity takes the newest instead.
        /// </summary>
        internal static List<ToolInputs.SelectedItem> Merge(IReadOnlyList<ToolInputs.SelectedItem> held, IReadOnlyList<ToolInputs.SelectedItem> picked, bool multiple)
        {
            if (picked.Count == 0) return held.ToList();
            if (!multiple) return new List<ToolInputs.SelectedItem> { picked[picked.Count - 1] };
            var result = held.ToList();
            foreach (var item in picked)
            {
                if (!result.Any(h => SameEntity(h, item))) result.Add(item);
            }
            return result;
        }

        private static bool SameEntity(ToolInputs.SelectedItem a, ToolInputs.SelectedItem b) =>
            !string.IsNullOrEmpty(a.Ref) && !string.IsNullOrEmpty(b.Ref) ? a.Ref == b.Ref : a.Entity != null && ReferenceEquals(a.Entity, b.Entity);

        /// <summary>Files added to a files input (a single-file input keeps the last one; the same path is listed once).</summary>
        public void AddFiles(string id, IEnumerable<string> paths)
        {
            var f = Field(id, "files");
            var list = _files.TryGetValue(f.Id, out var have) ? have : (_files[f.Id] = new List<string>());
            foreach (var path in paths ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) continue;
                if (f.Spec.Multiple == false) list.Clear();
                if (!list.Contains(path, StringComparer.OrdinalIgnoreCase)) list.Add(path);
            }
        }

        private FormField Field(string id, string type = null)
        {
            var f = Form.Get(id) ?? throw new InvalidOperationException($"The tool has no input '{id}'.");
            if (type != null && f.Type != type) throw new InvalidOperationException($"'{id}' is not a {type} input.");
            return f;
        }

        // ---- changes from the panel ----

        /// <summary>
        /// One change from the panel: { id, value } (numbers in the unit the form shows them in, sizes with "index" or as an
        /// array, a checkbox's bool, a choice's entry, text, files' paths), { id, unit }, { id, on } (an Optional's switch),
        /// { id, remove: index } (a picked entity or file), { id, clear: true }, or { tier }. Returns true when the agent's tier changed.
        /// </summary>
        public bool Change(JObject p)
        {
            if (p == null) return false;
            var tier = (string)p["tier"];
            if (tier != null)
            {
                if (!ToolSpec.Tiers.Contains(tier) || tier == Tier) return false;
                Tier = tier;
                return true;
            }
            var f = Field((string)p["id"]);
            Message = null;
            if (p["unit"] is JValue unit && unit.Type == JTokenType.String) Form.SetUnit(f, (string)unit);
            if (p["on"] is JValue on && on.Type == JTokenType.Boolean) Form.SetOn(f, (bool)on);
            if ((bool?)p["clear"] == true)
            {
                _selections.Remove(f.Id);
                _files.Remove(f.Id);
            }
            if (p["remove"] is JValue remove && remove.Type == JTokenType.Integer)
            {
                var i = (int)remove;
                if (_selections.TryGetValue(f.Id, out var items) && i >= 0 && i < items.Count) items.RemoveAt(i);
                if (_files.TryGetValue(f.Id, out var files) && i >= 0 && i < files.Count) files.RemoveAt(i);
            }
            if (p.TryGetValue("value", out var value)) SetValue(f, value, (int?)p["index"]);
            return false;
        }

        private void SetValue(FormField f, JToken value, int? index)
        {
            switch (f.Type)
            {
                case "number":
                case "integer":
                {
                    var v = ToolData.Number(value);
                    if (v == null || double.IsNaN(v.Value) || double.IsInfinity(v.Value)) return;
                    var shown = Shown(f);
                    var si = shown == null ? v.Value : shown.ToSiValue(v.Value);
                    if (f.Type == "integer") si = Math.Round(si);
                    // Like the page's boxes: native numbers stay in their range; converted ones are checked before OK instead.
                    if (!f.Converted) si = Clamp(si, RangeSi(f));
                    Form.SetNumber(f, si);
                    break;
                }
                case "size":
                {
                    var unit = Units.For("length", Imperial);
                    var range = RangeSi(f);
                    if (value is JArray all)
                    {
                        for (var k = 0; k < Math.Min(all.Count, f.SizeSi.Length); k++)
                        {
                            var n = ToolData.Number(all[k]);
                            if (n != null) Form.SetSize(f, k, Clamp(unit.ToSiValue(n.Value), range));
                        }
                    }
                    else if (index != null && ToolData.Number(value) is double one)
                    {
                        Form.SetSize(f, index.Value, Clamp(unit.ToSiValue(one), range));
                    }
                    break;
                }
                case "checkbox":
                    if (value?.Type == JTokenType.Boolean) Form.SetChecked(f, (bool)value);
                    break;
                case "choice":
                case "options":
                    Form.SetChoice(f, ToolData.Text(value));
                    break;
                case "text":
                    Form.SetText(f, ToolData.Text(value) ?? "");
                    break;
                case "files":
                    _files.Remove(f.Id);
                    if (value is JArray paths) AddFiles(f.Id, paths.Select(x => (string)x));
                    break;
                case "selection":
                    // Entities come from SolidWorks (pick); from the panel only a clear.
                    if (value == null || value.Type == JTokenType.Null || value is JArray a && a.Count == 0) _selections.Remove(f.Id);
                    break;
            }
        }

        // ---- units and ranges as the panel shows them ----

        /// <summary>The unit a number is shown in: its own choice (converted), the document's length or angle, else the spec unit; null for integers.</summary>
        public UnitDef Shown(FormField f)
        {
            if (f.Type == "integer") return null;
            if (f.Type == "size") return Units.For("length", Imperial);
            if (f.Converted) return f.Unit;
            return f.Quantity == "length" || f.Quantity == "angle" ? Units.For(f.Quantity, Imperial) : Units.Spec(f.Quantity);
        }

        /// <summary>The range in SI the page's box would have (its defaults when inputs.json gives none).</summary>
        internal static (double? Min, double? Max) RangeSi(FormField f)
        {
            var s = f.Spec;
            if (f.Type == "integer") return (s.Min ?? 0, s.Max ?? 1000);
            if (f.Type == "size") return ((s.Min ?? 0) / 1000.0, (s.Max ?? 10000) / 1000.0);
            if (f.Converted)
            {
                var floor = f.Quantity == "temperature" ? -273.15 : 0;
                return (Units.SpecToSi(s.Unit, s.Min ?? floor), s.Max == null ? (double?)null : Units.SpecToSi(s.Unit, s.Max.Value));
            }
            switch (f.Quantity)
            {
                case "length": return ((s.Min ?? 0) / 1000.0, (s.Max ?? 10000) / 1000.0);
                case "angle": return ((s.Min ?? 0) * Math.PI / 180.0, (s.Max ?? 360) * Math.PI / 180.0);
            }
            return (s.Min, s.Max);
        }

        private static double Clamp(double si, (double? Min, double? Max) r) => Math.Max(r.Min ?? double.MinValue, Math.Min(r.Max ?? double.MaxValue, si));

        private static JToken Display(double si, UnitDef unit) => Units.Nice(unit == null ? si : unit.FromSi(si), 10);

        // ---- OK ----

        /// <summary>
        /// Why OK is unavailable (null when it isn't): a required selection, file or text that is missing, then the form's own
        /// rules (a required choice, an Optional to switch on, a value out of range) - the page's RequiredSatisfied.
        /// </summary>
        public (FormField Field, string Message) Blocking()
        {
            foreach (var f in Form.Fields.Where(x => ToolSpec.IsRequired(x.Spec) && !x.Hidden))
            {
                switch (f.Type)
                {
                    case "selection" when Selected(f.Id).Count == 0:
                        return (f, "Select " + What(f) + " in SolidWorks, then press Use selection.");
                    case "files" when Files(f.Id).Count == 0:
                        return (f, "Add a file.");
                    case "text" when string.IsNullOrWhiteSpace(f.Value):
                        return (f, "Type something here.");
                }
            }
            return Form.Blocking();
        }

        private static string What(FormField f)
        {
            var kinds = (f.Spec.Filter ?? new string[0]).Select(x => (x ?? "").Trim().ToLowerInvariant().Replace('_', ' ')).Where(x => x.Length > 0).ToList();
            if (kinds.Count == 0) return "it";
            // "faces", "vertices", "an edge", "a face or an edge"
            var plural = f.Spec.Multiple != false;
            var names = kinds.Distinct().Select(k => plural ? (k.EndsWith("ex") ? k.Substring(0, k.Length - 2) + "ices" : k + "s") : ("aeiou".IndexOf(k[0]) >= 0 ? "an " : "a ") + k).ToList();
            return names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " or " + names.Last();
        }

        /// <summary>
        /// What the macro and the agent get: the picked entities (as they are now: SolidWorks resolved them just before) and files,
        /// then everything else exactly as the page collects it. Inputs hidden by ShowWhen are left out.
        /// </summary>
        public ToolInputs Collect()
        {
            var inputs = new ToolInputs();
            foreach (var f in Form.Fields.Where(x => x.Type == "selection" || x.Type == "files"))
            {
                if (f.ConditionHidden)
                {
                    inputs.SetMeta(f.Id, new ToolInputs.InputMeta { Hidden = true });
                    continue;
                }
                if (f.Type == "selection") inputs.SetSelection(f.Id, Selected(f.Id).ToList());
                else inputs.SetFiles(f.Id, Files(f.Id).ToList());
            }
            Form.Collect(inputs);
            return inputs;
        }

        /// <summary>
        /// OK: the inputs for the run, with every "Remember" value (spec units) and unit choice stored as next run's default on
        /// this PC, as the page does. Null, storing nothing, while something holds OK back (see Blocking).
        /// </summary>
        public ToolInputs Finish(string rememberDir = null)
        {
            if (Blocking().Field != null) return null;
            var inputs = Collect();
            RememberStore.Save(Spec.Name, Form.RememberJson(), rememberDir);
            Profiles?.Finished(Form, inputs);
            return inputs;
        }

        // ---- profiles ----

        /// <summary>The files and picked entities as a profile reads and restores them.</summary>
        internal DocumentInputs DocumentInputs() => new DocumentInputs
        {
            Files = Files,
            SetFiles = (id, paths) =>
            {
                _files.Remove(id);
                AddFiles(id, paths);
            },
            Selected = Selected,
            SetSelection = SetSelection,
        };

        /// <summary>A profile action from the panel's Profile row (ProfileSession.Act); a refusal becomes the profile state's error.</summary>
        public void Profile(JObject p)
        {
            var profiles = Profiles ?? throw new InvalidOperationException("This form has no profiles.");
            Message = null;
            try
            {
                profiles.Act(Form, p);
            }
            catch (ToolException ex)
            {
                profiles.Error = ex.Message;
            }
        }

        // ---- the state the panel draws ----

        /// <summary>
        /// { formId, tool, title, description, message, imperial, groups: [{ id, title, expanded, inputs: [...] }], okEnabled,
        /// okReason, okField, agent: { tier, tiers } | null, preview: { available, live, message } | null, profile:
        /// ProfileSession.State | null }. Inputs carry mark "set" / "edited" while a profile is loaded.
        /// </summary>
        public JObject State()
        {
            var (blockField, blockMessage) = Blocking();
            var modified = Profiles?.Modified(Form) ?? new List<string>();
            var groups = new JArray();
            var byTitle = new Dictionary<string, JArray>(StringComparer.Ordinal);
            foreach (var f in Form.Fields)
            {
                // The page's grouping: inputs with the same Group share a box; the first input of a group says whether it starts open.
                var s = f.Spec;
                var title = string.IsNullOrWhiteSpace(s.Group) ? (f.Type == "selection" ? "Selections" : f.Type == "text" || f.Type == "files" ? "Context" : "Parameters") : s.Group;
                if (!byTitle.TryGetValue(title, out var inputs))
                {
                    inputs = byTitle[title] = new JArray();
                    groups.Add(new JObject { ["id"] = title, ["title"] = title, ["expanded"] = s.GroupExpanded != false, ["inputs"] = inputs });
                }
                var input = Input(f, f == blockField ? blockMessage : null);
                // A small marker: the profile set this input, or the user changed it after loading the profile.
                if (Profiles?.Mark(f, modified) is string mark) input["mark"] = mark;
                inputs.Add(input);
            }
            var state = new JObject
            {
                ["formId"] = FormId,
                ["tool"] = Spec.Name,
                ["title"] = Spec.Title,
                ["description"] = string.IsNullOrWhiteSpace(Spec.Description) ? null : Spec.Description,
                ["message"] = Message,
                ["imperial"] = Imperial,
                ["groups"] = groups,
                ["okEnabled"] = blockField == null,
                ["okReason"] = blockField == null ? null : $"{blockField.Spec.Label ?? blockField.Id}: {blockMessage}",
                ["okField"] = blockField?.Id,
                ["agent"] = Spec.Agent
                    ? new JObject { ["tier"] = Tier, ["tiers"] = new JArray(ToolSpec.Tiers) }
                    : null,
                ["preview"] = Spec.Preview == null
                    ? null
                    : new JObject { ["available"] = PreviewAvailable, ["live"] = PreviewLive, ["message"] = PreviewMessage },
                ["profile"] = Profiles?.State(Form, modified),
            };
            return state;
        }

        /// <summary>One input: { id, type, label, tip, visible, enabled, required, error, ... } plus what its type needs.</summary>
        public JObject Input(FormField f, string blocking = null)
        {
            var s = f.Spec;
            var o = new JObject
            {
                ["id"] = f.Id,
                ["type"] = f.Type,
                ["label"] = s.Label ?? (f.Type == "info" ? "" : f.Id),
                ["tip"] = f.Type == "info" ? s.Tip : Form.TipOf(f),
                ["visible"] = !f.Hidden,
                ["enabled"] = !(f.Optional && !f.On),
                ["required"] = ToolSpec.IsRequired(s),
            };
            string error = blocking;
            switch (f.Type)
            {
                case "number":
                case "integer":
                case "size":
                {
                    var unit = Shown(f);
                    var (min, max) = RangeSi(f);
                    if (f.Type == "size")
                    {
                        o["value"] = new JArray(f.SizeSi.Select(v => Display(v, unit)));
                        o["dims"] = f.Dims;
                    }
                    else
                    {
                        o["value"] = Display(f.Si, unit);
                    }
                    o["unit"] = unit == null ? "" : unit.Symbol;
                    if (f.Converted && f.UnitChoices.Count > 1) o["unitChoices"] = new JArray(f.UnitChoices.Select(u => u.Symbol));
                    if (min != null) o["min"] = Display(min.Value, unit);
                    if (max != null) o["max"] = Display(max.Value, unit);
                    o["step"] = unit?.Inc ?? 1;
                    if (f.Optional)
                    {
                        o["optional"] = true;
                        o["on"] = f.On;
                    }
                    if (f.TableSi != null)
                    {
                        o["table"] = Display(f.TableSi.Value, unit);
                        o["edited"] = f.Edited;
                    }
                    if (f.NotApplicable) o["notApplicable"] = true;
                    if (error == null && f.Converted && !f.Hidden && !(f.Optional && !f.On))
                    {
                        // A value out of range shows at its box (Blocking names only the first one).
                        var outside = min != null && f.Si < min.Value && !ToolForm.Same(f.Si, min.Value, ToolForm.LimitTolerance)
                                      || max != null && f.Si > max.Value && !ToolForm.Same(f.Si, max.Value, ToolForm.LimitTolerance);
                        if (outside) error = $"Enter a value from {(min == null ? "-" : Units.Show(min.Value, unit, 4))} to {(max == null ? "-" : Units.Show(max.Value, unit, 4))}.";
                    }
                    break;
                }
                case "checkbox":
                    o["value"] = f.Checked;
                    if (f.TableText != null) o["edited"] = !string.Equals(f.TableText, f.Checked ? "true" : "false", StringComparison.OrdinalIgnoreCase);
                    break;
                case "choice":
                case "options":
                    o["value"] = f.Value;
                    o["choices"] = new JArray(f.Items);
                    if (f.TableText != null)
                    {
                        o["table"] = f.TableText;
                        o["edited"] = ToolForm.ChoiceEdited(f);
                    }
                    break;
                case "text":
                    o["value"] = f.Value ?? "";
                    break;
                case "info":
                    o["text"] = f.Caption ?? "";
                    break;
                case "files":
                    o["files"] = new JArray(Files(f.Id).Select(p => new JObject { ["name"] = Path.GetFileName(p), ["path"] = p }));
                    o["accept"] = new JArray(s.Accept ?? new string[0]);
                    o["multiple"] = s.Multiple != false;
                    break;
                case "selection":
                    o["items"] = new JArray(Selected(f.Id).Select(i => new JObject { ["type"] = i.Type, ["label"] = i.Label, ["detail"] = i.Detail }));
                    o["filter"] = new JArray(s.Filter ?? new string[0]);
                    o["multiple"] = s.Multiple != false;
                    break;
            }
            o["error"] = error;
            return o;
        }
    }

    /// <summary>
    /// The SolidWorks side of the panel's tool forms. ToolRunner opens a form here instead of the PropertyManager page; the
    /// panel draws it (event toolForm.show { formId, ...state }) and drives it with RPCs, all on the SolidWorks UI thread:
    ///   toolForm.change { formId, id, value | unit | on | remove | clear } or { formId, tier } or { formId, preview: true } -> state
    ///   toolForm.pick { formId, id } -> state: a selection input takes what is selected in SolidWorks now (the panel calls it on
    ///       "Use selection", and on selection.changed while the input is focused); a files input opens the file dialog
    ///   toolForm.ok { formId } -> { closed: true } | { closed: false, form: state }: the same run path as the page's OK
    ///   toolForm.cancel { formId } -> { closed: true }
    ///   toolForm.list -> [state]: the open form again after the panel reloads (ToolRunner is still waiting on it)
    ///   toolForm.profile { formId, action, ... } -> state: the Profile row (ProfileSession.Act, plus "export" { name },
    ///       "loadFile" and "openFolder", which open Windows dialogs here); a refused action is the state's profile.error
    /// and toolForm.closed { formId, reason } when the add-in closes a form itself (another tool started, its document closed).
    /// One form at a time: a new one cancels the one before. Previews (preview.cs) run inside change and pick, never from a
    /// timer, and only while the user isn't in a SolidWorks command; their bodies go on OK, Cancel and document close.
    /// </summary>
    internal sealed class ToolFormBridge
    {
        private const int PreviewBudgetMs = 350;

        private readonly ISwApplication _app;
        private readonly ScriptHost _scripts;
        private WebBridge _bridge;
        private Open _open;

        private sealed class Open
        {
            public ToolFormSession Session;
            public IModelDoc2 Model;
            // Asynchronous continuations: ToolRunner never carries on inside the caller (e.g. a document's destroy event).
            public readonly TaskCompletionSource<ToolInputs> Done = new TaskCompletionSource<ToolInputs>(TaskCreationOptions.RunContinuationsAsynchronously);
            public Action<string> TierChanged;
            public ScriptRunner<object> Preview;
            public bool PreviewRunning;
            public readonly List<IBody2> Shown = new List<IBody2>();
        }

        public ToolFormBridge(ISwApplication app, ScriptHost scripts)
        {
            _app = app;
            _scripts = scripts;
        }

        /// <summary>The panel is loaded, so a form can show there (else the tool falls back to its page).</summary>
        public bool CanShow => _bridge?.IsReady == true;

        /// <summary>Registers the RPCs. The bridge is attached again when the panel reloads: this only re-registers, the open form stays.</summary>
        public void Attach(WebBridge bridge)
        {
            _bridge = bridge;
            bridge.Register("toolForm.list", p => _open == null ? new JArray() : new JArray(_open.Session.State()));
            bridge.RegisterAsync("toolForm.change", async p =>
            {
                var open = Current(p);
                var preview = (bool?)p["preview"] == true;
                if (!preview && open.Session.Change(p as JObject)) open.TierChanged?.Invoke(open.Session.Tier);
                await PreviewAsync(open, force: preview);
                return open == _open ? (object)open.Session.State() : null;
            });
            bridge.RegisterAsync("toolForm.pick", async p =>
            {
                var open = Current(p);
                Pick(open, (string)p["id"], follow: (bool?)p["follow"] == true);
                await PreviewAsync(open, force: false);
                return open == _open ? (object)open.Session.State() : null;
            });
            bridge.Register("toolForm.ok", p => Ok(Current(p)));
            bridge.Register("toolForm.profile", p => ProfileAction(Current(p), p as JObject));
            ProfilesBridge.Register(bridge, Owner);
            bridge.Register("toolForm.cancel", p =>
            {
                var formId = (string)p?["formId"];
                if (_open != null && _open.Session.FormId == formId) Close(_open, null);
                return new JObject { ["closed"] = true };
            });
        }

        private Open Current(JToken p)
        {
            var formId = (string)p?["formId"];
            if (_open == null || _open.Session.FormId != formId) throw new InvalidOperationException("This tool form is closed. Run the tool again.");
            return _open;
        }

        /// <summary>Show a tool's form in the panel; completes with the inputs on OK, or null on Cancel (as ToolPage.ShowAsync does).</summary>
        internal Task<ToolInputs> ShowAsync(ToolSpec spec, IModelDoc2 model, List<ToolPage.SelectedEntity> preselected, Action<string> tierChanged, string profile = null)
        {
            CancelPending("Another tool was started.");
            var imperial = Units.IsImperial(model);
            // The profile asked for, else the default profile, else today's defaults and remembered values.
            var form = ProfileSession.OpenForm(spec, imperial, profile, out var profiles, documentInputs: true);
            var open = new Open
            {
                Session = new ToolFormSession(spec, form, imperial) { Profiles = profiles },
                Model = model,
                TierChanged = tierChanged,
            };
            profiles.Attach(open.Session.DocumentInputs());
            Preselect(open, preselected ?? new List<ToolPage.SelectedEntity>());
            _open = open;

            if (spec.Preview != null && spec.Paused.Contains("preview"))
            {
                open.Session.PreviewMessage = "The live preview is paused: SolidWorks closed while drawing it last time. Ask SolidWorks Agent to fix this tool.";
            }
            else if (spec.Preview != null)
            {
                // Compiled off the UI thread while the user looks at the form. The panel asks for the first preview (a change
                // with preview: true) when it sees it is available: previews only ever run inside an RPC.
                _scripts.CompilePreviewAsync(spec.Preview).ContinueWith(t => Safe.Run("Tool form preview compiled", () =>
                {
                    if (_open != open) return;
                    if (t.IsFaulted || t.Result.Errors != null)
                    {
                        open.Session.PreviewMessage = "Preview unavailable: " + (t.IsFaulted ? t.Exception?.InnerException?.Message : t.Result.Errors.Split('\n')[0]);
                    }
                    else
                    {
                        open.Preview = t.Result.Runner;
                        open.Session.PreviewAvailable = true;
                    }
                    _bridge?.Emit("toolForm.show", open.Session.State());
                }), TaskScheduler.FromCurrentSynchronizationContext());
            }
            _bridge?.Emit("toolForm.show", open.Session.State());
            return open.Done.Task;
        }

        /// <summary>Close the open form as cancelled (another tool started, the document closed, the add-in stops).</summary>
        public void CancelPending(string reason)
        {
            var open = _open;
            if (open == null) return;
            Close(open, null);
            _bridge?.Emit("toolForm.closed", new JObject { ["formId"] = open.Session.FormId, ["reason"] = reason });
        }

        /// <summary>
        /// The form's document closed: its preview bodies are gone with it, and the form is cancelled. This runs inside the
        /// document's destroy event, so nothing here calls SolidWorks: the bodies are only forgotten, not hidden.
        /// </summary>
        public void DocumentClosed(IModelDoc2 model)
        {
            var open = _open;
            if (open == null || model == null || !ReferenceEquals(open.Model, model)) return;
            open.Shown.Clear();
            open.Preview = null;
            CancelPending("The document was closed.");
        }

        private void Close(Open open, ToolInputs result)
        {
            if (_open == open) _open = null;
            HidePreview(open);
            open.Done.TrySetResult(result);
        }

        private JObject Ok(Open open)
        {
            var session = open.Session;
            // The picked entities as they are now (the model may have changed since): one that no longer exists is dropped.
            var lost = new List<string>();
            foreach (var f in session.Form.Fields.Where(x => x.Type == "selection"))
            {
                var kept = new List<ToolInputs.SelectedItem>();
                foreach (var item in session.Selected(f.Id))
                {
                    var entity = Resolve(open.Model, item);
                    if (entity == null)
                    {
                        lost.Add($"{item.Type}{(string.IsNullOrEmpty(item.Label) ? "" : " of " + item.Label)}");
                        continue;
                    }
                    item.Entity = entity;
                    kept.Add(item);
                }
                if (kept.Count != session.Selected(f.Id).Count) session.SetSelection(f.Id, kept);
            }
            // The user sees what was dropped before the tool runs without it (OK again runs it).
            if (lost.Count > 0) session.Message = $"No longer in the model, so left out: {string.Join(", ", lost)}. Select again if it is needed, or press OK.";
            if (lost.Count > 0 || session.Blocking().Field != null)
                return new JObject { ["closed"] = false, ["form"] = session.State() };

            ToolInputs inputs;
            try
            {
                inputs = session.Finish();
            }
            catch (Exception ex)
            {
                Log.Error("Reading the tool form failed", ex);
                session.Message = "Reading the form failed: " + ex.Message;
                inputs = null;
            }
            if (inputs == null) return new JObject { ["closed"] = false, ["form"] = session.State() };
            Close(open, inputs);
            return new JObject { ["closed"] = true };
        }

        // ---- profiles ----

        private JObject ProfileAction(Open open, JObject p)
        {
            var session = open.Session;
            var profiles = session.Profiles ?? throw new InvalidOperationException("This form has no profiles.");
            var tool = session.Spec.Name;
            try
            {
                switch ((string)p?["action"])
                {
                    case "export":
                        ProfilesBridge.Export(tool, (string)p["name"], Owner());
                        break;
                    case "openFolder":
                        ProfilesBridge.OpenFolder(tool);
                        break;
                    case "loadFile":
                    {
                        var text = ProfilesBridge.PickFile(Owner());
                        if (text != null) session.Profile(new JObject { ["action"] = "import", ["content"] = text });
                        break;
                    }
                    default:
                        session.Profile(p);
                        break;
                }
            }
            catch (ToolException ex)
            {
                profiles.Error = ex.Message; // the dialogs' refusals (Profile catches its own)
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Warn($"Tool '{tool}': profile action failed: {ex.Message}");
                profiles.Error = "That didn't work: " + ex.Message;
            }
            return session.State();
        }

        /// <summary>SolidWorks' main window, so dialogs open in front of it (zero when it can't be read).</summary>
        private IntPtr Owner()
        {
            try
            {
                return new IntPtr(((IFrame)_app.Sw.Frame()).GetHWndx64());
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        // ---- selection ----

        /// <summary>What was selected when the tool started goes into the matching selection inputs, as the page's Preselect does.</summary>
        private void Preselect(Open open, List<ToolPage.SelectedEntity> preselected)
        {
            var used = new HashSet<object>();
            foreach (var f in open.Session.Form.Fields.Where(x => x.Type == "selection"))
            {
                var allowed = new HashSet<int>(ToolPage.SelectFilters(f.Spec.Filter));
                var matches = preselected.Where(p => p.Entity != null && !used.Contains(p.Entity) && (allowed.Count == 0 || allowed.Contains(p.Type))).ToList();
                if (f.Spec.Multiple == false) matches = matches.Take(1).ToList();
                if (matches.Count == 0) continue;
                foreach (var m in matches) used.Add(m.Entity);
                open.Session.SetSelection(f.Id, matches.Select(m => Capture(open.Model, m)));
            }
        }

        /// <summary>
        /// "Use selection" (replaces the list with what is selected), a selection change while the input is focused (follow:
        /// adds to the list, like a PropertyManager selection box - clicking empty space or an entity the input doesn't take
        /// leaves the list as it is), or a files input's Add.
        /// </summary>
        private void Pick(Open open, string id, bool follow = false)
        {
            var f = open.Session.Form.Get(id) ?? throw new InvalidOperationException($"The tool has no input '{id}'.");
            if (f.Type == "files")
            {
                PickFiles(open, f);
                return;
            }
            if (f.Type != "selection") throw new InvalidOperationException($"'{id}' takes no selection.");
            // The same gates as tools.run: never after a native fault, never while the user is in a SolidWorks command.
            if (NativeFault.Tripped != null)
            {
                open.Session.Message = "SolidWorks needs a restart before this form can read the selection: " + NativeFault.RestartAdvice;
                return;
            }
            if (SwBusy.State(_app.Sw).Blocking)
            {
                open.Session.Message = "The selection is read once the command open in SolidWorks is finished.";
                return;
            }
            var allowed = new HashSet<int>(ToolPage.SelectFilters(f.Spec.Filter));
            var picked = NativeFault.Run("tool form selection", () =>
            {
                var current = ToolPage.CurrentSelection(open.Model);
                var matches = current.Where(p => p.Entity != null && (allowed.Count == 0 || allowed.Contains(p.Type))).ToList();
                return (Count: current.Count, Items: matches.Select(m => Capture(open.Model, m)).ToList());
            });
            open.Session.Message = picked.Count > 0 && picked.Items.Count == 0
                ? $"Nothing selected fits '{f.Spec.Label ?? f.Id}'" + (allowed.Count > 0 ? $" (it takes {string.Join(", ", f.Spec.Filter)})." : ".")
                : null;
            if (!follow)
            {
                open.Session.SetSelection(f.Id, picked.Items);
                return;
            }
            if (picked.Items.Count == 0) return;
            open.Session.SetSelection(f.Id, ToolFormSession.Merge(open.Session.Selected(f.Id), picked.Items, f.Spec.Multiple != false));
        }

        /// <summary>An entity with what the agent needs to refer to it later (and to find it again at OK): its persistent reference.</summary>
        private static ToolInputs.SelectedItem Capture(IModelDoc2 model, ToolPage.SelectedEntity m)
        {
            var item = new ToolInputs.SelectedItem
            {
                Entity = m.Entity,
                Type = SelectionTools.Pretty((swSelectType_e)m.Type),
                Label = SelectionTools.Owner(m.Entity, m.Component),
                Detail = SelectionTools.Describe(m.Entity, m.Component),
            };
            try
            {
                if (model.Extension.GetPersistReference3(m.Entity) is byte[] bytes) item.Ref = Convert.ToBase64String(bytes);
            }
            catch
            {
                // No persistent id: the entity itself is used.
            }
            return item;
        }

        /// <summary>The entity a picked item names now: by its persistent reference, else the object picked (null when it is gone).</summary>
        private static object Resolve(IModelDoc2 model, ToolInputs.SelectedItem item)
        {
            if (string.IsNullOrEmpty(item.Ref)) return item.Entity;
            try
            {
                var obj = model.Extension.GetObjectByPersistReference3(Convert.FromBase64String(item.Ref), out var err);
                return obj != null && err == (int)swPersistReferencedObjectStates_e.swPersistReferencedObject_Ok ? obj : null;
            }
            catch (Exception ex)
            {
                Log.Warn("Resolving a picked entity failed: " + ex.Message);
                return null;
            }
        }

        private void PickFiles(Open open, FormField f)
        {
            using (var dialog = new System.Windows.Forms.OpenFileDialog
            {
                Title = f.Spec.Label,
                Multiselect = f.Spec.Multiple != false,
                Filter = ToolPage.FileFilter(f.Spec.Accept),
            })
            {
                System.Windows.Forms.IWin32Window owner = null;
                try
                {
                    owner = new FrameWindow(new IntPtr(((IFrame)_app.Sw.Frame()).GetHWndx64()));
                }
                catch
                {
                    // No owner: the dialog still opens.
                }
                if (dialog.ShowDialog(owner) != System.Windows.Forms.DialogResult.OK) return;
                open.Session.AddFiles(f.Id, dialog.FileNames);
            }
        }

        private sealed class FrameWindow : System.Windows.Forms.IWin32Window
        {
            public FrameWindow(IntPtr handle) => Handle = handle;

            public IntPtr Handle { get; }
        }

        // ---- preview ----

        /// <summary>
        /// Draw the tool's preview for the current values (the page's RunPreviewAsync): live after every change while it stays
        /// fast, else when the panel's Preview button asks (force). Never while the user is in a SolidWorks command.
        /// </summary>
        private async Task PreviewAsync(Open open, bool force)
        {
            var session = open.Session;
            if (open.Preview == null || open.PreviewRunning || _open != open || !(session.PreviewLive || force)) return;
            if (open.Model.GetType() != (int)swDocumentTypes_e.swDocPART)
            {
                session.PreviewMessage = "Preview is available in part documents.";
                return;
            }
            if (NativeFault.Tripped != null)
            {
                session.PreviewMessage = "No preview: SolidWorks needs a restart (a fault earlier: " + NativeFault.Tripped + ").";
                return;
            }
            if (SwBusy.State(_app.Sw).Blocking)
            {
                session.PreviewMessage = "The preview waits until the command open in SolidWorks is finished.";
                return;
            }
            open.PreviewRunning = true;
            var watch = Stopwatch.StartNew();
            try
            {
                HidePreview(open);
                if (session.Blocking().Field != null) return;
                var inputs = session.Collect();
                foreach (var id in inputs.SelectionIds.ToList())
                {
                    // The entities as they are now (cheap); the stored items keep what was picked.
                    var items = inputs.SelectedItems(id).Select(i => new ToolInputs.SelectedItem { Entity = Resolve(open.Model, i), Type = i.Type, Label = i.Label, Detail = i.Detail, Ref = i.Ref })
                        .Where(i => i.Entity != null).ToList();
                    inputs.SetSelection(id, items);
                }
                // If SolidWorks dies in here, the next start knows it was this tool's preview and pauses it.
                using (CrashGuard.Enter("preview", session.Spec.Name, session.Spec.Title, session.Spec.Preview))
                {
                    var globals = new ScriptGlobals { swApp = _app.Sw, Model = open.Model, Inputs = inputs };
                    // An access violation in the part that runs now becomes an error (and blocks model tools), as in tools.run.
                    await NativeFault.Run("tool form preview", () => open.Preview(globals));
                    if (_open == open) open.Shown.AddRange(NativeFault.Run("tool form preview", () => PreviewDisplay.Show(open.Model, globals.PreviewBodies)));
                }
                session.PreviewMessage = null;
            }
            catch (Exception ex)
            {
                var inner = ex is AggregateException agg ? agg.InnerException ?? ex : ex;
                session.PreviewMessage = "Preview: " + (inner is MacroInputException ? inner.Message : inner.GetType().Name + ": " + inner.Message);
            }
            finally
            {
                open.PreviewRunning = false;
                if (_open != open) HidePreview(open); // closed while it ran
                if (watch.ElapsedMilliseconds > PreviewBudgetMs && session.PreviewLive)
                {
                    // Never let the preview make the form feel sluggish.
                    session.PreviewLive = false;
                    session.PreviewMessage = "This preview is slow on this model, so it updates when you press Preview.";
                }
            }
        }

        private static void HidePreview(Open open) => PreviewDisplay.Hide(open.Model, open.Shown);
    }
}
