**Nightly build - for testing.** Built from the development branch; expect rough edges. The automatic updater never offers nightly builds - install this one by hand, and install a regular release later to go back.

## New in this nightly

**Chats follow you**
- One chat list for all your files: opening, creating or switching documents never switches the chat, and the agent works in the active document. The chat list shows which files each chat worked in.
- If you start a new chat while the agent is busy, a **Working: ...** chip in the header takes you back to it.
- Chats are kept on this PC. Chats that older versions saved inside your files are added to the list when you open them.

**Faster starts**
- The agent says what it's building first and works in stages, instead of planning a whole design silently before its first step. A note appears if it's been thinking for a while.

**Talk to the agent**
- **Hold a key and talk** (` by default, or a mouse side button): your words appear as you speak and go out when you let go. What you select, add, sketch or mark up while talking goes along with your words, in order - so "this face, about here" means what it should.
- Speech is recognised **on your PC** (Whisper) - the audio never leaves it. Turn it on in Options > Voice; it downloads a speech model once (74-465 MB).
- Answer the agent's questions by voice, too. Typed messages and **Send now** also answer a question the agent is waiting on, instead of queueing behind it.
- Optional: replies read aloud with the Windows voices.

**Markup pen**
- The pen stays on while you turn, pan and zoom the model; every view you mark is kept. Esc when you're done.

**Smart Rebuild** (SolidWorks Agent ribbon tab)
- The first built-in SolidWorks Agent tool: rebuilds a design around a deep change (e.g. a 2-cavity mould to 3). It surveys how the file is built, picks a strategy, agrees a staged plan with you, saves checkpoints, and keeps a journal so a second attempt learns from the first. Works on a copy; leftover bodies go into a "Garbage" folder.

**Getting started**
- A first-run **tour** that points at the real controls (skip it any time; Options > Take the tour shows it again).

**Fixes**
- A crash while the agent's drawings were shown during a SolidWorks command.
- Chats that got stuck on "tool_use without tool_result" after a run was cut off; tools now run in the chat you're in.

## New since v0.1.1

**Tools**
- Tools now have three styles: **Hard coded** (the macro does the job), **Agent in the loop** (the agent writes the code each run from the tool's skill) and **Hybrid** (the macro runs first, hands cases it can't handle to the agent, and the tool learns from them).
- A guided **New tool** screen (ribbon button, command search, Workflows menu).
- Tools get their own PropertyManager pages, live previews and four intelligence levels (Fast, Balanced, High, Max).
- Tool runs that need you jump to the question and bring the panel forward.

**Working with the agent**
- **3D overlays:** the agent can draw on your view - outlines, arrows, circles, dimensions and labels anchored to the model, faded where the model hides them.
- Follow-up messages queue while the agent works, with **Send now**.
- **Undo all** the agent's changes when you resend or edit a message.
- Shorter replies by default, with a Replies setting (Brief / Normal / Detailed).
- Answered questions fold away; dividers mark earlier sessions in the chat.
- The agent sees other views of the model off-screen without moving your camera.

**SolidWorks**
- **Save and restart** reopens your documents, views and unsent messages where you left off.
- A built-in SolidWorks knowledge base (multi-body features, rebuild pitfalls, configurations and more).
- Crash protection: a tool step that crashes SolidWorks is paused, and the agent can find and fix the cause.
