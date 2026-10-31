import { buildAgentPrompt, buildSystemPrompt, type IndexedSkill, type IndexedTool, type Verbosity } from "./systemPrompt";
import { MEMORY_WRITE_TOOLS, memoryRefusal, type MemoryIndex } from "../lib/memory";
import { modelOption, resolveModel } from "../lib/models";
import { hookContextText, hookToolResponse } from "../lib/hooks";
import type {
  AgentInfo,
  HookResult,
  ApprovalMode,
  ContentBlockParam,
  Effort,
  ProviderId,
  StoredMessage,
  ToolDef,
  ToolResultBlockParam,
  ToolRunResult,
  ToolUseBlockParam,
  Usage,
  FormSpec,
  KnowledgeIndex,
  SelectionAsk,
} from "../lib/types";
import { clampSteps } from "../lib/toolRun";
import type { SecretAsk } from "../lib/creator";
import { getProvider } from "./providers";
import { AbortedError, MalformedOutputError } from "./providers/errors";
import { readableHistory } from "./providers/media";
import type { ClearingRules, TurnResult } from "./providers/types";

/**
 * Tools that change the model; gated by the approval setting. run_tool_macro runs a tool's macro.cs (or C# the agent
 * passes as `code`) inside SolidWorks, so it is gated like execute_csharp. plugin_try_mcp starts a program on this PC
 * (a plugin's MCP server being tried), so it is gated the same way.
 */
export const WRITE_TOOLS = new Set(["execute_csharp", "undo", "rebuild", "run_tool_macro", "test_helper", "plugin_try_mcp"]);

/** Handled in the UI: a questionnaire card in the chat; the answers come back as JSON. */
export const ASK_FORM_TOOL: ToolDef = {
  name: "ask_form",
  description:
    "Show the user a short questionnaire (a form in the chat) and wait for the answers, returned as JSON keyed by field id. " +
    "Use it to settle several decisions at once - above all when designing a new tool or workflow (what it does, what the user " +
    "selects, which values they set in its PropertyManager page, defaults, preview, whether an agent is needed, intelligence). " +
    "Pre-fill sensible defaults so the user can just confirm; ask only what you can't infer; at most about 8 fields.",
  input_schema: {
    type: "object",
    properties: {
      title: { type: "string", description: "Form title, e.g. \"New tool: Smart fillet\"." },
      intro: { type: "string", description: "One or two sentences on what you're proposing." },
      fields: {
        type: "array",
        items: {
          type: "object",
          properties: {
            id: { type: "string" },
            label: { type: "string" },
            type: { type: "string", enum: ["choice", "multi", "text", "number", "yesno"] },
            options: { type: "array", items: { type: "string" } },
            default: { description: "Pre-filled answer (string, number, boolean, or array for multi)." },
            help: { type: "string" },
            required: { type: "boolean" },
          },
          required: ["id", "label", "type"],
        },
      },
    },
    required: ["title", "fields"],
  },
};

/** Handled in the UI: a "Save and restart SolidWorks" button at the end of the agent's reply. */
export const OFFER_RESTART_TOOL: ToolDef = {
  name: "offer_restart",
  description:
    "Show the user a 'Save and restart SolidWorks' button at the end of your reply. It saves the open documents, restarts SolidWorks " +
    "and reopens everything where the user left off (documents, views, this chat). Use it whenever a change only takes effect after a " +
    "restart (a new, renamed or deleted tool's ribbon button, a changed tool icon) instead of telling the user to restart. Don't use it as a hedge: changes " +
    "to an existing tool's TOOL.md, macro, preview or inputs apply on its next run without a restart.",
  input_schema: {
    type: "object",
    properties: {
      reason: { type: "string", description: "One short sentence shown with the button, e.g. 'Restart to add Flash grooves to the SolidWorks Agent tab.'" },
    },
    required: ["reason"],
  },
};

/** Handled entirely in the UI: shows a question card and waits for the user. */
export const ASK_USER_TOOL: ToolDef = {
  name: "ask_user",
  description:
    "Ask the user a question and wait for the answer. Use it when a decision changes the result (a dimension, which " +
    "face or component, design intent) and you can't infer it. Offer 2-5 concrete options when possible; the user can " +
    "also type a free answer.",
  input_schema: {
    type: "object",
    properties: {
      question: { type: "string", description: "The question, one or two sentences." },
      options: { type: "array", items: { type: "string" }, description: "Suggested answers." },
    },
    required: ["question"],
  },
};

