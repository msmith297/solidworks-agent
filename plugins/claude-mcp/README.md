# Claude MCP

Lets **Claude Desktop** on this PC talk to the agent in SolidWorks. Works on this PC only: no hub, no
agent-bridge, no network port.

## What it does

Installing it adds one MCP server, named `solidworks-agent`, to Claude Desktop's settings file
(`%APPDATA%\Claude\claude_desktop_config.json`; the install dialog shows the exact path). The server is a small program
that ships with SolidWorks Agent (`%LOCALAPPDATA%\SwAgent\local-mcp\SolidWorksAgent.LocalMcp.exe`). Nothing else in the file changes, and
SolidWorks Agent keeps a backup of it in its own data folder.

In Claude Desktop you then get these tools:

| Tool | Does |
|---|---|
| `ask_solidworks_agent` | Sends a message to the agent in SolidWorks and returns its reply |
| `get_solidworks_agent_answer` | Gets a reply that took longer than the wait, by its ticket |
| `solidworks_status` | Whether SolidWorks runs, the active document, the open chat, automatic turns left |
| `solidworks_model_context`, `solidworks_feature_tree`, `solidworks_selection`, `solidworks_open_documents` | Read the open model |

## Safety

- A message from Claude Desktop shows in the SolidWorks Agent chat as "Message from Claude Desktop". Any change to a model waits
  for your OK in the SolidWorks Agent panel, whatever you allowed before in that chat.
- Messages count against the automatic turns per hour (Options > Plugins).
- Settings > **Let Claude Desktop read the open model** turns off the four read tools.
- Messages, replies and model facts go to Claude Desktop's AI provider.

## After installing

Quit Claude Desktop from its tray icon (closing the window leaves it running) and open it again. The Installed tab
shows "Connected" once Claude Desktop has used it.

## Removing it

Turn the plugin off, or uninstall it. SolidWorks Agent removes the `solidworks-agent` entry from Claude Desktop's settings and nothing
else. Restart Claude Desktop to drop the tools.
