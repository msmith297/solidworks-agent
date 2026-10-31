<p><img src="assets/brand/logo-full.png" alt="SolidWorks Agent" height="72"></p>

<sub>Formerly Unify3D Agent. An independent add-in; not affiliated with or endorsed by Dassault Systèmes. SOLIDWORKS is a trademark of Dassault Systèmes.</sub>

# SolidWorks Agent

An AI design agent for SOLIDWORKS. A chat panel in the Task Pane, a **SolidWorks Agent** ribbon tab and right-click
commands let you talk to Claude or OpenAI's GPT models about the open model. The agent reads and changes the model through the full
SolidWorks API. Each document keeps its own conversations, stored **inside the .sldprt / .sldasm / .slddrw file**.

```
SLDWORKS.exe
 └─ SwAgent.dll  (C# .NET Framework 4.8 add-in, xCAD.NET)
     ├─ Ribbon tab "SolidWorks Agent" (panel commands + your Tools), right-click "Ask SolidWorks Agent" on faces/edges/features
     ├─ Task Pane ─ WebView2 (separate Chromium processes)
     │               └─ React + TypeScript UI  ── agent loop ──▶ Claude API (@anthropic-ai/sdk) or OpenAI Responses API
     │                        ▲ JSON-RPC over postMessage │ tool calls
     ├─ ToolRegistry ◀────────┘───────────────────────────┘
     │   execute_csharp · capture_views · get_model_context · get_feature_tree · get_selection
     │   search_sw_api · get_sw_api_type · list_open_documents · undo · rebuild   (+ ask_user, UI-side)
     └─ Chat storage: third-party stream "SwAgent.Chat.v1" inside each document
```

## Workflows and tools

- **Workflows** are larger saved procedures (e.g. building a compression mold). Run them from the **Workflows** menu in
  the chat panel; the agent follows the saved instructions.
