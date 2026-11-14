import type { OverlayEdit } from "./types";

/**
 * What the user changed on the agent's overlay: double-clicking a tag in the view changes its value or adds a note
 * (SceneOverlay in the add-in). The edits wait as one chip above the text box and go along with the next message.
 */
export const OVERLAY_TAG = "<overlay_edits";

/** The list with this tag's edit in place of its old one; an edit with no value and no note takes the tag off. */
export function mergeOverlayEdit(edits: OverlayEdit[], edit: OverlayEdit): OverlayEdit[] {
  const rest = edits.filter((e) => e.id !== edit.id);
  return edit.value || edit.note ? [...rest, edit].sort((a, b) => a.id - b.id) : rest;
}

export function overlayEditsName(edits: OverlayEdit[]): string {
  return `${edits.length} overlay edit${edits.length === 1 ? "" : "s"}`;
}

/** One edit in words, for the chip's tooltip and the chat's card. */
export function overlayEditLine(e: OverlayEdit): string {
  const parts = [`${e.type} "${e.original}"`];
  if (e.value) parts.push(`changed to "${e.value}"`);
  if (e.note) parts.push(`note: "${e.note}"`);
  return parts.join(", ");
}

/** The edits for the agent. */
export function overlayEditsText(edits: OverlayEdit[]): string {
  const rows = edits.map((e, i) => {
    const where = e.points.length ? ` (at ${e.points.map((p) => `[${p.join(", ")}]`).join(" to ")} mm)` : "";
    return `${i + 1}. ${overlayEditLine(e)}${where}`;
  });
  return (
    `${OVERLAY_TAG} count="${edits.length}">\n` +
    "The user double-clicked tags on your overlay and typed these directly on the model. They are the user's own values and notes: they take priority over what you drew.\n" +
    `${rows.join("\n")}\n</overlay_edits>`
  );
}
