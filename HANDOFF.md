# Handoff: Unify3D Agent, and the "Design compression mold" tool

This document is for a coding agent picking up this project with no previous context. Read it all before changing anything. You have access to the same project folder the previous agent worked in, described below.

Written 2026-10-20 at the end of a long session. Its last step, a multi-angle review of the new mold tool, was stopped by a usage limit. Updated 2026-10-21: plugins were built and reviewed (section 7).

---

## 1. The project in one paragraph

**Unify3D Agent** is a SolidWorks add-in with an AI agent that reads and changes the user's CAD models through the SolidWorks API.
- **Add-in:** C# net48, using xCAD and Roslyn scripting. It runs inside SOLIDWORKS 2026.
- **Panel:** a chat panel built with React, TypeScript, zustand and Vite, hosted in WebView2. It calls the model provider's API directly (Claude or OpenAI): the loop in `ui/src/agent/runAgent.ts`, the adapters in `ui/src/agent/providers/` (section 13).
- **Tools:** the agent's tools are C# `[AgentTool]` classes (`src/SwAgent/Tools/*.cs`) plus `execute_csharp` cells.
- **User tools:** the agent can build "tools" and "workflows" for the user: folders with a `TOOL.md`, an `inputs.json` (a native PropertyManager page) and an optional `macro.cs`.
- **Built-in tools:** the add-in ships these in the same folder format.

The owner (GitHub `msmith297`) runs a PU / resin compression-molding shop. They test everything themselves in SolidWorks on their PC, and they are building this as a product.
- Repo: `msmith297/unify3d-agent`. Releases and nightlies are published on GitHub (section 6).

## 2. Where things are: read this first

