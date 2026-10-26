# Claude MCP

Lets **Claude Desktop** on this PC talk to the Unify3D agent in SolidWorks. Works on this PC only: no hub, no
agent-bridge, no network port.

## What it does

Installing it adds one MCP server, named `unify3d`, to Claude Desktop's settings file
(`%APPDATA%\Claude\claude_desktop_config.json`; the install dialog shows the exact path). The server is a small program
that ships with Unify3D (`%LOCALAPPDATA%\SwAgent\local-mcp\Unify3D.LocalMcp.exe`). Nothing else in the file changes, and
Unify3D keeps a backup of it in its own data folder.

In Claude Desktop you then get these tools:

| Tool | Does |
|---|---|
| `ask_unify3d` | Sends a message to the agent in SolidWorks and returns its reply |
| `get_unify3d_answer` | Gets a reply that took longer than the wait, by its ticket |
| `unify3d_status` | Whether SolidWorks runs, the active document, the open chat, automatic turns left |
| `unify3d_model_context`, `unify3d_feature_tree`, `unify3d_selection`, `unify3d_open_documents` | Read the open model |

## Safety

- A message from Claude Desktop shows in the Unify3D chat as "Message from Claude Desktop". Any change to a model waits
  for your OK in the Unify3D panel, whatever you allowed before in that chat.
- Messages count against the automatic turns per hour (Options > Plugins).
- Settings > **Let Claude Desktop read the open model** turns off the four read tools.
- Messages, replies and model facts go to Claude Desktop's AI provider.

## After installing

Quit Claude Desktop from its tray icon (closing the window leaves it running) and open it again. The Installed tab
shows "Connected" once Claude Desktop has used it.

## Removing it

Turn the plugin off, or uninstall it. Unify3D removes the `unify3d` entry from Claude Desktop's settings and nothing
else. Restart Claude Desktop to drop the tools.
