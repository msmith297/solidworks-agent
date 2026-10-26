import { useState, type ReactNode } from "react";
import { Code } from "./Markdown";
import {
  Caret,
  IconBook,
  IconCamera,
  IconDocs,
  IconError,
  IconInfo,
  IconMacro,
  IconOk,
  IconRebuild,
  IconSearch,
  IconSelect,
  IconShield,
  IconTree,
  IconUndo,
} from "./SwIcons";
import type { ImageBlockParam, TextBlockParam, ToolResultBlockParam } from "../lib/types";
import type { RunState } from "../store";
import { webTitle } from "../lib/creator";
import { mcpTitle } from "../lib/mcpView";

export type ToolStatus = "streaming" | "queued" | "approval" | "running" | "ok" | "error" | "retried" | "stopped";

export interface ToolCall {
  id: string;
  name: string;
  input: any;
  /** Raw partial JSON while the call is still streaming. */
  partial?: string;
  result?: ToolResultBlockParam;
  /** A failed attempt the agent moved on from (retried or worked around) - shown quietly, not as a failure. */
  soft?: boolean;
  /** Failed attempts with the same title folded into this (successful) call. */
  retries?: number;
}

const ICONS: Record<string, ReactNode> = {
  execute_csharp: <IconMacro />,
  capture_views: <IconCamera />,
  get_feature_tree: <IconTree />,
  get_model_context: <IconInfo />,
  get_selection: <IconSelect />,
  search_sw_api: <IconSearch />,
  get_sw_api_type: <IconBook />,
  list_open_documents: <IconDocs />,
  undo: <IconUndo />,
  rebuild: <IconRebuild />,
};

const CODE_TOOLS = new Set(["execute_csharp", "save_helper", "test_helper"]);

export function toolTitle(name: string, input: any, partial?: string): string {
  const field = (key: string): string | undefined => input?.[key] ?? (partial ? partialField(partial, key) : undefined);
  switch (name) {
    case "execute_csharp":
      return field("title") || "Run C# in SolidWorks";
    case "web_search":
    case "web_fetch":
      return webTitle(name, input) ?? name;
    case "capture_views": {
      const v = input?.views as string[] | undefined;
      return `Capture ${v?.length ? v.join(", ") : "current"} view${v && v.length > 1 ? "s" : ""}`;
    }
    case "get_feature_tree":
      return "Read the feature tree";
    case "get_model_context":
      return "Read model context";
    case "get_selection":
      return "Read the selection";
    case "search_sw_api":
      return `Search the API for "${field("query") ?? "…"}"`;
    case "get_sw_api_type":
      return `Look up ${field("type_name") ?? "API type"}`;
    case "list_open_documents":
      return "List open documents";
    case "undo":
      return `Undo${input?.steps > 1 ? ` ${input.steps} steps` : ""}`;
    case "rebuild":
      return input?.force ? "Force rebuild" : "Rebuild";
    case "save_helper":
      return `Save helper ${field("name") ?? "..."}`;
    case "test_helper":
      return `Test helper ${field("name") ?? "..."}`;
    case "save_api_note":
      return `Save API note: ${field("topic") ?? "..."}`;
    case "describe_model_history":
      return "Read the full feature history";
    case "select_refs":
      return `Select ${(input?.ids as string[] | undefined)?.join(", ") ?? "references"}`;
    case "list_user_tools":
      return "List your tools";
    case "read_user_tool":
      return `Read tool "${field("name") ?? "…"}"`;
    case "save_user_tool":
      return `Save tool "${field("title") ?? field("name") ?? "…"}"`;
    case "edit_user_tool":
      return `Update tool "${field("name") ?? "…"}"`;
    case "design_library":
      return field("query") ? `Search the Design Library for "${field("query")}"` : "Browse the Design Library";
    case "add_to_design_library":
      return `Add to Design Library: ${field("folder") ?? "…"}`;
    default:
      return mcpTitle(name, input) ?? name;
  }
}

