import { Fragment, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { ArrowDown, X } from "lucide-react";
import { IconCancel, IconCopy, IconDocs, IconEdit, IconError, IconEye, IconMic, IconModelFile, IconOk, IconPlugin, IconQuestion, IconResend, IconShield, IconWatch, ToolIcon } from "./SwIcons";
import { Code, Markdown } from "./Markdown";
import { BrandLogo } from "./Brand";
import { FormCard } from "./FormCard";
import { QuestionTimer } from "./QuestionTimer";
import { SelectionCard } from "./SelectionCard";
import { SecretCard } from "./SecretCard";
import { Answered } from "./Answered";
import { RestartCard } from "./Recovery";
import { ToolFormCard, ToolFormPane } from "./ToolFormCard";
import { MemoryChip } from "./MemoryChip";
import type { RememberedChip } from "../lib/memory";
import { useToolForm } from "../lib/toolForm";
import { svgDataUrl } from "../lib/toolIcons";
import { breakLabel, isBreak } from "../lib/sessions";
import { Caret } from "./SwIcons";
import { ToolCard, statusFor, toolTitle, type ToolCall } from "./ToolCard";
import { assistantItems } from "./chatItems";
import { breakdown, highlights as pickHighlights } from "../agent/workSummary";
import { EVENT_TAG, HOOK_TAG, MODEL_TAG, VOICE_TAG, WATCH_TAG, cellIdsAfter, currentThread, isPrompt, promptText, useStore, writesAfter, type RunState } from "../store";
import { parseTagged } from "../lib/hooks";
import { bridgeMessages, reminderSenders } from "../lib/mcpView";
import { labelNumber } from "../lib/markups";
import { parseLinkMessage } from "../lib/localLink";
import { ask } from "./Dialog";
import { rpc } from "../lib/bridge";
import { modelOption } from "../lib/models";
import type { ContentBlockParam, ImageBlockParam, MessageParam, Thread, ToolResultBlockParam, TurnMeta } from "../lib/types";
import type { LiveBlock, SubAgentProgress } from "../agent/runAgent";

/**
 * A 3D model or "Watch me" log attached to a prompt (its text block and the images that follow it), what a plugin hook
 * added for the agent ("hook"), or a turn a plugin started ("event": a wake, a Stop-hook continuation).
 */
interface Extra {
  kind: "model" | "watch" | "pdf" | "voice" | "hook" | "event";
  title: string;
  body: string;
  images: string[];
  /** A plugin's housekeeping (e.g. agent-bridge's "you haven't replied" reminder): one muted line, not a card. */
  quiet?: boolean;
}

type Item =
  | {
      kind: "prompt";
      key: string;
      index: number;
      text: string;
      images: string[];
      /** The markup name of each image ("M2"), or null. */
      imageLabels: (string | null)[];
      refs: { id: string; text: string }[];
      view: string | null;
      extras: Extra[];
    }
  | { kind: "progress"; key: string; text: string }
  | { kind: "text"; key: string; text: string; streaming?: boolean }
  | { kind: "tool"; key: string; call: ToolCall }
  | { kind: "restart"; key: string; id: string; reason?: string; title?: string }
  | { kind: "divider"; key: string; label: string }
  | { kind: "question"; key: string; call: ToolCall }
  | { kind: "footer"; key: string; turn: TurnMeta; isLast: boolean }
  | { kind: "work"; key: string; items: Item[]; highlights: Item[]; steps: number; durationMs?: number; breakdown: string; more: number }
  | { kind: "thinking"; key: string }
  | { kind: "hook"; key: string; extra: Extra }
  | { kind: "memory"; key: string; chip: RememberedChip };

export function ChatView() {
  const thread = useStore(currentThread);
  const run = useStore((s) => (thread ? s.runs[thread.id] : undefined));
  const showProgress = useStore((s) => s.settings.showProgress);
  const [lightbox, setLightbox] = useState<string | null>(null);
  const scroller = useRef<HTMLDivElement>(null);
  const [pinned, setPinned] = useState(true);
  const toolForm = useToolForm((s) => !!s.form);

  const items = useMemo(() => (thread ? buildItems(thread, run, showProgress) : []), [thread, run, showProgress]);

  // Stick to the bottom while new content streams in, unless the user scrolled up.
  useLayoutEffect(() => {
    const el = scroller.current;
    if (el && pinned) el.scrollTop = el.scrollHeight;
  }, [items, pinned]);

  useEffect(() => setPinned(true), [thread?.id]);

  if (!thread) return toolForm ? <ToolFormPane /> : <EmptyState />;

  return (
    <div className="relative min-h-0 flex-1">
      <div
        ref={scroller}
        className="h-full overflow-y-auto px-3 pb-4 pt-3"
        onScroll={(e) => {
          const el = e.currentTarget;
          setPinned(el.scrollHeight - el.scrollTop - el.clientHeight < 40);
        }}
      >
        <div className="zoom-content mx-auto flex max-w-[780px] flex-col gap-2">
          {items.map((item) => (
            <Fragment key={item.key}>{renderItem(item, thread, run, setLightbox)}</Fragment>
          ))}
          <ToolFormCard />
        </div>
      </div>
      {!pinned && (
        <button
          type="button"
          onClick={() => setPinned(true)}
          className="sw-btn absolute bottom-3 left-1/2 !h-6 !w-6 -translate-x-1/2 !rounded-full !p-0"
          title="Scroll to latest"
        >
          <ArrowDown size={14} />
        </button>
      )}
      {lightbox && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/70 p-4" onClick={() => setLightbox(null)}>
          <img src={lightbox} alt="" className="max-h-full max-w-full rounded shadow-2xl" />
          <button className="absolute right-3 top-3 text-white/80 hover:text-white" title="Close">
            <X size={20} />
          </button>
        </div>
      )}
    </div>
  );
}

