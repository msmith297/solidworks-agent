# Plugins for SolidWorks Agent - design

Written 2026-10-21. Status: design for the build. Read with `HANDOFF.md` (sections 1-6) for the add-in's architecture.

## 0. What the user asked for

- "Plugins" for the SolidWorks Agent add-in, working the way Claude Code plugins work: a plugin can contain everything a
  Claude Code plugin can (commands, skills, agents, hooks, MCP servers) **plus SolidWorks Agent tools** (folders like the built-in
  `design-compression-mold` and `smart-rebuild`: `TOOL.md`, `inputs.json`, `macro.cs`, ...).
- A plugin store / "add plugins" section in settings.
- Tag plugins and call plugin commands with `/` (and similar) in the chat bar.
- Other features and settings that make sense for this.
- A plugin creator built into SolidWorks Agent (a tool or skill), so users can create plugins from inside the agent and bundle
  the tools they made with it.
- The first plugin: **Agent Bridge**, which connects the agent in SolidWorks to agent-bridge.
- Lower priority, same delivery:
  - tool inputs go back to the chat panel instead of native PropertyManager pages ("it looks way better that way");
  - fix the chat panel's bottom button row overflowing to the right when the panel is narrow.

## 1. Principles

1. **Claude Code compatible.** A Claude Code plugin folder installs and works unchanged as far as its parts apply: its
   commands, skills, agents, hooks (the Claude Code hook protocol) and MCP servers. SolidWorks Agent reads the same files with
   the same meaning. The concrete test: the real agent-bridge plugin (`github.com/msmith297/agent-bridge`, folder
   `plugin/`) runs in SolidWorks Agent unmodified: its MCP server (`client/mcp.mjs`) and its hooks (`client/hook.mjs`:
   UserPromptSubmit, PostToolUse, Stop, Stop `--listen` with asyncRewake, SessionStart `--listen`).
2. **SolidWorks Agent additions are additive.** `tools/` in a plugin holds SolidWorks Agent tool folders; an optional `solidworks-agent` object in
   `plugin.json` holds SolidWorks Agent-only metadata. Claude Code ignores both.
3. **Same safety rules as today.** No SolidWorks API calls from timers, hook processes or background threads; everything
   that touches the model still goes through `tools.run` (busy gate, NativeFault). Plugin processes (hooks, MCP servers)
   run outside SolidWorks; they only ever reach the model through agent tools.
4. **Trust is explicit.** Hooks and MCP servers run programs on the user's PC, and plugin tools' C# (`macro.cs`,
   `preview.cs`) runs inside SolidWorks. Installing a plugin that has any of them shows what it will run and asks once
   (review 2026-10-21: tool code was missing from the gate). The built-in marketplace is trusted.

## 2. Plugin format

```
<plugin>/
  .claude-plugin/plugin.json      required (name; everything else optional)
  commands/*.md                   slash commands (Claude Code format)
  skills/<skill>/SKILL.md         skills (Claude Code format; extra files in the folder are allowed)
  agents/*.md                     sub-agents (Claude Code format)
  hooks/hooks.json                hooks (Claude Code format), or "hooks" in plugin.json (object or path)
  .mcp.json                       MCP servers ({ "mcpServers": {...} }), or "mcpServers" in plugin.json (object or path)
  tools/<tool>/TOOL.md ...        SolidWorks Agent tools and workflows (same folder format as %APPDATA%\SwAgent\tools)
  README.md                       shown in the store's detail view
```

`plugin.json` (Claude Code fields, all honoured where meaningful):

```json
{
  "name": "agent-bridge",                     // kebab-case, ^[a-z0-9][a-z0-9-]{1,47}$
  "version": "1.0.0",
  "displayName": "Agent Bridge",
  "description": "...",
  "author": { "name": "...", "email": "...", "url": "..." },
  "homepage": "...", "repository": "...", "license": "...", "keywords": ["..."],
  "commands": "./commands",                   // string or array of paths (extra to the default folder)
  "agents": "./agents", "skills": "./skills",
  "hooks": "./hooks/hooks.json",              // or an inline object
  "mcpServers": { "name": { "command": "node", "args": ["${CLAUDE_PLUGIN_ROOT}/x.mjs"], "env": {} } },
  "userConfig": { "key": { "type": "string", "title": "...", "description": "...", "default": "...", "sensitive": false } },
  "solidworksAgent": {
    "tools": "./tools",                       // default ./tools
    "category": "Collaboration",              // store grouping; tools keep their own category
    "icon": "link",                           // a tool icon name, or "./icon.png"
    "minAddInVersion": "0.2.0",
    "env": { "AGENT_BRIDGE_NAME": "SolidWorks Agent - ${SOLIDWORKS_AGENT_DEVICE}" }   // extra env for its hooks and MCP servers
  }
}
```