- **Tools** are quick, AI-powered commands on the **SolidWorks Agent** ribbon tab, assignable to hotkeys in
  Tools > Customize > Keyboard (category *SolidWorks Agent*). A tool is a skill with optional hard-coded parts:
  - a native **PropertyManager page** built from its declared inputs (selection boxes, numbers with units, checkboxes,
    dropdowns, option buttons, a note, image/PDF/3D-model files), skipped when everything it needs is already selected;
  - a live **holographic preview** (temporary bodies, rebuilt on every change; switches to on-demand if it gets slow);
  - a **macro** (verified C#, one undo step) - for mechanical tools it is the whole job, no AI involved;
  - for tools with an **agent in the loop**: context captured silently (view, selection, model, feature history) and
    staged with the inputs for the agent at the tool's **intelligence** tier (Fast / Balanced / Max), shown in the panel
    as a single "executing" line.
- The agent creates both with an in-chat questionnaire, from a description, from "what you just did", or from the model.
- **Watch me** (the eye button in the chat box) records what you do in the active part or assembly while it is on:
  every feature you add, edit, rename, suppress or delete, with its type and parameters ("Added Fillet3 (Fillet):
  constant radius 2 mm on 4 edges - with 4 edges of Boss-Extrude1 selected", "Edited Boss-Extrude1: end blind 10 mm
  -> blind 15 mm"), what you selected for it, and a thumbnail of the view after each change. The log is kept compact:
  a feature tweaked right after creating it is one step with its final values, a value dragged through several
  numbers is one edit, something added and deleted again disappears. The camera is never moved. Press
  **Stop & attach** and the log, the final parameters of every feature it touches, the view before and after and a
  strip of step thumbnails go with your next message - "Turn what I just did into a tool" then builds the tool or
  workflow from exactly those steps.
- **3D model files** in a tool's files input, or attached to a chat message (paper-clip button, or drop them on the
  chat box), are read in the background instead of being passed as a bare path. The agent gets six rendered views in
  one image (isometric, front, top, right, back iso, from below, each with an axis triad) and a measurement summary:
  size and bounding box, volume, surface area, bodies / shells, watertightness, units, and SolidWorks mass properties
  where SolidWorks read the file.
  - STL, 3MF and OBJ are parsed, measured and rendered entirely off the SolidWorks UI thread (a small software
    renderer - no window, no GPU, nothing on screen changes).
  - STEP, IGES, Parasolid, SLDPRT and SLDASM are opened by SolidWorks as **hidden** documents, measured,
    tessellated in short slices (SolidWorks stays responsive), and closed again together with anything they pulled in;
    a file you already have open is read in place and left alone. Your documents, camera and view are not touched.
  - Budget: meshes up to 300 MB / 40 s, CAD files up to 400 MB / 90 s, 2.5 M triangles; results are cached per file
    version. If a file can't be read, the agent gets its path and the reason.

Both are created with the agent and stored in `%APPDATA%\SwAgent\tools\<name>\` (`TOOL.md`, `inputs.json`, `macro.cs`, `preview.cs`, `icon.svg`).

## How it works

- **Full API access.** `execute_csharp` runs agent-written C# with Roslyn, in-process on the SolidWorks UI thread.
  It has `swApp`, `Model`, unit helpers `mm()`/`deg()` and `Agent.CallToolAsync(...)` in scope. Variables persist
  between calls in a chat. Each call is recorded as **one undo step** named `Agent: <title>`.
- **Same tools for UI and agent.** Every plugin capability is an `[AgentTool]` method in `src/SwAgent/Tools`. The
  registry generates the JSON schemas the model sees. The UI calls the same tools, for example the camera button
  calls `capture_views`. To add a capability, add a method; the agent picks it up automatically.
- **Seeing the model.** `capture_views` grabs the graphics area. It can also cycle iso/front/top/... views, tile them
  into one labelled image, and then restore the user's view.
- **Exact API signatures.** `search_sw_api` and `get_sw_api_type` reflect over the installed interop assemblies, so
  the agent can check signatures for your SolidWorks version instead of guessing them.
- **Model selector.** Opus 5.5 at *high* effort is the default. Fable 5.1, Sonnet 5.5 and Haiku 5.5 are also
  offered, at efforts from low to max. Requests use adaptive thinking with progress updates, server-side refusal
  fallbacks and prompt caching. With an OpenAI key, GPT-6.1 Sol, GPT-6 Astra and GPT-6 Luna are offered too (see
  *Model providers*).
- **Approvals.** *Ask* (the default) shows the code and waits for **Run / Always allow in this chat / Reject**.
  *Auto* runs without asking. *Read-only* disables tools that change the model.

### Chat history is saved with the file

- Conversations are written into the document as a gzip'd JSON third-party storage stream on **every save**.
  Renaming, moving, copying, Pack and Go and PDM check-in all keep the history. Nothing is keyed by file path; each
  file carries its own `docId`.
- Each chat change marks the document as modified, so SolidWorks prompts you to save. You can turn this off in
  Settings.
- Unsaved chat is also backed up to `%LOCALAPPDATA%\SwAgent\chat-backup\{docId}.json.gz`. If SolidWorks crashes,
  or you close without saving, the next open restores the newer messages and tells you so.
- To keep files small, only the newest two screenshots per conversation are stored in the file. Older ones become a
  placeholder.
- Saved history is replayed exactly. Requests set `thinking.block_binding.prefix_mismatch_behavior: "drop_block"`,
  so a chat reopened later, or after an add-in update, never fails on stale thinking blocks.
- API keys are never stored in documents. Each provider's key is encrypted with Windows DPAPI in
  `%APPDATA%\SwAgent\`: `apikey.bin` (Anthropic) and `apikey-openai.bin` (OpenAI). The `ANTHROPIC_API_KEY` and
  `OPENAI_API_KEY` environment variables also work.

### Model providers

The agent runs on Claude (Anthropic) or on OpenAI's GPT models. Every model belongs to one provider, and a chat runs on
the provider of its model: pick a model from the other provider and the next message goes there, with the whole chat.

- **Keys:** Options > *AI providers* has a row per provider: save, test or remove its key, or get one. Model pickers
  list the models of the providers that have a key, grouped by provider when there are two.
- **Tools on the ribbon** use their intelligence tier on your provider: on OpenAI, Fast is GPT-6 Luna, Balanced
  GPT-6.1 Sol, High and Max GPT-6 Astra. Plugin aliases (`sonnet`, `opus`, ...) map the same way.
- **What differs on OpenAI:** web research can search and open pages from search results, but not a link you give it;
  a refusal ends the turn (no fallback model); reasoning summaries show as progress notes. Options > *AI providers* >
  *Speed* (Standard, Fast, Ultrafast) applies to the models that offer it.
- **Secrets:** plugins, hooks, MCP servers and git never see `ANTHROPIC_*` or `OPENAI_*` variables.
- **Adding a provider** is one adapter in `ui/src/agent/providers/`, rows in the model list, a key file and a CSP
  entry: `docs/providers/design.md` section 10.

## Build and install

## Install

Download `SolidWorks-Agent-Setup-x.y.z.exe` from [Releases](https://github.com/msmith297/solidworks-agent/releases/latest)
and run it (administrator rights are needed to register a SOLIDWORKS add-in). Requires Windows 10/11 x64 and
SOLIDWORKS 2024 or newer. Restart SOLIDWORKS, open the **SolidWorks Agent** Task Pane tab and paste an Anthropic or
OpenAI API key in Options. The add-in checks for updates itself (Options > Updates).

## Build from source

Requirements: SolidWorks 2024+ (developed on 2026), Visual Studio 2022 with the *.NET desktop
development* workload, Node 20+, and the WebView2 runtime (included with Windows 11).

```powershell
.\scripts\build.ps1                     # builds ui/ and the add-in (Debug)
```

Then, in an **Administrator** PowerShell:

```powershell
.\scripts\register.ps1                  # RegAsm /codebase + SolidWorks add-in keys
```

Restart SolidWorks. The add-in loads at startup and also appears under **Tools > Add-Ins** as *SolidWorks Agent*.
Open the **SolidWorks Agent** tab in the Task Pane, paste your API key in Settings, and start chatting.

To uninstall, run `.\scripts\unregister.ps1` as Administrator. Chats already saved in your files stay there,
harmless to SolidWorks.

## Development

- **UI with hot reload, inside SolidWorks:**
  1. Run `npm --prefix ui run dev`.
  2. Set the user environment variable `SWAGENT_DEV_URL=http://localhost:5173`.
  3. Restart SolidWorks.

  The panel then loads the Vite dev server. Right-click > Inspect opens DevTools.
- **UI without SolidWorks:** run `npm --prefix ui run dev` and open `http://localhost:5173/?demo`. A mock host
  (`ui/src/lib/mockHost.ts`) stands in for the add-in and seeds sample conversations (one on Claude, one on OpenAI).
  Add `&fake` to answer the model APIs from canned streams (`ui/src/agent/providers/fake.ts`) instead of the network.
- **Tests:** `dotnet test tests/SwAgent.Tests -c Release` runs the headless tests (mesh reading, measuring and
  rendering; the Watch me diff and log). They need no SolidWorks; set `SWAGENT_TEST_DUMP` to a folder to save a
  sample six-view sheet there.
- **Logs:** `%LOCALAPPDATA%\SwAgent\logs\swagent-YYYYMMDD.log`. Settings > *Open logs* opens the folder.
- **Rebuilding the add-in:** close SolidWorks first, because it locks `SwAgent.dll`. You only need to re-register
  if the output path changes.

## Layout

| Path | What |
|---|---|
| `src/SwAgent/AgentAddIn.cs` | Add-in entry point: ribbon commands, context menus, task pane, document handlers |
| `src/SwAgent/AgentController.cs` | RPC surface for the UI; document, selection and chat events |
| `src/SwAgent/Chat/` | In-file chat storage and crash backups |
| `src/SwAgent/Tools/` | Agent tools: model inspection, view capture, API reference, registry; `ToolRunner` runs the user's tools |
| `src/SwAgent/ModelFiles/` | 3D model context: mesh readers (STL/3MF/OBJ), measurements, software renderer, hidden SolidWorks loading |
| `src/SwAgent/Watch/` | "Watch me": feature snapshots and diffs, the compact activity log, the recorder |
| `tests/SwAgent.Tests/` | Headless tests (xUnit) for the parts that don't need SolidWorks |
| `src/SwAgent/Scripting/` | Roslyn C# REPL that `execute_csharp` runs on |
| `src/SwAgent/UI/` | WebView2 task pane and JSON-RPC bridge |
| `ui/src/agent/` | System prompt and the agent loop (streaming, tools, approvals) |
| `ui/src/components/` | Chat UI |
| `src/SwAgent/Infrastructure/Updater.cs`, `src/SwAgent/update.ps1` | Update check (GitHub Releases), download, install after SOLIDWORKS exits |
| `installer/SolidWorksAgent.iss` | Windows installer (Inno Setup 6) |
| `assets/brand/` | Logo and icons |

## Releasing

1. Put the "What's new" text for users in `RELEASE_NOTES.md` (shown in the in-app update dialog).
2. Tag and push: `git tag v0.2.0; git push origin v0.2.0`.

GitHub Actions (`.github/workflows/release.yml`) builds the UI, the add-in and the installer, and publishes a release with
`SolidWorks-Agent-Setup-0.2.0.exe` and its `.sha256`. Installed copies find it within a few hours and offer the update.
To build an installer locally: `.scriptsmake-installer.ps1 -Version 0.2.0` (needs Inno Setup 6).