function renderItem(item: Item, thread: Thread, run: RunState | undefined, onImage: (s: string) => void) {
  switch (item.kind) {
    case "prompt":
      return <PromptBubble item={item} thread={thread} running={!!run} onImage={onImage} />;
    case "progress":
      return (
        <div className="flex gap-2 pl-1 text-[12px] italic leading-snug text-text-3">
          <span className="mt-[7px] h-1 w-1 shrink-0 rounded-full bg-text-3" />
          <span className="selectable">{item.text}</span>
        </div>
      );
    case "thinking":
      return <ThinkingIndicator run={run} />;
    case "text":
      return (
        <div className="selectable px-0.5 text-text">
          <Markdown text={item.text} streaming={item.streaming} />
        </div>
      );
    case "tool":
      return (
        <>
          <ToolCard call={item.call} status={statusFor(item.call, run)} onImage={onImage}>
            {run?.approval?.toolUseId === item.call.id && <ApprovalCard threadId={thread.id} />}
          </ToolCard>
          {run?.subAgent?.toolUseId === item.call.id && <SubAgentSteps progress={run.subAgent} />}
        </>
      );
    case "question":
      return item.call.name === "ask_form" ? <FormCard call={item.call} threadId={thread.id} run={run} />
        : item.call.name === "ask_selection" ? <SelectionCard call={item.call} threadId={thread.id} run={run} />
        : item.call.name === "ask_secret" ? <SecretCard call={item.call} threadId={thread.id} run={run} />
        : <QuestionCard call={item.call} threadId={thread.id} run={run} />;
    case "footer":
      return <TurnFooter turn={item.turn} isLast={item.isLast} running={!!run} />;
    case "divider":
      return (
        <div className="my-1 flex items-center gap-2 text-[10.5px] text-text-3" title="Messages above are from an earlier session">
          <div className="h-px flex-1 bg-line-strong" />
          <span className="shrink-0">{item.label}</span>
          <div className="h-px flex-1 bg-line-strong" />
        </div>
      );
    case "restart":
      return <RestartCard reason={item.reason} title={item.title} onDismiss={() => useStore.getState().dismissRestart(thread.id, item.id)} />;
    case "work":
      return <WorkGroup item={item} thread={thread} run={run} onImage={onImage} />;
    case "hook":
      return <HookCard extra={item.extra} />;
    case "memory":
      return <MemoryChip chip={item.chip} threadId={thread.id} undone={!!thread.memoryUndone?.includes(item.chip.id)} />;
  }
}

/**
 * A finished turn's working steps, folded: a header ("18 steps · 2m 53s") over just the steps that changed
 * something. Expanding shows everything - every check, search, failed attempt and the narration between them.
 */
function WorkGroup({
  item,
  thread,
  run,
  onImage,
}: {
  item: Extract<Item, { kind: "work" }>;
  thread: Thread;
  run: RunState | undefined;
  onImage: (s: string) => void;
}) {
  const [open, setOpen] = useState(false);

  return (
    <div className="flex flex-col gap-1.5">
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        aria-expanded={open}
        className="flex items-center gap-1.5 self-start rounded-[3px] px-1 py-0.5 text-[11.5px] text-text-3 hover:bg-accent-soft hover:text-text"
        title={open ? "Show only the outcomes" : "Show every step: lookups, checks, tests and retries"}
      >
        <Caret open={open} />
        <span>
          {item.steps} step{item.steps === 1 ? "" : "s"}
          {item.durationMs !== undefined && ` · ${formatDuration(item.durationMs)}`}
          {item.breakdown && <span className="text-text-3/80"> · {item.breakdown}</span>}
        </span>
      </button>
      {(open ? item.items : item.highlights).map((sub) => (
        <Fragment key={sub.key}>{renderItem(sub, thread, run, onImage)}</Fragment>
      ))}
      {!open && item.more > 0 && (
        <button type="button" onClick={() => setOpen(true)} className="self-start pl-1 text-[11px] text-text-3 hover:text-text hover:underline">
          + {item.more} earlier change{item.more === 1 ? "" : "s"}
        </button>
      )}
    </div>
  );
}

/**
 * Fold a finished turn: everything up to the last tool call is "work"; the text after it is the answer and stays.
 * Turns without tool calls are left as they are.
 */
function foldTurn(segment: Item[], turn: TurnMeta | undefined, key: string): Item[] {
  let last = -1;
  segment.forEach((it, i) => {
    if (it.kind === "tool" || it.kind === "question") last = i;
  });
  if (last < 0) return segment;
  // "Remembered" chips stay in view: they carry Undo.
  const chips = segment.slice(0, last + 1).filter((it) => it.kind === "memory");
  const work = segment.slice(0, last + 1).filter((it) => it.kind !== "memory");
  const calls = work.filter((it): it is Extract<Item, { kind: "tool" | "question" }> => it.kind === "tool" || it.kind === "question");
  const picked = pickHighlights(calls.map((c) => c.call));
  const keep = new Set(picked.map((c) => c.id));
  const group: Item = {
    kind: "work",
    key,
    items: work,
    highlights: calls.filter((c) => keep.has(c.call.id)),
    steps: calls.filter((c) => c.kind === "tool").length,
    durationMs: turn?.durationMs,
    breakdown: breakdown(calls.map((c) => c.call)),
    more: picked.more ?? 0,
  };
  return [group, ...chips, ...segment.slice(last + 1)];
}

/**
 * Trying something, seeing it fail and adjusting is normal agent work, not a failure. A failed call that the agent
 * moved on from (anything after it in the turn, or the turn is still running) is shown quietly as "retried"; when
 * the next attempt with the same title worked, the failed one is folded into it. Red stays only for a turn that
 * ended on a failure.
 */
function quietFailures(segment: Item[], finished: boolean): Item[] {
  const tools = segment.filter((it): it is Extract<Item, { kind: "tool" }> => it.kind === "tool");
  const drop = new Set<string>();
  const retries = new Map<string, number>();
  tools.forEach((it, i) => {
    if (!it.call.result?.is_error) return;
    const later = tools.slice(i + 1);
    if (later.length === 0 && finished) return; // the turn ended here: a real failure
    const title = toolTitle(it.call.name, it.call.input);
    const retry = later.find((l) => toolTitle(l.call.name, l.call.input) === title);
    if (retry && retry.call.result && !retry.call.result.is_error) {
      drop.add(it.key);
      retries.set(retry.key, (retries.get(retry.key) ?? 0) + 1 + (retries.get(it.key) ?? 0));
    }
  });
  return segment
    .filter((it) => !drop.has(it.key))
    .map((it) => {
      if (it.kind !== "tool") return it;
      const i = tools.findIndex((t) => t.key === it.key);
      const soft = !!it.call.result?.is_error && (i < tools.length - 1 || !finished);
      const n = retries.get(it.key);
      return soft || n ? { ...it, call: { ...it.call, soft, retries: n } } : it;
    });
}

/** "Thinking..." with how long the model has been at it, so a long think never looks like a hang. */
function ThinkingIndicator({ run }: { run: RunState | undefined }) {
  const [, tick] = useState(0);
  useEffect(() => {
    const t = setInterval(() => tick((n) => n + 1), 1000);
    return () => clearInterval(t);
  }, []);
  const seconds = run?.requestStartedAt ? Math.floor((Date.now() - run.requestStartedAt) / 1000) : 0;
  const time = seconds >= 60 ? `${Math.floor(seconds / 60)}m ${String(seconds % 60).padStart(2, "0")}s` : `${seconds}s`;
  return (
    <div className="flex flex-col gap-0.5 pl-1 text-[12px]">
      <div>
        <span className="shimmer">{seconds >= 45 ? "Thinking it through" : "Thinking"}</span>
        <span className="working-dots text-text-3">
          <span>.</span>
          <span>.</span>
          <span>.</span>
        </span>
        {seconds >= 5 && <span className="text-text-3"> {time}</span>}
      </div>
      {run?.status && <div className="text-[11px] text-text-3">{run.status}</div>}
      {!run?.status && seconds >= 90 && (
        <div className="text-[11px] text-text-3">
          Still connected and working - big requests can take a few minutes to plan. Lower the effort below for quicker starts, or press Stop and break the request into steps.
        </div>
      )}
    </div>
  );
}