/**
 * Handled in the UI: a card asks the user to pick entities in SolidWorks and press Done; the answer lists them as [S#]
 * references (numbered on from the chat's), so mid-run picks are as addressable as a selection sent with a message.
 */
export const ASK_SELECTION_TOOL: ToolDef = {
  name: "ask_selection",
  description:
    "Ask the user to select entities in SolidWorks (e.g. the part's critical faces, or where a parting line must not run) and wait " +
    "until they press Done. Pre-select your suggestions in code first; the user adds or removes. Returns one line per entity with " +
    "an [S#] reference usable with Ref(\"S#\") and select_refs, plus anything the user said or typed.",
  input_schema: {
    type: "object",
    properties: {
      prompt: { type: "string", description: "What to select and why, one or two sentences, e.g. \"Select the faces that must stay free of flash and parting lines.\"" },
      types: {
        type: "array",
        items: { type: "string", enum: ["face", "edge", "vertex", "body", "feature", "component"] },
        description: "Entity kinds that count (default: any). Others the user selects are reported but not numbered.",
      },
      min: { type: "integer", description: "Fewest entities needed before Done (default 0)." },
      max: { type: "integer", description: "Most entities wanted (more are still reported; at most 500 are read)." },
    },
    required: ["prompt"],
  },
};

/**
 * Handled in the UI: runs one of the plugins' sub-agents (agents/*.md) as a nested agent loop with the agent's own
 * prompt, model and tools, and returns its final reply. Offered only while a plugin provides agents.
 */
export const RUN_AGENT_TOOL: ToolDef = {
  name: "run_agent",
  description:
    "Hand a self-contained task to one of the plugins' sub-agents (listed under \"Plugin agents\" in your instructions) and get its final " +
    "reply. The sub-agent starts fresh: put everything it needs in the prompt (the document, [S#] references and their meaning, what to " +
    "report). It can't ask the user anything, and its intermediate steps aren't shown to the user - summarise its findings yourself.",
  input_schema: {
    type: "object",
    properties: {
      agent: { type: "string", description: "The agent's id, e.g. \"shop-standards:mold-reviewer\"." },
      prompt: { type: "string", description: "The complete task for the sub-agent." },
    },
    required: ["agent", "prompt"],
  },
};

/**
 * Handled in the UI: a password card for one of a plugin's sensitive options. The value goes straight to the plugin's
 * encrypted options (plugins.setConfig); the agent only ever gets "saved" or "skipped".
 */
export const ASK_SECRET_TOOL: ToolDef = {
  name: "ask_secret",
  description:
    "Ask the user for an API key, token or password a plugin needs, in a masked field. The value is stored encrypted for the plugin and " +
    "never reaches you: you get \"saved\" or \"skipped\". The plugin must declare the key in userConfig with \"sensitive\": true first; its " +
    "MCP server or hook then gets it as ${user_config.<key>} in env or headers. Never ask for secrets in the chat.",
  input_schema: {
    type: "object",
    properties: {
      plugin: { type: "string", description: "The plugin's name." },
      key: { type: "string", description: "The userConfig key, e.g. \"api_key\"." },
      label: { type: "string", description: "What to enter, e.g. \"Your Acme API key\"." },
      help_url: { type: "string", description: "Where the user gets one (shown as a link)." },
    },
    required: ["plugin", "key", "label"],
  },
};

/** The UI-handled tools, offered on every request after the add-in's (run_agent only while there are plugin agents). */
export const LOCAL_TOOLS: ToolDef[] = [ASK_USER_TOOL, ASK_FORM_TOOL, ASK_SELECTION_TOOL, OFFER_RESTART_TOOL, ASK_SECRET_TOOL];

/** Local tools that wait for the user: never offered to a sub-agent, and a hook's "ask" doesn't add an approval to them. */
const USER_TOOLS = new Set([ASK_USER_TOOL.name, ASK_FORM_TOOL.name, ASK_SELECTION_TOOL.name, ASK_SECRET_TOOL.name]);

