# Compression mold - structural sizing method and validation plan

Scope: built-in tool "Design compression mold for plastics and resins" (Molding & Toolmaking). This file covers how the
tool sizes plate thickness, wall thickness, bolts, inserts and fits from the press and material inputs, and the
small plate solver plus parameter sweep that validates the sizing rules. Nothing in the repository was changed.

Prototype (Python + numpy, used to produce every FE number in this file):
`C:/Users/msmit/AppData/Local/Temp/claude/C--Users-msmit-Documents-Claude-Code-SolidWorks-Agent-Plugin/2b455cdd-1c60-4ed3-9eb5-8eb641a4b994/scratchpad/mold/proto/`
- `plate.py`: MITC4 Mindlin plate on a rectangular grid, plus Navier series (Kirchhoff and first-order shear deformation theory (FSDT))
- `validate.py`: closed-form validation runs (section 9.5)
- `sweep.py`: coefficient tables (sections 4.4 and 5.2); the output is in `plates.txt` and `wall.txt`
- `examples.py`, `table.py`: the worked examples and the example sizing table

---

## 0. Main conclusions (read this first)

1. **Size for stiffness first, then check stress.** A gap at the parting line or at an insert lets the material flash.
   Deflection limits of 0.01-0.08 mm decide almost every mold plate and wall. Stress and fatigue decide only (a) cavity
   walls in aluminium at 1e5 cycles or more, (b) bolts and threads, (c) bearing faces.
2. **"Press pressure" is ambiguous and must be asked as two numbers.** You need a pressure plus what it means (hydraulic
   gauge, or pressure on the part), and the ram diameter or rated tonnage. 3000 psi gauge on an 8 in ram is 75 US tons.
   On 48 in^2 of cavity that is 3,142 psi on the part. The same gauge reading on a 12 in ram gives 7,069 psi on the part.
3. **Plates fully backed by a flat platen do not bend.** Lid and base bending (Roark) only matters over unsupported
   spans: rails or parallels for knockouts, overhang past the platen, heater or ejector pockets. The best design is
   no span. Each unsupported span gets a pillar or rail spacing from the tables in section 4.
4. **Mold plates are thick plates.** Roark and Timoshenko assume thin (Kirchhoff) plates, and their coefficients
   underestimate deflection at the thickness-to-span ratios molds use. At thickness/span t/b = 0.3 the error is 47%
   for a simply supported square plate and 156% for a clamped one (FE and exact FSDT, section 4.4). The tool must use
   the thick-plate (Mindlin) coefficient tables, not the Roark values.
5. **Never carry the cavity's separating force by bolt friction.** With 3,142 psi on a 12 in x 1.5 in side, the force
   is 56.5 kip (252 kN). Carrying it by friction needs 424-848 kip of preload. Key the side pieces into a step or pocket
   in the base. Bolts only hold the pieces together, or carry tension with a separation factor of 2 or more.
6. **Steel bolts in aluminium plates gain preload when hot.** For a 1/2 in bolt with a 2 in grip at dT = 100 K the gain
   is about 3.4 kip (+27% of a 0.75-proof preload). Tighten to 0.5-0.6 of proof load, not the usual 0.75.
7. **Aluminium temperature limit.** 6061-T6 yield after long exposure is 276 MPa at 24 C, 262 at 100 C, 214 at 149 C
   and 103 at 204 C. Above about 150 C an aluminium mold is a short-life tool. Recommend tool steel for platen
   temperatures of 160 C and up (most rubber, silicone and thermoset work).
8. **Mixed aluminium and steel needs a thermal centre.** Over 300 mm at dT = 140 K, aluminium grows 0.45 mm more than
   P20. Locate with one round dowel at the thermal centre and one slotted (diamond) dowel on a radial line. A steel
   insert press-fit cold in aluminium comes loose when hot unless the cold interference is at least
   d * (alpha_Al - alpha_st) * dT. For 25 mm at dT = 140 K that is 0.038 mm.

---

## 1. Symbols and units

The tool works internally in SI (N, mm, MPa, K). The page and reports show the user's units. 1 psi = 0.006895 MPa;
1 lbf = 4.448 N; 1 US ton (force) = 2,000 lbf = 8.896 kN; 1 metric tonne-force = 9.807 kN.

| Symbol | Meaning |
|---|---|
| F | press force on the mold (N) |
| p_g | hydraulic gauge pressure (MPa) |
| A_ram | total ram piston area, all rams (mm^2) |
| A_proj | projected area of all cavities + runners/overflows + flash lands, in the press direction (mm^2) |
| p | design cavity pressure (MPa) |
| a, b | plate panel long and short side (mm); b is always the short (governing) side |
| t | plate thickness (mm); tau = t/b |
| h, L, w | cavity wall loaded height, wall length, wall thickness (mm) |
| E, nu, G | Young's modulus, Poisson ratio, shear modulus at the mold temperature |
| D | plate flexural rigidity E t^3 / (12 (1 - nu^2)) |
| delta_allow | allowable parting-line gap (deflection) (mm) |
| alpha, beta | dimensionless deflection and stress coefficients: w = alpha p b^4/(E t^3), sigma = beta p b^2/t^2 (Roark form) |
| CTE | coefficient of thermal expansion (1/K) |

---

## 2. Press force and the pressures on the mold

### 2.1 What "press pressure 3000 psi" can mean
In compression shops it usually means one of three things:
1. **Hydraulic gauge (line) pressure** on the press manometer. This is the most common meaning. Many presses are
   built for a 2,000-3,000 psi hydraulic system, and operators "run at 2000". It says nothing about pressure on the
   part until you know the ram area.
2. **Specific (molding) pressure on the part's projected area**, as given by a compound or recipe sheet. Examples:
   "mold at 1000-2000 psi", or ISO 2393 rubber test sheets at 3.5 MPa (about 500 psi) minimum on the cavity area.
3. Rarely, force divided by the whole mold face area.

A forum example shows why these cannot be swapped between machines: on a 500-ton press, 70 tons and 2000 psi are not
the same setting.

**The tool must ask:**

| Field (inputs.json Id) | Type | Notes |
|---|---|---|
| `press_pressure` | number + unit | The value the user knows |
| `press_pressure_is` | options: "Hydraulic gauge" / "Pressure on the part" | No default. Ask when unclear |
| `press_size_by` | options: "Ram diameter" / "Rated tonnage at max gauge" | |
| `ram_diameter` | length; with `ram_count` (default 1) | A_ram = n pi D^2/4 |
| `rated_force` + `rated_gauge` | force, pressure | A_ram = rated_force / rated_gauge (for example 50 ton at 3000 psi gives 33.3 in^2, a 6.51 in ram) |
| `platen_size` | L x W | The mold footprint must fit inside it (section 2.6) |
| `daylight` | length | Maximum opening between platens |
| `stroke` | length | Must cover plug engagement + part removal clearance |
| `platen_heated`, `platen_temp` | checkbox, temperature | The press temperature input already exists |
| `mold_handling` | options: "Stays in press" / "Shuttled out each cycle" | Shuttled molds: handling weights decide; thermal cycling adds fatigue |
| `mold_support` | options: "Flat on platen" / "On rails or parallels" / "Bolted to platen" | Decides the plate bending cases (section 4.2) |

### 2.2 Formulas
- Press force: F = p_g * A_ram (subtract the ram return-spring or weight term only if the press maker gives one; it
  is usually under 2%).
- Rated-tonnage cap: F <= F_rated. If the requested gauge setting is above the rated gauge, stop and warn.
- **Design cavity pressure (worst case):** p = F / A_proj. Assume the charge is overfilled and the lands are not yet in
  contact, so the material (nearly incompressible) carries the whole press force. This is the load case for plate
  bending and cavity walls in flash-type and semi-positive molds. For positive (plunger) molds it holds for the whole
  cycle.
- If the material data gives a needed molding pressure p_req, tell the user the gauge setting:
  **p_g,set = p_req * A_proj / A_ram**. Also flag p > 1.5 p_req: too much force means excess load and wear on the
  mold.
- **Bearing (closed) case:** once the mold is closed metal to metal, the force goes through the bearing faces:
  sigma_bear = (F - p_res * A_proj) / A_bear. Use p_res = 0 (no residual material pressure, worst case).
  A_bear = mold face contact area outside the cavities, flash lands and overflow grooves. Faces relieved more than
  0.1 mm do not count.
- **Off-centre load:** the centroid of A_proj (weighted by cavity) must be within +/-5% of the platen length or width
  from the ram axis. Otherwise the platen tilts and the flash is uneven. The 5% is a judgement value; check it against
  the press maker's eccentric-load rating if given.

### 2.3 Typical specific molding pressures
These ranges are only defaults for the material lookup, which another workstream owns. Ranges for the page:
- Natural and synthetic rubber compression: about 3.5-14 MPa (500-2,000 psi) on the cavity area. ISO 2393 test sheets
  use 3.5 MPa minimum. Soft compounds about 1.5-2.5 MPa; parts with metal inserts up to 8 MPa.
