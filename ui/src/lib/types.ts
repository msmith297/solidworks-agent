import type Anthropic from "@anthropic-ai/sdk";

export type MessageParam = Anthropic.Beta.Messages.BetaMessageParam;
export type ContentBlockParam = Anthropic.Beta.Messages.BetaContentBlockParam;
export type ToolResultBlockParam = Anthropic.Beta.Messages.BetaToolResultBlockParam;
export type ImageBlockParam = Anthropic.Beta.Messages.BetaImageBlockParam;
export type TextBlockParam = Anthropic.Beta.Messages.BetaTextBlockParam;
export type ToolUseBlockParam = Anthropic.Beta.Messages.BetaToolUseBlockParam;

export type Effort = "low" | "medium" | "high" | "xhigh" | "max";

/** A model provider (docs/providers/design.md). Every model id belongs to one (lib/models.ts). */
export type ProviderId = "anthropic" | "openai";

/** Where the add-in found a provider's key. */
export type KeySource = "stored" | "environment" | "none";

/** Data only the provider that wrote a message can use. Never shown; never sent to another provider. */
export interface ProviderReplay {
  provider: ProviderId;
  /** The model that produced the message (OpenAI carries reasoning only within a model family). */
  model: string;
  /** Provider-defined. OpenAI: { output: ResponseOutputItem[] } - response.output exactly as returned. */
  data: unknown;
}

/**
 * A stored message: the Anthropic message plus, on assistant messages from some providers, replay data. repairHistory
 * rebuilds every message as { role, content }, so `replay` never reaches an API request by itself.
 */
export type StoredMessage = MessageParam & { replay?: ProviderReplay };
export type ApprovalMode = "ask" | "auto" | "read_only";
export type Theme = "solidworks" | "system" | "light" | "dark";

/** SolidWorks interface theme as reported by the add-in (swInterfaceBrightnessTheme_e + panel colours). */
export interface SwThemeInfo {
  theme: number;
  treeBg?: string;
  pmBg?: string;
}

export interface Usage {
  input: number;
  output: number;
  cacheRead: number;
  cacheWrite: number;
  /** Largest single prompt in the turn (tokens the model actually saw at once, after context editing). */
  context?: number;
}

/** Bookkeeping for one user turn (one press of Send), stored alongside the API messages. */
export interface TurnMeta {
  at: string;
  /** The SolidWorks session this turn ran in (its start time, ms) - for the "Last session" dividers. */
  session?: number;
  /** Index of the user message this turn answers. */
  msgIndex: number;
  model: string;
  effort: Effort;
  usage: Usage;
  durationMs?: number;
  stop?: string;
  error?: string;
  /** The OpenAI speed tier that served the turn, when not the standard one. */
  serviceTier?: string;
}

/**
 * A conversation. `messages` is the Anthropic Messages history (the one stored format for every provider; each
 * provider's adapter translates it at request time), plus `replay` on some assistant messages.
 */
export interface Thread {
  id: string;
  title: string;
  createdAt: string;
  updatedAt: string;
  model: string;
  effort: Effort;
  messages: StoredMessage[];
  turns: TurnMeta[];
  /** "Always allow" was chosen for code execution in this chat. */
  autoApprove?: boolean;
  /** Selection references [S1]... captured with the user's messages: id -> SolidWorks persistent reference (base64). */
  selRefs?: Record<string, string>;
  /** "tools": the per-document chat where ribbon tools run when the agent has to step in. */
  kind?: "tools";
  /** Save-and-restart offers the user answered "Not now" to (offer ids, see ChatView restartOffer). */
  restartDismissed?: string[];
  /** "Remembered" chips the user undid (note ids). */
  memoryUndone?: string[];
  /** The files this chat worked in (the chat follows the work from file to file). key: the add-in's runtime key. */
  files?: { title: string; path?: string | null; key?: string }[];
  /** A long tool's run in this chat: its settings carry over to follow-ups while it is open (lib/toolRun.ts). */
  toolRun?: ToolRunSticky | null;
}

/** A document as a long tool run remembers it (the add-in key, plus title and path: a reopened file gets a new key). */
export interface ToolRunDoc {
  key: string;
  title: string;
  path?: string | null;
}

/**
 * The settings of a long tool run (front matter long_run) kept for typed follow-ups: the tool's model, effort, step
 * limit and (for ribbon tools) auto-approval - only while one of its documents is active, until the tool reports it is
 * done, the user closes the banner chip or changes the chat's model, another tool or workflow runs in the chat, or
 * after 2 h without activity.
 */
