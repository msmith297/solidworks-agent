export const meta = {
  name: 'compression-mold-review',
  description: 'Review the compression-mold tool from four angles, verify each finding, then fix',
  phases: [
    { title: 'Review', detail: 'engineering, SolidWorks safety, page+panel, procedure - in parallel, then verify each high/medium finding' },
    { title: 'Fix', detail: 'apply verified findings, full test + builds' },
  ],
}
// The review/fix half of the original integrate-review workflow (its integrate phase ran on 2026-10-20).
const R = 'C:/Users/msmit/Desktop/Software Projects/Unify3D Agent'
const DOCS = `${R}/docs/compression-mold`
const SCRATCH = 'C:/Users/msmit/AppData/Local/Temp/solidworks-agent-mold-review'
const BUILD = `Build: "/c/Program Files/Microsoft Visual Studio/2022/Community/MSBuild/Current/Bin/MSBuild.exe" src/SwAgent/SwAgent.csproj -restore -nologo -v:minimal -p:Configuration=Debug ; tests: dotnet test tests/SwAgent.Tests (uses test.runsettings / SWAGENT_DATA_DIR - never touch the user's real data) ; UI: cd ui && npx tsc -b && npm run build (node_modules exists). Never run scripts/build.ps1 -Configuration Release and never touch src/SwAgent/bin/Release (SolidWorks loads from it). Never push.`
const CTX = `Project: the SolidWorks Agent add-in (C# net48 + React panel), repository ${R} (branch claude/awesome-blackburn-350fc3). Read ${R}/HANDOFF.md first. A new built-in tool "Design compression mold for plastics and resins" (tools/builtin/design-compression-mold) was built in six parallel tracks (A1 data+calculator+record in src/SwAgent/Mold, A2 FE solvers+sweeps in src/SwAgent/Mold/Structural/Fe + tools/dev/MoldSweep, B PropertyManager page extensions, C categories/long runs/ask_selection/panel, D SolidWorks geometry tools + MoldTools.cs, E the tool folder), merged and integrated on this branch; all tests pass. Background: ${DOCS}/contract.md (scope decisions, interfaces), ${DOCS}/design.md, ${DOCS}/critique.md (critique wins), ${DOCS}/research/. ${BUILD}`

const RESULT = { type: 'object', properties: { commit: { type: 'string' }, done: { type: 'string' }, tests: { type: 'string' }, open: { type: 'array', items: { type: 'string' } } }, required: ['commit', 'done', 'tests', 'open'] }
const REVIEW = { type: 'object', properties: { findings: { type: 'array', items: { type: 'object', properties: {
  severity: { type: 'string', enum: ['high', 'medium', 'low'] }, file: { type: 'string' }, issue: { type: 'string' }, evidence: { type: 'string' }, fix: { type: 'string' } },
  required: ['severity', 'file', 'issue', 'evidence', 'fix'] } }, summary: { type: 'string' } }, required: ['findings', 'summary'] }
const VERDICT = { type: 'object', properties: { real: { type: 'boolean' }, reason: { type: 'string' } }, required: ['real', 'reason'] }