- Silicone HCR: many grades flow at about 1.4 MPa (200 psi) (Dow XIAMETER note via forum).
- Millable polyurethane: about 7-14 MPa. Castable PU closed in a press: under 1-2 MPa.
- SMC/BMC and phenolic/epoxy thermosets: about 3.5-40 MPa depending on the compound. Take it from the compound sheet.

### 2.4 Load cases the tool evaluates

| Id | Case | Load | Checks |
|---|---|---|---|
| LC1 | Fill (worst) | p = F/A_proj on every cavity floor, plug face and wall up to the cavity depth (hydrostatic, the same sideways as vertically) | plate bending, walls, side-piece joints, inserts |
| LC2 | Closed on lands | F over A_bear | bearing and hobbing, land crush, pillar compression |
| LC3 | Thermal | uniform dT = T_press - 20 C; gradient for shuttled molds | fits, dowels, bolt preload change |
| LC4 | Handling | self weight x 2 (lifting shock) on pry slots, lift handles and eye bolts | handles, threads |
| LC5 | Demold | ejector or knockout force; prying at pry slots (later from the process simulator) | knockout plate, pry slot edges |

### 2.5 Worked example (used through this file)
The press has an 8.00 in ram and runs at 3000 psi gauge. The mold has 2 cavities, each 6.0 x 4.0 in projected
(48.0 in^2 total). Base 16 x 8 in, 6061-T6, press at 120 C.
- A_ram = pi * 8^2 / 4 = 50.27 in^2, so F = 150,800 lbf = 75.4 US ton = 670.8 kN.
- p = 150,800 / 48.0 = **3,142 psi (21.7 MPa)**. This is close to the gauge reading only because A_proj is about
  equal to A_ram.
- The same 3000 psi gauge on a 12 in ram gives 339 kip and 7,069 psi on the part: 2.25 times more.
- If the compound wants 1000 psi on the part, the gauge setting is 1000 * 48 / 50.27 = **955 psi**.
- Bearing: A_bear = 128 - 48 - 2 cavities x 20 in perimeter x 0.125 in overflow groove = 75 in^2, so
  sigma_bear = 2,011 psi (13.9 MPa). OK against hot aluminium.
- If only 1/16 in flash lands touched (the rest relieved): A = 2.5 in^2 and the land stress is 60 ksi. That crushes
  aluminium, so stop or bearing pads are mandatory (section 2.7).
- Plate weight: 16 x 8 x 1.25 in is 15.6 lb in aluminium (0.0975 lb/in^3) and 45.4 lb in steel (0.284 lb/in^3).

### 2.6 Press-fit geometry checks
- The mold footprint must be inside the platen, with no overhang. If an overhang is unavoidable, the overhanging plate
  is a cantilever (section 4.2 case D).
- Closed mold stack height H <= daylight - 25 mm (judgement margin for insulation boards and shims). The opening
  needed (H_open = H + plug engagement + part height + 50 mm hand clearance) must be within the stroke.
- Platen coverage: the platens should contact at least 80% of the plate back face over the cavity footprint.
  Otherwise use the unsupported-span case.

### 2.7 Bearing and land allowables
- sigma_bear <= S_y,hot / 3 for 1e5 cycles or more; <= S_y,hot / 2 for 1e3-1e4 cycles. These are judgement values
  meant to prevent hobbing and peening of parting edges. Treat them as editable defaults, to be calibrated against
  shop experience.
- Flash or cut-off lands: width 0.8-1.6 mm (1/32-1/16 in) next to the cavity, then an overflow or spew groove. The
  load-bearing face is outside the groove. If A_bear is too small, add bearing pads or stop blocks outside the
  overflow, at the same height as the land.

---

## 3. Materials and allowables at temperature

| Material | E at 20 C (GPa) | nu | rho (g/cm^3) | CTE 20-200 C (um/m.K) | S_y / S_u at 20 C (MPa) | Notes |
|---|---|---|---|---|---|---|
| 6061-T6 / T651 | 68.9 | 0.33 | 2.70 | 23.6 (24-25 to 200 C) | 276 / 310 | Long-exposure yield: 262 at 100 C, 214 at 149 C, 103 at 204 C; S_u 290 at 100 C, 235 at 150 C. Fatigue 96.5 at 5e8 (R.R. Moore) |
| 7075-T651 | 71.7 | 0.33 | 2.81 | 23.6 | 503 / 572 | Overages above about 120 C. Use only up to 100-120 C without MMPDS hot data. Fatigue 159 at 5e8 |
| MIC-6 cast plate | 71 | 0.33 | 2.65 | 24 | about 105 / 165 | Flat, stable backing plates only. Not for cavity walls |
| P20 prehard (28-32 HRC) | 205 | 0.29 | 7.85 | 12.8 | about 830 / 1000 | Usual default steel. Good to 250 C+ |
| 4140 prehard (28-32 HRC) | 205 | 0.29 | 7.85 | 12.3 | about 655-850 / 1000 | Plates, frames |
| H13 (44-48 HRC) | 210 | 0.29 | 7.80 | 10.4-11.5 | about 1300 / 1550 | Inserts, fragile features, high-wear lands |
| 1018 / A36 | 205 | 0.29 | 7.85 | 11.7 | 250-370 / 400-440 | Clamp plates and backing only |

Derating defaults (editable):
- E factor: aluminium 0.97 at 100 C, 0.93 at 150 C, 0.88 at 200 C. Steel 0.97 at 100 C, 0.95 at 200 C.
- S_y,hot for aluminium: interpolate the long-exposure yield values above. Steels: use the 20 C value x 0.95 up to
  250 C.
- Safety factors: static yield SF_s = 2.0 on S_y,hot for plates and walls. Fatigue SF_f = 1.5 on the corrected fatigue
  strength. Deflection: none, because delta_allow is already a design limit. Bolts: section 6.
- Temperature rule: if T_press > 150 C, an aluminium mold is limited to short runs (cycle life <= 1e4) and the tool
  says why. If T_press > 175 C, recommend steel outright.

---

## 4. Lid and base plates: bending

### 4.1 Allowable deflection (parting-line gap)
The real criterion is the *relative* gap at a land or shut-off, not the absolute centre sag. If lid and base bend
together, no gap opens. If one is backed by a flat platen and the other sags, the gap is the full sag. The tool takes
the stricter of two limits:
1. **Flash limit** from the part material (a field the material lookup fills; defaults below).
2. **Tolerance limit**: delta_allow <= 0.25 x the thickness tolerance of the critical dimension from the drawing
   (cavity floor deflection changes part thickness).

| Material class (viscosity at mold temperature) | Flash gap threshold (default delta_allow) |
|---|---|
| LSR and other very-low-viscosity liquids (< about 5 Pa.s) | 0.005 mm (0.0002 in). One mold maker reports flash at a 0.0004 in (0.01 mm) mismatch |
| Unfilled cast PU, epoxy, low-viscosity resins | 0.012 mm (0.0005 in) |
| PU prepolymers, filled resins, BMC | 0.025 mm (0.001 in) |
| Rubber compounds (NR, EPDM, NBR, FKM), HCR silicone, SMC | 0.05-0.075 mm (0.002-0.003 in) |

These are engineering defaults in line with Menges' approach: permissible deformation set by the material's flash
limit. Mold-maker vent-depth practice gives the same order (0.01-0.05 mm). Both fields are editable.

### 4.2 When a plate actually bends: support cases

| Case | Situation | Model |
|---|---|---|
| A | Plate backed by a flat platen (or a full backing plate) over the whole cavity footprint | No bending. Check compression p (trivial) and bearing (section 2.7). Plate thickness then comes from minimum rules (section 4.6) and weight |
| B | Base on two rails or parallels (knockout clearance) | One-way span b = clear distance between rails. Table "rails" (two SS edges, two free) |
| C | Plate over a rectangular opening supported all round (lid on a picture-frame base, base over a pocket in a backing plate) | All-round SS (lower bound on support) or clamped (bolted and stiff). The Mindlin SS4 and C4 tables; check both, design to SS4 unless the frame is integral |
| D | Overhang past the platen edge, length c | Cantilever strip: w = 1.5 p c^4 (1 - nu^2)/(E t^3) + shear term; sigma = 3 p c^2/t^2 |
| E | Support pillars on a grid s_x x s_y under a plate (DME/Hasco support-pillar practice) | Interior panel between pillars, about C4 with panel sides s_x, s_y. Exact: the FE solver with point supports |
| F | Lid pushed by a ram or platen smaller than the lid | Patch load: FE solver only |

Rees (Mold Engineering) and the DME/Hasco catalogues treat plate deflection over the ejector space with exactly this
span-plus-pillars logic. The fix for a too-thick plate is a smaller span, not a thicker plate.

