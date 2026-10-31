# Design document: built-in tool "Design compression mold for plastics and resins"

Category: Molding & Toolmaking. Status: ready to build, 2026-10-19. Author: lead engineer (SolidWorks Agent).

This document turns the eight research streams into one build plan. It picks one approach per question. No repository file was changed while writing it.

- Repository root, written `R/` below: `C:/Users/msmit/Desktop/Software Projects/Unify3D Agent`.
- Research notes, written `N/` below: `C:/Users/msmit/AppData/Local/Temp/claude/C--Users-msmit-Documents-Claude-Code-SolidWorks-Agent-Plugin/2b455cdd-1c60-4ed3-9eb5-8eb641a4b994/scratchpad/mold/`. Appendix A lists each file.
- File:line references were checked against the worktree at commit f299941.

Unit conventions:
- Every number carries its unit.
- Inside the calculator and the design record, units are SI-like: mm, MPa, degC, kg, kN, cP, min.
- The page and the reports show the document's own units, with SI in brackets.
- The user thinks in inches, and his template is IPS.

---------------------------------------------------------------------------------------------------------------------

## 0. Decisions at a glance

| # | Question | Decision | Why (source) |
|---|---|---|---|
| D1 | Tool identity | Folder `tools/builtin/design-compression-mold/`, name `design-compression-mold`, title "Design compression mold for plastics and resins", new `ribbon_title: Compression Mold`, `category: Molding & Toolmaking`, `kind: tool`, `agent: true`, `intelligence: max`, `confirm: always`, `context: view, model, history`, new `long_run: true`. | `confirm: always`: without it the page is skipped (ToolPage.cs:666). `model` in context is the only way a tool run learns the active document (N/pipeline.md 1.5). `selection` in context is never read (ToolRunner.cs:180-199). |
| D2 | Where the mold lives | One multi-body part, which is the open part. The molded part comes in as an Insert Part feature (`IPartDoc.InsertPart3`). That keeps the link, so Smart Rebuild's `replace_inserted_part` can swap a revised part later. A shrunk reference copy is kept in the body folder "Mold - Reference". | It is the user's process, and it matches the Smart Rebuild target (N/existing-mold-knowledge.md 4.4). |
| D3 | Body identity | Each role gets a solid-body folder: "Mold - Base", "Mold - Middle", "Mold - Lid", "Mold - Subplate", "Mold - Inserts", "Mold - Stops", "Mold - Reference", plus "Garbage". Inside a folder, bodies are ordered by geometry (sorted by centroid U, then V). Every feature the tool makes starts with `MOLD `. `IBody2.set_Name` is used only if spike S5 shows the name persists. | v1 found that body renames do not persist, and bodies renumber (N/existing-mold-knowledge.md 2.11 item 6). Folders already work in Smart Rebuild phase 4. |
| D4 | Process class | Set from the part material's `processType`. **A, liquid pour:** liquid_cast_hot, liquid_cast_rt, foam_pour. **B, compression:** rubber_compression, compound_compression, liquid_compression (LSR). **C, positive:** thermoplastic_compression, preform_sinter. | Land, vent, pressure and structural rules differ about 10x between the classes (N/design-rules.md section 0). |
| D5 | "Press pressure" | Four page inputs: <ul><li>a value;</li><li>what it means: "Gauge reading", "On the part" or "Not sure - ask me";</li><li>the press rating: rated force (tonnage) at a rated gauge, which gives the ram area.</li></ul> All four are remembered between runs. | The same gauge reading gives 3,142 or 7,069 psi on the part, depending on the ram (N/structural.md 0.2). |
| D6 | Units on the page | Pressure, temperature, mass, force/tonnage, viscosity and time use unitless number boxes, with the unit in the label. Display follows the document's unit system (IPS shows psi, degF, lb, US ton). `ToolInputs.Number` returns SI. Length and angle keep the native SolidWorks boxes. | `swNumberboxUnitType_e` stops at Percent=11, with no pressure, temperature or mass type (N/pm-page.md 3). |
| D7 | Material data on the page | Dropdowns come from JSON tables in the tool folder: family, then grade. The chosen row fills editable fields. The payload shows what the user edited. Shop overrides go in `%APPDATA%\SwAgent\tools\builtin-data\design-compression-mold\`. | Built-in folders are replaced on every install (UserTools.cs:52-58). |
| D8 | Calculator | A compiled C# library `SwAgent.Mold` with no SolidWorks types, plus one agent tool `mold_calc`. The same code can be called from `macro.cs` and from code cells. | ScriptHost already references the add-in assembly (ScriptHost.cs:285). `#load` does not resolve (N/agent-abilities.md G12). |
| D9 | Structural method | <ol><li>Stiffness first: the allowable deflection comes from the flash class and the drawing tolerance.</li><li>Mindlin thick-plate coefficient tables, made and checked by an MITC4 finite-element sweep.</li><li>Long-term (10,000 h) aluminium derating.</li><li>Goodman fatigue at R = 0.</li><li>Shigley bolts, including the thermal preload change.</li><li>Keyed multi-piece bases.</li></ol> | N/structural.md sections 0-12. |
| D10 | Geometry | Two new read-only tools: `mold_part` (draft, undercuts, air traps, projected area, parting-line candidates, fragile features) and `mold_bodies` (per-body report against the limits, interference, clearance, STEP export). Changes to the model stay in `execute_csharp` cells, which use a tested `Mold` helper object. | Keeps undo and approval rules. Closes gaps G1-G9 and G11 (N/agent-abilities.md 6). |
| D11 | State outside the chat | <ul><li>One JSON design record, `mold.json`, in the folder `<mold file name>.mold\` next to the part. It is also the simulator manifest.</li><li>`rebuild_journal` holds lessons.</li><li>`checkpoint` handles stage rollback. The mold file is registered as its own working copy, so restore works.</li></ul> | Old tool results are cleared after 40k input tokens (runAgent.ts:149-156). Restore needs a registered copy (RebuildTools.cs:248). |
| D12 | Long runs | Front matter `long_run: true` gives: <ul><li>600 API steps per turn instead of 200;</li><li>a 1-hour prompt-cache TTL;</li><li>the tool's model, effort and auto-approval stay on for follow-up messages;</li><li>one system-prompt sentence that lets TOOL.md override the "one short line" finish.</li></ul> | runAgent.ts:207 and 345, store.ts:618-621, systemPrompt.ts:51 and 88. The 1-hour cache costs 2x per write and pays off for gaps of 5-60 min. |
| D13 | Picking faces mid-run | A new UI tool `ask_selection` returns `[S#]` refs. The overlay gets a translucent `face` item. | Answers carry no refs today (store.ts:1344-1349). Overlay polygons are flat (N/agent-abilities.md 3). |
| D14 | Flash system | **Class A default:** the user's own flash-groove standard: <ul><li>lip 0.125 in (3.175 mm);</li><li>moat 0.5 x 0.125 in;</li><li>runners at 2 in pitch;</li><li>R0.25 in corners;</li><li>0.25 in steel to every other feature.</li></ul> **Class B default:** land and overflow from the material table. Both live in `data/shop-standards.json`, and the plan confirms them with the user. | The user called the flash-groove rules "exact" (N/existing-mold-knowledge.md 1.3). |
| D15 | Simulators (later) | <ul><li>An MCP client inside the add-in, with role aliases `sim_process` and `sim_machinability`.</li><li>Hooks: H1 after the plan, H2 after the build, H3 a pressure field fed into the structural check.</li><li>Export: STEP AP214 per body, plus `mold.json` with schema `unify3d.mold/1`.</li></ul> | The Messages-API MCP connector takes only remote `url` servers (beta `mcp-client-2025-11-20`) and can't read local STEP paths. |
| D16 | Categories | <ul><li>One ribbon tab box per category.</li><li>One Workflows-menu section per category.</li><li>The category appears in the agent's tool index.</li></ul> A missing category behaves as today. | N/pipeline.md 5. |
| D17 | Bolt data | Inch screws use ASTM A574 proof stress from the mold-materials table: 140 ksi up to 1/2 in, 135 ksi above. Metric screws use ISO 898-1 class 12.9: 970 MPa. | A574 was checked against the Fastenal chart (N/mold-materials.md 3). The structural notes used grade-8 proof (120 ksi) only because A574 had not been confirmed. |
| D18 | 6061 fatigue data | Lower the 6061 S-N points in `mold-materials.json` to 142 / 111 / 101 MPa at 1e5 / 1e6 / 1e7 cycles (the Engineers Edge fit). The table's 185 / 130 / 105 MPa are replaced until checked against MMPDS. | The two streams disagree by 30% at 1e5 cycles. Take the conservative values. |

---------------------------------------------------------------------------------------------------------------------

## 1. The tool folder

### 1.1 Layout

```
tools/builtin/design-compression-mold/
  TOOL.md                         front matter + the agent's procedure (1.2, 1.3)
  inputs.json                     the PropertyManager page (1.4)
  icon.png                        ribbon icon, 64 x 64 px: two plates and a cavity, read by AgentRibbon.LoadIcon
  macro.cs                        gathering macro: measures the part when it is already in the document (1.6)
  data/materials.json             96 part-material subtypes in 38 families, with process data (1.5.1)
  data/mold-materials.json        16 mold grades, fasteners, thread inserts, dowels, stock thicknesses (1.5.2)
  data/shop-standards.json        the user's shop rules: flash grooves, dowels, pry slots, chamfers, TC wells, rounding (1.5.3)
  data/sizing-defaults.json       every judgement factor of the structural method in one editable place (1.5.4)
  data/mold.schema.json           JSON Schema (draft 2020-12) of the design record / simulator manifest (4.3)
  data/setup-sheet.html           template for the operator's setup sheet (1.5.6)
  data/validation/examples.json   worked examples E1-E12: inputs and expected outputs, read by unit tests (6.2)
  data/validation/sizing-table.csv          derived design table: thickness vs pressure, span, material (3.20)
  data/validation/montecarlo-summary.json   result of the closed-loop validation over 2,000 random molds (3.20)
```

How it ships and what stays out of the shipped folder:
- **Shipping.** `SwAgent.csproj:60` copies `tools\builtin\**\*` into `builtin-tools\` next to the dll. The copy is recursive, so `data/` ships with no build change. The `macro.cs` file is outside `src/SwAgent`, so the SDK compile glob never compiles it.
- **The name.** The tool name matches `UserTools.NamePattern` (UserTools.cs:23).
- **Development-only folders** (in the repository, not shipped):
  - `tools/dev/mold-data/`: the two generators `gen_materials.py` and `build_materials.py`, moved from the scratchpad. Run them with `python -I`.
  - `tools/dev/MoldSweep/`: a sweep console (2.4).

### 1.2 TOOL.md front matter (exact)

```
---
name: design-compression-mold
title: Design compression mold for plastics and resins
ribbon_title: Compression Mold
description: Designs a heat-press compression mold for a PU, resin, rubber or thermoset part, with every plate, insert and pad as a body in this part. It reads the part and its drawing, agrees the parting line, pour and vents with you, sizes plates and bolts with a validated structural calculation, builds in checkpointed stages and writes a setup sheet.
category: Molding & Toolmaking
kind: tool
icon: part
agent: true
intelligence: max
confirm: always
context: view, model, history
long_run: true
max_steps: 600
requires: mold_calc, mold_part, mold_bodies, mold_record
created: 2026-10-19T00:00:00Z
updated: 2026-10-19T00:00:00Z
---
```

The parser splits each line at the first colon only (UserTools.cs:599-610), so `&` and later colons are safe. `icon: part` is one of the 12 allowed names (UserTools.cs:21). `icon.png` overrides it on the ribbon.

### 1.3 TOOL.md body: outline the builder turns into text

The body is written as a procedure. It must make sense without this conversation. Headings, in order:

**"# Design compression mold (built-in SolidWorks Agent tool)"**: two sentences on what it does, and on what the user gets: a multi-body mold part, `mold.json`, STEP exports, and a setup sheet.

**"## How to work"**: these rules override the generic tool-run rules:
- This is a long, phased job. Follow the phases in order and stop at every gate.
- At a gate, show a short summary: up to about 12 bullets, in the document's units with SI in brackets. Then ask or continue, as `decisions` says.
- **These reporting rules replace the "finish with one short line" rule and the reply-length preference.**
- **State.**
  - Begin every phase with `mold_record read` (a summary of up to 6,000 characters).
  - Before leaving a phase, write the results with `mold_record merge`.
  - Append decisions and lessons with `rebuild_journal append`.
  - After the context has been cleared, trust the record, not your memory.
- **Decisions.** Same meaning as Smart Rebuild:
  - "Ask on big ones": decide routine calls yourself. Ask before anything that changes function, cost, the operator's work, or the part.
  - "Ask about all": ask before every judgement call.
  - "Decide everything": make the calls yourself and report each one.
- **Questions.** At most one `ask_form` per gate (8 fields at most), plus `ask_selection` for faces. Pre-fill every answer with your recommendation.

**"## Rules that always apply"**:
1. **Frame.** The press axis W is the normal of the Top Plane. Find the Top Plane as the 2nd RefPlane feature, never by name. The name depends on the language (v1 bug, N/existing-mold-knowledge.md 2.0). U and V lie in the plane. +W points to the lid.
2. **Names.**
   - Every feature starts with `MOLD `, for example `MOLD PL1`, `MOLD Part <file>`, `MOLD Shrink 1.80%`, `MOLD Plate BASE-1`, `MOLD Cavity BASE-1`, `MOLD Dowels`, `MOLD Flash moat`, `MOLD Vents`.
   - Body roles and ids: BASE-n, MID-n, LID-n, SUB-n, INS-<cavity>-n, STOP-n, PART-REF.
   - Folders as in D3.
   - Global variables are named `Mold_*`, for example `Mold_FloorT`, `Mold_LidT`, `Mold_WallU`, `Mold_WallV`. Plate sizes are driven by these variables.
3. **Multi-body work** (knowledge base, N/existing-mold-knowledge.md 4.1):
   - One fillet or chamfer feature per body.
   - Combine-subtract: main body Mark 1, tool bodies Mark 2, `InsertCombineFeature((int)swBodyOperationType_e.SWBODYCUT, null, null)`. Move/Copy: select with Mark 1.
   - A cut meant for chosen bodies uses feature scope: `UseAutoSelect=false`, with those bodies selected.
   - Select by object. Never select by coordinates on the parting plane, where plates touch.
   - Add body operations (Combine, Split, Move/Copy, Scale) only after every body they pick already exists.
   - Never edit the body selections of an existing body-picking feature. Make a new feature.
4. **Sketch planes.** Sketch on reference planes (`MOLD PL1`, `MOLD Base bottom`, `MOLD Lid top`), not on faces that a later feature changes.
5. **Crash safety.**
   - No timers, callbacks or background threads that call the API.
   - Null-check every API return value.
   - Temporary bodies are only for previews (`Overlay.Body`).
   - In long cells, set `FeatureManager.EnableFeatureTree = false` and `ActiveView.EnableGraphicsUpdate = false`, and restore both in `finally`.
6. **The document.** Never close or reopen the mold document from code. To go back, use `checkpoint restore`.
7. **Hard limits:**
   - No parting line, flash land, groove, vent, sprue or overflow on a critical face.
   - No aluminium above its grade's `never_above` temperature.
   - Never go over a weight or size limit without the user agreeing (recorded as `waived` in the record).
   - Never hold a joint by friction alone.
8. **Data confidence.** If the part material row has confidence "low", say so where its values drive a decision. Ask for datasheet values when the drawing's tolerances are tighter than half the shrink spread, that is when `(S_max - S_min) x L > 0.5 x tol`.

**"## Phase 0 - Intake"** (aim: 6 steps or fewer)
1. Read `<tool_inputs>` and the macro output (the part analysis, if the part was already in the file).
2. Run `mold_record read` and `rebuild_journal read`. If an unfinished record exists, ask: Continue / Start over / Cancel.
3. The active document must be a part. If it has solid bodies but no record, ask: "Are these bodies the molded part?" (Use them / Stop).
4. If the part has never been saved:
   - Propose `<molded part name> mold.SLDPRT` in the molded part's folder.
   - Save with `Extension.SaveAs3(..., swSaveAsOptions_Silent)` (ask first, as `decisions` says).
   - Then run `mold_record init`. It creates `<name>.mold\` and registers the file for checkpoints (2.3.5).
5. If `press_pressure_is` is "Not sure - ask me", ask now with one `ask_form`: gauge reading or on the part; if a gauge reading, the rated tonnage and the gauge at that rating.

**"## Phase 1 - Understand the part and the drawing"**
1. **Insert the part.** If `part_file` is given and not yet in the file, call `Mold.InsertPart(path)`.
   - STEP, IGES and Parasolid files are first converted to SLDPRT in `<name>.mold\part\`.
   - An STL can't become a reliable solid. Ask for STEP or SLDPRT and stop.
2. **Orientation.** Run `mold_part analyze` with candidate pull directions: +W, then the three axes of the part's own box.
   - Choose the axis with no undercut (or the least undercut area), then the least cavity depth.
   - If the two best are within 10% of each other, draw both as overlay arrows and ask.
   - Turn the part so pull = +W with Move/Copy (bCopy=false). Record the transform.
3. **Read the drawings.**
   - Material callouts, and critical or cosmetic surfaces. Look for notes such as "no parting line", "no flash" or "class A".
   - Flash class: ISO 3302-1 / RMA T-codes and F-codes.
   - Tolerances: thickness, and which dimensions cross the parting line.
   - Finish (SPI or Ra), inserts or overmolded hardware, quantity.
   - Record each item with its sheet and zone.
   - A `.slddrw` arrives as a converted PDF (2.3.9). A DXF or DWG arrives as a path only: ask for a PDF.
4. **Analyze.** Run `mold_part analyze` (pull +W, minimum draft = `draft_min_deg`, fill = base pour). It returns:
   - volume, projected area and outline;
   - draft classes and undercuts;
   - fragile features;
   - parting-line candidates;
   - air traps.
   The tool writes them to the record.
5. **Gate G1** (no question unless something blocks the job): summarize the part in 8 bullets or fewer: size, volume, mass at the material's SG, projected area, draft problems, undercuts, cores.

**"## Phase 2 - Plan with the user"**
1. **Critical faces.**
   - Preselect the faces suggested by the drawing, plus the page's `critical_faces`.
   - Show them in yellow (`show_overlay` face items).
   - Call `ask_selection` ("Add or remove the critical faces, then press Done").
   - Record their refs and persistent ids.
2. **Process class and mold type** (rules from N/design-rules.md 1.2):
   - Class A: flash mold with a full-face metal-to-metal land.
   - Class B, shallow parts: flash mold with stop pads and an overflow groove.
   - Class B, parts deeper than they are wide: semi-positive.
   - Closure dimension critical, with a weighed charge: landed positive.
   - Class C: positive.
3. **Parting line.**
   - Take the candidates from `mold_part`. Drop any that touch a critical face.
   - Score the rest on: undercut area, number of closure dimensions created, air traps, setups per piece.
   - Draw the best 1-3 in cyan, with labels.
4. **Pour or charge, and air traps.**
   - For class A, compute air traps for a base pour. If any appear, also compute a lid pour and a 10 deg tilt.
   - Draw traps as magenta markers labelled with the trapped volume. Draw proposed vents and risers in blue.
5. **Inserts.** Fragile features (orange) become steel inserts in aluminium construction, by the rules in 3.14.
6. **Layout.** Run `mold_calc layout` for `cavities`. Draw the footprint as translucent grey boxes (`Overlay.Body` with `IModeler.CreateBodyFromBox3`).
7. **One `ask_form`** with at most 8 fields, pre-filled:
   - the parting-line choice;
   - pour side and tilt;
   - flash system (shop standard / material table / other);
   - demold method (pry slots + jack screws / + air poppet / + knockout holes);
   - inserts or overmolded hardware in the part (none / describe);
   - anything else;
   - any press questions still open.
8. Write the plan to the record and append "Plan agreed: ..." to the journal.
9. Run hook H1 (Phase 7).

**"## Phase 3 - Size"**
1. Run `mold_calc process`: shrink scale, charge per cavity and per shot, overflow volume, cure time at the press temperature, recommended and maximum gauge.
2. Run `mold_calc size`: walls, floors, lid, stops and bearing, keys, bolts, fits, inserts, plate thicknesses rounded to stock, masses and boxes. Read the lowest margins first.
3. **A weight or size limit fails:** run `mold_calc split` and present up to 3 options with piece masses. Ask, then run `size` again.
4. **A check fails that sizing can't fix** (aluminium above its derated temperature, a span with t/b > 0.5, the press too small): offer the choices the calculator lists and ask.
5. **Gate G3.**
   - Preview the stack as temporary boxes labelled with thickness and mass.
   - Summarize: pieces, materials, thicknesses, masses, bolts, recommended gauge.
   - Ask "Build this?" unless `decisions` is "Decide everything".

**"## Phase 4 - Build in stages"**

Every stage follows the same loop:
1. `checkpoint save stage-4.x`.
2. Build in one or more cells.
3. `rebuild`.
4. Run the stage's own check, by measurement.
5. `mold_record merge` (stage done, plus feature names).
6. Append to the journal.

If a stage fails and two fixes don't converge: `checkpoint restore` to the last good stage, record the lesson, revise, and continue.

| Stage | What gets built | Stage check |
|---|---|---|
| 4.1 Frame and reference | `MOLD PL<n>` offset planes from the Top Plane. The Insert Part (if not done yet). `MOLD Shrink` (Scale on the part body only, `swScaleAboutOrigin`, factors from the record). `MOLD Cavity copies` (Move/Copy per layout). Folder "Mold - Reference". | Reference volume = original x sx·sy·sz within 0.1%. One copy per cavity at its recorded origin, within 0.01 mm. |
| 4.2 Plates | One `MOLD Plate <ID>` per piece: `FeatureExtrusion3` with Merge off, sketched on the Top Plane or a PL plane with a start offset (the verified v1 pattern). Global variables. Folders. | Each body's box matches the plan within 0.01 mm. |
| 4.3 Cavities | Per plate: copy the tool bodies (Mark 1), then Combine-subtract (plate Mark 1, tools Mark 2) as `MOLD Cavity <ID>`. | Removed volume = copies' volume inside that plate within 0.1%. |
| 4.4 Inserts | Split the insert regions into INS-* bodies. Pockets with shoulders. Fits from `mold_calc`. Folder "Mold - Inserts". | Interference = only the planned fits. |
| 4.5 Alignment and fastening | Dowels and guide-pin holes (one round plus one diamond or slot, per the calculator). Bolts with `HoleWizard5`: counterbore in the top piece, tap drill or insert size in the bottom piece, feature scope set. Keys or steps for multi-piece bases. | Engagement and clearances measured. |
| 4.6 Stops and lands | Class B: STOP-n pads in pockets, and relief outside the land. Class A: lap-flat land notes only. | Bearing area = plan within 5%. |
| 4.7 Flash system | Class A: lip, moat and runners (`Mold.FlashGrooves`). Class B: land, overflow groove and radial exits. Vents at every air trap on the parting line, risers for internal traps, a sprue if needed. | Rules of D14 measured (acceptance A10, A11). |
| 4.8 Process and handling | One TC well per piece. Pry slots. Jack-screw holes. Lift holes or handles chosen by mass. Clamp ledges if the press needs them. Knockout holes or air-poppet seats. | 0.25 in clearance to the flash system. |
| 4.9 Finish | Fillets and chamfers, one feature per body. Per-body materials (`IBody2.SetMaterialProperty`). Leftover tool bodies go to "Garbage". Custom properties. | Rebuild is clean. |

**"## Phase 5 - Verify"**: the acceptance checks below, all measured. Then hook H2.

**"## Phase 6 - Report and setup sheet"**
1. `mold_bodies export`: one STEP per body, plus the part.
2. `mold_record setup_sheet`.
3. Write the custom properties. Keep the user's four: MoldNumber, Resin, CureTime_min, PressTemp_F. Add MoldMaterial, Shrink_pct, PlateSize_in, StackHeight_in, PlateThicknesses_in, Cavities, CycleLife, GaugeSetting_psi, ChargePerCavity_g and Unify3D.MoldRecord.
4. Give a chat report of 25 lines or fewer:
   - pieces, with material, size and mass;
   - the press setting;
   - charge and cure;
   - the lowest three margins;
   - open items;
   - file paths.

**"## Phase 7 - External checks (hooks)"**
- **H1** (end of Phase 2):
  - If a tool named `sim_process` is in your tool list, call it with the record path and `exports/part/*.step`. Merge the results, then adjust the vents, pour or orientation.
  - Otherwise record `simulations[] {hook:"H1", status:"not_run"}`.
- **H2** (end of Phase 5):
  - Call `sim_machinability` for each body STEP, and `sim_process` on the as-built cavity.
  - A failing check sends you back to the stage that owns it (table in 4.4).
- **H3**: if a pressure field arrived, `mold_calc check` uses it.
- Never invent results for a simulator that isn't there.

**"## Acceptance checks (verify by measurement before reporting success)"**

| Id | Check |
|---|---|
| A1 | After `ForceRebuild3`, no feature has `GetErrorCode2 != 0` unless it is only a warning. |
| A2 | Every solid body is in exactly one "Mold - *" folder or in "Garbage". The record's `bodies[]` matches by count and role. |
| A3 | Reference volume = original x sx·sy·sz within 0.1%. |
| A4 | Each cavity's removed volume = the copies' volume within 0.1%. Each cavity sits at its layout origin within 0.01 mm. |
| A5 | Every machined piece: mass ≤ `max_plate_mass`, and the sorted box + blank allowance ≤ the sorted `max_plate_size`. Lid ≤ `max_lid_mass`. Sum of base pieces ≤ `max_base_mass`. Otherwise the user waived it in the record. |
| A6 | No interference between mold bodies: `IModeler.CheckInterference3` volume < 1 mm³, except the recorded press fits. |
| A7 | No parting-line edge, land, groove, vent, riser or sprue face touches a critical face (`IModelDoc2.ClosestDistance` > 0 and no shared edge). |
| A8 | Every cavity face has a draft of at least `draft_min_deg` from its half's pull direction, or is an accepted exception in the record. |
| A9 | `mold_part` on each half reports no undercut, or each one is covered by an agreed insert or side core. |
| A10 | Flash system as planned. **Class A:** lip ≥ 0.125 in everywhere; moat 0.5 x 0.125 in; R0.25 in corners; runners reach the plate edge at a pitch of 2 in or less; at least 0.25 in of steel to every other feature; no dead ends. **Class B:** land width = plan ± 0.05 mm; overflow volume ≥ `V_req` (3.15). |
| A11 | Every recorded air trap has a vent or riser within 2 mm of its high point, at the planned depth. |
| A12 | `mold_calc check` (as built): every margin ≥ 0, or waived with a reason. |
| A13 | One round dowel plus one diamond or slot (or an offset pin). Pins engage at least 12 mm before the plug enters. |
| A14 | Every tapped hole meets its L_e/d, or has an insert. Hardened washers are noted on aluminium faces. The torque list is complete. |
| A15 | One STEP per body plus the part. `mold.json` validates against `data/mold.schema.json`. The setup sheet is written. The custom properties are set. |

**"## Colours on the model"**:
- yellow = critical faces;
- cyan = parting line;
- red = needs draft or an undercut;
- green = drafted correctly (only when asked);
- magenta = air trap;
- blue = vent or riser;
- orange = fragile feature, steel insert recommended;
- grey translucent = plate-stack preview;
- white = labels.

**"## How the sizing works"**: 15 lines or fewer, summarizing section 3 and pointing to `data/validation/sizing-table.csv` and `montecarlo-summary.json`. This gives the agent the reasoning without making it re-derive the numbers.

### 1.4 inputs.json

The order follows the user's ranking of importance. One group comes first: "Molded part", the part itself and its drawing. That matches SolidWorks commands, which put what you work on at the top.

Spec units are what `Default`, `Min`, `Max` and the data files use: mm, deg, %, MPa, degC, kg, kN, cP, min. Page display follows the document (1 MPa = 145.04 psi; °F = °C x 1.8 + 32; 1 kg = 2.2046 lb; 1 US ton-force = 8.896 kN). The new properties are defined in 2.1.1.

| # | Id | Type, Unit | Default (spec units = IPS display) | Range | Optional / Remember | Group | Data and visibility |
|---|---|---|---|---|---|---|---|
| 1 | part_file | files, model, single | empty | | no / no | Molded part | |
| 2 | drawing | files, pdf + image + drawing, several | empty | | no / no | Molded part | |
| 3 | critical_faces | selection, face, several | the selection at start | | no / no | Molded part | |
| 4 | mold_material | options | Aluminium + steel inserts | | no / yes | Mold | |
| 5 | mold_grade | choice | data default: 6061-T651, or P20 pre-hard | | no / yes | Mold | ChoicesFrom mold-materials#mold_materials, DependsOn mold_material |
| 6 | insert_grade | choice | data default: P20 pre-hard | | no / yes | Mold | ShowWhen mold_material = aluminium |
| 7 | grade_info | info | "Full strength to 248 degF, derated to 302 degF" (for 6061) | | | Mold | template |
| 8 | part_family | choice | Cast PU - TDI polyether (MBOCA cured) | | no / yes | Part material | ChoicesFrom materials#rows, key familyLabel |
| 9 | part_grade | choice | TDI-polyether / MBOCA 90A | | no / yes | Part material | DependsOn part_family; Fills 11-17 and 20 |
| 10 | material_info | info | "Liquid pour (hot cast) - data confidence medium" | | | Part material | template |
| 11 | shrink_pct | number, percent | 1.5 % (filled) | 0-10 | no / yes | Part material | filled from `shrinkPct.typ` |
| 12 | sg | number, none | 1.10 (filled) | 0.1-5 | no / yes | Part material | `sg.typ` |
| 13 | viscosity_cp | number, viscosity | 1,200 cP (filled) | 1-10,000,000 | no / yes | Part material | `viscosityCp.typ`; hidden when null |
| 14 | mooney | number, none | 45 (filled; hidden for this default grade) | 1-200 | no / yes | Part material | `mooney.typ`; hidden when null |
| 15 | pot_life_min | number, time | 6 min (filled) | 0-1440 min | no / yes | Part material | `potLifeMin.typ`; hidden when null |
| 16 | cure_min | number, time | 30 min (filled) | 0-1440 min | no / yes | Part material | `demoldMin.typ` |
| 17 | draft_min_deg | number, angle | 0.5 deg (filled) | 0-10 deg | no / yes | Part material | `draftDeg.min` |
| 18 | press_pressure | number, pressure | 13.79 MPa = 2,000 psi | 0.01-700 MPa | no / yes | Press | |
| 19 | press_pressure_is | choice | Not sure - ask me | | no / yes | Press | (kept right under the value it explains) |
| 20 | press_temp | number, temperature | 100 degC = 212 degF (filled) | 0-450 degC | no / no | Press | `moldTempC.typ` |
| 21 | press_rated_force | number, tonnage | 444.8 kN = 50 US ton | 1-50,000 kN | no / yes | Press | ShowWhen 19 ≠ "On the part" |
| 22 | press_rated_gauge | number, pressure | 20.68 MPa = 3,000 psi | 0.1-100 MPa | no / yes | Press | ShowWhen 19 ≠ "On the part" |
| 23 | platen_size | size, 2 dims | 610 x 610 mm = 24 x 24 in, off | 50-5,000 mm | yes (off) / yes | Press | |
| 24 | daylight | number, length | 300 mm = 11.8 in, off | 10-3,000 mm | yes (off) / yes | Press | |
| 25 | cavities | integer | 1 | 1-64 | no / no | Cavities & limits | |
| 26 | max_plate_mass | number, mass | 23 kg = 51 lb, on | 1-5,000 kg | yes (on) / yes | Cavities & limits | |
| 27 | max_plate_size | size, 3 dims | 600 x 400 x 150 mm = 23.6 x 15.7 x 5.9 in, on | 10-5,000 mm | yes (on) / yes | Cavities & limits | |
| 28 | max_base_mass | number, mass | 68 kg = 150 lb, off | 1-20,000 kg | yes (off) / yes | Cavities & limits | |
| 29 | max_lid_mass | number, mass | 18 kg = 40 lb, on | 1-5,000 kg | yes (on) / yes | Cavities & limits | |
| 30 | cycle_life | integer | 5,000 | 1-10,000,000 | no / yes | Cavities & limits | |
| 31 | decisions | options | Ask on big ones | | no / yes | Decisions & notes (collapsed) | |
| 32 | notes | text | empty | | no / no | Decisions & notes | |

Where the default limits come from:
- **23 kg** is the NIOSH lifting-equation load constant (Waters et al. 1993, NIOSH pub. 94-110). The user lifts plates now and then.
- **18 kg** is for the lid, which is lifted every cycle, hot, with gloves, at press height. It sits between the HSE guideline of 16 kg (women) and 25 kg (men), and below NIOSH 23 kg, because of the frequency and coupling factors (N/design-rules.md 17).
- **600 x 400 x 150 mm** is a typical vertical machining centre's capacity after fixture allowance.
- **5,000 cycles** is the middle of construction tier T2 (500-10,000).

Full file (spec units; `Tip` text is shown on the page):

```json
[
  { "Id": "part_file", "Type": "files", "Label": "Molded part", "Group": "Molded part",
    "Tip": "The part to mold (SLDPRT, STEP, IGES, Parasolid). Leave empty if the part is already in this file. It is inserted as a linked Insert Part, so a revised part can be swapped later with Smart Rebuild.",
    "Accept": [ "model" ], "Multiple": false, "Required": false },
  { "Id": "drawing", "Type": "files", "Label": "Engineering drawing", "Group": "Molded part",
    "Tip": "PDF, image or SolidWorks drawing of the part: tolerances, critical or cosmetic surfaces, flash class, finish. Keep PDFs under 20 MB.",
    "Accept": [ "pdf", "image", "drawing" ], "Multiple": true, "Required": false },
  { "Id": "critical_faces", "Type": "selection", "Label": "Critical faces (no parting line)", "Group": "Molded part",
    "Tip": "If the part is already in this file: faces no parting line, flash, vent or sprue may touch. Otherwise the agent asks you to pick them after it inserts the part.",
    "Filter": [ "face" ], "Multiple": true, "Required": false },

  { "Id": "mold_material", "Type": "options", "Label": "Mold material", "Group": "Mold", "Remember": true,
    "Tip": "Aluminium plates with steel inserts for fragile or wearing features, or tool steel throughout. Above about 150 degC aluminium loses strength over time; the agent will recommend steel.",
    "Choices": [ "Aluminium + steel inserts", "Tool steel" ], "Default": "Aluminium + steel inserts" },
  { "Id": "mold_grade", "Type": "choice", "Label": "Plate grade", "Group": "Mold", "Remember": true,
    "Tip": "Grade of the machined plates. Strength is derated for long service at the press temperature.",
    "ChoicesFrom": "data/mold-materials.json#mold_materials", "ChoiceKey": "short", "DependsOn": "mold_material",
    "RowFilter": { "family": "{mold_material}", "use": "plate" },
    "ValueMap": { "Aluminium + steel inserts": "aluminium", "Tool steel": "steel" } },
  { "Id": "insert_grade", "Type": "choice", "Label": "Insert grade", "Group": "Mold", "Remember": true,
    "Tip": "Steel for inserts at fragile, thin or wearing features in an aluminium mold.",
    "ChoicesFrom": "data/mold-materials.json#mold_materials", "ChoiceKey": "short",
    "RowFilter": { "family": "steel", "use": "insert" },
    "ShowWhen": { "mold_material": [ "Aluminium + steel inserts" ] } },
  { "Id": "grade_info", "Type": "info", "Group": "Mold",
    "Text": "Full strength to {mold_grade.max_service_C.full_strength|temperature}, derated to {mold_grade.max_service_C.derated|temperature}" },

  { "Id": "part_family", "Type": "choice", "Label": "Part material", "Group": "Part material", "Remember": true,
    "Tip": "Material family of the molded part. Picking a grade loads typical process data into the fields below; replace them with your datasheet values.",
    "ChoicesFrom": "data/materials.json#rows", "ChoiceKey": "familyLabel", "Default": "Cast PU - TDI polyether (MBOCA cured)" },
  { "Id": "part_grade", "Type": "choice", "Label": "Grade", "Group": "Part material", "Remember": true,
    "ChoicesFrom": "data/materials.json#rows", "ChoiceKey": "name", "DependsOn": "part_family",
    "RowFilter": { "familyLabel": "{part_family}" }, "Default": "TDI-polyether / MBOCA 90A",
    "Fills": { "shrink_pct": "shrinkPct.typ", "sg": "sg.typ", "viscosity_cp": "viscosityCp.typ", "mooney": "mooney.typ",
               "pot_life_min": "potLifeMin.typ", "cure_min": "demoldMin.typ", "draft_min_deg": "draftDeg.min", "press_temp": "moldTempC.typ" } },
  { "Id": "material_info", "Type": "info", "Group": "Part material",
    "Text": "{part_grade.processLabel} - data confidence {part_grade.confidence}" },
  { "Id": "shrink_pct", "Type": "number", "Unit": "percent", "Label": "Shrinkage", "Group": "Part material", "Remember": true,
    "Tip": "Linear mold shrinkage as the supplier quotes it (cold cavity to cold part, steel reference mold). The agent corrects it for the mold metal and temperature.",
    "Min": 0, "Max": 10, "Default": "1.5" },
  { "Id": "sg", "Type": "number", "Unit": "none", "Label": "Specific gravity", "Group": "Part material", "Remember": true,
    "Min": 0.1, "Max": 5, "Default": "1.10" },
  { "Id": "viscosity_cp", "Type": "number", "Unit": "viscosity", "Label": "Mixed viscosity", "Group": "Part material", "Remember": true,
    "Tip": "Initial mixed viscosity at the processing temperature. Sets the flash-gap class, land and vent depth.",
    "Min": 1, "Max": 10000000, "Default": "1200" },
  { "Id": "mooney", "Type": "number", "Unit": "none", "Label": "Mooney viscosity", "Group": "Part material", "Remember": true,
    "Min": 1, "Max": 200, "Default": "45" },
  { "Id": "pot_life_min", "Type": "number", "Unit": "time", "Label": "Pot life", "Group": "Part material", "Remember": true,
    "Min": 0, "Max": 1440, "Default": "6" },
  { "Id": "cure_min", "Type": "number", "Unit": "time", "Label": "Cure / demold time", "Group": "Part material", "Remember": true,
    "Min": 0, "Max": 1440, "Default": "30" },
  { "Id": "draft_min_deg", "Type": "number", "Unit": "angle", "Label": "Minimum draft", "Group": "Part material", "Remember": true,
    "Min": 0, "Max": 10, "Default": "0.5" },

  { "Id": "press_pressure", "Type": "number", "Unit": "pressure", "Label": "Press pressure", "Group": "Press", "Remember": true,
    "Tip": "The number you set or read on the press. Say below what it means.", "Min": 0.01, "Max": 700, "Default": "13.79" },
  { "Id": "press_pressure_is", "Type": "choice", "Label": "Press pressure is", "Group": "Press", "Remember": true,
    "Tip": "Gauge reading: the hydraulic gauge on the press (most common). On the part: pressure on the part's projected area, as material sheets quote it.",
    "Choices": [ "Gauge reading", "On the part", "Not sure - ask me" ], "Default": "Not sure - ask me" },
  { "Id": "press_temp", "Type": "number", "Unit": "temperature", "Label": "Press temperature", "Group": "Press",
    "Tip": "Platen set point. Filled from the material; edit it to your process.", "Min": 0, "Max": 450, "Default": "100" },
  { "Id": "press_rated_force", "Type": "number", "Unit": "tonnage", "Label": "Press rated force", "Group": "Press", "Remember": true,
    "Tip": "Nameplate tonnage. With the gauge at that tonnage it gives the ram area.", "Min": 1, "Max": 50000, "Default": "444.8",
    "ShowWhen": { "press_pressure_is": [ "Gauge reading", "Not sure - ask me" ] } },
  { "Id": "press_rated_gauge", "Type": "number", "Unit": "pressure", "Label": "Gauge at rated force", "Group": "Press", "Remember": true,
    "Min": 0.1, "Max": 100, "Default": "20.68",
    "ShowWhen": { "press_pressure_is": [ "Gauge reading", "Not sure - ask me" ] } },
  { "Id": "platen_size", "Type": "size", "Dims": 2, "Label": "Heated platen size", "Group": "Press", "Remember": true,
    "Optional": true, "Enabled": false, "Min": 50, "Max": 5000, "Default": "610,610",
    "Tip": "Usable heated area of the platen (length x width). Unchecked: the footprint check is skipped." },
  { "Id": "daylight", "Type": "number", "Unit": "length", "Label": "Daylight", "Group": "Press", "Remember": true,
    "Optional": true, "Enabled": false, "Min": 10, "Max": 3000, "Default": "300",
    "Tip": "Largest opening between the platens. Unchecked: the stack-height check is skipped." },

  { "Id": "cavities", "Type": "integer", "Label": "Number of cavities", "Group": "Cavities & limits", "Min": 1, "Max": 64, "Default": "1" },
  { "Id": "max_plate_mass", "Type": "number", "Unit": "mass", "Label": "Max weight per machined plate", "Group": "Cavities & limits",
    "Remember": true, "Optional": true, "Enabled": true, "Min": 1, "Max": 5000, "Default": "23",
    "Tip": "Default 23 kg (51 lb): the NIOSH lifting-equation load constant." },
  { "Id": "max_plate_size", "Type": "size", "Dims": 3, "Label": "Max size per plate", "Group": "Cavities & limits",
    "Remember": true, "Optional": true, "Enabled": true, "Min": 10, "Max": 5000, "Default": "600,400,150",
    "Tip": "Largest blank your machine and fixturing take. Compared sorted, so the plate may be turned; blank allowance is added." },
  { "Id": "max_base_mass", "Type": "number", "Unit": "mass", "Label": "Max total base weight", "Group": "Cavities & limits",
    "Remember": true, "Optional": true, "Enabled": false, "Min": 1, "Max": 20000, "Default": "68" },
  { "Id": "max_lid_mass", "Type": "number", "Unit": "mass", "Label": "Max lid weight", "Group": "Cavities & limits",
    "Remember": true, "Optional": true, "Enabled": true, "Min": 1, "Max": 5000, "Default": "18",
    "Tip": "The lid is lifted every cycle, hot. Default 18 kg (40 lb)." },
  { "Id": "cycle_life", "Type": "integer", "Label": "Cycle life (parts per cavity)", "Group": "Cavities & limits", "Remember": true,
    "Min": 1, "Max": 10000000, "Default": "5000",
    "Tip": "Shots the mold must survive. Sets fatigue design, wear parts and thread inserts." },

  { "Id": "decisions", "Type": "options", "Label": "Big decisions", "Group": "Decisions & notes", "GroupExpanded": false, "Remember": true,
    "Choices": [ "Ask on big ones", "Ask about all", "Decide everything" ], "Default": "Ask on big ones" },
  { "Id": "notes", "Type": "text", "Label": "Notes", "Group": "Decisions & notes", "Required": false,
    "Tip": "Anything the agent should know: customer requirements, press quirks, how you demold." }
]
```

Rules for the page:
- **Seven groups.** The six above plus the "SolidWorks Agent" group (ToolPage.cs:177-188). This breaks today's ids: group ids count from 1 (ToolPage.cs:160) while control ids start at 10 (ToolPage.cs:53). The fix in 2.1.4 is required.
- **`press_temp`** is deliberately not remembered. It follows the material each run unless the user edits it.
- **A field the selected row has no value for** (row value null) is hidden. An example is `viscosity_cp` for rubber. Its line in the payload reads "n/a for this material".

### 1.5 Data files

#### 1.5.1 data/materials.json (part materials)

Source: `N/materials.json` (96 rows, every row with the same keys; schema in N/materials.md 1). Changes before it ships:
1. **Wrap it.** The file becomes `{ "schema": "solidworksAgent.part-materials/1", "generated": "<iso date>", "sources": { "S1": {"title": ..., "url": ...}, ... "S22": ..., "K-PU": ... }, "rows": [ ... ] }`. The page uses `#rows`. The sources list comes from N/materials.md 7.
2. **Two new fields on every row:**
   - `processLabel`: "Liquid pour (hot cast)", "Liquid pour (room temperature)", "Foam pour", "Rubber compression", "Compound compression", "LSR compression", "Thermoplastic compression" or "Preform + sinter".
   - `isDefault`: one `true` per family. TDI-ether 90A for its family; for the others, the first row with medium confidence.
3. **Viscosity class.**
   - 11 rows carry `viscosityClass = liquid_low` while `viscosityCp.typ` is 1,000-2,500 cP: the TDI- and MDI-ether PU grades and three foams.
   - The definition becomes "free-flowing pour, < about 3,000 cP at the process temperature".
   - The calculator does not use the class for liquids. It uses the numeric viscosity from the page (3.5).
4. **Per-axis shrink.**
   - Optional fields `shrinkPctAxis: {x, y, z}` default to null. They are filled later for SMC/BMC, PEEK, UHMWPE and PTFE.
   - Until then the agent asks for per-axis shrink for those families (5, item 6).
5. **Generator.** `tools/dev/mold-data/gen_materials.py` produces exactly this file. It fails on:
   - duplicate ids;
   - unequal key sets between rows;
   - any range with min > typ or typ > max;
   - any `Fills` path in `inputs.json` that is missing from a row. A null value is allowed; a missing key is not.

The fields the tool reads:

| Field | Used for |
|---|---|
| `processType` | Process class |
| `shrinkPct` and `shrinkBasis` | Scale factor |
| `viscosityCp` and `viscosityClass` | Allowable deflection, land and vents |
| `mooney` | Charge and flow notes |
| `bulkFactor` | Loading well |
| `potLifeMin` | Pour sequence |
| `moldTempC` | Temperature checks; cure scaling |
| `demoldMin` | Cure time |
| `postCure` | Setup sheet |
| `sg` | Charge |
| `moldingPressurePsi` | Pressures |
| `draftDeg` | Draft check |
| `flash.*` | Class B land and overflow |
| `flashAllowancePct` and `overflowPctOfPartVolume` | Overflow volume |
| `ventingSensitivity` and `ventDepthMm` | Vents |
| `moistureSensitivity`, `corrosiveness`, `moldCompat` | Warnings and grade advice |
| `hazards` | Setup sheet |
| `confidence` and `sources` | Shown to the user |

#### 1.5.2 data/mold-materials.json (mold grades and hardware)

Source: `N/mold-materials.json`. The top-level keys stay: `units, conventions, mold_materials (16), fasteners_common, fasteners_inch (9 sizes #10-24 to 3/4-10), fasteners_metric (M5-M20), inserts, dowels, stock_thickness, hooks`. Changes:
1. **New fields on every grade:**
   - `short` is the page label, since the PropertyManager is about 300 px wide. Labels: "6061-T651", "7075-T651", "MIC-6 cast plate", "QC-10", "Alumold 500", "2024-T351", "5083", "P20 pre-hard", "4140 pre-hard", "A36", "1018 CD", "H13 44-50 HRC", "S7 54-56 HRC", "A2 57-62 HRC", "420 SS pre-hard", "17-4PH H1025".
   - `use` is a list from plate, insert, backing, stop:

     | `use` | Grades |
     |---|---|
     | [plate] | 6061, 7075, QC-10, Alumold 500 |
     | [plate, backing] | MIC-6, 5083 |
     | [] (not offered on the page) | 2024-T351: it ages, and so changes size, above about 120 degC |
     | [plate, insert] | P20, 420 |
     | [plate, insert, backing] | 4140 |
     | [backing] | A36, 1018 |
     | [plate, insert, stop] | H13 |
     | [insert, stop] | S7, A2 |
     | [insert] | 17-4PH |

   - `isDefault`: true for 6061-T651 and for P20. P20 is the default both for steel plates and for inserts.
   - `swMaterial`: the SOLIDWORKS library name, or null. "6061-T6 (SS)" is verified (v1). Spike S10 fills in the rest by listing the library on the user's PC.
2. **Fatigue (D18).** 6061 `fatigue_MPa` becomes `{"1e5": 142, "1e6": 111, "1e7": 101, "5e8": 96.5, "basis": "Engineers Edge fit S = 14479/sqrt(N) + 96.5 MPa (R=-1, smooth); lower than MMPDS-approx 185/130/105 - verify before release"}`.
   - The steel S-N values in the file are raw Shigley estimates and stay for information only. The calculator works out steel fatigue from `uts_MPa` (3.6).
3. **Units in the file:** E in GPa, strengths in MPa, CTE in 1/K, density in kg/m³. The `retained` factors are fractions of the 20 °C value at `temps_C` [100, 150, 200, 250]. The calculator uses `yield_long_10000h`, `uts_long_10000h` and `E`.

#### 1.5.3 data/shop-standards.json (the user's rules)

Each length is `{"in": x}` or `{"mm": x}`; the loader accepts either. Values are the user's stated rules (N/existing-mold-knowledge.md 1.2-1.3) plus N/design-rules.md where marked:

```json
{
  "schema": "unify3d.mold-shop/1",
  "flash": {
    "styleClassA": "moat",
    "lip": { "in": 0.125 }, "grooveWidth": { "in": 0.5 }, "grooveDepth": { "in": 0.125 },
    "runnerPitch": { "in": 2.0 }, "runnerPastEdge": { "in": 0.1 }, "cornerRadius": { "in": 0.25 },
    "lipCornerRule": "min(0.25 in, cavity corner R + lip)",
    "featureClearance": { "in": 0.25 }, "separateMoatGap": { "in": 1.5 },
    "partialMoatWidth": { "in": 0.25 }, "noDeadEnds": true,
    "seams": "runners leave only through the assembled outer edge, may cross a seam at 90 deg, never run along one (>= 0.25 in); seam edges not filleted"
  },
  "plates": { "roundLW": { "in": 0.5 }, "roundT": { "in": 0.125 }, "warnBelow": { "in": 0.25 },
              "blankAllowancePerSide": { "mm": 3 }, "skimPerFace": { "mm": 1.5 } },
  "dowels": { "diameter": { "in": 0.5 }, "inset": { "in": 0.625 }, "keyOffset": { "in": 0.25 }, "count": 4 },
  "prySlots": { "width": { "in": 1.0 }, "reach": { "in": 0.375 }, "depth": { "in": 0.125 } },
  "chamfer": { "size": { "in": 0.06 }, "angleDeg": 45, "where": "base bottom outer edges and lid top outer edges" },
  "thermocouple": { "diameter": { "in": 0.25 }, "depth": { "in": 2.0 }, "endFromCavity": { "mm": 9 }, "perPiece": 1 },
  "handlesAbove": { "kg": 5 },
  "pour": { "secondsPerCavity": 60, "holdUpPct": 5 },
  "customProperties": [ "MoldNumber", "Resin", "CureTime_min", "PressTemp_F" ],
  "materialFallback": "6061-T6 (SS)",
  "designLibrary": "W:\\Solidworks-library\\library-features"
}
```

#### 1.5.4 data/sizing-defaults.json (judgement factors, one place)

Every value is a default. `[K]` marks practice, `[C]` calculated, `[S]` published. The compiled `SizingDefaults` holds the same values, and a unit test asserts they are equal.

```json
{
  "schema": "unify3d.mold-sizing/1",
  "sfStatic": 2.0, "sfFatigue": 1.5,
  "bearingDivisor": { "highCycle": 3.0, "lowCycle": 2.0, "highCycleFrom": 100000 },
  "wallRootFactor": 1.2, "lateralPressureFactor": 1.0, "tauMax": 0.5,
  "deltaFlash_mm": { "liquidBelow1000cP": 0.012, "liquid1000to10000cP": 0.020, "liquidAbove10000cP": 0.025,
                     "paste_lsr": 0.005, "rubber": 0.050, "compound": 0.025, "melt": 0.025, "no_flow": 0.025 },
  "deltaFromTolerance": 0.25,
  "classA": { "pressureFactorOnTableMax": 1.5, "minDesignPressure_MPa": 0.35, "closeForceReserve": 1.25 },
  "thermosetDepthAdder_MPa_per_25mm": 4.1,
  "pressReserve": 1.25, "excessForceFlag": 1.5, "offCentre": 0.05, "platenEdgeMargin_mm": 25, "daylightMargin_mm": 25,
  "minFloor_mm": { "aluminium": 12, "steel": 10 }, "minFloorDepthFraction": 0.25,
  "minPlateFractionOfLongestSide": 0.05, "minPlate_mm": { "aluminium": 12, "steel": 10 },
  "minWallClassB_mm": { "aluminium": 15, "steel": 10 }, "steelAroundTcWell_mm": 6,
  "platenCoverageForCaseA": 0.8,
  "bolts": { "preloadSteelJoint": 0.75, "preloadAluminiumJoint": 0.60, "hotMaxOfProof": 0.90, "separationMin": 2.0,
             "loadFactorMin": 1.5, "embedmentLossAluminium": 0.10, "kDry": 0.20, "kLubed": 0.15,
             "edgeDistanceD": 1.5, "minSpacingD": 4.0, "maxSpacingD": 6.0, "insertAboveLeD": 2.5,
             "alphaBolt_perK": 1.17e-5, "enduranceInchA574_MPa": 160, "enduranceMetric129_MPa": 190, "headBearingOfUts": 0.9 },
  "fatigue": { "kaAluminium": 0.9, "kbSteel": 0.80, "keSteel": 0.868, "qAluminium": 0.8, "qSteel": 0.95,
               "ktByFilletRatio": [ [0.2, 1.5], [0.1, 1.8], [0.05, 2.2], [0.0, 2.5] ], "ktHoleInBentPlate": 2.5 },
  "keys": { "minStep_mm": 9.5, "bearingSf": 2.5, "shearSf": 2.0 },
  "pins": { "lateralImbalance": 0.5, "insertIfDiameterBelow_mm": 3.0, "insertIfHeightOverD": 2.0, "insertIfSteelWallBelow_mm": 3.0 },
  "thermal": { "alphaRefSteel_perK": 1.2e-5, "lidBaseGradient_K": 10, "runningClearance_mm": 0.02, "pinEngagementBeforeEntry_mm": 12 },
  "cycleTiers": [ { "upTo": 500, "tier": "T1" }, { "upTo": 10000, "tier": "T2" }, { "upTo": 100000, "tier": "T3" }, { "upTo": 1e12, "tier": "T4" } ],
  "splitClearanceFromCavity_mm": 15
}
```

#### 1.5.5 Shop overrides

Location: `%APPDATA%\SwAgent\tools\builtin-data\design-compression-mold\` (`UserTools.Root`). It holds files with the same names as in `data/`.

How the override is merged:
- **`rows` and `mold_materials`:** a row with the same `id` replaces the shipped row. New rows are added at the end.
- **Objects** are merged key by key.
- **Everything else** comes from the shipped file.

What reads the override:
- `ToolData` (the page);
- `MoldData` (the calculator);
- `mold_record save_default`, which writes it (2.3.5). Example: "save 2.1% as my EPDM 70A shrink".

The override survives every reinstall, because built-in folders are replaced (UserTools.cs:52-58).

#### 1.5.6 data/setup-sheet.html

A template with `{{path.in.record}}` placeholders and `{{#each bodies}}` blocks. The C# filler is in 2.3.5 and needs no library. Sections follow N/design-rules.md 23:

| # | Section |
|---|---|
| 1 | Mold id, revision, part number, the view image and the cavity map |
| 2 | Piece list: mass in kg and lb, how it is lifted, lift-point sizes |
| 3 | Assembly order: dowels, bolts and torques (dry and lubricated), stop shims |
| 4 | Press: platen set point, gauge setting with how it was converted, maximum gauge, closed height, load-centre mark |
| 5 | Preheat: time, and the TC readings needed |
| 6 | Material: mix ratio, temperatures, degassing, pot life, charge per cavity and per shot with tolerance, preform |
| 7 | Pour or charge sequence: tilt, wait before closing, bump schedule |
| 8 | Cure time, with the halving-per-10 °C note |
| 9 | Demold sequence |
| 10 | Release agent and cleaning |
| 11 | Post-cure |
| 12 | First-article checks |
| 13 | Safety, including the material's `hazards` |
| 14 | Maintenance by cycle tier |

Every number is printed in both unit systems.

### 1.6 macro.cs (gathering macro)

The tool is `agent: true`, so the macro's printed output reaches the agent as `macroOutput` (ToolRunner.cs:153, 160). It is cut at 6,000 characters (store.ts:315). The macro makes no change to the model. Steps:
1. If the active document is not a part: `Fail("Open a part (a blank part or your mold file) first")`.
2. If the part has solid bodies: run `await Agent.CallToolAsync("mold_part", new { action = "analyze", body = "auto", record = false })` and print its summary, at most 5,000 characters.
3. Otherwise print "No solid body yet - the molded part is in part_file" and print the file name and size.
4. Print the unit system (`IsImperial`) and whether the part has been saved.

This saves the agent 2-3 round trips in Phase 0-1, and the first thing it reads is real data.


---------------------------------------------------------------------------------------------------------------------

## 2. Add-in changes

Every new behaviour is opt-in through new optional properties. Existing tools render and run exactly as today: Smart Rebuild, and the user's flash-grooves, parametric-block and pu-compression-mold. A test proves it (6.2).

### 2.1 PropertyManager page extensions

#### 2.1.1 New spec properties: `ToolInputSpec` (R/src/SwAgent/Tools/ToolSpec.cs:14-62)

Each property gets a `[ToolParam]` text. That text is also the schema the agent sees when it writes user tools (UserTools.cs:156, 241), so the wording matters.

| Property | C# type | Applies to | Meaning |
|---|---|---|---|
| `ChoicesFrom` | string | choice | `"<relative path>#<top-level key>"` names a JSON array of row objects in the tool folder. Without `#`, the file root must be an array. |
| `ChoiceKey` | string | choice | The row field shown as the entry. Values are distinct and keep file order. |
| `DependsOn` | string | choice | The parent input id. The parent must be declared earlier; `LoadInputs` warns otherwise. The parent may be a choice or options input. |
| `RowFilter` | Dictionary<string,string> | choice | Field → pattern. `{id}` is the parent's current value after `ValueMap`. When the row field is an array, it matches by containment. Matching is case-insensitive. |
| `ValueMap` | Dictionary<string,string> | choice | Maps the parent's display value before substitution, e.g. "Tool steel" → "steel". |
| `Fills` | Dictionary<string,string> | choice | Target input id → dotted path in the selected row (`"shrinkPct.typ"`). The values are in the target's spec unit. A null value hides the target. |
| `Optional` | bool? | number, size | Adds a checkbox whose caption is the label. Unchecked means "not set": the value is null and the payload says "not set". |
| `Enabled` | bool? | number, size | The checkbox's starting state (default true). |
| `ShowWhen` | Dictionary<string,string[]> | any | Visible only while each named input's value is one of the listed values. A hidden input is left out of the payload. |
| `Remember` | bool? | number, integer, choice, options, checkbox, size | The default becomes the last value confirmed with OK. Stored in spec units. |
| `Dims` | int? | size | 2 or 3 length boxes. |
| `Text` | string | info | The caption template. `{id}` is an input's value. `{id.path}` is a field of that choice's selected row. `{...|unit}` formats the value in the page's display system. |
| `GroupExpanded` | bool? | any | The first input in a group decides whether the group starts expanded (default true). |

New `Type` values: `size` and `info`. New `Unit` values: `pressure`, `temperature`, `mass`, `force`, `tonnage`, `viscosity`, `time`. The existing `length`, `angle`, `percent` and `none` keep their meaning. Today an unknown unit renders as a unitless box (ToolPage.cs:250-251); that does not change.

`ToolSpec` (ToolSpec.cs:65-127) gains these fields:
- `public string Dir`: set in `Load`. Today `dir` is computed at line 102 and thrown away.
- `Category`, `RibbonTitle`, `LongRun` (bool), `MaxSteps` (int: default 200; default 600 when `long_run` is set; clamped to 50-1000), `Requires` (string[]).

`LoadInputs` (129-142) adds warnings for:
- a `DependsOn` that is missing or comes later in the file;
- a `ChoicesFrom` file that doesn't exist;
- a `Fills` target id that doesn't exist.

#### 2.1.2 Units: new file R/src/SwAgent/Tools/Units.cs

`internal sealed class UnitDef { string Quantity, Symbol; double ToSi, Offset, Inc, Fast, Slow; }`. Conversions:
- display → SI: `si = v * ToSi + Offset`
- SI → display: `v = (si - Offset) / ToSi`

Members: `Units.For(string unit, bool imperial)`, `Units.SpecToSi(string unit, double spec)`, `Units.Describe(ToolInputSpec s, double si, bool imperial)` (gives "150 psi (1.034 MPa)"), and `Units.IsImperial(IModelDoc2 m)`.

| Unit | Spec unit (Default, Min, Max, data files) | `ToolInputs.Number` returns | Metric display | Imperial display | Exact factor | Inc / Fast / Slow (display) |
|---|---|---|---|---|---|---|
| length | mm | m | native box, document units | native box | 0.001 | 1 / 10 / 0.1 mm (as today) |
| angle | deg | rad | native | native | π/180 | as today |
| percent | % | % (0-100) | % | % | 1 | 0.1 / 1 / 0.01 |
| pressure | MPa | Pa | MPa | psi | 1 psi = 6,894.757293168 Pa | MPa 0.1/1/0.01; psi 10/100/1 |
| temperature | degC | degC | degC | degF | °F = °C x 1.8 + 32 (offset handled) | 1 / 10 / 0.5 |
| mass | kg | kg | kg | lb | 1 lb = 0.45359237 kg | 0.5/5/0.1 kg; 1/10/0.1 lb |
| force | kN | N | kN | lbf | 1 lbf = 4.4482216152605 N | 1 / 10 / 0.1 |
| tonnage | kN | N | t (tonne-force) | US ton | 1 tf = 9.80665 kN; 1 US ton = 2,000 lbf = 8.896443230521 kN | 1 / 10 / 0.5 |
| viscosity | cP | Pa·s | cP | cP | 1 cP = 0.001 Pa·s | 100 / 1000 / 10 |
| time | min | s | min | min | 60 | 1 / 10 / 0.5 |
| none | as typed | as typed | (unit in label) | (unit in label) | 1 | 1 / 10 / 0.1 |

**Imperial or metric.** Read `Extension.GetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitSystem, (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified)` (the same call as SwModelLoader.cs:135).
- `swUnitSystem_IPS` means imperial.
- `Custom` is imperial when `swUnitsLinear` is inches, feet, feet-inches, mil or micro-inch, or when `swUnitsMassPropMass` is pounds.
- Everything else is metric.

No page-level "Units" switch in v1. Spike S2 has to show that `SetRange2` works after `Show2` first. The user changes the document units instead.

#### 2.1.3 Data-backed choices: new file R/src/SwAgent/Tools/ToolData.cs

How it loads:
- `ToolData.For(ToolSpec spec)` is cached per page.
- It loads `Path.Combine(spec.Dir, file)`.
- For built-in tools it also merges `UserTools.Root\builtin-data\<tool>\<file>` by the rules in 1.5.5.

Members:
- `List<JObject> Rows(string choicesFrom)`.
- `List<string> Entries(Field f, IReadOnlyDictionary<string,string> values)`: filtered rows, then distinct `ChoiceKey` values in file order.
- `JObject Selected(Field f)`.
- `JToken Path(JObject row, "a.b.c")`.
- `string Render(string template, ...)`: for `info` captions, with `|unit` formatting.

No SolidWorks types, so the whole class is unit-tested headless.

**Default entry of a dependent choice**, in this order:
1. the remembered value, if it is still among the entries;
2. `Default`, if it is among them;
3. the row with `isDefault: true`;
4. the first entry.

#### 2.1.4 ToolPage.cs changes (R/src/SwAgent/UI/ToolPage.cs)

| Where | Change |
|---|---|
| `Field` (67-79) | Add: `IPropertyManagerPageLabel Label`; `IPropertyManagerPageCheckbox Toggle` (for Optional); `IPropertyManagerPageNumberbox[] Boxes` (for size); `List<string> Items` (current combobox entries; the combobox has no ItemCount); `UnitDef Unit`; `double? TableSi`; `bool Hidden`. |
| New fields | `Dictionary<string, Field> _byInputId`; `int _quiet` (re-entrancy guard; every handler returns at once while it is above 0); `ToolData _data`; `bool _imperial`. |
| `Build` (157-189) | <ul><li>Take group ids from `_nextId++`, not from `groupId = 1` (line 160). This fixes the collision with control ids, which start at 10 (line 53); the mold page has 7 groups.</li><li>Honour `GroupExpanded`.</li><li>Load Remember values first.</li><li>After all fields exist, run once: `Cascade` for root choices, then `ApplyPreset`, `ApplyVisibility`, `RefreshInfo`.</li><li>All controls must exist before `Show2`, so every field is created and later hidden when not needed (N/pm-page.md 3).</li></ul> |
| `AddLabel` (207-211) | Return the label control; store it in `Field.Label`. |
| `AddField`, number (236-257) | **Converted units:** `SetRange2(swNumberBox_UnitlessDouble, display(Min), display(Max), true, Inc, Fast, Slow)` and the label caption "`Label` (symbol)". **Optional:** a checkbox in place of the label; `Enabled` on the box follows it. Length and angle stay native. |
| `AddField`, new `size` | One label (or a checkbox, when Optional), then `Dims` length boxes with picture labels Width, Depth and Thickness1 (`swBitmapLabel_*`, already mapped in `PictureFor`, 786-811). Boxes stack vertically; side-by-side layout waits for spike S4. |
| `AddField`, new `info` | A label whose `Caption` is `RefreshInfo()` (captions can change after creation). |
| `AddField`, choice (267-278) | With `ChoicesFrom`: entries come from `_data.Entries`; `f.Items` keeps them. |
| `OnComboboxSelectionChanged` (587-595) | `if (_quiet>0) return;` keep the tier box as it is, then `Cascade(f); ApplyPreset(f); ApplyVisibility(); RefreshInfo(); Changed();`. |
| `OnOptionCheck` (584) | Same cascade as a combobox, because options inputs can be parents. |
| `OnCheckboxCheck` (583) | An Optional checkbox enables its box(es), then `ApplyVisibility()`. |
| `OnNumberboxChanged` (581) | Return when `_quiet > 0`. |
| New `Cascade(Field parent)` | For each child: `Quiet(() => { box.Clear(); box.AddItems(entries); box.CurrentSelection = keepOrDefault; })`, then recurse, then `ApplyPreset(child)`. Uses `IPropertyManagerPageCombobox.Clear/AddItems/CurrentSelection`. |
| New `ApplyPreset(Field f)` | For each `Fills` pair: read the row value. Null hides the target. Otherwise convert spec → display and `Quiet(() => box.Value = display)`, and remember `TableSi`. At `Build`, a field with a remembered value is not overwritten. A user-picked grade does overwrite: that is an explicit action. |
| New `ApplyVisibility()` | `ShowWhen` plus the Fills-null hiding. Sets `IPropertyManagerPageControl.Visible` on the label, the checkbox and the control(s). |
| `Collect` (368-437) | <ul><li>Choice reads `f.Items[i]` instead of `Spec.Choices` (413-420), so dynamic lists return the right value.</li><li>Converted numbers are stored as SI.</li><li>Optional unchecked stores null.</li><li>Size stores `double[]` in metres.</li><li>Hidden inputs are skipped.</li><li>Display strings, table values and the selected row id go into `ToolInputs` side tables.</li></ul> |
| `RequiredSatisfied` (439-452) | A required choice needs a selection and at least one item. A required Optional must be checked. When OK is disabled, show `ShowBubbleTooltip` on the offending control. |
| `OnClose` OK (545-560) | Write the Remember values (spec units) to `UserTools.Root\last-inputs\<tool>.json`. Files, selections and text are never stored. |
| `TryFromSelection` (664-724) | Same defaults and data resolution for the new types. It is used by `ToolTester` with `forTest` (ToolTester.cs:51, 89), even though this tool always shows its page. |
| `DefaultNumber` (756-762) | Spec → SI through `Units`. |
| `AcceptText` and `FileFilter` (813-829) | `drawing` adds "SolidWorks drawings (*.slddrw)" and "DXF/DWG (*.dxf;*.dwg)". |

#### 2.1.5 Values to the macro and the agent

`ToolInputs.cs` (R/src/SwAgent/Scripting/ToolInputs.cs) adds:
- `double? Limit(string id)`: null when Optional is unchecked;
- `double[] Size(string id)`: in metres;
- `JObject Row(string id)`: the selected data row;
- `string Display(string id)`;
- `double? Table(string id)` (in SI);
- `bool Edited(string id)`;
- `bool Hidden(string id)`.

The doc comment at lines 11-12 changes to: "Numbers are SI: m, rad, Pa, degC, kg, N, Pa·s, s; percent stays 0-100."

`ToolRunner.BuildPayloadAsync` (ToolRunner.cs:217-266):
- The C# side formats every value string, so the panel's `stageToolPrompt` (store.ts:272-324) needs no change: it prints strings as they come (305-306). Each entry also gets structured `si`, `table` and `row` fields for later use. Today's UI ignores them.
- New `integer` and `size` cases.
- The top-level payload adds `"maxSteps"` and `"longRun"` from the spec (2.2).

What the agent sees:

```
- Plate grade (mold_grade): 6061-T651 [al_6061_t651]
- Grade (part_grade): TDI-polyether / MBOCA 90A [pu-tdi-ether-90a; data confidence medium; sources S19, S20, S3, K-PU]
- Shrinkage (shrink_pct): 1.8 % (table 1.5 % - edited by the user)
- Mixed viscosity (viscosity_cp): 1200 cP (1.2 Pa*s)
- Mooney viscosity (mooney): n/a for this material
- Press pressure (press_pressure): 2000 psi (13.79 MPa)
- Press temperature (press_temp): 212 degF (100 degC)
- Press rated force (press_rated_force): 50 US ton (444.8 kN)
- Heated platen size (platen_size): not set (check skipped)
- Max size per plate (max_plate_size): 23.62 x 15.75 x 5.906 in (600 x 400 x 150 mm; compare sorted)
- Max total base weight (max_base_mass): not set
- Cycle life (parts per cavity) (cycle_life): 5000 (tier T2: 500-10,000)
```

Also add one sentence to `systemPrompt.ts:69`, so the agent can use the new options in the user's own tools: units, Optional, size, info, ChoicesFrom/DependsOn/Fills, ShowWhen, Remember.

#### 2.1.6 Page spikes (run in SolidWorks 2026 before building; each takes under an hour)

| Id | Question | Pass condition | If it fails |
|---|---|---|---|
| S1 | Does setting `Value` or `CurrentSelection` from code fire `OnNumberboxChanged` / `OnComboboxSelectionChanged`? | The `_quiet` guard works either way. Record which. | none needed |
| S2 | Do `Combobox.Clear/AddItems` and `Numberbox.SetRange2` work after `Show2`, without flicker or a crash? | Entries and range update. | Make one combobox per family and toggle `Visible` (96 rows give 38 families: acceptable) |
| S3 | Does `Visible=false` collapse the space? Do `Label.Caption` changes repaint? | Yes | Keep the space, disable instead |
| S4 | Units of `Top/Left/Width`, for side-by-side size boxes | Known | Stack vertically (v1 default) |
| S5 | Does `IBody2.set_Name` persist after a rebuild and save? | It persists | Folders plus geometric order (D3, the default) |
| S6 | A 10-group test page | No id collision after the fix | none needed |

### 2.2 Categories, ribbon title, long runs, `requires`

| File:line | Change |
|---|---|
| UserTools.cs:522-564 `ListTools` | Emit `category` (`OneLine`, or null), `ribbonTitle`, `longRun`, `maxSteps` and `requires`. |
| UserTools.cs:133 `SaveInput`, :217 `EditInput` | New `[ToolParam("Optional category: a box on the SolidWorks Agent tab and a section in the Workflows menu, e.g. \"Molding & Toolmaking\". Reuse an existing name.")] public string Category`. `Save` (309) writes `category:` after `icon:`. `Edit` (369) calls `SetHeader(header, "category", ...)` in the 403-409 block, and adds `Category` to the "nothing to change" test. |
| AgentController.cs:336 | Allow `category` in the `userTools.setValue` whitelist. For built-ins it goes to builtin-settings.json; `EffectiveFrontMatter` applies it (UserTools.cs:98-106). |
| AgentRibbon.cs:60-64 `Signature()` | Append `:{category}:{ribbonTitle}`, so re-categorising marks the ribbon stale. |
| AgentRibbon.cs:97-101 | Button title = `ribbonTitle ?? title`. Hint = `"[" + category + "] " + description` when a category is set. |
| AgentRibbon.cs:133-141 boxes | Build the boxes as: fixed commands + Options, then one box per built-in category (first-seen order), then uncategorised built-ins, then one box per user category (a user tool in a built-in category's name joins that box), then uncategorised user tools + New Tool. Tab boxes have no captions, so the category shows only in the hint. No flyouts in v1. Command ids stay keyed by name (`StableIds`, 186-216), so hotkeys survive. |
| ToolRunner.cs, after line 62 | `requires` check. If a listed agent tool is missing from `_tools.Names`, show the status "SolidWorks Agent ▸ {title}: needs a newer add-in (missing x, y)" and return. |
| ui/src/lib/types.ts:269-297 `UserToolInfo` | Add `category?: string \| null; ribbonTitle?: string; longRun?: boolean; maxSteps?: number`. Add the same two last fields to `ToolRunPayload` (252-267). |
| ui/src/components/ToolsPalette.tsx:24-27, 119-143 | One section per category, titled with the category: built-ins first (Run only, `row(t, true)`), then the user's. Then the existing three uncategorised sections, unchanged. The group key is the trimmed, case-insensitive category; the first spelling seen is shown. |
| ui/src/agent/systemPrompt.ts:154, 162-164 `toolIndex` | The line becomes `- design-compression-mold (built-in tool, Molding & Toolmaking): ...`. |
| systemPrompt.ts:51 and :88 | Add: "When a tool's TOOL.md defines phases, gates or a report (long tools), follow its reporting rules instead of the one-line finish and the reply-length preference." |
| ui/src/store.ts:1564-1568 `runQuickTool` | `RunOverride` adds `maxSteps: payload.maxSteps ?? 200, longRun: payload.longRun`. For `longRun`, also set `thread.toolRun = { tool, label, model, effort, maxSteps, longRun, startedAt }`. |
| store.ts:618-621 (send path for follow-ups) | While `thread.toolRun` is set, a typed follow-up uses its model, effort, `autoApprove` and `maxSteps`. Today a continued max-level run quietly drops to the chat's model and starts asking for approval. It ends when a different tool runs in that thread, when the user closes the chip in `ToolRunBanner.tsx`, or after 24 h. |
| ui/src/agent/runAgent.ts:207 and :345 | The iteration limit comes from `opts.maxSteps` (default 200, cap 1000). |
| runAgent.ts:239 | `cache_control: opts.longRun ? { type: "ephemeral", ttl: "1h" } : { type: "ephemeral" }`. 1-hour writes cost 2x instead of 1.25x. Reads refresh the timer, and Opus 5.5 reads cost 0.05x. Gate questions are answered after 5-60 min, which is exactly where the 1-hour TTL pays off. |
| ui/src/lib/mockHost.ts:113-127 | Give the sample tool a category, so the palette can be checked in the browser mock. |

### 2.3 New agent tools and helpers

#### 2.3.1 Library layout (all new; namespace `SwAgent.Mold`)

```
src/SwAgent/Mold/
  MoldData.cs                 loads data/*.json from the built-in folder plus the overrides; typed views: PartMaterial, MoldGrade,
                              Fastener (inch/metric), InsertSpec, DowelSpec, StockList, ShopStandards, SizingDefaults
  ProcessClass.cs             enum A_Pour, B_Compression, C_Positive; From(processType)
  Materials/AtTemperature.cs  E(T), Sy(T), Su(T), service status (3.4)
  Process/Press.cs            S1-S9      Process/Shrink.cs  S64-S65      Process/Charge.cs  S66-S70
  Structural/Fatigue.cs       S15-S19    Structural/Coefficients.cs (embedded coefficients.csv, bilinear in aspect and tau^2)
  Structural/Walls.cs S20-S25  Plates.cs S26-S32  Bearing.cs S33-S36  Keys.cs S37-S41  Bolts.cs S42-S53
  Structural/Threads.cs S54-S56  Fits.cs S57-S60  Pins.cs S61-S62
  Structural/Fe/PlateModel.cs  Mitc4.cs  BandedLdlt.cs  MindlinPlateSolver.cs  Navier.cs  Recovery.cs   (2.4)
  Layout/CavityLayout.cs  Layout/PlateSplit.cs (S74)  Layout/StockRounding.cs (S75)
  Sizing.cs                   design JSON -> results + checks (pure; the only entry the tool uses)
  Record/MoldRecord.cs        schema unify3d.mold/1: init, read summary, deep merge, validate, paths
  Record/SetupSheet.cs        fills data/setup-sheet.html
  Geometry/MoldFrame.cs       Top Plane (2nd RefPlane) -> U, V, W frame                         [uses SolidWorks]
  Geometry/Tessellation.cs    face triangles and normals in the mold frame                      [SolidWorks]
  Geometry/PartAnalysis.cs    draft, undercut rays, projection raster, outline, PL candidates, fragile features [SolidWorks]
  Geometry/AirTraps.cs        ray heightmap [SolidWorks] + priority-flood [pure]
  Geometry/BodyReport.cs      per-body role, mass, box, limits, interference, clearance        [SolidWorks]
  Geometry/BodyExport.cs      STEP per body                                                     [SolidWorks]
  Geometry/FlashSystem.cs     port of the user's flash-grooves macro, plus per-body fillets     [SolidWorks]
  Geometry/MoldApi.cs         the `Mold` helper object for macros and execute_csharp cells       [SolidWorks]
src/SwAgent/Tools/MoldTools.cs    [AgentTool] mold_calc, mold_part, mold_bodies, mold_record
```

Everything above the Geometry line has no SolidWorks types and is unit-tested headless. Registration: in `AgentAddIn.OnConnect`, after line 65 (`tools.RegisterAll(new RebuildTools());`), add `tools.RegisterAll(new MoldTools());`. The panel fetches tool definitions once at boot (store.ts:1013) and sends all of them with every request, cached. Keep each description under 400 characters and start it with "For the design-compression-mold tool:". The four tools add about 2k tokens to the cached prefix.

**Registry fix needed first.** `ToolRegistry.SchemaFor` (ToolRegistry.cs:206-224; the IsClass branch is line 219) would turn a `JObject` property into a schema of JObject's own CLR properties (Type, HasValues, First...). That happens because `JObject` is a class and not generic. Add before the `IsClass` branch:

```csharp
if (typeof(Newtonsoft.Json.Linq.JToken).IsAssignableFrom(t))
    return new JObject { ["type"] = t == typeof(Newtonsoft.Json.Linq.JArray) ? "array" : "object" };
```

#### 2.3.2 `mold_calc` (pure calculator; read-only on the model)

Input POCO `MoldCalcInput`:

| Field | Type | Meaning |
|---|---|---|
| `action` | string, required | `lookup`, `process`, `press`, `layout`, `size`, `split` or `check` |
| `design` | JObject | Optional design in SI, with the same keys as the record (`inputs`, `materials`, `process`, `part`, `layout`, `structural.inputs`; 4.3). Omit it to use the active document's mold record. |
| `query` | JObject | For lookup: `{part:"<id>"}`, `{mold:"<id>", temp_c:120}`, `{fastener:"1/2-13"\|"M10"}` or `{stock:"6061_T651_plate"}` |
| `overrides` | JObject | One call's changes to sizing defaults or support, e.g. `{delta_allow_mm:0.02, support:"rails", rail_span_mm:80, kf:1.5}` |
| `record` | bool? | Write the results to the mold record. Default true for process, press, layout, size, split and check. |

What each action does:

| Action | Formulas (section 3) | What it returns, and what it writes to the record |
|---|---|---|
| lookup | | The row, or the material at temperature, as compact text. No table dumps, which keeps the context small. |
| process | S64-S70 | Scale factors, charge per cavity and per shot, overflow volume needed, groove radius, pour time against pot life, cure at T, loading well. Writes `process` and `shrink`. |
| press | S1-S9, S35 | Ram area, force, cavity pressure per class, required force, recommended and maximum gauge, off-centre, platen and daylight fit. Writes `process.gauge` and `structural.loadCases`. |
| layout | 3.16 rules | Cavity origins, footprint, moat style (separate or combined), cavity numbering. Writes `layout`. |
| size | S10-S62, S71-S75 | Walls per cavity side, floor and lid thickness, spans and supports, bearing and stop pads, keys, bolt groups, threads and inserts, fits, pins, piece masses and boxes, all rounded to stock. Then the plate FE check of the planned layout (under 1 s). Writes `structural` and `checks[]`. |
| split | S74 | Up to 3 split options with piece masses and boxes, keys, bolts. Writes nothing until the agent merges the chosen option. |
| check | as for size, with the as-built geometry | Reads `bodies[]` from the record: actual thicknesses, walls measured by `mold_bodies`, plus the pressure field if a simulator sent one (H3). Writes `checks[]` and the source. |

Output format:
- a text table, lowest margins first;
- each failing check with the action that fixes it;
- one fenced JSON block of up to 4,000 characters with the key numbers.

`Sizing` is deterministic. The same input gives the same output (unit-tested), so `check` can be re-run safely.

#### 2.3.3 `mold_part` (read-only analysis of the molded part)

Input: `action` ("analyze", the only action in v1 and the default), `body` ("auto", meaning the Insert Part body, or PART-REF, or the largest solid; or a body name), `pull` (double[3]; default +W), `fill` ("base", "lid" or "none"), `tilt_deg` (0), `min_draft_deg` (default: the page value), `sample_mm` (default 1.0), `critical` (`[S#]` refs), `show` (draw the overlay), `record` (default true).

The method (frame W = pull):
1. **Tessellation.** Per face, `IFace2.GetTessTriangles(true)` and `GetTessNorms()`, mapped into the frame.
2. **Draft.** Per triangle, `a = asin(n·W)` in degrees. Classes per face:
   - positive: every triangle has a ≥ +min;
   - negative: every triangle has a ≤ -min;
   - needs draft: more than 5% of the face's area has |a| < min;
   - straddle: both signs at least min.
   Areas come from `IFace2.GetArea()`.
3. **Undercuts.** Samples are area-weighted triangle centroids, at most 20,000 per call, offset 0.01 mm along the outward normal. Rays go along +W and -W:

   ```
   Extension.RayIntersections(new object[]{body}, pts, dirs,
       (int)(swRayPtsOpts_e.swRayPtsOptsTOPOLS | swRayPtsOpts_e.swRayPtsOptsNORMALS), 1e-7, 0, true)
   ```

   Hits are read with `GetRayIntersectionsPoints()`. Rays run in batches of 2,000, and `Ct` is checked between batches.
   - **lid-demoldable:** n·W ≥ sin(min) and the +W ray is clear;
   - **base-demoldable:** n·W ≤ -sin(min) and the -W ray is clear;
   - **needs draft:** |n·W| < sin(min);
   - **undercut:** anything else.
   The result is reported per face, as area fractions. Never run it from a timer (SceneOverlay.cs:296-301).
4. **Projection.** Rasterize the projected triangles on a UV grid with cell h = clamp(L_min/400, 0.1 mm, 1.0 mm), where L_min is the smaller footprint size.
   - Projected area = cells x h².
   - The outline comes from marching squares and is simplified (Douglas-Peucker, tolerance h/2). This gives the perimeter.
   - Repeat per cavity when several bodies are given.
5. **Planar parting-line candidates.**
   - z_lo = the largest top W among faces that are not lid-demoldable.
   - z_hi = the smallest bottom W among faces that are not base-demoldable.
   - If z_lo ≤ z_hi, a planar parting line can sit anywhere in [z_lo, z_hi]. Propose z_lo (flat lid), z_hi, and the level of the largest outline.
   - Otherwise the job needs a stepped parting line or side action. List the faces in conflict.
   - Score each candidate: critical-face contact (veto), closure dimensions crossing it, depth per half, and air traps.
6. **Air traps.** Cast vertical rays per UV cell (spacing `sample_mm`) to get the part's W intervals per column. The upper envelope c(u,v) is the ceiling map; it is tilted by `tilt_deg` about the U axis.
   - Outlets: cells whose ceiling sits on the parting plane at the part outline, plus recorded vents and risers.
   - Priority-flood (Barnes, Lehman, Mulla 2014) runs on h = -c from the outlets. The filled cells are the trapped air.
   - Each trap gives: its highest point (where the vent goes), its volume = Σ (fill - h) x h², and its area.
   - A ceiling inside a column (a lower interval's top) is reported as "internal ceiling - side core or check by hand".
   - The flood is pure and is unit-tested on synthetic heightmaps.
7. **Fragile features.**
   - Cores are steel regions inside the outline, from the column intervals. Their width is twice the inscribed radius (distance transform) and their height is their W extent. A core is flagged when D < 3 mm or H/D > 2.
   - Steel walls between part regions under 3 mm are flagged too (3.14).

Output:
- volume (mm³), area, box, projected area and perimeter, mass at SG;
- draft table, undercut faces, PL candidates with scores, air traps, fragile features;
- persistent refs (`GetPersistReference3`) for every face listed.

With `show`, the overlay colours from 1.3 are drawn.

#### 2.3.4 `mold_bodies` (read-only, plus export)

Input: `action` (report, clearance or export), `pairs` (string[][]: feature or body names, for clearance), `min_clearance_mm`, `format` ("step" or "x_t"), `step_ap` (214 or 242, default 214).

**report**
- Bodies: every solid body from `IPartDoc.GetBodies2((int)swBodyType_e.swSolidBody, false)`.
- Role: its "Mold - *" folder (`IBodyFolder.GetBodies`). Id: within a role, sorted by centroid U, then V.
- Volume: `IBody2.GetMassProperties(1.0)[3]`, in m³ converted to mm³.
- Mass = ρ(grade of the role) x V. This does not depend on a SolidWorks material assignment.
- SolidWorks material name: `GetMaterialPropertyName(cfg, out db)`.
- Box: six `GetExtremePoint` calls along ±U, ±V and ±W. `GetBodyBox` is not tight on curved bodies.
- Limit checks: S72-S73.
- Lid total = Σ masses above the highest parting plane. Base total = Σ below the lowest parting plane. Middle plates are reported on their own.
- Interference: every pair of mold bodies through `IModeler.CheckInterference3(new object[]{a}, new object[]{b}, 4 /*ReturnInterferingObject*/, out f1, out f2, out bodies)`. A pair is reported when its volume is over 1 mm³ and it is not a recorded fit.

**clearance**
- For each pair: faces from `IFeature.GetFaces()` or the body's faces.
- Pre-filter by bounding box, then `IModelDoc2.ClosestDistance(faceA, faceB, out p1, out p2)`. At most 400 face pairs per call.
- Returns the minimum distance and its points, drawn as `Overlay.Dimension`.

**export**, per body:
1. `IBody2.Copy2(true)`.
2. `swApp.DocumentVisible(false, swDocPART)`, then `NewDocument` (default part template).
3. `IPartDoc.CreateFeatureFromBody3(copy, false, 1 /*Check*/)`.
4. Set the material.
5. Set `swStepAP`, keeping the old value for the restore.
6. `Extension.SaveAs3(<record>/exports/bodies/<ID>.step, 0, swSaveAsOptions_Silent, null, null, ref e, ref w)`.
7. `QuitDoc`.
8. In `finally`: restore the visibility and `swStepAP`, then `ActivateDoc3` the mold document.

This adds nothing to the feature tree. It also writes `exports/part/shrunk-part.step` (PART-REF) and `exports/part/molded-part.step`. STEP files are in the model frame; the frame transform is in `mold.json`.

#### 2.3.5 `mold_record` (the design record = the simulator manifest)

**Location.** `<mold file folder>\<mold file name>.mold\mold.json`. The part custom property `Unify3D.MoldRecord` holds the folder name, so a renamed or moved part still finds it. Subfolders: `part\`, `exports\bodies\`, `exports\part\`, `checks\`.

| Action | What it does |
|---|---|
| `init` | Needs a saved part. Creates the folders and the skeleton: schema, GUID id, tool version, files, createdAt. Copies the page inputs from `ToolRunner.LastInputs["design-compression-mold"]` (ToolRunner.cs:39, internal static). Calls `RebuildTools.RegisterWorkingCopy(path, path)` (RebuildTools.cs:31) so that `checkpoint restore` works on this file (RebuildTools.cs:248). Sets the custom property. |
| `read` | `section` = "summary" (default): at most 6,000 characters. It covers status, stages, the key numbers, failing or warning checks, and recent decisions. Any top-level key returns that section as JSON, up to 20,000 characters. |
| `merge` | Deep-merges `data`: objects key by key, arrays of objects with an `id` by id, other arrays replaced. Sets `updatedAt`, validates with `MoldRecord.Validate`, and returns a short status ("merged; 2 checks fail: ..."). |
| `setup_sheet` | Fills `data/setup-sheet.html` and writes `setup-sheet.html` into the record folder. Returns the path. |
| `save_default` | `{table, id?, values}` writes the shop override (1.5.5) after checking the keys against the shipped table. |
| `path` | Returns the record folder. |

Validation is hand-written, so the add-in gains no dependency. A test validates sample records against `data/mold.schema.json` with JsonSchema.Net (MIT, netstandard2.0), used in tests only.

The tool is not excluded from context clearing. The TOOL.md has the agent re-read the summary at every phase start instead, as Smart Rebuild does with its journal.

#### 2.3.6 The `Mold` helper object for macros and cells (Geometry/MoldApi.cs)

Add `public MoldApi Mold => _mold ??= new MoldApi(this);` to `ScriptGlobals` (ScriptGlobals.cs, next to `Overlay`, line 120). Methods, each tested in SolidWorks (6.2):

| Method | API used | Self-check |
|---|---|---|
| `MoldFrame Frame()` | 2nd RefPlane feature → `IRefPlane.Transform.ArrayData[6..8]` = W (verified in v1) | W has unit length |
| `IFeature InsertPart(string path, string config = "")` | Neutral formats: `swApp.LoadFile4(path, "r", swApp.GetImportFileData(path), ref err)` hidden, then `Extension.SaveAs3(<record>/part/<name>.SLDPRT)` and `QuitDoc`. Then `((IPartDoc)Model).InsertPart3(sldprt, (int)(swInsertPartImportSolids \| swInsertPartDontZoomAll), config)`. Read the enum values with `get_sw_api_type swInsertPartOptions_e`. Renames the feature `MOLD Part <name>`. | A new solid body appears. STL is refused. |
| `IFeature Orient(IBody2 b, double[] axisAngle, double[] move)` | `InsertMoveCopyBody2(..., bCopy:false, 1)` with b at Mark 1 | Volume unchanged |
| `IFeature ScaleForShrink(IBody2 b, double sx, double sy, double sz)` | Select **only** b, then `FeatureManager.InsertScale((short)swScaleType_e.swScaleAboutOrigin, sx==sy&&sy==sz, sx, sy, sz)`. With nothing selected, Scale scales every body. | V_after/V_before = sx·sy·sz within 0.1% |
| `IFeature CopyBody(IBody2 b, double[] d, int n = 1)` | b at Mark 1 (Mark 0 fails, v1), `InsertMoveCopyBody2(dx, dy, dz, 0,0,0,0,0,0,0, true, n)` | n new bodies |
| `IFeature Plate(string id, double u0, double v0, double u1, double v1, double w0, double w1)` | Sketch on the Top Plane, corner rectangle, `FeatureExtrusion3(... Merge=false ..., T0 = swStartOffset, \|w0\|, flip = w0 < 0)` (verified v1 pattern) | A body at the expected box within 0.01 mm |
| `IFeature Subtract(IBody2 target, IEnumerable<IBody2> tools, string name)` | Target Mark 1, tools Mark 2, `InsertCombineFeature((int)swBodyOperationType_e.SWBODYCUT, null, null)` (passing the bodies as arguments returns null, v1) | Volume drop = Σ tool volumes inside the target, within 0.1% |
| `IFeature SplitByPlane(IBody2 b, IFeature plane, string name)` | `PreSplitBody2()` → `PostSplitBody2(marks, false, origins, null, "")` | Total volume kept |
| `IFeature Folder(string name, params IBody2[] bodies)` | `IBody2.Select2(true, null)`, then `FeatureManager.InsertSubFolder()` (as in Smart Rebuild phase 4), rename | `IBodyFolder.GetBodyCount()` = n |
| `bool SetBodyMaterial(IBody2 b, string name, string db = "SOLIDWORKS Materials")` | `IBody2.SetMaterialProperty(cfg, db, name)`, read back with `GetMaterialPropertyName` | Name read back matches |
| `IList<MoldBody> Bodies()` | Folders plus geometric order | |
| `FlashResult FlashGrooves(IFace2 face, FlashOptions o)` | Port of the flash-grooves macro (N/existing-mold-knowledge.md 3.2): `GroupOf`, `Poly`, `Offset`, `DrawRounded`, runner search, **one fillet per body** (fixing the open bug at macro.cs:250-252) | Acceptance A10, measured |
| `IFeature CboreHoles(...)`, `IFeature TappedHoles(...)` | `FeatureManager.HoleWizard5` with feature scope set to the named bodies; the exact parameters come from spike S11 | Hole count and diameters |
| `PartReport Analyze(...)` | The same code as `mold_part` | |

#### 2.3.7 `ask_selection` (new UI tool: picking faces mid-run)

In ui/src/agent/runAgent.ts, next to `ASK_USER_TOOL` (72-86):

```ts
export const ASK_SELECTION_TOOL: ToolDef = {
  name: "ask_selection",
  description: "Ask the user to select entities in SolidWorks (e.g. the part's critical faces) and wait until they press Done. " +
    "Pre-select your suggestions in code first; the user adds or removes. Returns one line per entity with an [S#] reference " +
    "usable with Ref(\"S#\") and select_refs.",
  input_schema: { type: "object", properties: {
    prompt: { type: "string" },
    types: { type: "array", items: { type: "string", enum: ["face", "edge", "vertex", "body", "feature", "component"] } },
    min: { type: "integer" }, max: { type: "integer" } }, required: ["prompt"] },
};
```

**In runAgent.ts:**
- Add it to the tools list (line 196) and to the local handling (356-377).
- Add `"ask_selection"` to `exclude_tools` (line 155).

**In store.ts:**
- `RunState.question` (line 68) gains `selection?: {types, min, max}`.
- On Done: `rpc("selection.snapshot", {docKey})` (AgentController.cs:260, already used at store.ts:550).
- Keep only the allowed types.
- Number the refs `S{n+1}` from `Object.keys(thread.selRefs).length`. Merge them into `thread.selRefs`, as `submitBlocks` does (store.ts:915).
- Resolve with lines in the `buildContext` format (store.ts:567), e.g. `[S7] face of MOLD Part bushing - cylinder r 10.0 mm, area 1256 mm²`.

**New component `SelectionCard.tsx`:** the prompt, "Select in SolidWorks, then press Done", and Done / Skip buttons. A voice answer given while the card is open counts as Done plus the spoken note.

#### 2.3.8 Translucent face highlighting

- **`show_overlay`** (OverlayTools.cs:63-103) gets the item type `face`, with a `ref` ("S3") or a `faces` list of persistent refs, plus `color`, `label` and `alpha` (default 0.3).
- **`SceneOverlay`** gets an item kind `Tris`: a float array of model-space triangles. Each triangle is filled with GDI `FillPolygon`; the depth test uses the triangle centroids (the existing `Samples` logic, 317-352). Items are capped at 20,000 triangles. Triangles are redrawn only when the camera changes (timer at :110).
- **`OverlayApi`** gets `Faces(IEnumerable<IFace2> faces, string color, string label = null, double alpha = 0.3)`.
- **Translucent temporary bodies.** The transparency argument is dropped today (PreviewDisplay.cs:21). Fix: when `transparency > 0`, draw the temporary body's faces as `Tris` and don't call `Display3`. Setting material values on temporary bodies crashes SolidWorks (PreviewDisplay.cs:11-13), which rules that route out.

#### 2.3.9 Drawing attachments (R/src/SwAgent/ModelFiles/FileAttachments.cs)

- **PDF size limit.** `MaxPdfBytes` (line 18) goes from 30,000,000 to 20,000,000. Encoded in base64, a 20 MB PDF is 26.7 MB, under the 32 MB per-request limit; 30 MB became 40 MB and failed. The 1M-context models accept up to 600 pages. The staged message is never cleared, so the PDF rides along on every request (as a cached read).
- **`.slddrw` branch** in `EntryAsync` (35-78):
  1. `OpenDoc6(path, swDocDRAWING, ReadOnly | Silent)`, with a 60 s budget.
  2. `GetExportFileData(swExportPdfData)` with `SetSheets(swExportData_ExportAllSheets, null)`.
  3. `Extension.SaveAs3(temp.pdf, ...)`, then `QuitDoc`.
  4. Attach the result as a PDF named "<file>.slddrw (as PDF)".
- `.dxf` and `.dwg` stay path-only. The TOOL.md asks for a PDF.

#### 2.3.10 Which gap each component closes (gaps from N/agent-abilities.md 6)

| Gap | Closed by |
|---|---|
| G1 insert the part | `Mold.InsertPart` |
| G2 shrink | `mold_calc process` + `Mold.ScaleForShrink` |
| G3 cavity | `Mold.CopyBody` + `Mold.Subtract` |
| G4 split | `Mold.SplitByPlane` (Mold Tools chain only for non-planar parting lines, by the agent) |
| G5 draft as data | `mold_part` |
| G6 undercuts | `mold_part` (rays) |
| G7 per-body report | `mold_bodies report` |
| G8 interference and clearance | `mold_bodies` |
| G9 STEP per body | `mold_bodies export` |
| G10 picking faces mid-run | `ask_selection` |
| G11 highlighting | `face` overlay item, `Overlay.Faces`, translucent temporary bodies |
| G12 plate and bolt calculation | `mold_calc` + the `SwAgent.Mold` library |
| G13 drawings | `.slddrw` to PDF, 20 MB cap |
| G14 simulator hooks | `mold_record` + `mold.json` + section 4 |
| G15 page | 2.1 |

### 2.4 The validation solver and the sweep (code)

**Solver** (`src/SwAgent/Mold/Structural/Fe/`), a C# port of `N/proto/plate.py`:
- **Element.** MITC4 Mindlin plate (Bathe and Dvorkin 1985) on a structured rectangular grid.
  - Each node has 3 degrees of freedom (w, βx, βy).
  - Bending uses 2x2 Gauss integration.
  - Shear strains are tied at the edge midpoints: γx sampled at (0, ±1) and interpolated in η; γy sampled at (±1, 0) and interpolated in ξ.
  - κs = 5/6.
  - On a uniform grid the 12x12 element matrix is computed once and reused.
- **Supports:**
  - edges F, S (hard: w = 0 and tangential rotation = 0), s (soft: w = 0 only), C;
  - interior line supports (rails);
  - point springs (pillars, k = E A / L);
  - Winkler regions (platen contact), with an optional unilateral pass (drop springs in tension, 3-6 passes).
- **Loads:** pressure patches, as a rectangle or an element mask from a cavity outline, applied as consistent loads q A/4 per node.
- **Solving.**
  - Symmetric banded LDLᵀ. Nodes are numbered along the short side first, so the half-bandwidth = 3(n_short + 2).
  - A 48 x 96 grid has about 14,000 degrees of freedom, takes about 0.5 s and uses 17 MB.
  - Constrained degrees of freedom are eliminated, not penalised, because penalties hurt conditioning for thin plates.
- **Recovery:**
  - deflection at the nodes;
  - interior moments at the Gauss points, extrapolated and averaged;
  - **clamped-edge moments from the nodal reactions**: M = R_rot / tributary length. Averaging under-reads them by 12-40%.
  - **free edges:** superconvergent patch recovery (Zienkiewicz-Zhu), or the first Gauss row. This removes the 5% overshoot.
  - stress σ = 6M/t², taking the largest principal moment from Mx, My and Mxy.
- **API:** `MindlinPlateSolver.Solve(PlateModel m, CancellationToken ct)` returns `PlateResult {W, Mx, My, Mxy, WMax, SigmaMax, WMaxAt, SigmaMaxAt, EdgeMoments, Reactions}`. `Navier.SimplySupported(a, b, t, E, nu, q, x, y, shear, terms = 199)` serves as a reference.

**Coefficient table.** `src/SwAgent/Mold/Structural/coefficients.csv` is an embedded resource. Columns: `bc, aspect, tau, nu, loadFrac, alpha, beta, wMaxX, wMaxY`. A header comment holds the generator version, date, mesh size and SHA-256. `Coefficients.Get(bc, aspect, tOverB, nu, loadFrac)` interpolates bilinearly in (aspect, τ²); τ² is the natural variable, because FSDT deflection is exactly linear in τ² for simply supported plates.

**Sweep console** `tools/dev/MoldSweep/MoldSweep.csproj` (net48; references `src/SwAgent`; not shipped):
- `MoldSweep coefficients` writes the CSV (Sweep 1, 3.20).
- `MoldSweep montecarlo --n 2000 --seed 1` writes `data/validation/montecarlo.csv` and `montecarlo-summary.json` (Sweep 2).
- `MoldSweep table` writes `data/validation/sizing-table.csv`.
- Runtime: about 7,000 FE runs at 24 elements on the short side, about 20 ms each, so 2-3 min. Monte Carlo takes about 5 min.

**Tests** (`tests/SwAgent.Tests/Mold/`, xUnit 2.9.2, net48, project at tests/SwAgent.Tests/SwAgent.Tests.csproj): see 6.2. The default `dotnet test` run includes T1-T18, the examples E1-E12, a 200-sample Monte Carlo smoke run (under 30 s), and a spot check that regenerates 50 random CSV rows and compares them within 0.5%. The full sweep is `[Trait("Category","Sweep")]` and is skipped unless `MOLD_SWEEP=1`.


---------------------------------------------------------------------------------------------------------------------

## 3. The structural method (what `mold_calc` computes)

Inside the calculator everything is in N, mm, MPa (N/mm²), degC, K and kg.

The ideas come from N/structural.md:
- Stiffness governs: the parting-line gap that lets material flash decides most sizes.
- Platen-backed plates do not bend.
- Mold plates are thick plates, so Mindlin, not Kirchhoff.
- Separating forces are never carried by friction.
- Steel bolts in hot aluminium gain preload.
- Aluminium is derated for long service.

Each formula below has a number S#. The code (2.3.1) and the tests (6.2) use the same numbers.

### 3.1 Inputs

| Symbol | Meaning | Source |
|---|---|---|
| p_in, meaning | Press value and its meaning (gauge or on the part) | page `press_pressure`, `press_pressure_is` (answered in Phase 0 if "Not sure") |
| F_rated, p_rated | Rated force and the gauge at that force | page |
| L_pl x W_pl, H_day | Platen size and daylight (optional) | page |
| T | Press temperature | page `press_temp` |
| plate and insert grades | Rows of mold-materials.json | page |
| part row | Row of materials.json, with the user's edits: S, SG, η, pot life, cure, draft | page |
| N | Cycle life | page `cycle_life` |
| limits | m_plate, box_plate, m_base, m_lid (each may be "not set") | page |
| A_i, P_i, V_i, h_i, b_i x L_i, r_f,i | Per cavity: projected area, outline perimeter, part volume, depth in each half, outline box, smallest wall-to-floor radius | `mold_part` and `layout` |
| t_tol | Tightest thickness tolerance (± value) of a dimension that a deflecting element controls | the drawing (Phase 1) |
| support | A (flat on the platen; default) to F | default A; the agent asks if the press uses rails or the mold overhangs |

### 3.2 Process class and load cases

Class A covers liquid_cast_hot, liquid_cast_rt and foam_pour. Class B covers rubber_compression, compound_compression and liquid_compression. Class C covers thermoplastic_compression and preform_sinter (D4).

| Case | Load | Used for |
|---|---|---|
| LC1 fill | Cavity pressure p_LC1 on floors, plug faces and walls up to the cavity depth. It acts hydrostatically, the same sideways as down (λ = 1.0). | Walls, spans, inserts, pins, keys |
| LC1x abuse (class A only) | p = F/A_proj: the charge gelled before the lid seated, so the press force goes through the material | Walls and inserts against yield with SF 1.0 only. This sets the maximum gauge on the setup sheet. |
| LC2 closed | F through the bearing faces (land, parting face, stops) | Bearing, hobbing, land crush |
| LC3 thermal | ΔT = T - 20 °C, uniform; 10 K lid-to-base gradient | Fits, dowels, bolt preload |
| LC4 handling | 2 x piece weight on handles, lift holes and threads | Lift hardware, threads |
| LC5 demold | Pry and jack forces | Shop-standard features in v1. The process simulator supplies forces later (H2). |

### 3.3 Press force and pressures

- **(S1) Ram area.** A_ram = F_rated / p_rated (mm² = N / (N/mm²)). Equivalent: A_ram = n·π·D²/4, if the user gives n rams of bore D.
- **(S2) Projected area.** A_proj = Σᵢ (A_i + P_i · w_land).
  - w_land = the class A lip (0.125 in = 3.175 mm, shop standard) or the class B `flash.landWidthMm.typ`.
  - The material carries the force over the cavity plus the land. This is the SPI tonnage convention (N/design-rules.md 10).
- **(S3) Press force.**
  - Gauge reading: F = p_in · A_ram. Requires p_in ≤ p_rated.
  - On the part: F = p_in · A_proj. Gauge needed = F / A_ram.
- **(S4) Classes B and C:** p_LC1 = F / A_proj.
- **(S5) Class A:**
  - p_LC1 = max(1.5 · p_tab,max, 0.35 MPa), where p_tab,max = row `moldingPressurePsi.max` x 0.006894757 MPa/psi.
  - Abuse case: p_LC1x = F / A_proj.
  - Why: in a liquid pour the press mostly clamps the lid. Hydrostatic head plus squeeze is the real load (N/design-rules.md 0, 10).
- **(S6) Force needed.**
  - Class A: F_req = 1.25 · p_LC1 · A_proj.
  - Classes B and C: F_req = p_req · A_proj, where p_req = `moldingPressurePsi.typ` x 0.006894757 MPa/psi.
  - Group thermoset_compound adds 4.1 MPa x max(0, (h_max - 25 mm)/25 mm) to p_req (SPI handbook via the Crescent module, N/design-rules.md 10).
- **(S7) Recommended gauge** p_g,set = F_req / A_ram. Flags:
  - F_req x 1.25 > F_rated: the press is too small.
  - Class B with p_LC1 > 1.5 · p_req: too much force, which wears the lands. Advise turning the gauge down to p_g,set.
- **(S8) Load centre.** The A_proj-weighted centroid must lie within ±5% of the platen length and width of the platen centre. The judgement value is in sizing-defaults; check it against the press maker's off-centre rating. The layout centres the cavities by construction.
- **(S9) Press envelope** (each check only when its input is set):
  - The footprint, including handles and ledges, must be ≤ the platen size minus 25 mm per side, because platen edges run cooler (N/design-rules.md 10.3).
  - Closed stack height H ≤ H_day - 25 mm.

### 3.4 Materials at temperature (long-term basis)

- **(S10)** f(T) is linear interpolation over T = [20, 100, 150, 200, 250] °C of [1, `retained` values]. Below 20 °C, f = 1. Above 250 °C there are no data: aluminium is refused, and steels use the 250 °C factors with the warning "no strength data above 250 °C - check the supplier's hot data". This matters for PEEK at about 400 °C in H13.
- **(S11)** E_T = E₂₀·f_E(T), S_y,T = S_y,20·f_Y,long(T), S_u,T = S_u,20·f_U,long(T).
  - The `*_long_10000h` rows are the design basis. A mold held hot 8 h a day reaches 10,000 h in about 5 years (N/mold-materials.md finding 1).
- **(S12) Service status** of a grade at temperature T:

  | T | Status | What happens |
  |---|---|---|
  | ≤ `max_service_C.full_strength` | ok | |
  | ≤ `derated` | ok-derated | The plan carries a note |
  | ≤ `never_above` | limited | Allowed only when N ≤ 10,000 and the user confirms |
  | > `never_above` | not allowed | The plan must switch grade |

  For 6061-T651 the limits are 120 / 150 / 175 °C; for 7075-T651, 100 / 120 / 135 °C; for P20, 250 / 350 / 450 °C.
- Values used in the examples:

  | Grade and temperature | E_T | S_y,T | S_u,T |
  |---|---|---|---|
  | 6061 at 100 °C | 66.8 GPa | 262 MPa | 291 MPa |
  | 6061 at 150 °C | 64.1 GPa | 215 MPa | 233 MPa |

### 3.5 Allowable deflection (the flash gap)

- **(S13) δ_flash** from sizing-defaults:

  | Material flow | δ_flash |
  |---|---|
  | Liquid, η < 1,000 cP | 0.012 mm |
  | Liquid, 1,000-10,000 cP | 0.020 mm |
  | Liquid, > 10,000 cP | 0.025 mm |
  | LSR paste | 0.005 mm |
  | Rubber | 0.050 mm |
  | Thermoset compound | 0.025 mm |
  | Melt / no-flow | 0.025 mm |

  - For liquids, η is the number on the page, so the user's edit counts. The stored `viscosityClass` is only used for the non-liquid classes.
  - Sources: Menges' permissible-deformation approach and mold-maker vent practice (N/structural.md 4.1); LSR flash at a 0.01 mm mismatch (PT Online).
- **(S14)** δ_allow = min(δ_flash, 0.25 · t_tol).
  - The gap that matters is the relative gap at a land: a platen-backed partner does not move, so the gap equals the full sag.

### 3.6 Fatigue allowable (R = 0: every shot loads from zero to full)

- **(S15) Fully reversed strength S_a(N, T):**
  - **Aluminium:**
    - S_N comes from `fatigue_MPa` (R = -1, smooth; 6061 per D18), interpolated log-log between the listed N.
    - Below 1e5 cycles: a Basquin line between (1e3, 0.9·S_u,20) and (1e5, S_1e5).
    - S_a = 0.9 (machined surface) · S_N · S_u,T / S_u,20.
  - **Steel** (Shigley chapter 6):
    - S_e = 0.5·min(S_u,20, 1,400 MPa)·k_a·k_b·k_e, with k_a = 4.51·S_u^-0.265 (machined, S_u in MPa), k_b = 0.80 and k_e = 0.868.
    - S(1e3) = f·S_u, with f = 0.9 (S_u < 500 MPa), 0.86 (S_u < 1,100 MPa), otherwise 0.80.
    - Basquin line from 1e3 to 1e6 cycles; constant from 1e6.
    - Then multiply by S_u,T / S_u,20.
  - The steel S-N numbers in the JSON are not used.
- **(S16) Goodman at R = 0:** σ_max = 2·S_a·S_u,T / (S_u,T + S_a), capped at S_y,T.
- **(S17) Notch factor:** K_f = 1 + q (K_t - 1).
  - K_t by r/w: 1.5 at r/w ≥ 0.2; 1.8 at r/w ≥ 0.1; 2.2 at r/w ≥ 0.05; otherwise 2.5. These are simplified Peterson shoulder-fillet values. K_t = 2.5 at holes in bent plates.
  - q = 0.8 for aluminium, 0.95 for steel.
  - r is the cavity's wall-to-floor radius. It is the part's own edge radius, measured by `mold_part`.
- **(S18)** σ_allow,fat = σ_max / (K_f · 1.5).
- **(S19)** σ_allow,static = S_y,T / 2.0.

Note: N/structural.md 7.2 says to scale S_a by S_u,hot/S_u,20, but its example table did not do it. This design applies the scaling, which is more conservative. For the 6061 wall at 150 °C and 1e5 cycles, the allowable falls from 74 to 60.3 MPa (E3).

### 3.7 Cavity walls (all classes; class A with the S5 pressure)

- **(S20) Wall model.** A plate of thickness w, loaded height h (the cavity depth in that piece) and length L.
  - Bottom edge clamped, top edge free.
  - Side edges clamped for a pocket milled from solid ("Wall-C") or simply supported for bolted frame corners ("Wall-S").
  - α_w(L/h, w/h) and β_w(L/h, w/h) come from coefficients.csv, interpolated bilinearly in (L/h, (w/h)²).
  - For L/h > 4, use the 4.0 row (conservative). The strip limits are α = 1.365, β = 3.0.
- **(S21) Thickness for deflection.** w = [1.2·α_w·λ·p_LC1·h⁴ / (E_T·δ_allow)]^(1/3).
  - Fixed-point iteration from w₀ = 0.3 h, until |Δw|/w < 1e-4. It converges in 4-8 steps, steadily, because α rises with w/h.
  - 1.2 is the wall-root factor: the floor rotates with the wall. Sweep 3 calibrates it. λ = 1.0.
- **(S22) Stress.** σ_w = β_w·λ·p·h²/w² + σ_m, where σ_m = p·b_perp/(2w) is the membrane stress from the perpendicular walls and b_perp is the cavity size at right angles to the wall.
- **(S23) Stress limit.** If σ_w exceeds min(σ_allow,static, σ_allow,fat), then w ← w·√(σ_w/σ_allow) (stress varies as 1/w²) and check again.
- **(S24) Range.** w_struct = max(w_defl, w_stress). If w/h > 0.5, plate theory no longer applies. The result is flagged, with the options steel, a wall keyed into the base, or a 3D check. A stocky wall in the LC1x abuse check uses the strip upper bound σ = 3·p·h²/w² + σ_m.
- **(S25) The wall that gets drawn.** w = max(w_struct, w_geom).
  - w_geom = lip + groove band + feature clearance + hardware band, from the layout.
  - Class B also applies a practical minimum: 15 mm in aluminium, 10 mm in steel.
  - The report says which of these governs. In the user's class A molds the flash system and dowels govern (E2).

### 3.8 Floors, lids and spans

- **Case A (default).** The mold sits flat on the platen and the platens cover at least 80% of each back face over the cavity footprint. The plates are in compression and do not bend (N/structural.md 0.3; checked by T14 and the Winkler FE check). Thickness then comes from the minimum rules:
  - **(S26) Floor:** t_floor ≥ max(12 mm aluminium / 10 mm steel, 0.25·h_max, d_TC + 2 x 6 mm when the thermocouple well runs under the cavity, counterbore or thread depth needs).
  - **(S27) Pocket plate:** t ≥ max(t_floor + h_max, 0.05·L_longest, 12 / 10 mm, counterbore depth + 6 mm, L_e + 0.5d + 3 mm).
  - **(S28) Lid:** t_lid ≥ max(0.05·L_longest, 12 / 10 mm, plug height + 10 mm for an integral plug, hardware).
  - The 0.05·L rule is for handling stiffness and machining distortion [K]. It replaces the 0.08·L in N/structural.md 4.6, which would have made lids 60% thicker than the user's proven 0.75 in practice.
- **Cases B-E** have an unsupported span b:
  - B: rails, one-way span.
  - C: an opening supported all round. Use SS4, or C4 when the frame is integral.
  - D: overhang c.
  - E: pillars at s_x x s_y. Treat the panel as C4.
  - **(S29) Thickness:** t = [α(BC, a/b, t/b)·p·b⁴ / (E_T·δ_allow)]^(1/3), by fixed point from t₀ = 0.2 b, using the Mindlin tables (not Roark).
  - **(S30) Stress:** σ = β·p·b²/t² ≤ min(σ_allow,static, σ_allow,fat with K_t = 2.5 at holes).
  - **(S31) When it doesn't fit.** If t/b > 0.5, or the plate breaks a weight limit, the span gets more support instead of a thicker plate: the largest span for the chosen t is b_max = [E_T·δ_allow·t³ / (α·p)]^(1/4). Alternatively switch to steel: same deflection at t x (E_Al/E_st)^(1/3) = 0.69 t.
  - **(S32) Case D cantilever strip:** w = 1.5·p·c⁴(1-ν²)/(E·t³) + p·c²/(2κGt) with κ = 5/6, and σ = 3·p·c²/t².
  - In the table path the gap at the land is the spanning plate's full deflection (conservative). The FE check (3.20) uses w_base - w_lid at the land nodes.
- **Case F** (the ram or platen is smaller than the lid): FE only.

### 3.9 Bearing: land, parting face, stops (LC2)

- **(S33) Bearing area.**
  - Class A: A_bear = the whole parting face minus cavities, grooves, runners and vents, and minus faces relieved by more than 0.1 mm.
  - Class B: stop-pad area, plus the land if it touches.
- **(S34)** σ_bear = F / A_bear ≤ S_y,T / D_b.
  - D_b = 3 for N ≥ 1e5; D_b = 2 below. This is judgement against hobbing (N/structural.md 2.7).
  - Use the weaker of the two contact materials. Under hardened pads (40-50 HRC), check the aluminium seat under the pad's base area.
- **(S35) Maximum gauge from bearing:** p_g,max = A_bear·S_y,T / (D_b·A_ram). It goes on the setup sheet, together with the abuse-case limit from S5 and S24 if that is lower.
- **(S36) Land crush.** Class B lands with no stops: σ_land = F / A_land. If it exceeds S_y,T / D_b, stop pads are mandatory. Example: 1/16 in lands alone, at 3,000 psi gauge on an 8 in ram, see 60 ksi and crush aluminium (N/structural.md 2.5).

### 3.10 Multi-piece bases: keys first

- **(S37) Separating force:** F_sep = p_LC1·L_c·h, where L_c is the loaded cavity length along that piece. Example: 56.5 kip on a 12 in bar shared by two cavities at 3,142 psi and 1.5 in depth.
- **(S38) Key bearing:** σ_key = F_sep / (k·L_key) ≤ S_y,T / 2.5. The key step height k is at least 9.5 mm (0.375 in).
- **(S39) Key root shear:** τ = F_sep / (L_key·w_key) ≤ 0.58·S_y,T / 2.0.
- **(S40) Tipping.** Bolt tension on the side piece: P_tot = F_sep·(h/2) / w_bar, which is the moment about the key root with the pressure resultant at h/2. P = P_tot / n per bolt. Then check the bolts as in 3.11.
- **(S41) Friction-only joints are forbidden.** They would need 424-848 kip of preload in the example above.

### 3.11 Bolts (Shigley, 10th ed., chapter 8)

- **(S42)** k_b = A_t·E_b / l_grip. This is the lower bound; for long shanks use the A_d·A_t form (eq. 8-17).
- **(S43)** k_m = E_m,T·d·A·exp(B·d/l_grip). Wileman: A = 0.78715, B = 0.62873 for steel; A = 0.79670, B = 0.63816 for aluminium (eq. 8-23). Mixed stacks use the frustum method per layer (eq. 8-20).
- **(S44)** C = k_b / (k_b + k_m).
- **(S45)** F_i = f_pre·F_p.
  - f_pre = 0.75 for steel-in-steel joints; 0.60 when any clamped member is aluminium.
  - F_p = A_t·S_p, with S_p from the table: A574 at 140 ksi (≤ 1/2 in) or 135 ksi; ISO 12.9 at 970 MPa.
- **(S46) Thermal preload change:** ΔF_th = (α_m - α_b)·(T - 20)·l_grip·k_b·k_m / (k_b + k_m), with α_b = 11.7e-6 /K.
- **(S47) Hot load:** F_i + ΔF_th + C·P ≤ 0.90·F_p.
- **(S48) Separation:** n_0 = (1 - e)·F_i / (P·(1 - C)) ≥ 2.0, with e = 0.10 for embedding and creep in aluminium and 0 for steel.
- **(S49) Load factor:** n_L = (F_p - F_i) / (C·P) ≥ 1.5 (eq. 8-28).
- **(S50) Bolt fatigue:** σ_a = C·P / (2·A_t) ≤ S_e / 1.5. S_e = 160 MPa for inch A574 (Shigley's 23.2 ksi grade-8 rolled-thread value, conservative) and 190 MPa for 12.9.
- **(S51) Head bearing:** p = (F_i + ΔF_th + C·P) / A_bearing ≤ 0.9·S_u,T of the member. Hardened washers (ASTM F436 / ISO 7089 300HV) are mandatory on aluminium faces. With a washer, A_bearing is the washer's contact area. Without one, 12.9 heads put 313-551 MPa on the plate (N/mold-materials.md finding 3).
- **(S52) Torque:** T = K·d·F_i, with K = 0.20 dry (the default on the setup sheet) and 0.15 lubricated (also listed).
- **(S53) Choosing the bolts.**
  - Size list: `fasteners_inch` for IPS documents, otherwise `fasteners_metric`, taken in ascending order.
  - Count n: from n_min (4 per plate, 2 per joint end) up to the count that keeps the spacing at 4 d or more.
  - Edge distance at least 1.5 d. Spacing along a sealing joint at most 6 d.
  - The first (size, n) that passes S47-S51 and S54 wins.

### 3.12 Threads and thread inserts

- **(S54)** L_e/d ≥ max(1.0, 0.5·S_u,bolt / S_u,parent,T). S_u,bolt = 1,220 MPa for 12.9 and 1,241 MPa (180 ksi) for A574 (N/mold-materials.md 3). Example: 6061 at 150 °C needs 2.6 d.
- **(S55)** Use a steel thread insert when any of these holds:
  - L_e/d > 2.5;
  - the screw is removed routinely (lid hardware, insert screws, eye bolts);
  - N ≥ 1e4 and the screw is loaded every cycle.
  Choose a helical-coil insert (2 d long in aluminium) or a key-locking insert. Re-check S54 at the insert's outer shear diameter: about 1.2 d for helical, about 1.75 d for heavy key-locking.
- **(S56) Stripping** of a directly tapped hole: τ = F_b,max / (π·d_min·L_e·0.75) ≤ 0.58·S_y,T / 2.0.

### 3.13 Fits and thermal

- **(S57) Differential growth:** Δ = |α₁ - α₂|·ΔT·L, where L is the distance from the datum or thermal centre. Example: aluminium against P20 at ΔT = 140 K over 300 mm gives 0.45 mm.
- **(S58) Locating.**
  - Mixed metals, or Δ at the second dowel larger than the 0.02 mm running clearance: one round dowel at the thermal centre plus one diamond or slot on a radial line.
  - Same metal at the same temperature: two round dowels are allowed if Δ for a 10 K lid-to-base gradient stays within the clearance.
- **(S59) Steel insert in aluminium.**
  - Line-to-line when hot needs a cold interference of i = d·(α_Al - α_st)·ΔT / (1 + α_Al·ΔT). That is 0.038 mm on 25 mm at ΔT = 140 K.
  - Size the pocket for 0 to +0.01 mm clearance when hot. Hold the insert with a shoulder and screws. Fit it by heating the plate.
  - Cold-fit hoop check (Lamé): p_c = (δ/d) / [(1/E_h)·((D²+d²)/(D²-d²) + ν_h) + (1-ν_s)/E_s] and σ_θ = p_c·(D²+d²)/(D²-d²) ≤ S_y,20 / 2. For shuttled molds, also check fatigue against σ_allow,fat.
- **(S60) Steel dowels pressed into aluminium.** Use the `press_hole_aluminium_150C` columns of the dowel table, which add 0.0016·d of interference for ΔT = 130 K, or use steel bushings retained by a flange or Loctite 620.

### 3.14 Fragile features become steel inserts (aluminium construction)

- **(S61)** An insert is required for any of:
  - a core pin with D < 3 mm or H/D > 2;
  - a steel wall under 3 mm between features;
  - pinch-off or cut-off edges that carry the closing load;
  - cut-off areas on the parting line at cycle tier T3 or above (≥ 10,000 cycles);
  - areas that take pry or cleaning abuse.

  Sources: N/design-rules.md 14.4; the Minnesota Rubber hole limits.
- **(S62) Pin check, any material.**
  - Lateral load q = λ_pin·p_LC1·d, with λ_pin = 0.5 (judgement).
  - Cantilever of height H: σ = 32M/(πd³) with M = q·H²/2, which must be ≤ min of the allowables.
  - Deflection δ = q·H⁴/(8·E·I), with I = πd⁴/64, which must be ≤ δ_allow.

### 3.15 Shrink, charge, overflow, cure (process numbers)

- **(S64) Scale factor.**
  - For the `cold_mold_cold_part_steel_ref` basis: s = 1/(1 - S) · (1 + α_ref·ΔT) / (1 + α_cav·ΔT), with ΔT = T - 20 °C, α_ref = 12.0e-6 /K and α_cav = the cavity metal's CTE.
  - For `rt_mold_rt_part`: s = 1/(1 - S).
  - For `preform_sinter`: s = 1/(1 - S), plus 1.5-3 mm machining stock per side, and the press temperature is ignored.
  - Per-axis s_x, s_y, s_z when per-axis S is given.
  - Use the exact form, not 1 + S, which is off by S² (ASTM D955).
  - Examples (an aluminium cavity with steel-basis data):

    | Shrink | Mold temperature | s | Effective shrink |
    |---|---|---|---|
    | 1.5% | 100 °C | 1.014288 | 1.41% |
    | 1.5% | 170 °C | 1.013468 | 1.33% |
- **(S65) Shrink against tolerance.** If (S_max - S_min)·L > 0.5·tol(L), flag the dimension "steel-safe": make the cavity smaller where it can be enlarged later, or larger for cores. Plan a first-article measurement.
- **(S66) Charge.** Per cavity, m = V_part·SG·(1 + flashAllowancePct/100). The shot is n·m·(1 + 5% hold-up for liquids) plus risers. PEEK matches Victrex's 102% rule.
- **(S67) Overflow.**
  - Required volume: V_req = max(overflowPct·V, k·flashAllowancePct·V)/100, with k = 2.0 for class A and 1.5 for class B.
  - The flash system must hold at least V_req. When runners run out past the plate edge (the user's standard), the excess leaves the mold and the check passes with a note.
  - Semicircular groove: r = √(2·V_req / (π·P)), clamped to `flash.overflowGrooveRadiusMm`. If r is above the maximum, add a second groove or spill pockets.
- **(S68) Pour time** (class A, n > 1): n·t_pour ≤ 0.5·pot life. t_pour = 60 s per cavity by default (shop standard).
- **(S69) Rubber cure time:** t = t_typ·2^((T_typ - T)/10) for T within `moldTempC`. Thick sections add (h/2)²/α_th, with α_th = 1.0e-7 m²/s (1.3e-7 for thermosets) (TSE).
- **(S70) Loading well** (bulkFactor > 1): V_load = m/SG·bulkFactor, and the well depth = (V_load - V_cav)/A_well.

### 3.16 Weights, boxes, splitting

- **(S71) Mass:** m = ρ·V, where V = `IBody2.GetMassProperties(1.0)[3]` once built, or from the layout before.
- **(S72) Box check.** Sort the finished size plus the blank allowance (+2 x 3 mm on L and W, +2 x 1.5 mm on T), then compare it, component by component, with the sorted `max_plate_size`.
- **(S73) Mass checks.** Each piece ≤ m_plate. The lid (the pieces lifted together) ≤ m_lid. Σ base ≤ m_base.
- **(S74) Split (`mold_calc split`).**
  - Candidate cuts lie in dead steel between cavity rows or columns: at least 15 mm from any cavity, clear of grooves and runners by the feature clearance, and never through a cavity or a critical face.
  - Rank by the fewest pieces, then the lightest heaviest piece.
  - The pieces are keyed into a subplate or an interlocked frame (3.10). The subplate must pass S72 and S73 itself. If not, split it at staggered lines, or propose frame plus inserts, or a hoist.
  - Return up to 3 options.

### 3.17 Rounding to stock

- **(S75) Thickness:** round the required finished thickness up to (stock nominal - skim):
  - 6061 and 7075: skim 0.8-1.5 mm per face;
  - MIC-6: no skim;
  - P20: 1.5-3 mm per face.
  - Take the `stock_thickness` list for the grade and the document's unit system. Round up to the shop's 0.125 in step.
- **L x W:** round up to 0.5 in (shop standard).

### 3.18 Safety factors and defaults

| Item | Default | Basis |
|---|---|---|
| Static stress | S_y,T / 2.0 | [K], N/structural.md 3 |
| Fatigue | σ_max(R=0) / (K_f x 1.5) | [K] |
| Deflection | ≤ δ_allow, no factor | the limit is already a design limit |
| Bearing | S_y,T / 3 (≥ 1e5 cycles); / 2 below | [K] anti-hobbing |
| Bolts | n_0 ≥ 2.0, n_L ≥ 1.5, hot ≤ 0.90 F_p, fatigue S_e/1.5 | Shigley chapter 8, [K] for 2.0 |
| Wall root | α x 1.2 | [K], calibrated by Sweep 3 |
| Keys | bearing S_y/2.5, shear 0.58 S_y/2 | [K] |
| Press reserve | F_req x 1.25 ≤ F_rated | N/design-rules.md 10.1 |
| Aluminium temperature | statuses from `max_service_C` | ASM/Kaufman 10,000 h data (verify) |

Every value lives in `data/sizing-defaults.json`. A shop can override it (1.5.5), and each call can override it (`overrides`).

### 3.19 Worked examples (the expected outputs in data/validation/examples.json; numbers checked with a reference script)

| # | Case | Result |
|---|---|---|
| E1 | Class B press: 8.00 in ram, 3,000 psi gauge, 2 cavities of 6 x 4 in, land not counted (w_land = 0) | A_ram = 50.27 in² (32,429 mm²). F = 150,796 lbf (75.4 US ton, 670.8 kN). p_LC1 = 3,142 psi (21.66 MPa). Gauge for 1,000 psi on the part: 955 psi. The same gauge on a 12 in ram gives 7,069 psi. |
| E2 | Class A, the user's typical job: TDI-ether 90A (S 1.5%, SG 1.10, η 1,200 cP, p_max 150 psi), 2 cavities 6 x 4 x 1 in deep, 6061-T651 at 100 °C, press 50 US ton at 3,000 psi run at 2,000 psi gauge, N = 5,000, sharp floor corner | **Press:** A_ram = 33.33 in² (21,505 mm²). F = 66,667 lbf (296.5 kN). A_proj = 53.0 in² with the 0.125 in lip. p_LC1 = 225 psi (1.551 MPa); abuse p = 1,258 psi (8.67 MPa). F_req = 14,906 lbf. **Recommended gauge 447 psi (3.08 MPa); the user's 2,000 psi is acceptable.** **Material:** E 66.8 GPa, S_y 262 MPa, S_u 291 MPa, status ok. δ_allow = 0.020 mm. **Wall:** structural w = 9.26 mm (0.364 in), σ = 40.0 MPa, against 131 MPa static and 68.9 MPa fatigue (S_a 186.4, σ_max 227.4, K_f 2.2). The drawn wall is set by the groove band and dowels, about 40 mm (1.58 in). Abuse check 21.5 MPa, against 262. **Floor:** 18.35 mm (TC well) rounds to 0.75 in. **Lid:** 0.05 x 16 in = 0.80 in rounds to 0.875 in. **Masses:** base 7.8 kg (17.2 lb), lid 4.95 kg (10.9 lb). **Bearing:** 9.58 MPa, against 131; the bearing allows a gauge of 188.8 MPa, so it doesn't limit. **Shrink:** s = 1.014288 (1.41% effective). **Charge:** 454 g per cavity, 954 g per shot. These match the user's v1 practice (0.75 in floors and lids, 1.25-1.58 in walls), but now each value has a reason. |
| E3 | Class B wall: p = 10.3 MPa, h = 25 mm, L/h ≥ 4, δ = 0.05 mm, K_f = 1.5 given, membrane off, N = 1e5 | **6061 at 150 °C** (E 64.1 GPa, S_y 215 MPa): w_defl = 12.5 mm without the root factor, 13.3 mm with it. Fatigue allowable 60.3 MPa, so w_fat = 16.9 mm. That is w/h = 0.68 > 0.5, so it is flagged: prefer P20, or run a 3D check. **P20 at 150 °C:** w = 8.65 mm, σ = 232 MPa against a fatigue allowable of 231 MPa, so it passes at 1e5. At 1e6 the allowable is 168 MPa and w = 10.1 mm. |
| E4 | Bolted frame corner: 4 x 1/2-13 A574, grip 2.0 in, aluminium joint, ΔT = 100 K, P = 28.25 kip per corner | F_p = 19,866 lbf. C = 0.306. ΔF_th = 3,400 lbf. F_i = 11,920 lbf (0.6 F_p). Hot load 17,479 lbf = 88.0% F_p (passes ≤ 90%). n_0 = 2.19 (passes). n_L = 3.68. Torque dry 99.3 lbf·ft. With grade-8 proof the notes found 1.87, "marginal"; A574 data passes it. |
| E5 | M10 12.9, grip 50 mm, 6061, 20 → 150 °C | k_b = 237.8 kN/mm, k_m = 580 kN/mm, ΔF_th = 13.05 kN. At 0.75 F_p: hot 55.3 kN = 98% F_p, which **fails** S47. At 0.60 F_p: hot 46.8 kN = 83%, which passes. This confirms the 0.60 rule. |
| E6 | Shrink 1.5%, steel basis, aluminium cavity, 170 °C | s = 1.013468; effective shrink 1.33% (N/structural.md quotes 1.32%) |
| E7 | Press "on the part" 1,000 psi, 48 in², 8 in ram | Gauge 955 psi |
| E8 | Thread engagement, 6061 at 150 °C, 12.9 screw | L_e/d = 2.62, above 2.5, so use an insert (2 d helical) |
| E9 | T16 thickness: SS4, a/b = 1, p = 10 MPa, b = 100 mm, E = 66 GPa, δ = 0.025 mm | t = 35.4 mm (t/b 0.354) |
| E10 | Steel insert, 25 mm, ΔT = 140 K | Cold interference 0.0377 mm |
| E11 | Split: base of 30 x 14 x 2.5 in, 6061, 2 x 3 cavities, m_plate 23 kg | Defined in stage 2 with the reference implementation: 2 pieces cut between the cavity columns, plus a keyed subplate |
| E12 | Monte Carlo smoke run, 200 random molds, seed 1 | max r_w ≤ 1.00, max r_s ≤ 1.00, median r_w ≥ 0.75 |

### 3.20 How the sweep builds and validates the tables

**The reduction.** For linear plates, w = α(BC, a/b, t/b, ν, load pattern)·p·b⁴/(E·t³) and σ = β(...)·p·b²/t². So pressure, span, modulus and deflection limit are applied in closed form. Only the shape parameters are swept.

**Sweep 1: coefficient tables** (`MoldSweep coefficients`; about 7,000 FE runs; 2-3 min).

| Variable | Values |
|---|---|
| Boundary set | SS4, C4, Rails (SS-F-SS-F), C2S2, Wall-C (C bottom, F top, C sides), Wall-S, Overhang (C-F-F-F), Pillar panel (3 x 3 point supports) |
| Aspect a/b or L/h | 0.5, 0.75, 1.0, 1.2, 1.4, 1.6, 1.8, 2.0, 2.5, 3.0, 4.0, 6.0 |
| τ = t/b | 0.01, 0.05, 0.1, 0.15, 0.2, 0.25, 0.3, 0.4, 0.5 |
| ν | 0.29 (steel), 0.33 (aluminium) |
| Loaded fraction of a centred patch | 0.25, 0.5, 0.75, 1.0 (plates only) |

- Mesh: 24 elements on the short side. On 5% of the runs (random), an h-refinement check at 48: **the table is rejected if any coefficient changes by more than 0.5%.**
- **Gates** (a release fails if any gate fails):
  - At τ = 0.01, every SS4 and C4 entry is within 1% of Roark 7th ed. Table 11.4, cases 1a and 8a.
  - Every SS4 entry is within 0.5% of the Navier FSDT series (199 x 199 odd terms) at every τ.
  - The free-edge recovery gives β ≤ 0.76 for rails (beam value 0.75).
  - Clamped-edge moments come from the reactions.
- Output: `coefficients.csv` (embedded) with its version and hash.

**Sweep 2: closed-loop Monte Carlo** (`MoldSweep montecarlo --n 2000 --seed 1`; about 5 min). It proves that "table + fixed point" (the fast path) is safe and not wasteful on realistic layouts.
- **Draw at random:**
  - press: ram 4-16 in, gauge 500-3,000 psi;
  - cavities: 1-8 rectangles of 20-200 mm, depth 5-60 mm, placed by `CavityLayout`;
  - grade: 6061, 7075, P20 or H13, at 60-200 °C (only in each grade's allowed range);
  - support A, B, C or E;
  - δ_allow from the flash classes.
- **Then:**
  1. size with the fast path;
  2. build the exact plate model of that layout: cavity outlines as pressure patches, rails as line supports, pillars as springs, the land nodes as outputs;
  3. solve it with MITC4;
  4. record r_w = w_FE,max at land / δ_allow and r_s = σ_FE,max·K_f / σ_allow.
- **Acceptance:**
  - max r_w ≤ 1.00 and max r_s ≤ 1.00: the fast path is never unsafe.
  - median r_w ≥ 0.75: no more than about 10% too thick, because t varies as r^(1/3).
  - Every failure is shrunk into a regression test.
- Output: `montecarlo.csv` and `montecarlo-summary.json` (histograms of r_w per boundary set), in `data/validation/`. TOOL.md quotes the summary.

**Sweep 3: 3D cross-check** (manual, about 20 molds, SOLIDWORKS Simulation if licensed).
- It calibrates the 1.2 wall-root factor and the τ = 0.5 cut-off, and checks platen indentation.
- The result goes into the metadata of `coefficients.csv`: source, date, factor.
- Until it is done, the 1.2 factor and every [K] value is shown as a default in the report.

**The derived table the user sees** (`MoldSweep table` → `data/validation/sizing-table.csv`):
- Required thickness against pressure (5, 10, 20, 35 MPa), span (50-300 mm), material (6061 at 120 °C with E 66 GPa; P20 at 120 °C with E 200 GPa) and δ (0.012, 0.020, 0.025, 0.05 mm), for SS4, C4, Rails and Wall-C.
- Cells with t/b > 0.5 read "add support".
- Example row (SS4, δ 0.025 mm, 6061): b = 100 mm gives 26.3 / 35.4 / 49.7 mm at 5 / 10 / 20 MPa, and "add support" at 35 MPa (N/structural.md 4.6).


---------------------------------------------------------------------------------------------------------------------

## 4. Hooks for the future MCP simulators (machinability, molding and demolding)

Nothing here needs the simulators to exist. Without them the procedure records `not_run`. The export package and the record are built and tested now.

### 4.1 Where they run in the procedure

| Hook | Point in TOOL.md | Simulator | Input | What comes back, and how the design changes |
|---|---|---|---|---|
| H1 | End of Phase 2: plan agreed, nothing built yet | `sim_process`: fill, air and demold, using the shrunk part only | `exports/part/shrunk-part.step`, `layout`, `frame.pull`, `partingLine`, `process.pour` (side, tilt, pour points), `vents`, the material row, T | Air traps and last-to-fill points, fill order, demold force per cavity, a pressure field. The agent changes vents, pour side or tilt and re-runs H1 at most twice. The pressure field is kept for H3. |
| H2 | End of Phase 5: as built, all acceptance checks done | `sim_machinability` per body; `sim_process` on the as-built cavity, with its vents and overflow | `exports/bodies/<ID>.step` with `bodies[].machining` (setup axes, minimum inside radius, maximum depth), the grade, `machinability{}` and hardness from mold-materials; for process, the cavity and vent geometry | Per body: setups, tool reach, corner radius against the tool, cycle time, cost. Process: fill and air for the real vents. Failing checks are routed to the stage that owns them (4.4). |
| H3 | Inside Phase 3 or Phase 5, whenever a pressure field exists | (none: `mold_calc check` consumes the field) | `structural.loadCases.LC1.pressureField` (a CSV grid in the mold frame: u_mm, v_mm, p_MPa) | Walls, spans and inserts are re-sized with the real pressure instead of the uniform p_LC1 |

Rules in TOOL.md:
- Call a simulator only if its alias is in your tool list.
- Never make up results.
- A simulator that is unavailable or fails is recorded as `not_run` or `failed`, with its reason. The report lists it under open items.

### 4.2 The export package (written by `mold_bodies export` and `mold_record`)

```
<mold file name>.mold\
  mold.json                      the design record = the manifest (4.3)
  setup-sheet.html
  part\<name>.SLDPRT             converted copy of a neutral-format molded part (Insert Part points here)
  exports\bodies\<ID>.step       one per mold body: BASE-1.step, SUB-1.step, LID-1.step, INS-C1-1.step, STOP-1.step ...
  exports\part\molded-part.step  the part as received (unscaled, own frame)
  exports\part\shrunk-part.step  PART-REF: scaled, in the mold frame; the cavity copies = this + layout transforms
  checks\<SIM-id>\...            simulator outputs (images, CSV, logs) - referenced from simulations[].artifacts
