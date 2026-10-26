import type { ApprovalMode, HookEvent, SessionInfo } from "./types";

/**
 * Plugin hooks as the panel sees them (docs/plugins/design.md 4.5). The add-in runs the hook programs (hooks.fire);
 * the panel decides when to fire the turn-level events and where their output goes in the conversation:
 *
 * - UserPromptSubmit: before a prompt the user typed (or queued, or a ribbon tool run) is sent - never for a wake or a
 *   Stop-hook continuation (agent-bridge resets its auto-resume counter on every UserPromptSubmit).
 * - PreToolUse / PostToolUse: around every agent tool, from runAgent's executeTool (before validation and approval).
 * - Stop: when a turn ends normally (end_turn / stop_sequence); a block continues the turn.
 *
 * What hooks add for the model goes in its own text blocks (HOOK_TAG), never inside a tool_result: context editing
 * clears old tool results, and with them anything a plugin put there. Pure, so it is easy to check (checks/lib.check.mjs).
 */

/** Context a hook added for the model; the chat shows it as a card, not as the user's words. */
export const HOOK_TAG = "<hook_context";
/** A turn the user didn't type: a plugin woke the agent, or a Stop hook continued the turn. */
export const EVENT_TAG = "<system_event";

/** tool_response sent to PostToolUse hooks is cut to about this many characters. */
export const HOOK_RESPONSE_CAP = 100_000;