### 4.3 Roark coefficients (thin plate, Kirchhoff)
Roark 7th ed., Table 11.4. w_max = alpha p b^4/(E t^3), sigma_max = beta p b^2/t^2, nu = 0.3, b = short side.

**Case 1a, all edges simply supported, uniform load** (sigma_max and w_max at the centre)

| a/b | 1.0 | 1.2 | 1.4 | 1.6 | 1.8 | 2.0 | 3.0 | 4.0 | 5.0 | inf |
|---|---|---|---|---|---|---|---|---|---|---|
| beta | 0.2874 | 0.3762 | 0.4530 | 0.5172 | 0.5688 | 0.6102 | 0.7134 | 0.7410 | 0.7476 | 0.7500 |
| alpha | 0.0444 | 0.0616 | 0.0770 | 0.0906 | 0.1017 | 0.1110 | 0.1335 | 0.1400 | 0.1417 | 0.1421 |

**Case 8a, all edges fixed, uniform load** (beta1 = edge stress at the middle of the long side, beta2 = centre)

| a/b | 1.0 | 1.2 | 1.4 | 1.6 | 1.8 | 2.0 | inf |
|---|---|---|---|---|---|---|---|
| beta1 | 0.3078 | 0.3834 | 0.4356 | 0.4680 | 0.4872 | 0.4974 | 0.5000 |
| beta2 | 0.1386 | 0.1794 | 0.2094 | 0.2286 | 0.2406 | 0.2472 | 0.2500 |
| alpha | 0.0138 | 0.0188 | 0.0226 | 0.0251 | 0.0267 | 0.0277 | 0.0284 |

These match Timoshenko and Woinowsky-Krieger (Theory of Plates and Shells, 2nd ed.):
- Table 8 (SS): w = 0.00406 q a^4/D for a square; 0.00406 x 12 x 0.91 = 0.0444.
- Table 35 (clamped): w = 0.00126 q a^4/D, edge moment -0.0513 q a^2; 0.0513 x 6 = 0.3078.

My FE prototype reproduces both (section 9.5).

### 4.4 Thick-plate (Mindlin) coefficients: what the tool uses
Mold plates sit at t/b = 0.1-0.5, where transverse shear deformation is not negligible. For an SS plate the FSDT
deflection is exactly w_FSDT = w_Kirchhoff + M_Marcus/(kappa G t), where M_Marcus = (M_x + M_y)/(1 + nu)
(Wang, Reddy and Lee 2000). For a square:

  w_FSDT / w_K = 1 + 5.18 (t/b)^2 (nu = 0.3)

This is exact in tau^2, giving 1.052, 1.207 and 1.466 at tau = 0.1, 0.2 and 0.3 (Navier, section 9.5). Clamped
plates are worse, because their bending deflection is small and shear is a larger share: 1.19, 1.71 and 2.56.

