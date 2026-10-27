import { useEffect, useMemo, useRef, useState } from "react";
import { X } from "lucide-react";
import { Dropdown, IconButton } from "./Menu";
import { ToolsPalette } from "./ToolsPalette";
import { SlashMenu, type MenuRow } from "./SlashMenu";
import {
  IconAgent,
  IconEffort,
  IconPlugin,
  IconAttach,
  IconCamera,
  IconDocs,
  IconEye,
  IconModelFile,
  IconWarning,
  IconWatch,
  Spinner,
  IconCancel,
  IconEdit,
  IconMarkup,
  IconMic,
  IconOk,
  IconSelect,
  IconSend,
  IconStop,
  IconToolArrow,
  IconToolCircle,
  IconToolHighlighter,
  IconToolPen,
  IconUndo,
} from "./SwIcons";
import { EMPTY, MARKUP_COLORS, MARKUP_NAME, currentThread, draftKey, isAgentSelection, talkKeyName, useStore, type MarkupTool, type QueuedMessage, type Tag } from "../store";
import { atQuery, slashQuery, slashRows, tagRows, type CommandSources } from "../lib/commands";
import { markupNumber, mentionMarkup } from "../lib/markups";
import { MODELS, clampEffort, effortsFor, groupModels, pickerModels } from "../lib/models";
import { PROVIDERS, getProvider } from "../agent/providers";
import { isHosted, rpc } from "../lib/bridge";
import type { Attachment, Effort, FileEntry, ImageMediaType, Wake } from "../lib/types";

/** Below these composer widths (CSS px inside the composer's own zoom) labels give way to icons, so the row never overflows. */
const NARROW = "@max-[360px]:hidden";
const NARROWER = "@max-[440px]:hidden";
const TINY = "@max-[300px]:hidden";

