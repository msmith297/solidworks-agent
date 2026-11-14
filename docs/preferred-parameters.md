# Preferred parameters

Written 2026-11-03.

The user's preferred design values, such as "M5 self-tapping pilot holes in FDM parts: Ø4.6 mm". They live in one file
the user can open, which the add-in mostly keeps up to date itself.

- **File:** `%APPDATA%\SwAgent\agent-knowledge\preferred-parameters.md`.
  - Values are grouped under `## Topic` headings, one `- yyyy-MM-dd: text` bullet each.
  - Lines the user writes that aren't bullets are kept.
  - Each entry's id is `p` + 6 hex characters, from its topic and text.
- **Separate from memory:** memory (`agent-memory.md`) is for behaviour; this file is for values. The user chose that
  on 2026-11-03.
- **Agent:**
  - The whole file is in the system prompt ("Preferred parameters", `lib/preferences.ts`), with ids.
  - The agent uses an entry by default and says so when one decides something.
  - When the user picks, corrects or confirms a value that applies beyond the current part, the agent saves it
    without being asked: `save_preference(topic, text, replaces?)`. It removes one with `forget_preference(id)`.
  - Both tools are refused in turns the user didn't start, like `remember`.
- **Panel:**
  - The chat shows a chip with Undo (`prefs.undo` puts back what a save replaced).
  - Options > "Preferred parameters" edits and deletes entries, and opens the file.
  - RPCs: `prefs.index`, `prefs.edit`, `prefs.forget`, `prefs.undo`, `prefs.openFile`. The event is `prefs.changed`.
