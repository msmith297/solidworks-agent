import { create } from "zustand";
import { on, rpc } from "./lib/bridge";
import { questionTimerStep } from "./lib/questionTimeout";
import { syncRibbonIcons } from "./lib/toolIcons";
import { DEFAULT_SETTINGS, SETTINGS_VERSION, effortsFor, findModel, inferProvider, keyedDefaultModel, modelOption, pickToolProvider, pickerModels, resolveModel, tierModel } from "./lib/models";
import { PROVIDERS, getProvider, providerErrorText } from "./agent/providers";
import { ProviderError } from "./agent/providers/errors";
import { unreadableNote } from "./agent/providers/media";
import type {
  Attachment,
  ChatData,
  ResumePanel,
  ContentBlockParam,
  DocInfo,
  Effort,
  FileEntry,
  ImageMediaType,
  HostState,
  MessageParam,
  SelectionItem,
  Settings,
  SwThemeInfo,
  CrashInfo,
  ToolRunPayload,
  FormSpec,
  UpdateInfo,
  UserToolInfo,
  Thread,
  VoicePayload,
  VoiceStatus,
  ToolDef,
  ToolRunResult,
  TurnMeta,
  Usage,
  WatchEntry,
  WatchResult,
  SelectionAsk,
  SelectionKind,
  ToolRunSticky,
  AgentInfo,
  CommandInfo,
  ExpandedCommand,
  HookEvent,
  HookResult,
  MarketplaceInfo,
  McpServerStatus,
  PluginInfo,
  SessionInfo,
  SkillInfo,
  KnowledgeIndex,
  KeySource,
  ProviderId,
  Wake,
} from "./lib/types";
import { recordSession } from "./lib/sessions";
import { refBase, selectionAnswer, type SnapshotItem } from "./lib/selection";
import { DEFAULT_STEPS, LONG_RUN_STEPS, clampSteps, runReportsDone, stickyState, withRunDoc } from "./lib/toolRun";
import { profileLine, splitProfileArg } from "./lib/toolFormLogic";
import { WRITE_TOOLS, runAgent, type ApprovalDecision, type ApprovalRequest, type LiveBlock, type SubAgentProgress } from "./agent/runAgent";
import { BUILTIN_COMMANDS, commandHead, parseSlash, resolveSlash, tagLines, type CommandSources, type TagKind } from "./lib/commands";
import { EVENT_TAG, HOOK_TAG, autoTurnAllowed, hasHook, hookContextText, systemEventText, wakeApproval, wakeTrusted, wakesMayFollow } from "./lib/hooks";
import { SECRET_SAVED, SECRET_SKIPPED, secretTarget, type SecretAsk } from "./lib/creator";
import { NO_WRITTEN_REPLY, linkAnswerText, linkState } from "./lib/localLink";
import type { MemoryIndex } from "./lib/memory";

export interface ChatSlot {
  docKey: string;
  docId: string;
  data: ChatData;
  recovered: boolean;
}

/** A follow-up typed while the agent was working: sent when the run ends, or at its next step with "Send now". */
export interface QueuedMessage {
  id: string;
  text: string;
  attachments: Attachment[];
  now?: boolean;
  /** A tool run waiting for the agent: its staged prompt and run settings (text is then just its label). */
  staged?: { blocks: ContentBlockParam[]; refs: Record<string, string>; run: RunOverride };
  /** A workflow run or a new tool waiting for the agent: its own settings (it isn't a follow-up of the current run). */
  run?: RunOverride;
}

/** Settings for one run that differ from the chat's own (a tool's intelligence level, auto-approval). */
export interface RunOverride {
  model?: string;
  effort?: Effort;
  autoApprove?: boolean;
  label?: string;
  /** API steps before the turn stops (a tool's max_steps); default 200. */
  maxSteps?: number;
  /** A long tool run: 1-hour prompt cache. */
  longRun?: boolean;
  /** Set for a tool or workflow run (its name): a long one keeps its settings for follow-ups, any other ends that. */
  tool?: string;
  /** Not a follow-up (e.g. making a new tool): ends a long tool run's follow-up settings and uses the chat's own. */
  fresh?: boolean;
  /** A plugin command's run ("plugin:command"): its model applies to this run only, not to a chat it starts. */
  command?: string;
  /** What the user typed (a slash command): back into the composer if a hook blocks the prompt. */
  input?: string;
  /** This run continues a turn a Stop hook blocked: its own Stop then fires with stop_hook_active. */
  continuation?: boolean;
  /** Set by runSettings: this run continues a long tool run (the documents it activates become the run's too). */
  inToolRun?: boolean;
  /**
   * A turn a plugin started (a wake, or a Stop-hook continuation of one): a message from another agent, not the user.
   * It never inherits auto-approval (a long tool run's, or "Always allow" in the chat): write tools ask, or follow read-only.
   */
  fromWake?: boolean;
  /**
   * A wake the user trusts (wakeTrusted): from their own console, or from their other agents / Claude Desktop / Codex
   * while "Trust messages from my agents" is on. The turn follows the approval setting like a typed message. Not carried
   * into Stop-hook continuations (a plugin's request).
   */
  trustedWake?: boolean;
  /** A Stop-hook continuation: the turn goes on because a plugin asked, so no memory, profile or tool-copy writes (lib/memory.ts). */
  fromHook?: boolean;
}

/** Valid ask_selection kinds (the agent's input is checked loosely). */
const SELECTION_KINDS: SelectionKind[] = ["face", "edge", "vertex", "body", "feature", "component"];

export interface RunState {
  docKey: string;
  threadId: string;
  live: LiveBlock[] | null;
  runningToolId: string | null;
  approval: (ApprovalRequest & { resolve: (d: ApprovalDecision) => void }) | null;
  question: {
    toolUseId: string;
    question: string;
    options: string[];
    form?: FormSpec;
    /** ask_selection: the user picks in SolidWorks and presses Done (answerSelection). */
    selection?: Omit<SelectionAsk, "prompt">;
    /** ask_secret: a masked field; typed or spoken messages never answer it (answerSecret). */
    secret?: SecretAsk & { id: string; title: string; set: boolean };
    /** When an unanswered question gives the agent NO_ANSWER (epoch ms); undefined = it waits for the user. */
    deadline?: number;
    resolve: (answer: string) => void;
  } | null;
  controller: AbortController;
  /** Ribbon tool runs: the tool's title (shown in the "executing" banner) and when it started. */
  label?: string;
  /** The run starts or continues a long tool run (RunOverride.inToolRun). */
  inToolRun?: boolean;
  startedAt?: number;
  /** When the current request to the model started (the thinking timer). */
  requestStartedAt?: number;
  /** A status line, e.g. a stalled connection being retried. */
  status?: string | null;
  /** A run_agent sub-agent at work: its tool calls so far (shown in the run_agent card). */
  subAgent?: SubAgentProgress | null;
}

/** The last ribbon tool run's outcome, shown briefly in the banner. */
export interface ToolRunOutcome {
  label: string;
  text: string;
  ok: boolean;
  threadId: string;
  docKey: string;
  at: number;
}

interface State {
  booted: boolean;
  bootError: string | null;
  host: HostState | null;
  settings: Settings;
  /** Where each provider's key comes from (the add-in's apiKeySources); a provider with "none" can't run. */
  apiKeySources: Record<ProviderId, KeySource>;
  /** Options shows this provider's key row first and focused (a run needed its key). */
  keyFocus: ProviderId | null;
  swTheme: SwThemeInfo | null;
  tools: ToolDef[];
  userTools: UserToolInfo[];
  /** Update feed state; the header shows an update button while a newer version exists. */
  update: UpdateInfo | null;
  updateDialog: boolean;
  docs: Record<string, DocInfo>;
  activeDocKey: string | null;
  viewKey: string;
  chats: Record<string, ChatSlot>;
  selection: Record<string, SelectionItem[]>;
  /** How many entities are selected per document: the chips stop at 30 (the ask_selection card shows the real count). */
  selectionTotal: Record<string, number>;
  includeSelection: boolean;
  /** Indexes (in SolidWorks selection order) the user removed from the selection chips. Reset when the selection changes. */
  excludedSel: number[];
  /** Per document: signature of a selection the agent made (it selects entities to build features). Hidden, never sent. */
  agentSel: Record<string, string>;
  markup: { active: boolean; tool: MarkupTool; color: string; strokes: number; frames: number };
  /** Push-to-talk: what is being said and done right now. */
  voice: VoiceLive;
  voiceStatus: VoiceStatus | null;
  /** Discard the voice message being recorded. */
  cancelVoice(): void;
  /** A speech model being downloaded to turn voice on with (it is selected and voice enabled when it arrives). */
  voiceWant: string | null;
  /** Turn voice on: with the model if it's downloaded, otherwise download it first. */
  enableVoice(modelId?: string): void;
  disableVoice(): void;
  /** Download a model and switch to it when it arrives. */
  downloadVoiceModel(modelId: string): void;
  /** The first-run tour: the step shown, or null. */
  tourStep: number | null;
  startTour(): void;
  setTourStep(step: number): void;
  endTour(): void;
  runs: Record<string, RunState>;
  /** Follow-ups per thread, waiting for the running turn. */
  queue: Record<string, QueuedMessage[]>;
  queueSendNow(threadId: string, id: string): void;
  queueEdit(threadId: string, id: string): void;
  queueRemove(threadId: string, id: string): void;
  drafts: Record<string, string>;
  attachments: Record<string, Attachment[]>;
  /** @ tags on a draft (per draft key): sent as "[Use plugin: x]" lines. */
  tags: Record<string, Tag[]>;
  addTag(tag: Tag): void;
  removeTag(index: number): void;
  /** The composer's Enter: a slash command runs, anything else is sent (with its tags). */
  submitInput(text: string): Promise<void>;
  /** Plugins (docs/plugins/design.md): the store, their components, MCP servers and the hook session. */
  plugins: PluginInfo[];
  marketplaces: MarketplaceInfo[];
  commands: CommandInfo[];
  skills: SkillInfo[];
  agents: AgentInfo[];
  mcpServers: McpServerStatus[];
  session: SessionInfo | null;
  /** The helpers cells can call and the saved API notes (knowledge.index), for the system prompt; null with older add-ins. */
  knowledge: KnowledgeIndex | null;
  /** Standing instructions (memory.index): what the user told the agent to do from now on; null with older add-ins. */
  memory: MemoryIndex | null;
  /** Undo on a "Remembered" chip: the note goes (and any it replaced come back); the chip shows it was undone. */
  undoMemory(threadId: string, noteId: string): Promise<void>;
  loadPlugins(): Promise<void>;
  pluginsTab: "discover" | "installed" | "marketplaces" | "create";
  setPluginsTab(tab: State["pluginsTab"]): void;
  /** Plugin wakes waiting for their thread's run to end. */
  wakes: Record<string, Wake[]>;
  /** Run a tool or workflow by name (ribbon tools through the add-in, workflows in the chat with the notes). */
  runNamedTool(name: string, notes?: string): Promise<void>;
  panel: "chat" | "settings" | "newTool" | "plugins";
  focusTick: number;
  notice: string | null;
  /** "Watch me": recording what the user does in a document (live log from the add-in). */
  watch: WatchState;

  boot(): Promise<void>;
  showDoc(key: string): Promise<void>;
  newChat(): Promise<void>;
  selectThread(threadId: string): Promise<void>;
  deleteThread(threadId: string): Promise<void>;
  /** "Not now" on a Save-and-restart offer. */
  dismissRestart(threadId: string, offerId: string): void;
  /** From the New tool screen: start the agent on a tool described there. */
  startNewTool(spec: NewToolSpec): Promise<void>;
  /** When this SolidWorks session started (or the panel, with older add-ins): ms since epoch. */
  sessionStart: number;
  /** Items the agent has drawn on the user's view (show_overlay); 0 when nothing is shown. */
  overlayCount: number;
  clearOverlay(): void;
  setThreadModel(model: string): void;
  setThreadEffort(effort: Effort): void;
  setDraft(text: string): void;
  addAttachment(a: Attachment): void;
  removeAttachment(id: string): void;
  attachView(): Promise<void>;
  /**
   * model / effort apply when this starts a new chat. A workflow run (tool) or a new request (fresh) isn't a follow-up:
   * it runs with its own settings and ends a long tool run's follow-up settings (runSettings).
   */
  send(text: string, opts?: RunOverride): Promise<void>;
  retry(): Promise<void>;
  /** Drop everything after the prompt at msgIndex and run again, optionally with edited text. */
  resendFrom(msgIndex: number, editedText?: string): Promise<void>;
  /** Stop the current chat's run, or a specific thread's (e.g. a background tool run). */
  stop(threadId?: string): void;
  answerApproval(threadId: string, d: ApprovalDecision): void;
  answerQuestion(threadId: string, answer: string): void;
  /** The pending question waits for the user (no timeout). */
  keepWaiting(threadId: string): void;
  /**
   * ask_selection: Done (done=true) snapshots the selection and answers with [S#] refs merged into the chat; Skip
   * answers with nothing. A typed or spoken answer while the card is open counts as Done with that note.
   */
  answerSelection(threadId: string, done: boolean, note?: string): Promise<void>;
  /**
   * ask_secret: Save stores the value in the plugin's encrypted options (plugins.setConfig) and tells the agent only
   * "saved"; null is Skip. Throws when saving failed (the card shows it and stays open).
   */
  answerSecret(threadId: string, value: string | null): Promise<void>;
  /** Close a long tool run's follow-up settings (the banner chip's X). */
  endToolRun(threadId: string): void;
  /** What's typed (or dictated) into a pending question's answer box, per thread. */
  answerDrafts: Record<string, string>;
  setAnswerDraft(threadId: string, text: string): void;
  saveSettings(patch: Partial<Settings>): Promise<void>;
  /** Save (or with "" remove) a provider's key. */
  setApiKey(provider: ProviderId, key: string): Promise<void>;
  setPanel(p: State["panel"]): void;
  setIncludeSelection(v: boolean): void;
  toggleSelItem(index: number): void;
  runUserTool(name: string): Promise<void>;
  /** A ribbon tool that needs the agent. Runs in the chat the user is in (queued if the agent is busy there). */
  runQuickTool(payload: ToolRunPayload): Promise<void>;
  toolOutcome: ToolRunOutcome | null;
  crash: CrashInfo | null;
  ribbonStale: boolean;
  dismissCrash(): void;
  /** Start a chat that hands the agent the crash details so it finds and fixes the cause. */
  askAboutCrash(): Promise<void>;
  /** Save open documents and restart SolidWorks (for new tools to reach the ribbon). */
  saveAndRestart(): Promise<string | null>;
  deleteUserTool(name: string): Promise<void>;
  startMarkup(): Promise<void>;
  setMarkup(patch: { tool?: MarkupTool; color?: string }): void;
  markupUndo(): void;
  markupClear(): void;
  finishMarkup(): Promise<void>;
  cancelMarkup(): void;
  dismissNotice(): void;
  /** Open the file dialog and attach what the user picks (images, PDFs, 3D models). */
  pickFiles(): Promise<void>;
  /** Attach files the add-in described (picked, dropped, ...); 3D models are read in the background. */
  addFileEntries(entries: FileEntry[]): void;
  startWatch(): Promise<void>;
  /** Stop recording and attach the log (with views before/after) to the next message. */
  stopWatch(): Promise<void>;
  cancelWatch(): void;
}

/** A plugin, tool, skill or agent tagged with @ in the composer. */
export interface Tag {
  kind: TagKind;
  name: string;
}

export interface WatchState {
  active: boolean;
  /** Stopping: the add-in is finishing the log and capturing the final view. */
  busy: boolean;
  docKey: string | null;
  doc: string | null;
  startedAt: number;
  entries: WatchEntry[];
}

const WATCH_IDLE: WatchState = { active: false, busy: false, docKey: null, doc: null, startedAt: 0, entries: [] };

/**
 * Chats aren't tied to documents: they all live in one workspace list (stored by the add-in in its own folder), so a
 * chat keeps going while files are opened, created and switched. Tools act on the active document.
 */
export const WORKSPACE = "workspace";

export type MarkupTool = "pen" | "arrow" | "circle" | "highlighter";

/** The live voice message: idle, listening (key held), transcribing (key let go), pen (waiting for the markup to end). */
export interface VoiceLive {
  phase: "idle" | "listening" | "transcribing" | "pen";
  text: string;
  level: number;
  events: { t: number; kind: string; summary: string }[];
  docKey: string | null;
}
export const VOICE_IDLE: VoiceLive = { phase: "idle", text: "", level: 0, events: [], docKey: null };

/** The talk key as a person reads it. */
export function talkKeyName(key: string | undefined): string {
  const k = key ?? "vk:192";
  if (k === "mouse:x1") return "the mouse Back button";
  if (k === "mouse:x2") return "the mouse Forward button";
  const vk = Number(k.slice(3));
  const names: Record<number, string> = { 192: "`", 20: "Caps Lock", 163: "Right Ctrl", 119: "F8", 120: "F9", 19: "Pause", 145: "Scroll Lock" };
  return names[vk] ?? `key ${vk}`;
}
export const MARKUP_COLORS = ["#E5302A", "#F2B11F", "#2F84D6", "#2E9E3E"];
export const MARKUP_NAME = "Markup";

interface SelectionSnapshot {
  items: { type: string; label: string; detail: string; ref: string | null }[];
  view: { text: string } | null;
  /** How many entities are selected (items stop at the snapshot's max). */
  total?: number;
}

/** ask_selection reads up to this many entities (the add-in's cap); a larger selection is reported as cut. */
const SELECTION_MAX = 500;

/** Identity of a selection as the panel sees it (type + owner of each entity, in order). */
export function selSig(items: SelectionItem[]): string {
  return items.map((i) => `${i.type}|${i.label}`).join(";");
}

/** True when what's selected in the document right now was selected by the agent, not the user. */
export function isAgentSelection(s: { selection: Record<string, SelectionItem[]>; agentSel: Record<string, string> }, docKey: string): boolean {
  const items = s.selection[docKey] ?? [];
  return items.length > 0 && s.agentSel[docKey] === selSig(items);
}

/**
 * The prompt for a ribbon tool run: the tool and the user's inputs first (what the chat shows when expanded), then
 * the silently captured context - model, feature history, macro output, selections as [S1]... and the view.
 * Images and PDFs the user added go in as real attachments.
 */
