using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Scripting;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swpublished;
using SwAgent.Infrastructure;
using SwAgent.Scripting;
using SwAgent.Tools;
using Timer = System.Windows.Forms.Timer;

namespace SwAgent.UI
{
    /// <summary>
    /// A tool's PropertyManager page, built at run time from its inputs.json: native selection boxes, number boxes with
    /// units, checkboxes, dropdowns, option buttons, a note box and file lists - so a user tool feels exactly like a
    /// SolidWorks command. While it's open the tool's preview code draws temporary ("holographic") bodies that follow
    /// every change; if that ever gets slow the preview switches to on-demand (the eye button) instead of lagging.
    /// </summary>
    [ComVisible(true)]
    public sealed partial class ToolPage : IPropertyManagerPage2Handler9
    {
        private const int PreviewBudgetMs = 350;

        /// <summary>The S2 fallback's limits: parent combinations tried, and dropdowns made for one choice.</summary>
        private const int MaxVariantCombinations = 2000, MaxVariants = 80;
        private static readonly string[] TierNames = { "Fast", "Balanced", "High", "Max" };

        /// <summary>Tool pages of this add-in open right now: their command doesn't hold back the agent's tool calls.</summary>
        private static int _open;
        private bool _counted;
        public static bool AnyOpen => System.Threading.Volatile.Read(ref _open) > 0;

        private readonly ISldWorks _sw;
        private readonly IModelDoc2 _model;
        private readonly ToolSpec _spec;
        private readonly List<SelectedEntity> _preselected;
        private readonly TaskCompletionSource<ToolInputs> _done = new TaskCompletionSource<ToolInputs>();
        private readonly SynchronizationContext _ui = SynchronizationContext.Current;

        private IPropertyManagerPage2 _page;

        /// <summary>
        /// Every group box and control of the page, held for as long as the page is open. If .NET's garbage collector
        /// releases one (labels and groups we don't otherwise touch), SolidWorks frees it while the page still uses
        /// it, and the next read of a control on the page crashes SolidWorks. A large allocation - such as compiling
        /// the preview two seconds after the page opens - is exactly when that collection happens.
        /// </summary>
        private readonly List<object> _keep = new List<object>();

        private IDisposable _guard;
        private readonly Dictionary<int, Field> _fields = new Dictionary<int, Field>();
        private readonly List<Field> _ordered = new List<Field>();
        private readonly Dictionary<string, Field> _byInputId = new Dictionary<string, Field>(StringComparer.OrdinalIgnoreCase);
        private int _nextId = 10;
        private IPropertyManagerPageCombobox _tierBox;
        private int _tierBoxId = -1;
        private ToolInputs _result;

        /// <summary>The page's state without SolidWorks (cascades, fills, visibility, units); the controls mirror it.</summary>
        private ToolForm _form;
        private bool _imperial;
        private PageQuirks _quirks = new PageQuirks();

        /// <summary>
        /// Re-entrancy guard: while the code sets values, entries or visibility, every handler returns at once. Whether
        /// SolidWorks raises change events for values set from code is spike S1; the form ignores repeats of its own values
        /// either way, so a late event is harmless too.
        /// </summary>
        private int _quiet;
        private string _bubbleFor;

        private ScriptRunner<object> _previewRunner;
        private readonly Timer _previewTimer = new Timer { Interval = 120 };
        private bool _livePreview = true;
        private bool _previewRunning;
        private readonly List<IBody2> _shownBodies = new List<IBody2>();

        /// <summary>The user changed the tool's intelligence level in the page.</summary>
        public event Action<string> TierChanged;

        private sealed class Field
        {
            public ToolInputSpec Spec;
            public string Type;
            public int Id;
            public int Mark;
            public object Control;
            public List<IPropertyManagerPageOption> Options;
            public IPropertyManagerPageListbox FileList;
            public List<string> Files = new List<string>();
            public int AddButtonId;
            public int ClearButtonId;

            /// <summary>The field's state in the form.</summary>
            public FormField Model;
            public List<int> OptionIds = new List<int>();
            public IPropertyManagerPageLabel Label;

            /// <summary>Optional inputs: the checkbox (captioned with the label) that switches the value on.</summary>
            public IPropertyManagerPageCheckbox Toggle;
            public int ToggleId = -1;

            /// <summary>size: the length boxes.</summary>
            public IPropertyManagerPageNumberbox[] Boxes;
            public int[] BoxIds;

            /// <summary>UnitChoices: the unit dropdown under the box, and the unit the box shows now.</summary>
            public IPropertyManagerPageCombobox UnitBox;
            public int UnitBoxId = -1;
            public UnitDef ShownUnit;

            /// <summary>The entries the (active) dropdown holds - the combobox has no item count.</summary>
            public List<string> Items = new List<string>();

            /// <summary>Fallback when a shown dropdown can't be refilled (spike S2): one dropdown per entry list, one visible.</summary>
            public List<Variant> Variants;
            public Variant Active;

            /// <summary>Every control of the field except variants, for visibility.</summary>
            public List<object> Parts = new List<object>();

            /// <summary>The number the code last wrote into the box, the form's value then, and when (see IsEcho).</summary>
            public double? Written;
            public double WrittenSi;
            public int WrittenAt;

            /// <summary>When the code last set this field's selection or check state (see IsLateEvent).</summary>
            public int? SetAt;

            /// <summary>
            /// The dropdown didn't take its new entries (spike S2): what it shows no longer matches the form, so it is disabled,
            /// its events are ignored and OK waits until the page is opened again.
            /// </summary>
            public bool Stale;

            /// <summary>The tooltip last given to the dropdown (data choices name their selected row).</summary>
            public string Tip;
        }

        private sealed class Variant
        {
            public int Id;
            public IPropertyManagerPageCombobox Box;
            public List<string> Items;
        }

        /// <summary>Something that was selected before the tool started.</summary>
        internal sealed class SelectedEntity
        {
            public object Entity;
            public int Type;
            public IComponent2 Component;
        }

        internal ToolPage(ISldWorks sw, IModelDoc2 model, ToolSpec spec, List<SelectedEntity> preselected, string profile = null)
        {
            _sw = sw;
            _model = model;
            _spec = spec;
            _preselected = preselected;
            _requestedProfile = profile;
            _previewTimer.Tick += (s, e) =>
            {
                _previewTimer.Stop();
                _ = RunPreviewAsync();
            };
        }

