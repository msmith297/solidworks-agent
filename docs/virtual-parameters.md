# Virtual parameters

Written 2026-11-03.

## What the user asked for

Named design values on every part and assembly, which the user changes in a table instead of hunting for dimensions.
For example, a tea cup the agent models gets "Wall thickness", "Top diameter" and "Bottom diameter" without being asked.

- **Types:** mostly lengths, but also angles, counts, plain numbers, text ("Engraving text ACME") and choices.
- **Who creates them:** the agent, by default, for the values a designer would want to change and for every value the
  user mentions. It also creates them on request.
- **The table:** opens from a ribbon button (f(x) icon), or with a key (P by default) on selected faces, edges or
  components, filtered to them. In an assembly, the assembly-wide parameters come first, then each part's.
- **The agent and the table:** the agent can open the table, explain what a parameter does and demonstrate it with the
  overlay, and update parameters when asked.

## Decisions (asked 2026-11-03)

| Question | Answer |
|---|---|
| How a changed value reaches the model | **The agent rebuilds it.** Values don't drive geometry by themselves; there is no instant fast path. |
| Applying edits | **Apply button, one message:** a `<vparam_edits>` block, like overlay edits. |
| Assembly-wide values | **The add-in pushes them.** An assembly parameter links to part parameters, and setting it sets theirs. No external references between files. |
| Where they're stored | **Inside the file:** the third-party stream `SwAgent.VParams.v1` (JSON), mirrored as `VP: <name>` custom properties. |
| What P shows | **Filtered to the selection,** with "Show all". |

## Pieces

- **Storage** (`src/SwAgent/Params/VParamDocument.cs`):
  - `VParamDocumentHandler` is registered for every document, hidden components included. It reads the stream on
    open and writes it on every save; a stream that isn't rewritten is dropped.
  - `Commit` updates the custom properties and sets the save flag. The end-of-reply save then stores them.
- **Agent tools** (`VParamTools.cs`):
  - `vparams`, with three actions:
    - `list`: an assembly lists its parts too.
    - `set`: create or update by name; only the fields given change. `drives` names features or dimensions; `links`
      is for assemblies only.
    - `delete`.
  - `show_vparams` opens the table, highlighting the names given.
- **Table** (`VParamTable`): `vparams.table({ document?, selection? })`. Rows are marked `match` when they drive what's
  selected: a face's or edge's feature, or a whole component. The table refreshes on the `vparams.changed` event.
- **Key** (`ParamHotkey.cs`): its own low-level keyboard hook, set by the `paramsKey` setting (default `vk:80`, or
  `off`). It acts only while SolidWorks is in front and no text field, dialog or panel has the keyboard.
- **Panel:**
  - `components/ParamsView.tsx`: the table.
  - `lib/vparams.ts`: edits and the message text.
  - `/params` and the ribbon's Parameters button open the table.
- **Prompt:** the "Virtual parameters" section in `agent/systemPrompt.ts`.