export interface ToolRunSticky {
  tool: string;
  label: string;
  model: string;
  effort: Effort;
  maxSteps: number;
  /** Follow-ups skip the approval prompt (a ribbon tool run; workflows keep the chat's approval). Missing: true. */
  autoApprove?: boolean;
  /** Agent tools whose results can report the run done (the tool's `requires`). */
  requires?: string[];
  /** The document the tool ran on. */
  doc?: ToolRunDoc | null;
  /** Other documents the run has worked in since (e.g. a part it built from a drawing and activated). */
  docs?: ToolRunDoc[];
  /** Index of the message that started the run: a resend from before it ends the run. */
  msgIndex?: number;
  startedAt: number;
  /** Last time a run in this chat started or ended (the idle clock). */
  lastActive: number;
}

/** What the add-in stores inside the SolidWorks file. */
export interface ChatData {
  schema: 1;
  docId: string;
  revision: number;
  activeThreadId: string | null;
  threads: Thread[];
  /** Workspace chats: ids of threads imported from files (older versions kept chats inside each file). */
  imported?: string[];
}

export interface DocInfo {
  key: string;
  title: string;
  path?: string;
  type?: "part" | "assembly" | "drawing" | "other";
}

export interface Settings {
  /** Bumped when a default changes in a way existing installs should pick up. */
  settingsVersion: number;
  defaultModel: string;
  defaultEffort: Effort;
  approval: ApprovalMode;
  theme: Theme;
  markDocumentDirty: boolean;
  showProgress: boolean;
  /** How much the agent writes in its replies. */
  verbosity: "low" | "medium" | "high";
  /** Ask the agent to give parts real materials and realistic colours/appearances. */
  realisticAppearance: boolean;
  /** Attach a screenshot of the user's current view to every message. */
  shareView: boolean;
  /** Silence SolidWorks' rebuild-error sound while the agent works. */
  muteErrorSounds: boolean;
  /** Text size of the panel (1 = 100%). Ctrl+/Ctrl-/Ctrl+0. */
  zoom: number;
  /** Download updates in the background and install them when SolidWorks closes. Unset until the user is asked. */
  autoUpdate?: boolean | null;
  /** Push-to-talk voice messages, transcribed on this PC (Whisper). Only on while a speech model is downloaded. */
  voiceEnabled?: boolean;
  /** The user answered the "talk to the agent?" prompt shown after the tour. */
  voiceAsked?: boolean;
  /** The first-run tour was finished or skipped. */
  tourDone?: boolean;
  /** The talk key: "vk:<Windows virtual key>" or "mouse:x1" / "mouse:x2" (the side buttons). */
  voiceKey?: string;
  /** Speech model id (see the add-in's SpeechModels). */
  voiceModel?: string;
  /** After letting go of the key: send at once, or leave it in the composer to check first. */
  voiceSend?: "release" | "review";
  /** While talking, keep the view each time it settles (turned, changed). */
  voiceViews?: boolean;
  /** Read the agent's replies aloud: never, after voice messages, or always. */
  speakReplies?: "off" | "voice" | "always";
  speakVoice?: string;
  /** -10 (slow) to 10 (fast); 0 is normal. */
  speakRate?: number;
  /** Update enabled plugins in the background at startup (third-party marketplaces only when they opt in). Default on. */
  pluginAutoUpdate?: boolean;
  /** Run plugin hooks (trusted plugins only). Default on. */
  pluginHooks?: boolean;
  /** Start plugin and mcp.json MCP servers. Default on. */
  pluginMcp?: boolean;
  /** Automatic turns per hour (plugin wakes and Stop-hook continuations together) before they pause. Default 20. */
  pluginWakesPerHour?: number;
  /** agent-bridge messages from the user's own console follow the approval setting (other agents' always ask). Default on. */
  trustConsoleWakes?: boolean;
  /** Show what plugins add (hook context, notices) in the chat. Default on. */
  pluginEventsInChat?: boolean;
  /** Where a tool's inputs appear: a card in the chat panel (default) or the native PropertyManager page. */
  toolInputs?: "panel" | "page";
  /** Seconds a question (ask_user / ask_form) waits for an answer before the agent decides on its own; 0 = never. Default 60. */
  questionTimeoutSec?: number;
  /** The agent may search the web and read web pages (the provider's web tools). Default on. */
  webResearch?: boolean;
  /** OpenAI speed (service_tier) for models that offer it; "standard" sends nothing. Default "standard". */
  openaiSpeed?: "standard" | "fast" | "ultrafast";
  /** Save standing instructions the user gives ("always ...", "from now on ..."). Default on. */
  saveMemory?: boolean;
}

