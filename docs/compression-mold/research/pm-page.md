# Tool PropertyManager page: how it works today, and what the mold tool needs

Repo root (repository): `C:/Users/msmit/Desktop/Software Projects/Unify3D Agent`
Files read: `src/SwAgent/UI/ToolPage.cs` (831 lines), `src/SwAgent/Tools/ToolSpec.cs` (162), `src/SwAgent/Tools/ToolRunner.cs` (271),
`src/SwAgent/Scripting/ToolInputs.cs`, `ui/src/store.ts` (stageToolPrompt, 272-322), `ui/src/lib/types.ts` (ToolRunPayload, 252-267),
`tools/builtin/smart-rebuild/inputs.json`.
API facts below were checked by reflecting the interop DLLs actually shipped in `src/SwAgent/bin/Debug/net48`
(`SolidWorks.Interop.swconst.dll` version 34.1.1.11 = SolidWorks 2026, pulled in by Xarial.XCad.SolidWorks 0.8.3).
Anything marked **(spike)** is SolidWorks runtime behaviour I could not confirm from the DLLs and should be checked in SW before relying on it.

---------------------------------------------------------------------------------------------------------------------

## 1. The data model: `ToolInputSpec` (ToolSpec.cs:14-62)

One JSON object per input in the tool folder's `inputs.json`, deserialised by Newtonsoft (`ToolSpec.LoadInputs`, ToolSpec.cs:129-142;
a parse error logs a warning and yields **no inputs** - silent). Property names are PascalCase in the file (`"Id"`, `"Type"`...), but
Newtonsoft is case-insensitive on read. `SaveInputs` (144-154) writes with `NullValueHandling.Ignore`.

| Property | Type | Meaning today |
|---|---|---|
| `Id` | string | key used by macro/preview/agent (`Inputs.Number("offset")`) |
| `Type` | string | `selection, number, integer, checkbox, choice, options, text, files` (lower-cased by `ToolSpec.Type`, 158) |
| `Label` | string | caption (a separate Label control above most controls) |
| `Tip` | string | tooltip (falls back to Label, ToolPage.cs:216) |
| `Required` | bool? | default: true for selection, false otherwise (`ToolSpec.IsRequired`, 156) |
| `Default` | string | number in the input's *spec unit* (mm / deg), "true"/"false", or the choice text |
| `Min`, `Max` | double? | number/integer, in spec unit |
| `Unit` | string | doc says `length | angle | none | percent` (41); only length and angle are actually handled |
| `Filter` | string[] | selection filters |
| `Multiple` | bool? | selection / files: several allowed (default true) |
| `Choices` | string[] | choice / options entries (static only) |
| `Accept` | string[] | files: image, pdf, model |
| `Icon` | string | native picture label name |
| `Group` | string | group box caption |

The `[ToolParam]` descriptions on these properties are also the schema the agent sees in `create_user_tool`/`edit_user_tool`
(UserTools.cs:156 and 241 take `ToolInputSpec[] Inputs`), so any new property must get a good `ToolParam` text, and
`ui/src/agent/systemPrompt.ts:69` (how the agent designs inputs pages) should mention it.

`ToolSpec` itself (65-127) does **not** keep the tool folder path: `Load` computes `dir` (102) and throws it away. Presets from a JSON
file in the tool folder need a new `public string Dir;` set in `Load`.

---------------------------------------------------------------------------------------------------------------------

## 2. How each input type renders (ToolPage.AddField, ToolPage.cs:213-332)

All controls are created through `Control(...)` (198-203) = `IPropertyManagerPageGroup.AddControl2(id, type, caption, align, options, tip)`
and are added to `_keep` (48) so the RCWs live as long as the page (a GC'd control crashes SolidWorks on the next read - see the
comment at 42-47). **Every new control in the extensions below must go through `Control()` / `Group()` for the same reason.**
Options for all controls: `Visible | Enabled` (205). IDs: `_nextId` starts at 10 (53) and increments per control.

| Type | Controls created | Units / range | Default | Notes |
|---|---|---|---|---|
| `selection` (220-235) | `swControlType_Selectionbox` (9) | - | pre-filled from the selection that existed when the tool started (`Preselect`, 335-363) | `SetSelectionFilters(SelectFilters(Filter))` (764-784), `Mark` = 2,4,8... (227-229) so each box owns its selected items, `SingleEntityOnly = Multiple==false`, height 15 or 50, picture label from Icon/filter (786-811) |
| `number` (236-257) | Label (separate control, 239) + `swControlType_Numberbox` (8) | `Unit` null/"length" -> `swNumberBox_Length` (3), range `Mm(Min??0)..Mm(Max??10000)`, increments 1/10/0.1 mm; "angle" -> `swNumberBox_Angle` (4), `Rad(Min??0)..Rad(Max??360)`, increments 1/15/0.1 deg; **anything else** ("none", "percent", typos) -> `swNumberBox_UnitlessDouble` (2), range `Min??-1e9..Max??1e9`, increments 1/10/0.1 | `DefaultNumber` (756-762): `Default` parsed with InvariantCulture, else `Min??0`, converted mm->m or deg->rad | `SetRange2(unitType, min, max, inclusive:true, inc, fastInc, slowInc)`. Length/angle boxes get a picture label (distance / angle) even without `Icon` (253-254). Length boxes display in the **document's length unit automatically** (SolidWorks does this), user may type `1in`, `25.4mm`, expressions; `.Value` is always metres. |
| `integer` (236-257) | Label + Numberbox | `swNumberBox_UnitlessInteger` (1), `Min??0 .. Max??1000`, increments 1/5/1 | rounded `Default` | `Unit` ignored |
| `checkbox` (258-266) | `swControlType_Checkbox` (2), LeftEdge aligned, caption = Label (no separate label) | - | `Default=="true"` (case-insensitive) | |
| `choice` (267-278) | Label + `swControlType_Combobox` (7) | - | `CurrentSelection = IndexOf(Choices, Default)` or 0 (exact, case-sensitive match) | `AddItems(string[])`; items static; no Style set (non-editable dropdown) |
| `options` (279-295) | Label + one `swControlType_Option` (4) per choice | - | Default text, else first | every option ID maps to the same Field (`_fields[id] = f`, 292); `f.Id` = first option's ID |
| `text` (296-308) | Label + `swControlType_Textbox` (5), LeftEdge | - | `Default ?? ""` | `Style = swPropMgrPageTextBoxStyle_Multiline` (8), `Height = 50`, tip gets " (Win+H to dictate)" appended (302) |
| `files` (309-326) | Label + `swControlType_Listbox` (6, height 40) + Button "Add file..." + Button "Clear" | - | empty | see 2.3 |
| anything else (327-329) | a Label "<label> (unsupported input type 'x')" | | | not added to `_fields`; `Collect` ignores it |

