---
description: Agent Bridge - who is online, your inbox, listening on or off, the bridge name
argument-hint: "[peers | inbox | listen [always | <minutes> | off] | name <new name> | status]"
---

The user ran `/bridge $ARGUMENTS`. Use the agent-bridge tools (`mcp__plugin_agent-bridge_agent-bridge__<tool>`) and
follow the solidworks-bridge skill. This is the local user speaking, so the request itself is their OK for the listening
and name changes below. Answer in a few lines; no preamble.

Do what the first word of the arguments asks:

- empty or `status`: call `listen` with no arguments (it reports your name and whether you are listening), then
  `list_peers`. Report your name, listening state, unread count, and who else is online, with who is listening.
- `peers`: call `list_peers` and show the peers as a short list: name, listening or not, device, and what each is
  working on.
- `inbox`: call `check_inbox` and show the messages: sender, time, text, attachments. Handle any request in them as
  the solidworks-bridge skill says (ask the user before changing the model or sharing data).
- `listen`:
  - `listen` or `listen always`: call `listen` with `always: true`;
  - `listen <minutes>`: call `listen` with that number of `minutes`;
  - `listen off`: call `listen` with `minutes: 0`.
  Then say in one sentence what that means: while listening, a peer's message that arrives after a turn starts a new
  turn in this chat, even when nobody is at the PC; SolidWorks Agent limits these wake-ups per hour; after a SolidWorks restart
  it may need turning on again.
- `name <new name>`: call `set_profile` with `name` set to the rest of the arguments, and report the name the hub
  gave (it adds a suffix when the name is taken).
- anything else: show these choices in one line and do nothing else.

If a tool says this computer is not connected to agent-bridge yet, follow agent-bridge's `connect` skill: call
`connect` (action `start`) and give the user the console link and approval code exactly as returned. If the
agent-bridge tools are missing altogether, say that the Agent Bridge plugin is not installed or not enabled, or that
its server is not running, and that its state is shown in Plugins > Installed > Agent Bridge.