export function Composer() {
  const thread = useStore(currentThread);
  const key = useStore(draftKey);
  const draft = useStore((s) => s.drafts[key] ?? "");
  const attachments = useStore((s) => s.attachments[key] ?? EMPTY);
  const running = useStore((s) => (thread ? !!s.runs[thread.id] : false));
  // The agent is waiting on a question: what's sent here answers it.
  const asking = useStore((s) => {
    const q = thread ? s.runs[thread.id]?.question : null;
    return !!q && !q.form && !q.secret; // a key goes in its own masked card, never here
  });
  // ...a selection (ask_selection): a message here is a note and counts as Done.
  const selecting = useStore((s) => !!(thread && s.runs[thread.id]?.question?.selection));
  const settings = useStore((s) => s.settings);
  const hasDoc = useStore((s) => !!s.docs[s.activeDocKey ?? ""]);
  const markupActive = useStore((s) => s.markup.active);
  const watch = useStore((s) => s.watch);
  const canWatch = useStore((s) => s.docs[s.activeDocKey ?? ""]?.type === "part" || s.docs[s.activeDocKey ?? ""]?.type === "assembly");
  const focusTick = useStore((s) => s.focusTick);
  const overlayCount = useStore((s) => s.overlayCount);
  const voicePhase = useStore((s) => s.voice.phase);
  // Push-to-talk is ready when it's on and its speech model is downloaded.
  const talkKey = useStore((s) =>
    s.settings.voiceEnabled !== false && s.voiceStatus?.available && s.voiceStatus.models.some((m) => m.id === s.voiceStatus?.model && m.installed)
      ? talkKeyName(s.settings.voiceKey)
      : null,
  );
  const tags = useStore((s) => s.tags[key] ?? (EMPTY as unknown as Tag[]));
  const commands = useStore((s) => s.commands);
  const skills = useStore((s) => s.skills);
  const agents = useStore((s) => s.agents);
  const plugins = useStore((s) => s.plugins);
  const userTools = useStore((s) => s.userTools);
  const keySources = useStore((s) => s.apiKeySources);
  const { setDraft, submitInput, stop, attachView, addAttachment, removeAttachment, setThreadModel, setThreadEffort, startMarkup, pickFiles, startWatch, addTag, removeTag } =
    useStore.getState();
  const ref = useRef<HTMLTextAreaElement>(null);
  const [dragging, setDragging] = useState(false);
  // The "/" and "@" popup: where the caret is, which row is highlighted, and the text it was closed for (Esc).
  const [caret, setCaret] = useState(0);
  const [active, setActive] = useState(0);
  const [closedFor, setClosedFor] = useState<string | null>(null);
  const sources: CommandSources = useMemo(
    () => ({ commands, skills, tools: userTools, plugins, agents, models: MODELS.filter((m) => (keySources[m.provider] ?? "none") !== "none").map((m) => m.label) }),
    [commands, skills, userTools, plugins, agents, keySources],
  );
  const menu = useMemo((): { prefix: "/" | "@"; title: string; rows: MenuRow[]; start: number } | null => {
    if (closedFor === draft || markupActive) return null;
    const slash = slashQuery(draft, caret);
    if (slash !== null) {
      const rows = slashRows(sources, slash).map((r, i): MenuRow => ({ key: `${r.kind}:${r.name}:${i}`, ...r }));
      return { prefix: "/", title: "Commands, skills, tools and workflows", rows, start: 0 };
    }
    const at = atQuery(draft, caret);
    if (at) {
      const rows = tagRows(sources, at.query).map((r, i): MenuRow => ({ key: `${r.kind}:${r.name}:${i}`, kind: r.kind, name: r.name, description: r.description, source: r.kind }));
      return { prefix: "@", title: "Tag a plugin, tool, skill or agent", rows, start: at.start };
    }
    return null;
  }, [draft, caret, closedFor, sources, markupActive]);
  useEffect(() => setActive(0), [menu?.prefix, menu?.rows.length]);

  const model = thread?.model ?? settings.defaultModel;
  const effort = thread?.effort ?? settings.defaultEffort;
  // Models of the providers with a key (and this chat's own), under provider headers when there are several.
  const modelItems = groupModels(pickerModels(model, (p) => (keySources[p] ?? "none") !== "none"), PROVIDERS.map((p) => p.id)).flatMap((g) =>
    g.models.map((m) => ({ id: m.id, label: m.label, note: m.note, group: g.header ? getProvider(g.provider).label : undefined })),
  );
  const reading = attachments.some((a) => a.kind === "model" && a.status === "reading");
  // While the agent works, Enter queues the message (see QueuedList).
  const canSend = !markupActive && !reading && (draft.trim().length > 0 || attachments.length > 0);

  /** Take a popup row: "/name " (a command without arguments runs at once with Enter), or an @ tag chip. */
  const pick = (index: number, run: boolean) => {
    if (!menu) return;
    const row = menu.rows[index];
    if (!row) return;
    if (menu.prefix === "@") {
      // The "@word" leaves the text (it follows a space or the start, so no words join); the tag becomes a chip.
      const next = draft.slice(0, menu.start) + draft.slice(caret).replace(/^\s*/, "");
      setDraft(next);
      addTag({ kind: row.kind as Tag["kind"], name: row.name });
      setCaret(menu.start);
      requestAnimationFrame(() => ref.current?.setSelectionRange(menu.start, menu.start));
      return;
    }
    const rest = draft.replace(/^\/\S*\s?/, "");
    if (run && row.kind === "builtin" && !row.hint && !rest.trim()) {
      void submitInput(`/${row.name}`);
      return;
    }
    const next = `/${row.name} ${rest}`;
    setDraft(next);
    const at = row.name.length + 2;
    setCaret(at);
    requestAnimationFrame(() => ref.current?.setSelectionRange(at, at));
  };

  // Files dropped anywhere on the panel must not make the WebView navigate to them.
  useEffect(() => {
    const block = (e: DragEvent) => {
      if (e.dataTransfer?.types.includes("Files")) e.preventDefault();
    };
    window.addEventListener("dragover", block);
    window.addEventListener("drop", block);
    return () => {
      window.removeEventListener("dragover", block);
      window.removeEventListener("drop", block);
    };
  }, []);

  useEffect(() => {
    ref.current?.focus();
    const el = ref.current;
    if (el) el.setSelectionRange(el.value.length, el.value.length);
  }, [focusTick, key]);

  // Auto-grow up to ~8 lines - again when the panel's width changes (text rewraps).
  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    const fit = () => {
      el.style.height = "auto";
      el.style.height = Math.min(el.scrollHeight, 160) + "px";
    };
    fit();
    let width = el.clientWidth;
    const ro = new ResizeObserver(() => {
      if (el.clientWidth === width) return;
      width = el.clientWidth;
      fit();
    });
    ro.observe(el);
    return () => ro.disconnect();
  }, [draft]);

  const submit = () => {
    if (canSend) void submitInput(draft);
  };

  const onPaste = (e: React.ClipboardEvent) => {
    for (const item of Array.from(e.clipboardData.items)) {
      if (!item.type.startsWith("image/")) continue;
      const file = item.getAsFile();
      if (!file) continue;
      e.preventDefault();
      readImage(file).then((a) => a && addAttachment(a));
    }
  };

  const onDrop = (e: React.DragEvent) => {
    setDragging(false);
    const files = Array.from(e.dataTransfer.files);
    if (files.length === 0) return;
    e.preventDefault();
    const webview = (window as any).chrome?.webview;
    if (isHosted && webview?.postMessageWithAdditionalObjects) {
      // The page can't see file paths; WebView2 passes the File objects to the add-in, which reads them.
      webview.postMessageWithAdditionalObjects({ kind: "files" }, e.dataTransfer.files);
      return;
    }
    // Outside SolidWorks (UI development): read images here, let the mock host stand in for the rest.
    for (const f of files) {
      if (f.type.startsWith("image/")) readImage(f).then((a) => a && addAttachment(a));
    }
    const others = files.filter((f) => !f.type.startsWith("image/")).map((f) => f.name);
    if (others.length) rpc<FileEntry[]>("files.entries", { paths: others }).then((entries) => useStore.getState().addFileEntries(entries));
  };

  return (
    <div
      className="sw-etch @container relative min-w-0 shrink-0 bg-panel px-1.5 pb-1.5 pt-1.5"
      onDragOver={(e) => {
        if (!e.dataTransfer.types.includes("Files")) return;
        e.preventDefault();
        setDragging(true);
      }}
      onDragLeave={(e) => {
        if (!e.currentTarget.contains(e.relatedTarget as Node)) setDragging(false);
      }}
      onDrop={onDrop}
    >
      {dragging && (
        <div className="pointer-events-none absolute inset-1 z-10 flex items-center justify-center rounded-[3px] border-2 border-dashed border-accent bg-panel/85 text-[11.5px] font-semibold text-text">
          Drop images, PDFs or 3D models (STL, STEP, 3MF, IGES, SLDPRT...)
        </div>
      )}
      {thread && <QueuedList threadId={thread.id} running={running} />}
      {menu && <SlashMenu rows={menu.rows} active={Math.min(active, Math.max(0, menu.rows.length - 1))} prefix={menu.prefix} title={menu.title} onPick={(i) => pick(i, true)} onHover={setActive} />}
      {voicePhase !== "idle" && <VoiceBar />}
      {watch.active || watch.busy ? <WatchBar /> : markupActive ? <MarkupBar /> : hasDoc && voicePhase === "idle" && <SelectionChips />}

      <div className="sw-input" data-tour="composer">
        {attachments.length > 0 && (
          <div className="flex flex-wrap gap-1.5 px-1.5 pt-1.5">
            {attachments.map((a) => (
              <AttachmentChip
                key={a.id}
                a={a}
                onRemove={() => removeAttachment(a.id)}
                onMention={(n) => {
                  setDraft(mentionMarkup(draft, n));
                  useStore.setState((s) => ({ focusTick: s.focusTick + 1 }));
                }}
              />
            ))}
          </div>
        )}
        {tags.length > 0 && (
          <div className="flex flex-wrap gap-1 px-1.5 pt-1.5">
            {tags.map((t, i) => (
              <span
                key={`${t.kind}:${t.name}`}
                className="flex h-[20px] max-w-full min-w-0 items-center gap-1 rounded-[2px] border border-accent/50 bg-accent-soft px-1.5 text-[10.5px] text-text"
                title={`The agent prefers this ${t.kind}'s tools and instructions for this message`}
              >
                {t.kind === "plugin" ? <IconPlugin size={11} /> : null}
                <span className="truncate">@{t.name}</span>
                <span className="shrink-0 text-text-3">{t.kind === "plugin" ? "" : t.kind}</span>
                <button type="button" onClick={() => removeTag(i)} title="Remove the tag" className="shrink-0 opacity-60 hover:opacity-100">
                  <X size={9} />
                </button>
              </span>
            ))}
          </div>
        )}
        <textarea
          ref={ref}
          rows={2}
          value={draft}
          onChange={(e) => {
            setDraft(e.target.value);
            setCaret(e.target.selectionStart ?? e.target.value.length);
          }}
          onSelect={(e) => setCaret(e.currentTarget.selectionStart ?? 0)}
          onPaste={onPaste}
          onKeyDown={(e) => {
            if (menu && !e.nativeEvent.isComposing) {
              const n = menu.rows.length;
              if (e.key === "ArrowDown" || e.key === "ArrowUp") {
                e.preventDefault();
                if (n) setActive((a) => (a + (e.key === "ArrowDown" ? 1 : n - 1)) % n);
                return;
              }
              if ((e.key === "Enter" && !e.shiftKey) || e.key === "Tab") {
                if (n) {
                  e.preventDefault();
                  pick(Math.min(active, n - 1), e.key === "Enter");
                  return;
                }
              }
              if (e.key === "Escape") {
                e.preventDefault();
                setClosedFor(draft);
                return;
              }
            }
            if (e.key === "Enter" && !e.shiftKey && !e.nativeEvent.isComposing) {
              e.preventDefault();
              submit();
            }
          }}
          placeholder={
            selecting
              ? "Select in SolidWorks and press Done above - or type a note (sends as Done)…"
              : asking
              ? talkKey
                ? `Answer the agent's question above… or hold ${talkKey} and say it`
                : "Answer the agent's question above…"
              : running
              ? "Add a follow-up - it's queued until the agent finishes…"
              : hasDoc
                ? talkKey
                  ? `Ask a question or describe a change… or hold ${talkKey} and talk`
                  : "Ask a question or describe a change… / for commands, @ to tag"
                : "Ask anything, or open a document first… / for commands"
          }
          className="block max-h-[160px] min-h-[36px] w-full resize-none bg-transparent px-2 pb-1 pt-1.5 text-[12px] leading-[1.42] text-text outline-none placeholder:text-text-3"
        />
      </div>

      <div className="mt-1 flex min-w-0 flex-wrap items-center gap-x-0.5 gap-y-1">
        <span className="flex min-w-0 items-center gap-0.5">
          <span className="inline-flex min-w-0 items-center gap-0.5" data-tour="model">
            <Dropdown title="Model" icon={<IconAgent size={14} />} labelClassName={NARROW} value={model} items={modelItems} onChange={setThreadModel} disabled={running} />
            <Dropdown<Effort> title="Effort" icon={<IconEffort size={14} />} labelClassName={NARROW} value={clampEffort(effort, model)} items={effortsFor(model).map((e) => ({ id: e.id, label: e.label, note: e.note }))} onChange={setThreadEffort} disabled={running} />
          </span>
          <span className="inline-flex" data-tour="tools">
            <ToolsPalette disabled={running || markupActive} labelClassName={NARROWER} />
          </span>
        </span>
        <span className="ml-auto flex shrink-0 items-center gap-0.5">
        {overlayCount > 0 && (
          <button type="button" className="sw-btn mr-1 !h-[22px] !px-1.5 text-[11px]" onClick={() => useStore.getState().clearOverlay()} title="Clear overlay: remove what the agent drew on your view">
            <IconEye size={12} /> <span className={NARROWER}>Clear overlay</span>
          </button>
        )}
        <IconButton title="Attach images, PDFs or 3D models (or drop them here)" onClick={pickFiles} disabled={markupActive}>
          <IconAttach />
        </IconButton>
        {hasDoc && (
          <>
            {canWatch && (
              <IconButton
                title={watch.active ? "Watching what you do - stop from the bar above" : "Watch me: record what you do in this document, then turn it into a tool or workflow"}
                active={watch.active}
                onClick={() => (watch.active ? undefined : startWatch())}
                disabled={markupActive || watch.busy}
              >
                <IconWatch recording={watch.active} />
              </IconButton>
            )}
            <IconButton tour="markup" title="Draw on the view (markup)" active={markupActive} onClick={() => (markupActive ? undefined : startMarkup())}>
              <IconMarkup />
            </IconButton>
            <IconButton title="Attach a screenshot of the current view" onClick={attachView} disabled={markupActive}>
              <IconCamera />
            </IconButton>
          </>
        )}
        {running ? (
          <>
            {canSend && (
              <button
                type="button"
                onClick={submit}
                title={asking ? "Answer the agent's question with this (Enter)" : "Queue this message (Enter): it's sent when the agent finishes"}
                className="sw-btn sw-btn-primary mr-1 !h-6 !px-2.5"
              >
                <IconSend size={13} /> <span className={TINY}>{asking ? "Answer" : "Queue"}</span>
              </button>
            )}
            <button type="button" onClick={() => stop()} title="Stop" className="sw-btn !h-6 !px-2">
              <IconStop size={13} /> <span className={TINY}>Stop</span>
            </button>
          </>
        ) : (
          <button type="button" onClick={submit} disabled={!canSend} title={reading ? "Waiting for the 3D model to be read" : "Send (Enter)"} className="sw-btn sw-btn-primary !h-6 !px-2.5">
            <IconSend size={13} /> <span className={TINY}>Send</span>
          </button>
        )}
        </span>
      </div>
    </div>
  );
}