/** A sub-agent's step limit (API round trips). */
export const SUB_AGENT_STEPS = 50;

/**
 * The tools a sub-agent gets: the ones its front matter lists that SolidWorks Agent has (Claude Code agents list Read, Bash...,
 * which don't exist here); every tool when it lists none; read-only tools (no WRITE_TOOLS) when nothing it lists exists.
 */
export function subAgentTools(listed: string[] | null | undefined, tools: ToolDef[]): ToolDef[] {
  if (!listed) return tools;
  const wanted = new Set(listed.map((n) => n.trim()));
  const kept = tools.filter((t) => wanted.has(t.name));
  return kept.length ? kept : tools.filter((t) => !WRITE_TOOLS.has(t.name));
}

/** A content block while it streams. */
export interface LiveBlock {
  type: "text" | "thinking" | "tool_use" | "other";
  text: string;
  name?: string;
  id?: string;
  json?: string;
}

export interface ApprovalRequest {
  toolUseId: string;
  name: string;
  input: any;
  /** A sub-agent's call: toolUseId is then the parent's run_agent call (the card shows under it), this is the real one. */
  sub?: { agent: string; toolUseId: string; name: string; input: any };
}

/** A sub-agent's tool calls so far, shown inside the parent's run_agent card while it runs. */
export interface SubAgentProgress {
  toolUseId: string;
  agent: string;
  steps: { id: string; name: string; state: "running" | "ok" | "error" }[];
}

export type ApprovalDecision = { decision: "run" | "always" | "reject"; note?: string };

export interface AgentCallbacks {
  getMessages(): StoredMessage[];
  /** Follow-ups the user chose to "Send now": delivered with the next tool results, without stopping the run. */
  takeSteering?(): ContentBlockParam[] | null;
  /** Append to the thread and persist. History is append-only - never edit earlier messages. */
  append(message: StoredMessage): Promise<void>;
  setLive(blocks: LiveBlock[] | null): void;
  setRunningTool(toolUseId: string | null): void;
  addUsage(usage: Usage): void;
  approve(req: ApprovalRequest): Promise<ApprovalDecision>;
  ask(toolUseId: string, question: string, options: string[], form?: FormSpec): Promise<string>;
  /** ask_selection: resolves with the [S#] lines once the user presses Done (or Skip). */
  askSelection(toolUseId: string, ask: SelectionAsk): Promise<string>;
  /** ask_secret: "saved" or "skipped" (never the value), or an error when the plugin has no such sensitive option. */
  askSecret?(toolUseId: string, ask: SecretAsk): Promise<{ text: string; isError?: boolean }>;
  /** A request to the model started (for the "Thinking... 1m 20s" timer). */
  onRequest?(startedAt: number): void;
  /** A status line for the user (e.g. a stalled connection being retried), or null to clear it. */
  onStatus?(text: string | null): void;
  runTool(name: string, input: unknown, toolUseId?: string): Promise<ToolRunResult>;
  isAutoApproved(): boolean;
  setAutoApproved(): void;
  /** run_agent's progress for the parent's tool card (null when the sub-agent is done). */
  setSubAgent?(progress: SubAgentProgress | null): void;
  /**
   * Plugin hooks around a tool call (PreToolUse before validation and approval, PostToolUse after it ran); null when
   * no hook handles this tool.
   */
  hook?(event: "PreToolUse" | "PostToolUse", call: { tool_name: string; tool_input: unknown; tool_response?: unknown }): Promise<HookResult | null>;
}

/** The panel's API keys, per provider: a run (or a sub-agent on another provider) asks for the one it needs. */
export interface AgentKeys {
  get(provider: ProviderId): Promise<string | null>;
  /** Whether the host reported a key for the provider (no RPC). */
  has(provider: ProviderId): boolean;
}