/** The newest successful save/edit of a tool in the chat: the restart offer goes under it, once. */
/**
 * The Save and restart button under the last reply: when the agent asked for it (offer_restart), or - as a safety net -
 * when the last turn saved a tool (RestartCard then shows only if the ribbon really is out of date).
 */
function restartOffer(thread: Thread): { id: string; reason?: string; title?: string } | null {
  let savedTool: string | undefined;
  let savedId = "";
  for (let i = thread.messages.length - 1; i >= 0; i--) {
    const m = thread.messages[i];
    if (isPrompt(m)) break;
    if (m.role !== "assistant" || typeof m.content === "string") continue;
    for (const b of m.content) {
      if (b.type !== "tool_use") continue;
      const input = b.input as { reason?: string; kind?: string; title?: string };
      if (b.name === "offer_restart") return { id: b.id, reason: input?.reason };
      if ((b.name === "save_user_tool" || b.name === "edit_user_tool" || b.name === "fork_tool") && input?.kind !== "workflow" && savedTool === undefined) {
        savedTool = input?.title ?? "";
        savedId = b.id;
      }
    }
  }
  return savedTool !== undefined ? { id: savedId, title: savedTool || undefined } : null;
}

/** Flatten API messages (+ the live stream) into display items. */
function buildItems(thread: Thread, run: RunState | undefined, showProgress: boolean): Item[] {
  const items: Item[] = [];
  const results = new Map<string, ToolResultBlockParam>();
  thread.messages.forEach((m) => {
    if (m.role === "user" && typeof m.content !== "string") {
      for (const b of m.content) if (b.type === "tool_result") results.set(b.tool_use_id, b);
    }
  });

  const footerAfter = new Map<number, TurnMeta>();
  thread.turns.forEach((t) => footerAfter.set(t.msgIndex, t)); // last turn per prompt wins
  const promptIndexes = thread.messages.map((m, i) => (isPrompt(m) ? i : -1)).filter((i) => i >= 0);
  const lastPrompt = promptIndexes[promptIndexes.length - 1] ?? -1;

  // Items of the current turn are collected in `segment` and folded when the turn is finished.
  let segment: Item[] = [];
  const flushSegment = (promptIdx: number, finished: boolean) => {
    const turn = footerAfter.get(promptIdx);
    segment = quietFailures(segment, finished);
    items.push(...(finished && promptIdx >= 0 ? foldTurn(segment, turn, `w${promptIdx}`) : segment));
    segment = [];
  };
  const flushFooter = (promptIdx: number) => {
    flushSegment(promptIdx, true);
    if (promptIdx === lastPrompt) {
      // Not after "Not now", and not once SolidWorks has restarted since (by the button or any other way).
      const offer = restartOffer(thread);
      const turnAt = Date.parse(thread.turns.find((t) => t.msgIndex === promptIdx)?.at ?? "") || 0;
      const done = turnAt > 0 && turnAt < useStore.getState().sessionStart;
      if (offer && !done && !thread.restartDismissed?.includes(offer.id)) items.push({ kind: "restart", key: `r${promptIdx}`, ...offer });
    }
    const turn = footerAfter.get(promptIdx);
    if (turn) items.push({ kind: "footer", key: `f${promptIdx}`, turn, isLast: promptIdx === lastPrompt });
  };

  let currentPrompt = -1;
  let prevTurn: TurnMeta | undefined;
  const sessionNow = useStore.getState().sessionStart;
  thread.messages.forEach((m, i) => {
    if (m.role === "user" && typeof m.content !== "string" && !isPrompt(m)) {
      // After tool results: context a plugin's PreToolUse / PostToolUse hook added for the agent.
      m.content.forEach((b, j) => {
        if (b.type === "text" && b.text.startsWith(HOOK_TAG)) segment.push({ kind: "hook", key: `m${i}h${j}`, extra: parseExtra(b.text) });
      });
      return;
    }
    if (isPrompt(m)) {
      if (currentPrompt >= 0) flushFooter(currentPrompt);
      else flushSegment(-1, false);
      currentPrompt = i;
      // Where an earlier SolidWorks session (or a day or more) ends: a divider labelled for the messages above it.
      const turn = thread.turns.find((t) => t.msgIndex === i);
      if (turn && prevTurn && isBreak(prevTurn, turn, sessionNow)) items.push({ kind: "divider", key: `d${i}`, label: breakLabel(prevTurn, sessionNow) });
      if (turn) prevTurn = turn;
      items.push(promptItem(m, i));
      return;
    }
    // Claude: one item per block; other providers add their progress notes and web steps in output order.
    segment.push(...assistantItems(m, i, results, showProgress));
  });

  if (run) {
    flushSegment(currentPrompt, false); // the running turn stays unfolded
    const waitingOnTool = !run.live && (run.runningToolId || run.approval || run.question);
    if (!waitingOnTool) items.push(...liveItems(run.live, showProgress));
  }
  else if (currentPrompt >= 0) flushFooter(currentPrompt);
  else flushSegment(-1, false);
  return items;
}

function liveItems(live: LiveBlock[] | null, showProgress: boolean): Item[] {
  const out: Item[] = [];
  if (!live || live.length === 0) return [{ kind: "thinking", key: "live-thinking" }];
  live.forEach((b, i) => {
    const key = `live${i}`;
    if (b.type === "thinking" && b.text.trim() && showProgress) out.push({ kind: "progress", key, text: b.text.trim() });
    else if (b.type === "text" && b.text) out.push({ kind: "text", key, text: b.text, streaming: i === live.length - 1 });
    else if (b.type === "tool_use" && b.name !== "ask_user" && b.name !== "ask_selection" && b.name !== "ask_secret" && b.name !== "offer_restart") {
      out.push({ kind: "tool", key, call: { id: b.id ?? key, name: b.name ?? "tool", input: undefined, partial: b.json ?? "" } });
    }
  });
  const last = live[live.length - 1];
  if (!last || last.type === "thinking" || last.type === "other") out.push({ kind: "thinking", key: "live-thinking" });
  return out;
}