/**
 * Follow-ups typed while the agent works. They go out together when the turn ends; "Send now" hands one to the
 * agent at its next step without stopping it. Edit puts it back in the composer.
 */
function QueuedList({ threadId, running }: { threadId: string; running: boolean }) {
  const queue = useStore((s) => s.queue[threadId] ?? (EMPTY as unknown as QueuedMessage[]));
  const wakes = useStore((s) => s.wakes[threadId] ?? (EMPTY as unknown as Wake[]));
  const { queueSendNow, queueEdit, queueRemove } = useStore.getState();
  if (queue.length === 0 && wakes.length === 0) return null;
  return (
    <div className="mb-1.5 flex flex-col gap-1">
      {wakes.length > 0 && (
        <div className="sw-rollout flex items-start gap-1.5 px-2 py-1 text-[11.5px]" title={wakes.map((w) => w.text).join("\n\n")}>
          <span className="mt-px shrink-0">
            <IconPlugin size={13} />
          </span>
          <div className="min-w-0 flex-1">
            <div className="line-clamp-2 break-words text-text">{wakes[wakes.length - 1].text}</div>
            <div className="text-[10.5px] text-text-3">
              {wakes.length > 1 ? `${wakes.length} plugin events` : wakes[0].plugin} -{" "}
              {running ? "goes to the agent when this turn ends" : "goes to the agent with your next message"}
            </div>
          </div>
        </div>
      )}
      {queue.map((m) => (
        <div key={m.id} className="sw-rollout flex items-start gap-1.5 px-2 py-1 text-[11.5px]">
          <div className="min-w-0 flex-1">
            <div className="line-clamp-2 whitespace-pre-wrap break-words text-text">{m.text || `${m.attachments.length} attachment${m.attachments.length === 1 ? "" : "s"}`}</div>
            <div className="text-[10.5px] text-text-3">
              {m.now ? "Sending at the agent's next step…" : running ? (m.staged || m.run ? "Runs when the agent finishes" : "Queued - sent when the agent finishes") : "Waiting to be sent"}
              {m.text && m.attachments.length > 0 && ` · ${m.attachments.length} attachment${m.attachments.length === 1 ? "" : "s"}`}
            </div>
          </div>
          {/* A queued tool or workflow run has its own settings: it can't join the run under way (store.queueSendNow). */}
          {!m.now && !(running && (m.staged || m.run)) && (
            <button className="sw-btn !h-5 shrink-0 !px-1.5 text-[11px]" onClick={() => queueSendNow(threadId, m.id)} title="Give this to the agent at its next step, without stopping it">
              Send now
            </button>
          )}
          <button className="sw-tool !h-5 !min-w-5 shrink-0" onClick={() => queueEdit(threadId, m.id)} title="Edit (moves it back into the message box)">
            <IconEdit size={11} />
          </button>
          <button className="sw-tool !h-5 !min-w-5 shrink-0" onClick={() => queueRemove(threadId, m.id)} title="Remove from the queue">
            <IconCancel size={10} />
          </button>
        </div>
      ))}
    </div>
  );
}