const escapeAttr = (v: string) => v.replace(/[&"<>]/g, (c) => ({ "&": "&amp;", '"': "&quot;", "<": "&lt;", ">": "&gt;" })[c]!);

/** A hook-context text block: `<hook_context event="PostToolUse" tool="x">...</hook_context>`. */
export function hookContextText(event: HookEvent, texts: string[], tool?: string): string {
  return `${HOOK_TAG} event="${event}"${tool ? ` tool="${escapeAttr(tool)}"` : ""}>\n${texts.join("\n\n")}\n</hook_context>`;
}

/**
 * A system-event text block: `<system_event source="agent-bridge" kind="wake">...</system_event>`. A message from Claude
 * Desktop or Codex also names its sender and ticket: `from="Claude Desktop" ticket="u3d_..."`.
 */
export function systemEventText(kind: "wake" | "stop", source: string, text: string, attrs?: { from?: string | null; ticket?: string | null }): string {
  const extra = (attrs?.from ? ` from="${escapeAttr(attrs.from)}"` : "") + (attrs?.ticket ? ` ticket="${escapeAttr(attrs.ticket)}"` : "");
  return `${EVENT_TAG} kind="${kind}" source="${escapeAttr(source)}"${extra}>\n${text}\n</system_event>`;
}

/** The parts of a hook_context / system_event block, for the chat's cards. */
export function parseTagged(raw: string): { tag: "hook" | "event"; attrs: Record<string, string>; body: string } | null {
  const tag = raw.startsWith(HOOK_TAG) ? "hook" : raw.startsWith(EVENT_TAG) ? "event" : null;
  if (!tag) return null;
  const head = /^<\w+([^>]*)>/.exec(raw)?.[1] ?? "";
  const attrs: Record<string, string> = {};
  for (const m of head.matchAll(/(\w+)="([^"]*)"/g)) attrs[m[1]] = m[2].replace(/&quot;/g, '"').replace(/&lt;/g, "<").replace(/&gt;/g, ">").replace(/&amp;/g, "&");
  const body = raw.replace(/^<[^>]*>\n?/, "").replace(/\n?<\/(hook_context|system_event)>\s*$/, "");
  return { tag, attrs, body };
}

/**
 * Whether a hook matcher matches a tool name, exactly as the add-in's HookEvents.Matches (and Claude Code): "" or "*"
 * match everything; letters, digits, _ and | are exact names ("Write|Edit"); anything else is a regular expression
 * searched in the name ("mcp__plugin_x_.*", "mcp__.*__send"). An invalid one matches nothing. The two must agree:
 * the panel skips hooks.fire when nothing matches.
 */
export function matcherMatches(matcher: string | null | undefined, toolName: string): boolean {
  const m = matcher ?? "";
  if (!m || m === "*") return true;
  if (/^[A-Za-z0-9_|]+$/.test(m)) return m.split("|").includes(toolName);
  try {
    return new RegExp(m).test(toolName);
  } catch {
    return false;
  }
}

/** Whether firing an event can do anything: some handler exists (and, for tool events, matches the tool). */
export function hasHook(info: Pick<SessionInfo, "hookEvents"> | null | undefined, event: HookEvent, toolName?: string): boolean {
  const matchers = info?.hookEvents?.[event];
  if (!matchers || matchers.length === 0) return false;
  return toolName === undefined || matchers.some((m) => matcherMatches(m, toolName));
}

type Block = { type: string; text?: string };

/** What PostToolUse hooks get as tool_response: text blocks only (images as "[image]"), cut to HOOK_RESPONSE_CAP. */
export function hookToolResponse(content: string | Block[] | undefined, isError: boolean): { content: { type: "text"; text: string }[]; isError: boolean } {
  const blocks: Block[] = typeof content === "string" ? [{ type: "text", text: content }] : content ?? [];
  let budget = HOOK_RESPONSE_CAP;
  const out: { type: "text"; text: string }[] = [];
  for (const b of blocks) {
    if (budget <= 0) break;
    const text = b.type === "text" ? b.text ?? "" : b.type === "image" ? "[image]" : `[${b.type}]`;
    const cut = text.length > budget ? `${text.slice(0, budget)}\n[cut]` : text;
    budget -= text.length;
    out.push({ type: "text", text: cut });
  }
  return { content: out, isError };
}

/** Automatic turns (wakes and Stop-hook continuations) in the last hour, and whether one more is allowed. */
export const AUTO_WINDOW_MS = 60 * 60 * 1000;

export function autoTurnAllowed(times: number[], now: number, perHour: number): { allowed: boolean; recent: number[] } {
  const recent = times.filter((t) => now - t < AUTO_WINDOW_MS);
  const limit = Number.isFinite(perHour) && perHour >= 0 ? Math.floor(perHour) : 20;
  return { allowed: recent.length < limit, recent };
}

/**
 * Wakes held while a chat was busy go out when its turn ended by itself; after Stop or an error they wait for the user's
 * next message (a wake must not restart what the user just stopped, nor retry a failed request at once).
 */
export function wakesMayFollow(outcome: string): boolean {
  return outcome !== "aborted" && outcome !== "error";
}

/**
 * The approval mode of a turn a plugin started (a wake, or a Stop-hook continuation of one): a message from another agent
 * is not the user's OK, so write tools always ask - whatever the setting, a tool run or "Always allow" said - unless the
 * user chose read-only.
 */
export function wakeApproval(mode: ApprovalMode): ApprovalMode {
  return mode === "read_only" ? "read_only" : "ask";
}

/** A message header in an agent-bridge wake from the user's own console: `#3751 from Martin Smith [Console] (1s ago):`. */
const CONSOLE_HEADER = /^#\d+ from [^\n[\]]+ \[Console\](?: in chat #\d+ .*)? \([^)\n]*\)(?: \[reply to #\d+\])?:$/;

/**
 * An agent-bridge wake whose messages all came from the user's operator console: the user at another screen, so the
 * turn may follow their approval setting. Every message's header must name the console (agent-bridge's formatMessages:
 * messages split by "---", each starting with its header), so a message from another agent - even one whose text
 * imitates a console header - makes the whole wake untrusted. Anything that doesn't parse is untrusted too.
 */
export function isConsoleWake(w: { text: string; plugin?: string | null }): boolean {
  if (!/^agent-bridge/.test(w.plugin ?? "") || !w.text.startsWith("agent-bridge: New agent-bridge messages")) return false;
  const start = w.text.indexOf("\n\n#");
  if (start < 0) return false;
  const chunks = w.text.slice(start + 2).split("\n\n---\n\n");
  return chunks.every((c) => CONSOLE_HEADER.test(c.split("\n", 1)[0]));
}
