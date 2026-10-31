# design-compression-mold: data files

This folder holds the tables, templates and schema of the built-in tool "Design compression mold for plastics and resins". The whole tool folder is copied next to the add-in on every build and install (`builtin-tools\design-compression-mold\`), so **don't edit these files in place on a user's PC: they are replaced by the next install.** Shop changes go in an override folder (below).

## What each file is

| File | What it holds | Read by |
|---|---|---|
| `materials.json` | Part materials: 96 subtypes in 38 families (`rows`), with process data: shrink and its basis, SG, viscosity, Mooney, pot life, mold temperature, demold time, post-cure, molding pressure, draft, flash/land/vent/overflow values, charge excess, hazards, confidence and sources. | The page (family and grade lists, the fields they fill), `mold_calc` |
| `mold-materials.json` | Mold grades (aluminium and steels) with strength at temperature (10,000 h basis), fatigue, CTE, density, service limits, `short` page labels and `use` (plate, insert, backing, stop); fasteners (inch and metric), thread inserts, dowels, stock thicknesses. | The page (grade lists), `mold_calc` |
| `shop-standards.json` | The shop's own rules: flash grooves (lip, moat, runners), plate rounding, blank allowance, dowels, pry slots, chamfers, thermocouple wells, handles, pour rates, custom properties. | `mold_calc`, the agent |
| `sizing-defaults.json` | Every judgement factor of the structural method in one place: safety factors, flash-gap classes and the gap budget, bearing divisors, bolt preload, tightening scatter and hot proof factors, minimum thicknesses, lifting defaults. | `mold_calc` |
| `validation/` | Worked examples (`examples.json`, written by `tools/dev/mold-data/examples_check.py`, read by the unit tests) and the results of the validation runs (`tools/dev/MoldSweep`): `sizing-table.csv` (the design table), `montecarlo-summary.json` (the calculator's own `size`, through `MoldCalcSizing`, against finer plate models over 2,000 random molds; the stub's table-only and design-as-written sizings of the same molds for comparison), `solid3d-summary.json` (the 3D checks behind the wall-root factor 1 + 2.8 w/h and the pillar-head factor 1.2). | Tests, the agent (quotes them in reports) |
| `mold.schema.json` | JSON Schema (draft 2020-12) of the design record `mold.json`, schema id `unify3d.mold/1`. | Tests, simulators |
| `setup-sheet.html` | Template of the operator's setup sheet. It reads the record plus `sheet`, the operator numbers `SetupSheet.Build` derives (pieces, press setting, partial-cavity table, bolts, lid lifting, preheat, maintenance). | `mold_record setup_sheet` |
| `machining-sheet.html` | Template of the machine shop's sheet. It reads the record plus `machining`, which `MachiningSheet.Build` derives: per machined body the stock and blank, holes with fits and limits, flatness, finish and notes; general rules; the purchased-hardware list (also written as `hardware-bom.csv`); the screw table. | `mold_record machining_sheet` |
| `examples/mold.sample.json` | A complete example record: the 2-cavity PU pour mold of worked example E2 as the calculator writes it, plus the keys the agent and `mold_bodies` write (including a PART-REF and a Garbage body). It validates against the schema. Use it as the reference for record shapes. | Tests, the agent |

Units inside the data and the record are SI-like: mm, MPa, degC, kg, kN, cP, min. A key that names another unit says so (`moldingPressurePsi`, `perCavity_g`, `torqueDry_Nm`). The page and the reports show the document's units, with SI in brackets.

## Shop overrides

Put a file with the **same name** in:

```
%APPDATA%\SwAgent\tools\builtin-data\design-compression-mold\
```

That folder survives reinstalls. The page, `mold_calc` and `mold_record save_default` all read it. Merge rules:

- `materials.json` `rows` and `mold-materials.json` `mold_materials`: a row with the same `id` is merged into the shipped row key by key (the page and the calculator use the same rule), so a partial row such as `{ "id": "epdm-s-70a", "shrinkPct": { "typ": 2.1 } }` keeps the shipped name, family and other values; `mold_record save_default` writes the whole row anyway. A new `id` is added at the end.
- Other objects are merged key by key. An override only needs the keys it changes, for example `sizing-defaults.json` with `{ "sfStatic": 2.5 }`, or `shop-standards.json` with `{ "flash": { "grooveWidth": { "in": 0.375 } } }`.
- Everything else comes from the shipped file.
- Lengths in `shop-standards.json` are `{"in": x}` or `{"mm": x}`.

The easy way: ask the agent, for example "save 2.1 % as my EPDM 70A shrink" or "our dowels are 3/8 in". It writes the override with `mold_record save_default` after checking the keys against the shipped table.

## Template syntax (setup and machining sheets)

The templates are plain HTML with placeholders, filled by `SwAgent.Mold.Template` without any library. The context is the whole record plus one derived object: `sheet` for the setup sheet, `machining` for the machining sheet.

| Placeholder | Meaning |
|---|---|
| `{{a.b.c}}` | The value at that dotted path, HTML-escaped. Index an array with brackets: `{{shrink.scale[0]}}`. |
| `{{this}}` | Inside a block: the current item itself (for lists of strings). |
| `{{this.a}}` | Inside a block: a key of the current item. Use it for every item key, so a key the item lacks is reported instead of being taken from an enclosing item or the root. |
| `{{@index}}`, `{{@number}}` | Inside a block: the item's position from 0, or from 1. |
| `{{#each a.b}} ... {{/each}}` | Repeats its content for every element of the array at that path. Blocks nest (`{{#each this.holes}}` inside `{{#each machining.bodies}}`). An empty array prints nothing. |
| `{{#if a.b}} ... {{else}} ... {{/if}}` | False for a missing path, null, false, "", 0 and []. For parts that only some jobs have (the class A pour time, a post-cure). |
| `{{path\|unit}}` | A number stored in `unit`, printed in the mold document's unit system first, the other in brackets: `{{sheet.charge.perCavity_g\|g}}` gives "454.3 g (1.001 lb)". |

What prints, and what counts as unresolved:

- A **missing key** prints `-` and is reported as unresolved. `mold_record setup_sheet` / `machining_sheet` list the unresolved paths, and acceptance check A15 wants none.
- A key whose value is **null** is deliberately empty: it prints `-` and is not reported. Write null for "not known" or "does not apply" (`partNumber`, an unset `daylight`, a not-run simulator's `summary`), never a made-up string.
- An `{{#each}}` over a missing key is reported; write `[]` for "none". An `{{#if}}` over a missing key is just false.

Unit filters (the unit the value is **stored** in), and what an imperial document prints first:

| Filter | Prints | Filter | Prints |
|---|---|---|---|
| `mm` | in | `degC` | degF |
| `mm2` | in² | `Nm` | lbf·ft |
| `mm3` | in³ (cm³ first in metric) | `min` | min |
| `MPa` | psi | `pct` | % |
| `kN` | US ton (t in brackets in metric) | `g` | g (lb in brackets) |
| `kg` | lb | `gauge` | a pressure stored in MPa, in the unit the user's gauge reads (`process.gauge.unitShown`: psi, bar, kgf/cm² or MPa) with MPa in brackets; like `MPa` when the unit is not known |

No other filter exists: an unknown one prints `-` and is reported as unresolved (`path|filter (unknown filter)`), never a silent pass-through. Seconds, kelvin, cP and degrees have no filter; write the unit after the placeholder (`{{sheet.pour.secondsTotal}} s`). Numbers print with 4 significant digits (a 0.01 mm flatness prints as 0.0003937 in (0.01 mm)). The record's `units.display` ("imperial" or "metric") picks the system. Pressures print in psi and MPa, except the gauge settings, which use `gauge`.

Keep placeholders out of HTML comments: the filler replaces them there too.

## The design record `mold.json`

- It lives in `<mold file name>.mold\mold.json` next to the mold part. The part's custom property `Unify3D.MoldRecord` names the folder, and `Unify3D.MoldId` holds the record id.
- Consumers ignore unknown keys. Additive changes keep the schema id `unify3d.mold/1`; a breaking change makes it `/2`.
- The arrays `mold_record merge` merges by id (`checks`, `stages`, `bodies`, `hardware.bolts` and the others listed in the schema's description) carry a string `id`.
- The schema's description says which tool writes which key. The sheets read mostly what the calculator and `mold_bodies` write; the agent adds only `identity`, `setup`, `views`, `report`, `part.drawing`, `bodies[].machining.notes` and `hardware.bom` (purchased items the calculator doesn't size).
- Geometry a simulator returns is located by `{bodyId, point, normal, stepFaceId?}` (body ids match `exports\bodies\<bodyId>.step`), never by SolidWorks persistent ids.
- `materials.part.rheology`, `.kinetics`, `.thermal` and `.mechanical` are reserved for simulator data, with `status: "unknown"` until someone fills them.
- A pressure field for the structural check (`structural.loadCases.LC1.pressureField`) is either `(u, v, w, p)` samples in the mold frame or per-face samples `{bodyId, point, normal, p_MPa}`.

## Before release: checks in SolidWorks

Nothing below could be run without SolidWorks. Every call was checked against the SW2026 interop by reflection and every method
measures its own result, but these behaviours still need a person at a SolidWorks seat. Tick them on the release build.

### The PropertyManager page (spikes S1-S6)

The step-by-step script, the expected payload and the S1-S6 table are in `tools/dev/page-test/README.md` (install the page-test
tool, about 15 minutes). In short:

- S1: switching Mold material and Grade a few times; nothing flips back (send the log if it does).
- S2: a dropdown whose entries are refilled; if the refill fails the page marks it stale and switches `comboRefill: variants` on.
- S3: hidden inputs collapse their space; if hiding misbehaves set `"hidden": "disable"` in `%APPDATA%\SwAgent\tools\page-quirks.json`.
- S4: size boxes stack vertically (side by side is a later polish).
- S5: a body renamed with `IBody2.Name` keeps its name after save and reopen (else the mold tool uses folders plus order, its default).
- S6: 13 groups, every control in the last groups works.
- Known deviation from critique C3, for the manual confirm list (design 6.3): `press_temp` stays visible in the Press group and is not
  remembered (it is the platen set point the operator sets every run, filled from the grade).
- Fallbacks in `page-quirks.json`: `comboRefill: variants` (one dropdown per entry list), `rangeAfterShow: false` (wide ranges for
  unit-choice boxes), `hidden: disable` (greyed out instead of hidden). The page sets the first two itself when it detects the failure.

### The geometry tools (`Mold.*`, `mold_part`, `mold_bodies`)

1. `InsertPart3(sldprt, ImportSolids | DontZoomAll, config)` inserts one body; `LoadFile4` + `SaveAs3` of a hidden STEP import;
   `IBody2.Check3` on the result.
2. `CreateCoordinateSystemUsingNumericalValues(true, x, y, z, false, 0, 0, 0)` and `InsertScale(swScaleAboutCoordinateSystem, ...)`
   with the body and the CS selected: which selection marks work (the code tries 0/0, 1/2, 2/1, checks each by measurement, then falls
   back to the origin plus a re-centring move).
3. `InsertMoveCopyBody2`: the rotation sign (the code checks and retries the other sign), and copies with NumCopies > 1 spaced by the
   translation.
4. `PreSplitBody2` / `PostSplitBody2` selection marks (plane 1 / body 2, then 0 / 0); and `PostSplitBody2` returning null while still
   adding the split feature (`SplitByPlane` detects the added feature by name and uses or removes it).
5. `mold_part` on a freshly inserted part, at `sample_mm` 0.3 and 1.0: it finishes and SolidWorks stays up. Its rays are cast in
   managed code on the display tessellation (`MeshRayCaster`); `RayIntersections` crashed SolidWorks here on 2026-10-20 (an access
   violation inside SolidWorks, 2,000-ray batch) and is no longer called anywhere. Check that the undercuts and air traps it draws
   (`show: true`) sit where you expect on a part with a known undercut.
6. Body folders: `InsertSubFolder` with bodies already in another folder moves them; membership survives later cuts.
7. `IBody2.SetMaterialProperty` per body reads back with `GetMaterialPropertyName`.
8. Export: `NewDocument` while `DocumentVisible(false)` gives a hidden part; `CreateFeatureFromBody3` of a `Copy2`; STEP AP 214 / 242.
9. A `.slddrw` dropped on the chat becomes a PDF of every sheet (hidden, read-only drawing, `IExportPdfData.SetSheets(ExportAllSheets)`),
   not blank; the chip shows "reading" meanwhile. `HiddenDocuments.Lock` fails fast ("busy, try again") when a second drawing is
   dropped while a big one converts.
10. `FeatureFillet3` with `swFeatureFilletUniformRadius` only (no Propagate), one per body, on the flash grooves (the user's test plate:
    40 R0.25 edges, hc-64).
11. Plate extrusion direction (`FeatureExtrusion3` start offset; the code retries reversed when the box is wrong).
12. `FeatureCut4` feature scope with `UseAutoSelect = false`: which selection mark the scope body needs (`Holes` tries auto scope, then
    marks 8, 1, 2, 0, each measured and undone if wrong; the mark that worked is remembered for the session), and whether
    `DeleteSelection2(0)` on a failed cut leaves its sketch for the next try.
13. The design snapshot (`DesignSnapshot`, last saved version) opens its copy through `HiddenDocuments.Scope`: check that the user's
    window never flashes and that a snapshot during a drawing conversion waits or says busy.

### The panel and the ribbon

- The SolidWorks Agent tab shows a box per category (Compression Mold in "Molding & Toolmaking", then the user's categorised tools), the
  uncategorised built-ins, then the user's tools and New Tool; button hints start with "[Molding & Toolmaking]".
- A long run (Design compression mold): the banner chip shows the run's model, effort, auto-approval and 600 steps; follow-ups in the
  same chat keep them while the mold document is active; `mold_record` writes end with `record_status: <status>`, and the run ends
  when a write reports `done`. In the browser preview (`npm run dev` in `ui`, mock host) this was checked on the mock `mold_record`.
- `ask_selection` cards: the live count, chips, a typed or spoken note, Skip / Done.
- Attach a `.slddrw` with the paperclip (the picker lists drawings) and by dropping it: it arrives as a PDF.

### Open items (not in v1)

- Hole Wizard holes (spike S11): holes are plain cuts with measured diameters; thread data lives in the record and on the machining sheet.
- Translucent temporary bodies in previews: plate-stack previews use translucent overlay triangles (`Overlay.Box`) instead.
  Tessellating a temporary body (`IBody2.GetTessellation`) into a `tris` item is possible but not verified as safe.
- Air-trap volumes stay at the column spacing on big parts (high points are refined to `sample_mm`).
- Parting-line scoring does not yet score closure dimensions crossing the plane (design 2.3.3 step 5): the record has none to read yet.
- `mold_part` with several bodies analyses the first in full and lists each one's projection; each extra body is a full run (seconds).
- `mold_bodies export` runs in one synchronous loop under the hidden-document gate, so SolidWorks is busy for the whole export.
- Case F (ram or platen smaller than the lid) is a warning only: model the ram contact in a separate FE.
- Pillar plates thinner than t/b 0.1 over rigid heads: the 3D peak above a sharp head corner exceeds the Pillar beta (up to 1.9 x at
  t/b 0.02). Radius or chamfer the head corners, or keep t/b >= 0.1.
- The panel's pure-module checks run with `node src/lib/checks/lib.check.mjs` (from `ui`); moving them to vitest needs the package
  added to `ui/package.json`.
- Workflows (kind workflow) with `long_run` get the step limit, the 1-hour cache and follow-up settings, but keep the chat's model and
  approval (their `model:` front matter is still not applied).
- MoldSweep: the calculator reads the coefficient table embedded at build time, so after `MoldSweep coefficients` rebuild before
  `MoldSweep montecarlo` (or `all`).
