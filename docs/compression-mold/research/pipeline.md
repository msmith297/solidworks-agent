# Tool-run pipeline, registry and listing - notes for the "Design compression mold" built-in tool

Paths are relative to the worktree root
`C:/Users/msmit/Desktop/Software Projects/Unify3D Agent`.
Nothing in the repository was changed.

---------------------------------------------------------------------------------------------------
## 1. End-to-end trace of one tool run

### 1.1 Start: ribbon button, hotkey, or the panel's Run button
* Ribbon/menu/toolbar: every command is registered with callback strings `OnAgentCommand(t<id>)` /
  `OnAgentEnable(t<id>)` (src/SwAgent/UI/AgentRibbon.cs:125). Tool IDs start at 100 and are kept per tool name in
  `%APPDATA%\SwAgent\tools\command-ids.json` (AgentRibbon.cs:186-216), so hotkeys survive adding/removing tools.
* `AgentAddIn.OnAgentCommand(arg)` (src/SwAgent/AgentAddIn.cs:136-174): `_ribbon.ToolFor(arg)` maps `t104` -> tool
  name (AgentRibbon.cs:77-78); a tool goes to `Controller.RunToolAsync(tool, ShowPane)` (AgentAddIn.cs:144) without
  opening the panel. Enable state: any `t...` command needs an active document (AgentAddIn.cs:177-188).
* Panel Workflows menu "Run" on a kind=tool row (built-ins included) calls `rpc("userTools.run", {name})`
  (ui/src/components/ToolsPalette.tsx:143) -> `AgentController` bridge handler (src/SwAgent/AgentController.cs:164-169)
  -> the same `RunToolAsync(name, null)` (AgentController.cs:428-429). Note: `showPane` is null on this path.
* `RunToolAsync` lazily creates one `ToolRunner(_app, _tools, _scripts, SendCommand, Status)` (AgentController.cs:429).

### 1.2 ToolRunner.RunAsync (src/SwAgent/Tools/ToolRunner.cs:58-175)
1. `_busy` guard: one tool at a time (line 60). It is released in `finally` right after the payload is sent
   (line 173) - i.e. it only covers the page + capture phase, NOT the agent run. A second tool can be started while
   the agent is still working on the first (the panel then queues it, see 1.4).
2. `ToolSpec.Load(name)` (line 61; src/SwAgent/Tools/ToolSpec.cs:100-127): user folder first, then built-in
   (`UserTools.DirOf`, UserTools.cs:63-71). Front matter is merged with per-PC built-in overrides
   (`EffectiveFrontMatter`, UserTools.cs:98-106 -> `builtin-settings.json`).
3. **Requires an active document** (lines 63-67): otherwise status-bar text "open a document first" and return.
   For the mold tool "start from a blank part" therefore means the user must have a (blank) part open first.
4. Inputs (lines 78-103): `spec.Inputs.Count == 0` -> empty inputs; else `ToolPage.TryFromSelection` (skips the page
   only when `confirm: missing` and all required selections are satisfied, src/SwAgent/UI/ToolPage.cs:664-679);
   otherwise the PropertyManager page `ToolPage` opens (`ShowAsync`). The page's Intelligence combobox writes the
   tier back via `UserTools.SetValue(name,"intelligence",tier)` (lines 82-93; built-ins -> builtin-settings.json).
5. While the page is open, for agent tools (not hybrid) `CaptureModelContextAsync` runs (line 96):
   * `model` in `context:` -> `get_model_context` text (lines 180-184), not truncated.
   * `history` / `history_full` -> `describe_model_history` (include_sketch_geometry for history_full), **truncated to
     `HistoryBudget = 24000` characters** (line 36, 190-192) with the note
     "... (history truncated - call describe_model_history for the rest)".
6. Cancel -> status "cancelled" (lines 98-101).
7. `LastInputs[spec.Name] = inputs` (line 110) for `run_tool_macro`.
8. Macro (lines 112-155): paused macro -> `context.failure` with the macro source; else `_scripts.RunMacroAsync` inside
   `CrashGuard.Enter("macro", ...)`. `Fail(...)` (MacroInputException) -> status-bar message and stop (127-132).
   Hardcoded / successful hybrid -> status-bar result, stop (133-139). `NeedsAgent(...)`/`AgentHandoffException` ->
   `context.handoff`; other errors -> `context.failure` (140-150). Successful agent-tool macro -> `macroOutput`.