### 2.1 Groups (Build, 157-189)
Group caption = `input.Group`, else "Selections" (selection), "Context" (text, files) or "Parameters" (165). Groups are created in order
of first appearance with `swGroupBoxOptions_Visible | swGroupBoxOptions_Expanded`. Group IDs come from a **separate counter starting at
1** (`groupId`, 160), while control IDs start at 10. For agent tools an extra collapsed group "SolidWorks Agent" with an "Intelligence"
combobox is appended (177-188; `_tierBoxId`, items `TierNames`, initial from `spec.Intelligence`).

**Latent bug for a big page:** with 9 or more distinct groups (+ the agent group) the group ID counter reaches 10 and collides with the
first control ID (53: `_nextId = 10`). The mold page will plausibly have 8-10 groups. Fix: take group IDs from `_nextId++` too (or start
`_nextId` at 1000). Also: `OnGroupExpand/OnGroupCheck(int id, ...)` then become unambiguous.

### 2.2 Tips, icons, message box
- Tip = `Tip ?? Label ?? ""` (216), passed as the AddControl2 `Tip` argument.
- Page message: `SetMessage3(Description, swMessageBoxVisible, swMessageBoxExpand, Title)` at show (115-117); `Message(text)` (523-541)
  shows errors as `swImportantMessageBox` and restores the description when `text==null`.
- Picture labels: `IPropertyManagerPageControl.SetStandardPictureLabel(swControlBitmapLabelType_e)`, mapping in `PictureFor` (786-811).

### 2.3 Files picker (OnButtonPress, 597-629)
- "Add file..." opens a WinForms `OpenFileDialog` (no owner window passed) with `Title = Label`, `Multiselect = Multiple != false`,
  `Filter = FileFilter(Accept)` (820-829): Images (png/jpg/jpeg/gif/webp/bmp), PDF, 3D models (stl/step/stp/3mf/igs/iges/sldprt/sldasm/x_t),
  always "All files". No `.slddrw` or `.dxf/.dwg` in the model filter - **the mold tool's "engineering drawing" input should accept
  PDF + image and probably `.slddrw`/`.dxf`** (new accept keyword `drawing`).
- `Multiple == false`: each picked file replaces the list. Duplicates skipped. The list shows only `Path.GetFileName`; full paths are in
  `Field.Files`. "Clear" empties both. There is no "remove one" (Listbox selection is not used; `OnListboxSelectionChanged` is empty).
- After either button: `Changed()`.

### 2.4 Required checks and the OK button (439-464)
`RequiredSatisfied()` only checks: selection box `ItemCount==0`, files list empty, text blank. **`Required` on number, integer, choice,
options or checkbox is ignored.** `UpdateOkButton()` calls `IPropertyManagerPage2.EnableButton(swPropertyManagerPageButton_Ok, ok)`
(try/catch for old SW). There is no other validation (no cross-field checks, no "value must be > 0" messages).

### 2.5 Page events (handler = `IPropertyManagerPage2Handler9`, 545-656)
`ToolPage` implements every Handler9 method (37 methods, list confirmed by reflection). Live ones:

| Event | What it does |
|---|---|
| `OnSelectionboxListChanged(id,count)` (580) | `Changed()` |
| `OnNumberboxChanged(id,value)` (581) | `Changed()` (fires while typing / spinning) |
| `OnNumberBoxTrackingCompleted(id,value)` (582) | `Changed()` |
| `OnCheckboxCheck(id,check)` (583) | `Changed()` |
| `OnOptionCheck(id)` (584) | `Changed()` |
| `OnTextboxChanged(id,text)` (585) | `UpdateOkButton()` only (no preview) |
| `OnComboboxSelectionChanged(id,item)` (587-595) | tier box -> `TierChanged` event (ToolRunner saves it, ToolRunner.cs:82-93); else `Changed()` |
| `OnButtonPress(id)` (597-629) | files Add/Clear |
| `OnPreview()` (574) | runs preview on demand |
| `OnClose(reason)` (545-560) | on `swPropertyManagerPageClose_Okay` -> `_result = Collect(withRefs:true)`; hides preview |
| `AfterClose()` (562-572) | releases `_keep`, posts `_done.TrySetResult(result)` to the UI sync context |

Everything else (`OnGroupCheck`, `OnGroupExpand`, `OnComboboxEditChanged`, `OnListboxSelectionChanged`, `OnSliderPositionChanged`,
`OnGainedFocus/OnLostFocus`, `OnSubmitSelection` (returns true), `OnKeystroke`, `OnWindowFromHandleControlCreated`...) is empty.
`Changed()` (466-470) = `UpdateOkButton()` + `SchedulePreview()` (a 120 ms debounce timer, 59, 474-479). No handler knows *which*
field changed - all field-specific logic (cascades, presets, enabling) has to be added here.