Variables expanded in hook commands, MCP `command`/`args`/`env` and command bodies:

- `${CLAUDE_PLUGIN_ROOT}` and `${SOLIDWORKS_AGENT_PLUGIN_ROOT}`: the installed plugin folder;
- `${CLAUDE_PLUGIN_DATA}`: a per-plugin writable folder `%APPDATA%\SwAgent\plugins\data\<plugin>`, which survives
  updates;
- `${SOLIDWORKS_AGENT_DEVICE}`: the hostname;
- `${user_config.<key>}`: the user's value for a `userConfig` key (a sensitive value is stored DPAPI-encrypted);
- and environment variables - of the hook or MCP server only: command and skill text never reads SolidWorks'
  environment (it goes to the model; review 2026-10-21).

## 3. Where things live (per PC, `%APPDATA%\SwAgent\plugins\`)

```
plugins\
  installed.json                  { plugins: { "<name>@<marketplace>": { version, source, enabled, installedAt, updatedAt,
                                     path, trusted, userConfig: {...} } }, marketplaces: { "<name>": { source, path, updatedAt } } }
  marketplaces\<marketplace>\     a copy/clone of each marketplace (holds .claude-plugin/marketplace.json)
  cache\<plugin>\<version>\       the installed plugin files (read-only; replaced on update)
  local\<plugin>\                 plugins the user creates in SolidWorks Agent (editable; the "local" marketplace)
  data\<plugin>\                  CLAUDE_PLUGIN_DATA
```