/** Raw bytes of files one tool run attaches (FileAttachments.MaxRunBytes in the add-in). */
export const RUN_ATTACH_BYTES = 20_000_000;

function stageToolPrompt(p: ToolRunPayload, label: string, refBase: number): { blocks: ContentBlockParam[]; refs: Record<string, string> } {
  const refs: Record<string, string> = {};
  const inputLines: string[] = [];
  const contextLines: string[] = [];
  const attachments: ContentBlockParam[] = [];
  let n = refBase;
  // one attachment budget for the whole run (older add-ins check each file only against its own cap): 20 MB raw is
  // 26.7 MB base64, under the API's 32 MB request cap with room for the view and text
  let budget = RUN_ATTACH_BYTES;
  const fits = (data: string) => {
    const raw = Math.floor((data.length * 3) / 4);
    if (raw > budget) return false;
    budget -= raw;
    return true;
  };
  for (const i of p.inputs ?? []) {
    if (i.type === "selection") {
      const ids = (i.items ?? []).map((it) => {
        const id = `S${++n}`;
        if (it.ref) refs[id] = it.ref;
        contextLines.push(`[${id}] ${it.type}${it.label ? ` of ${it.label}` : ""}${it.detail ? ` - ${it.detail}` : ""}${it.ref ? "" : " (no persistent id)"}`);
        return `[${id}]`;
      });
      inputLines.push(`- ${i.label} (${i.id}): ${ids.length ? ids.join(", ") : "(nothing selected)"}`);
    } else if (i.type === "files") {
      const names: string[] = [];
      for (const f of i.files ?? []) {
        if (f.data && !fits(f.data)) {
          names.push(`${f.name} (not attached: over the run's attachment budget of ${RUN_ATTACH_BYTES / 1e6} MB - path only: ${f.path})`);
        } else if (f.data && f.mediaType?.startsWith("image/")) {
          attachments.push({ type: "image", source: { type: "base64", media_type: f.mediaType as "image/png", data: f.data } });
          names.push(`${f.name} (attached image)`);
        } else if (f.data && f.mediaType === "application/pdf") {
          attachments.push({ type: "document", source: { type: "base64", media_type: "application/pdf", data: f.data }, title: f.name } as unknown as ContentBlockParam);
          names.push(`${f.name} (attached PDF)`);
        } else if (f.kind === "model" && f.summary) {
          attachments.push(...modelBlocks(f));
          names.push(`${f.name} (3D model - ${f.views ? "views and measurements" : "what could be read"} attached)`);
        } else {
          names.push(`${f.name} (${f.kind === "model" ? "3D model, not attached - open it with execute_csharp if you need it" : f.error ?? "not attached"}: ${f.path})`);
        }
      }
      inputLines.push(`- ${i.label} (${i.id}): ${names.length ? names.join("; ") : "(none)"}`);
    } else {
      const v = typeof i.value === "string" ? i.value : JSON.stringify(i.value);
      inputLines.push(`- ${i.label} (${i.id}): ${v === "" ? "(empty)" : v}`);
    }
  }
  const c = p.context ?? {};
  if (c.handoff) contextLines.unshift(`${c.handoff.slice(0, 6000)}

Finish this case yourself.${p.builtIn ? "" : " If it is a case the macro should cover, extend the macro (edit_user_tool) afterwards."}`);
  const toolName = p.title ?? p.tool;
  if (c.failure)
    contextLines.unshift(
      p.builtIn
        ? `The macro in the built-in "${toolName}" tool failed - finish the job with the tools (built-in tools are read-only; don't try to edit it):\n${c.failure.slice(0, 4000)}`
        : `The macro in the "${toolName}" tool failed - finish the job, then fix the macro (edit_user_tool) so the next run works:\n${c.failure.slice(0, 4000)}`,
    );
  if (c.macroOutput) contextLines.unshift(`Output of the macro in the "${toolName}" tool (already run):\n${c.macroOutput.slice(0, 6000)}`);
  if (p.notes?.trim()) contextLines.unshift(p.notes.trim()); // the tool's standing instructions (docs/agent-memory.md)
  if (c.model) contextLines.push(`Model:\n${c.model}`);
  if (c.history) contextLines.push(`Feature history:\n${c.history}`);

  const profile = profileLine(p.profile);
  if (profile) inputLines.unshift(`- ${profile}`);
  const head = `[Run tool: ${p.tool}]${inputLines.length ? `\n<tool_inputs>\n${inputLines.join("\n")}\n</tool_inputs>` : ""}`;
  const blocks: ContentBlockParam[] = [{ type: "text", text: head }, ...attachments];
  blocks.push({ type: "text", text: `<sw_context>\nTool run: ${label}\n${contextLines.join("\n")}${c.view ? "\nAttached after this block: the user's view when they ran the tool." : ""}\n</sw_context>` });
  if (c.view) blocks.push({ type: "image", source: { type: "base64", media_type: "image/jpeg", data: c.view } });
  return { blocks, refs };
}

/** Text blocks the panel adds for attachments; the chat shows them as cards, not as the user's words. */
export const MODEL_TAG = "<model_file";
export const WATCH_TAG = "<watch_log";
export const VOICE_TAG = "<voice_timeline";
export { EVENT_TAG, HOOK_TAG };
export function isAttachmentText(text: string): boolean {
  return text.startsWith(MODEL_TAG) || text.startsWith(WATCH_TAG) || text.startsWith(VOICE_TAG) || text.startsWith(HOOK_TAG) || text.startsWith(EVENT_TAG);
}

const clock = (ms: number) => {
  const s = Math.max(0, Math.round(ms / 1000));
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
};

/**
 * A voice message for the agent: one timeline of what the user said and did (selections get reference ids that
 * continue from startN, like the context block's), then the images it mentions, in order. Without refs (a message
 * handed over mid-run) the selections are described only.
 */
export function voiceBlocks(v: VoicePayload, startN: number | null): { blocks: ContentBlockParam[]; refs: Record<string, string>; count: number } {
  const refs: Record<string, string> = {};
  let n = startN ?? 0;
  let image = 0;
  const images: ContentBlockParam[] = [];
  type Row = { t: number; line: string };
  const rows: Row[] = v.segments.map((s) => ({ t: s.t0, line: `said: "${s.text}"` }));
  for (const e of v.events) {
    let line: string;
    switch (e.kind) {
      case "view":
      case "markup":
        if (!e.image) continue;
        image++;
        images.push({ type: "image", source: { type: "base64", media_type: (e.mediaType ?? "image/jpeg") as ImageMediaType, data: e.image } });
        line =
          e.kind === "view"
            ? `[image ${image}: the view at this moment${image === 1 ? " (when they started talking)" : ""}]`
            : `[image ${image}: the user's markup - pen strokes, arrows or circles they drew over this view]`;
        break;
      case "select": {
        const items = (e.items ?? []).map((it) => {
          const what = `${it.type}${it.label ? ` of ${it.label}` : ""}${it.detail ? ` - ${it.detail}` : ""}`;
          if (startN === null || !it.ref) return what;
          const id = `S${++n}`;
          refs[id] = it.ref;
          return `[${id}] ${what}`;
        });
        line = (e.summary.startsWith("Already") ? "already selected: " : "selected: ") + items.join("; ");
        break;
      }
      default:
        line = e.summary.charAt(0).toLowerCase() + e.summary.slice(1);
    }
    rows.push({ t: e.t, line });
  }
  rows.sort((a, b) => a.t - b.t);
  const markups = v.events.filter((e) => e.kind === "markup").length;
  const head =
    `${VOICE_TAG} duration="${clock(v.durationMs)}" images="${image}" markups="${markups}">\n` +
    "A voice message: the user talked while working in SolidWorks. Speech was transcribed on their PC, so expect recognition errors, " +
    "especially in names - prefer what the timeline and images show. What they said and what they did are interleaved by time " +
    '(m:ss from when they started); "this", "here" and "that face" usually mean what they selected or marked at that moment.' +
    (image ? ` The ${image} image${image === 1 ? "" : "s"} follow this block in order.` : "") +
    "\n\n";
  const body = rows.map((r) => `${clock(r.t)}  ${r.line}`).join("\n");
  const blocks: ContentBlockParam[] = [{ type: "text", text: `${head}${body}\n</voice_timeline>` }, ...images];
  return { blocks, refs, count: n - (startN ?? 0) };
}

