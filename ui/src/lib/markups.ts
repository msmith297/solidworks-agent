/**
 * Markups as things the user and the agent can both point at: every marked view gets a short name that stays unique in
 * the chat (M1, M2, ...), shown on its thumbnail and sent to the model as a text label right before the image, so "make
 * the hole in M2 deeper" means the same to both. Pure, so the panel checks can run it (checks/lib.check.mjs).
 */

/** The attachment name of markup number n ("Markup M3"); MARKUP_NAME is its prefix. */
export const markupName = (n: number) => `Markup M${n}`;

/** The markup number in an attachment name ("Markup M3" -> 3), or null. */
export function markupNumber(name: string): number | null {
  const m = /^Markup M(\d+)$/.exec(name);
  return m ? Number(m[1]) : null;
}

/** The text block sent right before markup n's image. */
export const markupLabel = (n: number) => `[M${n}] Markup M${n}: the user's drawing over their view`;

/** The markup number of a label block, or null for any other text. */
export function labelNumber(text: string): number | null {
  const m = /^\[M(\d+)\] Markup M\d+: /.exec(text);
  return m ? Number(m[1]) : null;
}

type Block = { type: string; text?: string };
type Msg = { role: string; content: string | Block[] };

/** The next free markup number in a chat: after every label already sent and every markup waiting in the composer. */
export function nextMarkupNumber(messages: Msg[], pendingNames: string[]): number {
  let max = 0;
  for (const m of messages) {
    if (m.role !== "user" || typeof m.content === "string") continue;
    for (const b of m.content) if (b.type === "text" && b.text) max = Math.max(max, labelNumber(b.text) ?? 0);
  }
  for (const n of pendingNames) max = Math.max(max, markupNumber(n) ?? 0);
  return max + 1;
}

/** The draft with a mention of markup n added at the end (one space before it, unless it's already there). */
export function mentionMarkup(draft: string, n: number): string {
  const tag = `M${n}`;
  if (new RegExp(`\\b${tag}\\b`).test(draft)) return draft;
  return draft.length === 0 || /\s$/.test(draft) ? `${draft}${tag} ` : `${draft} ${tag} `;
}
