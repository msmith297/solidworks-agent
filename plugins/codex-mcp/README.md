# Codex MCP

Lets **Codex** on this PC (the Codex app, the CLI and the IDE extension) talk to the agent in SolidWorks. Works
on this PC only: no hub, no agent-bridge, no network port.

## What it does

Installing it adds one MCP server, named `solidworks-agent`, to Codex's `config.toml` (`%USERPROFILE%\.codex\config.toml`, or
`CODEX_HOME`; the install dialog shows the exact path). It goes in a marked block at the end of the file:

```toml
# >>> solidworks-agent (managed by SolidWorks Agent: the Codex MCP plugin; turn it off in SolidWorks Agent to remove) >>>
[mcp_servers.solidworks-agent]
...
# <<< solidworks-agent <<<
```

The server is a small program that ships with SolidWorks Agent (`%LOCALAPPDATA%\SwAgent\local-mcp\SolidWorksAgent.LocalMcp.exe`). No
other line of the file changes, and SolidWorks Agent keeps a backup of it in its own data folder.

In Codex you then get these tools:

| Tool | Does |
|---|---|
| `ask_solidworks_agent` | Sends a message to the agent in SolidWorks and returns its reply |
| `get_solidworks_agent_answer` | Gets a reply that took longer than the wait, by its ticket |
| `solidworks_status` | Whether SolidWorks runs, the active document, the open chat, automatic turns left |
| `solidworks_model_context`, `solidworks_feature_tree`, `solidworks_selection`, `solidworks_open_documents` | Read the open model |

## Safety

- A message from Codex shows in the SolidWorks Agent chat as "Message from Codex". Any change to a model waits for your OK in
  the SolidWorks Agent panel, whatever you allowed before in that chat.
- Messages count against the automatic turns per hour (Options > Plugins).
- Settings > **Let Codex read the open model** turns off the four read tools.
- Messages, replies and model facts go to Codex's AI provider (OpenAI).

## After installing

Restart Codex (the app has a Restart button; in the CLI, start a new session). `codex mcp list` then shows `solidworks-agent`.

## Removing it

Turn the plugin off, or uninstall it. SolidWorks Agent removes its block from `config.toml` and nothing else. Restart Codex to
drop the tools.
