**SolidWorks Agent 0.2.0**: an AI agent inside SOLIDWORKS that reads, builds and edits your models from a chat in the Task Pane.

## Install on a new PC

1. Download **SolidWorks-Agent-Setup-0.2.0.exe** below and run it. It needs **administrator rights**, because SOLIDWORKS add-ins register for the whole PC. On a work PC, your IT department may need to run it for you.
2. Windows may say **"Windows protected your PC"**, because the installer isn't code-signed yet. Click **More info**, then **Run anyway**.
3. Start SOLIDWORKS (2024 or newer, Windows 10/11 64-bit) and open the **SolidWorks Agent** tab in the Task Pane.
4. Paste an API key in **Options**. Use an **Anthropic** key from platform.claude.com, or an **OpenAI** key from platform.openai.com. Add a monthly spend limit there. The agent is billed per use on that key.

From then on, the add-in updates itself (Options > Updates). To uninstall, use Windows **Settings > Apps**.

## What's new since 0.1.1

**Models**
- Claude **Opus 5.5** (default), **Fable 5.2** (most capable), **Sonnet 5.5** and **Haiku 5.5**, plus OpenAI **GPT-6.1 Sol**, **GPT-6 Astra** and **GPT-6 Luna** if you have an OpenAI key.
- Effort levels from Low to Max, and web research for questions the model can't answer from memory.

**Working with the agent**
- **One chat list for all your files.** Chats follow you between documents, and the agent works in the active one.
- **Talk to it:** hold a key, speak, and let go. Speech is recognised on your PC, so the audio never leaves it.
- **Markups:** draw on the view with several named markups per message (M1, M2, ...), which you and the agent can both refer to.
- **3D overlays:** the agent draws outlines, arrows, dimensions and labels on the model. Double-click a label to edit it.
- **Auto-save:** when a reply ends, the documents it changed are saved.
- **Undo all** the agent's changes when you edit or resend a message. Queue follow-ups while it works, with **Send now**.
- The **Running code** setting decides what needs your OK: ask before changes, run automatically, or read-only.

**Parameters and preferences**
- **Virtual parameters:** a table of named design values stored in the part (the f(x) button on the ribbon, or the P key).
- **Preferred parameters:** tell the agent your usual values (wall thickness, fillets, fasteners) and it uses them.

**Tools and plugins**
- **Tools** in three styles: hard-coded macros, agent-driven, or hybrid. Each tool gets its own PropertyManager page, live preview and saved input profiles. Use the **New tool** screen to make your own.
- **Smart Rebuild:** rebuilds a design around a deep change, in agreed stages, with checkpoints.
- **Plugins:** commands, hooks and MCP servers from a marketplace. The agent can also build plugins from a description.
- Claude Desktop and Codex can message the agent on the same PC.

**SolidWorks**
- **Save and restart** reopens your documents, views and unsent messages.
- SolidWorks message boxes during an agent step are answered safely instead of hanging it.
- On long jobs, the agent saves and closes the windows it no longer needs.
- A built-in SolidWorks knowledge base, and crash protection for tool steps.
- A first-run tour of the real controls.

**Fixes**
- With **Run automatically** on, messages from your other agents no longer stop for approval.
- Chats stuck on "tool_use without tool_result" after an interrupted run, and a crash while overlays were shown during a SolidWorks command.