/** Pull a string field out of incomplete streaming JSON (e.g. the C# code while it's being written). */
export function partialField(json: string, key: string): string | undefined {
  const m = new RegExp(`"${key}"\\s*:\\s*"((?:[^"\\\\]|\\\\.)*)`).exec(json);
  if (!m) return undefined;
  let raw = m[1];
  if (raw.endsWith("\\")) raw = raw.slice(0, -1);
  try {
    return JSON.parse(`"${raw}"`);
  } catch {
    return raw.replace(/\\n/g, "\n").replace(/\\"/g, '"').replace(/\\\\/g, "\\");
  }
}

export function statusFor(call: ToolCall, run: RunState | undefined): ToolStatus {
  if (call.partial !== undefined) return "streaming";
  if (call.result) return call.result.is_error ? (call.soft ? "retried" : "error") : "ok";
  if (!run) return "stopped";
  if (run.approval?.toolUseId === call.id) return "approval";
  if (run.runningToolId === call.id) return "running";
  return "queued";
}

/** One agent action, drawn as a PropertyManager rollout. */
export function ToolCard({
  call,
  status,
  children,
  onImage,
}: {
  call: ToolCall;
  status: ToolStatus;
  children?: ReactNode;
  onImage: (src: string) => void;
}) {
  const [open, setOpen] = useState(false);
  // Code isn't shown while it's being written or run - the card stays folded with a working indicator; click to watch.
  const expanded = open || status === "approval";
  const working = status === "running" || status === "streaming";
  // Code cells, and the helpers the agent saves and tests (the code every later cell can call).
  const code = CODE_TOOLS.has(call.name) ? (call.input?.code ?? (call.partial ? partialField(call.partial, "code") : undefined)) : undefined;
  const texts = resultTexts(call.result);
  const images = resultImages(call.result);

  return (
    <div className={`sw-rollout ${status === "approval" ? "!border-accent" : status === "error" ? "!border-err/60" : ""}`}>
      <button type="button" onClick={() => setOpen((o) => !o)} className={`sw-rollout-head !font-normal ${expanded ? "" : "collapsed"}`} aria-expanded={expanded}>
        <Caret open={expanded} className="text-text-3" />
        <span className="flex shrink-0">{ICONS[call.name] ?? <IconMacro />}</span>
        <span className={`min-w-0 flex-1 truncate text-[12px] ${working ? "shimmer" : status === "retried" ? "text-text-3" : "text-text"}`}>
          {toolTitle(call.name, call.input, call.partial)}
          {working && <WorkingDots />}
        </span>
        {status === "ok" && call.retries ? (
          <span className="shrink-0 text-[10.5px] text-text-3" title={`Worked after ${call.retries} earlier attempt${call.retries > 1 ? "s" : ""}`}>
            {call.retries + 1} tries
          </span>
        ) : null}
        <StatusBadge status={status} />
      </button>

      {images.length > 0 && !expanded && (
        <div className="flex gap-1.5 px-2 py-1.5">
          {images.map((src, i) => (
            <Thumb key={i} src={src} onClick={() => onImage(src)} />
          ))}
        </div>
      )}

      {expanded && (
        <div className="space-y-2 px-2 py-2">
          {code !== undefined && <Code code={code} className="max-h-[320px]" />}
          {code === undefined && call.input && Object.keys(call.input).length > 0 && (
            <pre className="selectable sw-input max-h-[120px] overflow-auto px-2 py-1 font-mono text-[11px] text-text-2">{JSON.stringify(call.input, null, 1)}</pre>
          )}
          {children}
          {texts.length > 0 && (
            <pre
              className={`selectable sw-input max-h-[260px] overflow-auto whitespace-pre-wrap px-2 py-1.5 font-mono text-[11px] leading-[1.45] ${
                call.result?.is_error && !call.soft ? "!bg-err-soft text-err" : "text-text-2"
              }`}
            >
              {texts.join("\n")}
            </pre>
          )}
          {images.length > 0 && (
            <div className="flex flex-wrap gap-1.5">
              {images.map((src, i) => (
                <Thumb key={i} src={src} large onClick={() => onImage(src)} />
              ))}
            </div>
          )}
        </div>
      )}
    </div>
  );
}

/** Three dots that fill in turn: the step is being written or run. */
function WorkingDots() {
  return (
    <span className="working-dots" aria-label="working">
      <span>.</span>
      <span>.</span>
      <span>.</span>
    </span>
  );
}

function StatusBadge({ status }: { status: ToolStatus }) {
  switch (status) {
    case "streaming":
    case "running":
      return null;
    case "queued":
      return <span className="shrink-0 text-[10.5px] text-text-3">queued</span>;
    case "approval":
      return (
        <span className="flex shrink-0 items-center gap-1 text-[10.5px] font-semibold text-accent">
          <IconShield size={13} /> Approval
        </span>
      );
    case "ok":
      return <IconOk size={12} title="Done" />;
    case "retried":
      return (
        <span className="shrink-0 text-[10.5px] text-text-3" title="This attempt didn't work; the agent adjusted and carried on">
          retried
        </span>
      );
    case "error":
      return <IconError size={13} title="Failed" />;
    case "stopped":
      return <span className="shrink-0 text-[10.5px] text-text-3">stopped</span>;
  }
}

function Thumb({ src, onClick, large }: { src: string; onClick: () => void; large?: boolean }) {
  return (
    <button type="button" onClick={onClick} className="overflow-hidden rounded-[2px] border border-input-border bg-white shadow-[var(--shadow)] hover:border-accent" title="Open image">
      <img src={src} alt="SolidWorks view" className={large ? "max-h-[180px] max-w-full" : "h-[54px] w-auto"} />
    </button>
  );
}

function resultTexts(r?: ToolResultBlockParam): string[] {
  if (!r?.content) return [];
  if (typeof r.content === "string") return [r.content];
  return r.content.filter((b): b is TextBlockParam => b.type === "text").map((b) => b.text);
}

function resultImages(r?: ToolResultBlockParam): string[] {
  if (!r?.content || typeof r.content === "string") return [];
  return r.content
    .filter((b): b is ImageBlockParam => b.type === "image")
    .map((b) => (b.source.type === "base64" ? `data:${b.source.media_type};base64,${b.source.data}` : ""))
    .filter(Boolean);
}