function promptItem(m: MessageParam, index: number): Item {
  const blocks: ContentBlockParam[] = typeof m.content === "string" ? [{ type: "text", text: m.content }] : m.content;
  const texts: string[] = [];
  const images: string[] = [];
  const imageLabels: (string | null)[] = [];
  let label: string | null = null; // a markup's "[M2] ..." block names the image after it
  let refs: { id: string; text: string }[] = [];
  let view: string | null = null;
  let afterContext = false;
  const extras: Extra[] = [];
  let extra: Extra | null = null;
  for (const b of blocks) {
    if (b.type === "text") {
      extra = null;
      if (b.text.startsWith("<sw_context>")) {
        refs = [...refs, ...parseRefs(b.text)];
        afterContext = true;
      } else if (labelNumber(b.text) !== null) {
        label = `M${labelNumber(b.text)}`;
      } else if (b.text.startsWith(MODEL_TAG) || b.text.startsWith(WATCH_TAG) || b.text.startsWith(VOICE_TAG) || b.text.startsWith(HOOK_TAG) || b.text.startsWith(EVENT_TAG)) {
        extra = parseExtra(b.text);
        extras.push(extra);
      } else texts.push(b.text);
    } else if (b.type === "image") {
      const src = (b as ImageBlockParam).source;
      if (src.type !== "base64") continue;
      const url = `data:${src.media_type};base64,${src.data}`;
      // Images right after a model / watch block belong to it; after the context block, it's the user's view.
      if (extra) extra.images.push(url);
      else if (afterContext) view = url;
      else {
        images.push(url);
        imageLabels.push(label);
      }
      label = null;
    } else if ((b.type as string) === "document") {
      extra = null;
      extras.push({ kind: "pdf", title: (b as { title?: string }).title ?? "PDF", body: "", images: [] });
    }
  }
  return { kind: "prompt", key: `p${index}`, index, text: texts.join("\n\n"), images, imageLabels, refs, view, extras };
}

function parseExtra(raw: string): Extra {
  const tagged = parseTagged(raw);
  if (tagged?.tag === "hook") {
    const { event, tool } = tagged.attrs;
    return { kind: "hook", title: `Plugin context${event ? ` · ${event}` : ""}${tool ? ` · ${tool}` : ""}`, body: tagged.body, images: [] };
  }
  if (tagged?.tag === "event") {
    const { kind, source, from } = tagged.attrs;
    // Claude Desktop or Codex (the local link): who sent it and the message, not the add-in's note to the agent.
    if (kind === "wake" && from) return { kind: "event", title: `Message from ${from}`, body: parseLinkMessage(tagged.body) ?? tagged.body, images: [] };
    // agent-bridge: who wrote and what they said, not its instructions to the model (those are one click away).
    const msgs = kind === "wake" ? bridgeMessages(tagged.body) : [];
    if (msgs.length) {
      const from = [...new Set(msgs.map((m) => (m.console ? m.from : m.from.replace(/@.*$/, ""))))];
      const title = `Message${msgs.length > 1 ? "s" : ""} from ${from.join(", ")}`;
      return { kind: "event", title, body: msgs.map((m) => (from.length > 1 ? `${m.from}: ${m.body}` : m.body)).join("\n\n"), images: [] };
    }
    const owed = kind === "stop" ? reminderSenders(tagged.body) : [];
    if (owed.length) return { kind: "event", title: `agent-bridge reminded the agent to reply to ${owed.join(", ")}`, body: tagged.body, images: [], quiet: true };
    return { kind: "event", title: kind === "stop" ? "Continued by a plugin" : source || "Plugin", body: tagged.body, images: [] };
  }
  if (raw.startsWith(VOICE_TAG)) {
    const head = /^<\w+([^>]*)>/.exec(raw)?.[1] ?? "";
    const attr = (name: string) => new RegExp(`${name}="([^"]*)"`).exec(head)?.[1];
    const body = raw.replace(/^<[^>]*>\n?/, "").replace(/\n?<\/voice_timeline>\s*$/, "").trim();
    const images = Number(attr("images") ?? 0);
    const markups = Number(attr("markups") ?? 0);
    const views = images - markups;
    const parts = [attr("duration"), views > 0 && `${views} view${views === 1 ? "" : "s"}`, markups > 0 && `${markups} markup${markups === 1 ? "" : "s"}`].filter(Boolean);
    // The timeline without the explanation for the agent.
    return { kind: "voice", title: `Voice message · ${parts.join(" · ")}`, body: body.split("\n").filter((l) => /^\d+:\d\d/.test(l)).join("\n"), images: [] };
  }
  const watch = raw.startsWith(WATCH_TAG);
  const head = /^<\w+([^>]*)>/.exec(raw);
  const attr = (name: string) => new RegExp(`${name}="([^"]*)"`).exec(head?.[1] ?? "")?.[1]?.replace(/&quot;/g, '"').replace(/&lt;/g, "<").replace(/&gt;/g, ">").replace(/&amp;/g, "&");
  const body = raw.replace(/^<[^>]*>\n?/, "").replace(/\n?<\/(model_file|watch_log)>\s*$/, "").trim();
  const steps = attr("steps");
  return {
    kind: watch ? "watch" : "model",
    title: watch ? `Watch log${steps ? ` · ${steps} step${steps === "1" ? "" : "s"}` : ""}${attr("doc") ? ` · ${attr("doc")}` : ""}` : attr("name") ?? "3D model",
    body,
    images: [],
  };
}

/** What a plugin hook added for the agent: a small card that opens to the text (Options can hide them). */
function HookCard({ extra }: { extra: Extra }) {
  const [open, setOpen] = useState(false);
  const shown = useStore((s) => s.settings.pluginEventsInChat !== false);
  if (!shown) return null;
  const first = extra.body.split("\n").find((l) => l.trim()) ?? "";
  return (
    <div className="flex max-w-[92%] min-w-0 flex-col gap-0.5 self-start">
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        className="flex min-w-0 items-center gap-1 rounded-[3px] border border-dashed border-line px-1.5 py-0.5 text-left text-[11px] text-text-3 hover:text-text"
        title={open ? "Hide" : "What the plugin added for the agent (not your words)"}
      >
        <IconPlugin size={12} />
        <span className="shrink-0 font-semibold">{extra.title}</span>
        {!open && <span className="min-w-0 truncate">{first}</span>}
        <Caret open={open} className="shrink-0" />
      </button>
      {open && <div className="selectable max-h-[220px] overflow-y-auto whitespace-pre-wrap break-words rounded-[3px] border border-line bg-card-2 px-2 py-1 text-[11px] text-text-2">{extra.body}</div>}
    </div>
  );
}

/** A turn a plugin started (a wake: news from another agent; a Stop hook that kept the turn going): not the user's words. */
function SystemEventPrompt({ extras }: { extras: Extra[] }) {
  const [open, setOpen] = useState(false);
  return (
    <div className="mt-2 flex min-w-0 flex-col gap-1 first:mt-0">
      {extras.map((x, i) =>
        x.quiet ? (
          <button
            key={i}
            type="button"
            onClick={() => setOpen((o) => !o)}
            title={open ? "Show less" : "Show what the plugin said"}
            className="flex min-w-0 items-start gap-1.5 self-start px-1 text-left text-[11px] text-text-3 hover:text-text"
          >
            <span className="mt-px shrink-0">
              <IconPlugin size={12} />
            </span>
            <span className="min-w-0 flex-1">
              {x.title}
              {open && <span className="mt-0.5 block whitespace-pre-wrap break-words">{x.body}</span>}
            </span>
          </button>
        ) : (
        <button
          key={i}
          type="button"
          onClick={() => setOpen((o) => !o)}
          title={open ? "Show less" : "Show all"}
          className="flex min-w-0 items-start gap-1.5 self-stretch rounded-[3px] border border-accent/40 bg-accent-soft px-2 py-1 text-left text-[11.5px] text-text"
        >
          <span className="mt-px shrink-0">
            <IconPlugin size={13} />
          </span>
          <span className="min-w-0 flex-1">
            <b>{x.title}:</b> <span className={`whitespace-pre-wrap break-words ${open ? "" : "line-clamp-3"}`}>{x.body}</span>
          </span>
        </button>
        ),
      )}
    </div>
  );
}