// ---- plugins (docs/plugins/design.md; RPCs implemented by the add-in's PluginsBridge, hooks and MCP runtime) ----

/** Counts of what a plugin contains (the store's cards show them). */
export interface PluginComponents {
  tools: number;
  commands: number;
  skills: number;
  agents: number;
  hooks: number;
  mcpServers: number;
}

/** One `userConfig` key of a plugin.json. A sensitive value is never sent to the panel: `set` says whether one is stored. */
export interface PluginConfigField {
  type: "string" | "number" | "boolean" | "directory" | "file";
  title?: string;
  description?: string;
  default?: string | number | boolean | null;
  sensitive?: boolean;
  required?: boolean;
}

/** A plugin as plugins.list returns it: every plugin of every marketplace, installed or not. */
export interface PluginInfo {
  /** "name@marketplace" */
  id: string;
  name: string;
  marketplace: string;
  displayName?: string | null;
  description?: string | null;
  version?: string | null;
  author?: { name?: string; email?: string; url?: string } | null;
  homepage?: string | null;
  repository?: string | null;
  license?: string | null;
  /** The marketplace entry's category and keywords: the store's tags. */
  category?: string | null;
  keywords?: string[];
  installed: boolean;
  enabled: boolean;
  /** The plugin's current hooks and MCP servers are the ones the user approved (or it has none). */
  trusted: boolean;
  /** A plugin in the "local" marketplace (made in Unify3D): editable, can be packaged. */
  local?: boolean;
  /** A newer version (or source commit) is listed by its marketplace. */
  updateAvailable?: string | null;
  components: PluginComponents;
  userConfig?: Record<string, PluginConfigField>;
  /** Current userConfig values; sensitive keys come as { set: boolean }. */
  config?: Record<string, string | number | boolean | { set: boolean } | null>;
  /** Plugins installed together with this one (marketplace entry `unify3d.installWith`). */
  installWith?: string[];
  /** Install requirements and caveats to show (e.g. "Needs Node.js 18 or newer"). */
  notes?: string[];
  installedAt?: string | null;
  updatedAt?: string | null;
  /** Why it can't be installed or loaded, if so. */
  error?: string | null;
}

export interface MarketplaceInfo {
  name: string;
  /** What the user added: "owner/repo", a git or https URL, a folder. */
  source: string;
  /** "unify3d" (ships with the add-in) and "local" (the user's own plugins) can't be removed. */
  builtIn?: boolean;
  pluginCount: number;
  updatedAt?: string | null;
  /** Plugins from it update by themselves (on for the built-in marketplace, off by default for others). */
  autoUpdate?: boolean;
  error?: string | null;
}

/** What installing or enabling needs the user to approve: the programs a plugin's hooks and MCP servers run. */
export interface TrustRequest {
  hooks: string[];
  mcp: string[];
  /** Tools whose C# runs inside SolidWorks: "plugin:tool (macro.cs, preview.cs)". */
  tools?: string[];
}

/** plugins.install / plugins.enable / plugins.update: needsTrust is set when the user must approve first (call again with trustHash: hash). */
export interface PluginActionResult {
  needsTrust?: TrustRequest | null;
  hash?: string | null;
  /** Ids of plugins installed along with it (installWith). */
  installed?: string[];
  note?: string | null;
}

/** A plugin command (commands/*.md). `id` is "plugin:command"; `/name` works when the name is unique. */
export interface CommandInfo {
  id: string;
  name: string;
  plugin: string;
  description?: string | null;
  argumentHint?: string | null;
  model?: string | null;
}

/** A plugin skill (skills/<skill>/SKILL.md). `id` is "plugin:skill". */
export interface SkillInfo {
  id: string;
  name: string;
  plugin: string;
  description?: string | null;
}

/** A plugin sub-agent (agents/*.md). `tools` null: every tool; `prompt` is the agent's system prompt. */
export interface AgentInfo {
  id: string;
  description?: string | null;
  tools: string[] | null;
  model?: string | null;
  prompt: string;
}

/** plugins.expandCommand: the command's body with $ARGUMENTS, $1..$9 and @file includes filled in. */
export interface ExpandedCommand {
  text: string;
  model?: string | null;
  /** E.g. "!`cmd` lines were left as text (shell commands are not run)". */
  note?: string | null;
}