/** One chip per selected entity; click a chip to leave it out of the next message. */
function SelectionChips() {
  const selection = useStore((s) => s.selection[s.activeDocKey ?? ""] ?? EMPTY);
  const includeSelection = useStore((s) => s.includeSelection);
  const excluded = useStore((s) => s.excludedSel);
  const agentMade = useStore((s) => isAgentSelection(s, s.activeDocKey ?? ""));
  const agentWorking = useStore((s) => Object.keys(s.runs).length > 0);
  const { setIncludeSelection, toggleSelItem } = useStore.getState();
  // Selections the agent makes while working (or leaves behind) aren't the user's - don't offer them.
  if (selection.length === 0 || agentMade || agentWorking) return null;

  const shown = selection.slice(0, 8);
  return (
    <div className="mb-1 flex flex-wrap items-center gap-1 px-0.5">
      <button
        type="button"
        onClick={() => setIncludeSelection(!includeSelection)}
        aria-pressed={includeSelection}
        title={includeSelection ? "The agent sees what you selected. Click to stop sharing the selection." : "Click to share your selection with the agent"}
        className={`sw-tool !h-[20px] !px-1 text-[10.5px] ${includeSelection ? "" : "opacity-60"}`}
      >
        <IconSelect size={13} />
        {includeSelection ? "Selection" : "Selection off"}
      </button>
      {includeSelection &&
        shown.map((item, i) => {
          const off = excluded.includes(i);
          return (
            <button
              key={i}
              type="button"
              onClick={() => toggleSelItem(i)}
              title={off ? "Click to include this in your message" : "Included in your message. Click to leave it out."}
              className={`flex h-[20px] max-w-[170px] items-center gap-1 rounded-[2px] border px-1.5 text-[10.5px] ${
                off ? "border-line text-text-3 line-through opacity-70" : "border-accent/50 bg-accent-soft text-text"
              }`}
            >
              <span className="truncate">
                {pretty(item.type)}
                {item.label ? ` · ${item.label}` : ""}
              </span>
              {!off && <X size={9} className="shrink-0 opacity-60" />}
            </button>
          );
        })}
      {includeSelection && selection.length > shown.length && <span className="text-[10.5px] text-text-3">+{selection.length - shown.length} more</span>}
    </div>
  );
}