phase('Review')
const LENSES = [
  { key: 'engineering', prompt: `ENGINEERING lens. Check the calculator end to end for the user's typical job and for edge cases: units (psi/MPa/lbf/kN/US ton, mm/in, degC/degF), the press force chain (gauge vs on-part vs force reading, ram area), cavity pressure per class, walls/floors/lids/plates (incl. the wall-root factor and the layout-FE finish), bearing and land crush, keys and bolts (scatter, hot proof, LC1x abuse), threads/inserts, fits and dowels at temperature, shrink with each basis, charge/overflow/pour time, temperature gates, lifting limits, stock rounding, split. Reproduce the worked examples (data/validation/examples.json) independently with your own quick calculations (python -I scripts in the scratch folder) and run mold_calc-equivalent calls through MoldCalc.Run in a small test harness if useful. Flag anything that could produce a dangerous, unmachinable or obviously wrong mold, or wrong press settings.` },
  { key: 'solidworks', prompt: `SOLIDWORKS SAFETY lens. Review every new code path that calls the SolidWorks API (src/SwAgent/Mold/Geometry/**, Tools/MoldTools.cs, ModelFiles changes, overlay changes, ToolPage.cs page changes, RebuildTools changes, HiddenDocuments, FileAttachments): calls from timers/background threads, missing null checks, missing try/finally restores (DocumentVisible, EnableGraphicsUpdate, EnableFeatureTree, swStepAP), selection-mark mistakes, body-picking order traps, COM objects used after the document changed, re-entrancy in PropertyManager handlers, anything that can crash or hang SolidWorks or damage the user's document. Verify API names/signatures by reflecting the interop dlls in ${R}/src/SwAgent/bin/Debug/net48 (write a small PowerShell reflection script in the scratch folder).` },
  { key: 'page-panel', prompt: `PAGE + PANEL lens. Review the PropertyManager page extensions (ToolPage.cs, ToolSpec.cs, Units.cs, ToolData.cs, ToolInputs.cs, ToolRunner.cs payload) against tools/builtin/design-compression-mold/inputs.json and its data files (every ChoicesFrom/DependsOn/RowFilter/Fills/ShowWhen/UnitChoices path resolves and behaves as intended; units convert exactly; payload text is what TOOL.md expects), existing tools (Smart Rebuild, the user's tools in %APPDATA%/SwAgent/tools - read only) still render and run unchanged, and the panel side (categories, long runs + sticky settings ending, ask_selection, toolIndex text, drawing attachments) in ui/src. Build the UI and, where useful, reason about the store logic with concrete sequences.` },
  { key: 'procedure', prompt: `PROCEDURE lens. Read tools/builtin/design-compression-mold/TOOL.md as the agent that will run it, with no other context, and check it against the actual tools: every tool name, action and parameter it tells the agent to use exists with that exact schema (read MoldTools.cs, the ask_selection/ask_form/show_overlay definitions, ScriptGlobals Mold.* methods); the phases cover the user's request (blank part or attached part, drawing-only start building the part first, critical faces never get a parting line, overlay highlights of critical surfaces and air traps, questions before building, structural sizing, plate/lid/base weight and size limits, cycle life, setup sheet, machining sheet, hardware BOM, future simulator hooks); stage order (handling before flash system), gates, acceptance A1-A16 measurable with the available tools; nothing contradicts the system prompt rules; the macro.cs gathers what it says. Also check inputs.json order matches the user's ranking (part material/mold material/press pressure/temperature/cavities/limits/cycle life) with the part/drawing group first.` },
]
const reviews = await parallel(LENSES.map(l => () => agent(`${CTX}
Review the integrated tool in ${R} through one lens. Do not modify repository files (scratch work goes in ${SCRATCH}/review-${l.key}/). ${l.prompt}
Return concrete findings with evidence (file:line, numbers, a reproduction).`, { label: `review:${l.key}`, phase: 'Review', schema: REVIEW, effort: 'max' }).then(r => r ? { lens: l.key, ...r } : null)))

const all = reviews.filter(Boolean).flatMap(r => r.findings.map(f => ({ ...f, lens: r.lens })))
const important = all.filter(f => f.severity !== 'low')
log(`${all.length} findings (${important.length} high/medium); verifying the high/medium ones`)
const verified = await parallel(important.map((f, i) => () => agent(`${CTX}
A reviewer (${f.lens} lens) reported this ${f.severity} finding about ${f.file}:
ISSUE: ${f.issue}
EVIDENCE: ${f.evidence}
PROPOSED FIX: ${f.fix}
Try to REFUTE it: read the code, reproduce it if possible. Default to real=false if you cannot confirm it. Do not modify repository files.`, { label: `verify:${i}`, phase: 'Review', schema: VERDICT, effort: 'high' }).then(v => ({ ...f, verdict: v }))))
const confirmed = verified.filter(f => f && f.verdict && f.verdict.real)
const lows = all.filter(f => f.severity === 'low')
log(`${confirmed.length} confirmed high/medium findings; ${lows.length} low`)

phase('Fix')
const fixed = await agent(`${CTX}
Apply these review findings in ${R}. Confirmed high/medium findings (each was independently verified):\n${JSON.stringify(confirmed.map(f => ({ lens: f.lens, severity: f.severity, file: f.file, issue: f.issue, fix: f.fix, verifier: f.verdict.reason })), null, 1)}\n
Low findings (fix where cheap and clearly right):\n${JSON.stringify(lows.map(f => ({ lens: f.lens, file: f.file, issue: f.issue, fix: f.fix })), null, 1)}\n
Add a regression test for every behaviour you fix where it can be tested headless. Build, run all tests, build the UI, commit (Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>). Return what you fixed, what you did not (and why), and the final test counts.`, { label: 'fix', phase: 'Fix', schema: RESULT, effort: 'high' })

return { reviews: reviews.filter(Boolean).map(r => ({ lens: r.lens, summary: r.summary, count: r.findings.length })), confirmed: confirmed.map(f => ({ lens: f.lens, severity: f.severity, file: f.file, issue: f.issue })), lows: lows.length, fixed }