| What | Where |
|---|---|
| **Your working folder** (a normal git checkout) | `C:\Users\msmit\Desktop\Software Projects\Unify3D Agent` |
| Its branch | `claude/awesome-blackburn-350fc3`. **Not pushed:** 84 commits ahead of `origin` on 2026-10-21 (mold tool, plugins). origin is at `d6fc975`. |
| `main` | A local branch in the same repo, at `d51979b` (switch with `git switch main`). Main has everything up to `d6fc975` (PR #1, merged). It has **neither** the built-in-tools refactor (`f299941`) nor the mold tool. |
| **What SolidWorks actually loads** | `<working folder>\src\SwAgent\bin\Release\net48\SwAgent.dll`. The COM code base is registered there. **Installed 2026-10-26 15:00: commit `3dc8aa7`** (one persistent agent-bridge connection per PC that talks to the open chat, listening always, section 7 item 3; agent-bridge wakes from the user's own console follow the approval setting, Options > Plugins > "Trust my agent-bridge console"; on top of model providers: Claude + OpenAI, section 13; agent efficiency, plugin creator v2, standing instructions and fork_tool, tool profiles, question timeout); not yet confirmed in SolidWorks (hand checks: sections 9-13; OpenAI needs the user's key). Backups in `C:\Users\msmit\Documents\Unify3D Agent backups\`: `Release-net48-2026-10-26-17730c2` (the previous install), `Release-net48-2026-10-23-a96f71d`, `Release-net48-2026-10-22-7a2bf2d` (the previous install, 2026-10-22 23:02, same code as `7a2bf2d`), `Release-net48-2026-10-22-d6479fd` and `Release-net48-2026-10-17-f299941`: to roll back, close SolidWorks and copy one over `bin\Release\net48`. The dev tool `tools/dev/page-test` is copied to `%APPDATA%\SwAgent\tools\page-test` for the page checks; remove it after. |
| Old location (stale) | Until 2026-10-20 the project lived in `C:\Users\msmit\Documents\Claude Code\SolidWorks Agent Plugin`, with the work in the worktree `.claude\worktrees\awesome-blackburn-350fc3`. It was copied here whole (git history, build outputs, `ui\node_modules`, the gitignored `MECAGENT_TEARDOWN.md`). The old folder is a stale copy: don't work there. The user may delete it. The six merged parallel-build branches (`worktree-wf_71b3d5bb-873-N`) were deleted here. |
| User data, per PC; never commit it | `%APPDATA%\SwAgent\` holds: <ul><li>settings.json</li><li>apikey.bin (the encrypted Anthropic API key) and apikey-openai.bin (OpenAI's)</li><li>tools\ (the user's own tools: flash-grooves, parametric-block, pu-compression-mold)</li><li>builtin-settings.json</li><li>last-inputs\</li><li>builtin-data\</li></ul> `%LOCALAPPDATA%\SwAgent\` holds: <ul><li>chats\ (workspace chats)</li><li>logs\</li><li>crash-guard.json</li><li>snapshots\ (Smart Rebuild journals and checkpoints)</li><li>speech\ (Whisper models)</li></ul> |
| Persistent memory of earlier sessions | `C:\Users\msmit\.claude\projects\C--Users-msmit-Desktop-Software-Projects-Unify3D-Agent\memory\` (MEMORY.md + files, copied from the old project's memory folder) |
| Mold-tool design docs, now in the repo | `docs\compression-mold\`: `design.md` (the design), `critique.md` (corrections; **these win** over design.md), `contract.md` (how the build was split, the scope decisions, the interfaces), `research\*.md` (the research notes). In those docs `N/` meant a temporary scratch folder that may already be gone. Files of the same name are in `docs\compression-mold\research\`. The JSON data now lives in `tools\builtin\design-compression-mold\data\`, and the reference scripts were ported to `tools\dev\mold-data\` and `tools\dev\MoldSweep\`. |

## 3. Build, test, install

All commands run from the working folder.

| Purpose | Command |
|---|---|
| Debug build | `"/c/Program Files/Microsoft Visual Studio/2022/Community/MSBuild/Current/Bin/MSBuild.exe" src/SwAgent/SwAgent.csproj -restore -nologo -v:minimal -p:Configuration=Debug` |
| Tests | `dotnet test tests/SwAgent.Tests`. Expect **722 passed, 0 failed, 4 skipped** (2026-10-23, after the model providers integration and its review fixes; about 8 minutes). The skipped four are the long FE sweeps; run them with `MOLD_SWEEP=1`. Some plugin tests need node, Git for Windows or a local agent-bridge clone and skip without them (section 7). Tests set `SWAGENT_DATA_DIR` through `tests/SwAgent.Tests/test.runsettings`, so they never touch the user's real data. Keep it that way. |
| UI | `cd ui && npx tsc -b && npm run build` (node_modules is installed). Panel logic checks: `node ui/src/lib/checks/<name>.check.mjs` for agent, providers, integration, lib, models, creator and cheatsheet (section 13). |
| Mold data generators | `python -I tools/dev/mold-data/<gen>.py --check`. Each must report that its output matches the shipped files. See `tools/dev/mold-data/README.md`. |
| FE sweeps / Monte Carlo | `tools/dev/MoldSweep` (README inside). Build it in Release **from a scratch copy**, never into `src/SwAgent/bin/Release`. |

**Installing** means building the Release add-in that SolidWorks loads:
- Command: `powershell -File scripts/build.ps1 -Configuration Release`.
- SolidWorks must be closed. The script moves locked DLLs aside, but it can't replace a loaded `SwAgent.dll`.
- The script builds the UI into `wwwroot`, the add-in, the speech helper (`speech\`) and `builtin-tools\`.
- The registration already points at this folder. `scripts/register.ps1` (admin) is only needed if the path changes.
- Check after building: the dll's timestamp, and that `bin\Release\net48\builtin-tools\design-compression-mold\` exists.
- When the user says "install", that is the whole job. Previous agents also confirmed `HKCR\CLSID\{6D2B1C1E-4A0F-4F1E-9C7B-3E5A8D2F7B41}\InprocServer32` CodeBase.
- **Before you install the mold work**, read section 5.3: it changes the PropertyManager page code that every tool uses.

## 4. Hard rules (from the user and from experience)

1. **Never publish secrets.** The API key lives in DPAPI-encrypted `apikey.bin` and must never appear in the repo, logs, release notes or artifacts. Scan the diff before every push or tag.
2. **No SolidWorks API calls from timers, background threads or callbacks while SolidWorks runs a command.**
   - Corrupted-state exceptions (e0434352) can't be caught and kill SolidWorks.
   - SolidWorks event handlers go through `Safe.Run`.
   - Null-check every API return.
   - Restore global state in `finally`: `DocumentVisible`, `EnableGraphicsUpdate`, `EnableFeatureTree`, `swStepAP`.
   - Hidden document opens go through `HiddenDocuments.Scope()` (shares SwModelLoader's gate).
3. **Don't push, tag or release unless the user asks.**
   - Commits end with the trailer `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
   - Never `--no-verify`.
   - Don't use bare `git stash`: the stash stack is shared with any other worktrees or sessions.
4. **Tests must not touch the user's real data** (`SWAGENT_DATA_DIR`).
5. **Built-in tools are read-only.** They ship in `tools/builtin/<name>/` and the installer copies them to `builtin-tools\`, replacing the folder each time. Per-PC settings for them go in the user's folder (`builtin-settings.json`, `builtin-data\`).
6. **User preferences for replies:** short, plain and concrete. Say what changed and what to check, and say plainly what was not tested in SolidWorks.

## 5. State of the work

### 5.1 Finished and installed earlier: on the branch, in nightly `v0.2.0-nightly.20261016.2` unless marked otherwise

| Feature | What it is |
|---|---|
| Voice messages | Hold-to-talk with local Whisper (`src/SwAgent.Speech`, a separate process), keyboard and mouse hooks on their own thread, and spoken replies (`src/SwAgent/Voice`). Voice can answer a question the agent is waiting on. |
| Sticky markup pen | The pen stays on while the model is turned, and each view becomes a frame (`UI/MarkupOverlay.cs`). |
| First-run tour | Plus a voice-setup prompt (`ui/src/components/Tour.tsx`, `VoicePrompt.tsx`). |
| Workspace chats | Chats follow the user across files (`Chat/WorkspaceChats.cs`, `%LOCALAPPDATA%\SwAgent\chats`). |
| Built-in tools as folders | Smart Rebuild as a PropertyManager-page tool (`tools/builtin/smart-rebuild`). Commit `f299941`: **installed, but not pushed or in a nightly.** |

### 5.2 The new tool "Design compression mold for plastics and resins": built, integrated, tested headless, NOT reviewed, NOT run in SolidWorks

**What the user asked for** (paraphrased; full request in the design doc §0):
- A built-in tool in the category **Molding & Toolmaking**. It designs a heat-press compression mold for PU, resin or rubber shops, with every mold piece a body in one multi-body part.
- **Starting point:** a blank part, an attached molded-part file, and/or an engineering drawing. With only a drawing, the agent first models the part from it.
- **Drawing use:** tolerances and critical surfaces guide the design. No parting line goes on a critical surface, and accuracy is kept highest there.
- **Page inputs, in order of importance:**
  - mold material: aluminium with steel inserts, or tool steel;
  - part material: family, then grade, from a lookup table that loads editable process data (shrink, viscosity, ...);
  - press pressure and press temperature;
  - number of cavities;
  - weight and bounding-box limits per plate, base and lid;
  - cycle life.
- **Sizing:** plate thickness and bolt count/size come from a structural method validated by simulating many examples.
- **Questions before building:** the agent asks first, and highlights suggested critical surfaces and air traps with the 3D overlay.
- **Later:** MCP-connected machinability and molding/demolding simulators. Hooks only; **they must not be built here.**

**What exists now** (all on the working branch):

| Area | Files |
|---|---|
| Tool folder | `tools/builtin/design-compression-mold/`: <ul><li>`TOOL.md`, 789 lines: phases 0-7, gates, acceptance A1-A16, simulator hooks H1/H2/H3;</li><li>`inputs.json`: 44 inputs using the new page features;</li><li>`macro.cs`: a gathering macro;</li><li>`icon.png`;</li><li>`data/`: materials.json (96 part-material grades), mold-materials.json (grades, fasteners, inserts, dowels, stock sizes), shop-standards.json (the user's flash-groove rules), sizing-defaults.json, setup-sheet.html, machining-sheet.html, mold.schema.json (`unify3d.mold/1`), examples, validation outputs, and **README.md, which holds the "Before release: checks in SolidWorks" checklist**.</li></ul> |
| Calculator (pure C#) | `src/SwAgent/Mold/**` (namespace `SwAgent.Mold`): <ul><li>MoldData (+ shop overrides), Design, Si;</li><li>process: press, shrink with 4 bases, charge, lifting;</li><li>structural: walls, plates, bearing, keys, bolts with scatter and hot proof, threads, fits, pins, gap budget, coefficients;</li><li>layout and split, stock rounding;</li><li>Sizing and MoldCalc (actions lookup, process, press, layout, size, split, check);</li><li>MoldRecord, Template, SetupSheet, MachiningSheet and HardwareBom.</li></ul> |
| Validation | The FE solvers in `src/SwAgent/Mold/Structural/Fe/**`: <ul><li>MITC4 Mindlin plate, banded LDLᵀ, Navier and Levy references, LayoutPlate (FE of the planned layout);</li><li>3D Hex8 and axisymmetric solvers, VDI 2230.</li></ul> `tools/dev/MoldSweep` holds the coefficient sweep, the 3D checks and the Monte Carlo. Results: <ul><li>wall-root factor **1 + 2.8·w/h** (from 3D);</li><li>pillar-head factor 1.2;</li><li>2,000-mold Monte Carlo through the real calculator: max deflection ratio 0.998, max stress ratio 0.999, accepted.</li></ul> The data is in `data/validation/` and `src/SwAgent/Mold/Structural/coefficients.csv` (embedded). |
| Agent tools | `src/SwAgent/Tools/MoldTools.cs`, registered in `AgentAddIn.BuildTools`: <ul><li>`mold_calc`;</li><li>`mold_part`: draft, undercuts, projection and outline, parting-line candidates, air traps, fragile features;</li><li>`mold_bodies`: report, clearance, STEP export;</li><li>`mold_record`: the design record `<mold>.mold\mold.json`, setup and machining sheets, `record_status:` lines.</li></ul> |
| SolidWorks helpers | `src/SwAgent/Mold/Geometry/**`. `MoldApi` is exposed to code cells as `Mold.*`: <ul><li>InsertPart (with import validation), Orient;</li><li>a `MOLD CS` coordinate system and ScaleForShrink about it;</li><li>CopyBody, Plate, Subtract, SplitByPlane, Folder, SetBodyMaterial;</li><li>FlashGrooves (port of the user's macro), holes.</li></ul> Also: RebuildTools checkpoints keyed by mold id, overlay `face` items, `.slddrw` → PDF attachments with a 20 MB cap. |
| Page extensions | `Tools/Units.cs`, `Tools/ToolData.cs`, and big changes to `UI/ToolPage.cs`, `Tools/ToolSpec.cs`, `Scripting/ToolInputs.cs`, `Tools/ToolRunner.cs`. Features: <ul><li>units for pressure, temperature, mass, force, tonnage, viscosity and time, with per-field `UnitChoices`;</li><li>data-driven dropdowns: `ChoicesFrom`, `DependsOn`, `RowFilter`, `ValueMap`, `Fills` with an optional `|unit`;</li><li>`Optional`/`Enabled`, a `size` type, an `info` type, `ShowWhen`, `Remember`, `GroupExpanded`.</li></ul> A dev test tool is in `tools/dev/page-test/`. |
| Panel and registry | `UserTools.cs`: categories, `long_run`, `max_steps`, `requires`, `ribbon_title`. `UnifyRibbon.cs`: one box per category. `ToolsPalette.tsx`: sections per category. `runAgent.ts`: maxSteps and a 1-hour cache TTL for long runs. `store.ts`: sticky long-run settings with limits. Plus the `ask_selection` UI tool with SelectionCard, and systemPrompt additions. |
| Registry safety | `ToolRegistry.SchemaFor` handles JToken and dictionaries and has a depth limit. A test builds the schema of every `[AgentTool]` input, because a JObject input once meant a stack overflow at add-in load. |

### 5.3 What is NOT done: do these next, in order

**Update 2026-10-20 (review pass):** the `compression-mold-review` findings (engineering, SolidWorks safety, page/panel, procedure) were applied in one commit on this branch, with regression tests (`tests/SwAgent.Tests/Mold/MoldReview2*Tests.cs`, `MoldStockyWallTests.cs`). Not re-run: the 2,000-mold Monte Carlo and the 3D sweep (`MOLD_SWEEP=1`), so `data/validation/montecarlo-summary.json` predates the stocky-wall and design-force changes. Nothing was tested in SolidWorks. Item 1 below is therefore done except for those runs.

**Crash 2026-10-20 22:11 (first real run in SolidWorks):** the agent inserted the user's part, then the first `mold_part` closed SolidWorks. SolidWorks' crash dump (`%LOCALAPPDATA%\SolidWorks\CXPA\*.zip`, read with ClrMD) showed an `AccessViolationException` inside `IModelDocExtension.RayIntersections` (2,000-ray batch from `PartAnalysis.CastFirstHits`), which passed straight through ToolRegistry's catch blocks (.NET doesn't let ordinary catches see corrupted-state exceptions). Fixed:
- `mold_part` casts its rays in managed code on the display tessellation (`Mold/Geometry/MeshRayCaster.cs`). `RayIntersections` is no longer called anywhere, and a test (`MeshRayCasterTests.No_production_code_calls_RayIntersections`) keeps it that way. The analysis gives identical results on the fixtures.
- `Infrastructure/NativeFault.cs`: tool calls and `Safe.Run` catch an access violation inside SolidWorks (`HandleProcessCorruptedStateExceptions`), return an error, and block model tools until SolidWorks restarts, with "Save As under a new name, then restart" advice.
- The agent's crash-safety prompt (`ui/src/agent/cad/solidworks.ts`) forbids `RayIntersections` in code cells.

To read a future SolidWorks crash dump: unzip the newest `%LOCALAPPDATA%\SolidWorks\CXPA\*.zip` and walk the managed threads with Microsoft.Diagnostics.Runtime (`DataTarget.LoadDump`, `thread.CurrentException`).

1. **The multi-angle review and fix pass** was stopped by the usage limit, so the integrated tool has had no independent review.
   - Run it again as the saved workflow `compression-mold-review` (`.claude\workflows\compression-mold-review.js`). It runs review, verify and fix, and points at this folder. The original script also had an integrate phase, already done, and pointed at the old folder and a scratch folder that no longer exists.
   - Or do the same by hand. There are four lenses, each with its findings verified (try to refute each one) before you fix it:
     - **engineering:** units, the press force chain, every sizing check, worked examples in `data/validation/examples.json`;
     - **SolidWorks safety:** every new API path under `Mold/Geometry`, `MoldTools.cs`, `ToolPage.cs`, `RebuildTools`, `HiddenDocuments`, `FileAttachments`. Verify API names against the interop dlls in `bin/Debug/net48` by reflection;
     - **page and panel:** inputs.json against ToolData/ToolPage; existing tools' pages must be unchanged;
     - **procedure:** read TOOL.md as the agent and check every tool name and parameter it uses against the real schemas.
2. **Check in SolidWorks before installing for daily use.**
   - `UI/ToolPage.cs` changed a lot and renders *every* tool page: Smart Rebuild and the user's flash-grooves, parametric-block and pu-compression-mold.
   - The user must run the checklist in `tools/builtin/design-compression-mold/data/README.md` § "Before release: checks in SolidWorks":
     - page spikes S1-S6 with `tools/dev/page-test`, in an IPS part and an MMGS part;
     - geometry spikes 1-13;
     - ribbon and panel checks.
   - Give the user that checklist in a short form, and tell them before you install that it replaces the 2026-10-17 build.
   - Keep a copy of the current `bin\Release\net48` folder (outside the repo), so a broken page can be rolled back.
3. **End-to-end runs** R1-R6 (design.md §6.2): a PU puck, a 4-cavity bushing, an air-trap part, an EPDM gasket at 170 °C (expect the aluminium warning), a split base over the weight limit, and the user's hc-64 mold for comparison. The user does these with you.
4. **Defaults the user hasn't confirmed** (ask; all are editable):
   - his flash-groove standard as the liquid-pour default, replacing v1's narrow land;
   - lid limit from the NIOSH lifting equation, about 11 kg (25 lb) by default, and plate limit 23 kg;
   - whether his shrink % figures are from datasheets or measured in his own aluminium molds (the `shrink_basis` page field);
   - dowels: one round plus slotted, or his 4-dowel practice;
   - molds sit flat on the platen and slide in and out each cycle;
   - v1 scope: classes A (liquid pour) and B (rubber/compound flash molds) are built; positive molds for PEEK, PTFE and UHMWPE are advisory only;
   - the page order: the "Molded part" group first, then his ranking.
5. **Known gaps** (also listed in data/README.md "Open items"):
   - Holes are plain cuts, not Hole Wizard (spike S11).
   - No translucent temporary bodies; overlay boxes are used instead.
   - Air-trap volumes use the column spacing on big parts.
   - Parting lines are not yet scored on closure dimensions.
   - Case F (platen smaller than the lid) is a warning only.
   - Pillar plates thinner than t/b 0.1 with sharp heads.
   - No vitest: the panel checks run under node.
   - Workflows don't apply `model:` from their front matter.
   - Dead `NotImplementedException` catches in MoldTools.cs, left from the stubs.
   - `MoldData.SaveDefault` is unused.
6. **Future (don't build unless asked):** the MCP client for the simulators (design.md §4.5). The hooks, the export package and `mold.json` are in place, and the simulators are recorded as `not_run`.

### 5.4 Housekeeping

- **Getting the work onto `main`** when the user agrees:
  - The working branch has `f299941` (built-in tools) plus 44 mold commits, none pushed.
  - Last time the user asked for "merge into main" and it went through a GitHub PR (#1).
  - Push the branch, then open a PR or merge. **Before pushing,** note that `docs/compression-mold/research/existing-mold-knowledge.md` quotes the user's private tool code and shop rules. Ask whether the repo is public before pushing that file, or move it out of the repo.
- **Nightly** when asked:
  - Add a section on top of `NIGHTLY_NOTES.md`, then tag `v0.2.0-nightly.YYYYMMDD` (add `.2` etc. for a second one that day) and push the tag.
  - `.github/workflows/release.yml` builds the installer as a pre-release. The auto-updater ignores pre-releases.
  - The workflow builds `SwAgent.csproj`, which builds the speech helper and copies `tools/builtin/**` (data included) as `builtin-tools`. Check that the new data files land in the installer (Inno copies `bin\Release\net48\*` recursively).
- **Old folder:** once no session is open in it, the user can delete `C:\Users\msmit\Documents\Claude Code\SolidWorks Agent Plugin` (section 2). Nothing in it is needed any more.

## 6. Where to look for things

| Topic | Location |
|---|---|
| Agent system prompt | `ui/src/agent/systemPrompt.ts` |
| SolidWorks knowledge base | `ui/src/agent/cad/solidworks.ts` |
| Agent loop and API usage | `ui/src/agent/runAgent.ts` (the provider-neutral loop) and `ui/src/agent/providers/` (Claude and OpenAI adapters: requests, streaming, caching, context clearing, history translation); section 13 |
| Panel state | `ui/src/store.ts` |
| Tool registry and pages | `src/SwAgent/Tools/{UserTools,ToolSpec,ToolRunner,ToolRegistry}.cs`, `src/SwAgent/UI/ToolPage.cs` |
| Ribbon | `src/SwAgent/UI/UnifyRibbon.cs`. Tools reach the ribbon only after a SolidWorks restart, through the "Save and restart" offer. |
| Crash protection | `Infrastructure/CrashGuard.cs`, `Safe.cs`, `SwBusy.cs`. Undo of agent changes: `Tools/UndoLedger.cs`. |
| Smart Rebuild | `tools/builtin/smart-rebuild/TOOL.md`, `Tools/RebuildTools.cs`, `Tools/DesignSnapshot.cs` |
| Release and installer | `.github/workflows/release.yml`, `installer/Unify3DAgent.iss`, `NIGHTLY_NOTES.md`, `RELEASE_NOTES.md` |

## 7. Plugins (built 2026-10-21; review fixes applied the same day)

**What it is.** Claude Code compatible plugins for the add-in, plus Unify3D tools inside plugins: a plugin store, `/`
commands and `@` tags in the composer, skills, sub-agents (`run_agent`), hooks (Claude Code protocol), MCP servers
(stdio), and tool inputs as a form in the chat panel instead of the PropertyManager page. The first plugin is Agent
Bridge (the real agent-bridge plugin, installed by reference, plus the `agent-bridge-unify3d` overlay). Read
`docs/plugins/design.md` (the design, with review notes) and `docs/plugins/authoring.md` (what a plugin author sees,
including the known limitations in sections 6, 8 and 10).

**State.** Built in five tracks (merge commits `65e5afb`..`ae33ca6`, integration `42c0722`), then a review (compat,
safety, panel lenses) whose findings were applied in one commit with regression tests. Everything is tested headless
only. **Nothing has run in SolidWorks, and the live agent-bridge acceptance (design section 6) has not been done.** Not
installed: the Release build SolidWorks loads is in section 2.

| Area | Where |
|---|---|
| Plugin core: manifest, marketplaces (git or GitHub zip), install/update/enable, trust, options (DPAPI), indexes, agent tools (`list_plugins`, `use_skill`, `plugin_create` ... `plugin_validate`), `plugins.*` RPCs | `src/SwAgent/Plugins/*.cs` (`PluginManager`, `PluginManifest`, `Marketplaces`, `PluginTools`, `CommandIndex`, `SkillIndex`, `AgentIndex`, `FrontMatter`, `PluginsBridge`) |
| Runtime: hooks (`hooks.fire`, SessionStart/End, async + asyncRewake wakes, hourly guard), the session (`plugins\session.json`, one id per PC), plugin processes (job objects, Git Bash or cmd.exe) | `src/SwAgent/Plugins/Hooks/*.cs` (`RuntimeHost`, `HookRunner`, `HookEvents`, `Session`, `PluginProcess` with `ChildEnv`, `HookShell`, `Jobs`) |
| MCP client and host (stdio; `mcp__<server>__<tool>` in `tools.list`/`tools.run`; user servers in `%APPDATA%\SwAgent\mcp.json`) | `src/SwAgent/Mcp/McpClient.cs`, `McpHost.cs` |
| Tool forms in the panel | `src/SwAgent/Tools/ToolFormBridge.cs` (`ToolFormSession`), the `ToolRunner` branch; `ui/src/components/ToolFormCard.tsx`, `ui/src/lib/toolForm.ts`, `toolFormLogic.ts`, `toolFormMock.ts`; setting "Tool inputs: Panel / PropertyManager page" |
| Panel | `ui/src/components/PluginsScreen.tsx`, `SlashMenu.tsx`, `Composer.tsx` (slash/@ popups, narrow row), `ui/src/store.ts` (plugin state, hooks around turns, wakes, `run_agent`), `ui/src/agent/runAgent.ts`, `ui/src/lib/hooks.ts`, `commands.ts`, `mockHost.ts` |
| Content | `plugins/.claude-plugin/marketplace.json` (built-in "unify3d" marketplace, copied to `builtin-plugins\`), `plugins/agent-bridge-unify3d/`, `tools/builtin/create-plugin/` |
| User data (per PC; tests redirect it with `SWAGENT_DATA_DIR`) | `%APPDATA%\SwAgent\plugins\` (`installed.json`, `marketplaces\`, `cache\`, `local\`, `data\`, `secrets\`, `session.json`), `%APPDATA%\SwAgent\mcp.json`, `%LOCALAPPDATA%\SwAgent\plugins\transcripts` |

**How to test.**
- Headless: `dotnet test tests/SwAgent.Tests` (plugin tests are in `tests/SwAgent.Tests/Plugins/`, collection "Plugins",
  not parallel). `NodeFact` tests need node; `GitBashFact` tests need Git for Windows; `AgentBridgeEndToEndTests` and
  `AgentBridgeHookTests` need a local agent-bridge clone (Claude Code's marketplace copy under
  `~/.claude*/plugins/marketplaces/agent-bridge`, or `AGENT_BRIDGE_REPO_DIR`) and run it unpaired against a closed
  local port: no network. The review's regressions are in `PluginReviewTests.cs`.
- Panel logic: `node ui/src/lib/checks/lib.check.mjs` (matchers, wake rules, number parsing, ...).
- Panel in a browser: `npm --prefix ui run dev` uses `ui/src/lib/mockHost.ts` (no SolidWorks). `window.__agentStore`
  is the store, `window.__mockWake()` sends a plugin wake (`?wake` in the URL does it after 5 s). There is no API key in
  the mock: set `localStorage["swagent-mock:apiKey"]` (Anthropic) or `localStorage["swagent-mock:apiKey:openai"]` to
  any test string, or save one in Options. `?fake` in the URL answers the model APIs from canned streams
  (`ui/src/agent/providers/fake.ts`), so nothing reaches `api.anthropic.com` or `api.openai.com` (the plugins review
  stubbed `window.fetch` by hand to check wakes, Stop, sub-agent approvals and Ctrl+Enter in tool forms).

**Review fixes (2026-10-21), all with tests where headless:**
- Tool code needs approval: a plugin tool's `.cs` files count in the trust hash (`PluginManifest.HasToolCode`,
  `TrustHash`), the trust dialog lists them, and `PluginManager.ToolDir`/`UsableTools` hide tools with code until the
  plugin is approved (so ribbon, palette, panel form, `run_tool_macro`, `test_tool` can't run them). Plugins without
  tool code hash as before (approvals hold). Local plugins' tools need no approval (the user's own).
- Command and skill text no longer reads SolidWorks' environment (`PluginManager.Expand` leaves `${ENV}` as written;
  hook and MCP command lines fill it from the child environment, `${VAR:-x}` included). Plugins can't set `ANTHROPIC_*`
  or `CLAUDE_CODE_*` env.
- Hooks run with Git Bash (`usr\bin\bash.exe -c`, as Claude Code), cmd.exe without Git; `plugin_validate` warns about
  bash-only syntax.
- Panel: matchers match like `HookEvents.Matches`; woken turns never auto-approve (`RunOverride.fromWake`) and
  `run_tool_macro` is a write tool; wakes held during a turn wait for the user after Stop or an error (never merged into
  the stopped prompt; they go with the next message); a thread preparing a run counts as busy (no second run; `startRun`
  refuses one); sub-agent approvals show under the `run_agent` card with the code, and its steps show while it runs;
  Ctrl+Enter in a tool form commits the box being typed in first; "2,000" is 2000 unless the locale writes decimal
  commas; narrow-panel overflow of unit dropdowns and the Watch bar fixed.
- Add-in: SessionStart `systemMessage` goes to the user (`session.info` `sessionNotices`, notice bar), not the prompt;
  `$1` in typed arguments isn't expanded again; `argument-hint: [message]` keeps its brackets; MCP stderr keeps the last
  64 KB; tool-form selection boxes add to their list like a PropertyManager box ("Use selection" still replaces), and
  `toolForm.pick`/previews check `NativeFault` and the busy gate; closing the form's document doesn't call SolidWorks
  from the destroy event.

**What is open, in order:**
1. **SolidWorks-side checks by hand** (none were possible here): the Plugins screen; installing Agent Bridge (trust
   dialog, Node check); a plugin tool on the ribbon and in the palette; tool inputs in the panel form for Smart Rebuild,
   the mold tool and the user's own tools (selection boxes following the selection, live preview, OK and Cancel,
   closing the document while a form is open); MCP tools through `tools.run`; hooks (SessionStart at boot, Stop
   `--listen`, wakes into the chat); the narrow composer at 280 px.
2. **The live agent-bridge acceptance, design section 6**, with the real hub: the agent shows in `list_peers` as
   "Unify3D SolidWorks - <device>"; a console message arrives (UserPromptSubmit context, or a wake when idle); it
   replies with `send_message`; "listen always" survives a SolidWorks restart - **this last one fails until item 3**.
3. **Done 2026-10-26: one persistent agent-bridge connection per PC.** agent-bridge main `70feb8b` (made by Bridge
   Manager@Windows Desktop) registers non-Claude/Codex hosts with `AGENT_BRIDGE_SESSION_ID`; the hub already takes over
   a known session's row (name, renames, listening, project, DM thread). Unify3D's built-in entry sets
   `AGENT_BRIDGE_SESSION_ID=${CLAUDE_CODE_SESSION_ID}` (the PC id from `plugins\session.json`) and
   `AGENT_BRIDGE_LISTEN=always`; built-in entry env now applies to an existing install at once
   (`PluginManager.EntryEnv`). Wakes go to the chat open in the panel (store `wakeThread`), or start the new-chat page's
   chat. Live check (restart SolidWorks twice: one "Unify3D SolidWorks - <device>" chat, listening) still to do.
   The original note, kept for history: **agent-bridge needs a change** (not made: it is another repository, and changing it means a push): its
   `client/mcp.mjs` trusts `CLAUDE_CODE_SESSION_ID` only for clients named "claude". Proposed: in `register()`,
   `SESSION_ID = kind === 'claude' || process.env.UNIFY3D === '1' ? CLAUDE_SESSION_ID : null;` (or the hub's
   `requirePeer` re-links a stale row for any kind but Claude, as it does for Codex). Until then, after a SolidWorks or
   server restart the hooks can stay bound to the old offline row: no mail injection, no wakes, name and listening lost.
   Renaming Unify3D's MCP client to something with "claude" is not the fix (it would join the user's Claude app on the
   hub: account, Bridge Manager, revive requests). Also decide whether Unify3D should listen "always" by default
   (`"AGENT_BRIDGE_LISTEN": "always"` in the marketplace entry's `unify3d.env`); it is off now.
   **Decided by the user (2026-10-21):** `github.com/msmith297/agent-bridge` stays **private**. Installing it from the
   built-in marketplace therefore works only where git already has the user's GitHub credentials (the git-subdir
   source; the zip fallback 404s). First-party approved plugins will get **a separate public repository** (a public
   marketplace of approved plugins; not created yet). When it exists, the built-in "unify3d" marketplace and the store
   should list it, and first-party entries should point there.
4. **Known limitation:** with agent-bridge on a local hub, the broker `mcp.mjs` starts lands in the add-in's job objects
   and dies with that MCP server or SolidWorks (documented in authoring.md section 10).
5. Smaller items left from the review: MCP server stderr isn't streamed to the log (only its last lines on exit); a
   slash command's `@file` includes and `` !`cmd` `` note still scan the typed arguments too (only `$` expansion was
   made one-pass).

## 8. How the previous agent worked (it worked well)

- **Big features ran as workflows:**
  1. parallel research streams;
  2. a design;
  3. an independent critique, whose corrections won over the design;
  4. parallel build tracks in isolated worktrees, each owning its own files;
  5. per-track review and fix;
  6. merge and integration.
- **Usage limits.** The weekly limit interrupted runs twice. Resuming a workflow can re-run agents in a different order instead of using the cache. If a run dies partway:
  1. commit the partial work in its worktree as WIP;
  2. re-run only the missing steps, with the saved findings.
  Workflow journals are in `C:\Users\msmit\.claude\projects\...\subagents\workflows\<run>\journal.jsonl`.
- **Saying what wasn't tested.** Nothing in SolidWorks can be run from these sessions. The user tests in SolidWorks and reports back, often with screenshots. Always say plainly what wasn't tested there.

## 9. Agent efficiency and self-improvement (built 2026-10-22; review fixes applied the same day)

**Why.** In the user's 11 chats, 83 of 401 `execute_csharp` cells failed (members on the wrong interface, `ISelectData`
vs `SelectData`, casts of SolidWorks arrays, long raw calls such as `InsertMoveCopyBody2` / `FeatureCut4` with wrong
signs and marks), and the agent spent 49 calls on `search_sw_api` / `get_sw_api_type`. The user wants it to "just know",
and to improve itself: when it learns something it saves an API note **and** a helper, so the same mistake is handled
next time.

**What exists** (all in `src/SwAgent/Scripting/`, panel side in `ui/src/agent/`):

| Part | Where |
|---|---|
| Interop extensions that make the chats' wrong forms work (`Model.FeatureByName`, `Model.Bodies()`, `Model.GetSelectedObjectCount2`, `x.AsDoubles()` / `AsObjects<T>()`, `body.Volume()` ...). `[Obsolete(error)]` guards stop `GetSelectedObject6` and similar on non-selection objects at compile time. | `Extensions/*.cs` |
| Measured helpers `Bodies.*` (Move, Rotate, Copy, Combine, Subtract, Intersect, Split, ToFolder), `Features.*` (Extrude, Cut, Revolve, RevolveCut, Fillet), `Sel.*`. Each tries the documented marks/signs, measures the result, removes wrong tries and remembers what worked (per SolidWorks session). Angles in degrees: `deg(90)` is refused. | `Helpers/*.cs` (`HelperCatalog` reads the `[Helper]` attributes) |
| Compile-error hints: the member's real interface and how to reach it (`Model.Extension.X`, `((IPartDoc)Model).X`, `feat.GetSpecificFeature2()`; never a cast between separate COM objects), `SelectData` vs `ISelectData`, array element types, the helper that wraps a raw call. Run-time `InvalidCastException` on arrays gets the converter line. | `CompileAdvisor.cs`, `ScriptHost.ArrayCastHint` |
| The self-improving loop: `save_api_note`, `list_api_notes`, `save_helper`, `read_helper`, `list_helpers`, `delete_helper`, `test_helper`. Helpers the agent writes are compiled into one assembly that every cell imports (the script session restarts once, carrying variables). Two crashes in a cell that called a helper disable it. | `KnowledgeTools.cs`, `KnowledgeStore.cs`, `UserHelpers.cs`, `HelperInfo.cs` |
| The nudge: a cell that works after API-shaped failures (CS1061/CS1503/..., COMException, InvalidCastException, a helper's refusal; not typos or the cell's own `throw`) that called the same members says "That worked after N failed tries ... save_api_note / save_helper". Saving a note or helper, or `test_helper`, settles the failures. | `CellCoach.cs` |
| The cached prompt: the cheat sheet of exact signatures (helper-covered raw calls only as `Member(N parameters) - use Features.Cut`), and `knowledge.index` (helpers one line each, interop extensions on one line, the most used notes; capped at 10,400 chars) at the end of the system prompt. | `ui/src/agent/cad/swApiCheatsheet.ts` (generated by `tools/dev/ApiCheatSheet`), `ui/src/lib/knowledge.ts`, `ui/src/agent/systemPrompt.ts` (`learning`) |

**Stores** (per PC; tests redirect them with `SWAGENT_DATA_DIR`): `%APPDATA%\SwAgent\agent-knowledge\` (`notes.json`,
`helpers\<Name>.cs` with older versions `<Name>.vN.cs`, the helper records), `%LOCALAPPDATA%\SwAgent\helpers-bin\` (the
compiled helpers assemblies). Plugins' helpers live in their plugin folder (`helpers\`, docs/plugins/authoring.md).

**How to test.** `dotnet test tests/SwAgent.Tests` (`Scripting/*Tests.cs`: `HelpersTests`, `CompileAdvisorTests`,
`ExtensionsTests`, `EfficiencyIntegrationTests`, `EfficiencyReviewTests`; `KnowledgeTests`; `ApiCheatSheetTests`). They
compile cells with the real `ScriptOptions` and run them through the real `execute_csharp` path with `Model` null, so the
helpers compile but their SolidWorks parts are not reached. `tools/dev/ApiCheatSheet/bin/Debug/net48/ApiCheatSheet.exe
check` and `node ui/src/lib/checks/lib.check.mjs` check the sheet.

**Review fixes (2026-10-22):** the first `Bodies.Rotate` of a session retries a slot that turned the wrong way with the
sign flipped (it used to throw); wrong-interface hints use the accessor for `Extension` / `FeatureManager` /
`SketchManager` / `SelMgr()` (a cast throws); `Model.GetSelectedObject*` work, the same calls on other objects fail to
compile; `Revolve(axis:)` uses only the given axis; enum parameters print as their type (`Model.Bodies(swBodyType_e ...)`,
and `Model.Bodies(1)` works); `Subtract(keepTools)` retries refind the copies; `Features.Cut` tries every scope mark
(8, 1, 2, 0) before the other side; `read_helper` / `list_helpers` / `delete_helper` compile off the UI thread; CS0021
hints name the element type; carried script variables are always released; helpers get `SwAgent.Scripting.Helpers` and can
extend `BodiesApi` (shown as `Bodies.X`, signatures `-> T`); `deg()` passed to a helper is refused; the index keeps each
helper's essentials (the rotation pivot); prompt duplication cut (execute_csharp description, API section, cheat sheet,
index: about 2 k characters, and 1.2 k less in the worst case); the cheat sheet has assembly-component transforms.

**What is open:**
1. **Nothing of this has run in SolidWorks.** Not installed (the Release build is older; section 2). Check by hand: a
   first `Bodies.Rotate` about X, Y and Z in a fresh session (and with `about`), `Bodies.Copy` / `Move`, `Subtract` with
   `keepTools`, `Split(body, "Z", mm(5))`, `Features.Extrude` / `Cut` with a scope / `Revolve` with an explicit `axis`
   (a sketch with its own centerline too), `Fillet`, `ToFolder` into an existing folder; then a wrong cell, its fix, the
   nudge, `save_api_note` + `save_helper` + `test_helper`, and a new chat that sees the helper in its index.
2. The scope selection marks for `Features.Cut` and the RotAngle slot behaviour are only known from the user's chats and
   the Mold helpers; whether SolidWorks builds a no-op cut (rather than failing) for a wrong mark is unverified.
3. No helper for assembly components (`Components.Rotate`); the cheat sheet lists the transform members and says the
   multiplication order is unverified.
4. A saved note or helper changes `knowledge.index`, which is in the system prompt: the next turn of an open chat rewrites
   the prompt cache. The nudge is now rarer; freezing the index per chat would avoid it but hide new helpers until a new
   chat.

## 10. Plugin creator v2 (built 2026-10-22; review fixes applied the same day)

**What exists.** The agent builds a plugin from a description (`docs/plugins/creator-v2.md`, section 5 is the as-built
table): `web_search` / `web_fetch` (Options "Allow web research", default on; main agent only), `ask_secret` (a masked
card; the key goes straight to the plugin's DPAPI options), `check_prerequisites` (finds programs on the PATH and their
versions, installs nothing), `plugin_try_mcp` (approval-gated; a throw-away session of a local plugin's MCP server), MCP
over Streamable HTTP with the HTTP+SSE fallback (`src/SwAgent/Mcp/McpHttpClient.cs`), and the rewritten
`tools/builtin/create-plugin/TOOL.md`. Code: `src/SwAgent/Plugins/CreatorTools.cs`, `ui/src/lib/creator.ts`,
`ui/src/components/SecretCard.tsx`.

**Stores.** Nothing new: keys in `plugins\secrets\` (DPAPI), plugins as in section 7.

**How to test.** `tests/SwAgent.Tests/Plugins/CreatorTests.cs` (a fake HTTP MCP server on 127.0.0.1: JSON, SSE, legacy SSE,
bearer token, no secret in logs or files), `ReviewV3Tests.cs` (below), `node ui/src/lib/checks/creator.check.mjs`.

**Review fixes (2026-10-22):** `check_prerequisites` takes program names only (a path, `..` or a drive is refused, and a
"program" found inside the add-in's data folders is never run): it could run a script the agent had just written into a
plugin. MCP over HTTP follows redirects by hand, only to the same scheme and host (the handler re-sent `X-Api-Key` style
headers to any host); a redirect elsewhere is an error naming the url to put in `.mcp.json`. Answers, events and lines
are capped at 16 MB, error bodies at 64 KB. TOOL.md Step 2 finds registry and package pages with `web_search`
(`web_fetch` only opens links from the chat or search results).

**What is open.** Nothing ran in SolidWorks or against the real API (no API calls were allowed): check web research
(search, then fetch), an X link, `ask_secret`, `plugin_try_mcp`'s approval card and a real remote MCP server by hand. No
OAuth for remote servers; no standalone Streamable HTTP GET stream.

## 11. Standing instructions and fork_tool (built 2026-10-22; review fixes applied the same day)

**What exists.** The agent remembers what the user tells it "from now on" (`docs/agent-memory.md`): `remember`,
`list_memory`, `forget` (`src/SwAgent/Memory/`), notes per scope (general, a tool, a skill) with replaces and Undo (the
chat's "Remembered" chip), Options "What the agent remembers" with a saving switch. Read-only tools are never a dead end:
a behaviour rule is a tool note, input values are a default profile (section 12), a real change is the user's own copy
(`fork_tool`, `compare_fork` in `src/SwAgent/Tools/ForkTools.cs`). The panel refuses these writes (`MEMORY_WRITE_TOOLS`
in `ui/src/lib/memory.ts`) in plugin wakes, in turns a plugin's Stop hook continued, and in sub-agents.

**Stores** (per PC, under `Paths`, so `SWAGENT_DATA_DIR` redirects them): `%APPDATA%\SwAgent\agent-knowledge\memory.md`,
`tool-notes\<tool>.md`, `skill-notes\`, `memory-undo.json` (the Undo journal); the user's own tools keep theirs in a
"Standing instructions" section of their `TOOL.md`. A copy records its original in `<tool>\.fork.json`; hidden originals
are in `tools\builtin-settings.json`.

**How to test.** `MemoryTests.cs`, `ForkToolTests.cs`, `IntegrationSeamTests.cs`, `ReviewV3Tests.cs`; panel rules in
`lib.check.mjs` (the prompt section, refusals, chips).

**Review fixes (2026-10-22):** Stop-hook continuations are refused like wakes (`RunOverride.fromHook`); with saving off,
the prompt no longer teaches `remember` / profile / fork routes; Undo puts a replaced note back in its old place (the
journal keeps its index); a damaged `memory-undo.json` is kept as `.bad`; a run's notes have their own heading ("... they
win over its TOOL.md"); `compare_fork` and `fork_tool` write through `AtomicFile`; the palette's pencil on a read-only
tool says "Change how this tool works" (it no longer asks for a copy outright).

**What is open.** Nothing ran in SolidWorks: check a mold run with a tool note, `fork_tool` of the mold tool (ribbon button
after a restart, hide the original), `compare_fork` after an update, the chip's Undo, and Options' notes editor.

## 12. Tool profiles (built 2026-10-22; review fixes applied the same day)

**What exists.** Saved sets of a tool's input values (`docs/tool-profiles.md`, section 5 is as built): a Profile row in
the panel form and the PropertyManager page, Save as profile / Update profile / Save as new, markers for what a profile
set and what changed, a default profile, locked shipped profiles (two starters for the mold tool), export and import,
`list_tool_profiles` / `save_tool_profile` / `run_tool`, `/run <tool> --profile`. Code: `src/SwAgent/Tools/Profiles/`,
`UI/ToolPage.Profiles.cs`, `ui/src/components/ToolFormCard.tsx`, `ToolProfilesSettings.tsx`.

**Stores.** `%APPDATA%\SwAgent\tools\profiles\<tool>\<slug>.json` and `_settings.json` (default, last used); plugin tools
as `<plugin>+<tool>`. Shipped profiles are in a tool folder's `profiles\`. `profiles` is a reserved tool name.

**How to test.** `ToolProfilesTests.cs`, `IntegrationSeamTests.cs`, `ReviewV3Tests.cs`; the panel in a browser with
`npm --prefix ui run dev` (the mock host has a mold form with profiles; check a narrow panel at 330 px).

**Review fixes (2026-10-22):** deleting a tool deletes only a tool (a folder with `TOOL.md`) and takes its profiles with
it; a new `fork_tool` copy never inherits a deleted tool's leftover profiles; `plugin_add_tool` with `move` keeps the
user's default, last used and unshipped profiles; a damaged `_settings.json` is kept as `.bad`; the Update button says
"Update profile" (a long name overflowed a narrow panel), the save box says "Save as new profile" when one is loaded, and
its checkbox says what it does ("Leave out fields at the tool default").

**What is open.** Nothing ran in SolidWorks: the page's Profile group (dropdown refill after a save, button enabling) and
the dialogs (export, load from file, open folder). Deleting a tool still leaves its `last-inputs\` entry (harmless; a new
tool of the name starts with those remembered values).

## 13. Model providers (Claude + OpenAI) (built 2026-10-23; CORE and EDGES tracks, integration, then a review pass)

**Design:** `docs/providers/design.md` (the contract; section 1 is the one-page summary). Built in two parallel tracks,
CORE (the provider layer, the neutral agent loop, the adapters) and EDGES (keys, settings, pickers, docs), merged and
wired together in "Model providers: integration". The chat's model picks the provider; a chat can switch model, and so
provider, between turns. Stored chats keep the Anthropic block format; OpenAI's own output items ride along in an
optional `replay` field on its assistant messages.

| Area | Where |
|---|---|
| Neutral agent loop: hooks, approval, local tools, sub-agents, steering, step limit, stall watchdog (`provider.stallMs`), malformed-output re-sends; images and PDFs become a note for a model without vision / PDF support | `ui/src/agent/runAgent.ts`, `ui/src/agent/providers/media.ts` |
| Provider layer: `PROVIDERS`, `getProvider`, `providerOf`, `displayParts`, `providerErrorText`; errors by kind; `livenessFetch`, `redactKeys` | `ui/src/agent/providers/{index,types,errors,transport}.ts` |
| Claude adapter (the old request and stream code, moved unchanged) | `ui/src/agent/providers/anthropic.ts` |
| OpenAI adapter: Responses API, streamed, `store: false`; history translation and replay; client-side context clearing; `web_search`; speed (`service_tier`); quota 429s not retried; a 403 country block is an `access` error, not a bad key. The 12 h cache for long runs is wired but off (`cacheTtlLong: null` on every GPT row) until hand check H6 | `ui/src/agent/providers/openai/{client,history,stream,display}.ts`; `openai` pinned at 7.32.0 |
| Fake model APIs for the checks and the mock host (`?fake`) | `ui/src/agent/providers/fake.ts`, `fixtures/` |
| Model registry: providers, caps, families, tiers per provider, default model per provider (`DEFAULT_MODELS`, `keyedDefaultModel`: with only another provider's key, new chats start on its default; checked at boot too, for environment keys), caps for unknown ids per provider (`UNKNOWN_MODEL`), `resolveModel` (aliases and `opus-5`-style family names into the chat's provider), tool provider choice, grouped pickers, effort clamp, `/model` | `ui/src/lib/models.ts` |
| System prompt per run: the web wording follows the model's web tools (`caps.web`, the `WEB` table), working-style notes the provider (`STYLE`; only OpenAI has some) | `ui/src/agent/systemPrompt.ts` |
| Keys (C#): one DPAPI file per provider, `apikey.bin` unchanged, `apikey-openai.bin` new, writes through `AtomicFile` (settings.json too); `apiKey.get/set {provider?}` (no provider: Anthropic), `getState.apiKeySources` | `src/SwAgent/Infrastructure/SettingsStore.cs` (`ProviderKeys`), `AgentController.cs` |
| Secrets away from children: `ANTHROPIC_*` and `OPENAI_*` hidden from plugin, hook, MCP and git processes and `check_prerequisites`' programs; `${OPENAI_*}` expands to "" (plugins may still set `OPENAI_API_KEY` for their own servers) | `Infrastructure/SecretEnv.cs`, `ChildEnv.Hidden`, `PluginManager.Expand`, `Fetcher.GitStartInfo`, `CreatorTools.VersionStartInfo` |
| Panel: per-provider keys (`keys.get` asks the add-in for one provider's key when a run needs it), the no-key notice, tool runs on `tierModel(tier, toolProvider())`, error texts by kind, the speed setting (sent to every run; each adapter sends it only to models that offer it), the served tier in the turn's footer | `ui/src/store.ts`, `ui/src/components/ChatView.tsx` (`chatItems.ts`) |
| Options > AI providers (save, test, remove, get a key, Speed), grouped model pickers, effort pickers and `/effort` by model, the tour's provider choice | `ui/src/components/ProviderSettings.tsx`, `SettingsPanel.tsx`, `Composer.tsx` (+ group headers in `Menu.tsx`), `Tour.tsx` |
| Mock host: per-provider keys, `?fake`, a demo chat on GPT-6.1 Sol with `replay` | `ui/src/lib/mockHost.ts` |

**Where keys live.** Per PC, DPAPI-encrypted (current user): `%APPDATA%\SwAgent\apikey.bin` (Anthropic, unchanged)
and `apikey-openai.bin` (OpenAI); without a saved key the add-in falls back to `ANTHROPIC_API_KEY` / `OPENAI_API_KEY`
(source "environment"). The panel asks for one provider's key per run (`keys.get`); keys never go into settings.json,
chats, logs or child processes (`SecretEnv`). Tests use `SWAGENT_DATA_DIR` and fake keys.

**Adding a provider** (design section 10):
1. An adapter in `ui/src/agent/providers/<id>/` implementing `Provider`, added to `PROVIDERS`.
2. In `ui/src/lib/models.ts`: its rows in `MODELS`, a column in `TIERS`, its default in `DEFAULT_MODELS`, its id prefix in
   `PREFIXES`, an `UNKNOWN_MODEL` entry (caps for ids not in the registry). Add the id to `ProviderId` and `NO_KEYS`
   (store.ts); the `Record<ProviderId, ...>` types then point at what is missing.
3. In `ui/src/agent/systemPrompt.ts`: a new kind of web tools (`ModelCaps.web`) needs its wording in `WEB`; working-style
   notes, if any, in `STYLE`.
4. C#: a line in `ProviderKeys.All` (SettingsStore.cs) and `SecretEnv.ProviderPrefixes`.
5. Its origin in `ui/index.html`'s CSP (the O19 check fails until it is there).
6. Fixtures and scenarios in `providers.check.mjs`, and its error codes mapped to `ErrorKind`.

**How to test.** All under node, no network (`fetch` is the fake, the keys are fakes):
- `node ui/src/lib/checks/agent.check.mjs`: the Claude goldens C1-C8, captured from the code before the refactor (`7a2bf2d`). Every request body must stay string-equal: **never regenerate them**; a changed golden is a regression.
- `providers.check.mjs`: the OpenAI adapter O1-O19 (O12b: a cleared turn sends no item id without its reasoning; O14b:
  403 access), provider switches, clearing, stalls, errors, the CSP, the chat items. The OpenAI goldens
  (`fixtures/golden/openai-*.json`) may be regenerated when an OpenAI request changes on purpose: delete the file and run
  with `--write`.
- `integration.check.mjs`: full turns with tool calls on each provider through the mock's fake script, a chat that switches Claude -> OpenAI -> Claude, keys reaching only their provider, sub-agent aliases, speed for sub-agents, capability flags.
- `models.check.mjs` (also run by `lib.check.mjs`).
- C#: `SettingsStoreTests.cs` and the OpenAI cases in `HookProtocolTests`, `HookRunnerTests`, `PluginManagerTests`, `PluginReviewTests`.
- Browser: `npm --prefix ui run dev`, open `/?demo&fake`, and set `localStorage["swagent-mock:apiKey"]` and `["swagent-mock:apiKey:openai"]` to any test string.

**Review fixes (2026-10-23), with checks:** no 12 h cache request until H6 (the SDK and API reference list only
"30m"; a rejected value would have failed every OpenAI long run); context clearing no longer replays a cleared turn's
`fc_` / `msg_` items without their reasoning (they go as translated items, `web_search_call` left out); translated
Claude text goes as the plain `{ role: "assistant", content: text }` input form; tool schemas with a property named
`default` / `enum` / ... still get `items: {}` on arrays; a 403 country block says so instead of "key rejected"; Luna gets
Fast mode; `opus-5` / `sonnet-5` in a command or agent resolve again as before the provider layer; an OpenAI-only setup
with the key in the environment starts new chats on GPT-6.1 Sol; the prompt's web wording and unknown-id caps are no
longer Claude's by default for a new provider.

**What is open:**
1. **Nothing ran against the live OpenAI API** (there is no OpenAI key on this PC) **or in SolidWorks.** Every OpenAI
   request shape is checked against the docs and the pinned SDK's types only. Hand checks H1-H12 are in design section
   11. Also not covered by them: whether replayed item ids and `web_search_call` items are accepted with `store: false`,
   and the plain assistant message form for translated Claude text.
2. H6 decides the long-run cache: if `prompt_cache_options.ttl: "12h"` is accepted, set `cacheTtlLong: "12h"` on the GPT
   rows in `models.ts` (and the O16 check); nothing else changes.
3. While OpenAI streams, the live reasoning text shows the summaries raw (bold `**` headings); the saved chat shows them
   as sentences.
4. A third provider is not wired in: the list above is the work.
