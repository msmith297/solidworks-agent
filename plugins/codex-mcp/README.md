# Codex MCP

Lets **Codex** on this PC (the Codex app, the CLI and the IDE extension) talk to the Unify3D agent in SolidWorks. Works
on this PC only: no hub, no agent-bridge, no network port.

## What it does

Installing it adds one MCP server, named `unify3d`, to Codex's `config.toml` (`%USERPROFILE%\.codex\config.toml`, or
`CODEX_HOME`; the install dialog shows the exact path). It goes in a marked block at the end of the file:

```toml
# >>> unify3d (managed by Unify3D Agent: the Codex MCP plugin; turn it off in Unify3D to remove) >>>
[mcp_servers.unify3d]
...
# <<< unify3d <<<
```

The server is a small program that ships with Unify3D (`%LOCALAPPDATA%\SwAgent\local-mcp\Unify3D.LocalMcp.exe`). No
other line of the file changes, and Unify3D keeps a backup of it in its own data folder.

In Codex you then get these tools:

| Tool | Does |
|---|---|
| `ask_unify3d` | Sends a message to the agent in SolidWorks and returns its reply |
| `get_unify3d_answer` | Gets a reply that took longer than the wait, by its ticket |
| `unify3d_status` | Whether SolidWorks runs, the active document, the open chat, automatic turns left |
| `unify3d_model_context`, `unify3d_feature_tree`, `unify3d_selection`, `unify3d_open_documents` | Read the open model |

## Safety

- A message from Codex shows in the Unify3D chat as "Message from Codex". Any change to a model waits for your OK in
  the Unify3D panel, whatever you allowed before in that chat.
- Messages count against the automatic turns per hour (Options > Plugins).
- Settings > **Let Codex read the open model** turns off the four read tools.
- Messages, replies and model facts go to Codex's AI provider (OpenAI).

## After installing

Restart Codex (the app has a Restart button; in the CLI, start a new session). `codex mcp list` then shows `unify3d`.

## Removing it

Turn the plugin off, or uninstall it. Unify3D removes its block from `config.toml` and nothing else. Restart Codex to
drop the tools.
