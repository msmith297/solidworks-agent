import type { ToolCall } from "../components/ToolCard";
import { isBridgeTool, mcpParts } from "../lib/mcpView";

/**
 * How a finished turn is summarized. Reads, API lookups, captures, checks, scratch tests and failed attempts are how
 * the agent got there; what changed the user's model, what it saved and what it asked are what happened. The
 * folded turn shows a one-line breakdown of the work and only those outcomes - capped, so a long build or a tool
 * creation with dozens of steps still folds to a few lines.
 */

/** Tools that never change anything the user cares about seeing afterwards. */
const MINOR_TOOLS = new Set([
  "get_model_context",
  "get_feature_tree",
  "get_selection",
  "list_open_documents",
  "search_sw_api",
  "get_sw_api_type",
  // web research and the plugin creator's checks (Anthropic's server tools show as steps too)
  "web_search",
  "web_fetch",
  "check_prerequisites",
  "capture_views",
  "describe_model_history",
  "list_user_tools",
  "read_user_tool",
  "design_library",
  "select_refs",
  "rebuild",
  "undo",
  "test_tool",
  "offer_restart",
  "remember", // shown as its own "Remembered" chip with Undo
  "list_memory",
  "compare_fork",
  "read_bridge_file", // a peer's attachment, read-only
]);

const LOOKUPS = new Set(["search_sw_api", "get_sw_api_type", "read_user_tool", "list_user_tools", "design_library", "list_memory", "compare_fork"]);
const OUTCOMES = new Set(["save_user_tool", "edit_user_tool", "add_to_design_library", "remember", "fork_tool"]);

/** C# cells that only look, test or tidy up (for calls without a `kind`, or labelled "change" but clearly scaffolding). */
const CHECK_TITLE = /^(check|verify|validate|inspect|list|find|get|read|measure|look|query|probe|debug|diagnose|examine|confirm|test|count|show|print|dump|report|search|compare|re-?check|investigate|locate|identify|explore|review|analy[sz]e|evaluate|determine)\b/i;
const SCAFFOLD_TITLE = /\b(scratch|test(ing)?|probe|debug|trial|experiment|harness|sandbox|temp(orary)?)\b|^(clean|clear|reset|restore|undo|revert|remove (the )?(test|scratch|probe))/i;

/** Changes to the model shown when a turn is folded; the rest are one click away. */
const MAX_CHANGES = 3;

function isCheck(call: ToolCall): boolean {
  const kind = String(call.input?.kind ?? "").toLowerCase();
  const title = String(call.input?.title ?? "").trim();
  if (SCAFFOLD_TITLE.test(title)) return true;
  if (kind === "check") return true;
  if (kind === "change") return false;
  return CHECK_TITLE.test(title);
}

/** The agent asking the user (a question, a form, a selection): always shown, never counted as work. */
const ASKS = new Set(["ask_user", "ask_form", "ask_selection", "ask_secret"]);

export function isHighlight(call: ToolCall): boolean {
  if (ASKS.has(call.name)) return true;
  if (!call.result || call.result.is_error) return false;
  if (MINOR_TOOLS.has(call.name)) return false;
  // agent-bridge housekeeping (messages, inbox, listening): counted in the summary line, not shown as cards.
  if (isBridgeTool(call.name)) return false;
  if (call.name === "execute_csharp") return !isCheck(call);
  return true; // save_user_tool, edit_user_tool, add_to_design_library, and anything new
}

/**
 * The outcomes of a finished turn, in order: questions, saves, and the last few changes to the model (a step retried
 * under the same title counts once). `more` is how many further changes were folded away.
 */
export function highlights(calls: ToolCall[]): ToolCall[] & { more?: number } {
  const keep = calls.filter(isHighlight);
  const lastByTitle = new Map<string, number>();
  keep.forEach((c, i) => {
    if (c.name === "execute_csharp" && c.input?.title) lastByTitle.set(String(c.input.title).trim().toLowerCase(), i);
  });
  const unique = keep.filter((c, i) => c.name !== "execute_csharp" || !c.input?.title || lastByTitle.get(String(c.input.title).trim().toLowerCase()) === i);
  const changes = unique.filter((c) => c.name === "execute_csharp");
  const shownChanges = new Set(changes.slice(-MAX_CHANGES));
  const result = unique.filter((c) => c.name !== "execute_csharp" || shownChanges.has(c)) as ToolCall[] & { more?: number };
  result.more = Math.max(0, changes.length - MAX_CHANGES);
  return result;
}

/** "9 lookups · 6 checks · 3 changes · 2 messages · 1 retry" - what the folded steps were. */
export function breakdown(calls: ToolCall[]): string {
  let lookups = 0, checks = 0, changes = 0, retries = 0, saves = 0, messages = 0;
  for (const c of calls) {
    if (ASKS.has(c.name)) continue;
    if (isBridgeTool(c.name)) {
      // Messages sent count; checking the inbox, listening and the like are housekeeping, not work to report.
      if (mcpParts(c.name)?.tool === "send_message" && !c.result?.is_error) messages++;
      continue;
    }
    if (c.result?.is_error) retries++;
    else if (LOOKUPS.has(c.name)) lookups++;
    else if (OUTCOMES.has(c.name)) saves++;
    else if (c.name === "execute_csharp") (isCheck(c) ? checks++ : changes++);
    else checks++;
  }
  const part = (n: number, one: string, many: string) => (n > 0 ? `${n} ${n === 1 ? one : many}` : null);
  return [
    part(changes, "change", "changes"),
    part(checks, "check", "checks"),
    part(lookups, "lookup", "lookups"),
    part(saves, "save", "saves"),
    part(messages, "message", "messages"),
    part(retries, "retry", "retries"),
  ]
    .filter(Boolean)
    .join(" · ");
}