export interface McpServerStatus {
  /** The plugin name, or null for a server from the user's mcp.json. */
  plugin: string | null;
  server: string;
  state: "starting" | "running" | "restarting" | "error" | "stopped";
  error?: string | null;
  toolCount: number;
}

export type HookEvent = "SessionStart" | "UserPromptSubmit" | "PreToolUse" | "PostToolUse" | "Stop" | "SessionEnd";

/** session.info: the session id hooks and MCP servers see, which hook events have handlers, and MCP server instructions. */
export interface SessionInfo {
  sessionId: string;
  /** Event -> the matchers of its handlers ("" or "*" match everything). */
  hookEvents: Partial<Record<HookEvent, string[]>>;
  /** Server instructions from initialize; plugin is null for a server from the user's mcp.json. */
  mcpInstructions: { server: string; plugin?: string | null; text: string }[];
  /** SessionStart hooks' additionalContext and plain stdout (the system prompt carries them). */
  sessionContext?: string[];
  /** SessionStart hooks' systemMessage and exit-2 text: for the user only (the notice bar), never the model. */
  sessionNotices?: string[];
  /** Wakes held back by the hourly limit; they go in with the next prompt. */
  heldWakes?: number;
}

/** What hooks.fire returns: every handler of the event ran (in parallel) and their outputs are combined. */
export interface HookResult {
  block: boolean;
  reason?: string | null;
  additionalContext: string[];
  systemMessages: string[];
  continue: boolean;
  stopReason?: string | null;
  permissionDecision?: "allow" | "deny" | "ask" | null;
  permissionDecisionReason?: string | null;
}

/** An async hook that woke the agent (session.takeWakes); acked with session.ackWake once it is in a chat. */
export interface Wake {
  id: string;
  text: string;
  /** What woke it, e.g. "Stop hook" / "SessionStart hook". */
  source: string;
  /** The plugin, e.g. "agent-bridge". */
  plugin: string;
}

/** One stretch of recognised speech; times in ms from the first key press. */
export interface VoiceSegment {
  t0: number;
  t1: number;
  text: string;
}

/** Something the user did while talking (the add-in's VoiceController). */
export interface VoiceEvent {
  t: number;
  kind: "view" | "markup" | "select" | "deselect" | "sketch" | "added" | "deleted" | "renamed";
  summary: string;
  image?: string;
  mediaType?: string;
  items?: { type: string; label?: string; detail?: string; ref?: string | null }[];
  total?: number;
  name?: string;
  what?: string;
  was?: string;
  closed?: string;
  strokes?: number;
}

/** A finished voice message: the words and, on the same clock, what the user did. */
export interface VoicePayload {
  docKey: string | null;
  durationMs: number;
  text: string;
  segments: VoiceSegment[];
  events: VoiceEvent[];
}

export interface VoiceStatus {
  enabled: boolean;
  model: string;
  models: { id: string; label: string; mb: number; note: string; installed: boolean }[];
  engine: "off" | "loading" | "ready" | "error";
  engineError: string | null;
  download: { id: string; progress: number } | null;
  downloadError: string | null;
  available: boolean;
}

/** Update feed state from the add-in (Infrastructure/Updater.cs). */
export interface UpdateInfo {
  current: string;
  latest: string | null;
  status: "idle" | "checking" | "uptodate" | "available" | "downloading" | "ready" | "error";
  notes: string | null;
  releaseUrl: string | null;
  error: string | null;
  progress: number;
  lastChecked: string | null;
}

export interface HostState {
  version: string;
  solidworksVersion: string | null;
  /** The Anthropic key's source (kept for older panels; apiKeySources has every provider). */
  apiKeySource: KeySource;
  /** Every provider's key source. */
  apiKeySources?: Partial<Record<ProviderId, KeySource>>;
  settings: Partial<Settings>;
  scratchKey: string;
  activeDoc: DocInfo | null;
  docs: DocInfo[];
  logFile: string;
  swTheme?: SwThemeInfo | null;
  update?: UpdateInfo;
  /** What SolidWorks was running when it closed unexpectedly last time (CrashGuard). */
  crash?: CrashInfo | null;
  /** Tools changed since SolidWorks started; the ribbon picks them up after a restart. */
  ribbonStale?: boolean;
  /** When this SolidWorks session started (ISO). Restart offers made before it are done. */
  sessionStart?: string;
  /** What the panel handed over before "Save and restart" (or an update restart), once. */
  resume?: ResumePanel | null;
}