```

Conventions:
- **STEP flavour.** STEP AP214 by default (`swStepAP` = 214). AP242 when the simulator says so in `mcp.json`.
- **Coordinates.** Model coordinates. `frame` in `mold.json` gives the U, V, W axes and the origin; W is the press and pull direction, with +W toward the lid.
- **Units.** Each STEP file carries its own. Every number in `mold.json` is mm, MPa, degC, kg or kN, as declared in `units`.
- **Names.** File names are the body ids (D3), and they stay stable between exports.

### 4.3 mold.json schema `unify3d.mold/1`

The formal JSON Schema ships as `data/mold.schema.json`. Rules:
- Unknown keys must be ignored by every consumer.
- Additive changes keep `/1`. A breaking change makes it `/2`.
- Every array of objects has a string `id`; merges match on it.
- Faces are stored as SolidWorks persistent-reference bytes in base64 (`GetPersistReference3`), plus a point on the face as a fallback.

```jsonc
{
  "schema": "unify3d.mold/1",
  "id": "5b0c6f0e-...",                                  // GUID; also custom property Unify3D.MoldId
  "tool": { "name": "design-compression-mold", "version": "1.0.0", "addin": "0.2.0" },
  "status": "planning",                                   // planning | sizing | building | verifying | done
  "createdAt": "2026-10-19T20:00:00Z", "updatedAt": "2026-10-19T21:14:03Z",
  "units": { "length": "mm", "angle": "deg", "pressure": "MPa", "temperature": "degC", "mass": "kg",
             "force": "kN", "viscosity": "cP", "time": "min", "display": "imperial" },
  "files": {
    "moldPart": "C:/Molds/HC-70/HC-70 mold.SLDPRT",
    "moldedPart": { "path": "C:/Parts/bushing.STEP", "converted": "part/bushing.SLDPRT", "feature": "MOLD Part bushing" },
    "drawings": [ { "path": "C:/Parts/bushing-revB.pdf", "pages": 2 } ]
  },
  "inputs": {                                             // page values: SI-like value + what the user saw
    "press_pressure": { "value": 13.79, "unit": "MPa", "display": "2000 psi" },
    "shrink_pct":     { "value": 1.8, "unit": "%", "table": 1.5, "edited": true }
  },
  "materials": {
    "part":    { "id": "pu-tdi-ether-90a", "used": { "shrinkPct": 1.8, "sg": 1.10, "viscosityCp": 1200, "potLifeMin": 6,
                 "cureMin": 30, "draftMinDeg": 0.5 }, "confidence": "medium", "sources": [ "S19", "S20", "S3", "K-PU" ] },
    "plates":  { "id": "al_6061_t651", "atTemp": { "tempC": 100, "E_GPa": 66.8, "Sy_MPa": 262.2, "Su_MPa": 291.4, "service": "ok" } },
    "inserts": { "id": "st_p20", "atTemp": { "tempC": 100, "E_GPa": 203.0, "Sy_MPa": 855, "Su_MPa": 979, "service": "ok" } }
  },
  "process": {
    "class": "A", "processType": "liquid_cast_hot", "moldType": "flash_full_land",
    "pour": { "side": "base", "tiltDeg": 0, "secondsPerCavity": 60, "points": [ { "cavity": "C1", "at": [ -80, 0, 20 ] } ] },
    "charge": { "perCavity_g": 454.2, "shot_g": 954, "tolerancePct": 2 },
    "overflowRequired_mm3": 39330, "cureMin": 30, "postCure": { "tempC": 100, "hours": 16 },
    "gauge": { "recommended_MPa": 3.08, "max_MPa": 20.68, "basis": "S7; max limited by press rating" }
  },
  "frame": { "origin": [ 0, 0, 0 ], "u": [ 1, 0, 0 ], "v": [ 0, 1, 0 ], "w": [ 0, 0, 1 ] },
  "shrink": { "pct": 1.8, "basis": "cold_mold_cold_part_steel_ref", "alphaRef": 1.2e-5, "alphaCavity": 2.36e-5,
              "deltaT_K": 80, "scale": [ 1.017387, 1.017387, 1.017387 ], "feature": "MOLD Shrink 1.80%" },
  "part": {
    "volume_mm3": 393290, "area_mm2": 82000, "box_mm": [ 152.4, 101.6, 25.4 ], "projectedArea_mm2": 15484, "perimeter_mm": 508,
    "draft": { "minDeg": 0.5, "faces": [ { "id": "F12", "persist": "<b64>", "class": "needs_draft", "minDeg": 0.0, "area_mm2": 120 } ] },
    "undercuts": [],
    "fragile": [ { "id": "K1", "kind": "core_pin", "d_mm": 2.5, "h_mm": 8.0, "at": [ 10, 5, 0 ], "decision": "insert INS-C1-1" } ],
    "criticalFaces": [ { "id": "S7", "persist": "<b64>", "point": [ 0, 0, 25.4 ], "source": "drawing note 3", "area_mm2": 812 } ],
    "drawing": { "flashClass": "F2", "finish": "SPI B-2",
                 "tolerances": [ { "id": "T1", "what": "thickness 25.4", "tol_mm": 0.10, "closure": true, "sheet": "1/B3" } ] }
  },
  "partingLine": { "type": "planar", "planes": [ { "id": "PL1", "w_mm": 25.4, "feature": "MOLD PL1" } ], "score": 0.92 },
  "airTraps": [ { "id": "AT1", "at": [ 40, 0, 31.0 ], "volume_mm3": 85, "fill": "base", "tiltDeg": 0, "vent": "V3" } ],
  "vents": [ { "id": "V3", "kind": "pl_vent", "at": [ 40, 50.8, 25.4 ], "depth_mm": 0.03, "width_mm": 5, "feature": "MOLD Vents" } ],
  "layout": { "pattern": "grid 2x1", "footprint_mm": [ 406.4, 203.2 ],
              "cavities": [ { "id": "C1", "origin": [ -88.9, 0, 0 ], "rotationDeg": 0 }, { "id": "C2", "origin": [ 88.9, 0, 0 ], "rotationDeg": 0 } ],
              "moats": "separate" },
  "bodies": [ {
    "id": "BASE-1", "role": "base", "folder": "Mold - Base", "createdBy": "MOLD Plate BASE-1", "swName": "Cut-Extrude7",
    "material": "al_6061_t651", "swMaterial": "6061-T6 (SS)",
    "volume_mm3": 2884000, "mass_kg": 7.79, "box_mm": [ 406.4, 203.2, 44.45 ], "centroid": [ 0, 0, 3.2 ],
    "limits": { "mass": "pass", "box": "pass" }, "cavities": [ "C1", "C2" ],
    "machining": { "setups": [ [ 0, 0, 1 ], [ 0, 0, -1 ] ], "minInsideRadius_mm": 3.0, "maxDepth_mm": 25.4 },
    "step": "exports/bodies/BASE-1.step" } ],
  "hardware": {
    "bolts":  [ { "id": "B1", "joint": "BASE-1/SUB-1", "size": "1/2-13", "spec": "ASTM A574", "count": 8, "grip_mm": 50.8,
                  "preload_kN": 53.0, "torqueDry_Nm": 134.6, "torqueLubed_Nm": 101.0, "insert": "helical 2D", "washer": "ASTM F436" } ],
    "dowels": [ { "id": "D1", "d_mm": 12.7, "kind": "round", "at": [ -184, -82, 0 ] }, { "id": "D2", "d_mm": 12.7, "kind": "diamond", "at": [ 184, 82, 0 ] } ],
    "fits":   [ { "id": "FIT1", "bodies": [ "INS-C1-1", "BASE-1" ], "d_mm": 25, "coldInterference_mm": 0.022, "retention": "shoulder + 2 x M5" } ],
    "stops": [], "tcWells": [ { "id": "TC-B1", "body": "BASE-1", "d_mm": 6.35, "depth_mm": 50.8 } ],
    "lift": [ { "body": "LID-1", "kind": "handles" } ]
  },
  "structural": {
    "inputs": { "support": "A", "deltaAllow_mm": 0.020, "cycles": 5000, "tempC": 100 },
    "loadCases": { "LC1": { "p_MPa": 1.551, "pressureField": null }, "LC1x": { "p_MPa": 8.67 }, "LC2": { "F_kN": 296.5 },
                   "LC3": { "deltaT_K": 80 } },
    "results": { "walls": [ { "id": "W-C1-U+", "w_struct_mm": 9.26, "w_drawn_mm": 40.2, "governs": "flash system" } ],
                 "floors": [ { "body": "BASE-1", "t_mm": 19.05, "governs": "TC well" } ] },
    "coefficients": { "file": "coefficients.csv", "version": "1.0.0", "sha256": "<hash>" }
  },
  "checks": [ {
    "id": "STR-BEAR-PL1", "source": "mold_calc@1.0.0", "type": "bearing", "body": "BASE-1", "loadCase": "LC2",
    "demand": 9.58, "capacity": 131.1, "unit": "MPa", "margin": 12.68, "status": "pass",
    "method": "S34, D_b = 2 (N < 1e5)", "stage": "3", "note": "", "waivedBy": null } ],
  "simulations": [ {
    "id": "SIM-1", "hook": "H1", "kind": "process", "server": null, "tool": "sim_process", "status": "not_run",
    "reason": "no process simulator configured", "requestedAt": null, "finishedAt": null,
    "inputs": { "files": [ "exports/part/shrunk-part.step" ], "record": "mold.json" },
    "summary": null, "artifacts": [], "checks": [], "overrides": {} } ],
  "stages": [ { "id": "4.3", "name": "Cavities", "status": "done", "checkpoint": "153012-stage-4-3",
                "features": [ "MOLD Cavity BASE-1" ], "at": "2026-10-19T21:02:00Z" } ],
  "decisions": [ { "id": "DEC-3", "at": "2026-10-19T20:31:00Z", "topic": "parting line", "choice": "PL1 at the top face",
                   "options": [ "PL1 top face", "PL2 mid-height" ], "by": "user" } ],
  "setupSheet": "setup-sheet.html"
}
```

Allowed values:

| Field | Values |
|---|---|
| `checks[].type` | deflection, stress, fatigue, bearing, boltSeparation, boltLoad, boltFatigue, stripping, fit, weight, box, interference, clearance, draft, undercut, criticalSurface, airTrap, machinability, fill, demold, cure |
| `checks[].status` | pass, warn, fail, not_run, waived |
| `margin` | capacity/demand - 1 when capacity is an upper limit. For lower limits (e.g. clearance), demand/capacity - 1. |

### 4.4 How results feed back

- **Merging.** A simulator's reply is merged with `mold_record merge`:
  - one `simulations[]` entry, with status, summary and artifacts;
  - its findings as `checks[]` entries, with `source = "<server>/<tool>@<version>"`;
  - optional `overrides`: paths into the record that the agent applies after confirming. Example: `{"structural.loadCases.LC1.pressureField": "checks/SIM-2/pressure.csv"}`.
- **Fixing.** The agent fixes each failing check in the stage that owns it:
  1. `checkpoint restore` to just before that stage, if the stage has to be rebuilt;
  2. re-run the stage and everything after it;
  3. re-run the hook once.
- **Waivers.** Only the user can waive a check. It is recorded with `waivedBy` and the reason.

| Finding | Owning stage | Usual fix |
|---|---|---|
| Air trap, short fill, bad fill order | Phase 2 plan → 4.7 | Add a vent or riser, change the pour side or tilt |
| Demold force too high, sticking | 4.8 (and draft: a part change, ask the user) | Air poppet, jack screws, more draft if the part allows |
| Pressure field above LC1 | Phase 3 (H3) | `mold_calc check` with the field → thicker walls or inserts, steel |
| Inside radius smaller than the tool | 4.3 / 4.4 | Bigger corner radius if the part allows (ask), or make the feature an insert |
| Depth over 4 x the tool diameter | 4.4 | Insert, or an EDM note |
| More than 2 setups | 4.2 / 4.4 | Split the piece, or make the feature an insert |
| Cycle time or cost too high | Phase 3 | Grade, thickness, number of pieces |

### 4.5 How they connect (decided now, built in stage 10)

**Why the add-in hosts the MCP client.** The Messages-API MCP connector (beta `mcp-client-2025-11-20`, `mcp_servers` plus `mcp_toolset`) only reaches remote `url` servers, and those can't read the local STEP files. So the client lives in the add-in:
- `src/SwAgent/Mcp/McpClient.cs`: JSON-RPC 2.0 over stdio and Streamable HTTP; `initialize`, `tools/list`, `tools/call`, progress notifications.
- `src/SwAgent/Mcp/McpRegistry.cs`: reads `%APPDATA%\SwAgent\mcp.json`, for example:

  ```
  {"servers":[{"name":"moldsim","command":"C:/Tools/moldsim/moldsim.exe","args":["--mcp"],"role":"process"},
              {"name":"camcheck","url":"http://cam-server:8080/mcp","role":"machinability"}]}
  ```

**Tools it registers:**
- Each remote tool as a proxy `[AgentTool]`, named `mcp__<server>__<tool>`.
- Two stable alias tools, `sim_process` and `sim_machinability`, which route to the server with that `role`. TOOL.md only ever names the aliases, so a vendor change doesn't touch the procedure.
- Aliases are registered only when a server for that role is configured, so no placeholder tools cost tokens today.

**Refresh.** Tool definitions are fetched once at panel start (store.ts:1013). Add a bridge event `tools.changed`, raised when `mcp.json` changes or a server connects; the store re-fetches `tools.list`.

**Consent.** Proxy tools don't change the model (they aren't WRITE_TOOLS), but they send files to another program. The first call to each server asks in the panel ("Send the mold's STEP files and mold.json to moldsim?"). The answer is remembered per server.

**Timeouts.** 10 min per call, with a progress line in the status bar. A longer run returns `status: "running"` and a job id. `mold_record` stores it, and the next Phase 7 entry polls it.

---------------------------------------------------------------------------------------------------------------------

## 5. Important things the user did not mention (by priority)

Each row says how the tool handles it. **Input** = a page field. **Default** = data or rule, shown in the plan. **Question** = the agent asks, at the gate shown.

| # | Item | Why it matters | How it's handled |
|---|---|---|---|
| 1 | What "press pressure" means; ram area or rated tonnage | The same gauge reading gives 2.25x more pressure on a 12 in ram than on an 8 in one. Every force, bearing and gauge number depends on it. | **Input** `press_pressure_is`, `press_rated_force`, `press_rated_gauge` (remembered). **Question** in Phase 0 when "Not sure". |
| 2 | Liquid pour vs compound compression vs positive (process class) | Lands, vents, overflow, pressure and structure differ about 10x between them | **Default**: derived from the material's `processType` (D4) and shown in the plan |
| 3 | Aluminium at the press temperature: derating, over-ageing, steel-insert and dowel fits loosening, lid/base mismatch | 7075 keeps 37% of its yield after 10,000 h at 149 °C. Steel inserts come loose when hot. 0.45 mm mismatch over 300 mm. | **Default**: long-term derating (S10-S12); a grade above `never_above` is blocked; insert and dowel fits from S59-S60; round + diamond pins (S58). **Question** at G3 when T > derated: switch to steel? |
| 4 | Part orientation and pull direction | Decides undercuts, depth and air traps; never stated | **Default** from `mold_part`, which ranks the axes. **Question** at G1 only if the two best are within 10%. |
| 5 | Press envelope: platen size, heated area, daylight, stroke, clamping | The mold must fit and sit inside the heated area | **Input** `platen_size`, `daylight` (optional, remembered). When not set: the check is skipped and listed as open. Stroke and clamping: **question** only if the plan needs ledges or a tall stack. |
| 6 | Shrink basis, per-axis shrink, tolerance against the shrink spread, post-cure | 0.1-0.25% errors in aluminium molds; SMC, PEEK and PTFE shrink differently per axis | **Default** S64 (corrected for mold metal and temperature). **Question** for per-axis shrink for compound and thermoplastic families. Steel-safe flag S65. |
| 7 | Critical dimensions across the parting line; flash class | Closure dimensions carry the flash thickness and looser tolerances | **Default**: read from the drawing; PL candidates scored on closure dimensions. **Question** in the plan if the drawing has no flash class. |
| 8 | Lid stops and closed height; land bearing | The press force otherwise hammers the cut-off land | **Default**: class A uses the full-face land with S33-S35; class B uses 4 hardened stop pads outside the overflow. Max gauge on the setup sheet. |
| 9 | Charge, overflow volume, pot life against pour time | Short shots; overflow too small; resin gels while pouring | **Default** S66-S68. **Question** for the pour time per cavity only when n > 1 and the pot life is under 20 min. |
| 10 | Air traps depend on pour orientation and tilt; vent depth by viscosity | Bubbles at high points; vents that leak | **Default**: priority-flood traps, vents from the row's `ventDepthMm`. **Question** in the plan: pour side and tilt (overlay). |
| 11 | Demolding without an ejector | Hot, soft parts stick; prying damages lands | **Default**: pry slots (shop standard) + jack screws; air poppets for soft deep cups. **Question** in the plan form: demold method. |
| 12 | Operator safety: hot handling, crush zones, eye bolts, weights, isocyanate and MOCA | Burns, strains, exposure | **Default**: handles above 5 kg, outside the platen footprint; lift holes for pieces over the limit (DIN 580 ratings in mold-materials); weights engraved and on the sheet; the material's `hazards` on the setup sheet |
| 13 | Heating and thermocouples; preheat time | Platen variation over 5 °C undercures parts | **Default**: one TC well per piece (shop standard; ends 9 mm from the cavity); preheat to TCs within 3 °C, then 10 min, on the sheet |
| 14 | Alignment: pin engagement before entry, anti-rotation, fine registration | Damaged cores; lid placed rotated 180° | **Default**: dowels per shop standard with a key offset; pins engage ≥ 12 mm before the plug; tapered locks when mismatch must be < 0.05 mm (from the drawing tolerance) |
| 15 | Multi-piece base: keying, seal lines, joints away from cavities | Pieces pushed apart; flash in joints | **Default**: keyed into a subplate (S37-S41); joints ≥ 15 mm from cavities. **Question** at G3 when a split is needed (options with masses). |
| 16 | Thread inserts, hardened washers, torque, re-torque when hot | Stripped aluminium threads; bolts near yield when hot | **Default** S51-S55; setup sheet: torques dry and lubricated, "re-torque hot after 2-3 heat cycles" |
| 17 | Corrosive or abrasive materials (FKM, CR, PVC releasing HCl; glass-filled) and platinum-cure inhibition (LSR) | Pitted or worn aluminium; LSR that won't cure | **Default** from the row's `moldCompat`, `corrosiveness`, `notes`: grade advice (420 SS, plating); warning in the plan; note on the setup sheet |
| 18 | Release agent, finish and coatings (SPI per face; hard-anodize growth ~0.1 mm) | Release, appearance, fit | **Default**: release from the row; finish from the drawing. **Question** only if the drawing is silent and the part is clear or cosmetic. |
| 19 | Inserts or overmolded hardware in the part | Hold-down pins; 0.5 mm minimum rubber cover | **Question** in the plan form (none / describe) |
| 20 | Machine-shop limits: 3-axis, smallest cutter, corner radius ≥ depth/3, stock sizes | Pieces that can't be machined; costly blanks | **Default**: 3-axis rule (2 setups at most), stock rounding S75, blank allowance; `max_plate_size` input. **Question** for the smallest cutter only when cavity corners are sharp. |
| 21 | Maintenance and wear parts; a cycle counter | Lands, pins, stops and threads wear | **Default** by cycle tier (T1-T4, N/design-rules.md 14.2); setup sheet maintenance section |
| 22 | Marking and traceability | Plates mixed up between molds; wrong cavity | **Default**: custom properties + setup sheet list (mold id, cavity numbers, matching ids, weights). Engraving is not modelled in v1. |
| 23 | Long runs and state (context clearing, the 200-step limit, model drop on continue) | A multi-hour design loses earlier results or quietly changes model | **Default**: `mold.json` + journal + checkpoints; `long_run` (D12) |
| 24 | Starting point: an open part is required; STL won't work | ToolRunner refuses without a document (ToolRunner.cs:63-67) | Page description says so; Phase 0 checks; STL refused |
| 25 | Cost and lead time | Drives aluminium vs steel, cavities, inserts | **Default**: the report shows the material volume x `cost_index_per_kg` (order of magnitude). **Question** only if the user mentions a budget. |

---------------------------------------------------------------------------------------------------------------------

## 6. Build order and test plan

### 6.1 Stages (each one can be built, tested and merged on its own)

| Stage | Content | Depends on | Needs SolidWorks? | Done when |
|---|---|---|---|---|
| 0 | **Spikes** S1-S6 (page, 2.1.6) and S7-S12, each under an hour (see after this table) | none | yes | A one-page result per spike, decisions locked |
| 1 | **Data.** Move the generators to `tools/dev/mold-data/`; ship `data/*.json` with the changes from 1.5; `MoldData` loader with overrides; data validation tests | none | no | Data tests green; generator output = shipped files (CI diff) |
| 2 | **Calculator core** (pure): AtTemperature, Press, Shrink, Charge, Fatigue, Walls, Plates (cases A-F via tables), Bearing, Keys, Bolts, Threads, Fits, Pins, Layout, Split, Rounding, Sizing; `examples.json` E1-E11 | 1; temporary coefficients from N/proto | no | E1-E11 within 0.5% (E3: 1%) |
| 3 | **FE solver and sweep**: MITC4, banded LDLᵀ, Navier, recovery; `MoldSweep` console; `coefficients.csv`; Monte Carlo; sizing table | 2 (API) | no | T1-T18 pass; Sweep 1 gates pass; Monte Carlo acceptance passes (E12 smoke in CI) |
| 4 | **Agent tools `mold_calc` and `mold_record`**; ToolRegistry `JToken` schema fix; registration; setup-sheet filler; `mold.schema.json` | 2, 3 | record actions need a saved part; calc doesn't | ToolRegistry invoke tests with JSON in/out; record merge/validate/summary tests; schema validation of samples |
| 5 | **Page extensions**: Units, ToolData, ToolPage, ToolInputs, payload; `tools/dev/page-test/` test tool using every new feature | 0 (S1-S6) | yes for the page | Units and ToolData unit tests; manual page script (6.2) passes; Smart Rebuild and the user's flash-grooves and parametric-block pages unchanged (screenshot compare) |
| 6 | **Categories, ribbon title, long runs, requires**: UserTools, AgentRibbon, AgentController, ToolsPalette, systemPrompt, store sticky run, runAgent `maxSteps` and TTL | none | ribbon yes; panel in the browser mock | BuiltInToolsTests extended; mock shows category sections; a 600-step limit run test; follow-up keeps the model |
| 7 | **Geometry**: `MoldApi` (InsertPart, Orient, ScaleForShrink, CopyBody, Plate, Subtract, SplitByPlane, Folder, SetBodyMaterial, Bodies); `mold_part`; `mold_bodies`; `ask_selection`; overlay `face` and translucent temporary bodies; `.slddrw` → PDF; 20 MB PDF cap | 0 (S5, S7-S12), 4 | yes | Fixture tests F1-F6 match the expected numbers (6.2) |
| 8 | **Flash system port** (`Mold.FlashGrooves`, one fillet per body) and Hole Wizard wrappers | 7 | yes | The flash-grooves verified cases reproduce: the 10 x 8 x 1.5 in test plate (lip 0.125 exact, 3 + 2 runners per side) and the hc-64 replica |
| 9 | **The tool folder**: TOOL.md, inputs.json, data, macro.cs, icon; BuiltInToolsTests for it; end-to-end runs R1-R6 | 1-8 | yes | All acceptance checks A1-A15 pass on R1-R5; R6 reviewed with the user; step and token budget within target |
| 10 | **MCP client and simulator aliases** (4.5) | 9 + a simulator | yes | H1 and H2 run against a stub MCP server that returns canned results; feedback routed to the right stages |

The stages fall into three groups:
- **Pure stages** (1-4) can start at once.
- **Page and panel stages** (5-6) can run in parallel with them.
- **SolidWorks stages** (7-9) need stage 0.

**Spikes S7-S12** (stage 0):

| Id | What it checks |
|---|---|
| S7 | `InsertPart3` with each option flag; STEP → SLDPRT conversion through `LoadFile4` + `SaveAs3` + `QuitDoc` |
| S8 | Body-folder membership persists after later cuts and splits |
| S9 | `CreateFeatureFromBody3` export in a hidden document (`DocumentVisible(false)`) and re-activating the mold document |
| S10 | `IBody2.SetMaterialProperty` per body; the material library names (fills `swMaterial`) |
| S11 | `HoleWizard5` with feature scope in a multi-body part |
| S12 | `RayIntersections` speed: 20,000 rays should take under 2 s |

### 6.2 Test plan

**Unit tests** (headless; `tests/SwAgent.Tests/Mold/`; run in `dotnet test` and `scripts/test.ps1`):

| Test class | What it checks |
|---|---|
| UnitsTests | Exact factors (psi 6,894.757293168 Pa; lb 0.45359237 kg; US ton-force 8,896.443 N; °F offset); display rounding; IPS/Custom mapping |
| ToolDataTests | Distinct family entries in file order; RowFilter with ValueMap; array containment (`use`); dotted Fills paths; null hides; override merge by id; default resolution order (2.1.3); the `|unit` template |
| MaterialsDataTests | 96 rows, identical keys, min ≤ typ ≤ max, known `processType`, one `isDefault` per family, every Fills path exists; grades have `short`, `use`, `isDefault`; 6061 fatigue = D18 |
| PressTests | E1, E2 (press part), E7 |
| ShrinkTests | E6, E2 shrink |
| ChargeTests | E2 charge; the overflow rule with and without runner exits |
| FatigueTests | 6061 and P20 allowables at N = 1e3 … 1e6 against the reference script |
| WallTests | E2 wall, E3 (aluminium and P20, both N) |
| PlateTests | T16 (= E9); case D strip; b_max inversion (S31) |
| BoltTests | E4, E5; the sizing loop picks the smallest passing (size, n) |
| ThreadTests | E8; insert re-check |
| FitsTests | E10; the dowel rule (S58) |
| SplitTests | E11 |
| RoundingTests | Stock lists, skim, 0.125 in step |
| SizingTests | Full design → checks; deterministic (same input, byte-identical output) |
| FeTests | T1-T15 from N/structural.md 9.5 (Navier SS 0.004062 qa⁴/D; clamped 0.001265; clamped edge moment -0.0513 qa² from reactions; FSDT ratios 1.052 / 1.207 / 1.466; cantilever 0.125; Winkler w = q/k exact; equilibrium 1e-9; mirror symmetry 1e-10) |
| CoefficientsTests | Regenerate 50 random rows within 0.5%; thin-limit Roark gate; the SHA in the header matches |
| MonteCarloSmokeTests | E12 (200 samples) |
| AirTrapsTests | Synthetic heightmaps: flat top gives 0 traps; a dome gives 1 trap at its top with the exact volume; a dome connected to an outlet gives 0; a 10° tilt turns the trap into a vented corner |
| RasterTests | Projected area of a square and of a circle within 0.5%; perimeter within 1% |
| RecordTests | Deep merge (objects, id-arrays, replaced arrays); validation errors; summary ≤ 6,000 chars; `save_default` writes only valid keys; schema validation of sample files (JsonSchema.Net, tests only) |
| SetupSheetTests | Every placeholder resolved for a full sample record; dual units present |
| BuiltInToolsTests (extended) | `design-compression-mold` loads: kind tool, confirm always, long_run, max_steps 600, category, 32 inputs parse; data files load; every ChoicesFrom resolves; every `requires` name is registered (a ToolRegistry built with `new MoldTools()`); Smart Rebuild assertions unchanged |

**SolidWorks integration** (scripted fixtures in `tests/SwAgent.Tests/Fixtures/mold/`, run through `run_tool_macro` or a test harness in SolidWorks):

| Fixture | Part | Expected |
|---|---|---|
| F1 | Puck Ø100 x 25 mm | Volume 196,350 mm³ ± 0.1%; projected area 7,854 mm² ± 0.5%; 0 undercuts; 0 traps (base pour) |
| F2 | Bushing Ø60 / Ø20 x 40 mm | Core pin D = 20, H = 40 (H/D 2.0, so not flagged; flagged at Ø9) |
| F3 | Block with a side groove | One undercut face set, side reported |
| F4 | Cup with a lid-side dome | 1 air trap at the dome top ± 1 mm; volume ± 5% |
| F5 | Part with 2 mm ribs | Fragile steel-wall flags |
| F6 | Three touching window plates (hc-64-like) | `GroupOf` treats them as one parting face; runners only on the outer edge |

`MoldApi` operations on F1/F2 are checked against volumes and boxes (± 0.1%, ± 0.01 mm) and against the folder contents.

**Page script** (`tools/dev/page-test`, run by hand in SolidWorks; 15 min):
- cascade family → grade, and the fields fill;
- edit, re-pick, overwrite;
- the null-hiding grade (rubber hides viscosity);
- ShowWhen on mold material and pressure meaning;
- Optional on/off;
- the size boxes;
- an IPS document shows psi/°F/lb and an MMGS document shows MPa/°C/kg;
- Remember after OK;
- the payload text matches 2.1.5;
- a 7-group page has no id collision.

**End-to-end acceptance** (stage 9, real runs at intelligence max; each must pass A1-A15 and match a hand check of the key numbers within 2%):

| Job | Description |
|---|---|
| R1 | PU puck, 1 cavity, 6061, class A, starting from a blank part with a STEP file |
| R2 | PU bushing, 4 cavities, core pins: combined vs separate moats, steel insert pins |
| R3 | PU part with a lid-side boss: air trap, pour side and tilt question, vent |
| R4 | EPDM 70A gasket, 8 cavities, class B, at 170 °C. With "Aluminium + steel inserts" the tool must warn (6061 above 150 °C derated limit) and offer P20. Stop pads, overflow grooves, bump schedule. |
| R5 | Large PU part over the 23 kg plate limit: split + keyed subplate + bolts (E4-like) |
| R6 | Rebuild the user's hc-64 mold from its part, and compare with the real mold together with the user (qualitative review) |

**Regression:**
- All existing tests.
- Smart Rebuild run and page.
- The user tools flash-grooves, parametric-block and pu-compression-mold: their pages are pixel-identical and their macros run.
- The ribbon with no categories is unchanged.

**Budgets** (measured on R1-R5 and recorded in the stage 9 report):
- Steps per run: R1 ≤ 250, R5 ≤ 600.
- Total input tokens and cache hit rate (`cache_read_input_tokens` > 80% of input after the first request).
- Wall time per phase.
- Questions asked: at most 1 form per gate in "Ask on big ones".

**Release gates:**
- every unit test green;
- Sweep 1 gates and the Monte Carlo acceptance pass;
- R1-R5 pass;
- the data files marked "estimate" or "low" are shown as such in the page `info` line and on the setup sheet;
- the user has confirmed the items in 6.3.

### 6.3 Confirm with the user before release (the only open items)

1. **Flash system.** The flash-groove rules (lip 0.125 in, moat 0.5 x 0.125 in, runners, R0.25 in) replace v1's 0.060 in land and 0.125 x 0.0625 in ring as the class A default (D14).
2. **Default limits.** Lid 18 kg (40 lb); plate 23 kg (51 lb); plate size 600 x 400 x 150 mm.
3. **Default material.** TDI-ether 90A as the first-run part material, and the PU process data. Hot-cast PU shrink is an engineering estimate: confirm with the user's datasheets or the first article.
4. **Minimum plate rule.** 0.05 x the longest side, instead of the notes' 0.08: it keeps lids near the user's proven 0.75-0.875 in.
5. **Molds sit directly on the platen** (support case A), as the slide-in process suggests.

---------------------------------------------------------------------------------------------------------------------

## Appendix A: research notes used (all in N/)

| Stream | Notes and data files |
|---|---|
| pm-page | `pm-page.md` |
| pipeline | `pipeline.md` |
| agent-abilities | `agent-abilities.md`, `api-dump.txt`, `api-dump2.txt` (reflection over the SolidWorks 2026 interop) |
| existing-mold-knowledge | `existing-mold-knowledge.md` (the user's pu-compression-mold v1, the flash-grooves rules and code, the knowledge base) |
| materials | `materials.json` (96 rows), `materials.md`, `gen_materials.py` |
| mold-metals-fasteners | `mold-materials.json`, `mold-materials.md`, `gen/build_materials.py`, `gen/out.txt` |
| structural | `structural.md`, `proto/plate.py`, `validate.py`, `sweep.py`, `examples.py`, `table.py`, `plates.txt`, `wall.txt` |
| design-rules | `design-rules.md` (26 sections, about 40 sources) |
| this design | `check/method_check.py` (reference script for E1-E10 in 3.19) |

## Appendix B: risk register (top 12)

| # | Risk | Effect | Mitigation in this design |
|---|---|---|---|
| 1 | Hot-cast PU shrink and viscosity are estimates (52 of 96 material rows are low confidence) | Cavities off by up to about 0.5% | Confidence shown on the page and setup sheet; editable fields; steel-safe flag (S65); `save_default` for shop values |
| 2 | The long-term aluminium tables (ASM/Kaufman) and the 6061 fatigue curve were taken from memory or a secondary fit | Over- or under-designed hot aluminium molds | D18 conservative choice; "verify against MMPDS/ASM" is a release task; statuses come from `max_service_C` |
| 3 | The judgement factors (δ classes, 1.2 root factor, bearing /3, 0.05·L plate minimum, λ_pin 0.5) are uncalibrated | Wrong sizes at the extremes | One editable file (sizing-defaults.json); Monte Carlo against FE; Sweep 3 3D calibration |
| 4 | SolidWorks page behaviour after `Show2` (S1-S4) is unverified | Cascading or units don't work | Spikes first; per-family combobox fallback (S2) |
| 5 | Body identity: renames may not persist; folder membership after splits unverified (S5, S8) | Bodies picked wrong; renumbering cascades | Folders + geometric order + `MOLD` feature names; body operations appended at the end of the tree |
| 6 | A run can need more than 600 steps, or exhaust the context | Stops mid-build | Phases end with record writes; sticky continuation; checkpoints |
| 7 | Air-trap analysis is 2.5D (upper envelope + flagged internal ceilings) | Missed traps in complex parts | Internal ceilings reported for a manual check; H1 process simulator later |
| 8 | `InsertCombineFeature` / `InsertScale` selection traps (with nothing selected, Scale scales every body) | A wrong body is scaled or cut | `MoldApi` self-checks (volume ratios) after every operation |
| 9 | The flash-system port carries over the fillet bugs (fillet over several bodies; Propagate failed on hc-64) | Fillets lose edges on rebuild | One fillet per body, no Propagate; acceptance A10 measured |
| 10 | PDF drawings near the request size limit | Requests fail | 20 MB cap; ask for a smaller export |
| 11 | Tool-definition growth: 4 tools, about 2k tokens, in every chat | Cost | Short descriptions; cached prefix |
| 12 | Simulator contract drift | Hooks break | Versioned schema, role aliases, `not_run` fallback, stub-server tests |