export interface AgentRunOptions {
  keys: AgentKeys;
  model: string;
  effort: Effort;
  tools: ToolDef[];
  approval: ApprovalMode;
  realisticAppearance: boolean;
  verbosity: Verbosity;
  userTools: IndexedTool[];
  /** API round trips before the turn stops (a tool's max_steps; default 200, cap 1000). */
  maxSteps?: number;
  /** A long tool run: the prompt cache lives 1 hour (answers to its gate questions often take 5-60 min). */
  longRun?: boolean;
  /** Plugin skills, sub-agents and MCP server instructions for the system prompt; agents also enable run_agent. */
  skills?: IndexedSkill[];
  agents?: AgentInfo[];
  mcpInstructions?: { server: string; text: string }[];
  sessionContext?: string[];
  /** The helpers cells can call and the saved API notes (knowledge.index), for the system prompt. */
  knowledge?: KnowledgeIndex | null;
  /** The provider's web research tools (setting "Allow web research"; default on). Never for a sub-agent. */
  webResearch?: boolean;
  /** OpenAI speed (Options); sent only to models that offer it. */
  serviceTier?: "fast" | "ultrafast";
  /** The speed tier that served a request, when not the standard one (for the turn's footer). */
  onServedTier?(tier: string): void;
  /** Test only: no bytes for this long means the connection is dead (default: the provider's). */
  stallMs?: number;
  /** Standing instructions (memory.index), for the system prompt. */
  memory?: MemoryIndex | null;
  /** A turn a plugin or another agent started: remember, forget and fork_tool are refused (lib/memory.ts). */
  fromWake?: boolean;
  /** A turn a plugin's Stop hook continued: the same refusals as a wake. */
  fromHook?: boolean;
  /** A sub-agent run: its own system prompt and no local tools. */
  system?: string;
  localTools?: ToolDef[];
  signal: AbortSignal;
  cb: AgentCallbacks;
}

export interface AgentRunResult {
  stop: string;
  error?: string;
}

/**
 * Context clearing: once a request passes the trigger, the oldest tool calls and their results (inputs included -
 * execute_csharp cells are the bulk) are replaced by a placeholder, keeping the last few (Claude does it server-side,
 * other providers in their adapter). Clearing at least a step at a time makes each clearing worth the prompt-cache
 * rewrite it causes. The user's answers (ask_user, ask_form, ask_selection with its [S#] refs), read_user_tool
 * (instructions being followed) and the helpers the agent saved or read (save_helper, read_helper: the code it calls
 * from now on) are never cleared.
 */
const CLEARING: ClearingRules = {
  triggerTokens: 40000,
  keepToolUses: 6,
  clearAtLeastTokens: 12000,
  clearInputs: true,
  excludeTools: ["ask_user", "ask_form", "ask_selection", "ask_secret", "offer_restart", "read_user_tool", "use_skill", "save_helper", "read_helper"],
};