/** A 3D model or Watch me log in a sent prompt: a compact card that opens to the text the agent got. */
function ExtraCard({ extra, onImage }: { extra: Extra; onImage: (s: string) => void }) {
  const [open, setOpen] = useState(false);
  if (extra.kind === "hook") return <HookCard extra={extra} />;
  if (extra.kind === "event") return <SystemEventPrompt extras={[extra]} />;
  if (extra.kind === "pdf") {
    return (
      <span className="flex max-w-[92%] items-center gap-1 rounded-[3px] border border-line bg-card-2 px-1.5 py-0.5 text-[11px] text-text-2">
        <IconDocs size={13} />
        <span className="truncate">{extra.title}</span>
      </span>
    );
  }
  const line =
    extra.kind === "model"
      ? (extra.body.split("\n").find((l) => l.startsWith("Size:")) ?? extra.body.split("\n").find((l) => l.includes("couldn't be read")) ?? "")
      : extra.kind === "voice"
        ? extra.body
            .split("\n")
            .map((l) => l.replace(/^\d+:\d\d\s+/, ""))
            .filter((l) => !l.startsWith("said:") && !l.startsWith("[image"))
            .slice(0, 3)
            .join(" · ")
        : (extra.body.split("\n").find((l) => /^\d+\. /.test(l)) ?? "");
  return (
    <div className="flex max-w-[92%] flex-col items-end gap-1">
      <div className="flex items-stretch gap-1.5 overflow-hidden rounded-[3px] border border-line bg-card-2 shadow-[var(--shadow)]">
        {extra.images.slice(0, 1).map((src, i) => (
          <button key={i} onClick={() => onImage(src)} title="Open the image" className="shrink-0 border-r border-line">
            <img src={src} alt="" className="h-[52px] w-auto" />
          </button>
        ))}
        <button type="button" onClick={() => setOpen((o) => !o)} className="flex min-w-0 flex-col items-start justify-center py-1 pr-2 text-left" title={open ? "Hide details" : "Show what the agent received"}>
          <span className="flex items-center gap-1 text-[11.5px] font-semibold text-text">
            {extra.kind === "model" ? <IconModelFile size={13} /> : extra.kind === "voice" ? <IconMic size={13} /> : <IconWatch size={13} />}
            <span className="truncate">{extra.title}</span>
            <Caret open={open} className="text-text-3" />
          </span>
          {line && <span className="max-w-[260px] truncate text-[10.5px] text-text-3">{line.replace(/^Size: /, "")}</span>}
        </button>
      </div>
      {open && (
        <>
          <div className="selectable max-h-[220px] overflow-y-auto whitespace-pre-wrap rounded-[3px] border border-line bg-card-2 px-2 py-1 text-[11px] text-text-2">{extra.body}</div>
          {extra.images.length > 1 && (
            <div className="flex flex-wrap justify-end gap-1.5">
              {extra.images.slice(1).map((src, i) => (
                <button key={i} onClick={() => onImage(src)} className="overflow-hidden rounded-[2px] border border-input-border shadow-[var(--shadow)]">
                  <img src={src} alt="" className="h-[48px] w-auto" />
                </button>
              ))}
            </div>
          )}
        </>
      )}
    </div>
  );
}

/** "[S1] Face of Boss-Extrude1 - planar, ..." lines from the context block, shortened for chips. */
function parseRefs(raw: string): { id: string; text: string }[] {
  const out: { id: string; text: string }[] = [];
  for (const line of raw.split("\n")) {
    const m = /^\[(S\d+)\] (.+?)(?: - |$)/.exec(line);
    if (m) out.push({ id: m[1], text: m[2] });
  }
  return out;
}