/** Panel state carried across a restart: unsent drafts per thread / new-chat slot. */
export interface ResumePanel {
  drafts?: Record<string, string>;
}

export interface CrashInfo {
  kind: "preview" | "macro" | "cell" | "page" | "addin" | "tool";
  tool?: string | null;
  title?: string;
  code?: string;
  at?: string;
  logTail?: string;
  /** The part of the tool that was paused so it can't crash again. */
  paused?: string;
  /** A built-in tool (read-only): nothing is paused and the agent can't edit it. */
  builtIn?: boolean;
  /** The agent's helpers the code called ("Name", or "plugin:x/Name" for a plugin's). */
  helpers?: string[];
  /** Helpers disabled now: involved in two crashes. */
  helpersDisabled?: { name: string; reason: string }[];
}

/** knowledge.index: the helpers cells can call and the most used API notes, for the system prompt (capped by the add-in). */
export interface KnowledgeIndex {
  helpers: { name: string; source: string; signature?: string | null; summary?: string | null; line: string }[];
  disabled: { name: string; source: string; reason?: string | null }[];
  notes: { id: string; topic: string; right: string; wrong?: string | null; apis: string[]; hits: number; line: string }[];
  /** How many did not fit the budget (list_helpers / list_api_notes show them). */
  omitted: { helpers: number; notes: number };
}

export interface ToolDef {
  name: string;
  description: string;
  input_schema: { type: "object"; properties?: Record<string, any>; required?: string[] };
}

export interface ToolRunResult {
  content: Array<TextBlockParam | ImageBlockParam>;
  isError: boolean;
}

/** A user-created tool (a saved, reusable procedure the agent runs). */
/** How much intelligence a tool needs; mapped to a model per provider (lib/models.ts). */
export type Tier = "fast" | "balanced" | "high" | "max";

/** One question of an ask_form questionnaire. */
export interface FormField {
  id: string;
  label: string;
  type: "choice" | "multi" | "text" | "number" | "yesno";
  options?: string[];
  default?: string | number | boolean | string[];
  help?: string;
  required?: boolean;
}

export interface FormSpec {
  title: string;
  intro?: string;
  fields: FormField[];
}

/** Entity kinds ask_selection can ask for. */
export type SelectionKind = "face" | "edge" | "vertex" | "body" | "feature" | "component";

/** An ask_selection request: pick entities in SolidWorks, then Done. */
export interface SelectionAsk {
  prompt: string;
  types?: SelectionKind[];
  min?: number;
  max?: number;
  /** Set by the panel: requested types it doesn't know (any entity then counts, and the answer says so). */
  unknownTypes?: string[];
}

/** What the add-in hands the panel when a ribbon tool needs the agent (Tools/ToolRunner.cs). */
export interface ToolRunPayload {
  tool: string;
  title?: string;
  intelligence?: Tier;
  inputs?: {
    id: string;
    label: string;
    type: string;
    value?: unknown;
    items?: { type: string; label: string; detail: string; ref?: string }[];
    files?: FileEntry[];
  }[];
  context?: { model?: string; history?: string; view?: string; failure?: string; handoff?: string; macroOutput?: string };
  /** Older add-ins: the macro's failure report. */
  failure?: string;
  /** API steps per turn for this tool (front matter max_steps; default 200, cap 1000). */
  maxSteps?: number;
  /** A long, phased tool (front matter long_run): 1-hour prompt cache, settings kept for follow-ups. */
  longRun?: boolean;
  /** A built-in Unify3D tool: read-only, so the agent is never told to fix its macro with edit_user_tool. */
  builtIn?: boolean;
  /** The tool's standing instructions (the user's notes on it; a copy has the original's), already worded for the agent. */
  notes?: string | null;
  /** The tool profile the inputs were loaded from, and the inputs the user changed after loading it. */
  profile?: { name: string; modified?: string[]; locked?: boolean } | null;
}