### 2.6 Reading values on OK (Collect, 368-437) and what macros/agents get
`ToolInputs` (src/SwAgent/Scripting/ToolInputs.cs) is a bag: `_values`, `_selections`, `_files`.
- selection: walks `ISelectionMgr` items whose `GetSelectedObjectMark(i) == f.Mark`; with refs: `GetSelectedObjectType3`, `GetSelectedObjectsComponent4`,
  `Extension.GetPersistReference3` -> base64.
- number/integer: `IPropertyManagerPageNumberbox.Value` **raw** (m, rad, or plain number) -> `inputs.Set(id, double)`.
- checkbox: `.Checked`. choice: `CurrentSelection` index into **`f.Spec.Choices`** (415-419) - breaks as soon as items become dynamic.
- options: index of the checked option into `Spec.Choices`. text: `.Text`. files: copy of `f.Files`.

`ToolInputs.Number(id)` documents "Lengths are in meters and angles in radians" (ToolInputs.cs:11-12).

### 2.7 ToolRunner use (ToolRunner.cs)
- Line 78: `spec.Inputs.Count == 0 ? new ToolInputs() : ToolPage.TryFromSelection(spec, model, preselected)`.
  **`TryFromSelection` (ToolPage.cs:664-724) skips the page** unless `confirm: always` (666), a required selection is missing (679) or a
  required text/files input exists (717). A page made only of numbers/choices with no required selection is **never shown** unless the
  tool's TOOL.md says `confirm: always`. -> the mold tool must set `confirm: always` (Smart Rebuild does). TryFromSelection fills
  numbers from `DefaultNumber`, choices from `Default ?? Choices[0]` (713) - it must learn any new types (ToolTester.cs:51 and 89 use it with `forTest: true`).
- 81-97: `new ToolPage(...)`, `TierChanged` saves intelligence, `page.ShowAsync(_scripts)`; model context captured while the page is open (96).
- 110: `LastInputs[spec.Name] = inputs` (used by `run_tool_macro`).
- `BuildPayloadAsync` (217-266) builds the `runTool` JSON for the panel: per input `{id,label,type}` plus
  - selection: `items[{type,label,detail,ref}]`
  - **`number` only** (232-240): `value` string - length `"12.5 mm (0.0125 m)"`, angle `"30 deg"`, else the raw number (`0.######`)
  - files: `FileAttachments.EntryAsync` per path (3D models are read into views + measurements)
  - default (integer, checkbox, choice, options, text): `JToken.FromObject(inputs[id] ?? "")`
- Panel side: `stageToolPrompt` (ui/src/store.ts:272-322) prints `- <label> (<id>): <value>` lines inside `<tool_inputs>`; non-string
  values are `JSON.stringify`'d (303-305). `ToolRunPayload.inputs[]` type: ui/src/lib/types.ts:256-263 (`value?: unknown`).
  So **if the C# side formats the value string with units, the UI needs no change**; extra structured fields need a small type + print change.

---------------------------------------------------------------------------------------------------------------------

## 3. SolidWorks PropertyManager API facts and limits (verified against the SW2026 interop)

- `swNumberboxUnitType_e` has exactly: `UnitlessInteger=1, UnitlessDouble=2, Length=3, Angle=4, Density=5, Stress=6, Force=7,
  Gravity=8, Time=9, Frequency=10, Percent=11`. **There is no pressure, temperature, mass or viscosity unit type.** `Stress` (Pa) and `Force` (N)
  exist, but SolidWorks document units have no pressure/stress setting (the document unit preferences that exist are
  `swUnitsLinear(47)`, `swUnitsAngular(51)`, `swUnitsMassPropLength(258)`, `swUnitsMassPropMass(259)`, `swUnitsMassPropVolume(260)`,
  `swUnitsForce(262)`, `swUnitSystem(263)`, `swUnitsEnergyUnits(333)`, `swUnitsPowerUnits(335)`, `swUnitsTimeUnits(337)`), so what a
  Stress/Force/Percent box *displays* and whether `.Value` for Percent is 0-1 or 0-100 is **(spike)**.
- `IPropertyManagerPageNumberbox` members: `SetRange(...)`, `SetRange2(int units, double min, double max, bool inclusive, double inc, double fastInc, double slowInc)`,
  `Value` (double), `Text` (string), `DisplayedUnit` (int), `Style` (int), `AddItems(object)`, `Clear()`, `InsertItem`, `DeleteItem`,
  `CurrentSelection`, `ItemText[short]`, `Height`, `SetSliderParameters`.
  - `DisplayedUnit` takes `swLengthUnit_e` / `swAngleUnit_e` values - documented for Length/Angle boxes only, so it can't make a unitless box show "psi".
  - `Style` flags (`swPropMgrPageNumberBoxStyle_e`): `ComboEditBox=1` (number box with a drop-down list of values - set with `AddItems`),
    `EditBoxReadOnly=2`, `AvoidSelectionText=4`, `NoScrollArrows=8`, `Slider=16`, `Thumbwheel=32`, `SuppressNotifyWhileTracking=64`.
  - A unitless box can't be empty: there is no "blank / no limit" state - needs a checkbox or a sentinel.
- `IPropertyManagerPageCombobox`: `AddItems(object string[])`, `Clear()`, `InsertItem(short, string)`, `DeleteItem(short)`,
  `CurrentSelection` (short, -1 = none), `ItemText[short]`, `Height`, `Style` (`Sorted=1, EditableText=2, EditBoxReadOnly=4, AvoidSelectionText=8`), `EditText`.
  **No ItemCount property** on the combobox (the Listbox has one) - keep our own item list per field.