/** Runs the agent until it ends its turn, is stopped, or fails. */
export async function runAgent(opts: AgentRunOptions): Promise<AgentRunResult> {
  const { cb, signal } = opts;
  const m = modelOption(opts.model);
  const provider = getProvider(m.provider);
  const key = await opts.keys.get(m.provider);
  if (!key) return { stop: "error", error: `${m.label} needs an ${provider.vendor} API key. Add one in Options, or pick another model.` };
  // Liveness: every byte the API sends - text, thinking, reasoning summaries or keep-alive pings - counts. A long think
  // keeps the connection busy; a silent connection is stalled.
  const liveness = { last: Date.now() };
  const session = provider.open(key, liveness);
  const stallMs = opts.stallMs ?? provider.stallMs;

  // Web research runs on the provider's side (server tools); a sub-agent (its own system prompt) never gets it, nor a
  // model without web tools.
  const web = !opts.system && !!opts.webResearch && m.caps.web !== null;
  const tools = [...opts.tools, ...localTools(opts)];
  // use_skill exists only with plugins (and a sub-agent may lack read_user_tool): exclude only tools this run has.
  const names = new Set(tools.map((t) => t.name));
  const clearing: ClearingRules = { ...CLEARING, excludeTools: CLEARING.excludeTools.filter((n) => names.has(n)) };

  const system =
    opts.system ??
    buildSystemPrompt({
      realisticAppearance: opts.realisticAppearance,
      verbosity: opts.verbosity,
      userTools: opts.userTools,
      skills: opts.skills,
      agents: opts.agents,
      mcpInstructions: opts.mcpInstructions,
      sessionContext: opts.sessionContext,
      knowledge: opts.knowledge,
      webResearch: web,
      webTools: m.caps.web ?? undefined,
      memory: opts.memory,
      provider: m.provider,
    });
  let malformedRetries = 0;
  let stallRetries = 0;
  const maxSteps = clampSteps(opts.maxSteps);
  try {
    for (let iteration = 0; iteration < maxSteps; iteration++) {
      if (signal.aborted) return { stop: "aborted" };

      // One controller per request, so a stalled request can be dropped and re-sent without stopping the run.
      const request = new AbortController();
      const onAbort = () => request.abort();
      signal.addEventListener("abort", onAbort);
      let stalled = false;
      liveness.last = Date.now();
      cb.onRequest?.(Date.now());
      const watchdog = setInterval(
        () => {
          if (Date.now() - liveness.last > stallMs) {
            stalled = true;
            request.abort();
          }
        },
        Math.min(5000, Math.max(10, stallMs / 5)),
      );

      let live: LiveBlock[] = [];
      let frame = 0;
      const flush = () => {
        frame = 0;
        cb.setLive(live.map((b) => ({ ...b })));
      };
      const schedule = (blocks: LiveBlock[]) => {
        live = blocks;
        if (!frame) frame = requestAnimationFrame(flush);
      };

      let r: TurnResult;
      const endRequest = () => {
        clearInterval(watchdog);
        signal.removeEventListener("abort", onAbort);
      };
      try {
        r = await session.turn(
          { model: m, effort: opts.effort, system, tools, messages: readableHistory(cb.getMessages(), m.caps), web, longRun: !!opts.longRun, clearing, serviceTier: opts.serviceTier },
          { signal: request.signal, onLive: schedule },
        );
        malformedRetries = 0;
        stallRetries = 0;
        endRequest();
      } catch (err) {
        endRequest();
        if (frame) cancelAnimationFrame(frame);
        if (signal.aborted) return { stop: "aborted" };
        if (stalled) {
          // Nothing (not even keep-alive pings) for a long time: the connection is dead. Nothing was appended, so
          // sending the same request again is safe.
          if (stallRetries++ < 2) {
            cb.setLive(null);
            cb.onStatus?.(`The connection to ${provider.label} went quiet - retrying (${stallRetries}/2)...`);
            continue;
          }
          throw new Error(`The connection to ${provider.label} stopped responding. Check your internet connection, then press Retry.`);
        }
        if (err instanceof AbortedError) return { stop: "aborted" };
        // A reply that couldn't be read (e.g. unparseable tool JSON): re-issue that turn (nothing was appended).
        if (err instanceof MalformedOutputError && malformedRetries++ < 2) continue;
        throw err;
      }
      if (frame) cancelAnimationFrame(frame);
      cb.setLive(null);
      cb.onStatus?.(null);

      cb.addUsage(r.usage);
      if (r.servedTier) opts.onServedTier?.(r.servedTier);

      if (r.stop === "refusal") {
        return { stop: "refusal", error: `The model declined this request${r.stopDetail ? ` (${r.stopDetail})` : ""}. Try rephrasing it.` };
      }

      const content = typeof r.message.content === "string" ? [] : r.message.content;
      const toolUses = content.filter((b): b is ToolUseBlockParam => b.type === "tool_use");

      if (r.stop === "max_tokens" && toolUses.length > 0) {
        return { stop: "max_tokens", error: "The response was cut off in the middle of a tool call. Ask the agent to continue in smaller steps." };
      }

      await cb.append(r.message);

      if (r.stop === "pause_turn") continue;
      if (toolUses.length === 0) return { stop: r.stop === "tool_use" ? "end_turn" : r.stop };

      const results: ToolResultBlockParam[] = [];
      // What plugin hooks add goes after all the results, in its own text blocks (context editing clears old results).
      const added: ContentBlockParam[] = [];
      let hookStop: string | null = null;
      for (const tu of toolUses) {
        if (signal.aborted || hookStop !== null) {
          results.push({ type: "tool_result", tool_use_id: tu.id, is_error: true, content: signal.aborted ? "Stopped by the user before this ran." : "Not run: a plugin hook stopped the turn." });
          continue;
        }
        const res = await executeWithHooks(tu, opts);
        results.push(res.result);
        added.push(...res.added);
        if (res.stop !== undefined) hookStop = res.stop;
      }
      const steer = signal.aborted ? null : cb.takeSteering?.();
      await cb.append({ role: "user", content: [...results, ...added, ...(steer ?? [])] });
      if (signal.aborted) return { stop: "aborted" };
      if (hookStop !== null) return { stop: "hook_stop", error: `A plugin hook stopped the turn${hookStop ? `: ${hookStop}` : "."}` };
    }
    return { stop: "iteration_limit", error: `Stopped after ${maxSteps} steps. Send "continue" to carry on.` };
  } finally {
    cb.setLive(null);
    cb.setRunningTool(null);
  }
}