const TOOLS: { id: MarkupTool; label: string; icon: React.ReactNode }[] = [
  { id: "pen", label: "Pen", icon: <IconToolPen size={14} /> },
  { id: "arrow", label: "Arrow", icon: <IconToolArrow size={14} /> },
  { id: "circle", label: "Circle", icon: <IconToolCircle size={14} /> },
  { id: "highlighter", label: "Highlighter", icon: <IconToolHighlighter size={14} /> },
];

const clock = (ms: number) => {
  const s = Math.max(0, Math.round(ms / 1000));
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
};

/** While the user holds the talk key: the words as they're recognised and what they did meanwhile. */
function VoiceBar() {
  const voice = useStore((s) => s.voice);
  const key = useStore((s) => talkKeyName(s.settings.voiceKey));
  const answering = useStore((s) => {
    const t = currentThread(s);
    const q = t ? s.runs[t.id]?.question : null;
    return !!q && !q.form && !q.secret;
  });
  const { cancelVoice } = useStore.getState();
  const label =
    voice.phase === "listening" ? `Listening - let go of ${key} to ${answering ? "answer the question" : "send"}` : voice.phase === "transcribing" ? "Finishing the transcript…" : "Pen is on - press Esc in SolidWorks to send";
  const bars = [0.55, 0.85, 1, 0.8, 0.5];
  return (
    <div className="sw-rollout mb-1.5">
      <div className="sw-rollout-head !font-normal">
        <IconMic size={14} live={voice.phase === "listening"} />
        {voice.phase === "listening" ? (
          <span className="flex h-3 items-end gap-[2px]" aria-hidden>
            {bars.map((b, i) => (
              <span key={i} className="w-[3px] rounded-[1px] bg-[#E5302A] transition-[height] duration-100" style={{ height: `${Math.max(2, Math.min(12, 2 + voice.level * 12 * b))}px` }} />
            ))}
          </span>
        ) : voice.phase === "transcribing" ? (
          <Spinner size={12} />
        ) : null}
        <span className="flex-1 truncate text-[11.5px] font-semibold">{label}</span>
        <button type="button" className="sw-btn !h-5 !px-1.5 text-[11px]" onClick={cancelVoice} title="Discard this voice message (or press Esc while holding the key)">
          <IconCancel size={9} /> Discard
        </button>
      </div>
      <div className="selectable max-h-[90px] min-h-[22px] overflow-y-auto px-2 py-1 text-[12px] leading-[1.4] text-text">
        {voice.text || <span className="text-text-3">Speak - your words appear here. Select, sketch, turn the model or draw with the pen while you talk.</span>}
      </div>
      {voice.events.length > 0 && (
        <div className="flex flex-wrap gap-1 px-2 pb-1.5">
          {voice.events.slice(-8).map((e, i) => (
            <span key={i} className="flex h-[18px] max-w-[190px] items-center gap-1 rounded-[2px] border border-line bg-card-2 px-1 text-[10px] text-text-2" title={e.summary}>
              <span className="tabular-nums text-text-3">{clock(e.t)}</span>
              <span className="truncate">{e.summary}</span>
            </span>
          ))}
        </div>
      )}
    </div>
  );
}