export interface UserToolInfo {
  name: string;
  /** A built-in Unify3D tool (ships with the add-in, read-only). */
  builtIn?: boolean;
  /** The plugin it comes from (its name is "plugin:tool"), or null for the user's own and built-in tools. */
  plugin?: string | null;
  /** Can't be edited or deleted: built-in tools and tools of installed plugins (a local plugin's tools stay editable). */
  readOnly?: boolean;
  title: string;
  description: string;
  icon: string;
  updated: string;
  /** The tool's own icon (SVG markup), if the agent designed one. */
  iconSvg?: string | null;
  /** Whether the ribbon PNG is up to date (false: the panel renders it). */
  hasPng?: boolean;
  /** "workflow": big procedure, Workflows menu. "tool": quick ribbon command (agentic macro). */
  kind?: "workflow" | "tool";
  /** A tool's verified macro exists (runs directly, without the agent). */
  hasMacro?: boolean;
  /** Model for agent-run steps, if the tool chose one. */
  model?: string | null;
  /** Tools: an agent finishes the job after the macro (false: the macro is the whole tool). */
  agent?: boolean;
  /** hardcoded | agent | hybrid. */
  runStyle?: "hardcoded" | "agent" | "hybrid";
  /** Tools: fast | balanced | high | max. */
  intelligence?: Tier;
  hasInputs?: boolean;
  hasPreview?: boolean;
  /** "preview" / "macro" paused after SolidWorks crashed while running it. */
  paused?: string;
  /** Front matter category: its own section in the Workflows menu and box on the ribbon tab. */
  category?: string | null;
  /** Built-ins: the category the tool ships with (the user may have changed it). */
  categoryDefault?: string | null;
  /** Shorter title for the ribbon button. */
  ribbonTitle?: string | null;
  /** Front matter long_run: a long, phased tool. */
  longRun?: boolean;
  /** Front matter max_steps (600 for a long tool without one); null: the default 200. */
  maxSteps?: number | null;
  /** Agent tools the tool needs from the add-in. */
  requires?: string[];
  /** A built-in or plugin tool the user keeps off the ribbon and palette (they use their own copy). */
  hidden?: boolean;
  /** The user's standing instructions on it (docs/agent-memory.md). */
  hasNotes?: boolean;
  /** The user's copy of this tool (fork_tool), and whether the original changed since. */
  forkedFrom?: string | null;
  sourceChanged?: boolean;
  /** Its saved profiles of input values (the user's, then the ones it ships with). */
  profiles?: string[];
}

export interface SelectionItem {
  type: string;
  label: string;
}

/**
 * A file as the add-in hands it over (src/SwAgent/ModelFiles/FileAttachments.cs): images and PDFs with their data,
 * 3D models with six rendered views (one JPEG) and a measurement summary once described. A SolidWorks drawing (.slddrw)
 * comes path-only as kind "drawing"; files.describe converts it and returns kind "pdf" with pdfPath (every sheet).
 */
export interface FileEntry {
  name: string;
  path: string;
  kind?: "image" | "pdf" | "model" | "drawing" | "file" | "too_large";
  /** For a drawing converted to PDF: where the PDF was written. */
  pdfPath?: string;
  mediaType?: string;
  data?: string;
  format?: string;
  summary?: string;
  views?: string;
  viewsMediaType?: string;
  error?: string;
  seconds?: number;
}

/** One step in a "Watch me" recording (src/SwAgent/Watch/ActivityLog.cs). */
export interface WatchEntry {
  id: number;
  t: number;
  kind: "add" | "edit" | "delete" | "rename" | "suppress" | "unsuppress" | "select" | "note";
  text: string;
  byAgent?: boolean;
}

/** What the add-in returns when the user stops "Watch me". */
export interface WatchResult {
  docKey: string;
  doc: string;
  docType: string;
  seconds: number;
  steps: number;
  /** The log as the agent reads it. */
  text: string;
  entries: WatchEntry[];
  stats: { added: number; edited: number; deleted: number; renamed: number; selections: number };
  /** JPEGs (base64): the view when recording started and stopped, and numbered step thumbnails. */
  before?: string | null;
  after?: string | null;
  film?: string | null;
}

export type ImageMediaType = "image/jpeg" | "image/png" | "image/webp" | "image/gif";

export type Attachment =
  | {
      id: string;
      kind: "image";
      name: string;
      mediaType: ImageMediaType;
      data: string; // base64
    }
  | { id: string; kind: "pdf"; name: string; mediaType: "application/pdf"; data: string; path?: string }
  | {
      id: string;
      kind: "model";
      name: string;
      path: string;
      /** "reading" until the add-in has rendered and measured it. */
      status: "reading" | "ready" | "error";
      entry?: FileEntry;
    }
  | { id: string; kind: "watch"; name: string; watch: WatchResult }
  | { id: string; kind: "voice"; name: string; voice: VoicePayload };