/** The local tools a run offers: the panel's own (run_agent only with plugin agents), or a sub-agent's (none). */
function localTools(opts: AgentRunOptions): ToolDef[] {
  return opts.localTools ?? (opts.agents?.length ? [...LOCAL_TOOLS, RUN_AGENT_TOOL] : LOCAL_TOOLS);
}

/**
 * A tool call with the plugins' PreToolUse and PostToolUse hooks around it (Claude Code semantics): PreToolUse can
 * deny the call, or ask for the user's approval even where none is needed ("allow" never skips SolidWorks Agent's own approval);
 * what hooks add for the model, and a PostToolUse block's reason, come back as text blocks for after the results;
 * `continue: false` ends the turn after this call.
 */
async function executeWithHooks(tu: ToolUseBlockParam, opts: AgentRunOptions): Promise<{ result: ToolResultBlockParam; added: ContentBlockParam[]; stop?: string }> {
  const { cb } = opts;
  const added: ContentBlockParam[] = [];
  let stop: string | undefined;
  const note = (event: "PreToolUse" | "PostToolUse", h: HookResult | null) => {
    if (!h) return;
    const texts = [...h.additionalContext, ...(event === "PostToolUse" && h.block && h.reason ? [h.reason] : [])].filter((t) => t?.trim());
    if (texts.length) added.push({ type: "text", text: hookContextText(event, texts, tu.name) });
    if (!h.continue) stop = h.stopReason ?? "";
  };
  const pre = cb.hook ? await cb.hook("PreToolUse", { tool_name: tu.name, tool_input: tu.input }).catch(() => null) : null;
  note("PreToolUse", pre);
  if (pre && (pre.block || pre.permissionDecision === "deny")) {
    const why = pre.permissionDecisionReason || pre.reason;
    return { result: { type: "tool_result", tool_use_id: tu.id, is_error: true, content: `A plugin hook blocked this call${why ? `: ${why}` : "."}` }, added, stop };
  }
  if (stop !== undefined) return { result: { type: "tool_result", tool_use_id: tu.id, is_error: true, content: "Not run: a plugin hook stopped the turn." }, added, stop };
  const result = await executeTool(tu, opts, pre?.permissionDecision === "ask");
  if (cb.hook) {
    const response = hookToolResponse(result.content as string | { type: string; text?: string }[] | undefined, !!result.is_error);
    note("PostToolUse", await cb.hook("PostToolUse", { tool_name: tu.name, tool_input: tu.input, tool_response: response }).catch(() => null));
  }
  return { result, added, stop };
}