/** Shown while the markup pen is on over the SolidWorks view. */
function MarkupBar() {
  const markup = useStore((s) => s.markup);
  const { setMarkup, markupUndo, markupClear, cancelMarkup, finishMarkup } = useStore.getState();
  return (
    <div className="sw-rollout mb-1.5">
      <div className="sw-rollout-head !font-normal">
        <IconMarkup size={14} />
        <span className="flex-1 text-[11.5px] font-semibold" title="Draw with the left button. Turn, pan and zoom the model as usual - each view you mark is kept. Esc in SolidWorks when you're done.">
          Pen - turn the model to mark more views
        </span>
        <span className="text-[10.5px] text-text-3">
          {markup.frames > 0 && `${markup.frames} view${markup.frames === 1 ? "" : "s"} kept · `}
          {markup.strokes} mark{markup.strokes === 1 ? "" : "s"}
        </span>
      </div>
      <div className="flex items-center gap-0.5 px-1 pt-1">
        {TOOLS.map((t) => (
          <button key={t.id} type="button" title={t.label} aria-pressed={markup.tool === t.id} onClick={() => setMarkup({ tool: t.id })} className="sw-tool" style={{ color: markup.color }}>
            {t.icon}
          </button>
        ))}
        <span className="mx-1 h-4 w-px bg-line" />
        {MARKUP_COLORS.map((c) => (
          <button
            key={c}
            type="button"
            title="Colour"
            aria-pressed={markup.color === c}
            onClick={() => setMarkup({ color: c })}
            className="sw-tool !min-w-[20px] !p-0"
          >
            <span className="h-3 w-3 rounded-full border border-black/40" style={{ background: c }} />
          </button>
        ))}
      </div>
      <div className="flex flex-wrap items-center gap-0.5 px-1 pb-1 pt-0.5">
        <button type="button" title="Undo last mark (Ctrl+Z)" onClick={markupUndo} className="sw-tool">
          <IconUndo size={14} />
        </button>
        <button type="button" title="Clear all marks" onClick={markupClear} className="sw-tool text-[11px]">
          Clear
        </button>
        <div className="flex-1" />
        <button type="button" onClick={cancelMarkup} className="sw-btn" title="Discard the markup">
          <IconCancel size={10} /> Discard
        </button>
        <button type="button" onClick={finishMarkup} className="sw-btn" title="Done: attach the marked-up views to your message (Esc or Enter in SolidWorks)">
          <IconOk size={11} /> Done
        </button>
      </div>
    </div>
  );
}