9. `CaptureViewAsync` (lines 197-211): only if `view` in context: `capture_views` `["current"]`, `max_size 1024`,
   base64 JPEG -> `context.view`.
10. `BuildPayloadAsync` (lines 217-266) -> `_send("runTool", payload)` = `SendCommand` = bridge event
    `command {name:"runTool", args: payload}` (AgentController.cs:369-373).
11. `finally`: `_scripts.Reset("tool:" + name)` and `_busy = false`.

**`context: selection` is never used by ToolRunner.** Only selection-type *inputs* reach the agent. Entities
preselected but not consumed by a selection input are dropped (only `view`, `model`, `history`, `history_full` are read:
lines 180, 185, 199).

### 1.3 The payload (ToolRunner.cs:217-266; TS type ui/src/lib/types.ts:252-267)
```json
{ "tool": "<name>", "title": "<title>", "intelligence": "fast|balanced|high|max",
  "inputs": [ { "id", "label", "type", ...one of: } ],
  "context": { "model"?, "history"?, "view"? (base64 jpeg), "failure"?, "handoff"?, "macroOutput"? } }
```
Per input type (lines 224-255):
* `selection` -> `items: [{type,label,detail,ref}]` (ref = base64 persistent reference).
* `number` -> `value` string: unit `length` (THE DEFAULT when `Unit` is null, line 235) ->
  `"12.5 mm (0.0125 m)"`; `angle` -> `"3 deg"`; anything else (`none`, `percent`, or an unknown unit) -> the raw
  double `"150"` with no unit. **Pressure/temperature/mass inputs must set `"Unit": "none"` explicitly and put the unit
  in the Label**, otherwise the value is treated as a length (also in the page: ToolPage.cs:243-251 and
  `DefaultNumber` 756-762 convert mm -> m).
* `integer` is not a case here: falls to `default` -> `JToken.FromObject(double)`; the panel JSON.stringifies it -> "4".
* `files` -> `files: [FileEntry]` built by `FileAttachments.EntryAsync` (src/SwAgent/ModelFiles/FileAttachments.cs:35-78):
  images <= 5,000,000 bytes and PDFs <= 30,000,000 bytes travel as base64 `data` (lines 17-18, 62-71); bigger ->
  `kind:"too_large"`; 3D models (`ModelFileContext.IsModelFile`) are read now (status "reading X...") into
  `summary` + `views` (six-view JPEG sheet); other files -> `kind:"file"` with path only.
* `checkbox`/`choice`/`options`/`text` -> `value` (bool or string).

### 1.4 Panel side: command handler -> runQuickTool (ui/src/store.ts)
* `command` handler, case `runTool` (store.ts:1181-1191): if the tool's kind is `workflow` it calls `runUserTool`
  (sends `[Run workflow: name]` + composer text through the normal `send`, store.ts:1534-1538); otherwise
  `runQuickTool(payload)`.
* `runQuickTool` (store.ts:1540-1593):
  1. Always the WORKSPACE chat list; the **chat the user currently has open** (`activeThreadId`), or a new thread
     titled `Tool: <label>` with the chat defaults (1546-1562). A tool run is appended to whatever conversation is open.
  2. `tierModel(payload.intelligence ?? tool.intelligence)` (1564) -> `TIERS` (ui/src/lib/models.ts:61-70):
     | tier | model | effort |
     |---|---|---|
     | fast | claude-haiku-5-5 | low |
     | balanced (default) | claude-sonnet-5-5 | medium |
     | high | claude-opus-5-5 | medium |
     | max | claude-opus-5-5 | high |
     No tier maps to `claude-fable-5-1` or efforts `xhigh`/`max` although both exist (models.ts:16, 25-26). The
     front-matter `model:` field (listed by UserTools.cs:546) is ignored by runQuickTool.
  3. `stageToolPrompt(payload, label, refBase)` (1565; function at 272-324).
  4. `RunOverride = { model, effort, autoApprove: true, label }` (1568): **write tools (execute_csharp, undo, rebuild)
     run without approval for this run** (runAgent.ts:379-390, store.ts:680).
  5. If the thread already has a run: the staged prompt is queued as a `QueuedMessage` with `staged` and a notice
     "The agent is busy in this chat - X runs when it finishes" (1569-1577); `flushQueue` submits it alone when the
     turn ends (831-858); on Stop it is dropped (834-844).
  6. `submitBlocks` (902-922): appends one user message (or extends a dangling user message), merges `selRefs`, adds
     a `turns[]` entry with the overriding model/effort, persists, `startRun` (590-716).
  7. When the run ends: `toolOutcome` banner = first line of the agent's last text, 160 chars, or the error (1581-1592).