async function executeTool(tu: ToolUseBlockParam, opts: AgentRunOptions, hookAsks = false): Promise<ToolResultBlockParam> {
  const { cb } = opts;
  const err = (text: string): ToolResultBlockParam => ({ type: "tool_result", tool_use_id: tu.id, is_error: true, content: text });

  const refused = memoryRefusal(tu.name, { fromWake: opts.fromWake, fromHook: opts.fromHook, subAgent: !!opts.system });
  if (refused) return err(refused);

  const local = localTools(opts).find((t) => t.name === tu.name);
  const def = local ?? opts.tools.find((t) => t.name === tu.name);
  if (!def) return err(`Unknown tool '${tu.name}'.`);
  const problem = validateInput(def, tu.input);
  if (problem) return err(`INVALID_INPUT: ${problem}. Re-issue the call with complete, valid JSON input.`);

  // A PreToolUse hook asked for the user's OK: the approval card, whatever the approval setting (read-only still wins below).
  let approved = false;
  if (hookAsks && !USER_TOOLS.has(tu.name) && !(WRITE_TOOLS.has(tu.name) && opts.approval === "read_only")) {
    const d = await cb.approve({ toolUseId: tu.id, name: tu.name, input: tu.input });
    if (d.decision === "reject") return err(`The user declined to run this.${d.note ? ` Their note: ${d.note}` : ""} Don't retry it unchanged.`);
    if (d.decision === "always") cb.setAutoApproved();
    approved = true;
  }

  if (tu.name === RUN_AGENT_TOOL.name) {
    cb.setRunningTool(tu.id);
    try {
      return await runSubAgent(tu, opts);
    } finally {
      cb.setRunningTool(null);
    }
  }

  if (tu.name === ASK_FORM_TOOL.name) {
    const form = tu.input as FormSpec;
    const answer = await cb.ask(tu.id, form.title, [], form);
    return { type: "tool_result", tool_use_id: tu.id, content: answer };
  }

  if (tu.name === OFFER_RESTART_TOOL.name) {
    // The chat shows the button under the reply (ChatView); nothing else to do here.
    return { type: "tool_result", tool_use_id: tu.id, content: "The Save and restart button is shown below your reply. Don't also tell the user to restart." };
  }

  if (tu.name === ASK_SELECTION_TOOL.name) {
    const answer = await cb.askSelection(tu.id, tu.input as SelectionAsk);
    return { type: "tool_result", tool_use_id: tu.id, content: answer };
  }

  if (tu.name === ASK_SECRET_TOOL.name) {
    const r = cb.askSecret ? await cb.askSecret(tu.id, tu.input as SecretAsk) : { text: "The user can't be asked here.", isError: true };
    return { type: "tool_result", tool_use_id: tu.id, content: r.text, ...(r.isError ? { is_error: true } : {}) };
  }

  if (tu.name === ASK_USER_TOOL.name) {
    const input = tu.input as { question: string; options?: string[] };
    const answer = await cb.ask(tu.id, input.question, input.options ?? []);
    return { type: "tool_result", tool_use_id: tu.id, content: answer };
  }

  if (WRITE_TOOLS.has(tu.name)) {
    if (opts.approval === "read_only") {
      return err("Read-only mode is on: tools that change the model are disabled. Explain what you would do instead.");
    }
    if (opts.approval === "ask" && !cb.isAutoApproved() && !approved) {
      const d = await cb.approve({ toolUseId: tu.id, name: tu.name, input: tu.input });
      if (d.decision === "reject") {
        return err(`The user declined to run this.${d.note ? ` Their note: ${d.note}` : ""} Don't retry it unchanged.`);
      }
      if (d.decision === "always") cb.setAutoApproved();
    }
  }

  cb.setRunningTool(tu.id);
  try {
    const res = await cb.runTool(tu.name, tu.input, tu.id);
    return { type: "tool_result", tool_use_id: tu.id, content: res.content, ...(res.isError ? { is_error: true } : {}) };
  } catch (e) {
    return err(`The add-in failed to run ${tu.name}: ${e instanceof Error ? e.message : String(e)}`);
  } finally {
    cb.setRunningTool(null);
  }
}

/**
 * run_agent: a nested agent loop with the sub-agent's prompt (plus the CAD essentials), its model and its tools -
 * never the ask_* tools or run_agent itself - and its own step limit. Write tools still go through the parent's
 * approval; usage counts towards the parent's turn. The parent gets the final reply and a one-line list of the steps.
 */