function PromptBubble({
  item,
  thread,
  running,
  onImage,
}: {
  item: Extract<Item, { kind: "prompt" }>;
  thread: Thread;
  running: boolean;
  onImage: (s: string) => void;
}) {
  const resendFrom = useStore((s) => s.resendFrom);
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState("");
  const [copied, setCopied] = useState(false);
  const { text, images, imageLabels, refs, view, index, extras } = item;

  /**
   * Resending drops later replies. Changes the agent made to the model since then can be undone in one go (the
   * add-in undoes step by step until the model matches its state when this message was sent) or kept.
   */
  const confirmRewind = async (): Promise<boolean> => {
    if (writesAfter(thread, index) === 0) return true;
    const docKey = useStore.getState().viewKey;
    const toolUseIds = cellIdsAfter(thread, index);
    const changes = await rpc<{ changes: number }>("history.undoable", { docKey, toolUseIds })
      .then((r) => r?.changes ?? -1)
      .catch(() => -1);
    if (changes === 0) return true; // the model is already back where it was
    const s = (n: number, one: string, many: string) => (n === 1 ? one : many);
    const keep = { value: "keep", label: "Keep changes" };
    const choice = await ask({
      title: "Replace the replies after this message?",
      message:
        changes > 0 ? (
          <p>
            The agent made {changes} {s(changes, "change", "changes")} to the model since then. Undo {s(changes, "it", "them")} to put the
            model back the way it was when you sent this message, or keep {s(changes, "it", "them")} and continue from the current model.
          </p>
        ) : (
          <p>
            The agent changed the model since then. Those changes can't be undone automatically here (SolidWorks was restarted or the
            document reopened since) - they stay in the model; use Ctrl+Z in SolidWorks to undo them.
          </p>
        ),
      actions:
        changes > 0
          ? [
              keep,
              {
                value: "undo",
                label: `Undo ${changes} ${s(changes, "change", "changes")}`,
                primary: true,
                run: async () => {
                  const r = await rpc<{ ok: boolean; message?: string }>("history.undoSince", { docKey, toolUseIds });
                  if (!r?.ok) return r?.message ?? "The undo didn't complete.";
                },
              },
            ]
          : [{ ...keep, label: "Continue", primary: true }],
    });
    return choice !== null;
  };

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(promptText(thread.messages[index]));
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch {
      /* clipboard unavailable */
    }
  };

  const submitEdit = async () => {
    if (!draft.trim() && images.length === 0 && extras.length === 0) return;
    if (!(await confirmRewind())) return;
    setEditing(false);
    resendFrom(index, draft);
  };

  if (editing) {
    return (
      <div className="sw-rollout mt-2 flex flex-col gap-1.5 !border-accent p-1.5 first:mt-0">
        <textarea
          autoFocus
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          onFocus={(e) => e.currentTarget.setSelectionRange(e.currentTarget.value.length, e.currentTarget.value.length)}
          onKeyDown={(e) => {
            if (e.key === "Enter" && !e.shiftKey && !e.nativeEvent.isComposing) {
              e.preventDefault();
              submitEdit();
            } else if (e.key === "Escape") {
              setEditing(false);
            }
          }}
          rows={Math.min(8, Math.max(2, draft.split("\n").length))}
          className="sw-input w-full resize-none px-2 py-1 text-[12px] leading-[1.42]"
        />
        <div className="flex items-center justify-end gap-1.5">
          <span className="mr-auto text-[10.5px] text-text-3">Replies after this message will be replaced.</span>
          <button onClick={() => setEditing(false)} className="sw-btn">
            <IconCancel size={11} />
            Cancel
          </button>
          <button
            onClick={submitEdit}
            disabled={!draft.trim() && images.length === 0 && extras.length === 0}
            className="sw-btn sw-btn-primary"
          >
            Send
          </button>
        </div>
      </div>
    );
  }

  if (/^\[Run tool: /.test(text)) return <ToolRunPrompt text={text} images={images} refs={refs} view={view} extras={extras} onImage={onImage} />;
  // A wake or a Stop-hook continuation (with no words of the user's): shown as the plugin's event, not as a bubble.
  if (!text && images.length === 0 && extras.length > 0 && extras.every((x) => x.kind === "event")) return <SystemEventPrompt extras={extras} />;

  return (
    <div className="group mt-2 flex flex-col items-end gap-1 first:mt-0">
      {images.length > 0 && (
        <div className="flex flex-wrap justify-end gap-1.5">
          {images.map((src, i) => (
            <button key={i} onClick={() => onImage(src)} title={imageLabels[i] ?? undefined} className="relative overflow-hidden rounded-[2px] border border-input-border shadow-[var(--shadow)]">
              <img src={src} alt={imageLabels[i] ?? ""} className="h-[64px] w-auto" />
              {imageLabels[i] && (
                <span className="absolute left-0.5 top-0.5 rounded-[2px] bg-black/65 px-1 text-[10px] font-semibold leading-[14px] text-white">{imageLabels[i]}</span>
              )}
            </button>
          ))}
        </div>
      )}
      {extras.map((x, i) => (
        <ExtraCard key={i} extra={x} onImage={onImage} />
      ))}
      {text && <PromptText text={text} />}
      {(refs.length > 0 || view) && (
        <div className="flex max-w-[92%] flex-wrap items-center justify-end gap-1">
          {refs.map((r) => (
            <span key={r.id} title={r.text} className="flex max-w-[180px] items-center gap-1 rounded-[2px] border border-line bg-card-2 px-1 text-[10.5px] text-text-2">
              <b className="text-accent">{r.id}</b>
              <span className="truncate">{r.text}</span>
            </span>
          ))}
          {view && (
            <button onClick={() => onImage(view)} title="The view the agent saw with this message" className="flex items-center gap-1 rounded-[2px] border border-line bg-card-2 py-px pl-0.5 pr-1 text-[10.5px] text-text-3 hover:text-text">
              <img src={view} alt="" className="h-4 w-auto rounded-[1px]" />
              <IconEye size={11} /> View
            </button>
          )}
        </div>
      )}
      <div className="flex h-5 items-center gap-0.5">
        <div className="flex items-center gap-0.5 opacity-0 transition-opacity group-hover:opacity-100 focus-within:opacity-100">
          <BubbleAction title={copied ? "Copied" : "Copy"} onClick={copy}>
            {copied ? <IconOk size={12} /> : <IconCopy size={13} />}
          </BubbleAction>
          {!running && (
            <>
              <BubbleAction
                title="Edit"
                onClick={() => {
                  setDraft(text);
                  setEditing(true);
                }}
              >
                <IconEdit size={13} />
              </BubbleAction>
              <BubbleAction title="Resend" onClick={async () => (await confirmRewind()) && resendFrom(index)}>
                <IconResend size={13} />
              </BubbleAction>
            </>
          )}
        </div>
      </div>
    </div>
  );
}

/**
 * A ribbon tool run: just "Tool: Smart fillet" - the inputs, attachments, selections and view the tool captured
 * stay folded away unless the user opens them.
 */
function ToolRunPrompt({
  text,
  images,
  refs,
  view,
  extras,
  onImage,
}: {
  text: string;
  images: string[];
  refs: { id: string; text: string }[];
  view: string | null;
  extras: Extra[];
  onImage: (s: string) => void;
}) {
  const [open, setOpen] = useState(false);
  const tools = useStore((s) => s.userTools);
  const m = /^\[Run tool: ([a-z0-9:-]+)\]\s*([\s\S]*)$/.exec(text);
  const tool = m ? tools.find((t) => t.name === m[1]) : undefined;
  const body = (m?.[2] ?? "").replace(/<\/?tool_inputs>/g, "").trim();
  const hasDetails = !!body || images.length > 0 || refs.length > 0 || !!view || extras.length > 0;
  return (
    <div className="mt-2 flex flex-col items-end gap-1 first:mt-0">
      <button
        type="button"
        onClick={() => hasDetails && setOpen((o) => !o)}
        className="flex max-w-[88%] items-center gap-1.5 rounded-[3px] border border-line bg-user px-2.5 py-1 text-[12px] font-semibold text-text shadow-[inset_0_1px_0_var(--bevel-hi),var(--shadow)]"
        title={hasDetails ? (open ? "Hide the inputs" : "Show the inputs and context the tool captured") : undefined}
      >
        {tool?.iconSvg ? <img src={svgDataUrl(tool.iconSvg)} alt="" width={14} height={14} /> : <ToolIcon name={tool?.icon ?? "macro"} size={14} />}
        <span>Tool: {tool?.title ?? m?.[1]}</span>
        {hasDetails && <Caret open={open} className="text-text-3" />}
      </button>
      {open && (
        <div className="flex max-w-[92%] flex-col items-end gap-1">
          {body && <div className="selectable whitespace-pre-wrap rounded-[3px] border border-line bg-card-2 px-2 py-1 text-[11.5px] text-text-2">{body}</div>}
          {extras.map((x, i) => (
            <ExtraCard key={i} extra={x} onImage={onImage} />
          ))}
          {(images.length > 0 || view) && (
            <div className="flex flex-wrap justify-end gap-1.5">
              {[...images, ...(view ? [view] : [])].map((src, i) => (
                <button key={i} onClick={() => onImage(src)} className="overflow-hidden rounded-[2px] border border-input-border shadow-[var(--shadow)]">
                  <img src={src} alt="" className="h-[48px] w-auto" />
                </button>
              ))}
            </div>
          )}
          {refs.length > 0 && (
            <div className="flex flex-wrap justify-end gap-1">
              {refs.map((r) => (
                <span key={r.id} title={r.text} className="flex max-w-[180px] items-center gap-1 rounded-[2px] border border-line bg-card-2 px-1 text-[10.5px] text-text-2">
                  <b className="text-accent">{r.id}</b>
                  <span className="truncate">{r.text}</span>
                </span>
              ))}
            </div>
          )}
        </div>
      )}
    </div>
  );
}