- `IPropertyManagerPageControl` (cast any control): `Visible`, `Enabled`, `Tip`, `Left`, `Top`, `Width`, `TextColor`, `BackgroundColor`,
  `OptionsForResize`, `SetStandardPictureLabel`, `SetPictureLabelByName`, `ShowBubbleTooltip(title, message, bitmap)`, `GetGroupBox()`.
  Two controls on one row via `Top`/`Left`/`Width` is **(spike)** (units of these properties - dialog units vs percent - need checking).
- `IPropertyManagerPageLabel.Caption` is settable after creation (relabel with units). Also `Bold/Italic/CharacterColor[start,end]`.
- `IPropertyManagerPageGroup`: `Visible`, `Expanded`, `Checked`, `Caption`; group option `swGroupBoxOptions_Checkbox=1 | Checked=2` makes a
  checkable group (event `OnGroupCheck(id, bool)`).
- `IPropertyManagerPage2`: `AddGroupBox`, `AddTab(id, caption, iconPath, options)`, `SetMessage3`, `EnableButton`, `SetFocus(id)`, `GetFocus()`,
  `Show2`, `Close(bool okay)`, `AddMenuPopupItem`, `Pinned`, `Title`.
- Control types available (`swPropertyManagerPageControlType_e`): Label 1, Checkbox 2, Button 3, Option 4, Textbox 5, Listbox 6, Combobox 7,
  Numberbox 8, Selectionbox 9, ActiveX 10, BitmapButton 11, CheckableBitmapButton 12, Slider 13, Bitmap 14, WindowFromHandle 15.
  WindowFromHandle (host a WinForms control, e.g. a property grid of the material table) is the escape hatch if fields multiply.