### 1.5 EXACTLY what the agent receives for a tool run
One user message whose content blocks are (stageToolPrompt, store.ts:272-324):

**Block 1 - text** (line 319):
```
[Run tool: compression-mold]
<tool_inputs>
- Mold material (mold_material): Aluminium + steel inserts
- Part material family (part_family): Polyurethane
- Press pressure, bar (press_pressure): 150
- Plate thickness override (plate_t): 25.4 mm (0.0254 m)
- Faces where no parting line may go (critical_faces): [S7], [S8]
- Molded part (part_file): bracket.SLDPRT (3D model - views and measurements attached)
- Drawing (drawing): bracket-rev-C.pdf (attached PDF)
- Notes (notes): (empty)
</tool_inputs>
```
Line formats: selection `- Label (id): [S1], [S2]` or `(nothing selected)` (286); files `- Label (id): name (attached image|attached PDF|3D model - views and measurements attached)` joined by "; ", or `name (<error or "not attached">: path)`; model not read -> "3D model, not attached - open it with execute_csharp if you need it" (288-303); other values: string as is, `(empty)` for "", non-strings JSON (305-306).

**Blocks 2..n - attachments, in input order** (291-298): image -> `image` block (base64); PDF -> `document` block with
`title` = file name; model -> text `<model_file name="..." path="...">\n<summary>\n</model_file>` + one six-view JPEG
`image` (modelBlocks, 397-402).

**Next block - text** (321):
```
<sw_context>
Tool run: <label>
Output of the macro in the "<title>" tool (already run):\n<first 6000 chars>          (if macroOutput)
The macro in the "<title>" tool failed - finish the job, then fix the macro ...\n<first 4000 chars> (if failure)
<handoff, first 6000 chars>\n\nFinish this case yourself. If it is a case the macro should cover, extend ... (if handoff)
[S7] face of Bracket-1 - plane, normal ..., area ...       (one line per selected item; "(no persistent id)" if no ref)
Model:\n<get_model_context text>                            (if context model)
Feature history:\n<describe_model_history, <= 24000 chars>  (if context history)
Attached after this block: the user's view when they ran the tool.   (if view)
</sw_context>
```
(unshift order means: macroOutput, then failure, then handoff appear first - lines 310-315.)

**Last block - image** (322): the current view, `image/jpeg`, max 1024 px.

Differences from a normal chat message (`submitPrompt` + `buildContext`, store.ts:533-588, 872-899): a tool run has
**no `Document: ...` line, no `Camera:` line**, and no live selection list beyond the selection inputs. If the tool's
`context` omits `model`, the agent is not told which document is active.