/**
 * The user's words; a leading "[Run workflow: name]" / "[Run tool: name]" is shown as a chip instead of raw text, so
 * are "[Use plugin: x]" tag lines, and a "[Command: /name args]" head with the command's expanded text folded away.
 */
function PromptText({ text: raw }: { text: string }) {
  const tools = useStore((s) => s.userTools);
  const [open, setOpen] = useState(false);
  const tagRe = /^\[Use (plugin|tool|skill|agent): ([^\]\n]+)\]\n?/;
  const tags: { kind: string; name: string }[] = [];
  let text = raw;
  for (let t = tagRe.exec(text); t; t = tagRe.exec(text)) {
    tags.push({ kind: t[1], name: t[2] });
    text = text.slice(t[0].length);
  }
  const cmd = /^\[Command: (\/[^\]\n]+)\]\n?([\s\S]*)$/.exec(text);
  const m = /^\[Run (tool|workflow): ([a-z0-9:-]+)\]\s*([\s\S]*)$/.exec(text);
  const tool = m ? tools.find((t) => t.name === m[2]) : undefined;
  const body = cmd ? (open ? cmd[2] : "") : m ? m[3] : text;
  const label = tool?.kind === "tool" ? "Tool" : tool?.kind === "workflow" ? "Workflow" : m?.[1] === "tool" ? "Tool" : "Workflow";
  return (
    <div className="selectable max-w-[88%] rounded-[3px] border border-line bg-user px-2.5 py-1.5 text-[12px] text-text shadow-[inset_0_1px_0_var(--bevel-hi),var(--shadow)]">
      {tags.length > 0 && (
        <div className={`flex flex-wrap gap-1 ${body || cmd || m ? "mb-1" : ""}`}>
          {tags.map((t) => (
            <span key={`${t.kind}:${t.name}`} className="flex h-[18px] max-w-full items-center gap-1 rounded-[2px] border border-accent/50 bg-accent-soft px-1 text-[10.5px]" title={`Tagged ${t.kind}`}>
              {t.kind === "plugin" && <IconPlugin size={10} />}
              <span className="truncate">@{t.name}</span>
            </span>
          ))}
        </div>
      )}
      {cmd && (
        <button type="button" onClick={() => setOpen((o) => !o)} className={`flex max-w-full items-center gap-1.5 text-left font-semibold ${body ? "mb-1" : ""}`} title={open ? "Hide the command's text" : "Show what the command sent to the agent"}>
          <IconPlugin size={14} />
          <span className="min-w-0 break-words">{cmd[1]}</span>
          <Caret open={open} className="shrink-0 text-text-3" />
        </button>
      )}
      {m && !cmd && (
        <div className={`flex items-center gap-1.5 font-semibold ${body ? "mb-1" : ""}`}>
          {tool?.iconSvg ? <img src={svgDataUrl(tool.iconSvg)} alt="" width={14} height={14} /> : <ToolIcon name={tool?.icon ?? "macro"} size={14} />}
          <span>
            {label}: {tool?.title ?? m[2]}
          </span>
        </div>
      )}
      {body && <div className={`whitespace-pre-wrap ${cmd ? "text-[11.5px] text-text-2" : ""}`}>{body}</div>}
    </div>
  );
}

function BubbleAction({ title, onClick, children }: { title: string; onClick: () => void; children: React.ReactNode }) {
  return (
    <button
      type="button"
      title={title}
      aria-label={title}
      onClick={onClick}
      className="sw-tool !h-[22px] !min-w-[22px] !p-0"
    >
      {children}
    </button>
  );
}

function TurnFooter({ turn, isLast, running }: { turn: TurnMeta; isLast: boolean; running: boolean }) {
  const retry = useStore((s) => s.retry);
  const u = turn.usage;
  const tokens = u ? u.input + u.cacheRead + u.cacheWrite : 0;
  const cachedPct = tokens > 0 ? Math.round((u!.cacheRead / tokens) * 100) : 0;
  return (
    <div className="flex flex-col gap-1.5">
      {turn.error && (
        <div className="flex items-start gap-2 rounded-[3px] border border-err/50 bg-err-soft px-2 py-1.5 text-[12px] text-text shadow-[var(--shadow)]">
          <span className="mt-px shrink-0"><IconError size={14} /></span>
          <span className="selectable flex-1">{turn.error}</span>
          {isLast && !running && turn.stop !== "aborted" && (
            <button onClick={retry} className="sw-btn shrink-0">
              <IconResend size={12} /> Retry
            </button>
          )}
        </div>
      )}
      {turn.stop === "aborted" && !turn.error && <div className="pl-1 text-[11px] text-text-3">Stopped.</div>}
      <div className="flex items-center gap-1.5 pl-1 text-[10.5px] text-text-3">
        <span>{modelOption(turn.model).label}</span>
        <span>·</span>
        <span>{turn.effort}</span>
        {turn.serviceTier && (
          <>
            <span>·</span>
            <span>{turn.serviceTier === "ultrafast" ? "Ultrafast" : turn.serviceTier === "fast" ? "Fast" : turn.serviceTier}</span>
          </>
        )}
        {turn.durationMs !== undefined && (
          <>
            <span>·</span>
            <span>{formatDuration(turn.durationMs)}</span>
          </>
        )}
        {tokens > 0 && (
          <>
            <span>·</span>
            <span
              title={
                `Largest prompt: ${(u.context ?? 0).toLocaleString()} tokens\n` +
                `Sent over all steps: ${tokens.toLocaleString()} (${cachedPct}% from cache, billed at ${Math.round(modelOption(turn.model).caps.cacheReadRatio * 100)}%)\n` +
                `uncached ${u.input.toLocaleString()} · cache write ${u.cacheWrite.toLocaleString()} · output ${u.output.toLocaleString()}`
              }
            >
              {u.context ? `${formatTokens(u.context)} context` : `${formatTokens(tokens)} in`} / {formatTokens(u.output)} out
            </span>
          </>
        )}
      </div>
    </div>
  );
}

/** A run_agent sub-agent's tool calls so far, under its card while it works. */
function SubAgentSteps({ progress }: { progress: SubAgentProgress }) {
  return (
    <div className="-mt-1 flex flex-wrap items-center gap-x-1.5 gap-y-0.5 pl-2 text-[11px] text-text-3" title={`${progress.agent}: ${progress.steps.length} step(s)`}>
      <span className="text-text-2">{progress.agent}</span>
      {progress.steps.length === 0 && <span className="shimmer">thinking</span>}
      {progress.steps.map((s) => (
        <span key={s.id} className={`inline-flex items-center gap-0.5 ${s.state === "running" ? "shimmer" : s.state === "error" ? "text-err" : ""}`}>
          {s.state === "ok" ? <IconOk size={10} /> : s.state === "error" ? <IconError size={10} /> : null}
          {toolTitle(s.name, undefined, undefined)}
        </span>
      ))}
    </div>
  );
}