/** One attached item above the text box: an image, a PDF, a 3D model (its six views once read) or a Watch me log. */
function AttachmentChip({ a, onRemove, onMention }: { a: Attachment; onRemove: () => void; onMention: (n: number) => void }) {
  let body: React.ReactNode;
  let title = a.name;
  // A markup (M1, M2, ...): its name on the thumbnail, and a click puts that name in the message.
  const markup = a.kind === "image" ? markupNumber(a.name) : null;
  switch (a.kind) {
    case "image":
      if (markup !== null) title = `M${markup}: click to mention it in your message ("make the hole in M${markup} deeper")`;
      body = (
        <>
          <img src={`data:${a.mediaType};base64,${a.data}`} alt={a.name} className="h-12 w-auto" />
          {markup !== null ? (
            <span className="absolute left-0.5 top-0.5 rounded-[2px] bg-black/65 px-1 text-[10px] font-semibold leading-[14px] text-white">M{markup}</span>
          ) : (
            a.name.startsWith(MARKUP_NAME) && <span className="absolute bottom-0 left-0 right-0 bg-black/55 px-1 text-[9.5px] text-white">{a.name}</span>
          )}
        </>
      );
      break;
    case "pdf":
      body = <ChipLabel icon={<IconDocs size={18} />} name={a.name} note="PDF" />;
      break;
    case "model":
      title = a.entry?.summary ?? (a.status === "reading" ? `Reading ${a.name}: rendering six views and measuring it...` : a.entry?.error ?? a.name);
      body =
        a.status === "ready" && a.entry?.views ? (
          <>
            <img src={`data:${a.entry.viewsMediaType ?? "image/jpeg"};base64,${a.entry.views}`} alt={a.name} className="h-12 w-auto" />
            <span className="absolute bottom-0 left-0 right-0 truncate bg-black/55 px-1 text-[9.5px] text-white">{a.name}</span>
          </>
        ) : (
          <ChipLabel
            icon={a.status === "reading" ? <Spinner size={16} /> : a.status === "error" ? <IconWarning size={16} /> : <IconModelFile size={18} />}
            name={a.name}
            note={a.status === "reading" ? "Reading model..." : a.status === "error" ? "Couldn't read - path only" : (a.entry?.format ?? "3D model")}
          />
        );
      break;
    case "voice": {
      const first = a.voice.events.find((e) => e.image);
      const images = a.voice.events.filter((e) => e.image).length;
      title = `Voice message (${a.name.replace("Voice ", "")}): what you did while talking goes along with your words - ${a.voice.events.length} event${a.voice.events.length === 1 ? "" : "s"}, ${images} image${images === 1 ? "" : "s"}`;
      body = (
        <>
          {first ? <img src={`data:${first.mediaType ?? "image/jpeg"};base64,${first.image}`} alt="" className="h-12 w-auto opacity-90" /> : <div className="h-12 w-[72px] bg-card-2" />}
          <span className="absolute bottom-0 left-0 right-0 flex items-center gap-1 bg-black/60 px-1 text-[9.5px] text-white">
            <IconMic size={11} /> {a.name.replace("Voice ", "")}
            {images > 0 && ` · ${images}`}
          </span>
        </>
      );
      break;
    }
    case "watch":
      title = a.watch.text;
      body = (
        <>
          {a.watch.after || a.watch.before ? (
            <img src={`data:image/jpeg;base64,${a.watch.after ?? a.watch.before}`} alt="" className="h-12 w-auto opacity-90" />
          ) : (
            <div className="h-12 w-[72px] bg-card-2" />
          )}
          <span className="absolute bottom-0 left-0 right-0 flex items-center gap-1 bg-black/60 px-1 text-[9.5px] text-white">
            <IconWatch size={11} /> {a.watch.steps} step{a.watch.steps === 1 ? "" : "s"}
          </span>
        </>
      );
      break;
  }
  return (
    <div
      className={`group relative overflow-hidden rounded-[2px] border border-line bg-card shadow-[var(--shadow)]${markup !== null ? " cursor-pointer hover:border-accent" : ""}`}
      title={title}
      onClick={markup !== null ? () => onMention(markup) : undefined}
    >
      {body}
      <button
        onClick={(e) => {
          e.stopPropagation();
          onRemove();
        }}
        title="Remove"
        className="absolute right-0.5 top-0.5 hidden h-4 w-4 items-center justify-center rounded-full bg-black/60 text-white group-hover:flex"
      >
        <X size={10} />
      </button>
    </div>
  );
}