- Built-in marketplace "solidworks-agent": shipped with the add-in in `<AddInDir>\builtin-plugins\` (from `plugins/` in this
  repo; the installer copies it like `builtin-tools`). Its `.claude-plugin/marketplace.json` lists the first-party
  plugins: `agent-bridge` first.
- Local marketplace "local": `plugins\local\*`, listed automatically. A plugin made with the plugin creator lives here
  and can be edited.
- Test isolation: `SWAGENT_DATA_DIR` redirects all of this (as it does for tools).

### Marketplaces

- Format: Claude Code `.claude-plugin/marketplace.json`:

  ```
  { name, owner, plugins: [ { name, source, description, version, category, keywords } ] }
  ```

- Plugin `source` kinds:
  - `"./relative/path"`, a folder inside the marketplace;
  - `{ "source": "github", "repo": "owner/repo", "ref"? }`;
  - `{ "source": "git", "url": "...", "ref"? }`;
  - `{ "source": "git-subdir", "url": "...", "path": "plugin", "ref"? }`;
  - `{ "source": "url", "url": "https://...zip" }`.
- Adding a marketplace: a GitHub `owner/repo`, a git URL, a local folder, or an https URL of a `marketplace.json`.
- Fetching:
  - git uses `git.exe` when it is on PATH (shallow clone; update = fetch + hard reset to the ref);
  - without git, a GitHub source is fetched as a zip from codeload.github.com;
  - HTTP goes through one `HttpClient` set up like `Updater.CreateClient` (TLS 1.2, User-Agent).
- "Update" re-fetches a marketplace and offers plugin updates when a listed version (or the source commit) changed.
  Setting `plugins.autoUpdate` updates enabled plugins at startup, in the background, files only; nothing in
  SolidWorks.

## 4. Components at run time

### 4.1 Tools (SolidWorks Agent extension)

- Plugin tools join the tool index (`UserTools.ListTools`) with:
  - `plugin: "<name>"`;
  - a namespaced name `<plugin>:<tool>` (the colon is allowed only for plugin tools);
  - `builtIn`-style read-only protection, except for plugins in `local\`, which stay editable through the existing
    tool tools.
- Disabled plugins contribute nothing.
- The ribbon, the Workflows/tools palette, `[Run tool: ...]` / `[Run workflow: ...]`, `read_user_tool` and `ToolRunner`
  resolve namespaced names through one lookup, `ToolLocator.DirOf(name)` (user, then built-in, then plugins).
- Per-PC settings of plugin tools go in `builtin-settings.json`-style storage keyed by the namespaced name.
- `userTools.changed` and `ribbon.stale` fire when plugins change.

### 4.2 Slash commands

- `commands/*.md`, Claude Code format:
  - front matter: `description`, `argument-hint`, `model`, `allowed-tools` (advisory), `disable-model-invocation`;
  - body with `$ARGUMENTS`, `$1`..`$9`;
  - `@relative/path` includes a file from the plugin folder;
  - `` !`cmd` `` (shell) is **not** run; it is left as text, with a note.
- Invoked as `/<plugin>:<command>`, or `/<command>` when the name is unique.
- The expanded body is sent as the user's message, with a chip showing the command. Its `model` front matter applies
  to that run.
- Built-in commands, in the same autocomplete:

  | Command | Does |
  |---|---|
  | `/help` | lists commands |
  | `/clear` | new chat |
  | `/plugins` | opens the plugin screen |
  | `/model <name>`, `/effort <level>` | set the model or effort |
  | `/tools` | lists tools |
  | `/run <tool>` | runs a tool |
  | `/new-tool` | the existing new-tool flow |
  | `/new-plugin` | the plugin creator |
  | `/settings` | opens settings |

- Each user tool or workflow and each skill is also invocable as `/<name>`.

### 4.3 Skills

- `skills/<skill>/SKILL.md`, Claude Code format: front matter `name`, `description`; body; extra files.
- The system prompt gets a skills index: one line per enabled skill (`<plugin>:<skill> - description`), next to the
  tool index.
- A new agent tool, `use_skill(name)`, returns `SKILL.md` and lists the skill folder's other files. A second tool,
  `read_plugin_file(plugin, path)`, reads one of them, limited to that plugin's folder.
- Context editing never clears `use_skill` results (like `read_user_tool`).
- The user can invoke a skill with `/<skill>`. That sends "Use the skill <name>" plus the arguments.

### 4.4 Agents (sub-agents)

- `agents/*.md`, Claude Code format: front matter `name`, `description`, `tools` (comma list; optional), `model`
  (optional: `inherit`, `sonnet`, `opus`, `haiku` or an id; aliases resolve to the user's provider, see
  docs/providers/design.md 7.4); the body is the agent's system prompt.
- A new panel-side tool, `run_agent(agent, prompt)`, in `LOCAL_TOOLS`. It runs a nested `runAgent`:
  - the system prompt is the agent's body plus the CAD profile essentials;
  - tools are filtered to the agent's list (the panel's local `ask_*` tools are never available to sub-agents);
  - its own step limit (default 50);
  - it returns the final text.
- The nested run's tool calls show collapsed in the parent's tool card.
- An index of agents (`<plugin>:<agent> - description`) goes in the prompt.

### 4.5 Hooks (Claude Code hook protocol)

Events implemented, with what SolidWorks Agent sends on stdin as JSON:

| Event | When | stdin extras |
|---|---|---|
| `SessionStart` | the add-in's panel session starts (boot) | `source: "startup"` |
| `UserPromptSubmit` | before a user message (or a queued or woken one) is sent | `prompt` |
| `PreToolUse` | before an agent tool runs (any tool, including MCP and local ones) | `tool_name`, `tool_input` |
| `PostToolUse` | after it returns | `tool_name`, `tool_input`, `tool_response` |
| `Stop` | the agent's turn ended | `stop_hook_active` |
| `SessionEnd` | SolidWorks closes or the add-in disconnects | `reason` |

Common stdin fields:

- `session_id`: the SolidWorks Agent session id (see 4.7);
- `transcript_path`: a JSONL export of the active thread, written on demand;
- `cwd`: the active document's folder, else `%USERPROFILE%`;
- `hook_event_name`.

`PermissionRequest` is not implemented in v1. SolidWorks Agent's own approval cards remain.

**Matchers.** For PreToolUse and PostToolUse a regex is matched against the tool name (`"*"` or empty matches all).
MCP tools are named `mcp__<server>__<tool>`, as in Claude Code.

**Running a hook.**

- `type: "command"` only. It runs as Claude Code runs it on Windows: through Git Bash (`usr\bin\bash.exe -c`) when Git
  for Windows is installed, else through `cmd.exe /c` (then bash syntax such as `$NAME`, `2>/dev/null` or `.sh`
  scripts fails; `plugin_validate` warns). With:
  - the plugin env;
  - `CLAUDE_PLUGIN_ROOT`, `CLAUDE_PROJECT_DIR`;
  - `CLAUDE_CODE_SESSION_ID` (= the session id, so plugins that expect Claude Code find it);
  - `SOLIDWORKS_AGENT=1`.
- `timeout` is in seconds (default 60).
- It runs off the UI thread. A hook never touches SolidWorks; results are posted back to the UI thread.

**Outputs** (Claude Code semantics):

| Exit code | Meaning |
|---|---|
| 0 | stdout may be JSON |
| 2 | blocking: stderr is the reason (PreToolUse: the tool is denied with that reason; UserPromptSubmit: the prompt is blocked and shown; Stop: the turn continues with the reason as a user-role message) |
| other | non-blocking error, logged |

JSON on stdout:

- `{ "decision": "block", "reason": "..." }`: Stop continues the turn with the reason; PostToolUse adds the reason
  after the tool result;
- `{ "hookSpecificOutput": { "hookEventName", "additionalContext" } }`: UserPromptSubmit, PostToolUse and SessionStart
  add it to the model's context. UserPromptSubmit adds it to the user message as a `<hook_context>` block; PostToolUse
  appends it to that tool result;
- PreToolUse: `hookSpecificOutput.permissionDecision` (`allow`, `deny`, `ask`) with `permissionDecisionReason`;
- `{ "continue": false, "stopReason": "..." }`: the run stops;
- `systemMessage`: shown to the user as a notice.

**Async hooks.** `"async": true` runs in the background and the turn does not wait. With `"asyncRewake": true`, when the
process exits with code 2 its stdout and stderr (prefixed with `rewakeMessage` when given) wake the agent:

- the session is idle: a new agent turn starts in the session's wake thread (4.7), with that text as a user-role
  message marked as a system event;
- the session is busy: it is queued (the composer's existing queue) and runs after the current turn.

Async hooks are killed after `timeout` or at SessionEnd. A Stop hook with `--listen` style long waits is just a long
async process.

**Loop guard.** At most 20 automatic wakes per hour per session (setting), the same idea as agent-bridge's resume
counter; then a notice.

### 4.6 MCP servers

- **Transport:** stdio (JSON-RPC 2.0, newline-delimited), Streamable HTTP and the older HTTP+SSE (added 2026-10-22,
  creator-v2.md). The client lives in C#: `src/SwAgent/Mcp/` (`McpClient` for stdio, `McpHttpClient` for HTTP, both
  `IMcpConnection`; `McpHost`).
- **Life cycle:**
  1. Start when the plugin is enabled, at boot or on enable.
  2. `initialize` (protocol version `2025-06-18`, client info `solidworks-agent`), then `tools/list`.
  3. Restart with backoff when the process dies.
  4. Stop on disable or SessionEnd.
- **Tools:**
  - exposed to the agent as `mcp__<server>__<tool>`, with the server's `inputSchema`;
  - `notifications/tools/list_changed` → refetch → bridge event `tools.changed`, so the panel refetches `tools.list`;
  - `tools/call` results map to text; image content goes in as image blocks; `isError` maps to an error result.
- **Call path:** MCP tools go through `tools.run` like every agent tool: the busy gate (only if the server is listed as
  touching the model, which is never in v1, so MCP tools skip the gate), hooks, a timeout of 10 minutes by default and
  cancel support (`notifications/cancelled`).
- **Environment:** the plugin's MCP servers get the same env as its hooks, including `CLAUDE_CODE_SESSION_ID`.
  agent-bridge's `mcp.mjs` registers its peer under that id, and its hooks find the same peer through `session_id` on
  stdin. **Not yet true (review 2026-10-21):** `mcp.mjs` trusts `CLAUDE_CODE_SESSION_ID` only for clients named
  "claude", and SolidWorks Agent (`solidworks-agent`) must not claim to be Claude Code (it would join the user's Claude app on the
  hub). Until agent-bridge also trusts it for SolidWorks Agent (`SOLIDWORKS_AGENT=1`), the peer is bound by process id only, and a
  restart loses the binding, the name and "listen always" (docs/plugins/authoring.md section 10).
- **User-level servers:** `%APPDATA%\SwAgent\mcp.json` (the same `mcpServers` shape) adds user servers outside
  plugins, as planned in `docs/compression-mold/design.md` §4.5. The compression-mold tool's simulator hooks H1/H2
  will find their `sim_*` tools this way later.
- **Where it shows:** server state appears in the Plugins screen (running, error, tool count, restart button).

### 4.7 The session

- **The session id:** one SolidWorks Agent session per SolidWorks run. `session_id` is a GUID minted at add-in start. Hooks and
  MCP servers see this one id.
- **The wake thread:** the thread that was last active. If none was, a workspace chat named after the plugin event
  ("Agent Bridge") is created. Messages that wake the agent are shown with the system-event chip: "agent-bridge:" plus
  the text.
- **SessionStart** runs at boot, after the panel is ready.
- **SessionEnd** runs on disconnect, best effort, 3 seconds.

### 4.8 Tags

- In the composer, `@` opens a picker of plugins, tools, skills and agents. Choosing one inserts a chip such as
  `@agent-bridge`. On send, chips become `[Use plugin: agent-bridge]` / `[Use tool: x]` / `[Use skill: x]` lines that
  the system prompt explains: prefer that component's tools and instructions.
- In the plugin store, plugins carry tags: the marketplace's `category` and `keywords`, which can be filtered.

## 5. Panel UI

### 5.1 Plugins screen

`panel: "plugins"`. It opens from the header (a puzzle icon next to Options), from `/plugins`, and from a "Plugins"
section in Options. It has four tabs:

- **Discover**: plugins from every marketplace.
  - Each card shows the name, description, tags and components (counts of tools, commands, skills, agents, hooks and MCP
    servers), plus Install.
  - There is search, and a filter by tag or marketplace.
- **Installed**: each plugin has an enable switch, Update, Uninstall, settings (its `userConfig` fields), MCP server
  status and a details view (README, components).
- **Marketplaces**: add (GitHub `owner/repo`, URL, folder), update, remove. The built-in "solidworks-agent" and "local"
  marketplaces cannot be removed.
- **Create**: starts the plugin creator (5.3), and lists local plugins with Edit and Package actions.

**The trust dialog** opens on install when a plugin has hooks or MCP servers. It shows each command line it will run,
with Install / Cancel.

### 5.2 Composer

- **Slash autocomplete.** Typing `/` at the start of the input opens a popup, filtered as the user types:
  - built-in commands first;
  - then plugin commands, then skills, then tools and workflows;
  - each row has a name, description and source.

  Keys: ↑/↓ to move, Enter or Tab to accept, Esc to close. A command with `argument-hint` puts the hint after the name.
- **`@` tags** work the same way (4.8).
- **Narrow panels (the overflow bug).** The bottom row must never overflow:
  - add `min-w-0` and `flex-wrap`;
  - Model and Effort collapse to icon-only below about 360 px;
  - the Workflows button becomes an icon;
  - "Clear overlay" becomes an icon;
  - the textarea row keeps its size.

  Check with the existing `.zoom-chrome` scale at 100% and 125%. No horizontal scroll at 280 px wide.

### 5.3 Plugin creator

- **Superseded 2026-10-22 by `docs/plugins/creator-v2.md`** (research, triage, prerequisites, `ask_secret`,
  `plugin_try_mcp`); the steps below are the v1 flow.
- **The built-in workflow** `tools/builtin/create-plugin` (kind `workflow`, category "Plugins"). It also runs as
  `/new-plugin`. `TOOL.md` guides the agent:
  1. ask what the plugin is for;
  2. pick a name;
  3. choose components;
  4. bundle existing user tools, by copying them into `plugins\local\<name>\tools\`;
  5. write commands, skills and agents;
  6. optionally add hooks and MCP servers, explained;
  7. validate;
  8. enable;
  9. offer packaging.
- **New agent tools, in `src/SwAgent/Plugins/PluginTools.cs`:**

  | Tool | Does |
  |---|---|
  | `list_plugins` | lists plugins |
  | `read_plugin` | manifest, components, files |
  | `plugin_create(name, description, displayName?, category?)` | creates `plugins\local\<name>` with `.claude-plugin/plugin.json` |
  | `plugin_write_file(plugin, path, content)` | local plugins only; relative paths inside the plugin; validates `.md` front matter and JSON on write |
  | `plugin_add_tool(plugin, tool, move?)` | copies or moves a user tool folder in |
  | `plugin_validate(plugin)` | manifest, names, front matter, hook and MCP shapes; returns findings |
  | `plugin_enable(plugin, enabled)` | |
  | `plugin_package(plugin)` | zips to `Documents\SolidWorks Agent Plugins\<name>-<version>.zip`, with a `marketplace.json` alongside so the folder can be shared as a marketplace |

### 5.4 Settings (in Options, a "Plugins" rollout)

| Setting | Default |
|---|---|
| Auto-update plugins | on |
| Allow plugin hooks | on, with the trust prompt |
| Allow MCP servers | on |
| Automatic wakes per hour | 20 |
| Show plugin events in chat | on |

Plus a "Manage plugins" button.

## 6. The Agent Bridge plugin (the first plugin)

- **Where it lives:** `plugins/agent-bridge/` in this repo, listed in the built-in marketplace.
- **What it contains:**
  - **Its runtime, by reference.** The marketplace entry's `source` is
    `{ "source": "git-subdir", "url": "https://github.com/msmith297/agent-bridge.git", "path": "plugin" }`, so SolidWorks Agent
    installs the real agent-bridge plugin (MCP server + hooks + skills) and updates it with the hub. Nothing is
    vendored, and this needs no change to the agent-bridge repo.
  - **SolidWorks Agent overlay.** A small companion plugin, `agent-bridge-solidworks` (in `plugins/agent-bridge-solidworks/`),
    installed automatically with it. It holds:
    - a skill, `solidworks-bridge`: how to act on bridge messages inside SolidWorks (never edit the user's model on a
      peer's request without the user's OK; report what you changed; summarise the open document when asked who you
      are);
    - `solidworksAgent.env` that sets `AGENT_BRIDGE_NAME` to "SolidWorks Agent - <device>" (editable through `userConfig`);
    - a `/bridge` command: list peers, show the inbox, toggle listening.

    Both plugins appear together as "Agent Bridge" in the store (the overlay depends on the runtime).
- **Requirements:**
  - `node.exe` on PATH (checked at install, with a clear message and a link to nodejs.org);
  - `~/.agent-bridge/config.json` from pairing. When it is missing, the plugin's own `connect` skill and MCP `connect`
    tool pair the PC: the user approves a code in the console.
- **Acceptance:**
  - after install and enable, the SolidWorks Agent appears in `list_peers` as "SolidWorks Agent - <device>";
  - a message from the console reaches it: a UserPromptSubmit-injected context on the next prompt, or an asyncRewake
    wake when idle;
  - it can reply with `send_message`;
  - "listen always" survives a SolidWorks restart (SessionStart `--listen`).

## 7. Tool inputs back in the chat panel

- **What changes:** tool inputs render in the chat panel as an inline **tool card form**, not in the native
  PropertyManager page.
- **Settings:** "Tool inputs: Panel (default) / PropertyManager page". The page stays for users who prefer it, and is
  the fallback.
- **Form logic stays in C#.** The logic is `Tools/ToolData.cs` `ToolForm`: defaults, cascading choices (`ChoicesFrom`,
  `DependsOn`, `RowFilter`, `ValueMap`), `Fills`, units and `UnitChoices`, `Optional`/`Enabled`, `ShowWhen`,
  `Remember`, group expansion, validation. The panel renders the state ToolForm produces. New RPCs:

  | RPC | Does |
  |---|---|
  | `toolForm.open(tool)` | returns `{ formId, title, groups[{ id, title, expanded, inputs[...] }], okEnabled, okReason }`; each input is `{ id, type, label, tip, value, unit, unitChoices, choices, visible, enabled, optional, on, error, dims, accept, multiple, filter }` |
  | `toolForm.change(formId, id, value \| unit \| on)` | returns the updated state |
  | `toolForm.pick(formId, id)` | selection inputs: starts "take the current SolidWorks selection" |
  | `toolForm.ok(formId)` | the same run path as the page's OK: ToolRunner continues (macro, payload, agent) |
  | `toolForm.cancel(formId)` | |

- **Selection inputs:**
  - shown as a list box of chips ("Face <1>", ...) with a "Use selection" button, and live updates while focused (the
    existing `selection.changed` event);
  - previews (`preview.cs`) still draw in SolidWorks (temporary bodies). They run from the C# side on `change`, never
    from timers, as today.
- **Files inputs** use the existing `files.pick` and drop.
- **Where it shows:** the card appears in the active chat, above the composer, with the same visual language as
  `FormCard` / `ToolCard`. OK runs the tool, and its output then flows into the chat as today.
- **What doesn't change:** ribbon buttons, `/run`, the Workflows palette and the agent's `[Run tool:]` all open the
  panel form. `TryFromSelection` (skip the form when the selection already covers the inputs and `confirm` is
  `missing`) is unchanged.
- **Removed:** nothing. `ToolPage.cs` stays as the alternative.

## 8. Build tracks (parallel, each owns its files)

| Track | Owns | Delivers |
|---|---|---|
| **A: plugin core (C#)** | new `src/SwAgent/Plugins/` (`PluginManager`, `PluginManifest`, `Marketplaces`, `PluginTools`, `CommandIndex`, `SkillIndex`, `AgentIndex`), `UserTools.cs`/`ToolSpec.cs` lookup changes for namespaced plugin tools, RPC registrations in a new `PluginsBridge.cs` (called from `AgentController.AttachBridge`, one line) | install, update, enable and remove; marketplaces; component indexes; agent tools of 4.3 and 5.3; RPCs `plugins.list`, `plugins.marketplaces`, `plugins.install`, `plugins.uninstall`, `plugins.enable`, `plugins.update`, `plugins.addMarketplace`, `plugins.removeMarketplace`, `plugins.updateMarketplace`, `plugins.commands`, `plugins.skills`, `plugins.agents`, `plugins.readme`, `plugins.setConfig`; events `plugins.changed`; built-in marketplace folder + csproj/installer copy |
| **B: runtime (C#)** | new `src/SwAgent/Mcp/` and `src/SwAgent/Plugins/Hooks/` (`HookRunner`, `HookEvents`, `Session`), the `tools.run` hook points (one call each in `AgentController`) | MCP client and host, `tools.list` merge and `tools.changed`; hook execution for every event; async/rewake → bridge event `session.wake { text, source }`; RPCs `hooks.fire(event, payload)` returning `{ block, reason, additionalContext[], systemMessages[], continue }` for the panel-side events (UserPromptSubmit, Stop), `session.info` |
| **C: panel (UI)** | `ui/src/components/PluginsScreen.tsx` (new), `Composer.tsx` (slash and @ popups, narrow-row fix), `SlashMenu.tsx` (new), `store.ts` (plugin state, commands, hook calls around send and turn end, wake handling, `run_agent`), `runAgent.ts` (`run_agent` local tool, PreToolUse/PostToolUse via `tools.run` already), `systemPrompt.ts` (skills, agents and tag lines), `types.ts`, `mockHost.ts` cases | everything in 5.1, 5.2 and 5.4; slash commands and built-ins; tags; sub-agents |
| **D: tool forms in the panel** | `Tools/ToolFormBridge.cs` (new, wraps `ToolForm`), `ToolRunner.cs` page-vs-panel branch, `ui/src/components/ToolFormCard.tsx` (new), its store slice | section 7 |
| **E: plugins content** | `plugins/agent-bridge-solidworks/`, the built-in marketplace's `marketplace.json`, `tools/builtin/create-plugin/` | sections 5.3 (content) and 6 |

Shared contracts are frozen in this document (RPC names and shapes above). Integration merges A-E and wires the two
one-line hooks in `AgentController`. Tests: headless unit tests per track, `SWAGENT_DATA_DIR` isolation, a fake MCP
server (node script in `tests/fixtures/mcp-echo.mjs`) and fake hook scripts. The agent-bridge acceptance (6) needs the
real hub and is checked by hand at the end.