const escapeAttr = (v: string) => v.replace(/[&"<>]/g, (c) => ({ "&": "&amp;", '"': "&quot;", "<": "&lt;", ">": "&gt;" })[c]!);

/** A 3D model for the agent: the measurement summary, then the six-view sheet. */
export function modelBlocks(f: FileEntry): ContentBlockParam[] {
  const text = f.summary ?? `3D model: ${f.name} - not read${f.error ? ` (${f.error})` : ""}. Path: ${f.path}`;
  const blocks: ContentBlockParam[] = [{ type: "text", text: `${MODEL_TAG} name="${escapeAttr(f.name)}" path="${escapeAttr(f.path)}">\n${text}\n</model_file>` }];
  if (f.views) blocks.push({ type: "image", source: { type: "base64", media_type: (f.viewsMediaType ?? "image/jpeg") as ImageMediaType, data: f.views } });
  return blocks;
}

/** A "Watch me" recording for the agent: the log, then the views before / after and the step thumbnails. */
export function watchBlocks(w: WatchResult): ContentBlockParam[] {
  const images: { label: string; data: string }[] = [];
  if (w.before) images.push({ label: "the view when recording started", data: w.before });
  if (w.after) images.push({ label: "the view when it stopped", data: w.after });
  if (w.film) images.push({ label: "thumbnails of the steps, labelled STEP n like the log", data: w.film });
  const note = images.length ? `\nAttached after this block, in order: ${images.map((i) => i.label).join("; ")}.` : "";
  return [
    { type: "text", text: `${WATCH_TAG} doc="${escapeAttr(w.doc)}" steps="${w.steps}">\n${w.text}${note}\n</watch_log>` },
    ...images.map((i): ContentBlockParam => ({ type: "image", source: { type: "base64", media_type: "image/jpeg", data: i.data } })),
  ];
}

/** Follow-ups delivered mid-run ("Send now"): text and attachments, after the tool results. */
function queuedBlocks(q: QueuedMessage[]): ContentBlockParam[] {
  const blocks: ContentBlockParam[] = [];
  for (const m of q) {
    if (m.staged) {
      blocks.push(...m.staged.blocks); // a queued tool run: its whole staged prompt
      continue;
    }
    if (m.text) blocks.push({ type: "text", text: m.text });
    for (const a of m.attachments) blocks.push(...attachmentBlocks(a));
  }
  return blocks;
}

/** Content blocks for whatever is attached to a message. */
function attachmentBlocks(a: Attachment): ContentBlockParam[] {
  switch (a.kind) {
    case "image":
      return [{ type: "image", source: { type: "base64", media_type: a.mediaType, data: a.data } }];
    case "pdf":
      return [{ type: "document", source: { type: "base64", media_type: "application/pdf", data: a.data }, title: a.name } as unknown as ContentBlockParam];
    case "model":
      return modelBlocks(a.entry ?? { name: a.name, path: a.path, kind: "model" });
    case "watch":
      return watchBlocks(a.watch);
    case "voice":
      return voiceBlocks(a.voice, null).blocks;
  }
}

/** Threads whose running turn answers a voice message (its reply may be read aloud). */
const speakAfter = new Set<string>();
let lastVoice: VoicePayload | null = null;

/** Plugin wakes already taken (a nudge and session.takeWakes can hand the same one over twice before it is acked). */
const seenWakes = new Set<string>();
/** This page's id for session.info: the add-in sees a new one after a reload (the old page's link turns are gone). */
const PANEL_ID = `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
/** When automatic turns (plugin wakes, Stop-hook continuations) started: at most settings.pluginWakesPerHour an hour. */
let autoTimes: number[] = [];
/** The tags of the message being sent: back on the composer with its text if a hook blocks it. */
let sentTags: { text: string; tags: Tag[] } | null = null;
/** SessionStart notices (session.info sessionNotices) already shown in the notice bar. */
let shownSessionNotices = 0;
/**
 * Threads whose next run is being put together (prompt hooks, selection and view, saving) but isn't in `runs` yet. They
 * count as busy: a wake arriving meanwhile is held and a send is queued, so two runs never start on one thread.
 */
const preparing = new Map<string, { token: object; release: () => void }>();

/** hooks.fire's result with every field present (an older or partial add-in may leave some out). */
function hookResult(r: Partial<HookResult> | null | undefined): HookResult | null {
  if (!r) return null;
  return {
    ...r,
    block: !!r.block,
    continue: r.continue !== false,
    additionalContext: (r.additionalContext ?? []).filter((t) => typeof t === "string" && t.trim()),
    systemMessages: (r.systemMessages ?? []).filter((t) => typeof t === "string" && t.trim()),
  };
}

/** ask_selection cards whose Done is being processed (a double click must not answer twice). */
const answeringSelection = new Set<string>();

/** What the agent gets when a question goes unanswered (the user asked for exactly this wording). */
export const NO_ANSWER = "No response. Use your skills to make the best decision possible.";

/** How long a question (ask_user / ask_form) waits: Settings > questionTimeoutSec (default 60 s, 0 = forever). */
export function questionTimeoutMs(settings: Pick<Settings, "questionTimeoutSec">): number {
  const sec = settings.questionTimeoutSec ?? 60;
  return Number.isFinite(sec) && sec > 0 ? sec * 1000 : 0;
}

/** 3D models being read by the add-in, by attachment id (send waits for them). */
const describing = new Map<string, Promise<void>>();
const IMAGE_TYPES = ["image/jpeg", "image/png", "image/webp", "image/gif"];

/** Stable empty array for selectors (a fresh [] each call would re-render forever). */
export const EMPTY: never[] = [];

const uid = () => crypto.randomUUID();
const now = () => new Date().toISOString();
const ZERO_USAGE: Usage = { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 };

/** Key for drafts/attachments: the active thread, or the doc's "new chat" slot. */
export function draftKey(s: Pick<State, "chats" | "viewKey">): string {
  const slot = s.chats[s.viewKey];
  return slot?.data.activeThreadId ?? `new:${s.viewKey}`;
}

export function currentThread(s: Pick<State, "chats" | "viewKey">): Thread | null {
  const slot = s.chats[s.viewKey];
  if (!slot?.data.activeThreadId) return null;
  return slot.data.threads.find((t) => t.id === slot.data.activeThreadId) ?? null;
}

/** No key anywhere: the state before the host has answered. */
export const NO_KEYS: Record<ProviderId, KeySource> = { anthropic: "none", openai: "none" };

/** Whether a provider has a key (stored or from the environment). */
export function hasKey(s: Pick<State, "apiKeySources">, p: ProviderId): boolean {
  return (s.apiKeySources[p] ?? "none") !== "none";
}

/** Whether any provider has a key. */
export function anyKey(s: Pick<State, "apiKeySources">): boolean {
  return PROVIDERS.some((p) => hasKey(s, p.id));
}

/** The host's key sources; an older add-in reports Anthropic's only. */
function keySources(host: Pick<HostState, "apiKeySource" | "apiKeySources">): Record<ProviderId, KeySource> {
  return { ...NO_KEYS, anthropic: host.apiKeySource, ...(host.apiKeySources ?? {}) };
}

/**
 * The OpenAI speed setting for a run; standard sends nothing. Not filtered by the run's model: a sub-agent may run on
 * another one, and each adapter sends the tier only to models that offer it.
 */
export function serviceTierFor(settings: Settings): "fast" | "ultrafast" | undefined {
  const speed = settings.openaiSpeed;
  return speed === "fast" || speed === "ultrafast" ? speed : undefined;
}

/** The notice when a run's provider has no key: today's text when there is no key at all. */
export function noKeyNotice(model: string, anyKeyAtAll: boolean): string {
  const m = modelOption(model);
  const vendor = getProvider(m.provider).vendor;
  return anyKeyAtAll ? `${m.label} needs an ${vendor} API key. Add one in Options, or pick another model.` : `Add your ${vendor} API key to start chatting.`;
}

// Saves are serialized per document so an older snapshot never overwrites a newer one.
const saveChains = new Map<string, Promise<unknown>>();

export const useStore = create<State>((set, get, api) => {
  const persist = (docKey: string) => {
    const slot = get().chats[docKey];
    if (!slot) return Promise.resolve();
    const snapshot = slot.data;
    const next = (saveChains.get(docKey) ?? Promise.resolve())
      .then(() => rpc("chat.save", { docKey, data: snapshot }))
      .catch((err) => set({ notice: `Couldn't save the chat: ${err.message}` }));
    saveChains.set(docKey, next);
    return next;
  };

  const updateThread = (docKey: string, threadId: string, fn: (t: Thread) => Thread) => {
    set((s) => {
      const slot = s.chats[docKey];
      if (!slot) return {};
      const threads = slot.data.threads.map((t) => (t.id === threadId ? { ...fn(t), updatedAt: now() } : t));
      return { chats: { ...s.chats, [docKey]: { ...slot, data: { ...slot.data, threads } } } };
    });
  };

  const updateSlot = (docKey: string, fn: (d: ChatData) => ChatData) => {
    set((s) => {
      const slot = s.chats[docKey];
      return slot ? { chats: { ...s.chats, [docKey]: { ...slot, data: fn(slot.data) } } } : {};
    });
  };

  const updateRun = (threadId: string, patch: Partial<RunState>) => {
    const before = get().runs[threadId];
    set((s) => (s.runs[threadId] ? { runs: { ...s.runs, [threadId]: { ...s.runs[threadId], ...patch } } } : {}));
    if (before && ("approval" in patch || "question" in patch)) {
      const after = get().runs[threadId];
      const was = !!(before.approval || before.question);
      const now = !!(after?.approval || after?.question);
      if (was !== now) reportLinkWaiting(threadId, now);
    }
  };

  /**
   * Answer a pending question with NO_ANSWER once its deadline passes. Typing an answer pushes the deadline back (nobody is
   * cut off mid-sentence); "Keep waiting" clears it; answering, Stop or a newer question ends this timer.
   */
  const armQuestionTimeout = (threadId: string, toolUseId: string, timeout: number) => {
    const check = () => {
      const q = get().runs[threadId]?.question;
      const step = questionTimerStep(q, toolUseId, get().answerDrafts[threadId] ?? "", Date.now(), timeout);
      if (step.kind === "extend" && q) updateRun(threadId, { question: { ...q, deadline: step.deadline } });
      if (step.kind === "extend" || step.kind === "wait") setTimeout(check, step.wait);
      else if (step.kind === "answer") get().answerQuestion(threadId, NO_ANSWER);
    };
    setTimeout(check, timeout);
  };

  const loadChat = async (docKey: string) => {
    if (get().chats[docKey]) return;
    const res = await rpc<{ docKey: string; docId: string; recovered: boolean; data: ChatData }>("chat.load", { docKey });
    const raw: Partial<ChatData> = res.data ?? {};
    const data: ChatData = {
      schema: 1,
      docId: res.docId,
      revision: raw.revision ?? 0,
      activeThreadId: raw.activeThreadId ?? null,
      threads: raw.threads ?? [],
      imported: raw.imported ?? [],
    };
    data.threads = (data.threads ?? []).map((t) => ({ ...t, turns: t.turns ?? [], messages: t.messages ?? [] }));
    set((s) => ({
      chats: { ...s.chats, [docKey]: { docKey, docId: res.docId, data, recovered: res.recovered } },
      notice: res.recovered ? "Recovered chat messages that hadn't been saved into the file yet." : s.notice,
    }));
  };

  /**
   * The add-in's view of the world at the moment of sending: document, camera, the selection as numbered
   * references ([S1]...) and, optionally, a screenshot of exactly what the user sees. Appended after the
   * user's text so the history stays append-only.
   */
  const buildContext = async (
    docKey: string,
    threadId: string,
    hasMarkup: boolean,
    opts: { noView?: boolean; reserved?: number } = {},
  ): Promise<{ blocks: ContentBlockParam[]; refs: Record<string, string> }> => {
    const s = get();
    const doc = s.docs[s.activeDocKey ?? ""];
    const lines: string[] = [];
    const refs: Record<string, string> = {};
    const extra: ContentBlockParam[] = [];
    if (!doc) {
      lines.push("No document is linked to this chat. Tools act on whichever document is active in SolidWorks (if any).");
      return { blocks: [{ type: "text", text: `<sw_context>\n${lines.join("\n")}\n</sw_context>` }], refs };
    }

    lines.push(`Document: ${doc.title} (${doc.type ?? "document"})${doc.path ? ` - ${doc.path}` : " - not saved yet"}`);
    const snap = await rpc<SelectionSnapshot | null>("selection.snapshot", { docKey }).catch(() => null);
    if (snap?.view?.text) lines.push(`Camera: ${snap.view.text}`);

    const items = isAgentSelection(s, s.activeDocKey ?? "") ? [] : snap?.items ?? [];
    const thread = s.chats[docKey]?.data.threads.find((t) => t.id === threadId);
    let n = refBase(thread?.selRefs) + (opts.reserved ?? 0);
    if (items.length > 0 && !s.includeSelection) {
      lines.push("Selection: (the user chose not to share it)");
    } else {
      const included = items.filter((_, i) => !s.excludedSel.includes(i));
      if (included.length === 0) {
        lines.push("Selection: nothing selected");
      } else {
        lines.push(`Selected when this message was sent (${included.length}) - may or may not relate to the request:`);
        for (const it of included) {
          const id = `S${++n}`;
          if (it.ref) refs[id] = it.ref;
          lines.push(`[${id}] ${it.type}${it.label ? ` of ${it.label}` : ""}${it.detail ? ` - ${it.detail}` : ""}${it.ref ? "" : " (no persistent id: not addressable with Ref)"}`);
        }
      }
    }

    if (hasMarkup) {
      lines.push("The images named Markup attached above are the user's markup: strokes, arrows and circles drawn over their view - one image per view they marked.");
    } else if (s.settings.shareView && !opts.noView && doc.type !== "other") {
      const shot = await rpc<ToolRunResult>("tools.run", {
        docKey,
        threadId,
        name: "capture_views",
        input: { views: ["current"], max_size: 1024 },
      }).catch(() => null);
      const img = shot && !shot.isError ? shot.content.find((c) => c.type === "image") : undefined;
      if (img) {
        extra.push(img);
        lines.push("Attached after this block: a screenshot of exactly what the user sees right now (selected entities are highlighted).");
      }
    }
    return { blocks: [{ type: "text", text: `<sw_context>\n${lines.join("\n")}\n</sw_context>` }, ...extra], refs };
  };

  /** Each provider's key for runAgent: `has` from the reported sources, `get` from the add-in when a run needs it. */
  const keys = {
    has: (p: ProviderId) => hasKey(get(), p),
    get: (p: ProviderId) => rpc<{ key: string | null }>("apiKey.get", { provider: p }).then((r) => r?.key ?? null),
  };

  /** The provider ribbon tool runs use (design 7.3). */
  const toolProvider = () => pickToolProvider(get().settings.defaultModel, keys.has, PROVIDERS.map((p) => p.id));

  const startRun = async (docKey: string, threadId: string, extra: Partial<RunState> = {}, run: RunOverride = {}) => {
    const thread = get().chats[docKey]?.data.threads.find((t) => t.id === threadId);
    if (!thread) return;
    // The run's model decides its provider; without that provider's key, Options opens at its row.
    const runModel = run.model ?? thread.model;
    const provider = inferProvider(runModel);
    if (!keys.has(provider)) {
      set({ panel: "settings", keyFocus: provider, notice: noKeyNotice(runModel, anyKey(get())) });
      reportLinkAnswer(docKey, threadId, "error", noKeyNotice(runModel, anyKey(get())));
      return;
    }
    if (get().runs[threadId]) {
      // Never a second run on one thread: its Stop would only reach one of them (callers mark threads busy first).
      console.warn("startRun: a run is already going in this chat; not starting another");
      return;
    }

    const controller = new AbortController();
    set((s) => ({
      runs: { ...s.runs, [threadId]: { docKey, threadId, live: null, runningToolId: null, approval: null, question: null, controller, inToolRun: !!run.inToolRun, ...extra } },
    }));
    preparing.get(threadId)?.release(); // the run itself is the busy mark now
    // A plugin's turn: write tools ask (or follow read-only), whatever this chat or a tool run allowed before.
    const settings = get().settings;
    const untrustedWake = run.fromWake && !run.trustedWake;
    const approval = untrustedWake ? wakeApproval(settings.approval) : run.autoApprove ? "auto" : settings.approval;
    let allowedInWake = false; // "Always allow" chosen during this woken turn
    let stopHook: HookResult | null = null;
    const started = performance.now();
    let outcome = "end_turn";
    let failure: string | undefined; // why the turn failed, for a local-link answer
    const selDoc = get().activeDocKey ?? "";
    const selectionAtStart = selSig(get().selection[selDoc] ?? []);
    rpc("agent.activity", { running: true }).catch(() => {});
    const turnIndex = thread.turns.length - 1;
    const patchTurn = (patch: Partial<TurnMeta>) =>
      updateThread(docKey, threadId, (t) => ({
        ...t,
        turns: t.turns.map((m, i) => (i === turnIndex ? { ...m, ...patch } : m)),
      }));

    try {
      const result = await runAgent({
        keys,
        serviceTier: serviceTierFor(get().settings),
        onServedTier: (tier) => patchTurn({ serviceTier: tier }),
        model: runModel,
        effort: run.effort ?? thread.effort,
        tools: get().tools,
        approval,
        realisticAppearance: get().settings.realisticAppearance,
        verbosity: get().settings.verbosity ?? "medium",
        userTools: get().userTools,
        maxSteps: run.maxSteps,
        longRun: run.longRun,
        skills: get().skills,
        agents: get().agents,
        mcpInstructions: get().session?.mcpInstructions ?? [],
        sessionContext: get().session?.sessionContext ?? [],
        knowledge: get().knowledge,
        webResearch: get().settings.webResearch !== false,
        memory: get().memory,
        fromWake: !!run.fromWake,
        fromHook: !!run.fromHook,
        signal: controller.signal,
        cb: {
          getMessages: () => get().chats[docKey]?.data.threads.find((t) => t.id === threadId)?.messages ?? [],
          append: async (message: MessageParam) => {
            updateThread(docKey, threadId, (t) => ({ ...t, messages: [...t.messages, message] }));
            await persist(docKey);
          },
          setLive: (live) => updateRun(threadId, { live }),
          setRunningTool: (id) => updateRun(threadId, { runningToolId: id }),
          addUsage: (u) =>
            updateThread(docKey, threadId, (t) => ({
              ...t,
              turns: t.turns.map((m, i) => {
                if (i !== turnIndex) return m;
                const prev = m.usage ?? ZERO_USAGE;
                return {
                  ...m,
                  usage: {
                    input: prev.input + u.input,
                    output: prev.output + u.output,
                    cacheRead: prev.cacheRead + u.cacheRead,
                    cacheWrite: prev.cacheWrite + u.cacheWrite,
                    context: Math.max(prev.context ?? 0, u.context ?? 0),
                  },
                };
              }),
            })),
          approve: (req) =>
            new Promise<ApprovalDecision>((resolve) => {
              updateRun(threadId, { approval: { ...req, resolve } });
              void revealRun(docKey, threadId);
            }),
          onRequest: (at) => updateRun(threadId, { requestStartedAt: at }),
          onStatus: (text) => updateRun(threadId, { status: text }),
          ask: (toolUseId, question, options, form) =>
            new Promise<string>((resolve) => {
              const timeout = questionTimeoutMs(get().settings);
              updateRun(threadId, { question: { toolUseId, question, options, form, resolve, deadline: timeout ? Date.now() + timeout : undefined } });
              if (timeout) armQuestionTimeout(threadId, toolUseId, timeout);
              void revealRun(docKey, threadId);
            }),
          askSelection: (toolUseId, ask) =>
            new Promise<string>((resolve) => {
              const asked = (Array.isArray(ask.types) ? ask.types : []).map((k) => String(k).trim().toLowerCase());
              const types = asked.filter((k): k is SelectionKind => SELECTION_KINDS.includes(k as SelectionKind));
              const unknownTypes = asked.filter((k) => !SELECTION_KINDS.includes(k as SelectionKind));
              const num = (v: unknown) => (typeof v === "number" && Number.isFinite(v) && v >= 0 ? Math.min(SELECTION_MAX, Math.round(v)) : undefined);
              const max = num(ask.max);
              const min = num(ask.min);
              const selection = {
                types: types.length ? types : undefined,
                min: min !== undefined && max !== undefined ? Math.min(min, max) : min,
                max,
                unknownTypes: unknownTypes.length ? unknownTypes : undefined,
              };
              updateRun(threadId, { question: { toolUseId, question: String(ask.prompt ?? ""), options: [], selection, resolve } });
              void revealRun(docKey, threadId);
            }),
          askSecret: async (toolUseId, ask) => {
            const plugins = await rpc<PluginInfo[]>("plugins.list").catch(() => get().plugins);
            const target = secretTarget(plugins ?? [], ask);
            if ("error" in target) return { text: target.error, isError: true };
            const text = await new Promise<string>((resolve) => {
              updateRun(threadId, { question: { toolUseId, question: String(ask.label ?? ""), options: [], secret: { ...ask, ...target }, resolve } });
              void revealRun(docKey, threadId);
            });
            return { text };
          },
          runTool: (name, input, toolUseId) =>
            rpc<ToolRunResult>("tools.run", {
              docKey,
              threadId,
              name,
              input,
              toolUseId,
              refs: get().chats[docKey]?.data.threads.find((t) => t.id === threadId)?.selRefs ?? {},
              // the tool run this call belongs to (a copy of the mold tool reads its own data)
              tool: run.tool ?? get().chats[docKey]?.data.threads.find((t) => t.id === threadId)?.toolRun?.tool,
            }),
          takeSteering: () => {
            const q = get().queue[threadId] ?? [];
            const now = q.filter((m) => m.now);
            if (now.length === 0) return null;
            set((s) => ({ queue: { ...s.queue, [threadId]: (s.queue[threadId] ?? []).filter((m) => !m.now) } }));
            return queuedBlocks(now);
          },
          isAutoApproved: () =>
            untrustedWake ? allowedInWake : !!run.autoApprove || !!get().chats[docKey]?.data.threads.find((t) => t.id === threadId)?.autoApprove,
          setAutoApproved: () => {
            allowedInWake = true;
            updateThread(docKey, threadId, (t) => ({ ...t, autoApprove: true }));
          },
          setSubAgent: (subAgent) => updateRun(threadId, { subAgent }),
          hook: (event, call) => fireHook(event, docKey, threadId, call),
        },
      });
      patchTurn({ stop: result.stop, error: result.error, durationMs: Math.round(performance.now() - started) });
      outcome = result.stop;
      failure = result.error;
      // The turn ended normally: the plugins' Stop hooks run while the chat still shows the agent as working.
      if ((outcome === "end_turn" || outcome === "stop_sequence") && !controller.signal.aborted) {
        stopHook = await fireHook("Stop", docKey, threadId, { stop_hook_active: !!run.continuation });
        if (stopHook && !stopHook.continue && stopHook.stopReason) set({ notice: stopHook.stopReason });
      }
    } catch (err: any) {
      outcome = "error";
      const status = err?.status;
      if (err instanceof ProviderError && err.requestId) console.warn(`${err.provider} request ${err.requestId} failed: ${err.kind}`);
      const message =
        err instanceof ProviderError ? providerErrorText(err)
        // An SDK error from before the provider adapters (the Claude path): the same texts.
        : status === 401 ? "The API key was rejected. Check it in Settings."
        : status === 429 ? "Rate limited by the API. Wait a moment and press Retry."
        : status === 529 || status === 503 ? "The API is overloaded right now. Press Retry in a moment."
        : err?.message ?? String(err);
      patchTurn({ stop: "error", error: message, durationMs: Math.round(performance.now() - started) });
      failure = message;
    } finally {
      set((s) => {
        const runs = { ...s.runs };
        delete runs[threadId];
        return { runs };
      });
      rpc("agent.activity", { running: false }).catch(() => {});
      // Selection events can lag the last API call; read the final state once the run is over.
      const afterDoc = get().activeDocKey ?? "";
      const after = afterDoc ? await rpc<{ docKey: string; items: SelectionItem[] } | null>("selection.get", { docKey: afterDoc }).catch(() => null) : null;
      if (after) {
        const sig = selSig(after.items);
        set((s) => ({
          selection: { ...s.selection, [afterDoc]: after.items },
          agentSel: afterDoc !== selDoc || (sig !== selectionAtStart && after.items.length > 0) ? { ...s.agentSel, [afterDoc]: sig } : s.agentSel,
        }));
      }
      noteToolRunActivity(docKey, threadId, turnIndex);
      await persist(docKey);
      speakReply(docKey, threadId, outcome);
      // A Stop hook that blocks continues the turn (queued follow-ups go with it); otherwise the queue, then plugin wakes.
      // Wakes only after a turn that ended by itself: after Stop or an error they wait for the user's next message (a
      // wake must not restart what the user just stopped, nor retry a failed request at once).
      const continued = !!stopHook?.block && stopHook.continue && continueAfterStop(docKey, threadId, stopHook.reason ?? "", run);
      // Messages from Claude Desktop / Codex in this chat get the turn's answer; a continuation answers at its own end.
      if (!continued) reportLinkAnswer(docKey, threadId, outcome, failure);
      if (!continued && !flushQueue(docKey, threadId, outcome) && wakesMayFollow(outcome)) flushWakes(threadId);
    }
  };

  /**
   * Fire a plugin hook event in the add-in (hooks.fire), when a handler can match: hooks are on and the session lists
   * one for the event (and tool). Hook notices for the user show in the notice bar. Never throws: no result is null.
   */
  const fireHook = async (event: HookEvent, docKey: string, threadId: string, extra: Record<string, unknown> = {}): Promise<HookResult | null> => {
    const s = get();
    const tool = typeof extra.tool_name === "string" ? extra.tool_name : undefined;
    if (s.settings.pluginHooks === false || !hasHook(s.session, event, tool)) return null;
    const doc = s.docs[s.activeDocKey ?? ""];
    const r = hookResult(
      await rpc<HookResult>("hooks.fire", {
        event,
        threadId,
        docKey,
        unify3d_document: doc ? { title: doc.title, path: doc.path ?? null, type: doc.type ?? null } : null,
        ...extra,
      }).catch(() => null),
    );
    if (r?.systemMessages.length) set({ notice: r.systemMessages.join(" ") });
    return r;
  };

  /**
   * UserPromptSubmit for a prompt the user typed, queued or ran from the ribbon (never for wakes or Stop-hook
   * continuations: agent-bridge resets its auto-resume counter on it). Blocked: the reason, and the caller puts the
   * prompt back. Otherwise the context the hooks added, as a block for the message.
   */
  const promptHook = async (docKey: string, threadId: string, prompt: string): Promise<{ blocked: string | null; blocks: ContentBlockParam[] }> => {
    const h = await fireHook("UserPromptSubmit", docKey, threadId, { prompt });
    if (h && (h.block || !h.continue)) return { blocked: (h.block ? h.reason : h.stopReason) || "a plugin hook blocked this message.", blocks: [] };
    return { blocked: null, blocks: h?.additionalContext.length ? [{ type: "text", text: hookContextText("UserPromptSubmit", h.additionalContext) }] : [] };
  };

  /** A prompt a hook blocked: back into the composer (an empty chat the send created goes away), with the reason. */
  const restoreBlocked = (docKey: string, threadId: string, text: string, attachments: Attachment[], reason: string) => {
    const t = get().chats[docKey]?.data.threads.find((x) => x.id === threadId);
    if (t && t.messages.length === 0)
      updateSlot(docKey, (d) => ({ ...d, threads: d.threads.filter((x) => x.id !== threadId), activeThreadId: d.activeThreadId === threadId ? null : d.activeThreadId }));
    const key = get().chats[docKey]?.data.activeThreadId === threadId ? threadId : draftKey(get());
    const tags = sentTags?.text === text ? sentTags.tags : [];
    set((s) => ({
      drafts: { ...s.drafts, [key]: [text, s.drafts[key]].filter(Boolean).join("\n\n") },
      tags: tags.length ? { ...s.tags, [key]: [...(s.tags[key] ?? []), ...tags] } : s.tags,
      attachments: { ...s.attachments, [key]: [...attachments, ...(s.attachments[key] ?? [])] },
      notice: `Not sent: ${reason}`,
      focusTick: s.focusTick + 1,
    }));
  };

  /** Room for one more automatic turn this hour (plugin wakes and Stop-hook continuations together); counts it if so. */
  const allowAutoTurn = (): boolean => {
    const { allowed, recent } = autoTurnAllowed(autoTimes, Date.now(), get().settings.pluginWakesPerHour ?? 20);
    autoTimes = allowed ? [...recent, Date.now()] : recent;
    return allowed;
  };

  /**
   * A Stop hook blocked the end of the turn: the turn continues with its reason as a system event, in the same chat and
   * with the same run settings, together with the follow-ups the user queued meanwhile. False when automatic turns are
   * paused (the loop guard): the caller then flushes the queue as usual.
   */
  const continueAfterStop = (docKey: string, threadId: string, reason: string, run: RunOverride): boolean => {
    if (!allowAutoTurn()) {
      set({ notice: `A plugin asked the agent to keep going, but automatic turns are paused (${get().settings.pluginWakesPerHour ?? 20} per hour). Send a message to continue.` });
      return false;
    }
    const q = get().queue[threadId] ?? [];
    const n = q.findIndex((m) => m.staged || m.run);
    const batch = n < 0 ? q : q.slice(0, n);
    set((s) => ({ queue: { ...s.queue, [threadId]: n < 0 ? [] : q.slice(n) } }));
    const next: RunOverride = { model: run.model, effort: run.effort, autoApprove: run.autoApprove, label: run.label, maxSteps: run.maxSteps, longRun: run.longRun, continuation: true, fromWake: run.fromWake, fromHook: true };
    const release = beginPreparing(docKey, threadId);
    void (async () => {
      // The user already has the answer above: the continuation does only what the plugin asks, without a recap.
      const note = "\n\n(Unify3D: the user already sees your answer above. Do only what this asks, then end your turn without repeating or summarising it.)";
      const blocks: ContentBlockParam[] = [{ type: "text", text: systemEventText("stop", "Stop hook", (reason || "A plugin's Stop hook asked you to continue.") + note) }];
      let refs: Record<string, string> = {};
      if (batch.length) {
        // The queued follow-ups are the user's words: UserPromptSubmit fires for them (a block sends them back).
        const text = batch.map((m) => m.text).filter(Boolean).join("\n\n");
        const attachments = batch.flatMap((m) => m.attachments);
        const h = text ? await promptHook(docKey, threadId, text) : { blocked: null, blocks: [] };
        if (h.blocked) restoreBlocked(docKey, threadId, text, attachments, h.blocked);
        else {
          blocks.push(...queuedBlocks(batch), ...h.blocks);
          const ctx = await buildContext(docKey, threadId, false, { noView: true });
          blocks.push(...ctx.blocks);
          refs = ctx.refs;
        }
      }
      await submitBlocks(docKey, threadId, blocks, refs, next);
    })().finally(release);
    return true;
  };

  /**
   * The chat plugin wakes go to: the one open in the panel, with or without history (agent-bridge is one connection per
   * SolidWorks that talks to whatever chat the user has open). null: the new-chat page is showing, so the wake starts a
   * chat there.
   */
  const wakeThread = (): string | null => {
    const d = get().chats[WORKSPACE]?.data;
    const id = d?.activeThreadId;
    return id && d.threads.some((t) => t.id === id) ? id : null;
  };

  /** Wakes the add-in holds (session.takeWakes): taken at panel start and on every session.wake nudge. */
  const pullWakes = async () => {
    const list = await rpc<Wake[] | null>("session.takeWakes").catch(() => null);
    for (const w of list ?? []) await receiveWake(w);
  };

  /** A plugin woke the agent: into the wake chat now when it is idle, else when its run (and any question) ends. */
  const receiveWake = async (w: Wake) => {
    if (!w?.id || seenWakes.has(w.id)) return;
    seenWakes.add(w.id);
    await loadChat(WORKSPACE);
    let threadId = wakeThread();
    if (!threadId) {
      // The new-chat page is open: the wake starts that chat (named after the plugin) and it stays the open one, with
      // anything the user had typed there moved into it.
      const label = w.from ?? get().plugins.find((p) => p.name === w.plugin)?.displayName ?? w.plugin;
      const created: Thread = {
        id: uid(),
        title: label || "Plugin",
        createdAt: now(),
        updatedAt: now(),
        model: get().settings.defaultModel,
        effort: get().settings.defaultEffort,
        messages: [],
        turns: [],
      };
      updateSlot(WORKSPACE, (d) => ({ ...d, threads: [created, ...d.threads], activeThreadId: created.id }));
      const draft = `new:${WORKSPACE}`;
      set((s) => ({
        drafts: { ...s.drafts, [created.id]: s.drafts[draft] ?? "", [draft]: "" },
        tags: { ...s.tags, [created.id]: s.tags[draft] ?? [], [draft]: [] },
      }));
      threadId = created.id;
    }
    // Busy (a run, or one being prepared), or its last turn was stopped or failed: held, shown in the composer.
    if (busy(threadId) || endsOnUser(threadId)) {
      const id = threadId;
      set((s) => ({ wakes: { ...s.wakes, [id]: [...(s.wakes[id] ?? []), w] } }));
      reportLinkState([w], "queued");
      return;
    }
    await deliverWakes(threadId, [w]);
  };

  /** After a run that ended by itself: the wakes that waited for it go out together. */
  const flushWakes = (threadId: string) => {
    const list = get().wakes[threadId] ?? [];
    if (!list.length || busy(threadId) || endsOnUser(threadId)) return;
    set((s) => ({ wakes: { ...s.wakes, [threadId]: [] } }));
    void deliverWakes(threadId, list);
  };

  /**
   * A thread whose next run is being put together; the returned function ends that (startRun ends it as the run
   * starts). If no run started after all (a hook blocked the prompt, no API key), what was held meanwhile goes now:
   * messages the user sent, then wakes.
   */
  const beginPreparing = (docKey: string, threadId: string): (() => void) => {
    const token = {};
    let released = false;
    const release = () => {
      if (released) return;
      released = true;
      if (preparing.get(threadId)?.token !== token) return;
      preparing.delete(threadId);
      if (!busy(threadId) && !flushQueue(docKey, threadId, "end_turn")) flushWakes(threadId);
    };
    preparing.set(threadId, { token, release });
    return release;
  };

  /** A run is going in the thread, or about to start. */
  const busy = (threadId: string) => !!get().runs[threadId] || preparing.has(threadId);

  /** The workspace chat ends on a user message: its last turn was stopped or failed before the agent answered. */
  const endsOnUser = (threadId: string) => {
    const t = get().chats[WORKSPACE]?.data.threads.find((x) => x.id === threadId);
    return t?.messages[t.messages.length - 1]?.role === "user";
  };

  const wakeBlocks = (list: Wake[]): ContentBlockParam[] =>
    list.map((w) => ({ type: "text", text: systemEventText("wake", w.plugin || w.source, w.text, { from: w.from, ticket: w.ticket }) }));

  /**
   * Tickets of messages from Claude Desktop or Codex (the local link) per chat, with the index of the message they went
   * into: the chat's next turn that ends without a Stop-hook continuation reports its answer for them
   * (localLink.answer). Not saved: after a reload the add-in fails the tickets the old page had taken.
   */
  const linkTickets = new Map<string, { tickets: string[]; from: number }>();

  const ticketsOf = (list: Wake[]) => list.map((w) => w.ticket).filter((t): t is string => !!t);

  /** The add-in's tickets follow their wakes: queued (held in a busy chat), working (in a turn) or paused. */
  const reportLinkState = (list: Wake[], state: "queued" | "working" | "paused") => {
    const tickets = ticketsOf(list);
    if (tickets.length) rpc("localLink.state", { tickets, state }).catch(() => {});
  };

  /**
   * An approval or question card opened (or closed) in a chat whose turn holds tickets: Claude Desktop / Codex hear that the
   * answer waits for the user here (approval), not just that the agent is still working.
   */
  const reportLinkWaiting = (threadId: string, waiting: boolean) => {
    const held = linkTickets.get(threadId);
    if (held?.tickets.length) rpc("localLink.state", { tickets: held.tickets, state: waiting ? "approval" : "working" }).catch(() => {});
  };

  /** Wakes just added to a chat's last message: their tickets wait for that chat's answer. */
  const holdLinkTickets = (docKey: string, threadId: string, list: Wake[], state: "working" | "paused") => {
    const tickets = ticketsOf(list);
    if (!tickets.length) return;
    const at = Math.max(0, (get().chats[docKey]?.data.threads.find((t) => t.id === threadId)?.messages.length ?? 1) - 1);
    const held = linkTickets.get(threadId);
    linkTickets.set(threadId, { tickets: [...(held?.tickets ?? []), ...tickets], from: Math.min(held?.from ?? at, at) });
    reportLinkState(list, state);
  };

  /** A turn in the chat ended: its final reply goes to every ticket the chat holds (done, stopped or failed). */
  const reportLinkAnswer = (docKey: string, threadId: string, outcome: string, error?: string) => {
    const held = linkTickets.get(threadId);
    if (!held) return;
    linkTickets.delete(threadId);
    const state = linkState(outcome);
    const answer = linkAnswerText(get().chats[docKey]?.data.threads.find((t) => t.id === threadId)?.messages ?? [], held.from);
    const text = state === "done" || answer !== NO_WRITTEN_REPLY ? answer : "";
    rpc("localLink.answer", { tickets: held.tickets, threadId, state, text, error: state === "failed" ? error || "unknown error" : null }).catch(() => {});
  };

  /** Wakes saved in a chat are acked: the add-in stops handing them over again. */
  const ackWakes = (list: Wake[]) => {
    for (const w of list) rpc("session.ackWake", { id: w.id }).catch(() => {});
  };

  /**
   * Wakes into a chat as one system-event message, acked once it is saved there (a reload before that gets them handed
   * over again), then a turn - unless automatic turns are paused: then they wait in the chat for the user's next
   * message. Never through send(): that would answer a pending question, or end up in the composer on Stop. The turn is
   * marked fromWake. Whether it follows the approval setting or asks before any change is the user's choice per sender
   * (wakeTrusted): their console, and by default their other agents too.
   */
  const deliverWakes = async (threadId: string, list: Wake[]) => {
    const blocks = wakeBlocks(list);
    const ack = () => ackWakes(list);
    if (allowAutoTurn()) {
      const appended = () => {
        ack();
        holdLinkTickets(WORKSPACE, threadId, list, "working");
      };
      const release = beginPreparing(WORKSPACE, threadId);
      try {
        const trustedWake = list.every((w) => wakeTrusted(w, get().settings));
        await submitBlocks(WORKSPACE, threadId, blocks, {}, { autoApprove: false, fromWake: true, trustedWake }, appended);
      } finally {
        release();
      }
      return;
    }
    updateThread(WORKSPACE, threadId, (t) => {
      const last = t.messages[t.messages.length - 1];
      const messages: MessageParam[] =
        last && last.role === "user" ? [...t.messages.slice(0, -1), { role: "user", content: [...asBlocks(last.content), ...blocks] }] : [...t.messages, { role: "user", content: blocks }];
      return { ...t, messages };
    });
    await persist(WORKSPACE);
    ack();
    holdLinkTickets(WORKSPACE, threadId, list, "paused"); // answered at the end of the user's next turn here
    const perHour = get().settings.pluginWakesPerHour ?? 20;
    set({
      notice:
        list[0].ticket && list[0].from
          ? `${list[0].from} sent a message, but automatic turns are paused (${perHour} per hour): the agent answers it with your next message.`
          : `${list[0].plugin || "A plugin"} added news to the chat, but automatic turns are paused (${perHour} per hour): the agent sees it with your next message.`,
    });
  };


  /**
   * After a run in a chat with an open long tool run: the idle clock restarts, unless the tool reported in this turn
   * that it is done (its record's status), which ends the follow-up settings.
   */
  const noteToolRunActivity = (docKey: string, threadId: string, turnIndex: number) => {
    const t = get().chats[docKey]?.data.threads.find((x) => x.id === threadId);
    const tr = t?.toolRun;
    if (!t || !tr) return;
    const from = t.turns[turnIndex]?.msgIndex ?? 0;
    if (runReportsDone(t.messages, from, tr.requires)) {
      updateThread(docKey, threadId, (x) => ({ ...x, toolRun: null }));
      set({ notice: `${tr.label} is done - follow-ups in this chat use the chat's own model and approval again.` });
      return;
    }
    updateThread(docKey, threadId, (x) => (x.toolRun ? { ...x, toolRun: { ...x.toolRun, lastActive: Date.now() } } : x));
  };

  /** End a long tool run's follow-up settings because the user changed the chat's own model or effort. */
  const endToolRunWith = (docKey: string, threadId: string, label: string) => {
    updateThread(docKey, threadId, (t) => ({ ...t, toolRun: null }));
    set({ notice: `${label}: its run settings ended - follow-ups in this chat use the model and effort you chose, and your approval setting.` });
  };

  /**
   * The settings a run in this chat gets (lib/toolRun.ts). A tool or workflow run brings its own, and a long one opens
   * the chat's follow-up settings; any other tool or workflow run, or a fresh request such as making a new tool, closes
   * them. Anything else - a typed or spoken follow-up, Retry, a resend - inherits a long tool run's model, effort, step
   * limit and auto-approval while that run is open: one of its documents is active, it hasn't reported done, and the
   * chat was active within 2 hours. The user's read-only approval mode always wins over the tool's auto-approval.
   */
  const runSettings = (docKey: string, threadId: string, run: RunOverride): RunOverride => {
    const s = get();
    const thread = s.chats[docKey]?.data.threads.find((t) => t.id === threadId);
    if (!thread) return run;
    const doc = s.docs[s.activeDocKey ?? ""];
    if (run.tool || run.fresh) {
      const info = run.tool ? s.userTools.find((t) => t.name === run.tool) : undefined;
      const last = thread.messages[thread.messages.length - 1];
      // A long run is tied to its documents; without one there is nothing to scope it to, so it doesn't stay open.
      const toolRun: ToolRunSticky | null =
        run.tool && run.longRun && doc
          ? {
              tool: run.tool,
              label: info?.ribbonTitle || run.label || info?.title || run.tool, // short: it heads the banner chip
              model: run.model ?? thread.model,
              effort: run.effort ?? thread.effort,
              maxSteps: clampSteps(run.maxSteps, LONG_RUN_STEPS),
              autoApprove: !!run.autoApprove,
              requires: info?.requires ?? [],
              doc: { key: doc.key, title: doc.title, path: doc.path || null },
              docs: [],
              // The message this run's prompt lands in (submitBlocks extends a trailing user message).
              msgIndex: last && last.role === "user" ? thread.messages.length - 1 : thread.messages.length,
              startedAt: Date.now(),
              lastActive: Date.now(),
            }
          : null;
      if (toolRun || thread.toolRun) updateThread(docKey, threadId, (t) => ({ ...t, toolRun }));
      return toolRun ? { ...run, inToolRun: true } : run;
    }
    const tr = thread.toolRun;
    if (!tr || run.model) return run;
    const state = stickyState(tr, Date.now(), doc);
    if (state === "expired") {
      updateThread(docKey, threadId, (t) => ({ ...t, toolRun: null }));
      return run;
    }
    if (state === "other_doc") return run; // paused while another document is active
    const autoApprove = tr.autoApprove !== false && s.settings.approval !== "read_only";
    return { model: tr.model, effort: tr.effort, autoApprove, label: tr.label, maxSteps: tr.maxSteps, longRun: true, ...run, inToolRun: true };
  };

  /**
   * Keep the voice setting true to what's on disk: it is only on with a downloaded model. A finished download the
   * user asked for selects that model and turns voice on; a cancelled or failed one leaves it off.
   */
  const reconcileVoice = (st: VoiceStatus) => {
    set({ voiceStatus: st });
    if (st.download) return; // wait for it
    const s = get();
    const installed = st.models.filter((m) => m.installed);
    const want = s.voiceWant;
    if (want) {
      set({ voiceWant: null });
      if (installed.some((m) => m.id === want)) {
        get().saveSettings({ voiceEnabled: true, voiceModel: want, voiceAsked: true });
        set({ notice: `Voice is on: hold ${talkKeyName(s.settings.voiceKey)} in SolidWorks and talk.` });
        return;
      }
    }
    const selected = s.settings.voiceModel ?? "base.en";
    if (installed.some((m) => m.id === selected)) return;
    if (installed.length > 0) get().saveSettings({ voiceModel: (installed.find((m) => m.id === "base.en") ?? installed[0]).id });
    else if (s.settings.voiceEnabled) get().saveSettings({ voiceEnabled: false });
  };

  /** Remember that a chat worked in the active document (shown in the chat list; the chat follows the work). */
  const noteFile = (threadId: string) => {
    const s = get();
    const doc = s.docs[s.activeDocKey ?? ""];
    if (!doc) return;
    const slot = s.chats[WORKSPACE];
    const t = slot?.data.threads.find((x) => x.id === threadId);
    if (!t) return;
    const files = t.files ?? [];
    if (files.some((f) => f.key === doc.key || (doc.path ? f.path === doc.path : !f.path && f.title === doc.title))) return;
    updateSlot(WORKSPACE, (d) => ({
      ...d,
      threads: d.threads.map((x) => (x.id === threadId ? { ...x, files: [...files, { title: doc.title, path: doc.path || null, key: doc.key }] } : x)),
    }));
  };

  /** Chats an older version saved inside a file: copied into the workspace list once (by thread id). */
  const importDocChats = async (docKey: string) => {
    if (!docKey || docKey === WORKSPACE || !get().docs[docKey]) return;
    const res = await rpc<{ data: ChatData }>("chat.load", { docKey }).catch(() => null);
    const theirs = res?.data?.threads ?? [];
    if (theirs.length === 0) return;
    await loadChat(WORKSPACE);
    const slot = get().chats[WORKSPACE];
    if (!slot) return;
    const have = new Set(slot.data.threads.map((t) => t.id));
    const done = new Set(slot.data.imported ?? []);
    const doc = get().docs[docKey];
    const fresh = theirs
      .filter((t) => !have.has(t.id) && !done.has(t.id))
      .map((t) => ({ ...t, turns: t.turns ?? [], messages: t.messages ?? [], files: t.files ?? (doc ? [{ title: doc.title, path: doc.path || null, key: doc.key }] : []) }));
    if (fresh.length === 0) return;
    updateSlot(WORKSPACE, (d) => ({
      ...d,
      threads: [...d.threads, ...fresh].sort((a, b) => (b.updatedAt ?? "").localeCompare(a.updatedAt ?? "")),
      imported: [...(d.imported ?? []), ...fresh.map((t) => t.id)],
    }));
    await persist(WORKSPACE);
  };

  /** Read the reply aloud (Settings > Voice): after voice messages, or always. */
  const speakReply = (docKey: string, threadId: string, outcome: string) => {
    const mode = get().settings.speakReplies ?? "off";
    const fromVoice = speakAfter.delete(threadId);
    if (outcome !== "end_turn" || mode === "off" || (mode === "voice" && !fromVoice)) return;
    const messages = get().chats[docKey]?.data.threads.find((t) => t.id === threadId)?.messages ?? [];
    const last = [...messages].reverse().find((m) => m.role === "assistant");
    if (!last || typeof last.content === "string") return;
    const text = last.content
      .filter((b) => b.type === "text")
      .map((b) => (b as { text: string }).text)
      .join("\n\n");
    if (text.trim()) rpc("voice.speak", { text }).catch(() => {});
  };

  /** A voice message arrived from the add-in: into that document's chat, sent at once or left to check first. */
  const receiveVoice = async (p: VoicePayload) => {
    if (p === lastVoice) return; // the same event twice (a panel reload re-registers listeners)
    lastVoice = p;
    set({ voice: VOICE_IDLE });
    set({ panel: "chat" });
    const s = get();
    // The agent is waiting on a question in this chat: the voice message is the answer, not a new message.
    const thread = currentThread(s);
    const asked = thread ? s.runs[thread.id]?.question : null;
    if (thread && asked?.selection) {
      // Waiting for a selection: speaking counts as Done, with the words as the note (Done takes a fresh snapshot, so
      // whatever was selected while talking is included).
      const note = p.text.trim();
      const selected = p.events.some((e) => e.kind === "select");
      if (!note && !selected) return;
      if ((s.settings.voiceSend ?? "release") === "release") void get().answerSelection(thread.id, true, note);
      else get().setAnswerDraft(thread.id, [s.answerDrafts[thread.id]?.trim(), note].filter(Boolean).join(" "));
      return;
    }
    if (thread && asked && !asked.form && !asked.secret) {
      const picked = p.events.filter((e) => e.kind === "select" && e.t > 0).flatMap((e) => (e.items ?? []).map((i) => `${i.type}${i.label ? ` of ${i.label}` : ""}`));
      const answer = [p.text.trim(), picked.length ? `(while answering I selected: ${picked.join("; ")})` : ""].filter(Boolean).join(" ");
      if (!answer) return;
      if ((s.settings.voiceSend ?? "release") === "release") get().answerQuestion(thread.id, answer);
      else get().setAnswerDraft(thread.id, [s.answerDrafts[thread.id]?.trim(), answer].filter(Boolean).join(" "));
      return;
    }
    const key = draftKey(s);
    const seconds = Math.max(1, Math.round(p.durationMs / 1000));
    const attachment: Attachment = { id: uid(), kind: "voice", name: `Voice ${seconds} s`, voice: p };
    const text = [s.drafts[key]?.trim(), p.text.trim()].filter(Boolean).join(" ");
    set((st) => ({
      drafts: { ...st.drafts, [key]: text },
      attachments: { ...st.attachments, [key]: [...(st.attachments[key] ?? []).filter((a) => a.kind !== "voice"), attachment] },
      focusTick: st.focusTick + 1,
    }));
    if ((s.settings.voiceSend ?? "release") === "release") await get().send(text);
  };

  /**
   * After a run: queued follow-ups go out together as the next prompt. If the user pressed Stop they go back into
   * the composer instead (nothing is lost, nothing is sent behind their back); after an error they wait. True when
   * a new run is on its way.
   */
  const flushQueue = (docKey: string, threadId: string, outcome: string): boolean => {
    const q = get().queue[threadId] ?? [];
    if (q.length === 0 || outcome === "error") return false;
    if (outcome === "aborted") {
      // Stop: follow-ups go back into the composer, queued tool runs are dropped (the user can click the tool again).
      set((s) => ({ queue: { ...s.queue, [threadId]: [] } }));
      const typed = q.filter((m) => !m.staged);
      const text = typed.map((m) => m.text).filter(Boolean).join("\n\n");
      const attachments = typed.flatMap((m) => m.attachments);
      set((s) => ({
        drafts: { ...s.drafts, [threadId]: [text, s.drafts[threadId]].filter(Boolean).join("\n\n") },
        attachments: { ...s.attachments, [threadId]: [...attachments, ...(s.attachments[threadId] ?? [])] },
      }));
      return false;
    }
    if (q[0].staged || q[0].run) {
      // One tool or workflow run, with its own settings; the rest wait for the next turn to end.
      const [first, ...rest] = q;
      set((s) => ({ queue: { ...s.queue, [threadId]: rest } }));
      if (first.staged) void submitStaged(docKey, threadId, first.staged.blocks, first.staged.refs, first.staged.run);
      else void submitPrompt(docKey, threadId, first.text, first.attachments, first.run);
      return true;
    }
    const n = q.findIndex((m) => m.staged || m.run);
    const batch = n < 0 ? q : q.slice(0, n);
    set((s) => ({ queue: { ...s.queue, [threadId]: n < 0 ? [] : q.slice(n) } }));
    void submitPrompt(docKey, threadId, batch.map((m) => m.text).filter(Boolean).join("\n\n"), batch.flatMap((m) => m.attachments));
    return true;
  };

  /** The agent needs the user (a question, an approval): show that chat and bring the pane up - tool runs happen out of sight. */
  const revealRun = async (docKey: string, threadId: string) => {
    const s = get();
    const shown = s.panel === "chat" && s.chats[docKey]?.data.activeThreadId === threadId;
    if (!shown) {
      updateSlot(docKey, (d) => ({ ...d, activeThreadId: threadId }));
      set({ panel: "chat" });
    }
    rpc("app.showPane").catch(() => {});
  };

  /** Add a prompt (text, attachments, the user's selection and view) to a thread and run the agent. */
  const submitPrompt = async (docKey: string, threadId: string, trimmed: string, attachments: Attachment[], run: RunOverride = {}) => {
    const release = beginPreparing(docKey, threadId);
    try {
      await preparePrompt(docKey, threadId, trimmed, attachments, run);
    } finally {
      release();
    }
  };

  const preparePrompt = async (docKey: string, threadId: string, trimmed: string, attachments: Attachment[], run: RunOverride) => {
    // Plugins see the prompt first (UserPromptSubmit): a block puts it back into the composer.
    const hook = await promptHook(docKey, threadId, trimmed);
    if (hook.blocked) {
      restoreBlocked(docKey, threadId, run.input ?? trimmed, attachments, hook.blocked);
      return;
    }
    // Plugin events held for this chat (it was busy, or its last turn was stopped or failed) go with the user's message.
    const held = get().wakes[threadId] ?? [];
    if (held.length) set((s) => ({ wakes: { ...s.wakes, [threadId]: [] } }));
    // The agent's drawings on the view belong to the conversation so far: a new message starts clean.
    if (get().overlayCount > 0) get().clearOverlay();
    const blocks: ContentBlockParam[] = [];
    if (trimmed) blocks.push({ type: "text", text: trimmed });
    blocks.push(...hook.blocks);
    // A voice message's selections are numbered first, so the context block's ids continue after them.
    const thread = get().chats[docKey]?.data.threads.find((t) => t.id === threadId);
    const taken = refBase(thread?.selRefs);
    let reserved = 0;
    const voiceRefs: Record<string, string> = {};
    for (const a of attachments) {
      if (a.kind === "voice") {
        const v = voiceBlocks(a.voice, taken + reserved);
        blocks.push(...v.blocks);
        Object.assign(voiceRefs, v.refs);
        reserved += v.count;
      } else blocks.push(...attachmentBlocks(a));
    }
    const hasVoice = attachments.some((a) => a.kind === "voice");
    if (hasVoice) speakAfter.add(threadId);
    // A voice message carries its own views; the context block then only adds the document, camera and selection.
    const ctx = await buildContext(docKey, threadId, attachments.some((a) => a.kind === "image" && a.name.startsWith(MARKUP_NAME)), {
      reserved,
      noView: hasVoice,
    });
    blocks.push(...ctx.blocks);
    const appended = () => {
      ackWakes(held);
      holdLinkTickets(docKey, threadId, held, "working"); // the user's turn answers them too
    };
    await submitBlocks(docKey, threadId, [...wakeBlocks(held), ...blocks], { ...voiceRefs, ...ctx.refs }, run, held.length ? appended : undefined);
  };

  /**
   * A ribbon tool run's staged prompt: UserPromptSubmit fires for it like a typed prompt (a block cancels the run, with
   * the reason); what the hooks add goes after the tool's head line.
   */
  const submitStaged = async (docKey: string, threadId: string, blocks: ContentBlockParam[], refs: Record<string, string>, run: RunOverride): Promise<boolean> => {
    const release = beginPreparing(docKey, threadId);
    try {
      const head = blocks[0]?.type === "text" ? blocks[0].text : "";
      const hook = await promptHook(docKey, threadId, head);
      if (hook.blocked) {
        set({ notice: `${run.label ?? "The tool"} didn't run: ${hook.blocked}` });
        // A chat made just for this run goes away again.
        if (get().chats[docKey]?.data.threads.find((t) => t.id === threadId)?.messages.length === 0)
          updateSlot(docKey, (d) => ({ ...d, threads: d.threads.filter((t) => t.id !== threadId), activeThreadId: d.activeThreadId === threadId ? null : d.activeThreadId }));
        return false;
      }
      await submitBlocks(docKey, threadId, [...blocks.slice(0, 1), ...hook.blocks, ...blocks.slice(1)], refs, run);
      return true;
    } finally {
      release();
    }
  };

  /**
   * Add a user message (already built) to a thread and run the agent on it, with optional settings for this run.
   * afterAppend runs once the message is saved, before the run starts (plugin wakes are acked there).
   */
  const submitBlocks = async (docKey: string, threadId: string, blocks: ContentBlockParam[], refs: Record<string, string>, given: RunOverride = {}, afterAppend?: () => void) => {
    const run = runSettings(docKey, threadId, given);
    updateThread(docKey, threadId, (t) => {
      const last = t.messages[t.messages.length - 1];
      // After a stopped or failed turn the history can end on a user message; extend it rather than
      // sending two user messages in a row (no assistant output follows it, so nothing is invalidated).
      const messages: MessageParam[] =
        last && last.role === "user"
          ? [...t.messages.slice(0, -1), { role: "user", content: [...asBlocks(last.content), ...blocks] }]
          : [...t.messages, { role: "user", content: blocks }];
      const msgIndex = messages.length - 1;
      return {
        ...t,
        messages,
        selRefs: { ...(t.selRefs ?? {}), ...refs },
        turns: [...t.turns, { at: now(), session: get().sessionStart, msgIndex, model: run.model ?? t.model, effort: run.effort ?? t.effort, usage: { ...ZERO_USAGE } }],
      };
    });
    noteFile(threadId);
    await persist(docKey);
    afterAppend?.();
    await startRun(docKey, threadId, run.label ? { label: run.label, startedAt: Date.now() } : {}, run);
  };

  /** What slash commands and @ tags can reach. */
  const sources = (): CommandSources => {
    const s = get();
    return { commands: s.commands, skills: s.skills, tools: s.userTools, plugins: s.plugins, agents: s.agents };
  };

  /** Clear the composer (text, attachments, tags) of the current draft. */
  const clearComposer = () => {
    const key = draftKey(get());
    set((s) => ({ drafts: { ...s.drafts, [key]: "" }, tags: { ...s.tags, [key]: [] } }));
  };

  /**
   * A typed "/name args": a built-in, a plugin command (expanded by the add-in; its model applies to this run), a
   * skill ("[Use skill: x]" plus the arguments), or a tool or workflow by name. False: not a command; send it as text.
   */
  const runSlash = async (name: string, args: string, typed: string, tagText: string): Promise<boolean> => {
    const s = get();
    const target = resolveSlash(name, sources());
    const withTags = (text: string) => (tagText ? `${tagText}\n${text}` : text);
    switch (target.kind) {
      case "unknown":
        set({ notice: `There is no /${name} command. Type / to see the commands.` });
        return true;
      case "ambiguous":
        set({ notice: `Several plugins have /${name}: type ${target.options.map((o) => `/${o}`).join(" or ")}.` });
        return true;
      case "tool":
        clearComposer();
        await get().runNamedTool(target.tool.name, args);
        return true;
      case "skill":
        clearComposer();
        await get().send(withTags(`[Use skill: ${target.skill.id}]${args ? `\n${args}` : ""}`), { input: typed });
        return true;
      case "command": {
        const c = target.command;
        let res: ExpandedCommand;
        try {
          res = await rpc<ExpandedCommand>("plugins.expandCommand", { name: c.id, args });
        } catch (err: any) {
          set({ notice: `Couldn't run /${c.id}: ${err.message}` });
          return true;
        }
        clearComposer();
        if (res?.note) set({ notice: res.note });
        // An alias maps into this chat's provider (design 7.4).
        const chatModel = currentThread(get())?.model ?? get().settings.defaultModel;
        const model = resolveModel(res?.model ?? c.model, { chatModel, hasKey: keys.has }) ?? undefined;
        await get().send(withTags(`${commandHead(c.id, args)}\n${res?.text ?? ""}`), { model, command: c.id, input: typed });
        return true;
      }
      case "builtin":
        break;
    }
    const done = () => clearComposer();
    switch (target.name) {
      case "help": {
        done();
        const plugin = s.commands.length ? ` Plugin commands: ${s.commands.map((c) => `/${c.id}`).join(", ")}.` : "";
        set({ notice: `Commands: ${BUILTIN_COMMANDS.map((b) => `/${b.name}`).join(", ")}.${plugin} Your tools, workflows and skills run as /their-name. Type / to browse, @ to tag a plugin, tool, skill or agent.` });
        return true;
      }
      case "clear":
        done();
        set({ panel: "chat" });
        await get().newChat();
        return true;
      case "plugins":
        done();
        set({ panel: "plugins" });
        return true;
      case "settings":
        done();
        set({ panel: "settings" });
        return true;
      case "new-tool":
        done();
        set({ panel: "newTool" });
        return true;
      case "new-plugin":
        done();
        await get().runNamedTool("create-plugin", args);
        return true;
      case "tools": {
        done();
        const names = s.userTools.map((t) => `${t.name}${t.kind === "tool" ? "" : " (workflow)"}`);
        set({ notice: names.length ? `Your tools and workflows: ${names.join(", ")}. Run one with /run <name> or /<name>.` : "No tools or workflows yet. /new-tool makes one." });
        return true;
      }
      case "run": {
        const [tool, ...rest] = args.split(/\s+/);
        const t = tool ? s.userTools.find((x) => x.name.toLowerCase() === tool.toLowerCase()) : undefined;
        if (!t) {
          set({ notice: tool ? `There is no tool or workflow "${tool}". /tools lists them.` : "Which one? /run <tool>, e.g. /run smart-rebuild." });
          return true;
        }
        done();
        await get().runNamedTool(t.name, rest.join(" "));
        return true;
      }
      case "model": {
        const chatModel = currentThread(s)?.model ?? s.settings.defaultModel;
        const m = findModel(args, { chatModel, hasKey: keys.has });
        if (!m) {
          set({ notice: `Models: ${pickerModels(chatModel, keys.has).map((x) => x.label).join(", ")} - e.g. /model sonnet.` });
          return true;
        }
        done();
        get().setThreadModel(m.id);
        set({ notice: `Model: ${m.label}.` });
        return true;
      }
      case "effort": {
        const a = args.toLowerCase().replace(/[\s-]/g, "");
        // Only the levels the chat's model offers.
        const offered = effortsFor(currentThread(s)?.model ?? s.settings.defaultModel);
        const e = offered.find((x) => x.id === a || x.label.toLowerCase().replace(/[\s-]/g, "") === a);
        if (!e) {
          set({ notice: `Effort levels: ${offered.map((x) => x.id).join(", ")} - e.g. /effort high.` });
          return true;
        }
        done();
        get().setThreadEffort(e.id);
        set({ notice: `Effort: ${e.label}.` });
        return true;
      }
    }
    return false;
  };

  /** Fetch everything plugin-related the panel shows or the agent uses (older add-ins: empty). */
  const loadPluginState = async () => {
    const list = <T,>(method: string) => rpc<T[] | null>(method).then((r) => r ?? []).catch(() => [] as T[]);
    const [plugins, marketplaces, commands, skills, agents, mcpServers, session] = await Promise.all([
      list<PluginInfo>("plugins.list"),
      list<MarketplaceInfo>("plugins.marketplaces"),
      list<CommandInfo>("plugins.commands"),
      list<SkillInfo>("plugins.skills"),
      list<AgentInfo>("plugins.agents"),
      list<McpServerStatus>("mcp.status"),
      // The first session.info of a session starts its SessionStart hooks (the add-in fires them once).
      rpc<SessionInfo | null>("session.info", { panel: PANEL_ID }).catch(() => null),
    ]);
    set({ plugins, marketplaces, commands, skills, agents, mcpServers });
    applySession(session);
  };

  /** A new session.info: kept, and SessionStart notices for the user (systemMessage) not shown yet go to the notice bar. */
  const applySession = (session: SessionInfo | null) => {
    if (!session) return;
    set({ session });
    const notices = session.sessionNotices ?? [];
    if (notices.length > shownSessionNotices) {
      set({ notice: notices.slice(shownSessionNotices).join(" ") });
      shownSessionNotices = notices.length;
    }
  };

  /**
   * A workflow run, with notes as its inputs. Not a follow-up: it ends a long tool run's settings in this chat. A long
   * workflow (front matter long_run) gets its step limit and 1-hour cache and opens its own follow-up settings - with
   * the chat's model and approval (only ribbon tool runs are auto-approved).
   */
  const runWorkflow = async (name: string, notes: string) => {
    const tool = get().userTools.find((t) => t.name === name);
    const longRun = !!tool?.longRun;
    const run: RunOverride = { tool: name, longRun, maxSteps: clampSteps(tool?.maxSteps ?? (longRun ? LONG_RUN_STEPS : DEFAULT_STEPS)) };
    await get().send(`[Run workflow: ${name}]${notes ? `\n${notes}` : ""}`, run);
  };

  return {
    booted: false,
    sessionStart: Date.now(),
    overlayCount: 0,

    clearOverlay() {
      set({ overlayCount: 0 });
      rpc("overlay.clear").catch(() => {});
    },
    bootError: null,
    host: null,
    settings: DEFAULT_SETTINGS,
    apiKeySources: NO_KEYS,
    keyFocus: null,
    swTheme: null,
    tools: [],
    userTools: [],
    update: null,
    updateDialog: false,
    toolOutcome: null,
    crash: null,
    ribbonStale: false,
    docs: {},
    activeDocKey: null,
    viewKey: WORKSPACE,
    chats: {},
    selection: {},
    selectionTotal: {},
    includeSelection: true,
    excludedSel: [],
    agentSel: {},
    markup: { active: false, tool: "pen", color: MARKUP_COLORS[0], strokes: 0, frames: 0 },
    voice: VOICE_IDLE,
    voiceStatus: null,
    voiceWant: null,
    tourStep: null,

    enableVoice(modelId) {
      const s = get();
      const id = modelId ?? s.settings.voiceModel ?? "base.en";
      const models = s.voiceStatus?.models ?? [];
      if (models.some((m) => m.id === id && m.installed)) {
        get().saveSettings({ voiceEnabled: true, voiceModel: id, voiceAsked: true });
        return;
      }
      // Voice can only be on with a model on disk: download it, and turn on when it arrives.
      set({ voiceWant: id });
      get().saveSettings({ voiceAsked: true });
      rpc("voice.download", { id }).catch((err) => set({ voiceWant: null, notice: `Couldn't download the speech model: ${err.message}` }));
    },

    disableVoice() {
      if (get().voiceWant) rpc("voice.cancelDownload").catch(() => {});
      set({ voiceWant: null });
      get().saveSettings({ voiceEnabled: false });
    },

    downloadVoiceModel(id) {
      set({ voiceWant: id });
      rpc("voice.download", { id }).catch((err) => set({ voiceWant: null, notice: `Couldn't download the speech model: ${err.message}` }));
    },

    startTour() {
      set({ tourStep: 0, panel: "chat" });
    },

    setTourStep(step) {
      set({ tourStep: step });
    },

    endTour() {
      set({ tourStep: null });
      get().saveSettings({ tourDone: true });
      // Without a key nothing works yet: Options is where it goes.
      if (!anyKey(get())) set({ panel: "settings", notice: noKeyNotice(get().settings.defaultModel, false) });
    },
    cancelVoice() {
      set({ voice: VOICE_IDLE });
      rpc("voice.cancel").catch(() => {});
    },
    runs: {},
    queue: {},
    drafts: {},
    attachments: {},
    tags: {},
    plugins: [],
    marketplaces: [],
    commands: [],
    skills: [],
    agents: [],
    mcpServers: [],
    session: null,
    knowledge: null,
    memory: null,
    pluginsTab: "discover",
    wakes: {},

    addTag(tag) {
      const key = draftKey(get());
      set((s) => {
        const list = s.tags[key] ?? [];
        if (list.some((t) => t.kind === tag.kind && t.name === tag.name)) return {};
        return { tags: { ...s.tags, [key]: [...list, tag] } };
      });
    },

    removeTag(index) {
      const key = draftKey(get());
      set((s) => ({ tags: { ...s.tags, [key]: (s.tags[key] ?? []).filter((_, i) => i !== index) } }));
    },

    async submitInput(text) {
      const key = draftKey(get());
      const tags = get().tags[key] ?? [];
      const tagText = tagLines(tags);
      const trimmed = text.trim();
      const slash = parseSlash(trimmed);
      if (slash && (await runSlash(slash.name, slash.args, trimmed, tagText))) return;
      if (!trimmed && (get().attachments[key] ?? []).length === 0) return; // tags alone aren't a message
      set((s) => ({ tags: { ...s.tags, [key]: [] } }));
      sentTags = tags.length ? { text: trimmed, tags } : null;
      await get().send(tagText && trimmed ? `${tagText}\n${trimmed}` : trimmed || tagText, tagText ? { input: trimmed } : undefined);
    },

    async loadPlugins() {
      await loadPluginState();
    },

    setPluginsTab(tab) {
      set({ pluginsTab: tab });
    },

    async runNamedTool(name, notes) {
      const t = get().userTools.find((x) => x.name === name);
      set({ panel: "chat" });
      // A ribbon tool opens its inputs (page or panel form) in the add-in, which hands the run back when it needs the agent.
      // "--profile "Shop A"" opens them with that profile loaded.
      if (t?.kind === "tool") {
        const { profile } = splitProfileArg(notes ?? "");
        await (profile ? rpc("toolProfiles.run", { tool: name, profile }) : rpc("userTools.run", { name })).catch((err) =>
          set({ notice: `Couldn't run ${t.title}: ${err.message}` }),
        );
        return;
      }
      if (!t && name !== "create-plugin") {
        set({ notice: `There is no tool or workflow "${name}".` });
        return;
      }
      await runWorkflow(name, (notes ?? "").trim());
    },
    panel: "chat",
    focusTick: 0,
    notice: null,
    watch: WATCH_IDLE,

    async boot() {
      try {
        const [host, tools] = await Promise.all([rpc<HostState>("app.getState"), rpc<ToolDef[]>("tools.list")]);
        const docs: Record<string, DocInfo> = {};
        for (const d of host.docs) docs[d.key] = d;
        if (host.activeDoc) docs[host.activeDoc.key] = host.activeDoc;
        const viewKey = WORKSPACE;
        let settings: Settings = { ...DEFAULT_SETTINGS, ...host.settings };
        if ((host.settings.settingsVersion ?? 1) < SETTINGS_VERSION) {
          // v2: the panel now follows the SolidWorks interface theme by default.
          settings = { ...settings, theme: "solidworks", settingsVersion: SETTINGS_VERSION };
          rpc("settings.save", settings);
        }
        if (host.update) set({ update: host.update });
        if (host.sessionStart) set({ sessionStart: Date.parse(host.sessionStart) || Date.now() });
        recordSession(get().sessionStart);
        if (host.resume) {
          // Back from "Save and restart": the documents reopen on their own (the add-in); bring back unsent drafts.
          set((s) => ({ drafts: { ...s.drafts, ...(host.resume?.drafts ?? {}) }, notice: "SolidWorks restarted - reopening your documents where you left off." }));
          setTimeout(() => set((s) => (s.notice?.startsWith("SolidWorks restarted") ? { notice: null } : {})), 8000);
        }
        set({ crash: host.crash ?? null, ribbonStale: !!host.ribbonStale });
        set({
          host,
          tools,
          docs,
          activeDocKey: host.activeDoc?.key ?? null,
          viewKey,
          apiKeySources: keySources(host),
          swTheme: host.swTheme ?? null,
          settings,
        });
        // Only another provider has a key (an environment key never goes through setApiKey): new chats start on its
        // default model instead of one that can't run.
        const keyed = keyedDefaultModel(settings.defaultModel, (p) => hasKey(get(), p), PROVIDERS.map((p) => p.id));
        if (keyed) await get().saveSettings({ defaultModel: keyed });
        await loadChat(viewKey);
        // A new SolidWorks session starts on a fresh chat (earlier ones are in the chat list); after Save and restart,
        // where the user left off.
        if (!host.resume) updateSlot(viewKey, (d) => ({ ...d, activeThreadId: null }));
        for (const k of Object.keys(docs)) void importDocChats(k);
        if (host.activeDoc) {
          rpc<{ docKey: string; items: SelectionItem[]; total?: number } | null>("selection.get", { docKey: host.activeDoc.key }).then((r) => {
            if (r) set((s) => ({ selection: { ...s.selection, [r.docKey]: r.items }, selectionTotal: { ...s.selectionTotal, [r.docKey]: r.total ?? r.items.length } }));
          });
        }
        // First run: the tour (it ends in Options when there's no key yet).
        const firstRun = !settings.tourDone;
        set({ booted: true, panel: !anyKey(get()) && !firstRun ? "settings" : "chat", tourStep: firstRun ? 0 : null });
      } catch (err: any) {
        set({ bootError: err?.message ?? String(err) });
      }

      on("overlay.changed", (p: { count: number }) => set({ overlayCount: p?.count ?? 0 }));

      // Plugins: the store and components, the agent's tools (MCP servers come and go), and wakes from async hooks.
      on("plugins.changed", () => void loadPluginState());
      on("tools.changed", () => {
        rpc<ToolDef[]>("tools.list")
          .then((tools) => set({ tools: tools ?? [] }))
          .catch(() => {});
        rpc<McpServerStatus[] | null>("mcp.status")
          .then((mcpServers) => set({ mcpServers: mcpServers ?? [] }))
          .catch(() => {});
      });
      on("session.wake", () => void pullWakes());
      // SessionStart context arrived, plugins or servers changed (hook events, MCP instructions), or a wake was held back.
      on("session.changed", () => {
        rpc<SessionInfo | null>("session.info", { panel: PANEL_ID })
          .then(applySession)
          .catch(() => {});
      });
      // The panel is ready: plugin state (its first session.info starts the session's hooks), then any waiting wakes.
      void loadPluginState().then(() => pullWakes());
      // The local link's status: the chat open here and whether it is busy, at ready and on every change.
      let lastPanel: string | null = null;
      const reportPanel = () => {
        const s = get();
        const d = s.chats[WORKSPACE]?.data;
        const t = d?.threads.find((x) => x.id === d.activeThreadId);
        const chat = t ? { title: t.title, busy: !!s.runs[t.id] } : null;
        const key = JSON.stringify(chat);
        if (key === lastPanel) return;
        lastPanel = key;
        rpc("localLink.panel", { chat }).catch(() => {});
      };
      reportPanel();
      api.subscribe(reportPanel);
      // The agent's helpers and API notes for the system prompt: at boot, and again when it saves one (or a plugin brings some).
      const loadKnowledge = () =>
        rpc<KnowledgeIndex | null>("knowledge.index")
          .then((knowledge) => set({ knowledge: knowledge ?? null }))
          .catch(() => {});
      on("knowledge.changed", () => void loadKnowledge());
      void loadKnowledge();
      // Standing instructions for the system prompt: at boot, and again when one is saved, edited or undone.
      const loadMemory = () =>
        rpc<MemoryIndex | null>("memory.index")
          .then((memory) => set({ memory: memory ?? null }))
          .catch(() => {});
      on("memory.changed", () => void loadMemory());
      void loadMemory();

      on("doc.activated", (doc: DocInfo) => {
        set((s) => ({ docs: { ...s.docs, [doc.key]: doc }, activeDocKey: doc.key }));
        get().showDoc(doc.key);
      });
      on("doc.updated", (doc: DocInfo) =>
        // Saved under a name (Save As): chats that worked in it show the new name, and a long tool run on it keeps
        // following it (a restored checkpoint reopens the file under a new key; the path still matches).
        updateSlot(WORKSPACE, (d) => ({
          ...d,
          threads: d.threads.map((t) => {
            let x = t;
            if (x.files?.some((f) => f.key === doc.key)) x = { ...x, files: x.files.map((f) => (f.key === doc.key ? { ...f, title: doc.title, path: doc.path || null } : f)) };
            if (x.toolRun?.doc?.key === doc.key) x = { ...x, toolRun: { ...x.toolRun, doc: { key: doc.key, title: doc.title, path: doc.path || null } } };
            return x;
          }),
        })),
      );
      on("theme.changed", (t: SwThemeInfo) => set({ swTheme: t }));
      on("ribbon.stale", ({ stale }: { stale: boolean }) => set({ ribbonStale: !!stale }));
      // SolidWorks read a document's chat from the file (it may have been asked for before that): reload it.
      // SolidWorks read a file's stored chats (older versions kept chats inside files): add them to the list.
      on("doc.chatChanged", ({ key }: { key: string }) => void importDocChats(key));
      on("update.changed", (u: UpdateInfo) => set({ update: u }));
      on("userTools.changed", (list: UserToolInfo[]) => {
        set({ userTools: list ?? [] });
        syncRibbonIcons(list ?? []);
      });
      rpc<UserToolInfo[]>("userTools.list")
        .then((list) => {
          set({ userTools: list ?? [] });
          syncRibbonIcons(list ?? []);
        })
        .catch(() => {});
      on("doc.updated", (doc: DocInfo) => set((s) => ({ docs: { ...s.docs, [doc.key]: doc } })));
      on("doc.closed", ({ key }: { key: string }) => {
        set((s) => {
          const docs = { ...s.docs };
          delete docs[key];
          return { docs, activeDocKey: s.activeDocKey === key ? null : s.activeDocKey };
        });
      });
      on("selection.changed", ({ docKey, items, total }: { docKey: string; items: SelectionItem[]; total?: number }) =>
        set((s) => {
          const agentWorking = Object.keys(s.runs).length > 0;
          return {
            selection: { ...s.selection, [docKey]: items },
            selectionTotal: { ...s.selectionTotal, [docKey]: total ?? items.length },
            excludedSel: [],
            agentSel: agentWorking ? { ...s.agentSel, [docKey]: selSig(items) } : s.agentSel,
          };
        }),
      );
      on("markup.strokes", ({ count, frames }: { count: number; frames?: number }) =>
        set((s) => ({ markup: { ...s.markup, strokes: count, frames: frames ?? s.markup.frames } })),
      );

      // Push-to-talk (the add-in's VoiceController).
      on("voice.status", (st: VoiceStatus) => reconcileVoice(st));
      rpc<VoiceStatus>("voice.status")
        .then((st) => reconcileVoice(st))
        .catch(() => {});
      on("voice.state", (st: { phase: VoiceLive["phase"]; text: string; events: VoiceLive["events"]; docKey: string | null }) =>
        set((s) => ({
          voice:
            st.phase === "idle"
              ? VOICE_IDLE
              : { ...s.voice, phase: st.phase, text: st.text ?? s.voice.text, events: st.events ?? s.voice.events, docKey: st.docKey },
        })),
      );
      on("voice.partial", ({ text }: { text: string }) => set((s) => (s.voice.phase === "idle" ? {} : { voice: { ...s.voice, text } })));
      on("voice.level", ({ v }: { v: number }) => set((s) => (s.voice.phase === "listening" ? { voice: { ...s.voice, level: v } } : {})));
      on("voice.event", (e: { t: number; kind: string; summary: string }) =>
        set((s) => (s.voice.phase === "idle" ? {} : { voice: { ...s.voice, events: [...s.voice.events, e] } })),
      );
      on("voice.final", (p: VoicePayload) => void receiveVoice(p));
      on("voice.notice", ({ text }: { text: string }) => set({ notice: text }));
      on("markup.key", ({ command }: { command: string }) => {
        if (command === "finish") get().finishMarkup();
        else get().cancelMarkup();
      });
      rpc("markup.cancel").catch(() => {}); // a reloaded panel must not leave an orphaned overlay

      on("files.dropped", async ({ paths }: { paths: string[] }) => {
        const entries = await rpc<FileEntry[]>("files.entries", { paths }).catch((err) => {
          set({ notice: `Couldn't attach the files: ${err.message}` });
          return [] as FileEntry[];
        });
        set({ panel: "chat" });
        get().addFileEntries(entries);
      });
      on("watch.log", (st: { entries?: WatchEntry[] }) => set((s) => (s.watch.active ? { watch: { ...s.watch, entries: st.entries ?? [] } } : {})));
      on("watch.ended", ({ reason }: { reason?: string }) => set({ watch: WATCH_IDLE, notice: reason ?? "Watch me stopped." }));
      // A reloaded panel picks up a recording that is still running.
      rpc<{ active: boolean; docKey?: string; doc?: string; startedAt?: string; entries?: WatchEntry[] }>("watch.status")
        .then((st) => {
          if (st?.active)
            set({
              watch: { active: true, busy: false, docKey: st.docKey ?? null, doc: st.doc ?? null, startedAt: st.startedAt ? Date.parse(st.startedAt) : Date.now(), entries: st.entries ?? [] },
            });
        })
        .catch(() => {});

      // SolidWorks doesn't raise an activation event for File > New, and events can be missed while the panel
      // loads, so also follow the active document by polling (cheap: one RPC every 1.5 s).
      setInterval(async () => {
        const st = await rpc<HostState>("app.getState").catch(() => null);
        const active = st?.activeDoc;
        if (!active || active.key === get().activeDocKey) return;
        set((s) => ({ docs: { ...s.docs, [active.key]: active }, activeDocKey: active.key }));
        get().showDoc(active.key);
      }, 1500);
      on("command", ({ name, args }: { name: string; args?: unknown }) => {
        const s = get();
        switch (name) {
          case "focus":
            set({ panel: "chat", focusTick: s.focusTick + 1 });
            break;
          case "newChat":
            set({ panel: "chat" });
            s.newChat();
            break;
          case "openSettings":
            set({ panel: "settings" });
            break;
          case "runTool": {
            // A ribbon tool the add-in couldn't run on its own (no macro yet, or the macro failed).
            const payload = (args as ToolRunPayload | undefined) ?? null;
            if (!payload?.tool) break;
            const info = s.userTools.find((t) => t.name === payload.tool);
            if (info?.kind === "workflow") {
              set({ panel: "chat" });
              if (!s.runs[currentThread(s)?.id ?? ""]) void s.runUserTool(payload.tool);
            } else void s.runQuickTool(payload.failure ? { ...payload, context: { ...payload.context, failure: payload.failure } } : payload);
            break;
          }
          case "newTool":
            set({ panel: "newTool" });
            break;
          case "attachView":
            set({ panel: "chat" });
            s.attachView();
            break;
          case "askAboutSelection": {
            const sel = s.selection[s.activeDocKey ?? ""] ?? [];
            const what = sel.length === 0 ? "the selection" : sel.length === 1 ? `the selected ${sel[0].type.toLowerCase()}` : `the ${sel.length} selected items`;
            const key = draftKey(s);
            set({
              panel: "chat",
              includeSelection: true,
              drafts: { ...s.drafts, [key]: (s.drafts[key] ? s.drafts[key] + " " : "") + `About ${what}: ` },
              focusTick: s.focusTick + 1,
            });
            break;
          }
        }
      });
    },

    async showDoc(key) {
      // The chat stays where it is; the running chats note that they now work in this file too.
      await loadChat(WORKSPACE);
      for (const id of Object.keys(get().runs)) noteFile(id);
      // A long tool run that activates another document (e.g. a part it built from a drawing) keeps its settings there.
      const doc = key === get().activeDocKey ? get().docs[key] : undefined;
      for (const r of Object.values(get().runs)) {
        if (r.inToolRun && doc) updateThread(r.docKey, r.threadId, (t) => (t.toolRun ? { ...t, toolRun: withRunDoc(t.toolRun, doc) } : t));
      }
      if (Object.keys(get().runs).length) void persist(WORKSPACE);
      void importDocChats(key);
      const r = await rpc<{ docKey: string; items: SelectionItem[]; total?: number } | null>("selection.get", { docKey: key }).catch(() => null);
      if (r) set((s) => ({ selection: { ...s.selection, [r.docKey]: r.items }, selectionTotal: { ...s.selectionTotal, [r.docKey]: r.total ?? r.items.length } }));
    },

    async newChat() {
      const key = get().viewKey;
      await loadChat(key);
      updateSlot(key, (d) => ({ ...d, activeThreadId: null }));
      set((s) => ({ focusTick: s.focusTick + 1 }));
    },

    async undoMemory(threadId, noteId) {
      const key = get().viewKey;
      await rpc<boolean>("memory.undo", { id: noteId });
      updateThread(key, threadId, (t) => ({ ...t, memoryUndone: [...(t.memoryUndone ?? []), noteId] }));
      void persist(key);
    },

    dismissRestart(threadId, offerId) {
      const key = get().viewKey;
      updateThread(key, threadId, (t) => ({ ...t, restartDismissed: [...(t.restartDismissed ?? []), offerId] }));
      void persist(key);
    },

    async startNewTool(spec) {
      const s = get();
      set({ panel: "chat" });
      const prompt = newToolPrompt(spec);
      // Making a new tool is a new request, not a follow-up of a long tool run in this chat.
      if (spec.from === "chat" && currentThread(s)) {
        await get().send(prompt, { fresh: true });
        return;
      }
      // In the chat the user is in - no separate chat.
      if (spec.from === "watch") {
        // Record first; the log attaches to the next message (Stop & attach in the bar), which already says what to build.
        set((st) => ({ drafts: { ...st.drafts, [draftKey(st)]: prompt } }));
        await get().startWatch();
        return;
      }
      await get().send(prompt, { fresh: true });
    },

    async selectThread(threadId) {
      const key = get().viewKey;
      updateSlot(key, (d) => ({ ...d, activeThreadId: threadId }));
      await persist(key);
    },

    async deleteThread(threadId) {
      const key = get().viewKey;
      get().runs[threadId]?.controller.abort();
      updateSlot(key, (d) => ({
        ...d,
        threads: d.threads.filter((t) => t.id !== threadId),
        activeThreadId: d.activeThreadId === threadId ? null : d.activeThreadId,
      }));
      rpc("session.reset", { threadId });
      await persist(key);
    },

    setThreadModel(model) {
      const s = get();
      const t = currentThread(s);
      if (t) {
        // Choosing the chat's model is choosing it for the follow-ups too: a long tool run's own settings end.
        if (t.toolRun) endToolRunWith(s.viewKey, t.id, t.toolRun.label);
        updateThread(s.viewKey, t.id, (th) => ({ ...th, model }));
        persist(s.viewKey);
      }
      get().saveSettings({ defaultModel: model });
    },

    setThreadEffort(effort) {
      const s = get();
      const t = currentThread(s);
      if (t) {
        if (t.toolRun) endToolRunWith(s.viewKey, t.id, t.toolRun.label);
        updateThread(s.viewKey, t.id, (th) => ({ ...th, effort }));
        persist(s.viewKey);
      }
      get().saveSettings({ defaultEffort: effort });
    },

    setDraft(text) {
      const key = draftKey(get());
      set((s) => ({ drafts: { ...s.drafts, [key]: text } }));
    },

    addAttachment(a) {
      const key = draftKey(get());
      // A model without vision or PDF support gets a note instead (agent/providers/media.ts): say so now.
      const model = modelOption(currentThread(get())?.model ?? get().settings.defaultModel);
      const note = a.kind === "image" || a.kind === "pdf" ? unreadableNote(a.kind, model.label, model.caps) : null;
      if (note) set({ notice: note });
      set((s) => ({ attachments: { ...s.attachments, [key]: [...(s.attachments[key] ?? []), a] } }));
    },

    removeAttachment(id) {
      const key = draftKey(get());
      set((s) => ({ attachments: { ...s.attachments, [key]: (s.attachments[key] ?? []).filter((a) => a.id !== id) } }));
    },

    async attachView() {
      const s = get();
      try {
        const res = await rpc<ToolRunResult>("tools.run", {
          docKey: s.viewKey,
          threadId: currentThread(s)?.id ?? "attach",
          name: "capture_views",
          input: { views: ["current"], max_size: 1280 },
        });
        const img = res.content.find((c) => c.type === "image");
        if (res.isError || !img || img.type !== "image" || img.source.type !== "base64") {
          const text = res.content.find((c) => c.type === "text");
          set({ notice: `Couldn't capture the view: ${text && text.type === "text" ? text.text : "no image"}` });
          return;
        }
        get().addAttachment({ id: uid(), kind: "image", name: "View", mediaType: img.source.media_type as ImageMediaType, data: img.source.data });
        set((st) => ({ focusTick: st.focusTick + 1 }));
      } catch (err: any) {
        set({ notice: `Couldn't capture the view: ${err.message}` });
      }
    },

    async send(text, opts) {
      const s0 = get();
      const docKey = s0.viewKey;
      await loadChat(docKey);
      const dKey = draftKey(get());
      // 3D models still being read: wait for them (the composer disables Send meanwhile; this is a safety net).
      const pending = (get().attachments[dKey] ?? []).map((a) => describing.get(a.id)).filter(Boolean);
      if (pending.length) await Promise.all(pending);
      const attachments = get().attachments[dKey] ?? [];
      const trimmed = text.trim();
      if (!trimmed && attachments.length === 0) return;

      let thread = currentThread(get());
      const asked = thread ? get().runs[thread.id]?.question : null;
      const ownRun = !!(opts?.tool || opts?.fresh || opts?.command); // a workflow run, a new request or a command: never an answer or a follow-up
      if (thread && asked && !asked.form && !asked.secret && trimmed && !ownRun && !attachments.some((a) => a.kind !== "voice")) {
        // The agent is waiting on a question: this is the answer (a queued message would wait behind it forever).
        // While it waits for a selection, the message is the user's note and counts as Done.
        if (asked.selection) void get().answerSelection(thread.id, true, trimmed);
        else get().answerQuestion(thread.id, trimmed);
        set((s) => ({ drafts: { ...s.drafts, [dKey]: "" }, attachments: { ...s.attachments, [dKey]: [] } }));
        return;
      }
      if (thread && busy(thread.id)) {
        // The agent is working, or a run is being put together (a wake, a queued message): queue it (sent when the turn
        // ends, or at the next step with "Send now"; a workflow run keeps its own settings and waits for the turn to end).
        const queued: QueuedMessage = { id: uid(), text: trimmed, attachments, ...(ownRun ? { run: opts } : {}) };
        const id = thread.id;
        set((s) => ({
          queue: { ...s.queue, [id]: [...(s.queue[id] ?? []), queued] },
          drafts: { ...s.drafts, [dKey]: "" },
          attachments: { ...s.attachments, [dKey]: [] },
        }));
        return;
      }
      const settings = get().settings;
      if (!thread) {
        thread = {
          id: uid(),
          title: (trimmed || attachments[0]?.name || "Image")
            .replace(/^(\[Use \w+: [^\]\n]+\]\s*)+/, "")
            .replace(/^\[Command: (\/[^\]\n]+)\][\s\S]*/, "$1")
            .replace(/^\[Use skill: ([^\]\n]+)\]\s*/, "Skill $1: ")
            .replace(/^\[New tool\]\s*/, "New tool: ")
            .replace(/^\[Smart rebuild\]\s*/, "Smart Rebuild: ")
            .replace(/\s+/g, " ")
            .slice(0, 70),
          createdAt: now(),
          updatedAt: now(),
          // A command's model is for its own run only, not for the chat it starts.
          model: (!opts?.command && opts?.model) || settings.defaultModel,
          effort: opts?.effort ?? settings.defaultEffort,
          messages: [],
          turns: [],
        };
        const created = thread;
        updateSlot(docKey, (d) => ({ ...d, threads: [created, ...d.threads], activeThreadId: created.id }));
      }

      const threadId = thread.id;
      // Clear the composer right away; capturing the selection and view can take a moment.
      set((s) => ({
        drafts: { ...s.drafts, [dKey]: "", [threadId]: "" },
        attachments: { ...s.attachments, [dKey]: [], [threadId]: [] },
      }));
      await submitPrompt(docKey, threadId, trimmed, attachments, opts ?? {});
    },

    queueSendNow(threadId, id) {
      const q = get().queue[threadId] ?? [];
      const asked = get().runs[threadId]?.question;
      const item = q.find((m) => m.id === id);
      // A queued tool or workflow run brings its own settings (model, steps, approval, a long run's follow-ups), which a
      // run already under way can't take on: it waits for the turn to end (the list offers no "Send now" for it).
      if (item?.staged || item?.run) {
        if (!busy(threadId)) flushQueue(get().viewKey, threadId, "end_turn");
        return;
      }
      if (asked && !asked.form && !asked.secret && item?.text) {
        // The agent is waiting on a question, so it won't take a next step: answer it with this message.
        set((s) => ({ queue: { ...s.queue, [threadId]: (s.queue[threadId] ?? []).filter((m) => m.id !== id) } }));
        if (asked.selection) void get().answerSelection(threadId, true, item.text);
        else get().answerQuestion(threadId, item.text);
        return;
      }
      if (!busy(threadId)) {
        // Nothing running (e.g. the turn failed): send the queue now (tool runs one at a time, as at a turn's end).
        flushQueue(get().viewKey, threadId, "end_turn");
        return;
      }
      set((s) => ({ queue: { ...s.queue, [threadId]: q.map((m) => (m.id === id ? { ...m, now: true } : m)) } }));
    },

    queueEdit(threadId, id) {
      const m = (get().queue[threadId] ?? []).find((x) => x.id === id);
      if (!m) return;
      set((s) => ({
        queue: { ...s.queue, [threadId]: (s.queue[threadId] ?? []).filter((x) => x.id !== id) },
        drafts: { ...s.drafts, [threadId]: [s.drafts[threadId], m.text].filter(Boolean).join("\n") },
        attachments: { ...s.attachments, [threadId]: [...(s.attachments[threadId] ?? []), ...m.attachments] },
        focusTick: s.focusTick + 1,
      }));
    },

    queueRemove(threadId, id) {
      set((s) => ({ queue: { ...s.queue, [threadId]: (s.queue[threadId] ?? []).filter((x) => x.id !== id) } }));
    },

    async resendFrom(msgIndex, editedText) {
      const s = get();
      const docKey = s.viewKey;
      const t = currentThread(s);
      if (!t || s.runs[t.id]) return;
      const msg = t.messages[msgIndex];
      if (!msg || !isPrompt(msg)) return;

      const blocks = asBlocks(msg.content);
      let content: ContentBlockParam[] = blocks;
      let newRefs: Record<string, string> = {};
      if (editedText !== undefined) {
        const text = editedText.trim();
        // Tool results answer the previous assistant turn and must stay; the prompt itself is replaced,
        // keeping the user's own images (those before the context block) and taking a fresh snapshot of
        // the document, selection and view.
        const ctxAt = blocks.findIndex((b) => b.type === "text" && b.text.startsWith("<sw_context>"));
        const ownImages = blocks.filter(
          (b, i) =>
            (ctxAt < 0 || i < ctxAt) &&
            (b.type === "image" || (b.type as string) === "document" || (b.type === "text" && isAttachmentText(b.text))),
        );
        const ctx = await buildContext(docKey, t.id, false);
        newRefs = ctx.refs;
        content = [
          ...blocks.filter((b) => b.type === "tool_result"),
          ...(text ? [{ type: "text" as const, text }] : []),
          ...ownImages,
          ...ctx.blocks,
        ];
        if (!content.some((b) => b.type === "text" && !b.text.startsWith("<sw_context>")) && !content.some((b) => b.type === "image")) return;
      }

      // Truncating the tail and replacing the last message keeps every earlier thinking block valid.
      const isFirstPrompt = !t.messages.slice(0, msgIndex).some(isPrompt);
      // A resend from before a long tool run started drops that run's messages, so its settings end with them; resending
      // its own prompt runs it again.
      const startedAt = t.toolRun?.msgIndex;
      if (typeof startedAt === "number" && msgIndex <= startedAt) {
        updateThread(docKey, t.id, (th) => ({
          ...th,
          toolRun: msgIndex < startedAt || !th.toolRun ? null : { ...th.toolRun, startedAt: Date.now(), lastActive: Date.now() },
        }));
      }
      const run = runSettings(docKey, t.id, {});
      updateThread(docKey, t.id, (th) => ({
        ...th,
        title: editedText !== undefined && isFirstPrompt && editedText.trim() ? editedText.trim().replace(/\s+/g, " ").slice(0, 70) : th.title,
        messages: [...th.messages.slice(0, msgIndex), { role: "user", content }],
        selRefs: { ...(th.selRefs ?? {}), ...newRefs },
        turns: [
          ...th.turns.filter((m) => m.msgIndex < msgIndex),
          { at: now(), msgIndex, model: run.model ?? th.model, effort: run.effort ?? th.effort, usage: { ...ZERO_USAGE } },
        ],
      }));
      await persist(docKey);
      await startRun(docKey, t.id, run.label ? { label: run.label, startedAt: Date.now() } : {}, run);
    },

    async retry() {
      const s = get();
      const t = currentThread(s);
      if (!t || s.runs[t.id]) return;
      const last = t.messages[t.messages.length - 1];
      if (!last || last.role !== "user") return;
      // Retrying a long tool run (e.g. after its step limit) keeps the tool's settings while its run is open.
      const run = runSettings(s.viewKey, t.id, {});
      updateThread(s.viewKey, t.id, (th) => ({
        ...th,
        turns: [...th.turns, { at: now(), msgIndex: lastPromptIndex(th.messages), model: run.model ?? th.model, effort: run.effort ?? th.effort, usage: { ...ZERO_USAGE } }],
      }));
      await startRun(s.viewKey, t.id, run.label ? { label: run.label, startedAt: Date.now() } : {}, run);
    },

    stop(threadId) {
      const s = get();
      const id = threadId ?? currentThread(s)?.id;
      if (!id) return;
      const run = s.runs[id];
      if (!run) return;
      run.controller.abort();
      rpc("voice.stopSpeaking").catch(() => {});
      run.approval?.resolve({ decision: "reject", note: "The user pressed Stop." });
      run.question?.resolve("(The user pressed Stop instead of answering.)");
      rpc("tools.cancel", { threadId: id });
    },

    answerApproval(threadId, d) {
      const run = get().runs[threadId];
      run?.approval?.resolve(d);
      updateRun(threadId, { approval: null });
    },

    answerQuestion(threadId, answer) {
      const run = get().runs[threadId];
      run?.question?.resolve(answer);
      updateRun(threadId, { question: null });
      set((s) => ({ answerDrafts: { ...s.answerDrafts, [threadId]: "" } }));
    },

    async answerSecret(threadId, value) {
      const q = get().runs[threadId]?.question;
      if (!q?.secret) return;
      if (value !== null) await rpc("plugins.setConfig", { id: q.secret.id, values: { [q.secret.key]: value } });
      // The run may have been stopped while the value was saved.
      if (get().runs[threadId]?.question?.toolUseId === q.toolUseId) get().answerQuestion(threadId, value !== null ? SECRET_SAVED : SECRET_SKIPPED);
    },

    keepWaiting(threadId) {
      const q = get().runs[threadId]?.question;
      if (q) updateRun(threadId, { question: { ...q, deadline: undefined } });
    },

    async answerSelection(threadId, done, note) {
      const run = get().runs[threadId];
      const q = run?.question;
      if (!run || !q?.selection || answeringSelection.has(q.toolUseId)) return;
      answeringSelection.add(q.toolUseId);
      try {
        const snap = done ? await rpc<SelectionSnapshot | null>("selection.snapshot", { docKey: run.docKey, max: SELECTION_MAX }).catch(() => null) : null;
        // Stopped (or answered) while the snapshot was taken: nothing to do.
        if (get().runs[threadId]?.question?.toolUseId !== q.toolUseId) return;
        const thread = get().chats[run.docKey]?.data.threads.find((t) => t.id === threadId);
        const items: SnapshotItem[] | null = done ? snap?.items ?? null : [];
        const answer = selectionAnswer(items, { prompt: q.question, ...q.selection }, refBase(thread?.selRefs), { skipped: !done, note, total: snap?.total });
        if (Object.keys(answer.refs).length) {
          // Like a message's selection: the refs join the chat, so Ref("S7") works in the agent's next code.
          updateThread(run.docKey, threadId, (t) => ({ ...t, selRefs: { ...(t.selRefs ?? {}), ...answer.refs } }));
          void persist(run.docKey);
        }
        q.resolve(answer.text);
        updateRun(threadId, { question: null });
        set((s) => ({ answerDrafts: { ...s.answerDrafts, [threadId]: "" } }));
      } finally {
        answeringSelection.delete(q.toolUseId);
      }
    },

    endToolRun(threadId) {
      const s = get();
      for (const [docKey, slot] of Object.entries(s.chats)) {
        if (!slot.data.threads.some((t) => t.id === threadId && t.toolRun)) continue;
        updateThread(docKey, threadId, (t) => ({ ...t, toolRun: null }));
        void persist(docKey);
      }
    },

    answerDrafts: {},
    setAnswerDraft(threadId, text) {
      set((s) => ({ answerDrafts: { ...s.answerDrafts, [threadId]: text } }));
    },

    async saveSettings(patch) {
      const settings = { ...get().settings, ...patch };
      set({ settings });
      await rpc("settings.save", settings);
    },

    async setApiKey(provider, key) {
      const res = await rpc<{ source: KeySource; sources?: Record<ProviderId, KeySource> }>("apiKey.set", { provider, key });
      set((s) => ({ apiKeySources: { ...s.apiKeySources, [provider]: res.source, ...(res.sources ?? {}) }, notice: null, keyFocus: null }));
      // The first key is OpenAI's: new chats start on OpenAI's default instead of a model that can't run.
      const keyed = keyedDefaultModel(get().settings.defaultModel, (p) => hasKey(get(), p), PROVIDERS.map((p) => p.id));
      if (keyed) await get().saveSettings({ defaultModel: keyed });
    },

    setPanel(panel) {
      set(panel === "settings" ? { panel } : { panel, keyFocus: null });
    },

    setIncludeSelection(v) {
      set({ includeSelection: v });
    },

    async runUserTool(name) {
      // Whatever is typed in the composer becomes the workflow's notes/inputs.
      await runWorkflow(name, (get().drafts[draftKey(get())] ?? "").trim());
    },

    async runQuickTool(payload) {
      const name = payload.tool;
      const docKey = WORKSPACE;
      await loadChat(docKey);
      const tool = get().userTools.find((t) => t.name === name);
      const label = payload.title ?? tool?.title ?? name;
      // Tool runs happen in the chat the user is in (a new chat when none is open) - no separate "Tool runs" chat.
      const activeId = get().chats[docKey]?.data.activeThreadId;
      let thread = get().chats[docKey]?.data.threads.find((t) => t.id === activeId);
      if (!thread) {
        const created: Thread = {
          id: uid(),
          title: `Tool: ${label}`,
          createdAt: now(),
          updatedAt: now(),
          model: get().settings.defaultModel,
          effort: get().settings.defaultEffort,
          messages: [],
          turns: [],
        };
        updateSlot(docKey, (d) => ({ ...d, threads: [created, ...d.threads], activeThreadId: created.id }));
        thread = created;
      }
      const threadId = thread.id;
      // The tier's model on the provider the user has (the default model's, else the one with a key).
      const { model, effort } = tierModel(payload.intelligence ?? tool?.intelligence, toolProvider());
      const staged = stageToolPrompt(payload, label, refBase(thread.selRefs));
      // The tool's intelligence level, step limit and auto-approval (the user ran it: that is the approval) apply to
      // this run; the chat keeps its own model and settings. A long tool's also carry over to follow-ups while its run
      // is open (runSettings).
      const longRun = payload.longRun ?? tool?.longRun ?? false;
      const maxSteps = clampSteps(payload.maxSteps ?? tool?.maxSteps ?? (longRun ? LONG_RUN_STEPS : DEFAULT_STEPS));
      const run: RunOverride = { model, effort, autoApprove: true, label, maxSteps, longRun, tool: name };
      if (busy(threadId)) {
        // The agent is busy in this chat: the tool run waits in the queue like a follow-up.
        const queued: QueuedMessage = { id: uid(), text: `Run ${label}`, attachments: [], staged: { blocks: staged.blocks, refs: staged.refs, run } };
        set((s) => ({
          queue: { ...s.queue, [threadId]: [...(s.queue[threadId] ?? []), queued] },
          notice: `The agent is busy in this chat - ${label} runs when it finishes.`,
        }));
        return;
      }
      set({ toolOutcome: null });
      if (!(await submitStaged(docKey, threadId, staged.blocks, staged.refs, run))) return;

      // A one-line outcome for the banner: the agent's last words, or the error.
      const th = get().chats[docKey]?.data.threads.find((t) => t.id === threadId);
      const turn = th?.turns[th.turns.length - 1];
      const lastText = [...(th?.messages ?? [])]
        .reverse()
        .find((m) => m.role === "assistant" && typeof m.content !== "string" && m.content.some((b) => b.type === "text"));
      const text =
        turn?.error ??
        (lastText && typeof lastText.content !== "string"
          ? (lastText.content.filter((b) => b.type === "text").map((b) => (b as { text: string }).text).join(" ").split("\n").find((l) => l.trim()) ?? "")
          : "");
      set({ toolOutcome: { label, text: text.replace(/[*_`#>]/g, "").trim().slice(0, 160), ok: !turn?.error && turn?.stop !== "aborted", threadId, docKey, at: Date.now() } });
    },

    dismissCrash() {
      set({ crash: null });
      rpc("crash.dismiss").catch(() => {});
    },

    async askAboutCrash() {
      const c = get().crash;
      if (!c) return;
      get().dismissCrash();
      const what =
        c.kind === "preview" ? `the live preview of the tool "${c.tool}"`
        : c.kind === "addin" ? `an unhandled error inside the Unify3D add-in (${c.title})`
        : c.kind === "page" ? `the tool "${c.tool}" while its PropertyManager page was open`
        : c.kind === "macro" ? `the macro of the tool "${c.tool}"`
        : `your code "${c.title ?? "a C# cell"}"`;
      const lines = [
        `SolidWorks closed unexpectedly (crashed) while running ${what}.${c.paused ? ` I paused the tool's ${c.paused} so it can't happen again until it's fixed.` : ""}`,
        c.helpersDisabled?.length
          ? `Your helper${c.helpersDisabled.length > 1 ? "s" : ""} ${c.helpersDisabled.map((h) => h.name).join(", ")} ${c.helpersDisabled.length > 1 ? "were" : "was"} disabled: involved in two crashes. Fix it with read_helper, save_helper and test_helper, or delete_helper.`
          : "",
        c.builtIn
          ? "It is a built-in Unify3D tool (read-only, so it was not paused and can't be edited): find the likely cause, tell me how to avoid it, and what to report to Unify3D. If you need more, ask me for SolidWorks' crash log (it lists the last API calls)."
          : "Find the cause and fix it so this can't happen again. If you need more, ask me for SolidWorks' crash log (it lists the last API calls).",
        "",
        "<crash_report>",
        `what: ${c.kind}${c.tool ? ` of tool ${c.tool}` : ""}${c.title ? ` (${c.title})` : ""}`,
        c.at ? `started: ${c.at}` : "",
        c.code ? `code that was running:\n${c.code}` : "",
        c.helpers?.length ? `helpers that code called: ${c.helpers.join(", ")}` : "",
        ...(c.helpersDisabled ?? []).map((h) => `helper disabled now: ${h.name} - ${h.reason}`),
        c.logTail ? `last lines of the add-in log:\n${c.logTail}` : "",
        "</crash_report>",
      ].filter(Boolean);
      await get().newChat();
      await get().send(lines.join("\n"));
    },

    async saveAndRestart() {
      const r = await rpc<{ ok: boolean; reason?: string }>("app.saveAndRestart", { saveDocuments: true, panel: resumePanel() }).catch((e) => ({ ok: false, reason: String(e?.message ?? e) }));
      return r.ok ? null : r.reason ?? "SolidWorks couldn't be restarted.";
    },

    async deleteUserTool(name) {
      await rpc("userTools.delete", { name });
    },

    toggleSelItem(index) {
      set((s) => ({
        excludedSel: s.excludedSel.includes(index) ? s.excludedSel.filter((i) => i !== index) : [...s.excludedSel, index],
      }));
    },

    async startMarkup() {
      const s = get();
      try {
        await rpc("markup.start", { docKey: s.activeDocKey, tool: s.markup.tool, color: s.markup.color });
        set((st) => ({ markup: { ...st.markup, active: true, strokes: 0, frames: 0 } }));
      } catch (err: any) {
        set({ notice: `Can't draw on the view: ${err.message}` });
      }
    },

    setMarkup(patch) {
      set((s) => ({ markup: { ...s.markup, ...patch } }));
      if (get().markup.active) rpc("markup.set", patch);
    },

    markupUndo() {
      rpc("markup.undo");
    },

    markupClear() {
      rpc("markup.clear");
    },

    async finishMarkup() {
      if (!get().markup.active) return;
      set((s) => ({ markup: { ...s.markup, active: false } }));
      try {
        const res = await rpc<{ frames: { mediaType: ImageMediaType; data: string; strokes: number }[]; inVoice: number } | null>("markup.finish");
        const frames = res?.frames ?? [];
        if (frames.length > 0) {
          // One markup per message (a frame per view): replace an earlier one that wasn't sent yet.
          const key = draftKey(get());
          set((s) => ({
            attachments: {
              ...s.attachments,
              [key]: (s.attachments[key] ?? []).filter((a) => !(a.kind === "image" && a.name.startsWith(MARKUP_NAME))),
            },
          }));
          frames.forEach((f, i) =>
            get().addAttachment({ id: uid(), kind: "image", name: frames.length === 1 ? MARKUP_NAME : `${MARKUP_NAME} ${i + 1}`, mediaType: f.mediaType, data: f.data }),
          );
          set((s) => ({ focusTick: s.focusTick + 1 }));
        }
      } catch (err: any) {
        set({ notice: `Couldn't attach the markup: ${err.message}` });
      }
    },

    cancelMarkup() {
      set((s) => ({ markup: { ...s.markup, active: false, strokes: 0, frames: 0 } }));
      rpc("markup.cancel");
    },

    dismissNotice() {
      set({ notice: null });
    },

    async pickFiles() {
      try {
        const entries = await rpc<FileEntry[]>("files.pick");
        get().addFileEntries(entries ?? []);
      } catch (err: any) {
        set({ notice: `Couldn't attach the files: ${err.message}` });
      }
    },

    addFileEntries(entries) {
      const skipped: string[] = [];
      for (const f of entries) {
        if (f.kind === "image" && f.data && IMAGE_TYPES.includes(f.mediaType ?? "")) {
          get().addAttachment({ id: uid(), kind: "image", name: f.name, mediaType: f.mediaType as ImageMediaType, data: f.data });
        } else if (f.kind === "pdf" && f.data) {
          get().addAttachment({ id: uid(), kind: "pdf", name: f.name, mediaType: "application/pdf", data: f.data, path: f.pdfPath ?? f.path });
        } else if (f.kind === "drawing") {
          // A SolidWorks drawing: converted to PDF in the background (opening it and its models takes a while). It shows
          // as a chip "reading" like a model meanwhile (sending waits for it), then becomes the PDF attachment.
          const id = uid();
          get().addAttachment({ id, kind: "model", name: f.name, path: f.path, status: "reading" });
          const fail = (entry: FileEntry) =>
            set((s) => {
              const attachments: Record<string, Attachment[]> = {};
              for (const [k, list] of Object.entries(s.attachments)) {
                attachments[k] = list.map((a) => (a.id === id && a.kind === "model" ? { ...a, status: "error" as const, entry } : a));
              }
              return { attachments };
            });
          const job = rpc<FileEntry>("files.describe", { path: f.path })
            .then((entry) => {
              if (entry?.kind === "pdf" && entry.data) {
                // the chip becomes the PDF, in the same place (whichever draft it is in)
                set((s) => {
                  const attachments: Record<string, Attachment[]> = {};
                  for (const [k, list] of Object.entries(s.attachments)) {
                    attachments[k] = list.map((a) =>
                      a.id === id
                        ? { id, kind: "pdf" as const, name: entry.name || `${f.name} (as PDF)`, mediaType: "application/pdf" as const, data: entry.data!, path: entry.pdfPath ?? f.path }
                        : a,
                    );
                  }
                  return { attachments };
                });
              } else {
                fail({ ...f, error: entry?.kind === "too_large" ? "too large as a PDF" : entry?.error ?? "couldn't convert the drawing" });
              }
            })
            .catch((err) => fail({ ...f, error: err.message }))
            .finally(() => describing.delete(id));
          describing.set(id, job);
        } else if (f.kind === "model") {
          const id = uid();
          if (f.summary) {
            get().addAttachment({ id, kind: "model", name: f.name, path: f.path, status: f.views ? "ready" : "error", entry: f });
            continue;
          }
          get().addAttachment({ id, kind: "model", name: f.name, path: f.path, status: "reading" });
          // Read it in the background (hidden in SolidWorks for CAD formats); the chip fills in when done.
          const patch = (entry: FileEntry, status: "ready" | "error") =>
            set((s) => {
              const attachments: Record<string, Attachment[]> = {};
              for (const [k, list] of Object.entries(s.attachments)) {
                attachments[k] = list.map((a) => (a.id === id && a.kind === "model" ? { ...a, status, entry } : a));
              }
              return { attachments };
            });
          const job = rpc<FileEntry>("files.describe", { path: f.path })
            .then((entry) => patch(entry, entry.views ? "ready" : "error"))
            .catch((err) => patch({ ...f, error: err.message }, "error"))
            .finally(() => describing.delete(id));
          describing.set(id, job);
        } else {
          skipped.push(`${f.name}${f.kind === "too_large" ? " (too large)" : f.error ? ` (${f.error})` : ""}`);
        }
      }
      if (skipped.length) set({ notice: `Not attached: ${skipped.join(", ")}. Attach images, PDFs, SolidWorks drawings or 3D models (STL, STEP, 3MF, IGES, SLDPRT, SLDASM, X_T).` });
      set((s) => ({ focusTick: s.focusTick + 1 }));
    },

    async startWatch() {
      const s = get();
      if (s.watch.active || s.watch.busy) return;
      const docKey = s.activeDocKey ?? WORKSPACE;
      try {
        const st = await rpc<{ doc?: string }>("watch.start", { docKey });
        set({ watch: { active: true, busy: false, docKey, doc: st?.doc ?? s.docs[docKey]?.title ?? null, startedAt: Date.now(), entries: [] } });
      } catch (err: any) {
        set({ notice: `Can't watch: ${err.message}` });
      }
    },

    async stopWatch() {
      if (!get().watch.active || get().watch.busy) return;
      set((s) => ({ watch: { ...s.watch, busy: true } }));
      try {
        const res = await rpc<WatchResult>("watch.stop");
        set({ watch: WATCH_IDLE, panel: "chat" });
        // One recording per message: replace an earlier one that wasn't sent yet.
        const key = draftKey(get());
        set((s) => ({ attachments: { ...s.attachments, [key]: (s.attachments[key] ?? []).filter((a) => a.kind !== "watch") } }));
        get().addAttachment({ id: uid(), kind: "watch", name: `Watch log · ${res.steps} step${res.steps === 1 ? "" : "s"}`, watch: res });
        if (!(get().drafts[key] ?? "").trim()) set((s) => ({ drafts: { ...s.drafts, [key]: "Turn what I just did into a tool" } }));
        set((s) => ({ focusTick: s.focusTick + 1 }));
      } catch (err: any) {
        set({ watch: WATCH_IDLE, notice: `Couldn't finish the recording: ${err.message}` });
      }
    },

    cancelWatch() {
      set({ watch: WATCH_IDLE });
      rpc("watch.cancel").catch(() => {});
    },
  };
});

/** Model-changing tool calls that ran after the message at msgIndex (they are not undone by resending). */
export function writesAfter(thread: Thread, msgIndex: number): number {
  let n = 0;
  for (const m of thread.messages.slice(msgIndex + 1)) {
    if (m.role !== "assistant" || typeof m.content === "string") continue;
    for (const b of m.content) if (b.type === "tool_use" && WRITE_TOOLS.has(b.name)) n++;
  }
  return n;
}

/** What the New tool screen collects. Anything left open is the agent's to propose (in one ask_form). */
export interface NewToolSpec {
  what: string;
  from: "describe" | "chat" | "watch";
  usage: string[];
  style: "auto" | "hardcoded" | "agent" | "hybrid";
  name: string;
}

export function newToolPrompt(t: NewToolSpec): string {
  const from = {
    describe: "my description above",
    chat: "what you just did in this chat - generalize the steps and the code you ran",
    watch: "the recording of what I do (the watch log attached to this message)",
  }[t.from];
  const style = {
    auto: "recommend one of hard coded / agent in the loop / hybrid, with roughly how long each would take to build",
    hardcoded: "hard coded - the macro is the whole tool",
    agent: "agent in the loop - the tool holds the skill; the agent writes the code each run from my context",
    hybrid: "hybrid - the macro runs first and hands what it can't handle to the agent, which adapts the code and improves the macro",
  }[t.style];
  const lines = [
    `[New tool] ${t.what.trim() || "(see below)"}`,
    "",
    `- Start from: ${from}`,
    `- When I run it: ${t.usage.length ? t.usage.join("; ") : "your call"}`,
    `- Run style: ${style}`,
  ];
  if (t.name.trim()) lines.push(`- Name: ${t.name.trim()}`);
  return lines.join("\n");
}

/** What the panel hands over across a restart (see ResumeSession in the add-in). */
export function resumePanel(): ResumePanel {
  const drafts = Object.fromEntries(Object.entries(useStore.getState().drafts).filter(([, v]) => v?.trim()));
  return { drafts };
}

/** The tool_use ids of the agent's code cells after a message (the add-in's undo ledger knows them). */
export function cellIdsAfter(thread: Thread, msgIndex: number): string[] {
  const ids: string[] = [];
  for (const m of thread.messages.slice(msgIndex + 1)) {
    if (m.role !== "assistant" || typeof m.content === "string") continue;
    for (const b of m.content) if (b.type === "tool_use" && b.name === "execute_csharp") ids.push(b.id);
  }
  return ids;
}

/** Plain text of a prompt, without the add-in's context block. */
export function promptText(m: MessageParam): string {
  if (typeof m.content === "string") return m.content;
  return m.content
    .filter((b): b is Extract<ContentBlockParam, { type: "text" }> => b.type === "text" && !b.text.startsWith("<sw_context>") && !isAttachmentText(b.text))
    .map((b) => b.text)
    .join("\n\n");
}

/** Index of the last user message that has something other than tool results (i.e. a prompt). */
export function lastPromptIndex(messages: MessageParam[]): number {
  for (let i = messages.length - 1; i >= 0; i--) {
    if (isPrompt(messages[i])) return i;
  }
  return 0;
}

/** A user message with something besides tool results (and the context plugin hooks add after them): a prompt. */
export function isPrompt(m: MessageParam): boolean {
  return m.role === "user" && (typeof m.content === "string" || m.content.some((b) => b.type !== "tool_result" && !(b.type === "text" && b.text.startsWith(HOOK_TAG))));
}

function asBlocks(content: MessageParam["content"]): ContentBlockParam[] {
  return typeof content === "string" ? [{ type: "text", text: content }] : [...content];
}

// Dev-only handle for poking at state from the browser console.
if (import.meta.env.DEV) (window as any).__agentStore = useStore;