function ChipLabel({ icon, name, note }: { icon: React.ReactNode; name: string; note: string }) {
  return (
    <div className="flex h-12 max-w-[190px] items-center gap-1.5 px-2">
      <span className="shrink-0">{icon}</span>
      <span className="min-w-0">
        <span className="block truncate text-[11px] text-text">{name}</span>
        <span className="block truncate text-[10px] text-text-3">{note}</span>
      </span>
    </div>
  );
}

/** Shown while "Watch me" records: the steps so far, live, and Stop / Discard. */
function WatchBar() {
  const watch = useStore((s) => s.watch);
  const { stopWatch, cancelWatch } = useStore.getState();
  const [, tick] = useState(0);
  useEffect(() => {
    const t = setInterval(() => tick((n) => n + 1), 1000);
    return () => clearInterval(t);
  }, []);
  const list = useRef<HTMLDivElement>(null);
  useEffect(() => {
    list.current?.scrollTo({ top: list.current.scrollHeight });
  }, [watch.entries.length]);
  const seconds = Math.max(0, Math.round((Date.now() - watch.startedAt) / 1000));
  const clock = `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, "0")}`;
  return (
    <div className="sw-rollout mb-1.5">
      <div className="sw-rollout-head !font-normal">
        <span className="relative flex h-2.5 w-2.5 shrink-0">
          {!watch.busy && <span className="absolute inline-flex h-full w-full animate-ping rounded-full bg-[#E5302A] opacity-60" />}
          <span className="relative inline-flex h-2.5 w-2.5 rounded-full bg-[#E5302A]" />
        </span>
        <span className="min-w-0 flex-1 truncate text-[11.5px] font-semibold">
          {watch.busy ? "Finishing the recording..." : `Watching what you do${watch.doc ? ` in ${watch.doc}` : ""}`}
        </span>
        <span className="text-[10.5px] tabular-nums text-text-3">{clock}</span>
      </div>
      <div ref={list} className="max-h-[92px] overflow-y-auto px-2 py-1 text-[10.5px] leading-[1.45] text-text-2">
        {watch.entries.length === 0 ? (
          <div className="text-text-3">Model as usual - features you add, edit, rename or delete, and what you select, are recorded here.</div>
        ) : (
          watch.entries.map((e, i) => (
            <div key={e.id} className="flex gap-1.5">
              <span className="w-4 shrink-0 text-right text-text-3">{i + 1}.</span>
              <span className={`min-w-0 ${e.kind === "select" ? "text-text-3" : ""}`}>
                {e.text}
                {e.byAgent ? <span className="text-text-3"> (agent)</span> : null}
              </span>
            </div>
          ))
        )}
      </div>
      <div className="flex flex-wrap items-center gap-0.5 px-1 pb-1 pt-0.5">
        <span className="px-1 text-[10.5px] text-text-3">
          {watch.entries.length} step{watch.entries.length === 1 ? "" : "s"}
        </span>
        <div className="flex-1" />
        <button type="button" onClick={cancelWatch} disabled={watch.busy} className="sw-btn" title="Stop and throw the recording away">
          <IconCancel size={10} /> Discard
        </button>
        <button
          type="button"
          onClick={stopWatch}
          disabled={watch.busy}
          className="sw-btn sw-btn-primary"
          title="Stop and attach the log, with the view before and after, to your next message"
        >
          <IconOk size={11} /> Stop &amp; attach
        </button>
      </div>
    </div>
  );
}

function pretty(type: string) {
  const t = type.toLowerCase().replace(/_/g, " ");
  return t.charAt(0).toUpperCase() + t.slice(1);
}

async function readImage(file: File): Promise<Attachment | null> {
  const mediaType = file.type as ImageMediaType;
  if (!["image/jpeg", "image/png", "image/webp", "image/gif"].includes(mediaType)) return null;
  const data = await new Promise<string>((resolve, reject) => {
    const r = new FileReader();
    r.onload = () => resolve(String(r.result).split(",")[1] ?? "");
    r.onerror = () => reject(r.error);
    r.readAsDataURL(file);
  });
  return { id: crypto.randomUUID(), kind: "image", name: file.name || "Pasted image", mediaType, data };
}