function ApprovalCard({ threadId }: { threadId: string }) {
  const answer = useStore((s) => s.answerApproval);
  // A sub-agent's call: the card sits under the run_agent card, so it says whose call it is and shows the code.
  const sub = useStore((s) => s.runs[threadId]?.approval?.sub);
  const [rejecting, setRejecting] = useState(false);
  const [note, setNote] = useState("");
  return (
    <div className="rounded-[3px] border border-accent/60 bg-accent-soft px-2 py-1.5 shadow-[inset_0_1px_0_var(--bevel-hi)]">
      <div className="mb-1.5 flex items-center gap-1.5 text-[12px] font-semibold text-text">
        <IconShield size={14} /> {sub ? `The sub-agent ${sub.agent} wants to run ${sub.name}. Run it in SolidWorks?` : "Run this code in SolidWorks?"}
      </div>
      {sub && (typeof sub.input?.code === "string" ? (
        <Code code={sub.input.code} className="mb-1.5 max-h-[240px]" />
      ) : (
        <pre className="selectable sw-input mb-1.5 max-h-[120px] overflow-auto px-2 py-1 font-mono text-[11px] text-text-2">{JSON.stringify(sub.input ?? {}, null, 1)}</pre>
      ))}
      {!rejecting ? (
        <div className="flex flex-wrap gap-1.5">
          <button autoFocus onClick={() => answer(threadId, { decision: "run" })} className="sw-btn">
            <IconOk size={12} />
            Run
          </button>
          <button onClick={() => answer(threadId, { decision: "always" })} className="sw-btn" title="Don't ask again in this chat">
            Always allow in this chat
          </button>
          <button onClick={() => setRejecting(true)} className="sw-btn">
            <IconCancel size={11} />
            Reject…
          </button>
        </div>
      ) : (
        <form
          className="flex gap-1.5"
          onSubmit={(e) => {
            e.preventDefault();
            answer(threadId, { decision: "reject", note: note.trim() || undefined });
          }}
        >
          <input
            autoFocus
            value={note}
            onChange={(e) => setNote(e.target.value)}
            placeholder="Optional: tell the agent what to do instead"
            className="sw-input h-[22px] min-w-0 flex-1 px-1.5 text-[12px]"
          />
          <button type="submit" className="sw-btn">
            <IconCancel size={11} />
            Reject
          </button>
        </form>
      )}
    </div>
  );
}

function QuestionCard({ call, threadId, run }: { call: ToolCall; threadId: string; run: RunState | undefined }) {
  const answer = useStore((s) => s.answerQuestion);
  // In the store, so a voice message can fill it in (Settings > Voice > Send: check first).
  const text = useStore((s) => s.answerDrafts[threadId] ?? "");
  const setText = (v: string) => useStore.getState().setAnswerDraft(threadId, v);
  const pending = run?.question?.toolUseId === call.id;
  const answered = typeof call.result?.content === "string" ? call.result.content : undefined;
  const options: string[] = call.input?.options ?? [];
  if (!pending)
    return (
      <Answered question={String(call.input?.question ?? "")} answer={answered ?? null}>
        <div className="selectable text-[12px] text-text-2">{call.input?.question}</div>
        {options.length > 0 && <div className="mt-1 text-[11px] text-text-3">Options: {options.join(" · ")}</div>}
      </Answered>
    );
  return (
    <div className={`sw-rollout px-2 py-1.5 ${pending ? "!border-accent" : ""}`}>
      <div className="mb-1.5 flex items-start gap-2">
        <span className="mt-px shrink-0"><IconQuestion size={15} /></span>
        <div className="selectable text-[12px] font-semibold text-text">{call.input?.question}</div>
      </div>
      {pending ? (
        <div className="flex flex-col gap-1.5 pl-[22px]">
          {options.length > 0 && (
            <div className="flex flex-wrap gap-1.5">
              {options.map((o) => (
                <button key={o} onClick={() => answer(threadId, o)} className="sw-btn !h-auto min-h-[22px] whitespace-normal py-0.5 text-left">
                  {o}
                </button>
              ))}
            </div>
          )}
          <form
            className="flex gap-1.5"
            onSubmit={(e) => {
              e.preventDefault();
              if (text.trim()) answer(threadId, text.trim());
            }}
          >
            <input
              autoFocus
              value={text}
              onChange={(e) => setText(e.target.value)}
              placeholder={options.length ? "Or type your own answer" : "Type your answer"}
              className="sw-input h-[22px] min-w-0 flex-1 px-1.5 text-[12px]"
            />
            <button type="submit" disabled={!text.trim()} className="sw-btn sw-btn-primary">
              Answer
            </button>
          </form>
          <QuestionTimer threadId={threadId} run={run} />
        </div>
      ) : answered ? (
        <div className="pl-[22px] text-[12px] text-text-2">
          <span className="text-text-3">Answer: </span>
          {answered}
        </div>
      ) : (
        <div className="pl-[22px] text-[11.5px] text-text-3">Not answered</div>
      )}
    </div>
  );
}

const SUGGESTIONS = [
  "Summarize this model and check it for rebuild errors",
  "Add a 2 mm fillet to the selected edges",
  "Create a 120 × 80 × 10 mm plate with a Ø8 hole 15 mm in from each corner",
  "Set the material to 6061 aluminium and tell me the mass",
];

function EmptyState() {
  const doc = useStore((s) => s.docs[s.activeDocKey ?? ""]);
  const send = useStore((s) => s.send);
  const setDraft = useStore((s) => s.setDraft);
  return (
    <div className="flex min-h-0 flex-1 flex-col items-center justify-center overflow-y-auto px-5 py-6 text-center">
      <div className="mb-4">
        <BrandLogo height={30} />
      </div>
      <div className="text-[14px] font-semibold text-text">{doc ? `Ask about ${doc.title}` : "Ask Unify3D anything"}</div>
      <p className="mt-1 max-w-[300px] text-[12px] text-text-2">
        {doc
          ? "It works in the active document through the SolidWorks API. This chat keeps going as you open and switch files."
          : "Open or create a part, assembly or drawing to keep the conversation with that file."}
      </p>
      <div className="mt-4 flex w-full max-w-[340px] flex-col gap-1.5">
        {SUGGESTIONS.map((s) => (
          <button
            key={s}
            onClick={() => (s.includes("selected") ? setDraft(s) : send(s))}
            className="sw-btn !h-auto !justify-start whitespace-normal py-1.5 text-left"
          >
            {s}
          </button>
        ))}
      </div>
    </div>
  );
}

function formatDuration(ms: number) {
  const s = Math.round(ms / 1000);
  return s < 60 ? `${s}s` : `${Math.floor(s / 60)}m ${s % 60}s`;
}

function formatTokens(n: number) {
  return n >= 1_000_000 ? `${(n / 1_000_000).toFixed(1)}M` : n >= 1000 ? `${(n / 1000).toFixed(1)}k` : String(n);
}