Prototype MITC4 sweep (nu = 0.3, 12 elements on the short side; thin values are 0.5-2% low against the converged
Roark values; the C# version regenerates them on a finer mesh). Entries are alpha / beta.

**SS4 (all edges hard simply supported)**

| a/b \ t/b | 0.02 | 0.1 | 0.2 | 0.3 | 0.5 |
|---|---|---|---|---|---|
| 1.0 | 0.0444/0.287 | 0.0466/0.287 | 0.0535/0.287 | 0.0651/0.287 | 0.1020/0.287 |
| 1.2 | 0.0617/0.376 | 0.0643/0.376 | 0.0724/0.376 | 0.0860/0.375 | 0.1295/0.375 |
| 1.4 | 0.0769/0.450 | 0.0798/0.450 | 0.0889/0.450 | 0.1040/0.450 | 0.1524/0.450 |
| 1.6 | 0.0902/0.514 | 0.0933/0.513 | 0.1031/0.513 | 0.1194/0.513 | 0.1715/0.513 |
| 2.0 | 0.1101/0.605 | 0.1135/0.605 | 0.1241/0.605 | 0.1419/0.605 | 0.1989/0.605 |
| 3.0 | 0.1325/0.705 | 0.1362/0.705 | 0.1477/0.705 | 0.1668/0.705 | 0.2281/0.705 |
| 4.0 | 0.1387/0.731 | 0.1424/0.731 | 0.1541/0.731 | 0.1735/0.731 | 0.2357/0.731 |

**C4 (all edges clamped)**. beta uses the edge moment taken from nodal reactions (section 9.3).

| a/b \ t/b | 0.02 | 0.1 | 0.2 | 0.3 | 0.5 |
|---|---|---|---|---|---|
| 1.0 | 0.0139/0.305 | 0.0163/0.294 | 0.0236/0.275 | 0.0354/0.263 | 0.0726/0.252 |
| 1.2 | 0.0188/0.382 | 0.0218/0.372 | 0.0305/0.350 | 0.0445/0.332 | 0.0883/0.312 |
| 1.4 | 0.0224/0.433 | 0.0257/0.426 | 0.0354/0.406 | 0.0511/0.386 | 0.1002/0.361 |
| 1.6 | 0.0248/0.466 | 0.0284/0.461 | 0.0390/0.446 | 0.0560/0.427 | 0.1092/0.400 |
| 2.0 | 0.0272/0.495 | 0.0310/0.494 | 0.0426/0.487 | 0.0614/0.476 | 0.1200/0.453 |
| 3.0 | 0.0280/0.499 | 0.0318/0.500 | 0.0436/0.501 | 0.0634/0.500 | 0.1261/0.495 |
| 4.0 | 0.0279/0.499 | 0.0317/0.499 | 0.0435/0.499 | 0.0631/0.498 | 0.1257/0.498 |

**Rails (two long edges SS at span b, two short edges free); a = rail length**

| a/b \ t/b | 0.02 | 0.1 | 0.2 | 0.3 | 0.5 |
|---|---|---|---|---|---|
| 1.0 | 0.1623/0.784 | 0.1678/0.765 | 0.1821/0.757 | 0.2039/0.748 | 0.2701/0.748 |
| 2.0 | 0.1643/0.794 | 0.1701/0.776 | 0.1848/0.768 | 0.2073/0.758 | 0.2750/0.753 |
| 4.0 | 0.1645/0.795 | 0.1703/0.777 | 0.1851/0.769 | 0.2075/0.759 | 0.2753/0.754 |

Notes on these tables:
- The rails alpha (at the free edge) is 0.164, which is w D/(q b^4) = 0.0150. That is above both the plate-strip value
  5/32 x (1 - nu^2) = 0.142 and the beam value 5/32 = 0.156, because the free edges lose the anticlastic restraint.
  Timoshenko's free-edge value of about 0.0151 q b^4/D agrees (see T11).
- beta for rails should approach 0.75 (q b^2/8). The 0.78-0.80 thin values are a stress-recovery artefact at free
  edges (Mindlin boundary layer plus extrapolation from Gauss points). Use beta = 0.75 for one-way spans until the C#
  solver's recovery is fixed (section 9.3).
- For t/b > 0.5, plate theory (even Mindlin) is outside its range. The tool should not "solve" these. It should reduce
  the span (pillars, rails closer together) or flag the case for a 3D check.

### 4.5 Sizing procedure for a spanning plate
1. Pick the case (section 4.2). Take a, b and the boundary-condition set.
2. Deflection: solve t = (alpha(a/b, t/b) p b^4/(E_hot delta_allow))^(1/3) by fixed-point iteration from t0 = 0.2 b.
   alpha rises with t, so the iteration is monotone and converges in 4-8 steps to 1e-4 relative.
3. Stress: sigma = beta p b^2/t^2 <= min(S_y,hot/SF_s, sigma_fat(N)/K_f) (section 7).
4. If t/b > 0.5, or the weight limit is broken (W = rho a_plate b_plate t), add support. Halving the span cuts t by
   about 2^(4/3) = 2.5 in the thin regime. Or switch to steel: E ratio 3, so t falls by 3^(1/3) = 1.44 for the same
   deflection.

### 4.6 Worked examples
**Example 1: base on two rails (case B), 6061-T6 at 120 C.** E = 9.5e6 psi, nu = 0.33, p = 3,142 psi,
delta_allow = 0.001 in (PU class). One-way strip; bending w_b = 5 p L^4/(384 D), shear w_s = p L^2/(8 kappa G t).

| Clear span L | Kirchhoff t | Mindlin t (bending + shear share) | Steel P20 Mindlin t | sigma (Al) |
|---|---|---|---|---|
| 6.0 in | 3.91 in | 6.27 in (24% / 76%): t > L, invalid, redesign | 3.37 in | 2.2 ksi |
| 3.0 in | 1.55 in | 2.06 in (42% / 58%) | 1.24 in | 5.0 ksi |
| 2.0 in | 0.90 in | 1.12 in (53% / 47%) | 0.70 in | 7.5 ksi |

Take-aways from this example:
- Stress is far below the limit; stiffness decides.
- Shear is half or more of the deflection, so the Roark (Kirchhoff) answer is 25-60% too thin.
- A 6 in span at 3,000 psi cannot hold 0.001 in in any practical plate. Pillars at 2-3 in are the answer.

**Example 2: example sizing table, case C (SS4, square panel), delta_allow = 0.025 mm.** t in mm, with t/b in
brackets. Entries with t/b > 0.5 are out of range (see 4.4); there the tool must add supports.

| Span b (mm) | 6061-T6 at 120 C, E 66 GPa: p = 5 / 10 / 20 / 35 MPa | P20 at 120 C, E 200 GPa: p = 5 / 10 / 20 / 35 MPa |
|---|---|---|
| 50 | 10.0 (0.20) / 13.2 (0.26) / 17.7 (0.35) / 23.2 (0.46) | 6.7 / 8.6 / 11.2 / 14.0 |
| 100 | 26.3 (0.26) / 35.4 (0.35) / 49.7 (0.50) / out | 17.2 / 22.3 / 29.5 / 37.8 |
| 150 | 46.8 (0.31) / 64.3 (0.43) / out / out | 30.0 / 39.4 / 52.9 / 69.2 |
| 200 | 70.8 (0.35) / 99.4 (0.50) / out / out | 44.7 / 59.1 / 80.6 / out |
| 300 | 128.7 (0.43) / out / out / out | 78.7 / 105.7 / 148.3 / out |

The full set (C4, SS4 at a/b = 2, rails) is in `proto/table.py` output. **Case A (platen backed) needs none of this.**

**Minimum thickness without bending (case A)** (judgement rules, editable):
- Cavity floor under the deepest cavity: >= 12 mm (aluminium) or 10 mm (steel), and >= 0.25 x cavity depth.
- Plate: >= 0.08 x the plate's longest side, for machining distortion and handling stiffness. Pry slots, dowels and
  bolt counterbores must fit.
- Lid >= plug height + 10 mm when the plug is integral.

---

## 5. Cavity side walls and multi-piece frames

### 5.1 Load
LC1 hydrostatic pressure p acts on the wall over the cavity depth h. Rubber and liquid resins are nearly
incompressible, so the sideways pressure equals the vertical pressure. For highly filled SMC or BMC a lateral factor
of about 0.7-1 applies; keep 1.0 unless the process simulator later supplies a pressure field.

### 5.2 Wall model and coefficients
Model the wall as a plate of thickness w, height h (loaded) and length L:
- Bottom edge clamped (integral with the floor).
- Top edge free (conservative: the lid's friction restraint is ignored).
- Side edges clamped (pocket milled from solid; corners integral) or simply supported (bolted frame corners).

w_max = alpha p h^4/(E w^3), sigma_max = beta p h^2/w^2 (the reference length is h). Values from the MITC4 sweep,
nu = 0.3, alpha / beta:

**Sides clamped (integral pocket)**

| L/h \ w/h | 0.02 | 0.1 | 0.2 | 0.3 | 0.5 |
|---|---|---|---|---|---|
| 0.5 | 0.0020/0.131 | 0.0030/0.127 | 0.0060/0.127 | 0.0110/0.126 | 0.0268/0.125 |
| 1.0 | 0.0316/0.547 | 0.0361/0.515 | 0.0494/0.504 | 0.0704/0.491 | 0.1340/0.470 |
| 1.5 | 0.1433/1.158 | 0.1544/1.073 | 0.1836/1.032 | 0.2276/0.987 | 0.3549/0.908 |
| 2.0 | 0.3446/1.721 | 0.3634/1.581 | 0.4093/1.501 | 0.4750/1.420 | 0.6586/1.281 |
| 3.0 | 0.8016/2.308 | 0.8302/2.170 | 0.8959/2.172 | 0.9873/2.164 | 1.2368/2.139 |
| 4.0 | 1.1035/2.660 | 1.1322/2.673 | 1.1997/2.688 | 1.2959/2.695 | 1.5670/2.685 |
| inf (strip) | 1.365/3.00 | | | | |

**Sides simply supported (bolted frame corners)**

| L/h \ w/h | 0.02 | 0.1 | 0.2 | 0.3 | 0.5 |
|---|---|---|---|---|---|
| 0.5 | 0.0102/0.195 | 0.0114/0.189 | 0.0147/0.186 | 0.0199/0.185 | 0.0359/0.185 |
| 1.0 | 0.1224/0.702 | 0.1290/0.675 | 0.1449/0.607 | 0.1676/0.570 | 0.2324/0.572 |
| 2.0 | 0.6382/1.911 | 0.6636/1.911 | 0.7199/1.892 | 0.7973/1.850 | 1.0045/1.732 |
| 4.0 | 1.2228/2.839 | 1.2502/2.853 | 1.3162/2.873 | 1.4136/2.890 | 1.6977/2.902 |

The infinite-length limit is the cantilever strip: w = 12 (1 - nu^2)/8 x p h^4/(E w^3) = 1.365 p h^4/(E w^3);
sigma = 3 p h^2/w^2. The prototype gives 0.1259 against the beam's 0.125 q L^4/D, and a root moment of 0.503 against
0.5.

**Root compliance.** "Clamped at the floor" is ideal. A real wall root rotates with the floor, which adds roughly
10-30% deflection (to be calibrated, section 10.4). Until then, multiply alpha by **1.2** for integral walls.
Picture-frame membrane tension in the long walls, from pressure on the short walls, adds
sigma_m = p b_cav/(2 w). That is small (4.8 ksi in Example 1) but is added to the bending stress at the corners.

### 5.3 Worked example: cavity wall
Rubber at p = 10.3 MPa (1,500 psi), cavity depth h = 25 mm, wall length L = 150 mm (L/h = 6, using the L/h = 4 row,
conservative), delta_allow = 0.05 mm, 6061-T6 at 150 C (E = 64 GPa).
- Deflection: iterating gives w = 12.5 mm (w/h = 0.50, alpha = 1.567). With the 1.2 root factor, w = 13.3 mm.
- Stress: sigma = 2.69 x 10.3 x 25^2 / 12.5^2 = 110 MPa. Static allowable 214/2 = 107 MPa: about at the limit.
- Fatigue at 1e5 cycles (section 7): allowable 74 MPa with K_f = 1.5 and SF 1.5, so **w = 12.5 x sqrt(110/74) =
  15.3 mm**. Fatigue decides this aluminium wall.
- In P20 (E = 190 GPa hot) the deflection-sized wall is 8.3 mm, or 8.8 mm with the root factor, at sigma = 225 MPa.
  The allowable at 1e5 is 540/(1.5 x 1.5) = 240 MPa, so it is just OK. At 1e6 (401/2.25 = 178 MPa) the wall needs
  8.8 x sqrt(225/178) = 9.9 mm. So in steel, fatigue only decides at long life with a corner K_f.
- At 3,142 psi with a 1.5 in deep cavity and delta = 0.001 in (Example 1 numbers): the cantilever strip needs
  1.31 in (Al) or 0.92 in (steel). sigma is 12.4 ksi (Al).

### 5.4 Multi-piece bases (side bars, end bars, bottom plate)
**Separating force** on a side piece = p x projected side area: F_sep = p L_c h (L_c = loaded length of the cavity
along that piece). Example 1 at 3,142 psi with h = 1.5 in: 28.3 kip (126 kN) per 6 in cavity side; 56.5 kip
(252 kN) for a 12 in bar shared by 2 cavities.

Load path options, in order of preference:
1. **Keyed:** the side pieces sit in a pocket or step (tongue) machined into the bottom plate, or interlock at the
   corners (end bars captured between side bars in a slot). F_sep becomes bearing on the key:
   sigma_key = F_sep/(key height x length). With a 0.375 in step over 12 in: 12.6 ksi. Fine for 6061 hot (214 MPa =
   31 ksi; SF 2.5). Check key root shear: tau = F_sep/(key length x key root width) <= 0.58 S_y,hot/SF_s. The bolts
   then only clamp.
2. **Bolted in tension** (horizontal bolts through end bars into side bars): the method in section 6.
3. **Friction only:** not allowed. With slip SF 1.5 it needs sum F_i >= 1.5 F_sep/mu: 424 kip at mu = 0.2 (dry Al/Al)
   or 848 kip at mu = 0.1 (release agent on the faces). Impossible.

Dowels: 2 per piece, for location only. If a dowel must take shear, tau = F/(n pi d^2/4) <= 0.5 x the dowel's
S_y/SF and the hole bearing sigma = F/(n d L_e) <= S_y,hot(Al)/2.

---

## 6. Bolts: preload, separation, thermal change, threads in aluminium

Method: Shigley's Mechanical Engineering Design (10th ed.), chapter 8. Equation numbers refer to that edition.

### 6.1 Formulas
- Bolt stiffness k_b = A_d A_t E/(A_d l_t + A_t l_d) (eq. 8-17). Use k_b = A_t E/l as a quick lower bound.
- Member stiffness, Wileman form (eq. 8-23): k_m = E d A exp(B d/l). A = 0.78715, B = 0.62873 for steel; A = 0.79670,
  B = 0.63816 for aluminium. For mixed stacks, use the frustum method (eq. 8-20) per layer.
- Joint constant C = k_b/(k_b + k_m). Bolt load F_b = F_i + C P; member load F_m = F_i - (1 - C) P, where P is the
  external tension per bolt.
- Load factor against proof: n_L = (S_p A_t - F_i)/(C P) >= 1.5 (eq. 8-28).
- Separation: n_0 = F_i/(P (1 - C)) >= 2.0, with F_i reduced 10% for embedding and creep in aluminium at temperature.
  The 2.0 is our choice; Shigley's eq. 8-29 defines n_0 without a fixed minimum. VDI 2230 treats embedding explicitly.
- Preload: F_i = 0.75 F_p (reused bolts, Shigley) at room temperature **only for steel-in-steel**. For steel bolts in
  aluminium joints that run hot, use F_i = 0.5-0.6 F_p and check the hot case below.
- Torque: T = K F_i d, with K = 0.20 dry, 0.15-0.18 with anti-seize (Shigley Table 8-15).
- **Thermal preload change:** dF = (CTE_joint - CTE_bolt) dT l_grip x k_b k_m/(k_b + k_m). Positive for Al plates and
  steel bolts on heating. Hot bolt load F_i + dF + C P <= 0.9 F_p.
- Bolt fatigue (if the load cycles every shot): sigma_a = C P/(2 A_t) <= S_e/SF. S_e for rolled threads
  (Shigley Table 8-17): SAE grade 8 = 23.2 ksi; ISO 10.9 = 162 MPa; ISO 12.9 = 190 MPa.
- Head bearing on aluminium: p_head = F_b,max/A_bearing <= p_G. VDI 2230 Table A9 limiting pressures: about
  370 MPa for AlZnMgCu0.5 (7075-type), 1986 values via Bossard. I found no value for AlMgSi1 (6061-type), so use
  0.9 S_y,hot and hardened washers.
- Threads in aluminium: engagement L_e >= 2 d for 6061-T6 tapped holes (1.5 d for 7075). Stripping check
  tau = F_b,max/(pi d_min,ext L_e x 0.75) <= 0.58 S_y,hot/SF_s, where 0.75 is a thread-fill approximation. Any screw
  that is removed routinely (lid hardware, inserts, frame bolts) gets a helical-coil or key-locking steel insert,
  length 1.5-2 d.

### 6.2 Property and size tables
SHCS to ASTM A574 are stronger than grade 8 (S_u 170-180 ksi). This file conservatively uses grade 8 proof
(S_p = 120 ksi) for inch SHCS and ISO 898-1 class 12.9 (S_p = 970 MPa) for metric.

| Inch UNC | A_t (in^2) | F_p @120 ksi (lbf) | 0.6 F_p (lbf) | T dry K = 0.2 (lbf.ft) |
|---|---|---|---|---|
| 1/4-20 | 0.0318 | 3,816 | 2,290 | 9.5 |
| 5/16-18 | 0.0524 | 6,288 | 3,773 | 19.6 |
| 3/8-16 | 0.0775 | 9,300 | 5,580 | 34.9 |
| 1/2-13 | 0.1419 | 17,028 | 10,217 | 85.1 |
| 5/8-11 | 0.226 | 27,120 | 16,272 | 169.5 |
| 3/4-10 | 0.334 | 40,080 | 24,048 | 300.6 |

| Metric 12.9 | A_t (mm^2) | F_p (kN) | 0.6 F_p (kN) | T dry K = 0.2 (N.m) |
|---|---|---|---|---|
| M6 | 20.1 | 19.5 | 11.7 | 14.0 |
| M8 | 36.6 | 35.5 | 21.3 | 34.1 |
| M10 | 58.0 | 56.3 | 33.8 | 67.6 |
| M12 | 84.3 | 81.8 | 49.1 | 117.8 |
| M16 | 157 | 152 | 91.4 | 292 |
| M20 | 245 | 238 | 142.6 | 570 |

### 6.3 Worked example: bolted frame corner
Setup: 12 in side bar, F_sep = 56.5 kip, carried by end bars at 2 corners, so P_corner = 28.3 kip. 1/2-13 SHCS,
grip 2.0 in, aluminium joint, dT = 100 K.
- k_b = 2.06e6 lbf/in, k_m = 4.67e6 lbf/in, so C = 0.306.
- dF_thermal = 11.9e-6 x 100 x 2.0 x 1.43e6 = 3,400 lbf.

| Bolts per corner, preload | F_i (lbf) | Hot bolt load (lbf, % F_p) | n_L | n_0 (cold, -10%) | Verdict |
|---|---|---|---|---|---|
| 2 at 0.75 F_p | 12,771 | 20,493 (120%) | 0.20 | 1.17 | fails |
| 3 at 0.75 F_p | 12,771 | 19,052 (112%) | 0.30 | 1.76 | yields when hot |
| 3 at 0.60 F_p | 10,217 | 16,498 (97%) | 1.18 | 1.41 | fails both |
| 4 at 0.60 F_p | 10,217 | 15,778 (93%) | 1.58 | 1.87 | marginal |
| 4 x 5/8-11 at 0.60 F_p (grip 2.5 in) | 16,272 | about 23,900 (88%) | about 1.8 | about 3.0 | OK |

Lesson: at about 3,000 psi on the part, a bolt-only frame needs 5/8 in or larger bolts. The keyed base (section 5.4)
is smaller, cheaper and is the default the tool should draw. Bolt fatigue here: sigma_a = 7.6-10 ksi against
S_e = 23.2 ksi, OK.

---

## 7. Fatigue for cycle life 1e3-1e6

### 7.1 Loading
Each shot loads the mold from about zero to the maximum (R = sigma_min/sigma_max = 0). Shuttled molds add a thermal
cycle. Mean stress is corrected with modified Goodman: the allowable maximum stress at R = 0 is

  sigma_max,allow = 2 S_a(N) S_u,hot/(S_u,hot + S_a(N)), capped at S_y,hot,

where S_a(N) is the fully reversed (R = -1) fatigue strength at N cycles, corrected for surface and size. Then
divide by K_f and SF_f = 1.5. K_f = 1 + q (K_t - 1) applies at:
- cavity inside corners (wall-to-floor fillet r; Peterson shoulder-fillet charts give K_t about 1.5-2.5 for
  r/w = 0.05-0.2)
- bolt and dowel holes in bent plates (K_t about 2-3)
- tapped holes

Notch sensitivity q is about 0.7-0.9 for aluminium at r >= 1 mm and 0.9-1.0 for hardened steels. Enforce a
wall-to-floor fillet r >= 0.1 w where the part drawing allows it.

### 7.2 Aluminium 6061-T6
It has no endurance limit. A literature fit to R = -1 smooth data is S = 14,479/sqrt(N) + 96.5 MPa (Engineers Edge;
not an MMPDS curve). It gives 142, 111 and 101 MPa at 1e5, 1e6 and 1e7. Secondary sources give about 140 MPa at 1e7.
Cap at 0.9 S_u for N <= 1e3. Apply a 0.9 surface factor (machined) and, for hot service, scale S_a by
S_u,hot/S_u,20C (approximation).

| N | S_a (MPa) | sigma_max,R=0 at 120 C (S_u 265) | at 150 C (S_u 235) | allowable at 150 C, K_f 1.5 and SF 1.5 |
|---|---|---|---|---|
| 1e3 | 251 | 258, capped at S_y | 243, capped at S_y 214 | 95 (S_y/2.25) |
| 1e4 | 217 | 239 | 226 | 100 |
| 1e5 | 128 | 173 | 166 | 74 |
| 1e6 | 100 | 145 | 140 | 62 |

7075-T651: fatigue strength 159 MPa at 5e8 and about 210 MPa at 1e7 (the higher S_u helps). Its temperature limit
(section 3) usually rules it out above 120 C.

### 7.3 Steels (Shigley chapter 6 method)
- S_e' = 0.5 S_u.
- Marin factors: k_a = 4.51 S_u^-0.265 (machined), k_b about 0.80 (large sections), k_d about 1.0 up to 250 C,
  k_e = 0.868 (95% reliability).
- f S_u at 1e3 cycles, with f about 0.86 (P20) or 0.80 (H13). Basquin line from 1e3 to 1e6.

| N | P20/4140PH (S_u 1000): S_a / sigma_max,R=0 | H13 46 HRC (S_u 1550): S_a / sigma_max,R=0 |
|---|---|---|
| 1e3 | 800 / 889, capped at S_y 830 | 1240 / 1378, capped at S_y 1300 |
| 1e4 | 544 / 704 | 784 / 1041 |
| 1e5 | 369 / 540 | 495 / 751 |
| 1e6 | 251 / 401 | 313 / 521 |

### 7.4 Effect on thickness
Stress scales with 1/t^2 (plates and walls), so t_fat = t_defl x sqrt(sigma_defl/sigma_allow,fat).
- Aluminium walls at 1e5 or more: fatigue often decides (example 5.3: 12.5 to 15.3 mm, +22%).
- Steel: deflection decides up to about 1e5. At 1e6, wall roots with K_f >= 1.5 can be decided by fatigue
  (example 5.3: 8.8 to 9.9 mm).
- Plates backed by the platen (case A): compression only, no bending fatigue. Only bearing (section 2.7) and thread
  checks remain.
- Threaded inserts: helical or key-locking steel inserts move the stripping and fatigue failure surface to a larger
  diameter in the aluminium. They are mandatory where a screw is loaded each cycle (frame bolts, plug retaining screws)
  for N >= 1e4, and wherever screws are removed more than about 10 times.

---

## 8. Thermal: expansion, fits, dowels, shrink correction

CTE (um/m.K): 6061/7075 23.6; P20 12.8; 4140 12.3; H13 10.4-11.5; 1018 11.7; hardened dowel pins (52100 or 8620)
about 11.5-12.

1. **Differential growth**, Al against P20, dCTE = 10.8e-6/K:

   | dT | 25 mm | 50 mm | 300 mm |
   |---|---|---|---|
   | 80 K | 0.022 mm | 0.043 mm | 0.259 mm |
   | 140 K | 0.038 mm | 0.076 mm | 0.454 mm |

2. **Steel insert in an aluminium pocket.** The pocket opens on heating by d (CTE_Al - CTE_st) dT. To be line-to-line
   at molding temperature, the cold interference = d (CTE_Al - CTE_st) dT/(1 + CTE_Al dT). That is 0.038 mm on 25 mm
   at dT = 140 K, enough to push any cold H7/p6 fit loose. Rules:
   - Locate the insert with a shoulder and screws from the back, not by press fit alone.
   - Size the pocket for 0 to +0.01 mm clearance at temperature.
   - Check the cold interference hoop stress in the aluminium with a Lame thick ring: delta/d = 0.0015 with outer/inner
     ratio 3 gives p of about 57 MPa contact and 71 MPa hoop. Fine statically; fatigue-check it if the mold is
     shuttled (thermal cycle).
   - Fit the insert by heating the aluminium plate, not by pressing.
3. **Plates of different metal** (Al lid on steel base, or a steel insert plate): one round dowel at the thermal centre
   (usually the mold centre) and one diamond or slotted dowel whose slot lies on the line to the centre. Never 2 round
   dowels 300 mm apart: that is 0.45 mm of fight at dT = 140 K.
4. **Same-metal plates at the same temperature:** ordinary 2-dowel location. For shuttled molds whose halves heat
   unevenly, treat a 10 K difference as dT = 10 K with the full CTE: 0.07 mm over 300 mm for aluminium. Enough for
   leader-pin clearances of 0.02-0.05 mm to bind, so add slotted pins.
5. **Shrink correction for mold material.** Shrinkage is roughly S = (CTE_part - CTE_mold) x (T_cure - T_room), and
   rubber shrink data come from the molds they were measured in. If a compound's shrink was measured in steel and the
   mold is aluminium: S_Al = S_steel - (CTE_Al - CTE_steel) dT. Rubber at 1.5% cured at 170 C (dT = 150 K) gives
   S_Al = 1.5 - 0.18 = 1.32%. The material lookup should store which mold metal its shrink value refers to.
6. **Bolts:** the thermal preload gain is covered in section 6.1.

---

## 9. The validation solver (C#, net48, unit-tested)

### 9.1 Where it lives
- Pure library, no SolidWorks references: `src/SwAgent/Mold/Structural/*.cs`, namespace `SwAgent.Mold.Structural`.
  It compiles into SwAgent.dll, so tool `macro.cs` scripts can call it directly: `ScriptHost.BuildOptions()`
  (`src/SwAgent/Scripting/ScriptHost.cs:268-300`) already references `typeof(ScriptGlobals).Assembly`.
- Agent tool: a `MoldStructuralTools` class with
  `[AgentTool("mold_structural_check", "...")]` (attribute at `src/SwAgent/Tools/ToolRegistry.cs:16`), registered next
  to the others in `src/SwAgent/AgentAddIn.cs:54-63`. It takes a JSON design description (section 11) and returns
  checks and margins.
- Tests: `tests/SwAgent.Tests/MoldStructuralTests.cs` (xUnit 2.9.2, net48; see `tests/SwAgent.Tests/SwAgent.Tests.csproj`).
- Coefficient tables: embedded resource `src/SwAgent/Mold/Structural/coefficients.csv`, regenerated by the sweep
  console (section 10).

### 9.2 Element: Mindlin plate, MITC4 (Bathe and Dvorkin 1985), on a structured rectangular grid
Why MITC4 and not Kirchhoff finite differences:
- Mold plates are thick (section 4.4); Kirchhoff cannot show shear deflection.
- Free edges (rails, walls, overhangs) are awkward in finite-difference Kirchhoff.
- MITC4 has no shear locking down to t/b = 0.01 and no spurious modes on rectangles.

Prototype result: thin SS square within 0.03% at a 24 x 24 mesh.

Formulation (as prototyped in `proto/plate.py`):
- Degrees of freedom per node: (w, beta_x, beta_y), with u = -z beta_x and v = -z beta_y.
- Curvatures: kappa = [d beta_x/dx, d beta_y/dy, d beta_x/dy + d beta_y/dx]; moments M = D_b kappa, where
  D_b = D [[1, nu, 0], [nu, 1, 0], [0, 0, (1 - nu)/2]].
- Shear strains: gamma_x = dw/dx - beta_x, gamma_y = dw/dy - beta_y; Q = kappa_s G t gamma, kappa_s = 5/6.
- Bilinear shapes; 2x2 Gauss for bending.
- MITC shear: gamma_x is sampled at edge midpoints (xi = 0, eta = -1) and (0, +1) and interpolated linearly in eta;
  gamma_y is sampled at (-1, 0) and (+1, 0) and interpolated in xi; then integrated 2x2. On a rectangle the Jacobian
  is constant: compute the 12x12 element matrix **once** per (hx, hy, t, material) and reuse it for every element.
- Loads: pressure patches (rectangles, or a per-element mask from the cavity footprint), consistent nodal load q A/4
  per node.
- Supports:
  - Edge sets per side: F (free); S (hard simply supported: w = 0 and tangential rotation = 0); s (soft: w = 0 only);
    C (clamped).
  - Interior line supports (rails: w = 0 along a grid line).
  - Point springs (pillars: k = E A/L of the pillar; bolts).
  - Winkler foundation regions k_f (N/mm^3) for platen contact through insulation board.
  - Optional unilateral contact (iterate: drop springs in tension, 3-6 passes) for "plate lifts off the platen".
- Solver: symmetric banded LDL^T.
  - Number the nodes along the short direction first; half-bandwidth = 3 (n_short + 2).
  - A 48 x 96 grid has 14,259 DOF with a band of about 150: n b^2 = 3.2e8 flops, about 0.3-0.6 s in C#, 17 MB.
  - Supports go in by row/column elimination (constrained DOF removed before factorisation) or by a 1e12 x k_max
    penalty. Prefer elimination, because penalties hurt conditioning for thin plates.

### 9.3 Results recovery (where the prototype taught something)
- **Deflection:** nodal w. It converges from below. Report the max over loaded and land regions and the **relative
  gap** to the mating plate (lid against base: gap = w_base - w_lid at land nodes).
- **Interior moments:** at Gauss points, extrapolated to nodes and averaged. The SS square centre moment
  M = 0.04789 q a^2 matches Navier exactly from 8 x 8 up.
- **Clamped-edge moments must come from nodal reactions:** M_edge = R_rot / tributary length. Gauss extrapolation
  gave 0.031-0.045 q a^2 against the exact 0.0513 for the clamped square at 8-32 elements. Reactions gave 0.0505 (8),
  0.0511 (16) and 0.0513 (32).
- **Free edges:** Mindlin has a boundary layer in twisting moment and shear near free edges. The extrapolated bending
  moment overshoots about 5% on thin plates (rails beta 0.78-0.80 against the beam's 0.75). Fix: superconvergent patch
  recovery (Zienkiewicz-Zhu), or report the moment at the first Gauss row. Unit-test against the beam.
- **Stress:** sigma = 6 M/t^2 (max principal from M_x, M_y, M_xy), times K_f where features sit.

### 9.4 C# API sketch
```csharp
namespace SwAgent.Mold.Structural {
  public enum EdgeBc { Free, SimpleHard, SimpleSoft, Clamped }
  public sealed class PlateMaterial { public double E, Nu, Sy, Su, Rho, Cte; public static PlateMaterial At(string id, double tempC); }
  public sealed class PlateModel {
    public double A, B, T;                      // mm (A along x, B along y)
    public PlateMaterial Mat;
    public EdgeBc X0, XA, Y0, YB;
    public int Nx, Ny;                          // auto: 24 on the short side, h <= t
    public List<PressurePatch> Pressures;       // x1,x2,y1,y2,p (MPa)
    public List<LineSupport> Lines;             // axis, position
    public List<PointSpring> Springs;           // x, y, k (N/mm)
    public List<WinklerRegion> Foundations;     // region, k (N/mm^3), unilateral flag
  }
  public sealed class PlateResult { public double[,] W, Mx, My, Mxy; public double WMax, SigmaMax; public (double x,double y) WMaxAt, SigmaMaxAt; public double[] EdgeMoments; }
  public static class MindlinPlateSolver { public static PlateResult Solve(PlateModel m, CancellationToken ct = default); }
  public static class Navier { public static (double w,double mx,double my) SimplySupported(double a,double b,double t,double E,double nu,double q,double x,double y,bool shear,int terms=199); }
  public static class Coefficients { public static (double alpha,double beta) Get(BcSet bc,double aspect,double tOverB,double nu); } // bilinear in (aspect, tau^2)
  public static class Sizing {
    public static double ThicknessForDeflection(BcSet bc,double a,double b,double p,double E,double nu,double deltaAllow); // fixed point
    public static double PressForce(PressInput press);  public static double CavityPressure(PressInput press, double aProj);
    public static double GaugeFor(double pReq, double aProj, double aRam);
  }
  public static class Joints { public static BoltCheck CheckBolts(BoltGroupInput g); }  // C, n_L, n_0, hot load, torque, stripping
  public static class Fatigue { public static double AllowableMaxStressR0(string material,double cycles,double tempC,double kf,double sf); }
  public static class Thermal { public static double FitChange(double d,double cteHole,double ctePin,double dT); public static double ColdInterferenceForLineToLine(...); }
}
```

### 9.5 Unit tests and their targets (prototype results in brackets)
All tests use E = 1, q = 1, nu = 0.3 unless stated. Tolerances are for the C# implementation at the mesh given.

| # | Case | Target (source) | Tolerance | Prototype |
|---|---|---|---|---|
| T1 | SS square, t/a = 0.01, centre w D/(q a^4) | 0.004062 (Navier; Timoshenko Table 8: 0.00406) | 0.5% at 16x16, 0.1% at 32x32 | 0.004059 (16), 0.004063 (32) |
| T2 | same, centre M_x/(q a^2) | 0.04789 (Navier; Timoshenko 0.0479) | 0.5% | 0.04789 (16) |
| T3 | Clamped square, w D/(q a^4) | 0.001265 (Timoshenko Table 35: 0.00126) | 1% at 16x16 | 0.001264 (16), 0.001267 (32) |
| T4 | Clamped square, edge moment (reactions) | -0.0513 q a^2 | 1.5% at 16x16 | -0.05111 (16), -0.05126 (32) |
| T5 | Clamped square, centre moment | 0.0231 q a^2 | 1% | 0.02301 (16) |
| T6 | SS rectangle b/a = 2, w and M_x | 0.01013, 0.1017 (Navier 0.010129, 0.10168) | 1% at 16x32 | 0.010096, 0.10123 |
| T7 | Clamped b/a = 2, w; edge M | 0.00254; -0.0829 (Timoshenko) | 1.5% | 0.002507; -0.0827 |
| T8 | SS square FSDT, t/a = 0.1/0.2/0.3: w D/(q a^4) | 0.004273 / 0.004904 / 0.005957 (Navier FSDT, hard SS) | 0.5% at 16x16 | 0.004268 / 0.004902 / 0.005957 |
| T9 | Wang-Reddy relation: w_FSDT - w_K = M_Marcus/(kappa G t) at centre | exact | 0.5% | from T1 and T8 |
| T10 | Wide cantilever strip (C, F, F, F; width 4L), tip w D/(q L^4) at mid-width; root M | 0.125 (beam; plate about 0.1259 from anticlastic stiffening); 0.5 q L^2 | 1% | 0.12589; 0.5026 |
| T11 | Plate SS on 2 opposite edges, other 2 free: w at the middle of the free edge, w D/(q b^4) | about 0.0151 for a/b of about 1-2 (Timoshenko and Woinowsky-Krieger, section 47 table, nu = 0.3; I recalled this value from memory, so confirm it against the book). It must lie above the beam value 5/384/(1 - nu^2) = 0.01431 | 1% | 0.01486 (a/b = 1, 12 per side), 0.01506 (a/b >= 2) |
| T12 | Thin-limit coefficient tables against Roark Table 11.4 cases 1a and 8a (all a/b) | Roark values | 1% (fine mesh) | 0.5-2% low at 12 per side |
| T13 | Patch symmetry: off-centre patch mirrored gives a mirrored result | exact | 1e-10 | - |
| T14 | Winkler foundation, uniform load, free plate: w = q/k everywhere | exact | 1e-8 | - |
| T15 | Rigid-body check: free-free plate with self-equilibrated loads; reaction sums equal applied load | exact | 1e-9 | - |
| T16 | Thickness solver: SS4 a/b = 1, p = 10 MPa, b = 100 mm, E = 66 GPa, delta = 0.025 mm | 35.4 mm (table 4.6) | 0.5% | 35.4 |
| T17 | Bolt example 6.3 (4 x 1/2-13 at 0.6 F_p) | C = 0.306, dF = 3,400 lbf, n_0 = 1.87 | 1% | as listed |
| T18 | Press: 8 in ram x 3000 psi | 150,796 lbf; p = 3,142 psi on 48 in^2 | exact | as listed |

Navier series (T1, T2, T6, T8): w = sum over odd m, n of
q_mn [1/(D k^4) + 1/(kappa G t k^2)] sin(m pi x/a) sin(n pi y/b), where q_mn = 16 q/(pi^2 m n) and
k^2 = (m pi/a)^2 + (n pi/b)^2 (hard SS, FSDT; Reddy, Theory and Analysis of Elastic Plates and Shells, 2nd ed.).
Drop the second term for Kirchhoff. Moments use only the Kirchhoff term: M_x = D (a_m^2 + nu b_n^2) w_K,mn. Use
199 x 199 odd terms for 1e-6 convergence of w; moments converge more slowly, so test M at about 0.1%.

---

## 10. The "simulation": parameter sweep that builds and validates the sizing tables

### 10.1 Reduce the variables first
The linear plate problem is self-similar:

  w = alpha(BC, a/b, t/b, nu, load pattern) x p b^4/(E t^3), sigma = beta(...) x p b^2/t^2

So pressure, span, modulus and deflection limit **do not need to be swept**; they are applied in closed form.
Sweeping p x b x E x delta directly would waste over 99% of runs and still interpolate badly. The sweep therefore runs
over the dimensionless parameters only, and the "plate thickness vs press pressure, span, material, deflection limit"
table the user sees is *derived* (as in section 4.6).

### 10.2 Sweep 1: coefficient tables (FE, about 4,000 runs, minutes)

| Variable | Values |
|---|---|
| BC set | SS4, C4, Rails (SS-F-SS-F), C2S2 (two clamped, two SS), Wall-C (C-bottom, F-top, C-sides), Wall-S (sides SS), Overhang (C-F-F-F), Pillar panel (C4 approx and point-supported 3x3 interior) |
| aspect a/b (or L/h) | 0.5, 0.75, 1.0, 1.2, 1.4, 1.6, 1.8, 2.0, 2.5, 3.0, 4.0, 6.0 |
| tau = t/b | 0.01, 0.05, 0.1, 0.15, 0.2, 0.25, 0.3, 0.4, 0.5 |
| nu | 0.29 (steel), 0.33 (aluminium) |
| load fraction (centred cavity patch / panel) | 0.25, 0.5, 0.75, 1.0 (plates only) |

8 x 12 x 9 x 2 x 4 is about 6,900 runs at most. Mesh: 24 elements on the short side, plus an h-refinement check at 48
on 5% of runs; reject the table if any |change| > 0.5%.

Store alpha and beta in CSV with columns bc, aspect, tau, nu, loadFrac, alpha, beta, wMaxX, wMaxY.

Interpolation and fitting:
- Bilinear in (aspect, tau^2). Tau^2 is the natural variable: FSDT is exactly linear in tau^2 for SS (section 4.4)
  and nearly so for clamped (c = 18.7, 17.9, 17.4 at tau = 0.1, 0.2, 0.3 for the C4 square).
- Optional compact fit per BC: alpha = alpha_K(aspect) x (1 + c1(aspect) tau^2 + c2(aspect) tau^4), with alpha_K from
  Roark. Accept the fit only if the max error on the grid is <= 1%. Otherwise ship the table.
- **Mesh-independence and thin-limit gate:** at tau = 0.01 every SS4 and C4 entry must match Roark Table 11.4 within
  1%, and every SS4 entry must match Navier FSDT within 0.5% at all tau.

### 10.3 Sweep 2: closed-loop Monte Carlo validation of the whole sizing rule (about 2,000 random molds)
Purpose: prove that "coefficient table + section 4.5 procedure" is safe and not wasteful on real layouts. Real layouts
have patch loads, several cavities, pillars and rails.

For each sample, draw:
- Press: ram 4-16 in, gauge 500-3000 psi.
- Cavities: 1-8, rectangles 20-200 mm, depth 5-60 mm, placed by the tool's own layout rules.
- Material: 6061, 7075, P20, H13; temperature 60-200 C.
- Support: A, B, C, E. delta_allow from the material classes.

Then:
1. Run the tool's sizing (the fast path) to get t, wall w, pillar spacing.
2. Build the exact plate model of that layout (real cavity footprints as pressure patches, rails as line supports,
   pillars as springs, lands as output nodes) and solve it with the MITC4 solver.
3. Record the ratios r_w = w_FE,max at lands / delta_allow and r_s = sigma_FE,max x K_f / sigma_allow.

Acceptance:
- max r_w <= 1.00 and max r_s <= 1.00 (the fast path is never unsafe).
- median r_w >= 0.75 (not more than about 10% too thick in t, because t scales with r^(1/3)).
- Any failure is minimised into a regression unit test.

Output: a CSV plus a summary (histogram of r_w by BC set) checked into `tests/SwAgent.Tests/Data/` and summarised in
the tool's TOOL.md "How sizing works" section.

### 10.4 Sweep 3: 3D cross-check (small, manual or optional)
Plate theory cannot see:
- wall-root compliance (the 1.2 factor in 5.2)
- local platen indentation
- t/b > 0.5 blocks

Run about 20 representative molds in SOLIDWORKS Simulation (if licensed; it can be driven through its API from the
add-in) or any 3D FE package. Use the same loads and compare the land gaps. Use the result to calibrate the root
factor and the t/b cut-off. Record the calibration in `coefficients.csv` metadata (source, date, version).

### 10.5 Runtime
A sweep console `tools/dev/MoldSweep` (net48 exe referencing SwAgent.dll's Structural namespace, or run as an xUnit
`[Trait("Category","Sweep")]` test skipped by default) writes `coefficients.csv`.

Cost: 7,000 FE runs at 24 per side (about 1,900 DOF, banded) take about 20 ms each, so about 2-3 minutes. The Python
dense prototype took 57 s for 105 runs.

---

## 11. Hooks for the later MCP simulators (machinability, process/demolding)

1. **Design record (input to every checker)** `mold-design.json`, saved next to the part and referenced from a part
   custom property `SolidWorks Agent.MoldDesign`. It holds:
   - inputs (press, materials, temperatures, cycle life)
   - bodies (id = SolidWorks body name, role: lid/base/side/insert/plug, material, bounding box, mass)
   - cavities (footprint polygons, depth, projected area)
   - supports, bolts (size, grade, grip, preload, torque), dowels and fits
   - load cases LC1-LC5
2. **Pressure field override:** LC1 uses p uniform = F/A_proj by default. The process simulator may later supply a
   per-cavity pressure map (grid or polygons with p) in `loadCases[LC1].pressureField`. The solver already takes
   arbitrary pressure patches, so nothing changes except the source.
3. **Checks out:** `structural-report.json` with one entry per check: id, body, type (deflection, stress, fatigue,
   bearing, boltSeparation, boltLoad, stripping, fit, weight), demand, capacity, margin = capacity/demand - 1, the
   governing load case, the method (coefficient table, FE, closed form) and the source/version. The machinability
   checker can append its own checks in the same shape (minimum tool radius against cavity corner r, wall height-to-
   tool reach). The agent reports the lowest margins first.
4. **Re-entry:** when a simulator changes something (a corner radius grows, the pressure field rises), the agent
   re-runs `mold_structural_check` with the changed design record. The check is deterministic and takes seconds.

---

## 12. Order of operations in the tool (structural part)

1. Resolve the press: A_ram, F, and p = F/A_proj or p_g,set. Ask if `press_pressure_is` is empty.
2. Materials at temperature: E, S_y, S_u at T_press; aluminium temperature rule.
3. delta_allow = min(flash class, 0.25 x the drawing tolerance).
4. Walls: alpha/beta tables (sides C or S by construction) x 1.2 root factor, then deflection, then stress and fatigue
   at the corner K_f, giving the wall thickness.
5. Base and lid: support case (A-F). Case A gives the minimum rules. Otherwise iterate on the table and choose the
   pillar or rail spacing so that t/b <= 0.5.
6. Multi-piece: keyed by default; key bearing and shear; bolts for clamping (section 6) with a hot check.
7. Inserts and dowels: thermal fits, thermal centre.
8. Bearing area and land check (LC2); add stop pads if needed.
9. Weights against the page limits (per plate, base total, lid). If exceeded, propose (in order) more support, steel
   for thin plates, splitting plates, or lightening pockets outside the load path. Never silently exceed a limit.
10. Final FE check of the actual layout with MITC4, then write `structural-report.json`.

---

## 13. Sources
- W. C. Young, R. G. Budynas, *Roark's Formulas for Stress and Strain*, 7th ed., McGraw-Hill, 2002: Table 11.4,
  cases 1a and 8a (rectangular plates, uniform load).
- S. Timoshenko, S. Woinowsky-Krieger, *Theory of Plates and Shells*, 2nd ed., McGraw-Hill, 1959: Table 8 (SS,
  uniform load), Table 35 (clamped), Navier solution.
- J. N. Reddy, *Theory and Analysis of Elastic Plates and Shells*, 2nd ed., CRC, 2007: FSDT Navier solution.
- C. M. Wang, J. N. Reddy, K. H. Lee, *Shear Deformable Beams and Plates: Relationships with Classical Solutions*,
  Elsevier, 2000.
- K. J. Bathe, E. N. Dvorkin, "A four-node plate bending element based on Mindlin/Reissner plate theory and a mixed
  interpolation", *Int. J. Numer. Meth. Eng.* 21 (1985) 367-383.
- R. G. Budynas, J. K. Nisbett, *Shigley's Mechanical Engineering Design*, 10th ed., McGraw-Hill: ch. 6 (fatigue,
  Marin factors) and ch. 8 (bolts: eqs. 8-17, 8-23, 8-28, 8-29; Tables 8-9, 8-11, 8-15, 8-17).
- G. Menges, W. Michaeli, P. Mohren, *How to Make Injection Molds*, 3rd ed., Hanser, 2001: mechanical mold design as
  a permissible-deformation problem tied to flash.
- H. Rees, *Mold Engineering*, 2nd ed., Hanser, 2002; DME and Hasco catalogue technical sections on support pillars
  and plate deflection.
- W. D. Pilkey, *Peterson's Stress Concentration Factors*, 2nd/3rd ed., Wiley: fillet K_t.
- VDI 2230 Part 1 (2015), Table A9: limiting surface pressure. Aluminium values via the Bossard technical sheet F-040
  (1986 values): https://media.bossard.com/-/media/bossard-group/website/documents/technical-resources/en/f-040-en.pdf
- ISO 898-1 (bolt property classes); SAE J429 (grade 8); ASTM A574 (SHCS).
- 6061-T6 elevated-temperature data (after up to 10,000 h at temperature): https://matmatch.com/materials/mitf349-6061-t6-aluminium.
  Short-exposure context: LIGO T1000564, https://dcc-llo.ligo.org/public/0021/T1000564/001/T1000564-v1%20bake%20temperatures%20for%20aluminum%20alloys.pdf
- 6061-T6 S-N fit (S = 14,479/sqrt(N) + 96.5 MPa): https://ns2.engineersedge.com/materials/fatigue_design_curves__16022.htm.
  6061 and 7075 at 1e7: https://www.sdcverifier.com/wiki/fatigue-strength-and-limit/
- Press force against gauge and ram area, ISO 2393 pressures, rubber molding pressures (forum practice):
  https://www.eng-tips.com/threads/pressure-in-compression-molding.361765/ ,
  https://cr4.globalspec.com/thread/65336/Specific-Pressure , https://cr4.globalspec.com/thread/68312/Rubber-Compression-Molding ,
  https://books.byui.edu/plastics_materials_a/compression_molding
- LSR flash at a 0.0004 in mismatch: https://ptonline.com/articles/getting-into-lsr--part-iv-how-lsr-tooling-is-different
- Rubber shrinkage mechanism (CTE difference between rubber and mold): https://www.martins-rubber.co.uk/rubber-shrinkage-what-is-it-and-why/
- NIOSH Revised Lifting Equation (Waters et al., 1993; NIOSH pub. 94-110): 23 kg load constant, as a default for the
  lid and handling weight limits.

Values marked "judgement" or "default" (bearing SF 3, flash classes, root factor 1.2, the 5% off-centre rule, the
minimum thickness rules) are engineering defaults to be confirmed with the user's shop. They are all editable fields
or constants in one place (`SizingDefaults`).