[S#] numbering continues from the thread's existing `selRefs` count (refBase, 1565 and 280-284); refs are stored on the
thread so later `Ref("S7")` / select_refs resolve through `tools.run` (store.ts:664-672, AgentController.cs:223-230).

### 1.6 How the agent is told what a tool run is (ui/src/agent/systemPrompt.ts)
* Tool section (line 51): "...you get [Run tool: name] with <tool_inputs> ... and <sw_context> ... Everything is
  already staged: don't re-ask for inputs, don't re-capture what's there, don't narrate. read_user_tool(name) for its
  guidance if you haven't, do the job efficiently on those inputs, and **finish with one short line saying what
  changed**." Lines 52-54: treat macro output as a helper; hybrid handoff; verify TOOL.md acceptance checks by
  measurement before reporting success.
* Built-in tools (line 88): run like user tools; `read_user_tool(name)` "then follow it exactly"; read-only.
* Tool index appended at the end of the system prompt (toolIndex, lines 162-164):
  `- smart-rebuild (built-in tool): Smart Rebuild - <description>`. Built from `get().userTools`
  (store.ts:624 -> runAgent.ts:203). The index changes (and the prompt cache is rewritten) only when tools change.
* Reply length: verbosity "medium" asks for "under about 120 words" (systemPrompt.ts:125-128).
* **Conflict for a long design tool**: the generic "don't narrate / one short line" rule (line 51) works against a
  multi-phase design that must present a plan, ask questions and report a design summary. Smart Rebuild gets away
  with it because its TOOL.md defines phases and the "follow it exactly" built-in rule (88). Recommendation: add one
  sentence to line 51/88, e.g. "When a tool's TOOL.md defines phases, questions or a report, its procedure overrides
  the one-line finish", and/or a front-matter flag (`run: long`) that the panel turns into a first line of the staged
  prompt (stageToolPrompt line 319) such as `[Run tool: compression-mold] (long design run - follow TOOL.md phases)`.

---------------------------------------------------------------------------------------------------
## 2. Limits that matter for a long run

| Limit | Value | Where |
|---|---|---|
| Steps (API round trips) per turn | 200, then error "Stopped after 200 steps." | ui/src/agent/runAgent.ts:207, 345 |
| Output per response | `max_tokens: 64000`; cut off mid tool call -> error "continue in smaller steps" | runAgent.ts:227, 324-326 |
| Context editing (server side) | `clear_tool_uses_20250919`: trigger at 40,000 input tokens, keep last 6 tool uses, clear >= 12,000 tokens each time, tool inputs cleared too | runAgent.ts:149-156, 242 |
| Never cleared | `ask_user`, `ask_form`, `offer_restart`, `read_user_tool` results; and the user messages (staged tool prompt with its PDFs/images, follow-ups) | runAgent.ts:155 |
| Prompt cache | top-level `cache_control: {type:"ephemeral"}` (default 5-minute TTL) | runAgent.ts:239 |
| Stall watchdog | 90 s with no bytes -> abort + resend, 2 retries; SDK `maxRetries: 3` | runAgent.ts:159, 170, 217-222, 291-299 |
| Malformed tool JSON | re-issued up to 2 times | runAgent.ts:303 |
| History captured for a tool | 24,000 chars | ToolRunner.cs:36, 190-192 |
| macroOutput / failure / handoff in staged prompt | 6,000 / 4,000 / 6,000 chars | store.ts:310-315 |
| execute_csharp output | 30,000 chars then "... (output truncated)" | src/SwAgent/Scripting/ScriptHost.cs:29, 214 |
| Files input | image <= 5 MB, PDF <= 30 MB raw (base64 ~ +33%) else too_large | FileAttachments.cs:17-18 |
| View capture | 1024 px JPEG | ToolRunner.cs:203 |
| Tool-index line | title + description, one line per tool | systemPrompt.ts:162-164 |
| Model/effort for a tool run | only via tier (table above) | models.ts:61-70, store.ts:1564 |

There is no client-side token budget, no summarisation/compaction and no total-context guard: the only shrinking is
the server-side clearing above. The stored chat (`messages`) stays complete and append-only (runAgent.ts:109-110, 240-242);
`repairHistory` (ui/src/agent/history.ts:17-59) only patches unanswered tool_use blocks with an `INTERRUPTED` result.

Caveat on PDFs: a 30 MB PDF passes the add-in's limit but, base64-encoded, exceeds the Anthropic request-size limit
(32 MB per request as documented; PDF support is also limited to 100 pages - verify against current docs). Because the
staged user message is never cleared, a drawing PDF is re-sent with every one of the up-to-200 requests (cached reads
while the cache is warm). Recommend capping drawing PDFs (e.g. <= 10 MB / <= 20 pages) for this tool.

---------------------------------------------------------------------------------------------------
## 3. How a long, multi-phase tool run (minutes to hours, with questions) behaves today

* **One turn.** The whole run is one `runAgent` loop (runAgent.ts:207-344). `ask_user` / `ask_form` are tool calls
  that await a Promise resolved by the question card (runAgent.ts:362-377; store.ts:659-663, `RunState.question`).
  There is **no timeout**: the run can wait for hours. Each answer becomes a `tool_result` and is never cleared.
* **Step budget.** Every API round trip (each tool batch, each question) counts toward the 200 steps. A mold design
  with dozens of execute_csharp cells, captures, checks and calculator calls can realistically exceed 200; the turn
  then ends with error "Stopped after 200 steps." (runAgent.ts:345), shown in the banner (store.ts:1587-1592).
* **Continuing after a stop/limit/error.** The user types "continue" in the same chat -> `send` -> `submitPrompt`
  WITHOUT the RunOverride: the run uses the **chat's model/effort** (`run.model ?? thread.model`, store.ts:618-619) and
  the **normal approval setting** (store.ts:621; auto-approve only if the user chose "always" earlier, 680-681). So a
  `max`-tier tool continued in a Sonnet chat silently drops to Sonnet, and execute_csharp starts asking for approval.
  Recommend: remember the tool-run override on the thread (e.g. `thread.toolRun = {tool, model, effort, until}`) and
  reuse it for follow-ups while the tool run is "open", or have the 200-step stop auto-continue for tool runs.
* **Context decay.** After 40k input tokens only the last 6 tool uses survive; earlier measurements, structural
  calculation results and analysis outputs become placeholders. The model keeps its own text and thinking, the staged
  inputs, the TOOL.md (read_user_tool is excluded) and all answers. Long tools must therefore keep their state outside
  the context window - Smart Rebuild does this with `rebuild_journal` / `checkpoint` / `snapshot_design`
  (src/SwAgent/Tools/RebuildTools.cs:59-66, 212-303, stored under `%LOCALAPPDATA%\...\snapshots\rebuild-<key>`). The
  mold tool needs the same: a design record (JSON) it writes after each phase and re-reads (see 6.4).
* **Prompt cache across questions.** With the 5-minute TTL, any question answered after > 5 min makes the next
  request a full cache write of the whole prompt (system + tools + the staged PDFs/images + history). For hour-long runs
  consider the 1-hour TTL (`cache_control: {type:"ephemeral", ttl:"1h"}`) for tool runs.
* **Steering.** Messages typed while it runs are queued; "Send now" delivers them with the next tool results without
  stopping the run (runAgent.ts:341-342; store.ts:673-679, queuedBlocks 418-429). Plain queued messages are sent after
  the turn ends (flushQueue 831-858).
* **Stop.** Abort -> `aborted`; queued tool runs are dropped, typed follow-ups go back to the composer (834-844).
* **SolidWorks closes / crashes mid-run.** The chat is persisted after every append (store.ts:628-631), so the history
  survives; the open tool_use gets `INTERRUPTED` on the next request (history.ts:4-6, 42-46). The run itself does not
  resume; the user must continue manually. A macro crash pauses the tool (`paused: macro`) via CrashGuard
  (AgentAddIn.cs:67-88; ToolRunner.cs:112-118).
* **Documents.** Tool calls act on the active document (`tools.run` with docKey WORKSPACE -> DocumentFor,
  AgentController.cs:223-230). Smart Rebuild warns never to close/reopen from code (checkpoint restore handles the
  re-keyed document, AgentController.cs:780-788).
* **ToolRunner `_busy`** only covers the page phase; launching the mold tool again during a run queues a second staged
  prompt in the same chat (store.ts:1569-1577).

---------------------------------------------------------------------------------------------------
## 4. Registry and listing (where a tool is seen)

* `UserTools.ListTools()` (UserTools.cs:522-564): built-in dirs (alphabetical) then user dirs (alphabetical, a name
  equal to a built-in is hidden). Fields emitted: name, builtIn, title, description, icon (one of the 12 `Icons`,
  UserTools.cs:21), kind (`tool` or `workflow`), hasMacro, model, agent, runStyle, intelligence, hasInputs, hasPreview,
  paused, updated, iconSvg, hasPng. The panel gets it via `userTools.list` at boot and `userTools.changed` on every
  `UserTools.Changed` event (AgentController.cs:321, 345-346, 769-773; store.ts:1081-1090).
* Agent tools (C#) are a separate registry: `ToolRegistry.RegisterAll` on `[AgentTool]` methods
  (src/SwAgent/Tools/ToolRegistry.cs:94-115) in `AgentAddIn.OnConnect` (AgentAddIn.cs:52-65). Their definitions are
  fetched **once at panel boot** (`tools.list`, store.ts:1013; AgentController.cs:222) and **all** are sent on every
  request (runAgent.ts:196-201).
* `list_user_tools` agent tool prints `- name (kind): title - description` (UserTools.cs:279-286).
* `read_user_tool(name, file?)` (UserTools.cs:288-304): TOOL.md (or any file inside the folder, path-checked by
  `SafePath` 591-597), plus "Extra files in this tool: ..." (296-301) and, for built-ins, a read-only note (302).
* Ribbon (AgentRibbon.cs:87-158): only `kind == "tool"` (line 92). Command list: 4 fixed (37-43), the tools, then
  New Tool / Options (45-49). Tab boxes (136-141): [fixed + Options] | [built-in tools] | [user tools + New Tool], same
  tab added for part/assembly/drawing (34, 142-151). Built at start-up only; `IsStale()` (58-64) compares a signature
  of name:title:icon-png-mtime and the panel offers Save and restart.
* Workflows menu (ToolsPalette.tsx:20-173): `builtIn` (all kinds), user `workflows`, user `tools` (lines 25-27);
  sections "Workflows" (119-125), "Built in - SolidWorks Agent tools" (127-134), "Tools - on the SolidWorks Agent ribbon tab" (136-143).
  Built-ins get a Run button and no edit/delete (46, 66-78).
* Built-in shipping: `SwAgent.csproj:57-60` copies `tools\builtin\**\*` to `builtin-tools\` next to the dll (recursive,
  so subfolders such as `data/` ship automatically). `UserTools.BuiltInRoot` = `Paths.AddInDir\builtin-tools`
  (UserTools.cs:58).

---------------------------------------------------------------------------------------------------
## 5. Design: `category` front-matter field (backward compatible)

Front matter: `category: Molding & Toolmaking` (the parser splits on the first ':' only, UserTools.cs:604-608, so
'&' and further colons are fine). Missing/blank = no category = today's behaviour.

C# side
1. `UserTools.ListTools` (UserTools.cs:537-556): add
   `["category"] = fm.TryGetValue("category", out var cat) && !string.IsNullOrWhiteSpace(cat) ? OneLine(cat) : null`.
2. `SaveInput` / `EditInput` (UserTools.cs:133-206, 217-277): add
   `[ToolParam("Optional group shown as its own box on the SolidWorks Agent tab and section in the Workflows menu, e.g. \"Molding & Toolmaking\". Reuse an existing category name.")] public string Category`.
   Save: write `category:` after `icon:` (after line 326) when not blank. Edit: `SetHeader(header,"category",...)` in
   the block at 401-409, and add `input.Category` to the "nothing to change" test (374-378).
3. `userTools.setValue` whitelist (AgentController.cs:336): allow `category` so the user can move a tool from the
   panel; for built-ins it lands in builtin-settings.json and `EffectiveFrontMatter` applies it (UserTools.cs:98-106,
   470-478).
4. Ribbon (AgentRibbon.cs):
   * `Signature()` (60-64): append `:{t["category"]}` so recategorising marks the ribbon stale.
   * Command order (95-102): order tools by (builtIn desc, category ?? "", title) so the SolidWorks Agent *menu* reads grouped;
     optionally `group.AddSpacer2(...)` between categories for menu separators (verify the index shifts it causes in
     `AddCommandItem2` positions).
   * Boxes (136-141), replacing the single built-in box and the single user box:
     ```
     fixed+Options
     for each category of built-in tools (in first-seen order): one box
     uncategorised built-in tools: one box
     for each category of user tools: one box  (a user tool in a built-in category name joins that built-in box)
     uncategorised user tools + New Tool: one box
     ```
     `ICommandTabBox` has no caption (boxes are separated by a divider only). If visible category labels on the tab are
     wanted, use one flyout per category (`ICommandManager.CreateFlyoutGroup2`, then put the flyout's command id in a
     box) - but flyouts hide the buttons one click deeper and add more command ids to keep stable; recommend plain boxes
     first and the category name in each button's hint (`Hint` = `"[Molding & Toolmaking] " + description`).
   * IDs stay keyed by name (`StableIds`, 186-216) so hotkeys survive recategorisation; the `unchanged` registry check
     (113-114) compares only ids, so moving between boxes keeps SolidWorks' saved customisation.
5. The agent's tool index (systemPrompt.ts:162-164): `- compression-mold (built-in tool, Molding & Toolmaking): ...`
   - extend the `userTools` element type there (154, 162), in runAgent.ts:133 and pass-through store.ts:624.
   `list_user_tools` (UserTools.cs:285): add the category too.

Panel side
6. `UserToolInfo` (ui/src/lib/types.ts:269-...): add `category?: string | null`.
7. ToolsPalette.tsx (25-27, 119-143): build sections as
   * one section per category (title = category; built-ins first inside it, then the user's), each row as today
     (`row(t, true)` for built-ins and workflows, `row(t, false)` for user ribbon tools);
   * then the existing uncategorised sections unchanged ("Workflows", "Built in - SolidWorks Agent tools", "Tools - on the
     SolidWorks Agent ribbon tab"). Empty-state text (120-124) only when there are no uncategorised workflows AND no categories.
   * Group key: trimmed, case-insensitive category; display the first spelling seen.
8. mockHost.ts:113-127: give the sample `compression-mold` a category so the UI can be checked in the browser mock.

Backward compatibility: older TOOL.md files have no `category` -> null -> exactly today's three groupings. Older
add-in builds ignore the extra front-matter key (ReadFrontMatter keeps all keys; nothing reads `category`).
Saving through older builds' save_user_tool drops the key (Save rewrites the whole header, UserTools.cs:320-337) -
acceptable; Edit keeps unknown header lines (header list is preserved, 384-419).

---------------------------------------------------------------------------------------------------
## 6. Design: reference data files + a C# calculator tool the built-in depends on

### 6.1 Folder layout (ships unchanged via the csproj glob SwAgent.csproj:60)
```
tools/builtin/compression-mold/
  TOOL.md            front matter: kind: tool, agent: true, intelligence: max, confirm: always,
                     context: view, model, history, category: Molding & Toolmaking, requires: mold_calc
  inputs.json        page fields (see 6.2)
  icon.png / icon.svg
  macro.cs           optional: a "gathering" macro that measures the part (projected area, bbox, volume, draft)
  data/materials.json        part-material families -> subtypes -> process data
  data/mold-materials.json   aluminium 7075-T6 / 6061-T6 / QC-10, P20, H13, inserts... (E, yield, density, k)
  data/fasteners.json        SHCS sizes, tensile-stress areas, proof loads, recommended torque
  data/validation/*.json     the worked examples the structural calculation was validated against
```

### 6.2 Page reading the data (ToolPage has no dependent fields today)
Today a `choice` gets fixed `Choices` (ToolPage.cs:267-277) and `OnComboboxSelectionChanged` only handles the tier box
and calls `Changed()` (ToolPage.cs:587-595). Number boxes cannot be filled from a table. Proposed additions to
`ToolInputSpec` (ToolSpec.cs:14-62), all optional (old inputs.json files unaffected):
* `Data`: relative path of a JSON table in the tool folder, e.g. `"data/materials.json"`.
* `ChoicesFrom`: path into it for the entries, e.g. `"families[*].name"` (family) or
  `"families[name={part_family}].subtypes[*].name"` (subtype, filtered by another input's current value).
* `FillFrom`: for number/text inputs, the row field to copy when the source choice changes, e.g.
  `"part_subtype.shrink_pct"`.
* `Unit` gains documented non-geometric values (`pressure`, `temperature`, `mass`, `percent`, `none`): stored as plain
  numbers; the Label carries the unit (`Press pressure (bar)`); BuildPayloadAsync (ToolRunner.cs:232-240) appends the
  unit text for them instead of the bare double.
Implementation in ToolPage: load tables once in `Build()` via `UserTools.DirOf(_spec.Name)`; on a combobox change,
repopulate dependent comboboxes (`IPropertyManagerPageCombobox.Clear()` + `AddItems`) and set dependent number boxes'
`Value`; keep a per-field "edited by user" flag (OnNumberboxChanged, ToolPage.cs:581) so a table refill doesn't overwrite
a value the user typed. In `BuildPayloadAsync` emit both: `value` and `table` (the table value) so `<tool_inputs>` reads
`- Shrinkage, % (shrink): 1.8 (table: 1.5 - edited by the user)`. Same logic must be mirrored in
`ToolPage.TryFromSelection` (664+) for the skip-page path (moot with `confirm: always`).

### 6.3 Code and agent reading the same data
* Macro/preview: add `public string ToolDir` and `public JToken ToolData(string relPath)` to `ScriptGlobals`
  (src/SwAgent/Scripting/ScriptGlobals.cs, next to `Inputs` line 84), set in `ScriptHost.RunMacroAsync`
  (ScriptHost.cs:169) and in the preview runner (ToolPage.cs:498).
* Agent: `read_user_tool(name:"compression-mold", file:"data/materials.json")` already works (UserTools.cs:291-303) -
  but **read_user_tool is excluded from context clearing** (runAgent.ts:155), so a big table read that way stays in the
  context for the rest of the run. Prefer the calculator tool to look rows up and return only what is needed.
* User-editable material data: built-in folders are read-only and replaced on install. Overlay file
  `%APPDATA%\SwAgent\tools\builtin-data\compression-mold\materials.json` (merged by row id over the shipped table),
  written by a later "save as my default" action; mirrors how builtin-settings.json overrides front matter.

### 6.4 The calculator as a C# agent tool
* New class, e.g. `src/SwAgent/Tools/MoldTools.cs`, with `[AgentTool]` methods (signature `(ToolContext, TInput)`,
  ToolRegistry.cs:94-115; input POCOs with `[ToolParam]`, snake_case schema auto-generated 188-221):
  * `mold_plate_sizing` - inputs: mold material id, part material id, press force or pressure x projected area,
    temperature, cavity layout footprint (mm), plate role (lid/base/cavity), weight and bounding-box limits, cycle life;
    output: thickness, deflection, bending stress vs fatigue-reduced allowable, bolt count/size/pitch with preload and
    margin, plate masses vs the limits, and which limit governs - as a short text table **plus** a fenced JSON block.
  * `mold_material_lookup` - returns one row of materials.json / mold-materials.json (with user overlay applied).
  * `mold_design_record` (read/append) - the JSON design state per mold (like `rebuild_journal`,
    RebuildTools.cs:283-303), so phase results survive context clearing; add it to `exclude_tools` (runAgent.ts:155) only
    for `read` if its output is small.
* Keep the math in a pure static class (no SolidWorks types) so it can be unit-tested against
  `data/validation/*.json` in a test project; the [AgentTool] wrapper only parses inputs and formats.
* Register in `AgentAddIn.OnConnect` after line 65: `tools.RegisterAll(new MoldTools());`. It then reaches the panel
  through `tools.list` at boot (store.ts:1013) - tool definitions are global (sent on every chat request, cached), so
  keep descriptions short and prefixed "For the compression-mold tool: ...".
* Macros can call it too: `await Agent.CallToolAsync("mold_plate_sizing", new {...})` (ScriptGlobals.cs:35, 148-160;
  wired in ScriptHost.cs:93), so the hard-coded and agent paths compute identically.
* Dependency declaration: front matter `requires: mold_plate_sizing, mold_material_lookup`. ToolSpec.Load reads it;
  ToolRunner (after line 61) checks `_tools.Names` and stops with a status-bar message if one is missing (protects a
  user tool copied to an older add-in). ListTools can expose `requires` for the panel.

### 6.5 Hooks for the later MCP simulators (machinability, process/demolding)
There is no MCP client anywhere today (no "mcp" in src/SwAgent or ui/src). Leave hooks now:
* A stable export contract: a `mold_export` tool/phase that writes per-body STEP files + `design.json` (inputs, material
  rows, sizing results, parting line, critical faces as persistent refs, gates/vents) into a per-design folder; the
  simulators consume that.
* Named phase gates in TOOL.md ("Phase 5 - external checks: if tools named check_machinability / simulate_compression
  are available call them with design.json, else record 'not run' in the report").
* Two options to connect later: (a) the add-in hosts an MCP client and registers each remote tool into ToolRegistry
  (dynamic registration needs a refresh of `tools.list`, currently fetched once at boot, store.ts:1013); (b) the panel
  passes remote MCP servers to the Messages API (MCP connector beta) - only for URL-reachable servers.

---------------------------------------------------------------------------------------------------
## 7. Concrete recommendations for the mold tool's front matter and inputs

* `kind: tool` (page-driven; appears on the ribbon and in the menu), `agent: true`, `intelligence: max`,
  `confirm: always`, `context: view, model, history` (`model` is essential - it is the only way the agent learns the
  active document in a tool run; `selection` does nothing), `category: Molding & Toolmaking`.
* inputs.json order = importance: mold_material (options), part_family (choice, Data), part_subtype (choice,
  ChoicesFrom), process fields (number, FillFrom, `Unit: none`, unit in label), press_pressure, press_temperature,
  cavities (integer, Min 1), max_plate_mass (none, kg), max_plate_bbox (3 length numbers or one text "L x W x H mm"),
  max_base_mass, max_lid_mass, cycle_life (integer), part_file (files, Accept model, Multiple false), drawing
  (files, Accept pdf+image), critical_faces (selection, Required false), notes (text), decisions (options, as Smart
  Rebuild), copy/working file options. Use `Group` to make boxes (Mold, Material, Press, Limits, References).
* TOOL.md: phases with a design record written after each phase; an explicit override of the one-line-finish rule;
  acceptance checks stated as measurable rules (plate mass <= limit, bbox <= limit, bolt margin >= x, no parting line
  on critical faces...).
* Expect > 200 steps: either split the run into phases the user continues (and fix the model/approval drop on
  continue - section 3) or raise the iteration limit for tool runs (runAgent.ts:207).