async function runSubAgent(tu: ToolUseBlockParam, opts: AgentRunOptions): Promise<ToolResultBlockParam> {
  const { cb } = opts;
  const input = tu.input as { agent: string; prompt: string };
  const err = (text: string): ToolResultBlockParam => ({ type: "tool_result", tool_use_id: tu.id, is_error: true, content: text });
  const agents = opts.agents ?? [];
  const wanted = String(input.agent ?? "").trim();
  const byShort = agents.filter((a) => a.id.slice(a.id.indexOf(":") + 1) === wanted);
  const agent = agents.find((a) => a.id === wanted) ?? (byShort.length === 1 ? byShort[0] : undefined);
  if (!agent) return err(`Unknown agent '${wanted}'. Available: ${agents.map((a) => a.id).join(", ") || "none"}.`);

  const messages: StoredMessage[] = [{ role: "user", content: [{ type: "text", text: String(input.prompt ?? "") }] }];
  const steps: SubAgentProgress["steps"] = [];
  const report = () => cb.setSubAgent?.({ toolUseId: tu.id, agent: agent.id, steps: steps.map((s) => ({ ...s })) });
  let res: AgentRunResult;
  try {
    res = await runAgent({
      ...opts,
      // An alias resolves on the chat's provider; the sub-agent gets its own provider's key through opts.keys.
      model: resolveModel(agent.model, { chatModel: opts.model, hasKey: opts.keys.has }) ?? opts.model,
      tools: subAgentTools(agent.tools, opts.tools).filter((t) => !MEMORY_WRITE_TOOLS.has(t.name)), // only the user's own words are remembered
      maxSteps: SUB_AGENT_STEPS,
      longRun: false,
      agents: [],
      localTools: [],
      system: buildAgentPrompt(agent, { knowledge: opts.knowledge }),
      onServedTier: undefined, // the footer shows the turn's own model
      cb: {
        ...cb,
        getMessages: () => messages,
        append: async (m) => {
          messages.push(m);
          if (typeof m.content === "string") return;
          for (const b of m.content) {
            if (b.type === "tool_use") steps.push({ id: b.id, name: b.name, state: "running" });
            else if (b.type === "tool_result") {
              const step = steps.find((s) => s.id === b.tool_use_id);
              if (step) step.state = b.is_error ? "error" : "ok";
            }
          }
          report();
        },
        // Its approvals show under the parent's run_agent card: the sub-agent's own tool_use ids are in no chat message.
        approve: (req) => cb.approve({ toolUseId: tu.id, name: req.name, input: req.input, sub: { agent: agent.id, toolUseId: req.toolUseId, name: req.name, input: req.input } }),
        setLive: () => {},
        setRunningTool: () => {},
        setSubAgent: undefined,
        takeSteering: undefined,
        onRequest: undefined,
        onStatus: undefined,
        ask: async () => "(A sub-agent can't ask the user.)",
        askSelection: async () => "(A sub-agent can't ask the user.)",
        askSecret: async () => ({ text: "A sub-agent can't ask the user.", isError: true }),
      },
    });
  } finally {
    cb.setSubAgent?.(null);
  }
  const last = [...messages].reverse().find((m) => m.role === "assistant");
  const text = last && typeof last.content !== "string" ? last.content.filter((b) => b.type === "text").map((b) => (b as { text: string }).text).join("\n\n").trim() : "";
  const content: ContentBlockParam[] = [{ type: "text", text: text || "(The sub-agent ended without a reply.)" }];
  if (steps.length) content.push({ type: "text", text: `[${agent.id}: ${steps.length} step${steps.length === 1 ? "" : "s"} - ${steps.map((s) => s.name).join(", ")}]` });
  if (res.error) content.push({ type: "text", text: `[The sub-agent stopped: ${res.error}]` });
  return { type: "tool_result", tool_use_id: tu.id, content: content as ToolResultBlockParam["content"], ...(res.stop === "end_turn" ? {} : { is_error: true }) };
}

/** Minimal JSON-schema check: object, required keys present, primitive types match. */
function validateInput(def: ToolDef, input: unknown): string | null {
  if (typeof input !== "object" || input === null || Array.isArray(input)) return "input must be an object";
  const obj = input as Record<string, unknown>;
  for (const key of def.input_schema.required ?? []) {
    if (obj[key] === undefined || obj[key] === null || obj[key] === "") return `missing required '${key}'`;
  }
  for (const [key, schema] of Object.entries(def.input_schema.properties ?? {})) {
    const v = obj[key];
    if (v === undefined || v === null) continue;
    const t = schema?.type;
    if (t === "string" && typeof v !== "string") return `'${key}' must be a string`;
    if ((t === "number" || t === "integer") && typeof v !== "number") return `'${key}' must be a number`;
    if (t === "boolean" && typeof v !== "boolean") return `'${key}' must be true or false`;
    if (t === "array" && !Array.isArray(v)) return `'${key}' must be an array`;
  }
  return null;
}