- **All controls must be created before `Show2`** - nothing can be added afterwards. Dynamic pages = create everything up front and toggle
  `IPropertyManagerPageControl.Visible` / `Enabled` (and the paired Label's Visible). Hidden controls collapse their space.
- Programmatic `Value` / `CurrentSelection` / `Checked` changes: whether they raise `OnNumberboxChanged` / `OnComboboxSelectionChanged`
  is **(spike)** - guard with a re-entrancy counter regardless.
- Keeping the page open on bad input: SolidWorks lets `OnClose` veto an OK close by throwing a `COMException` (xCAD's
  `PageClosingArg.Cancel` relies on this) **(spike on SW2026)**; the simpler path that already works is disabling OK via `EnableButton`
  plus `ShowBubbleTooltip` on the offending control.
- The PropertyManager is narrow (~ 300 px on 100 % DPI). Long dropdown items and labels get clipped - keep captions short; put
  the long explanation in `Tip`.

---------------------------------------------------------------------------------------------------------------------

## 4. Extensions for the mold tool - code-level plan

Guiding rule: keep `inputs.json` the single declaration; all new behaviour is opt-in through new optional properties, so existing user tools
and Smart Rebuild are untouched. Changes are confined to `ToolSpec.cs` (spec + ToolParam docs), `ToolPage.cs` (render/events/collect),
`ToolRunner.BuildPayloadAsync` (format), `ToolInputs.cs` (a couple of accessors), one shared `Units` helper (new file
`src/SwAgent/Tools/Units.cs`), and the TryFromSelection default path.

### 4.0 Prerequisite refactors in ToolPage
1. `Field` (67-79) gets: `IPropertyManagerPageLabel Label` (store the result of `AddLabel`, which today discards it - 207-211),
   `List<string> Items` (current combobox entries), `IPropertyManagerPageCheckbox Enable` (for optional numbers),
   `IPropertyManagerPageNumberbox[] Boxes` (size3), `UnitDef Unit` (see 4.3), `Dictionary<string,double> PresetValues`.
2. `AddLabel` returns the label: `private IPropertyManagerPageLabel AddLabel(...) => (IPropertyManagerPageLabel)Control(...)`.
3. A `Dictionary<string, Field> _byInputId` (Spec.Id -> Field) so handlers can find dependants.
4. Group IDs from `_nextId++` (fixes the collision in 2.1).
5. `private int _quiet;` - re-entrancy guard: `void Quiet(Action a) { _quiet++; try { a(); } finally { _quiet--; } }`; every
   handler starts with `if (_quiet > 0) return;`.
6. `ToolSpec.Dir` (set in `ToolSpec.Load`, ToolSpec.cs:102-116).
7. `Collect` "choice" (413-420) reads `f.Items[i]` instead of `f.Spec.Choices[i]`.

### 4.1 (a) Cascading choices (family -> subtype)

**Spec** (new `ToolInputSpec` properties):
```jsonc
{ "Id": "part_family",  "Type": "choice", "Label": "Part material", "ChoicesFrom": "part-materials.json", "Default": "Polyurethane (cast)" },
{ "Id": "part_grade",   "Type": "choice", "Label": "Grade",         "ChoicesFrom": "part-materials.json", "DependsOn": "part_family",
  "Fills": true }
```
- `ChoicesFrom` (string): JSON file in the tool folder. Without `DependsOn`, the entries are the names of the file's top-level list
  (see 4.2 format); with `DependsOn: "<inputId>"`, the entries are the children of the item currently chosen in that input.
- Static `Choices` still works; `ChoicesBy` (`Dictionary<string,string[]>`, parent value -> entries) is the inline alternative for small trees.

**Build** (`case "choice"`, 267-278): after `AddItems`, `f.Items = entries.ToList()`. For a dependant, entries are computed from the parent
field's *default* (parents are declared first; enforce "DependsOn must refer to an earlier input" in `LoadInputs` with a warning).

**Event** - replace `OnComboboxSelectionChanged` (587-595):
```csharp
public void OnComboboxSelectionChanged(int id, int item)
{
    if (_quiet > 0) return;
    if (id == _tierBoxId) { ...as today...; return; }
    if (id == _unitsBoxId) { SwitchUnitSystem(item); return; }   // 4.3
    if (_fields.TryGetValue(id, out var f)) { Cascade(f); ApplyPreset(f); ApplyVisibility(); }
    Changed();
}

private void Cascade(Field parent)
{
    foreach (var child in _ordered.Where(c => string.Equals(c.Spec.DependsOn, parent.Spec.Id, StringComparison.OrdinalIgnoreCase)))
    {
        var box = (IPropertyManagerPageCombobox)child.Control;
        var keep = SelectedText(child);                       // keep the same grade if it exists under the new family
        var entries = _data.ChildrenOf(child.Spec.ChoicesFrom, Path(child)); // names under the parent's chosen node
        Quiet(() =>
        {
            box.Clear();
            if (entries.Count > 0) box.AddItems(entries.ToArray());
            child.Items = entries;
            var i = entries.IndexOf(keep);
            box.CurrentSelection = (short)(i >= 0 ? i : DefaultIndex(child, entries));
        });
        ((IPropertyManagerPageControl)box).Enabled = entries.Count > 0;
        Cascade(child);       // grandchildren (family -> grade -> hardness...)
        ApplyPreset(child);   // the new grade's data fills its fields
    }
}
```
SolidWorks calls used: `IPropertyManagerPageCombobox.Clear()`, `.AddItems(object)`, `.CurrentSelection` (short),
`IPropertyManagerPageControl.Enabled`.

**Collect / payload:** value = `f.Items[CurrentSelection]` (string). Payload also carries the chain (`"Polyurethane (cast) / 80A MDI-polyether"`)
- simply each choice's own line, no change needed.

**TryFromSelection** (711-714): for `ChoicesFrom`, resolve defaults the same way (first entry when `Default` isn't in the list).

### 4.2 (b) Presets / lookup tables that fill editable fields

**Data file** in the tool folder, e.g. `tools/builtin/compression-mold/part-materials.json`. Values are in the inputs' *spec units*
(the same canonical units `Default/Min/Max` use - section 4.3), so no per-file units table is needed; each value key is an input `Id`.
Keys starting with `_` are metadata passed to the agent, never filled.
```jsonc
{
  "_about": "Process data per part material. Values in spec units: percent, MPa, degC, cP, min.",
  "items": [
    { "name": "Polyurethane (cast)",
      "items": [
        { "name": "80A MDI-polyether", "shrink_pct": 1.5, "viscosity_cp": 1500, "mold_temp_c": 100, "cure_min": 30,
          "_source": "supplier TDS (fill in)", "_notes": "..." },
        { "name": "95A TDI-polyester", ... } ] },
    { "name": "Epoxy / resin", "items": [ ... ] },
    { "name": "Rubber (compression)", "items": [ { "name": "EPDM 70A", ... }, { "name": "Silicone (HCR) 50A", ... } ] }
  ]
}
```
(The numbers above are placeholders; the material table itself is a separate research task and must cite sources.)

**Spec:** `"Fills": true` on the choice whose selected node holds the values (usually the leaf), or `"Fills": ["shrink_pct","viscosity_cp"]`
to limit it. Filled targets are ordinary `number` inputs (so the user can still edit them).

**Loader:** new small class `ToolData` (in ToolPage.cs or `Tools/ToolData.cs`): `JObject Load(string file)` cached per page from
`Path.Combine(_spec.Dir, file)`; for built-in tools also look for a user override at `Path.Combine(UserTools.Root, "<tool>-data", file)`
first (built-in folders are read-only and replaced by every install - UserTools.cs:52-58 - so a shop's own material list must live in
the user's folder). `Node(file, path)` walks `items[name==...]`.

**ApplyPreset(Field f):**
```csharp
private void ApplyPreset(Field f)
{
    if (f.Spec.Fills == null) return;
    var node = _data.Node(f.Spec.ChoicesFrom, Path(f));          // the chosen grade's JObject
    if (node == null) return;
    f.PresetValues = new Dictionary<string,double>();
    foreach (var p in node.Properties().Where(p => !p.Name.StartsWith("_") && p.Value.Type is JTokenType.Float or JTokenType.Integer))
    {
        if (!_byInputId.TryGetValue(p.Name, out var target) || !Allowed(f.Spec.Fills, p.Name)) continue;
        var spec = (double)p.Value;                                    // spec units (MPa, degC, %...)
        target.PresetValue = spec;                                    // remembered for "edited from preset" in the payload
        Quiet(() => SetSpecValue(target, spec));                       // converts to display unit, sets .Value
    }
    f.PresetMeta = node;                                               // _source/_notes go to the agent
}
```
SolidWorks call: `IPropertyManagerPageNumberbox.Value = display`. Re-picking a grade overwrites user edits (it is an explicit action);
on OK, `Collect` compares each filled field with its `PresetValue` and the payload says
`"shrink_pct": "1.8 % (edited; preset for 80A MDI-polyether is 1.5 %)"` so the agent knows the user overrode the table.

**Payload:** `BuildPayloadAsync` adds for the `Fills` choice `entry["preset"] = node` (the whole JSON row incl. `_source`); in
`stageToolPrompt` (store.ts:303-305) print it as one extra indented line (`  data: {...}`) - a 3-line UI change plus
`preset?: Record<string, unknown>` in `ToolRunPayload.inputs[]` (types.ts:256-263).

**Mold material** can use the same mechanism with no fills (or fills of hidden derived data): `"ChoicesFrom": "mold-materials.json"` with
rows like `{ "name": "Aluminium 7075-T6 + tool-steel inserts", "_density_kg_m3": 2810, "_yield_mpa": 503, ... }`; the `_` fields reach the
agent and the structural-calc macro (the macro can also read the file directly via `Inputs`... better: give `ToolInputs` a
`JObject Preset(string id)` accessor so macro.cs gets the row without re-parsing).

### 4.3 (c) Units: pressure, temperature, mass, force/tonnage, viscosity, count, percent

Because SolidWorks has no number-box unit types for pressure, temperature, mass or viscosity (section 3), these are rendered as
`swNumberBox_UnitlessDouble` boxes **with the unit in the label caption** (`"Press pressure (psi)"`), and ToolPage does the conversion.
Length and angle stay native (SolidWorks already shows document units and parses `1in`).

**New file `src/SwAgent/Tools/Units.cs`:**
```csharp
internal sealed class UnitDef { public string Quantity, Symbol; public double ToSi, Offset; public double Inc, Fast, Slow; }
// display -> SI:  si = v * ToSi + Offset ;  SI -> display: v = (si - Offset) / ToSi
internal static class Units
{
    // spec unit = what Default/Min/Max and preset files use; SI = what ToolInputs.Number returns
    // quantity    spec unit   SI returned to macros   metric display      imperial display
    // length      mm          m                        (native SW box)     (native SW box)
    // angle       deg         rad                      (native)            (native)
    // percent     %           % (unchanged, 0-100)     %                   %
    // pressure    MPa         Pa                       MPa  (alt: bar)     psi
    // temperature degC        degC  (deliberate: offset scale; K would invite mistakes)  degC  degF
    // mass        kg          kg                       kg                  lb
    // force       kN          N                        kN                  lbf
    // tonnage     tonne-force N                        t (tonne-force)     US ton (tonf)
    // viscosity   cP (mPa*s)  Pa*s                     cP                  cP
    // count       -           -                        (integer box)       (integer box)
    // time        s           s                        s / min             s / min
}
```
Exact factors (to SI): psi 6894.757293168 Pa; bar 1e5 Pa; MPa 1e6 Pa; lb 0.45359237 kg (exact, definition);
lbf 4.4482216152605 N; US short ton-force 2000 lbf = 8896.443230521 N; tonne-force 9806.65 N; kgf 9.80665 N;
cP = 1 mPa*s = 0.001 Pa*s; poise 0.1 Pa*s; degF = degC * 1.8 + 32 (Offset handling: Kelvin not used).
Sensible increments (display units): MPa 0.1/1/0.01; bar 1/10/0.1; psi 10/100/1; degC 1/10/0.5; degF 1/10/1; kg 0.5/5/0.1; lb 1/10/0.1;
kN 1/10/0.1; tonf/t 1/10/0.5; cP 100/1000/10; % 0.1/1/0.01.

**Which system to show (document units):**
```csharp
private static bool IsImperial(IModelDoc2 m)
{
    var ext = m.Extension; var none = (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified;
    var sys = (swUnitSystem_e)ext.GetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitSystem, none);
    if (sys == swUnitSystem_e.swUnitSystem_IPS) return true;
    if (sys != swUnitSystem_e.swUnitSystem_Custom) return false;           // MMGS, MKS, CGS
    var len = (swLengthUnit_e)ext.GetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitsLinear, none);
    var mass = ext.GetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitsMassPropMass, none);
    return len == swLengthUnit_e.swINCHES || len == swLengthUnit_e.swFEET || len == swLengthUnit_e.swFEETINCHES
        || len == swLengthUnit_e.swMIL || len == swLengthUnit_e.swUIN || mass == (int)swUnitsMassPropMass_e.swUnitsMassPropMass_Pounds;
}
```
(The same `Extension.GetUserPreferenceInteger(..., swDetailingNoOptionSpecified)` pattern is already used at
src/SwAgent/ModelFiles/SwModelLoader.cs:135.) Mass could follow `swUnitsMassPropMass` exactly (mg/g/kg/lb) and force `swUnitsForce`
(`swUnitsForce_e`: Dynes 1, mN 2, N 3, kN 4, MN 5, "Poundfeet" 6 = lbf, KgForce 7, OunceForce 8) - but for a mold shop a 200 000 lbf
reading is worse than "100 US tons", so use the system rule for tonnage and the document mass unit for weights (pounds vs kg; mg/g -> kg).

**Page-level override:** when the page has any converted-unit field, add one combobox "Units" at the top of the first group
(`Document (IPS)` / `Metric (MPa, degC, kg, t)` / `Imperial (psi, degF, lb, US ton)`); remember the choice per tool with
`UserTools.SetValue(name, "units", ...)` (already used for intelligence, ToolRunner.cs:87; for built-ins it goes to builtin-settings.json).
`SwitchUnitSystem(item)`:
```csharp
foreach (var f in _ordered.Where(x => x.Unit != null))
{
    var si = Si(f);                                   // read current display value -> SI with the OLD UnitDef
    f.Unit = Units.For(f.Spec.Unit, system);          // new UnitDef
    var box = (IPropertyManagerPageNumberbox)f.Control;
    Quiet(() =>
    {
        box.SetRange2((int)swNumberboxUnitType_e.swNumberBox_UnitlessDouble, Disp(f, SpecMinSi), Disp(f, SpecMaxSi), true, f.Unit.Inc, f.Unit.Fast, f.Unit.Slow);
        box.Value = f.Unit.FromSi(si);
    });
    f.Label.Caption = $"{f.Spec.Label} ({f.Unit.Symbol})";      // IPropertyManagerPageLabel.Caption
}
```
(`SetRange2` after `Show2` is allowed in practice for re-ranging, **(spike)** to confirm on SW2026.) Optional per-field fixed unit:
`"Display": "bar"` overrides the system choice for that field (e.g. a shop whose press gauge reads bar).

**Spec `Unit` values after the change:** `length | angle | percent | pressure | temperature | mass | force | tonnage | viscosity | time | none`.
`count` = existing `"Type": "integer"` (UnitlessInteger, `Min/Max` honoured - set Min 1 for cavities). Note `percent` today silently
renders as unitless with no "%" anywhere (ToolPage.cs:250-251); with this change it gets "(%)" in its label and keeps returning 0-100.

**Build** (`case "number"`, 236-257): `f.Unit = Units.For(s.Unit, system)`; length/angle branches unchanged; converted units:
`SetRange2(swNumberBox_UnitlessDouble, Disp(Min), Disp(Max), true, inc, fast, slow)`, `Value = Disp(Default)`, label caption with symbol.
`DefaultNumber` (756-762) gets the generic path: spec unit -> SI -> display.

**Collect** (406-409): store SI (`inputs.Set(id, f.Unit.ToSi(box.Value))`) so `ToolInputs.Number()` keeps meaning "SI" for every unit
(update the ToolInputs.cs:11-12 comment: Pa, degC, kg, N, Pa*s, %). Also stash the display value + symbol in a side table
(`inputs.SetDisplay(id, "150 psi")`, new internal method) for the payload.

**Payload** (ToolRunner.cs:232-240): replace the length/angle/raw ternary with `Units.Describe(spec, si, display)`:
`"150 psi (1.034 MPa)"`, `"350 degF (176.7 degC)"`, `"2.0 % "`, `"1500 cP (1.5 Pa*s)"`, `"85 lb (38.6 kg)"`, `"100 US ton (889.6 kN)"`.
The agent always sees the SI/metric figure next to what the user typed. Also handle `case "integer"` explicitly (today it falls to
`default`, giving `3.0`-style values from a double).

### 4.4 (d) Other things the mold page needs

1. **Optional number with "no limit"** - spec `"Optional": true` (+ optional `"Enabled": false` default). Render a checkbox whose caption
   is the label (e.g. "Limit weight per plate") and the number box under it (no separate label); `OnCheckboxCheck` sets
   `((IPropertyManagerPageControl)f.Control).Enabled = check`. Collect: unchecked -> `inputs.Set(id, null)` (so `Inputs.Has(id)` is
   true but `Number()` returns the fallback - add `double? Limit(string id)` to ToolInputs); payload `"no limit"`. A sentinel 0 is the
   fallback design if a checkbox per field makes the page too long. (A checkable group, `swGroupBoxOptions_Checkbox`, is an alternative
   when several limits share one switch - `OnGroupCheck(id, bool)`.)
2. **3-value bounding box** - new type `"size3"`: one label (`"Max plate size (L x W x H)"`), three `swNumberBox_Length` boxes with picture
   labels `swBitmapLabel_Width` / `swBitmapLabel_Depth` / `swBitmapLabel_Thickness1` (already mapped in `PictureFor`); `Default` "600, 400, 150"
   (mm, comma-separated), `Min/Max` per component; works with `Optional`. Value = `double[3]` in m (new `ToolInputs.Size3(id)`), payload
   `"600 x 400 x 150 mm (sorted: 600 >= 400 >= 150)"` - tell the agent to compare **sorted** dimensions since a plate may be rotated on the
   machine. Side-by-side layout via `Top/Left/Width` is a later polish (spike).
3. **Required for numbers/choices** - extend `RequiredSatisfied` (439-452): required number -> value > Min (or > 0) and, if Optional, its
   checkbox checked; required choice -> `CurrentSelection >= 0` and `Items.Count > 0`. Show why via
   `((IPropertyManagerPageControl)ctl).ShowBubbleTooltip(label, "Pick a grade", "")` when OK is disabled.
4. **Conditional visibility** - spec `"ShowWhen": { "mold_material": ["Aluminium + steel inserts"] }`. `ApplyVisibility()` after any
   choice/options/checkbox change sets `IPropertyManagerPageControl.Visible` on the field's control(s) and its Label; hidden fields are
   omitted from the payload. Use for: insert material / insert features only for the aluminium+inserts option; tonnage vs pressure
   when the user enters press capacity instead of pressure.
5. **Read-only "derived" line** - spec `"Type": "info"` (a Label whose Caption is recomputed on `Changed()`), e.g.
   "Clamp force needed approx. 46 US ton (pressure x projected area)" once a molded part is known. Keep tiny; the real calc stays in the macro/agent.
6. **Accept "drawing"** - add `.slddrw;.dxf;.dwg` (and keep pdf/images) to `FileFilter` (820-829) / `AcceptText` (813-818) for the
   engineering drawing input; `FileAttachments.EntryAsync` must not try to open a .slddrw as a 3D model (`ModelFileContext.IsModelFile`).
7. **Remember last values** - shops reuse their press settings. Save the page's values (spec units) on OK to
   `%APPDATA%\SwAgent\tools\<tool>-last.json` (or builtin-settings.json key `lastInputs`) and use them as defaults next time
   (spec flag `"Remember": true` per input, default false so existing tools are unchanged).
8. **Confirm** - the mold tool's TOOL.md must have `confirm: always` (see 2.7), or the page is skipped and defaults are used.
9. **Hooks for the later simulators** - nothing needed on the page yet. Keep the payload structured (SI numbers + preset rows) so a future
   MCP machinability / demolding check can consume the same inputs; optionally a hidden-until-available `options` input
   "Checks after design" whose `ShowWhen` is driven by a capability flag (not by a page input) - add when the MCPs exist.

### 4.5 Proposed inputs.json for the mold tool (order = importance)
```jsonc
[
  { "Id": "mold_material", "Type": "choice", "Label": "Mold material", "ChoicesFrom": "mold-materials.json",
    "Default": "Aluminium 7075-T6 + steel inserts", "Group": "Mold" },
  { "Id": "part_family", "Type": "choice", "Label": "Part material", "ChoicesFrom": "part-materials.json", "Group": "Part material" },
  { "Id": "part_grade",  "Type": "choice", "Label": "Grade", "ChoicesFrom": "part-materials.json", "DependsOn": "part_family", "Fills": true, "Group": "Part material" },
  { "Id": "shrink_pct",   "Type": "number", "Unit": "percent",    "Label": "Shrinkage",     "Min": 0, "Max": 10,  "Default": "1.5", "Group": "Part material" },
  { "Id": "viscosity_cp", "Type": "number", "Unit": "viscosity",  "Label": "Mixed viscosity","Min": 1, "Max": 1e6, "Group": "Part material" },
  { "Id": "press_pressure", "Type": "number", "Unit": "pressure", "Label": "Press pressure", "Min": 0.1, "Max": 200, "Default": "7", "Group": "Press" },
  { "Id": "press_temp",     "Type": "number", "Unit": "temperature", "Label": "Press temperature", "Min": 0, "Max": 300, "Default": "120", "Group": "Press" },
  { "Id": "cavities", "Type": "integer", "Label": "Number of cavities", "Min": 1, "Max": 64, "Default": "1", "Group": "Mold" },
  { "Id": "max_plate_mass", "Type": "number", "Unit": "mass", "Label": "Max weight per plate", "Optional": true, "Default": "25", "Group": "Limits" },
  { "Id": "max_plate_size", "Type": "size3", "Label": "Max plate size", "Optional": true, "Default": "600,400,150", "Group": "Limits" },
  { "Id": "max_base_mass",  "Type": "number", "Unit": "mass", "Label": "Max total base weight", "Optional": true, "Group": "Limits" },
  { "Id": "max_lid_mass",   "Type": "number", "Unit": "mass", "Label": "Max lid weight", "Optional": true, "Default": "23", "Group": "Limits" },
  { "Id": "cycle_life", "Type": "options", "Label": "Cycle life", "Choices": ["Prototype (<500)", "Short run (~5 000)", "Production (~50 000)", "High volume (250 000+)"], "Group": "Mold" },
  { "Id": "part_file", "Type": "files", "Label": "Molded part", "Accept": ["model"], "Multiple": false, "Group": "Part" },
  { "Id": "drawing",   "Type": "files", "Label": "Engineering drawing", "Accept": ["pdf","image","drawing"], "Group": "Part" },
  { "Id": "no_parting", "Type": "selection", "Label": "Critical faces (no parting line)", "Filter": ["face"], "Required": false, "Group": "Part" },
  { "Id": "notes", "Type": "text", "Label": "Notes", "Group": "Part" }
]
```
(Defaults are placeholders for illustration. 23 kg / 25 kg are the typical one-person lifting limits used in EU/UK manual-handling guidance
and the NIOSH lifting equation's 23 kg load constant - worth citing in TOOL.md if used as defaults. That is 7 groups + agent = 8 group IDs, close
to the 10-ID collision in 2.1.)

---------------------------------------------------------------------------------------------------------------------

## 5. Change list by file (minimal)

| File | Change |
|---|---|
| `src/SwAgent/Tools/ToolSpec.cs` | `ToolInputSpec`: `ChoicesFrom`, `DependsOn`, `ChoicesBy`, `Fills` (JToken: bool or string[]), `Optional`, `Enabled`, `ShowWhen` (Dictionary<string,string[]>), `Display`, `Remember`; extend `Unit` and `Type` ToolParam docs (19-21, 32-42). `ToolSpec.Dir`. Validate `DependsOn` ordering in `LoadInputs`. |
| `src/SwAgent/Tools/Units.cs` (new) | `UnitDef`, `Units.For(quantity, system, display)`, `IsImperial(model)`, `Describe(...)`, factor table. Unit-test it in tests/SwAgent.Tests (no SolidWorks needed). |
| `src/SwAgent/Tools/ToolData.cs` (new) | load/cache data JSON (tool dir + user override), `ChildrenOf`, `Node`. Unit-testable. |
| `src/SwAgent/UI/ToolPage.cs` | Field additions, AddLabel returns label, group IDs from `_nextId`, `_quiet` guard, units in Build/Collect/DefaultNumber, `Cascade`, `ApplyPreset`, `ApplyVisibility`, optional checkbox, size3, units combobox + `SwitchUnitSystem`, Required for number/choice, `OnComboboxSelectionChanged`/`OnCheckboxCheck`/`OnOptionCheck` dispatch, `TryFromSelection` for new types, drawing accept filter. |
| `src/SwAgent/Scripting/ToolInputs.cs` | `double? Limit(id)`, `double[] Size3(id)`, `JObject Preset(id)`, internal display-string side table; doc comment on SI units. |
| `src/SwAgent/Tools/ToolRunner.cs` | `BuildPayloadAsync` (217-266): unit-aware value strings, integer case, `preset` row, `"no limit"`, hidden fields omitted, edited-from-preset note. |
| `ui/src/lib/types.ts`, `ui/src/store.ts` | optional `preset` on payload inputs; print it in `stageToolPrompt`. |
| `ui/src/agent/systemPrompt.ts:69` | one sentence on the new input options so the agent can use them in user tools too. |

## 6. Spikes to run in SolidWorks before building
1. Does setting `IPropertyManagerPageNumberbox.Value` / `IPropertyManagerPageCombobox.CurrentSelection` from code raise
   `OnNumberboxChanged` / `OnComboboxSelectionChanged`? (Guard exists either way.)
2. `SetRange2` + `Clear/AddItems` after `Show2` on SW2026 - visible immediately, no flicker/crash?
3. What `swNumberBox_Stress`, `swNumberBox_Force`, `swNumberBox_Percent` display under IPS vs MMGS documents, and the Percent `.Value` scale
   (read `.Text` after setting `.Value`). If Stress/Force follow document units nicely, pressure/tonnage could use native boxes instead.
4. `IPropertyManagerPageControl.Top/Left/Width` units, for side-by-side X/Y/Z boxes or a value + unit dropdown on one row.
5. `OnClose` throwing `COMException` on OK to keep the page open (only if validation-on-OK is wanted beyond disabling OK).
6. Group/control ID collision (2.1) - reproduce with a 10-group inputs.json to confirm it misbehaves, then fix.
