# Page test - checking the PropertyManager page options in SolidWorks

`tools/dev/page-test` is a developer tool whose page uses every page option added for the compression-mold tool:

- cascading data choices (`ChoicesFrom`, `ChoiceKey`, `DependsOn`, `RowFilter`, `ValueMap`), including a choice under an options input;
- `Fills`, including a value stored in another unit (`|psi`) and null values that hide a field;
- converted units: pressure, temperature, mass, force, tonnage, viscosity, time. Each one shows in the document's system;
- per-field `UnitChoices`;
- `Optional`/`Enabled`, the `size` type (2 and 3 boxes) and `info` lines;
- `ShowWhen` driven by a choice and by a checkbox;
- `Remember`, `GroupExpanded`, and a required Optional;
- 12 groups plus the SolidWorks Agent group;
- the original options (length, angle, plain number, choice, checkbox), which must behave exactly as before.

The unit tests (`tests/SwAgent.Tests/ToolDataTests.cs`, `UnitsTests.cs`, `ToolSpecTests.cs`) already check the page logic without SolidWorks. That logic lives in the headless form, `ToolForm` in `src/SwAgent/Tools/ToolData.cs`. This checklist covers what only SolidWorks can show: whether the native controls follow the form.

The data in `data/*.json` is test data in the shape of the mold tool's files. It is not material data.

## Install

1. Build and register the add-in as usual.
2. Copy this folder to `%APPDATA%\SwAgent\tools\page-test`. The folder name must be `page-test`.
3. Restart SolidWorks. "Page test" appears on the SolidWorks Agent tab, in category Developer.
4. To start clean, delete these two files:
   - `%APPDATA%\SwAgent\tools\last-inputs\page-test.json` (remembered values);
   - `%APPDATA%\SwAgent\tools\page-quirks.json` (fallbacks the page chose, see below).

Run the test twice: once in a new part with **MMGS** units, then once in a new part with **IPS** units.

## Spikes S1-S6 (design 2.1.6)

The page code is written to work whatever these spikes show. Each spike tells us which code path your SolidWorks takes. Where a spike fails, a fallback is switched on in `%APPDATA%\SwAgent\tools\page-quirks.json`:

```json
{ "comboRefill": "dynamic", "rangeAfterShow": true, "hidden": "hide" }
```

The page writes the first two values itself when it detects a failure; it shows a message and asks you to reopen the page. The third value can only be set by hand. When you change the file, reopen the page.

