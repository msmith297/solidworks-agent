import type { ToolCall } from "./ToolCard";
import type { ContentBlockParam, StoredMessage, ToolResultBlockParam } from "../lib/types";
import { webResultSummary } from "../lib/creator";
import { rememberedChip, type RememberedChip } from "../lib/memory";
import { preferenceChip } from "../lib/preferences";
import { displayParts } from "../agent/providers";

/** What one assistant message shows in the chat (ChatView folds them into turns). */
export type AssistantItem =
  | { kind: "progress"; key: string; text: string }
  | { kind: "text"; key: string; text: string }
  | { kind: "tool"; key: string; call: ToolCall }
  | { kind: "question"; key: string; call: ToolCall }
  | { kind: "memory"; key: string; chip: RememberedChip };

const QUESTION_TOOLS = new Set(["ask_user", "ask_form", "ask_selection", "ask_secret"]);

export function resultText(r: ToolResultBlockParam | undefined): string {
  if (!r) return "";
  if (typeof r.content === "string") return r.content;
  return (r.content ?? []).map((c) => (c.type === "text" ? c.text : "")).join("\n");
}

/**
 * The items of the assistant message at `i`, in its provider's display order (lib: providers displayParts): a Claude
 * message gives one item per block as it always did; an OpenAI message adds its reasoning summaries as progress notes
 * and its web searches as step cards where they happened.
 */
export function assistantItems(m: StoredMessage, i: number, results: Map<string, ToolResultBlockParam>, showProgress: boolean): AssistantItem[] {
  if (m.role !== "assistant" || typeof m.content === "string") return [];
  const blocks = m.content as ContentBlockParam[];
  const items: AssistantItem[] = [];
  displayParts(m).forEach((part, n) => {
    if (part.kind === "progress") {
      if (showProgress && part.text.trim()) items.push({ kind: "progress", key: `m${i}p${n}`, text: part.text.trim() });
      return;
    }
    if (part.kind === "web") {
      const key = `m${i}p${n}`;
      const result: ToolResultBlockParam = { type: "tool_result", tool_use_id: key, content: part.result ?? part.detail ?? part.title, ...(part.isError ? { is_error: true } : {}) };
      items.push({ kind: "tool", key, call: { id: key, name: part.call?.name ?? part.title, input: part.call?.input ?? {}, result } });
      return;
    }
    const j = part.index;
    const b = blocks[j];
    if (!b) return;
    const key = `m${i}b${j}`;
    if (b.type === "thinking" && b.thinking.trim() && showProgress) items.push({ kind: "progress", key, text: b.thinking.trim() });
    else if (b.type === "text" && b.text.trim()) items.push({ kind: "text", key, text: b.text });
    else if (b.type === "tool_use") {
      const call: ToolCall = { id: b.id, name: b.name, input: b.input, result: results.get(b.id) };
      if (call.name === "offer_restart") return; // shown as the button under the reply
      items.push(QUESTION_TOOLS.has(call.name) ? { kind: "question", key, call } : { kind: "tool", key, call });
      // a saved standing instruction: a chip with Undo after the call
      const chip =
        call.name === "remember" ? rememberedChip(resultText(call.result), !!call.result?.is_error)
        : call.name === "save_preference" ? preferenceChip(resultText(call.result), !!call.result?.is_error)
        : null;
      if (chip) items.push({ kind: "memory", key: `${key}mem`, chip });
    } else if (b.type === "server_tool_use") {
      // Web research on Claude runs on Anthropic's side: its result is in this same message.
      const sum = webResultSummary(blocks.find((r) => "tool_use_id" in r && r.type !== "tool_result" && r.tool_use_id === b.id));
      const result: ToolResultBlockParam | undefined = sum ? { type: "tool_result", tool_use_id: b.id, content: sum.text, ...(sum.isError ? { is_error: true } : {}) } : undefined;
      items.push({ kind: "tool", key, call: { id: b.id, name: b.name, input: b.input, result } });
    }
  });
  return items;
}
