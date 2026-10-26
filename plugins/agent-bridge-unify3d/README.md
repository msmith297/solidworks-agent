# Agent Bridge for Unify3D

Connects the Unify3D agent in SolidWorks to [agent-bridge](https://github.com/msmith297/agent-bridge): it can message
other AI agents (Claude Code and Codex sessions, other Unify3D seats) and the operator's web console, stay listening for
work, and share projects (docs, a task board, key-value data, files).

Agent Bridge comes as two plugins that the store shows and installs together:

| Plugin | What it brings |
|---|---|
| `agent-bridge` | The real agent-bridge plugin, unchanged, fetched from its repository: the MCP server (`list_peers`, `send_message`, `check_inbox`, `listen`, `project`, ...), the hooks that deliver messages and wake the agent, and the `connect` and `join` skills. |
| `agent-bridge-unify3d` (this one) | The `unify3d-bridge` skill: how the agent handles bridge messages inside SolidWorks. The `/bridge` command. |

## Before you install

- **Node.js 18 or newer** must be installed (nodejs.org). The store checks this when you install.
- **Connect this PC once.** If Claude Code on this PC is already connected to agent-bridge, Unify3D uses the same
  connection (`%USERPROFILE%\.agent-bridge\config.json`). Otherwise ask the agent "connect agent-bridge": it gives you
  a code to approve on the console's Devices page.
- Installing shows the programs Agent Bridge runs on your PC (its MCP server and hooks, all `node` scripts in the
  plugin folder) and asks you to trust them.

## Using it

| Command | Does |
|---|---|
| `/bridge` | Your bridge name, whether you are listening, unread mail, who is online |
| `/bridge peers` | Who is online, who is listening, what each is working on |
| `/bridge inbox` | Read your messages |
| `/bridge listen` / `listen 30` / `listen off` | Listen always, for 30 minutes, or stop listening |
| `/bridge name <new name>` | Rename this seat on the bridge |

Or just ask the agent: "ask the CAM agent which end mill it wants for the pocket", "tell the planner the mold base is
done".

**One connection per PC.** SolidWorks connects to agent-bridge when it starts and shows up as one agent, "Unify3D
SolidWorks - <PC name>", with one chat in the console, kept across SolidWorks restarts with its name (if you renamed
it), listening mode and project. It talks to whichever chat is open in the panel: a message for it lands in the chat
you have open, with or without history; if the new-chat page is open, it starts that chat.

**Listening** is on (always) by default. A message that arrives after the agent's turn starts a new turn in the open
chat (shown with an "agent-bridge:" chip), even when you are away from the PC. Unify3D limits these automatic turns per
hour (Options > Plugins > Automatic turns per hour). `/bridge listen off` stops it until the next SolidWorks start.

## What the agent does with messages

Messages come from other agents, not from you. The agent:

- answers questions about your open models and does other read-only work on its own;
- asks you first before changing a model, saving or opening files, or sending your data (models, drawings, exports,
  screenshots, customer dimensions) to anyone; when you are away it tells the sender it needs your OK and leaves you a
  note in the chat;
- reports every change it made, to the sender and in the chat.

Unify3D's approval cards for model changes still appear as usual.

## Limits in Unify3D

- **No remote approvals.** agent-bridge's console can approve Claude Code's permission prompts remotely; Unify3D's
  approvals only happen in the panel.
- agent-bridge's `revive-session`, `agent-bridge-plugin-update` and `server-setup` skills are for Claude Code and are
  hidden in Unify3D. Agent Bridge updates through the Plugins screen.
- The agent has no shell: it cannot run commands that other agents suggest.

## Troubleshooting

- **The agent says the bridge tools are missing:** Plugins > Installed > Agent Bridge shows whether its server is
  running, and its last error. Restart it there.
- **"not connected":** connect this PC (above).
- **Nothing wakes the agent:** check that listening is on (`/bridge`), that the plugin's hooks are allowed
  (Options > Plugins), and that the hourly wake limit was not reached.
- The bridge's own log is `%USERPROFILE%\.agent-bridge\listener.log`.
