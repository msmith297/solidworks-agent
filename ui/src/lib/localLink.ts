import type { MessageParam } from "./types";

/**
 * The panel's side of the Claude MCP / Codex MCP plugins (docs/plugins/local-mcp.md 5.5): a message from Claude Desktop or
 * Codex arrives as a wake with a ticket, and when the turn it started ends the panel reports the agent's final reply for
 * that ticket (localLink.answer). Pure, so it is easy to check (checks/lib.check.mjs).
 */

/** The answer sent back is cut to this many characters. */
export const LINK_ANSWER_CAP = 50_000;

/** What the client gets when the turn ended without any text. */
export const NO_WRITTEN_REPLY = "(The agent finished without a written reply.)";

/** How a turn ended, as the ticket's state: done, stopped (the user pressed Stop) or failed. */
export type LinkAnswerState = "done" | "stopped" | "failed";

export function linkState(outcome: string): LinkAnswerState {
  return outcome === "aborted" ? "stopped" : outcome === "error" ? "failed" : "done";
}

/**
 * The agent's reply for a ticket: the text of the last assistant message after `from` (the message that carried the
 * wake) that has any text - a turn that ended on tool calls alone answers with its last words before them. Cut at
 * LINK_ANSWER_CAP; NO_WRITTEN_REPLY when the agent wrote nothing.
 */
export function linkAnswerText(messages: MessageParam[], from: number): string {
  for (let i = messages.length - 1; i > from && i >= 0; i--) {
    const m = messages[i];
    if (m.role !== "assistant") continue;
    const text = (typeof m.content === "string" ? m.content : m.content.filter((b) => b.type === "text").map((b) => (b as { text: string }).text).join("\n\n")).trim();
    if (!text) continue;
    return text.length > LINK_ANSWER_CAP ? `${text.slice(0, LINK_ANSWER_CAP)}\n(cut)` : text;
  }
  return NO_WRITTEN_REPLY;
}

/**
 * The message itself from a link wake's text (the add-in's preamble, then `<message>...</message>`), with the escaped
 * closing tags (`<\/message`, `<\/system_event`) shown as written. Null when the text has no message block.
 */
export function parseLinkMessage(text: string): string | null {
  const start = text.indexOf("<message>\n");
  const end = text.lastIndexOf("\n</message>");
  if (start < 0 || end < start) return null;
  return text.slice(start + "<message>\n".length, end).replace(/<\\\//g, "</");
}