        /// <summary>Show the page; completes with the inputs on OK, or null on Cancel.</summary>
        internal Task<ToolInputs> ShowAsync(ScriptHost scripts)
        {
            var options = (int)(swPropertyManagerPageOptions_e.swPropertyManagerOptions_OkayButton |
                                swPropertyManagerPageOptions_e.swPropertyManagerOptions_CancelButton |
                                swPropertyManagerPageOptions_e.swPropertyManagerOptions_LockedPage |
                                swPropertyManagerPageOptions_e.swPropertyManagerOptions_PushpinButton |
                                swPropertyManagerPageOptions_e.swPropertyManagerOptions_CanEscapeCancel);
            if (_spec.Preview != null && !_spec.Paused.Contains("preview")) options |= (int)swPropertyManagerPageOptions_e.swPropertyManagerOptions_PreviewButton;

            // The page's state (defaults, remembered values, data choices) before any control exists.
            _form = CreateForm(_spec, _model, out _imperial, _requestedProfile, out _profiles);
            _quirks = PageQuirks.Load();

            var errors = 0;
            _page = (IPropertyManagerPage2)_sw.CreatePropertyManagerPage(_spec.Title, options, this, ref errors);
            if (_page == null) throw new InvalidOperationException($"SolidWorks couldn't create the tool's page (error {errors}).");
            if (!string.IsNullOrWhiteSpace(_spec.Description))
                _page.SetMessage3(_spec.Description, (int)swPropertyManagerPageMessageVisibility.swMessageBoxVisible,
                    (int)swPropertyManagerPageMessageExpanded.swMessageBoxExpand, _spec.Title);

            // From here until the page closes, a SolidWorks crash is attributed to this tool's page (see CrashGuard).
            _guard = CrashGuard.Enter("page", _spec.Name, _spec.Title, _spec.Preview ?? _spec.Macro);
            try
            {
                Quiet(Build);
                _page.Show2(0);
                if (!_counted) { _counted = true; System.Threading.Interlocked.Increment(ref _open); }
            }
            catch
            {
                _guard.Dispose();
                _guard = null;
                throw;
            }
            Preselect();
            UpdateOkButton();
            if (_pendingProfileMessage != null) Message(_pendingProfileMessage); // "Loaded your default profile: ..."

            if (_spec.Preview != null && _spec.Paused.Contains("preview"))
            {
                Message("The live preview is paused: SolidWorks closed while drawing it last time. Ask SolidWorks Agent to fix this tool.");
            }
            else if (_spec.Preview != null)
            {
                // Compile the preview off the UI thread while the user looks at the page.
                scripts.CompilePreviewAsync(_spec.Preview).ContinueWith(t =>
                {
                    if (t.Result.Errors != null) Message("Preview unavailable: " + t.Result.Errors.Split('\n')[0]);
                    else
                    {
                        _previewRunner = t.Result.Runner;
                        SchedulePreview();
                    }
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }
            return _done.Task;
        }

        // ---- building ----

        /// <summary>
        /// The form for a tool: its data, the document's unit system, and the profile asked for or the default profile - else
        /// the values remembered on this PC.
        /// </summary>
        private static ToolForm CreateForm(ToolSpec spec, IModelDoc2 model, out bool imperial, string profile, out ProfileSession profiles)
        {
            imperial = Units.IsImperial(model);
            // Bad data must never stop the page (the same fallbacks as the panel's form).
            return ProfileSession.OpenForm(spec, imperial, profile, out profiles);
        }

        private void Quiet(Action a)
        {
            _quiet++;
            try
            {
                a();
            }
            finally
            {
                _quiet--;
            }
        }

        private void Build()
        {
            BuildProfileGroup();
            var groups = new Dictionary<string, IPropertyManagerPageGroup>();
            var nextMark = 2;
            foreach (var model in _form.Fields)
            {
                var input = model.Spec;
                var type = model.Type;
                var caption = string.IsNullOrWhiteSpace(input.Group) ? (type == "selection" ? "Selections" : type == "text" || type == "files" ? "Context" : "Parameters") : input.Group;
                if (!groups.TryGetValue(caption, out var group))
                {
                    // Group ids come from the same counter as control ids (they used to count from 1 and met the controls at 10).
                    // The first input of a group decides whether it starts expanded.
                    var options = swAddGroupBoxOptions_e.swGroupBoxOptions_Visible;
                    if (input.GroupExpanded != false) options |= swAddGroupBoxOptions_e.swGroupBoxOptions_Expanded;
                    group = (IPropertyManagerPageGroup)Group(_nextId++, caption, (int)options);
                    groups[caption] = group;
                }
                var f = new Field { Spec = input, Type = type, Model = model };
                AddField(group, f, ref nextMark);
                _ordered.Add(f);
                if (!_byInputId.ContainsKey(input.Id)) _byInputId[input.Id] = f;
            }

            if (_spec.Agent)
            {
                var group = (IPropertyManagerPageGroup)Group(_nextId++, "SolidWorks Agent", (int)swAddGroupBoxOptions_e.swGroupBoxOptions_Visible);
                AddLabel(group, "Intelligence");
                var id = _nextId++;
                _tierBoxId = id;
                _tierBox = (IPropertyManagerPageCombobox)Control(group, id, (short)swPropertyManagerPageControlType_e.swControlType_Combobox, "Intelligence",
                    (short)swPropertyManagerPageControlLeftAlign_e.swControlAlign_Indent, ControlOptions,
                    "How much intelligence the agent uses for this tool: Fast (quickest), Balanced, High (careful), or Max (hardest problems).");
                _tierBox.AddItems(TierNames);
                _tierBox.CurrentSelection = (short)Math.Max(0, Array.IndexOf(ToolSpec.Tiers, _spec.Intelligence));
            }

            // Every control was made from the form's current state.
            foreach (var model in _form.Fields) model.Changes = FieldChange.None;
        }

        private IPropertyManagerPageGroup Group(int id, string caption, int options)
        {
            var group = (IPropertyManagerPageGroup)_page.AddGroupBox(id, caption, options);
            _keep.Add(group);
            return group;
        }

        private object Control(IPropertyManagerPageGroup group, int id, short type, string caption, short align, int options, string tip)
        {
            var control = group.AddControl2(id, type, caption, align, options, tip);
            _keep.Add(control);
            return control;
        }

        private static int ControlOptions => (int)(swAddControlOptions_e.swControlOptions_Visible | swAddControlOptions_e.swControlOptions_Enabled);

        /// <summary>
        /// Options for a control of a field: created hidden when the field starts hidden (every control must exist before
        /// Show2, so hidden ones are made now and shown later), disabled when an Optional is off. With the "disable" quirk
        /// (spike S3) hidden fields stay visible but disabled.
        /// </summary>
        private int OptionsFor(Field f, bool followsToggle = false)
        {
            var m = f.Model;
            if (m == null) return ControlOptions;
            var hidden = m.Hidden;
            var visible = !hidden || _quirks.HideByDisabling;
            var enabled = !(hidden && _quirks.HideByDisabling) && !(followsToggle && m.Optional && !m.On);
            return (visible ? (int)swAddControlOptions_e.swControlOptions_Visible : 0) | (enabled ? (int)swAddControlOptions_e.swControlOptions_Enabled : 0);
        }

        private IPropertyManagerPageLabel AddLabel(IPropertyManagerPageGroup group, string text, int options = -1)
        {
            return (IPropertyManagerPageLabel)Control(group, _nextId++, (short)swPropertyManagerPageControlType_e.swControlType_Label, text,
                (short)swPropertyManagerPageControlLeftAlign_e.swControlAlign_LeftEdge, options < 0 ? ControlOptions : options, "");
        }

        /// <summary>The label above a number or size - or, for an Optional one, the checkbox that switches it on.</summary>
        private void AddCaption(IPropertyManagerPageGroup group, Field f, string tip)
        {
            var m = f.Model;
            if (m.Optional)
            {
                f.ToggleId = _nextId++;
                f.Toggle = (IPropertyManagerPageCheckbox)Control(group, f.ToggleId, (short)swPropertyManagerPageControlType_e.swControlType_Checkbox,
                    m.Caption, (short)swPropertyManagerPageControlLeftAlign_e.swControlAlign_LeftEdge, OptionsFor(f), tip);
                f.Toggle.Checked = m.On;
                _fields[f.ToggleId] = f;
                f.Parts.Add(f.Toggle);
            }
            else
            {
                f.Label = AddLabel(group, m.Caption, OptionsFor(f));
                f.Parts.Add(f.Label);
            }
        }

        private void AddField(IPropertyManagerPageGroup group, Field f, ref int nextMark)
        {
            var s = f.Spec;
            var m = f.Model;
            var tip = s.Tip ?? s.Label ?? "";
            var indent = (short)swPropertyManagerPageControlLeftAlign_e.swControlAlign_Indent;
            switch (f.Type)
            {
                case "selection":
                {
                    f.Id = _nextId++;
                    var box = (IPropertyManagerPageSelectionbox)Control(group, f.Id, (short)swPropertyManagerPageControlType_e.swControlType_Selectionbox,
                        s.Label, indent, OptionsFor(f), tip);
                    var filters = SelectFilters(s.Filter);
                    if (filters.Length > 0) box.SetSelectionFilters(filters);
                    f.Mark = nextMark;
                    nextMark *= 2;
                    box.Mark = f.Mark;
                    box.SingleEntityOnly = s.Multiple == false;
                    box.Height = (short)(s.Multiple == false ? 15 : 50);
                    ((IPropertyManagerPageControl)box).SetStandardPictureLabel((int)PictureFor(s.Icon, s.Filter));
                    f.Control = box;
                    f.Parts.Add(box);
                    break;
                }
                case "number":
                case "integer":
                {
                    AddCaption(group, f, tip);
                    f.Id = _nextId++;
                    var box = (IPropertyManagerPageNumberbox)Control(group, f.Id, (short)swPropertyManagerPageControlType_e.swControlType_Numberbox,
                        s.Label, indent, OptionsFor(f, followsToggle: true), tip);
                    var unit = (s.Unit ?? "length").ToLowerInvariant();
                    if (f.Type == "integer")
                        box.SetRange2((int)swNumberboxUnitType_e.swNumberBox_UnitlessInteger, s.Min ?? 0, s.Max ?? 1000, true, 1, 5, 1);
                    else if (m.Converted)
                        SetConvertedRange(f, box, wide: _quirks.NoRangeAfterShow && m.UnitChoices.Count > 1);
                    else if (unit == "angle")
                        box.SetRange2((int)swNumberboxUnitType_e.swNumberBox_Angle, Rad(s.Min ?? 0), Rad(s.Max ?? 360), true, Rad(1), Rad(15), Rad(0.1));
                    else if (unit == "length")
                        box.SetRange2((int)swNumberboxUnitType_e.swNumberBox_Length, Mm(s.Min ?? 0), Mm(s.Max ?? 10000), true, Mm(1), Mm(10), Mm(0.1));
                    else
                        box.SetRange2((int)swNumberboxUnitType_e.swNumberBox_UnitlessDouble, s.Min ?? -1e9, s.Max ?? 1e9, true, 1, 10, 0.1);
                    box.Value = BoxValue(f);
                    Wrote(f);
                    f.ShownUnit = m.Unit;
                    if (s.Icon != null || unit == "length" || unit == "angle")
                        ((IPropertyManagerPageControl)box).SetStandardPictureLabel((int)PictureFor(s.Icon ?? (unit == "angle" ? "angle" : "distance"), null));
                    f.Control = box;
                    f.Parts.Add(box);
                    if (m.Converted && m.UnitChoices.Count > 1)
                    {
                        // The unit the user reads on their press, gauge or nameplate - remembered on this PC.
                        f.UnitBoxId = _nextId++;
                        f.UnitBox = (IPropertyManagerPageCombobox)Control(group, f.UnitBoxId, (short)swPropertyManagerPageControlType_e.swControlType_Combobox,
                            "Unit", indent, OptionsFor(f, followsToggle: true), "Unit of " + (s.Label ?? s.Id));
                        f.UnitBox.AddItems(m.UnitChoices.Select(u => u.Symbol).ToArray());
                        f.UnitBox.CurrentSelection = (short)Math.Max(0, m.UnitChoices.IndexOf(m.Unit));
                        _fields[f.UnitBoxId] = f;
                        f.Parts.Add(f.UnitBox);
                    }
                    break;
                }
                case "size":
                {
                    AddCaption(group, f, tip);
                    var names = new[] { "Width", "Depth", "Thickness" };
                    var pictures = new[] { swControlBitmapLabelType_e.swBitmapLabel_Width, swControlBitmapLabelType_e.swBitmapLabel_Depth, swControlBitmapLabelType_e.swBitmapLabel_Thickness1 };
                    var dims = m.Dims;
                    f.Boxes = new IPropertyManagerPageNumberbox[dims];
                    f.BoxIds = new int[dims];
                    // Stacked vertically; boxes side by side wait for spike S4 (units of Top/Left/Width).
                    for (var k = 0; k < dims; k++)
                    {
                        var id = _nextId++;
                        var box = (IPropertyManagerPageNumberbox)Control(group, id, (short)swPropertyManagerPageControlType_e.swControlType_Numberbox,
                            $"{s.Label} - {names[k]}", indent, OptionsFor(f, followsToggle: true), $"{names[k]}. {tip}");
                        box.SetRange2((int)swNumberboxUnitType_e.swNumberBox_Length, Mm(s.Min ?? 0), Mm(s.Max ?? 10000), true, Mm(1), Mm(10), Mm(0.1));
                        box.Value = m.SizeSi[k];
                        ((IPropertyManagerPageControl)box).SetStandardPictureLabel((int)pictures[k]);
                        f.Boxes[k] = box;
                        f.BoxIds[k] = id;
                        _fields[id] = f;
                        f.Parts.Add(box);
                    }
                    f.Id = f.BoxIds[0];
                    f.Control = f.Boxes[0];
                    break;
                }
                case "info":
                {
                    f.Id = _nextId++;
                    f.Label = (IPropertyManagerPageLabel)Control(group, f.Id, (short)swPropertyManagerPageControlType_e.swControlType_Label, m.Caption ?? "",
                        (short)swPropertyManagerPageControlLeftAlign_e.swControlAlign_LeftEdge, OptionsFor(f), s.Tip ?? "");
                    f.Control = f.Label;
                    f.Parts.Add(f.Label);
                    break;
                }
                case "checkbox":
                {
                    f.Id = _nextId++;
                    var box = (IPropertyManagerPageCheckbox)Control(group, f.Id, (short)swPropertyManagerPageControlType_e.swControlType_Checkbox,
                        s.Label, (short)swPropertyManagerPageControlLeftAlign_e.swControlAlign_LeftEdge, OptionsFor(f), tip);
                    box.Checked = m.Checked;
                    f.Control = box;
                    f.Parts.Add(box);
                    break;
                }
                case "choice":
                {
                    f.Label = AddLabel(group, s.Label, OptionsFor(f));
                    f.Parts.Add(f.Label);
                    if (m.IsData)
                    {
                        tip = _form.TipOf(m);
                        f.Tip = tip;
                    }
                    if (m.IsData && m.Parents.Count > 0 && _quirks.ComboVariants && AddVariants(group, f, tip)) break;
                    f.Id = _nextId++;
                    var box = (IPropertyManagerPageCombobox)Control(group, f.Id, (short)swPropertyManagerPageControlType_e.swControlType_Combobox,
                        s.Label, indent, OptionsFor(f), tip);
                    var choices = m.Items.ToArray();
                    if (choices.Length > 0) box.AddItems(choices);
                    box.CurrentSelection = (short)Math.Max(0, Array.IndexOf(choices, m.Value));
                    f.Items = m.Items.ToList();
                    f.Control = box;
                    f.Parts.Add(box);
                    break;
                }
                case "options":
                {
                    f.Label = AddLabel(group, s.Label, OptionsFor(f));
                    f.Parts.Add(f.Label);
                    f.Options = new List<IPropertyManagerPageOption>();
                    f.Id = _nextId;
                    var choices = s.Choices ?? new string[0];
                    for (var i = 0; i < choices.Length; i++)
                    {
                        var id = _nextId++;
                        var opt = (IPropertyManagerPageOption)Control(group, id, (short)swPropertyManagerPageControlType_e.swControlType_Option,
                            choices[i], indent, OptionsFor(f), tip);
                        opt.Checked = choices[i] == m.Value;
                        f.Options.Add(opt);
                        f.OptionIds.Add(id);
                        f.Parts.Add(opt);
                        _fields[id] = f;
                    }
                    return;
                }
                case "text":
                {
                    f.Label = AddLabel(group, s.Label, OptionsFor(f));
                    f.Parts.Add(f.Label);
                    f.Id = _nextId++;
                    var box = (IPropertyManagerPageTextbox)Control(group, f.Id, (short)swPropertyManagerPageControlType_e.swControlType_Textbox,
                        s.Label, (short)swPropertyManagerPageControlLeftAlign_e.swControlAlign_LeftEdge, OptionsFor(f),
                        (tip.Length > 0 ? tip + " " : "") + "(Win+H to dictate)");
                    box.Style = (int)swPropMgrPageTextBoxStyle_e.swPropMgrPageTextBoxStyle_Multiline;
                    box.Height = 50;
                    box.Text = m.Value ?? "";
                    f.Control = box;
                    f.Parts.Add(box);
                    break;
                }
                case "files":
                {
                    f.Label = AddLabel(group, s.Label, OptionsFor(f));
                    f.Parts.Add(f.Label);
                    f.Id = _nextId++;
                    f.FileList = (IPropertyManagerPageListbox)Control(group, f.Id, (short)swPropertyManagerPageControlType_e.swControlType_Listbox,
                        s.Label, (short)swPropertyManagerPageControlLeftAlign_e.swControlAlign_LeftEdge, OptionsFor(f), tip);
                    f.FileList.Height = 40;
                    f.AddButtonId = _nextId++;
                    f.Parts.Add(Control(group, f.AddButtonId, (short)swPropertyManagerPageControlType_e.swControlType_Button, "Add file...",
                        (short)swPropertyManagerPageControlLeftAlign_e.swControlAlign_LeftEdge, OptionsFor(f), "Add " + AcceptText(s.Accept)));
                    f.ClearButtonId = _nextId++;
                    f.Parts.Add(Control(group, f.ClearButtonId, (short)swPropertyManagerPageControlType_e.swControlType_Button, "Clear",
                        (short)swPropertyManagerPageControlLeftAlign_e.swControlAlign_LeftEdge, OptionsFor(f), "Remove all files"));
                    _fields[f.AddButtonId] = f;
                    _fields[f.ClearButtonId] = f;
                    f.Control = f.FileList;
                    f.Parts.Add(f.FileList);
                    break;
                }
                default:
                    AddLabel(group, $"{s.Label} (unsupported input type '{s.Type}')");
                    return;
            }
            _fields[f.Id] = f;
        }

        /// <summary>
        /// The S2 fallback: one dropdown for every entry list a dependent choice can have (all made before Show2), and only
        /// the one for the current parents visible. False when there are too many lists (or the choices depend on each other
        /// in a circle) - the field then has one dropdown, which is disabled if it fails to refill (see ComboRefillFailed).
        /// </summary>
        private bool AddVariants(IPropertyManagerPageGroup group, Field f, string tip)
        {
            var m = f.Model;
            // Many parent combinations usually share few lists (one per family): the dropdowns made count, not the combinations.
            var lists = _form.PossibleEntryLists(m, MaxVariantCombinations);
            if (lists == null || lists.Count < 2 || lists.Count > MaxVariants) return false;
            if (!lists.Any(l => l.SequenceEqual(m.Items))) lists.Add(m.Items.ToList());
            f.Variants = new List<Variant>();
            foreach (var items in lists)
            {
                var active = f.Active == null && items.SequenceEqual(m.Items);
                var id = _nextId++;
                var options = active ? OptionsFor(f) : (int)swAddControlOptions_e.swControlOptions_Enabled;
                var box = (IPropertyManagerPageCombobox)Control(group, id, (short)swPropertyManagerPageControlType_e.swControlType_Combobox,
                    f.Spec.Label, (short)swPropertyManagerPageControlLeftAlign_e.swControlAlign_Indent, options, tip);
                if (items.Count > 0) box.AddItems(items.ToArray());
                box.CurrentSelection = (short)Math.Max(0, active ? items.IndexOf(m.Value) : 0);
                var v = new Variant { Id = id, Box = box, Items = items };
                f.Variants.Add(v);
                _fields[id] = f;
                if (active) f.Active = v;
            }
            f.Id = f.Active.Id;
            f.Control = f.Active.Box;
            f.Items = f.Active.Items;
            _fields[f.Id] = f;
            return true;
        }

        /// <summary>A converted number's range in the unit it shows (wide: every unit the field offers fits, for pages that can't re-range).</summary>
        private void SetConvertedRange(Field f, IPropertyManagerPageNumberbox box, bool wide)
        {
            var m = f.Model;
            var (min, max) = DisplayRange(m, m.Unit);
            if (wide)
            {
                foreach (var u in m.UnitChoices)
                {
                    var (a, b) = DisplayRange(m, u);
                    min = Math.Min(min, a);
                    max = Math.Max(max, b);
                }
            }
            box.SetRange2((int)swNumberboxUnitType_e.swNumberBox_UnitlessDouble, min, max, true, m.Unit.Inc, m.Unit.Fast, m.Unit.Slow);
        }

        private static (double Min, double Max) DisplayRange(FormField m, UnitDef unit)
        {
            var s = m.Spec;
            var floor = m.Quantity == "temperature" ? -273.15 : 0;
            var min = unit.FromSi(Units.SpecToSi(s.Unit, s.Min ?? floor));
            var max = unit.FromSi(Units.SpecToSi(s.Unit, s.Max ?? 1e9));
            return (Math.Min(min, max), Math.Max(min, max));
        }

        private static void Wrote(Field f)
        {
            if (f.Model == null) return;
            f.Written = BoxValue(f);
            f.WrittenSi = f.Model.Si;
            f.WrittenAt = System.Environment.TickCount;
        }

        /// <summary>
        /// A number box reporting back what the code wrote (spike S1: an event raised after the guard, or a box that keeps
        /// fewer digits than were written) rather than an edit, while the form still holds the value written. Events just
        /// after the write (timed) allow 0.5 %; reading the boxes at OK allows only the box's display rounding, so a small
        /// deliberate edit (2000 -> 2009 psi) that raised no event is still read. Only for numbers that use the newer
        /// options (filled from a table or converted): there a phantom edit would read "edited by the user". Plain boxes
        /// read as they always did.
        /// </summary>
        private static bool IsEcho(Field f, double boxValue, bool timed)
        {
            var m = f.Model;
            if (m == null || m.IsPlain || f.Written == null || !ToolForm.Same(m.Si, f.WrittenSi)) return false;
            var w = f.Written.Value;
            var diff = Math.Abs(boxValue - w);
            if (timed) return unchecked(System.Environment.TickCount - f.WrittenAt) <= 500 && diff <= 0.005 * Math.Max(Math.Abs(w), 1e-12);
            return diff <= RoundingSlack(f, w);
        }

        /// <summary>
        /// What a box's display rounding can change in its value: half a unit in the last digit SolidWorks shows (2 decimals
        /// for unitless and converted boxes, 0.01 mm / 0.001 in for lengths, 0.01 deg for angles), half a step for integers.
        /// </summary>
        internal static double RoundingSlack(string type, string unit, bool converted, double written)
        {
            double slack;
            if (type == "integer") slack = 0.49;
            else if (converted) slack = 0.005;
            else
            {
                switch ((unit ?? "length").Trim().ToLowerInvariant())
                {
                    case "length": slack = 0.0005 * 0.0254; break; // the coarser of 0.005 mm and 0.0005 in, in m
                    case "angle": slack = 0.005 * Math.PI / 180.0; break;
                    default: slack = 0.005; break;
                }
            }
            return Math.Max(slack, 1e-9 * Math.Abs(written));
        }

        private static double RoundingSlack(Field f, double written) => RoundingSlack(f.Type, f.Spec.Unit, f.Model.Converted, written);

        /// <summary>
        /// A selection or check event raised for something the code just set (a refilled dropdown, a filled-in option): if
        /// SolidWorks delivers it after the guard (spike S1) it must not undo the form's choice. No user clicks that fast.
        /// </summary>
        private static bool IsLateEvent(Field f) => f.SetAt != null && unchecked(System.Environment.TickCount - f.SetAt.Value) < 300;

        /// <summary>What a number box holds: SI for native and plain boxes, the display unit for converted ones.</summary>
        private static double BoxValue(Field f) => f.Model.Converted ? f.Model.Unit.FromSi(f.Model.Si) : f.Model.Si;

        private static double SiFromBox(Field f, double value) => f.Model.Converted ? (f.ShownUnit ?? f.Model.Unit).ToSiValue(value) : value;

        // ---- keeping the controls in step with the form ----

        /// <summary>Push what the form changed (entries, values, ranges, captions, visibility) into the controls, quietly.</summary>
        private void Sync()
        {
            if (_page == null || _form == null) return;
            Quiet(() =>
            {
                for (var round = 0; round < 3; round++)
                {
                    var any = false;
                    foreach (var f in _ordered)
                    {
                        var m = f.Model;
                        if (m == null || m.Changes == FieldChange.None) continue;
                        var c = m.Changes;
                        m.Changes = FieldChange.None;
                        any = true;
                        try
                        {
                            if ((c & FieldChange.Items) != 0 && f.Type == "choice") Refill(f);
                            if ((c & FieldChange.Range) != 0) Rerange(f);
                            if ((c & FieldChange.Value) != 0) ShowValue(f);
                            if ((c & FieldChange.Caption) != 0) ShowCaption(f);
                            if ((c & (FieldChange.Value | FieldChange.Items)) != 0 && m.IsData) ShowTip(f);
                            if ((c & (FieldChange.Visible | FieldChange.Enabled | FieldChange.Items)) != 0) ShowVisibility(f);
                        }
                        catch (Exception ex)
                        {
                            Log.Warn($"Tool page '{_spec.Name}': updating '{f.Spec.Id}' failed: {ex.Message}");
                        }
                    }
                    if (!any) break;
                }
            });
        }

        /// <summary>New entries for a dependent dropdown: switch to the matching variant, or Clear + AddItems (spike S2).</summary>
        private void Refill(Field f)
        {
            var items = f.Model.Items;
            if (f.Stale) return; // known not to follow: disabled, the page has said so and OK waits
            if (f.Variants != null)
            {
                var v = f.Variants.FirstOrDefault(x => x.Items.SequenceEqual(items));
                if (v != null)
                {
                    f.Active = v;
                    f.Id = v.Id;
                    f.Control = v.Box;
                    f.Items = v.Items;
                    f.Tip = null; // this dropdown still has the tip it was made with
                    return;
                }
                // A list the variants didn't foresee: refill the active one.
            }
            var box = (IPropertyManagerPageCombobox)f.Control;
            try
            {
                f.SetAt = System.Environment.TickCount;
                box.Clear();
                if (items.Count > 0) box.AddItems(items.ToArray());
                f.Items = items.ToList();
            }
            catch (Exception ex)
            {
                ComboRefillFailed(f, ex.Message);
                return;
            }
            var wrong = RefillMismatch(box, items);
            if (wrong != null) ComboRefillFailed(f, wrong);
        }

        /// <summary>
        /// The combobox has no item count, so a refill is checked by its entries: the first and the last must be the new
        /// ones and nothing may follow the last (a Clear that did nothing leaves old entries next to the new ones, and a pick
        /// would then map to the wrong entry). Null when it took, or when ItemText can't tell on this version.
        /// </summary>
        private static string RefillMismatch(IPropertyManagerPageCombobox box, List<string> items)
        {
            string Text(int i) => box.get_ItemText((short)i);
            try
            {
                if (items.Count > 0)
                {
                    var first = Text(0);
                    var last = Text(items.Count - 1);
                    // Empty reads: ItemText doesn't report entries here, no conclusion.
                    if (string.IsNullOrEmpty(first) && string.IsNullOrEmpty(last)) return null;
                    if (first != items[0]) return $"entry 0 reads '{first}'";
                    if (last != items[items.Count - 1]) return $"entry {items.Count - 1} reads '{last}'";
                }
            }
            catch
            {
                return null; // ItemText isn't a reliable probe on every version
            }
            try
            {
                var extra = Text(items.Count);
                if (!string.IsNullOrEmpty(extra)) return $"an old entry '{extra}' is still listed";
            }
            catch
            {
                // Past the end: as it should be.
            }
            return null;
        }

        private bool _refillReported;

        /// <summary>
        /// A dropdown that didn't take its new entries: disabled and marked stale (OK waits), and the page says so once.
        /// The first time on this PC it switches the one-list-per-choice layout on for the next page; when that is already
        /// on, this choice has too many lists to prepare, and opening the page again (its parents set) is what helps.
        /// </summary>
        private void ComboRefillFailed(Field f, string why)
        {
            Log.Warn($"Tool page '{_spec.Name}': refilling '{f.Spec.Id}' failed ({why}).");
            f.Stale = true;
            try
            {
                ((IPropertyManagerPageControl)f.Control).Enabled = false;
            }
            catch
            {
                // The page is closing.
            }
            var first = !_quirks.ComboVariants;
            if (first)
            {
                _quirks.ComboVariants = true;
                _quirks.Save();
            }
            if (_refillReported) return;
            _refillReported = true;
            Message(first
                ? $"SolidWorks didn't update the '{f.Spec.Label}' list. Close this page and run the tool again - it will use a layout that works."
                : $"SolidWorks can't update the '{f.Spec.Label}' list while this page is open, and it has too many variants to prepare. Close the page and run the tool again to pick from the list for these settings.");
        }

        /// <summary>A data choice's tooltip names its selected row (critique C5); a version that can't change tooltips keeps the first.</summary>
        private void ShowTip(Field f)
        {
            var tip = _form.TipOf(f.Model);
            if (tip == f.Tip || !(f.Control is IPropertyManagerPageControl ctl)) return;
            try
            {
                ctl.Tip = tip;
                f.Tip = tip;
            }
            catch
            {
                // Tooltips are a courtesy.
            }
        }

        /// <summary>A unit change: the box's range in the new unit (spike S2), unless this PC can't re-range shown boxes.</summary>
        private void Rerange(Field f)
        {
            var m = f.Model;
            if (!m.Converted || !(f.Control is IPropertyManagerPageNumberbox box) || _quirks.NoRangeAfterShow) return;
            try
            {
                SetConvertedRange(f, box, wide: false);
            }
            catch (Exception ex)
            {
                RangeFailed(f, ex.Message);
            }
        }

        private void RangeFailed(Field f, string why)
        {
            Log.Warn($"Tool page '{_spec.Name}': re-ranging '{f.Spec.Id}' failed ({why}); unit changes will need the page reopened.");
            if (!_quirks.NoRangeAfterShow)
            {
                _quirks.NoRangeAfterShow = true;
                _quirks.Save();
            }
            Message($"SolidWorks couldn't switch '{f.Spec.Label}' to another unit while the page is open. Close the page and run the tool again to use it.");
            // Back to the unit the box still shows.
            if (f.ShownUnit != null && !ReferenceEquals(f.ShownUnit, f.Model.Unit)) _form.SetUnit(f.Model, f.ShownUnit.Ascii);
        }

        private void ShowValue(Field f)
        {
            var m = f.Model;
            switch (f.Type)
            {
                case "number":
                case "integer":
                {
                    if (!(f.Control is IPropertyManagerPageNumberbox box)) break;
                    var oldUnit = f.ShownUnit;
                    var want = BoxValue(f);
                    box.Value = want;
                    if (m.Converted && oldUnit != null && !ReferenceEquals(oldUnit, m.Unit))
                    {
                        // A unit change: when the box kept a different number (clamped or refused by the old range), the
                        // range didn't follow the unit (spike S2) - go back to the unit it shows.
                        var got = box.Value;
                        if (Math.Abs(got - want) > 0.005 * Math.Max(Math.Abs(want), 1e-12))
                        {
                            RangeFailed(f, $"the box kept {got} instead of {want}");
                            return;
                        }
                    }
                    f.ShownUnit = m.Unit;
                    Wrote(f);
                    if (f.UnitBox != null)
                    {
                        f.UnitBox.CurrentSelection = (short)Math.Max(0, m.UnitChoices.IndexOf(m.Unit));
                        f.SetAt = System.Environment.TickCount;
                    }
                    break;
                }
                case "size":
                    for (var k = 0; k < (f.Boxes?.Length ?? 0); k++) f.Boxes[k].Value = m.SizeSi[k];
                    break;
                case "checkbox":
                    ((IPropertyManagerPageCheckbox)f.Control).Checked = m.Checked;
                    f.SetAt = System.Environment.TickCount;
                    break;
                case "choice":
                    if (f.Stale) break; // its entries aren't the form's
                    ((IPropertyManagerPageCombobox)f.Control).CurrentSelection = (short)f.Items.IndexOf(m.Value);
                    f.SetAt = System.Environment.TickCount;
                    break;
                case "options":
                    for (var i = 0; i < f.Options.Count; i++) f.Options[i].Checked = (f.Spec.Choices ?? new string[0]).ElementAtOrDefault(i) == m.Value;
                    f.SetAt = System.Environment.TickCount;
                    break;
                case "text":
                    ((IPropertyManagerPageTextbox)f.Control).Text = m.Value ?? "";
                    break;
            }
            if (f.Toggle != null && f.Toggle.Checked != m.On)
            {
                f.Toggle.Checked = m.On;
                f.SetAt = System.Environment.TickCount;
            }
        }

        private void ShowCaption(Field f)
        {
            var caption = f.Model.Caption ?? "";
            if (f.Type == "info" || f.Type == "number" || f.Type == "integer" || f.Type == "size")
            {
                if (f.Toggle != null) f.Toggle.Caption = caption;
                else if (f.Label != null) f.Label.Caption = caption;
            }
        }

        /// <summary>ShowWhen and n/a: Visible on every control of the field (or Enabled with the S3 quirk); Optional boxes follow their checkbox.</summary>
        private void ShowVisibility(Field f)
        {
            var m = f.Model;
            var hidden = m.Hidden;
            var byDisabling = _quirks.HideByDisabling;
            foreach (var part in f.Parts)
            {
                var ctl = (IPropertyManagerPageControl)part;
                var followsToggle = m.Optional && !ReferenceEquals(part, f.Toggle) && !ReferenceEquals(part, f.Label);
                if (byDisabling)
                {
                    ctl.Enabled = !hidden && !(followsToggle && !m.On);
                }
                else
                {
                    if (ctl.Visible != !hidden) ctl.Visible = !hidden;
                    if (followsToggle) ctl.Enabled = m.On;
                }
            }
            if (f.Variants != null)
            {
                foreach (var v in f.Variants)
                {
                    var ctl = (IPropertyManagerPageControl)v.Box;
                    var show = ReferenceEquals(v, f.Active) && (!hidden || byDisabling);
                    if (ctl.Visible != show) ctl.Visible = show;
                    if (byDisabling) ctl.Enabled = !hidden;
                }
            }
            if (f.Stale && f.Control is IPropertyManagerPageControl stale) stale.Enabled = false;
        }

        /// <summary>Put what the user had already selected into the matching selection boxes.</summary>
        private void Preselect()
        {
            var boxes = _ordered.Where(f => f.Type == "selection").ToList();
            if (boxes.Count == 0 || _preselected.Count == 0) return;
            _model.ClearSelection2(true);
            var used = new HashSet<object>();
            foreach (var f in boxes)
            {
                var allowed = new HashSet<int>(SelectFilters(f.Spec.Filter));
                var matches = _preselected.Where(p => !used.Contains(p.Entity) && (allowed.Count == 0 || allowed.Contains(p.Type))).ToList();
                if (f.Spec.Multiple == false) matches = matches.Take(1).ToList();
                if (matches.Count == 0) continue;
                ((IPropertyManagerPageSelectionbox)f.Control).SetSelectionFocus();
                foreach (var m in matches)
                {
                    try
                    {
                        if (SelectionTools.Select(_model, m.Entity, true, f.Mark)) used.Add(m.Entity);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("Preselect failed: " + ex.Message);
                    }
                }
            }
            // Leave the focus on the first box that still needs something.
            var next = boxes.FirstOrDefault(f => ((IPropertyManagerPageSelectionbox)f.Control).ItemCount == 0) ?? boxes[0];
            ((IPropertyManagerPageSelectionbox)next.Control).SetSelectionFocus();
        }

        // ---- values ----

        /// <summary>Read every control. withRefs adds persistent references (needed for the agent, skipped for previews).</summary>
        private ToolInputs Collect(bool withRefs)
        {
            // At OK, catch changes SolidWorks raised no event for. Previews (from the preview timer) read the form as the
            // events left it, so no new control reads run from a timer.
            if (withRefs) Pull();
            var inputs = new ToolInputs();
            var sm = (ISelectionMgr)_model.SelectionManager;
            foreach (var f in _ordered)
            {
                var id = f.Spec.Id;
                if (f.Model?.ConditionHidden == true && (f.Type == "selection" || f.Type == "files"))
                {
                    // Hidden by ShowWhen: left out, like every other hidden input.
                    inputs.SetMeta(id, new ToolInputs.InputMeta { Hidden = true });
                    continue;
                }
                switch (f.Type)
                {
                    case "selection":
                    {
                        var items = new List<ToolInputs.SelectedItem>();
                        var count = sm.GetSelectedObjectCount2(-1);
                        for (var i = 1; i <= count; i++)
                        {
                            if (sm.GetSelectedObjectMark(i) != f.Mark) continue;
                            var obj = sm.GetSelectedObject6(i, -1);
                            var item = new ToolInputs.SelectedItem { Entity = obj };
                            if (withRefs)
                            {
                                var comp = sm.GetSelectedObjectsComponent4(i, -1) as IComponent2;
                                item.Type = SelectionTools.Pretty((swSelectType_e)sm.GetSelectedObjectType3(i, -1));
                                item.Label = SelectionTools.Owner(obj, comp);
                                item.Detail = SelectionTools.Describe(obj, comp);
                                try
                                {
                                    if (_model.Extension.GetPersistReference3(obj) is byte[] bytes) item.Ref = Convert.ToBase64String(bytes);
                                }
                                catch
                                {
                                    // Some entities have no persistent id; the agent still gets the description.
                                }
                            }
                            items.Add(item);
                        }
                        inputs.SetSelection(id, items);
                        break;
                    }
                    case "files":
                        inputs.SetFiles(id, f.Files.ToList());
                        break;
                }
            }
            // Numbers (SI), sizes, checkboxes, choices, text - with their display text, table values and data rows.
            _form.Collect(inputs);
            return inputs;
        }

        /// <summary>
        /// Read the controls into the form before collecting, in case SolidWorks didn't raise an event for a change (typed
        /// text, a number still being edited). Same values as reading the controls directly, as the page always did.
        /// </summary>
        private void Pull()
        {
            foreach (var f in _ordered)
            {
                var m = f.Model;
                if (m == null) continue;
                try
                {
                    switch (f.Type)
                    {
                        case "number":
                        case "integer":
                        {
                            var v = ((IPropertyManagerPageNumberbox)f.Control).Value;
                            if (!IsEcho(f, v, timed: false)) _form.SetNumber(m, SiFromBox(f, v));
                            break;
                        }
                        case "size":
                            for (var k = 0; k < f.Boxes.Length; k++) _form.SetSize(m, k, f.Boxes[k].Value);
                            break;
                        case "checkbox":
                            _form.SetChecked(m, ((IPropertyManagerPageCheckbox)f.Control).Checked);
                            break;
                        case "choice":
                        {
                            if (f.Stale) break; // its index no longer maps to the form's entries
                            var i = ((IPropertyManagerPageCombobox)f.Control).CurrentSelection;
                            // Plain dropdowns always read their entry like this (the default when nothing is picked).
                            if (i >= 0 && i < f.Items.Count) _form.SetChoice(m, f.Items[i]);
                            else if (!m.IsData) m.Value = null;
                            break;
                        }
                        case "options":
                        {
                            var i = f.Options.FindIndex(o => o.Checked);
                            var choices = f.Spec.Choices ?? new string[0];
                            if (i >= 0 && i < choices.Length) _form.SetChoice(m, choices[i]);
                            else m.Value = null;
                            break;
                        }
                        case "text":
                            _form.SetText(m, ((IPropertyManagerPageTextbox)f.Control).Text ?? "");
                            break;
                    }
                    if (f.Toggle != null) _form.SetOn(m, f.Toggle.Checked);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Tool page '{_spec.Name}': reading '{f.Spec.Id}' failed: {ex.Message}");
                }
            }
        }

        private bool RequiredSatisfied()
        {
            foreach (var f in _ordered.Where(x => ToolSpec.IsRequired(x.Spec)))
            {
                if (f.Model?.Hidden == true) continue; // not shown: not asked for
                switch (f.Type)
                {
                    case "selection" when ((IPropertyManagerPageSelectionbox)f.Control).ItemCount == 0:
                    case "files" when f.Files.Count == 0:
                    case "text" when string.IsNullOrWhiteSpace(((IPropertyManagerPageTextbox)f.Control).Text):
                        return false;
                }
            }
            if (StaleField() != null) return false;
            return _form?.Blocking().Field == null;
        }

        /// <summary>A shown dropdown that didn't take its new entries (see ComboRefillFailed): OK waits for the page to reopen.</summary>
        private Field StaleField() => _ordered.FirstOrDefault(f => f.Stale && f.Model?.Hidden != true);

        private void UpdateOkButton()
        {
            try
            {
                var ok = RequiredSatisfied();
                _page?.EnableButton((int)swPropertyManagerPageButtons_e.swPropertyManagerPageButton_Ok, ok);
                ExplainBlocking();
            }
            catch
            {
                // Older SolidWorks: the button just stays enabled.
            }
        }

        /// <summary>When a newer rule (a required choice, an Optional to switch on, a value out of range) holds OK back, say why at that control once.</summary>
        private void ExplainBlocking()
        {
            var (field, message) = _form?.Blocking() ?? (null, null);
            var stale = StaleField();
            if (stale?.Model != null)
            {
                field = stale.Model;
                message = "SolidWorks didn't update this list. Close the page and run the tool again.";
            }
            var key = field == null ? null : field.Id + "|" + message;
            if (key == _bubbleFor) return;
            _bubbleFor = key;
            if (field == null || !_byInputId.TryGetValue(field.Id, out var f)) return;
            try
            {
                var target = (object)f.Toggle ?? f.Control;
                if (target is IPropertyManagerPageControl ctl) ctl.ShowBubbleTooltip(f.Spec.Label ?? f.Spec.Id, message, "");
            }
            catch
            {
                // Tooltips are a courtesy.
            }
        }

        private void Changed()
        {
            UpdateOkButton();
            SchedulePreview();
            ShowProfileState();
        }

        // ---- preview ----

        private void SchedulePreview()
        {
            if (_previewRunner == null || !_livePreview) return;
            _previewTimer.Stop();
            _previewTimer.Start();
        }

        private async Task RunPreviewAsync()
        {
            if (_previewRunner == null || _previewRunning || _page == null) return;
            if (_model.GetType() != (int)swDocumentTypes_e.swDocPART)
            {
                Message("Preview is available in part documents.");
                return;
            }
            _previewRunning = true;
            var watch = Stopwatch.StartNew();
            try
            {
                HidePreview();
                if (!RequiredSatisfied()) return;
                // If SolidWorks dies in here, the next start knows it was this tool's preview and pauses it.
                using (CrashGuard.Enter("preview", _spec.Name, _spec.Title, _spec.Preview))
                {
                    var globals = new ScriptGlobals { swApp = _sw, Model = _model, Inputs = Collect(withRefs: false) };
                    await _previewRunner(globals);
                    _shownBodies.AddRange(PreviewDisplay.Show(_model, globals.PreviewBodies));
                }
                Message(null);
            }
            catch (Exception ex)
            {
                var inner = ex is AggregateException agg ? agg.InnerException ?? ex : ex;
                Message("Preview: " + (inner is MacroInputException ? inner.Message : inner.GetType().Name + ": " + inner.Message));
            }
            finally
            {
                _previewRunning = false;
                if (watch.ElapsedMilliseconds > PreviewBudgetMs && _livePreview)
                {
                    // Never let the preview make the page feel sluggish.
                    _livePreview = false;
                    Message("This preview is slow on this model, so it updates when you press the eye (Preview) button.");
                }
            }
        }

        private void HidePreview() => PreviewDisplay.Hide(_model, _shownBodies);

        private void Message(string text)
        {
            try
            {
                if (text == null)
                {
                    if (!string.IsNullOrWhiteSpace(_spec.Description))
                        _page.SetMessage3(_spec.Description, (int)swPropertyManagerPageMessageVisibility.swMessageBoxVisible,
                            (int)swPropertyManagerPageMessageExpanded.swMessageBoxMaintainExpandState, _spec.Title);
                    return;
                }
                _page.SetMessage3(text, (int)swPropertyManagerPageMessageVisibility.swImportantMessageBox,
                    (int)swPropertyManagerPageMessageExpanded.swMessageBoxExpand, _spec.Title);
            }
            catch
            {
                // Page closing.
            }
        }

        // ---- handler ----

        public void OnClose(int reason)
        {
            _previewTimer.Stop();
            if (reason == (int)swPropertyManagerPageCloseReasons_e.swPropertyManagerPageClose_Okay)
            {
                try
                {
                    _result = Collect(withRefs: true);
                }
                catch (Exception ex)
                {
                    Log.Error("Reading the tool page failed", ex);
                }
                // Inputs with "Remember": their value (spec units) becomes next run's default on this PC.
                if (_result != null && _form != null) RememberStore.Save(_spec.Name, _form.RememberJson());
                if (_result != null && _form != null) _profiles?.Finished(_form, _result);
            }
            HidePreview();
        }

        public void AfterClose()
        {
            if (_counted) { _counted = false; System.Threading.Interlocked.Decrement(ref _open); }
            var result = _result;
            _page = null;
            _keep.Clear();
            _guard?.Dispose();
            _guard = null;
            // Let SolidWorks finish closing the page before the tool starts working.
            if (_ui != null) _ui.Post(_ => _done.TrySetResult(result), null);
            else _done.TrySetResult(result);
        }

        public bool OnPreview()
        {
            _ = RunPreviewAsync();
            return true;
        }

        public void OnSelectionboxListChanged(int id, int count) => Changed();

        public void OnNumberboxChanged(int id, double value)
        {
            if (_quiet > 0) return;
            NumberChanged(id, value);
            Changed();
        }

        public void OnNumberBoxTrackingCompleted(int id, double value)
        {
            if (_quiet > 0) return;
            NumberChanged(id, value);
            Changed();
        }

        public void OnCheckboxCheck(int id, bool check)
        {
            if (_quiet > 0 || IsProfileControl(id)) return;
            if (_fields.TryGetValue(id, out var f) && f.Model != null && !IsLateEvent(f))
            {
                Try(f, () =>
                {
                    if (id == f.ToggleId) _form.SetOn(f.Model, check); // an Optional's switch
                    else if (f.Type == "checkbox") _form.SetChecked(f.Model, check);
                });
                Sync();
            }
            Changed();
        }

        public void OnOptionCheck(int id)
        {
            if (_quiet > 0) return;
            // Options can be parents of dependent choices, like dropdowns.
            if (_fields.TryGetValue(id, out var f) && f.Model != null && f.Type == "options" && !IsLateEvent(f))
            {
                var i = f.OptionIds.IndexOf(id);
                var choices = f.Spec.Choices ?? new string[0];
                if (i >= 0 && i < choices.Length) Try(f, () => _form.SetChoice(f.Model, choices[i]));
                Sync();
            }
            Changed();
        }

        public void OnTextboxChanged(int id, string text)
        {
            if (_quiet > 0 || IsProfileControl(id)) return;
            if (_fields.TryGetValue(id, out var f) && f.Model != null && f.Type == "text")
            {
                Try(f, () => _form.SetText(f.Model, text));
                Sync();
            }
            UpdateOkButton();
        }

        public void OnComboboxSelectionChanged(int id, int item)
        {
            if (_quiet > 0) return;
            if (id == _tierBoxId)
            {
                if (item >= 0 && item < ToolSpec.Tiers.Length) TierChanged?.Invoke(ToolSpec.Tiers[item]);
                return;
            }
            if (OnProfileCombo(id, item)) return;
            if (_fields.TryGetValue(id, out var f) && f.Model != null && !IsLateEvent(f) && !(f.Stale && id != f.UnitBoxId))
            {
                if (id == f.UnitBoxId)
                {
                    if (item >= 0 && item < f.Model.UnitChoices.Count) Try(f, () => _form.SetUnit(f.Model, f.Model.UnitChoices[item].Ascii));
                }
                else if (f.Type == "choice")
                {
                    // With variants only the visible dropdown speaks; a hidden one's event is stale.
                    var items = f.Variants == null ? f.Items : ReferenceEquals(f.Variants.FirstOrDefault(v => v.Id == id), f.Active) ? f.Active.Items : null;
                    if (items != null && item >= 0 && item < items.Count) Try(f, () => _form.SetChoice(f.Model, items[item]));
                }
                Sync();
            }
            Changed();
        }

        private void NumberChanged(int id, double value)
        {
            if (!_fields.TryGetValue(id, out var f) || f.Model == null) return;
            Try(f, () =>
            {
                if (f.Type == "size") _form.SetSize(f.Model, Array.IndexOf(f.BoxIds, id), value);
                else if ((f.Type == "number" || f.Type == "integer") && id == f.Id && !IsEcho(f, value, timed: true)) _form.SetNumber(f.Model, SiFromBox(f, value));
            });
            Sync();
        }

        private void Try(Field f, Action a)
        {
            try
            {
                a();
            }
            catch (Exception ex)
            {
                Log.Warn($"Tool page '{_spec.Name}': handling a change of '{f.Spec.Id}' failed: {ex.Message}");
            }
        }

        public void OnButtonPress(int id)
        {
            if (OnProfileButton(id)) return;
            if (!_fields.TryGetValue(id, out var f) || f.Type != "files") return;
            if (id == f.ClearButtonId)
            {
                f.Files.Clear();
                f.FileList.Clear();
            }
            else if (id == f.AddButtonId)
            {
                using (var dialog = new System.Windows.Forms.OpenFileDialog
                {
                    Title = f.Spec.Label,
                    Multiselect = f.Spec.Multiple != false,
                    Filter = FileFilter(f.Spec.Accept),
                })
                {
                    if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                    foreach (var path in dialog.FileNames)
                    {
                        if (f.Spec.Multiple == false)
                        {
                            f.Files.Clear();
                            f.FileList.Clear();
                        }
                        if (f.Files.Contains(path)) continue;
                        f.Files.Add(path);
                        f.FileList.AddItems(new[] { Path.GetFileName(path) });
                    }
                }
            }
            Changed();
        }

        public void AfterActivation() { }
        public bool OnHelp() => false;
        public bool OnPreviousPage() => false;
        public bool OnNextPage() => false;
        public void OnWhatsNew() { }
        public void OnUndo() { }
        public void OnRedo() { }
        public bool OnTabClicked(int id) => true;
        public void OnGroupExpand(int id, bool expanded) { }
        public void OnGroupCheck(int id, bool check) { }
        public void OnComboboxEditChanged(int id, string text) { }
        public void OnListboxSelectionChanged(int id, int item) { }
        public void OnSelectionboxFocusChanged(int id) { }
        public void OnSelectionboxCalloutCreated(int id) { }
        public void OnSelectionboxCalloutDestroyed(int id) { }
        public bool OnSubmitSelection(int id, object selection, int selType, ref string itemText) => true;
        public int OnActiveXControlCreated(int id, bool status) => 0;
        public void OnSliderPositionChanged(int id, double value) { }
        public void OnSliderTrackingCompleted(int id, double value) { }
        public bool OnKeystroke(int wparam, int message, int lparam, int id) => false;
        public void OnPopupMenuItem(int id) { }
        public void OnPopupMenuItemUpdate(int id, ref int retval) { }
        public void OnGainedFocus(int id) { }
        public void OnLostFocus(int id) { }
        public int OnWindowFromHandleControlCreated(int id, bool status) => 0;
        public void OnListboxRMBUp(int id, int posX, int posY) { }

        // ---- running without the page ----

        /// <summary>
        /// When everything the tool needs is already selected (and it doesn't ask to always show its page), build the
        /// inputs from the selection and defaults so it can run straight away.
        /// </summary>
        internal static ToolInputs TryFromSelection(ToolSpec spec, IModelDoc2 model, List<SelectedEntity> preselected, bool forTest = false, string profile = null)
        {
            if (spec.Confirm == "always" && !forTest) return null;
            // Inputs using the newer options (units, Optional, size, data choices, fills, ShowWhen, remembered values) get
            // the page's defaults from the form; the original ones keep exactly the defaults they always had. With a profile
            // loaded (asked for, or the default) every value comes from the form.
            var form = CreateForm(spec, model, out _, profile, out var profiles);
            var fromProfile = profiles.Loaded != null;
            var inputs = new ToolInputs();
            var used = new HashSet<object>();
            foreach (var s in spec.Inputs)
            {
                var type = ToolSpec.Type(s);
                var field = s?.Id == null ? null : form.Get(s.Id);
                if (field != null && (!field.IsPlain || fromProfile) && type != "selection" && type != "files")
                {
                    // A note the tool needs and the profile left empty: the page asks for it.
                    if (type == "text" && ToolSpec.IsRequired(s) && string.IsNullOrWhiteSpace(field.Value) && !forTest) return null;
                    continue;
                }
                switch (type)
                {
                    case "selection":
                    {
                        var allowed = new HashSet<int>(SelectFilters(s.Filter));
                        var matches = preselected.Where(p => !used.Contains(p.Entity) && (allowed.Count == 0 || allowed.Contains(p.Type))).ToList();
                        if (s.Multiple == false) matches = matches.Take(1).ToList();
                        if (matches.Count == 0 && ToolSpec.IsRequired(s) && !forTest) return null;
                        var items = new List<ToolInputs.SelectedItem>();
                        foreach (var m in matches)
                        {
                            used.Add(m.Entity);
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
                                // No persistent id.
                            }
                            items.Add(item);
                        }
                        inputs.SetSelection(s.Id, items);
                        break;
                    }
                    case "number":
                    case "integer":
                        inputs.Set(s.Id, DefaultNumber(s, type));
                        break;
                    case "checkbox":
                        inputs.Set(s.Id, string.Equals(s.Default, "true", StringComparison.OrdinalIgnoreCase));
                        break;
                    case "choice":
                    case "options":
                        inputs.Set(s.Id, s.Default ?? s.Choices?.FirstOrDefault());
                        break;
                    case "text":
                    case "files":
                        if (ToolSpec.IsRequired(s) && !forTest) return null;
                        if (type == "text") inputs.Set(s.Id, s.Default ?? "");
                        else inputs.SetFiles(s.Id, new List<string>());
                        break;
                }
            }
            form.Collect(inputs, f => !f.IsPlain || fromProfile);
            profiles.Finished(form, inputs);
            return inputs;
        }

        /// <summary>What's selected right now, in order.</summary>
        internal static List<SelectedEntity> CurrentSelection(IModelDoc2 model)
        {
            var list = new List<SelectedEntity>();
            if (!(model?.SelectionManager is ISelectionMgr sm)) return list;
            var count = sm.GetSelectedObjectCount2(-1);
            for (var i = 1; i <= count; i++)
            {
                try
                {
                    list.Add(new SelectedEntity
                    {
                        Entity = sm.GetSelectedObject6(i, -1),
                        Type = sm.GetSelectedObjectType3(i, -1),
                        Component = sm.GetSelectedObjectsComponent4(i, -1) as IComponent2,
                    });
                }
                catch
                {
                    // Skip entities that can't be read.
                }
            }
            return list;
        }

        // ---- helpers ----

        private static double Mm(double v) => v / 1000.0;
        private static double Rad(double v) => v * Math.PI / 180.0;

        private static double DefaultNumber(ToolInputSpec s, string type)
        {
            var value = double.TryParse(s.Default, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : (s.Min ?? 0);
            if (type == "integer") return Math.Round(value);
            var unit = (s.Unit ?? "length").ToLowerInvariant();
            return unit == "angle" ? Rad(value) : unit == "length" ? Mm(value) : value;
        }

        internal static int[] SelectFilters(string[] names)
        {
            var list = new List<swSelectType_e>();
            foreach (var n in names ?? new string[0])
            {
                switch ((n ?? "").Trim().ToLowerInvariant())
                {
                    case "face": list.Add(swSelectType_e.swSelFACES); break;
                    case "edge": list.Add(swSelectType_e.swSelEDGES); break;
                    case "vertex": list.Add(swSelectType_e.swSelVERTICES); break;
                    case "feature": list.Add(swSelectType_e.swSelBODYFEATURES); break;
                    case "body": list.Add(swSelectType_e.swSelSOLIDBODIES); list.Add(swSelectType_e.swSelSURFACEBODIES); break;
                    case "component": list.Add(swSelectType_e.swSelCOMPONENTS); break;
                    case "plane": list.Add(swSelectType_e.swSelDATUMPLANES); break;
                    case "sketch": list.Add(swSelectType_e.swSelSKETCHES); break;
                    case "sketch_segment": list.Add(swSelectType_e.swSelSKETCHSEGS); break;
                    case "point": list.Add(swSelectType_e.swSelDATUMPOINTS); list.Add(swSelectType_e.swSelSKETCHPOINTS); list.Add(swSelectType_e.swSelVERTICES); break;
                }
            }
            return list.Distinct().Select(x => (int)x).ToArray();
        }

        private static swControlBitmapLabelType_e PictureFor(string icon, string[] filter)
        {
            switch ((icon ?? "").Trim().ToLowerInvariant())
            {
                case "distance": return swControlBitmapLabelType_e.swBitmapLabel_LinearDistance;
                case "depth": return swControlBitmapLabelType_e.swBitmapLabel_Depth;
                case "width": return swControlBitmapLabelType_e.swBitmapLabel_Width;
                case "thickness": return swControlBitmapLabelType_e.swBitmapLabel_Thickness1;
                case "radius": return swControlBitmapLabelType_e.swBitmapLabel_Radius;
                case "diameter": return swControlBitmapLabelType_e.swBitmapLabel_Diameter;
                case "angle": return swControlBitmapLabelType_e.swBitmapLabel_AngularDistance;
                case "face": return swControlBitmapLabelType_e.swBitmapLabel_SelectFace;
                case "edge": return swControlBitmapLabelType_e.swBitmapLabel_SelectEdge;
                case "vertex": return swControlBitmapLabelType_e.swBitmapLabel_SelectVertex;
                case "face_edge": return swControlBitmapLabelType_e.swBitmapLabel_SelectFaceEdge;
                case "component": return swControlBitmapLabelType_e.swBitmapLabel_SelectComponent;
                case "profile": return swControlBitmapLabelType_e.swBitmapLabel_SelectProfile;
            }
            var f = (filter ?? new string[0]).Select(x => x.ToLowerInvariant()).ToList();
            if (f.Count == 1 && f[0] == "face") return swControlBitmapLabelType_e.swBitmapLabel_SelectFace;
            if (f.Count == 1 && f[0] == "edge") return swControlBitmapLabelType_e.swBitmapLabel_SelectEdge;
            if (f.Count == 1 && f[0] == "vertex") return swControlBitmapLabelType_e.swBitmapLabel_SelectVertex;
            if (f.Count == 1 && f[0] == "component") return swControlBitmapLabelType_e.swBitmapLabel_SelectComponent;
            if (f.Count == 2 && f.Contains("face") && f.Contains("edge")) return swControlBitmapLabelType_e.swBitmapLabel_SelectFaceEdge;
            return swControlBitmapLabelType_e.swBitmapLabel_SelectEdgeFaceVertex;
        }

        private static string AcceptText(string[] accept)
        {
            var a = (accept ?? new string[0]).Select(x => x.ToLowerInvariant()).ToList();
            if (a.Count == 0) return "files";
            return string.Join(", ", a.Select(x => x == "image" ? "images" : x == "pdf" ? "PDFs" : x == "model" ? "3D models" : x == "drawing" ? "drawings" : x));
        }

        private const string ImageFiles = "*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp", PdfFiles = "*.pdf",
            ModelFiles = "*.stl;*.step;*.stp;*.3mf;*.igs;*.iges;*.sldprt;*.sldasm;*.x_t", DrawingFiles = "*.slddrw", CadDrawingFiles = "*.dxf;*.dwg";

        internal static string FileFilter(string[] accept)
        {
            var a = (accept ?? new string[0]).Select(x => x.ToLowerInvariant()).ToList();
            var parts = new List<string>();
            if (a.Contains("drawing"))
            {
                // Drawings usually come with PDFs or images: one entry that shows everything the input takes.
                var all = new List<string>();
                if (a.Contains("image")) all.Add(ImageFiles);
                if (a.Contains("pdf")) all.Add(PdfFiles);
                if (a.Contains("model")) all.Add(ModelFiles);
                all.Add(DrawingFiles);
                all.Add(CadDrawingFiles);
                var joined = string.Join(";", all);
                parts.Add($"Supported files ({joined})|{joined}");
            }
            if (a.Count == 0 || a.Contains("image")) parts.Add($"Images ({ImageFiles})|{ImageFiles}");
            if (a.Count == 0 || a.Contains("pdf")) parts.Add($"PDF documents ({PdfFiles})|{PdfFiles}");
            if (a.Count == 0 || a.Contains("model")) parts.Add($"3D models ({ModelFiles})|{ModelFiles}");
            if (a.Contains("drawing"))
            {
                parts.Add($"SolidWorks drawings ({DrawingFiles})|{DrawingFiles}");
                parts.Add($"DXF/DWG ({CadDrawingFiles})|{CadDrawingFiles}");
            }
            parts.Add("All files (*.*)|*.*");
            return string.Join("|", parts);
        }
    }

    /// <summary>
    /// How this PC's SolidWorks handles changes to a PropertyManager page after it is shown (spikes S2, S3), in
    /// %APPDATA%\SwAgent\tools\page-quirks.json. The page sets the first two itself when it sees a failure; the user sets
    /// them by hand after running tools/dev/page-test:
    ///   "comboRefill": "variants"  - dropdowns can't be refilled once shown: one dropdown per entry list, toggled visible;
    ///   "rangeAfterShow": false    - number boxes can't be re-ranged once shown: unit-choice boxes get a range wide enough
    ///                                for every unit (the form checks the real range before OK);
    ///   "hidden": "disable"        - Visible=false misbehaves: hidden inputs stay in place, disabled.
    /// </summary>
    internal sealed class PageQuirks
    {
        public bool ComboVariants;
        public bool NoRangeAfterShow;
        public bool HideByDisabling;

        private string _path;

        public static string DefaultPath => Path.Combine(UserTools.Root, "page-quirks.json");

        public static PageQuirks Load(string path = null)
        {
            var q = new PageQuirks { _path = path };
            try
            {
                path = path ?? DefaultPath;
                if (!File.Exists(path)) return q;
                var o = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path));
                q.ComboVariants = string.Equals((string)o["comboRefill"], "variants", StringComparison.OrdinalIgnoreCase);
                q.NoRangeAfterShow = o["rangeAfterShow"]?.Type == Newtonsoft.Json.Linq.JTokenType.Boolean && !(bool)o["rangeAfterShow"];
                q.HideByDisabling = string.Equals((string)o["hidden"], "disable", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Log.Warn("page-quirks.json unreadable: " + ex.Message);
            }
            return q;
        }

        public void Save()
        {
            try
            {
                var path = _path ?? DefaultPath;
                var o = new Newtonsoft.Json.Linq.JObject
                {
                    ["comboRefill"] = ComboVariants ? "variants" : "dynamic",
                    ["rangeAfterShow"] = !NoRangeAfterShow,
                    ["hidden"] = HideByDisabling ? "disable" : "hide",
                };
                File.WriteAllText(path, o.ToString());
            }
            catch (Exception ex)
            {
                Log.Warn("Saving page-quirks.json failed: " + ex.Message);
            }
        }
    }
}