| Id | What to do | Pass | If it fails |
|---|---|---|---|
| S1 | Switch **Mold material** between the two options a few times. Change **Grade** in Part material. Watch `%LOCALAPPDATA%\SwAgent\logs\swagent-<date>.log` for "handling a change ... failed". | The grade lists and filled fields follow every change, and nothing flips back on its own. Record whether SolidWorks fires change events for values the code sets: the page works either way, because the `_quiet` guard and the form ignore repeats. Also record whether typing in a number box raises a change event per keystroke or only on Enter / leaving the box: OK reads every box again either way. | Note what flipped back, and send the log. |
| S1b | In MMGS, pick Part material **Rubber - EPDM** (grade EPDM 70A), tick the required limit, press OK and read the payload in chat. Run again, change **Press pressure** by a little (13.79 → 13.85 MPa) and press OK at once, without Enter or Tab. | `Molding pressure` reads `10.3421 MPa` with no "edited by the user": number boxes keep the digits the code writes (an event just after a write may differ by 0.5 %; at OK only the box's display rounding is ignored). The second payload reads 13.85 MPa. | Note both payload lines. |
| S2a | Switch **Mold material**, then **Part material** family. | The **Plate grade** and **Grade** dropdowns change their entries immediately, without flicker or a crash. After each refill the page reads entry 0, the last entry and the one after it to check the refill. | The dropdown greys out, OK waits (a bubble says why), the page saves `"comboRefill": "variants"` and asks you to reopen. After that, the page makes one dropdown per entry list and shows only the active one. Check that this works too. A choice with more than 80 lists (or 2000 parent combinations) keeps one dropdown; if that can't refill either, it greys out and the page says to reopen. |
| S2b | In **Press pressure**, change the unit dropdown under the box: psi, then bar, MPa, kgf/cm². | The box shows the same pressure in the new unit, with no clamping. For example, 2000 psi becomes 137.9 bar, 13.79 MPa and 140.6 kgf/cm². The caption reads "Press pressure (bar)". | The page saves `"rangeAfterShow": false`, puts the unit back, and asks you to reopen. After that, unit boxes get a range wide enough for every unit. The real range is checked before OK: type 200 MPa and OK greys out with a bubble. |
| S3 | Pick **Tool steel**: Insert grade disappears. Pick Rubber - EPDM: Mixed viscosity and Pot life disappear and Mooney appears. Tick **Show the extra note**. Also watch the info lines (Full strength to ..., Liquid pour ...). | Hidden controls collapse their space, shown ones reappear in place, and info captions repaint at once. | If space isn't collapsed: cosmetic, note it. If hiding misbehaves (controls overlap, or the page draws wrongly), set `"hidden": "disable"` by hand. Hidden inputs then stay in place, greyed out. |
| S4 | Look at **Heated platen size** and **Max size per plate**. | The boxes are stacked vertically, with Width, Depth and Thickness pictures. Side by side is a later polish. If you can, record the units of `IPropertyManagerPageControl.Top/Left/Width` (dialog units or percent) for that. | Nothing to change in v1. |
| S5 | (Track D, not this page.) In a part, rename a body with `IBody2.Name = "x"` in an `execute_csharp` cell. Rebuild, save, close and reopen. | The name persists. | The mold tool falls back to folders plus geometric order (its default). |
| S6 | Count the groups: 12 plus "SolidWorks Agent". Click into controls in the last groups and change them. | Every control works. No group steals a control's events. Group ids now come from the same counter as control ids, so they can't collide. | Send the log. |

## The page script (design 6.2, about 15 min)

Tick each item in both an MMGS and an IPS part.

- [ ] **Cascade.** Part material → Grade: the grade list follows the family, and the Process data group (collapsed at start) fills in. Mold material → Plate grade: aluminium shows 6061-T651, 7075-T651 and MIC-6; steel shows P20 and H13.
- [ ] **Edit, re-pick, overwrite.** Change Shrinkage to 1.8 and press OK: the payload reads `1.8 % (table 1.5 % - edited by the user)`. Run again and pick the 70A grade: Shrinkage becomes 1.7 (picking a grade overwrites edits).
- [ ] **Null hides.** Rubber - EPDM hides Mixed viscosity and Pot life; the payload says `n/a for this grade`.
- [ ] **ShowWhen.** Mold material = Tool steel hides Insert grade. Press pressure is = On the part hides Gauge at rated force; Press rated force stays visible (critique C2). The checkbox shows and hides Extra note.
- [ ] **Optional on/off.** Platen size and Daylight start off: their boxes are grey and the payload says `not set`. Tick them: the boxes enable and the values are sent.
- [ ] **Required Optional.** OK stays grey until "Required limit (switch on)" is ticked. A bubble explains why, once.
- [ ] **Sizes.** 2 boxes for the platen, 3 for the plate.
- [ ] **Units.** IPS shows psi, °F, lb, lbf and US ton. MMGS shows MPa, °C, kg, kN and t. Defaults are round in both systems: 2000 psi, 3000 psi, 50 US ton, 212 °F, 50.7 lb.
- [ ] **Remember.** Change Press pressure (and its unit), Press rated force, Cycle life and the grades, then press OK. Run again: those values and units come back. Press temperature and Cavities don't (not remembered). Cancel never stores anything.
- [ ] **Remembered table values follow the data.** Press OK without touching Shrinkage (1.5). Edit `data/materials.json`: set the 90A row's `shrinkPct.typ` to 2.1. Run again: Shrinkage shows 2.1 and the payload reads `2.1 %` (an unedited value follows the table). Now type 1.8, OK, run again: it stays 1.8 and reads `1.8 % (table 2.1 % - edited by the user)`. Put 1.5 back in the file.
- [ ] **Tooltips.** Hover **Plate grade**: the tip ends with the full name of the selected grade ("Selected: 6061-T6 / T651 rolled plate") and follows a new pick.
- [ ] **Payload.** The chat shows the `<tool_inputs>` block (the tool's agent echoes it). Compare it with the expected lines below.
- [ ] **Groups.** 13 groups, no id collision (S6). "3 Process data" and "12 Decisions & notes" start collapsed (`GroupExpanded: false`).
- [ ] **Original options.** Group 11 looks and behaves as before: Legacy length shows document units with the depth picture, Legacy angle shows degrees, Legacy plain number is a plain box, the choice is a dropdown, the checkbox is ticked.
- [ ] **Existing tools unchanged.** Run **Smart Rebuild** and the user tools (flash-grooves, parametric-block, pu-compression-mold). Compare their pages with screenshots from before this change: same controls, captions, defaults and payload.

## Expected payload (defaults, nothing remembered)

MMGS part:

```
- Mold material (mold_material): Aluminium + steel inserts
- Plate grade (mold_grade): 6061-T651 [al_6061_t651]
- Insert grade (insert_grade): P20 pre-hard [st_p20]
- Part material (part_family): Cast PU - TDI polyether
- Grade (part_grade): TDI-polyether / MBOCA 90A [pu-tdi-ether-90a; data confidence medium; sources S19, S20, K-PU]
- Shrinkage (%) (shrink_pct): 1.5 %
- Specific gravity (sg): 1.1
- Mixed viscosity (viscosity_cp): 1200 cP (1.2 Pa*s)
- Mooney viscosity (mooney): n/a for this grade
- Pot life (pot_life_min): 6 min
- Cure / demold time (cure_min): 30 min
- Minimum draft (draft_min_deg): 0.5 deg
- Molding pressure (on the part) (molding_pressure): 0.344738 MPa
- Press pressure (press_pressure): 13.79 MPa
- Press pressure is (press_pressure_is): Not sure - ask me
- Press temperature (press_temp): 100 degC
- Press rated force (press_rated_force): 45.36 t (444.8 kN)
- Gauge at rated force (press_rated_gauge): 20.68 MPa
- Heated platen size (platen_size): not set
- Daylight (daylight): not set
- Max weight per plate (max_plate_mass): 23 kg
- Max size per plate (max_plate_size): 600 x 400 x 150 mm
- Clamp force (clamp_force): 50 kN
- Number of cavities (cavities): 1
- Cycle life (parts per cavity) (cycle_life): 5000
- Show the extra note (show_extra): false
- Faces (optional) (faces): (nothing selected)
- Engineering drawing (drawing): (none)
- Required limit (switch on) (must_set): 10 kg          <- after ticking it, so OK is available
- Legacy length (legacy_len): 12.5 mm (0.0125 m)
- Legacy angle (legacy_angle): 5 deg
- Legacy plain number (legacy_plain): 3.25
- Legacy choice (legacy_choice): Green
- Legacy checkbox (legacy_flag): true
- Big decisions (decisions): Ask on big ones
- Notes (notes): (empty)
```

IPS part: the same lines, except these:

```
- Molding pressure (on the part) (molding_pressure): 50 psi (0.3447 MPa)
- Press pressure (press_pressure): 2000 psi (13.79 MPa)
- Press temperature (press_temp): 212 degF (100 degC)
- Press rated force (press_rated_force): 50 US ton (444.8 kN)
- Gauge at rated force (press_rated_gauge): 3000 psi (20.68 MPa)
- Max weight per plate (max_plate_mass): 50.7 lb (23 kg)
- Max size per plate (max_plate_size): 23.62 x 15.75 x 5.906 in (600 x 400 x 150 mm)
- Clamp force (clamp_force): 11240 lbf (50 kN)
- Required limit (switch on) (must_set): 22.05 lb (10 kg)
```

The info lines stay on the page; they are not in the payload:
- MMGS: "Full strength to 120 °C, derated to 150 °C" and "Liquid pour (hot cast) - data confidence medium; molding 0.3447 MPa".
- IPS: "Full strength to 248 °F, derated to 302 °F" and "... molding 50 psi".

Extra note is left out of the payload while hidden.

The macro's output (in the chat after the block) lists every input as `Inputs` sees it. Check these:
- numbers are SI: `press_pressure` number=13790000 (Pa); `press_temp` 100 (degC); `pot_life_min` 360 (s); `viscosity_cp` 1.2 (Pa*s);
- `daylight` limit=null while off;
- `max_plate_size` size=[0.6, 0.4, 0.15] m;
- `part_grade` row.id=pu-tdi-ether-90a;
- `extra_note` hidden=True.

## Where things are

| File | Purpose |
|---|---|
| `src/SwAgent/Tools/Units.cs` | Units: factors, display and spec roles, payload text, IPS detection |
| `src/SwAgent/Tools/ToolData.cs` | `ToolData` (data files and shop overrides), `ToolForm` (the page's state: cascade, fills, visibility, units, remember, required checks, collect), `RememberStore` |
| `src/SwAgent/UI/ToolPage.cs` | The native controls that mirror the form; `PageQuirks` (the fallbacks above) |
| `src/SwAgent/Tools/ToolRunner.cs` | `requires` check; payload: value text plus `si`, `table`, `row`, `display`, `maxSteps`, `longRun` |
| `src/SwAgent/Scripting/ToolInputs.cs` | `Limit`, `Size`, `Row`, `Display`, `Table`, `Edited`, `Hidden` for macros |
