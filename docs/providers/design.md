# Model providers for SolidWorks Agent (Claude + OpenAI, more later) - design

Written 2026-10-23 on branch `claude/awesome-blackburn-350fc3` at `7a2bf2d`. Status: design for the build. Read with
`HANDOFF.md` (sections 1-6). Inputs: an inventory of everything in the repo that assumes Claude or the Anthropic API (by
file and line at `7a2bf2d`), and research on OpenAI's API as of 2026-10-23 (developers.openai.com/api/docs). Line numbers
below are at `7a2bf2d`.

Items marked **(verify)** could not be checked from here. Each one has a fallback in this design, and a hand check in
section 11.

## 0. What the user asked for

> "Let's add OpenAI API support (also in the future we will add others too). Please don't miss any integration details!
> There is a lot of Claude API specific code!"

So:

- a provider layer in the panel;
- Claude keeps working exactly as today: the same requests byte for byte, the same caching, the same features;
- OpenAI works as a first-class second provider;
- adding a third provider later means one adapter plus entries in the model list.

## 1. Decisions in one page

| Question | Decision |
|---|---|
| Stored chat format | **Keep the Anthropic block format as the canonical stored format** (`Thread.messages`). Translate to other providers inside their adapter, at request time. Old chats load unchanged, and Claude requests stay byte-identical. |
| Provider-only data (OpenAI reasoning items, `phase`, item ids, web search calls) | One optional **message-level** field, `replay`, on assistant messages. Old panels and the Claude adapter never send it: `repairHistory` rebuilds every message as `{role, content}` (section 3.2). |
| OpenAI API | **Responses API** (`POST /v1/responses`), streamed over HTTP/SSE, `store: false`, full history replayed every request. No Chat Completions: GPT-6 Astra and 6.1 Sol only do tool calls on Responses. No `previous_response_id`. No WebSocket mode (a browser can't set the auth header; it would need a C# relay). |
| OpenAI client | The `openai` npm package, pinned to an exact version at least two weeks old, with `dangerouslyAllowBrowser`, `maxRetries: 3` and the same liveness `fetch` wrapper as Claude. |
| Who picks the provider | **The model.** Every model id belongs to one provider. A chat runs on its model's provider. A chat can switch model, and so provider, between turns. Ribbon tool runs use the provider of the user's default model. |
| Keys | One DPAPI file per provider. `apikey.bin` stays the Anthropic key (no migration). OpenAI goes in `apikey-openai.bin`, with an `OPENAI_API_KEY` fallback. RPCs gain an optional `provider` argument; without it they mean Anthropic. |
| Stop reasons | Anthropic's vocabulary stays canonical (`end_turn`, `tool_use`, `max_tokens`, `refusal`, `pause_turn`, plus the panel's own stops). Every adapter maps to it. |
| Usage | `{input (uncached), output, cacheRead, cacheWrite, context}` as today. Every adapter normalises to it. |
| Context clearing on OpenAI | A client-side copy of `clear_tool_uses`, with the same trigger, keep count, minimum and exclude list. It runs in steps, so the prompt cache holds between steps. No server compaction in v1. |
| Prompt caching on OpenAI | Implicit caching. The system prompt is the first `developer` message, and tools, `parallel_tool_calls` and the parameter order are kept byte-stable. Long runs are meant to ask for `prompt_cache_options.ttl: "12h"`; as built nothing is sent until hand check H6 (6.7). |
| Web research on OpenAI | The hosted `web_search` tool, which can also open pages from its results. OpenAI has no separate `web_fetch`. No client-side fetch tool is built in v1. The prompt and the setting text say so. |
| Thinking | OpenAI `reasoning: {effort, summary: "auto"}`. Summaries show as progress notes, like Claude's thinking. The encrypted reasoning items are kept in `replay` and sent back. |
| Effort | The same five ids (`low`, `medium`, `high`, `xhigh`, `max`). Each model lists the ones it supports, and pickers show only those. |
| Speed (OpenAI only) | An Options setting, Standard / Fast / Ultrafast (`service_tier`), offered only for models that support it. Standard sends nothing. |
| Model aliases in plugins (`sonnet`, `opus`, `haiku`, `fable`, `inherit`) | An alias resolves inside the chat's provider: on Claude as today, on OpenAI through the tier table. An explicit id from a provider with no key falls back the same way (section 7.4). |
| Secrets | `ANTHROPIC_*` and `OPENAI_*` are hidden from every plugin and hook process and from git, and `${...}` of either becomes empty. Error text is scrubbed of anything that looks like a key. |
| Tests | Golden request bodies for Claude are captured from the **current** code before any refactor. The refactored Claude adapter must reproduce them byte for byte. Hand-written SSE fixtures cover both adapters. No network in tests. |

## 2. Principles

1. **Claude is the reference, not a special case.** Its adapter is today's code moved behind an interface. The golden
   check (section 5.2) proves nothing changed.
2. **One canonical history.** Everything outside the adapters reads Anthropic-shaped blocks: the renderer, tool-run
   status, counters, resend and retry, C# image stripping, the hook transcript, sub-agents. None of it learns about
   OpenAI. The exceptions are a small display helper (section 4.6) and the model registry.
3. **Capabilities, not model names.** The app asks the registry what a model supports (efforts, web, speed, cache ratio).
   It never tests `id.startsWith("claude-")` outside `inferProvider` and the Claude adapter's own `webTools`.
4. **Stateless requests.** Every request carries the whole (repaired, translated) history. Nothing depends on what a
   provider stored. That fits how the app treats history: local, editable, resendable, image-stripped on save.
5. **Downgrade safe.** A chat written by this version still loads and runs in an older add-in (on Claude). No new block
   types in `content`. No `ChatDocument.SchemaVersion` bump.
6. **No new secret exposure.** A provider key never reaches logs, chats, crash reports, prompts, git, test output or
   plugin processes. It reaches the panel's memory, as the Anthropic key does today, because the panel makes the calls.

## 3. The canonical conversation format

### 3.1 Keep the Anthropic blocks

`Thread.messages` stays the Anthropic Messages history (`ui/src/lib/types.ts:45`). Every stored chat (in
`%LOCALAPPDATA%\SwAgent\chats` and inside SolidWorks files saved by older versions) is already in this format. It has
every shape the app needs: text, images, PDF documents, tool calls, tool results with images, errors.

A neutral format of our own was considered and rejected:

- it would need a migration of every saved chat, or a translation on every load;
- the Claude path would then translate twice, which puts byte-identical requests at risk;
- about 15 readers (renderer, `toolRun.ts`, `ToolRunBanner`, `store.ts` counters, `ChatDocument.StripOldImages`,
  `Session.WriteTranscript`, the checks) would all change;
- Claude Code hook transcripts are in this format anyway.

### 3.2 The replay slot

```ts
// ui/src/lib/types.ts
export type ProviderId = "anthropic" | "openai";

/** Data only the provider that wrote a message can use. Never shown; never sent to another provider. */
export interface ProviderReplay {
  provider: ProviderId;
  /** The model that produced the message (OpenAI carries reasoning only within a model family). */
  model: string;
  /** Provider-defined. OpenAI: { output: ResponseOutputItem[] } - response.output exactly as returned. */
  data: unknown;
}

/** A stored message: the Anthropic message plus, on assistant messages from some providers, replay data. */
export type StoredMessage = MessageParam & { replay?: ProviderReplay };

export interface Thread { /* ... */ messages: StoredMessage[]; /* ... */ }
```

Why a message-level field:

- **Old panels drop it.** `repairHistory` (`history.ts:17-59`) builds every message it returns as a new
  `{ role, content }` object. Message-level fields never reach the API, in any version.
- **The Claude adapter drops it the same way.** It sends `repairHistory(messages)` as today. No new stripping code, and
  Claude requests are unchanged.
- **Block-level fields would leak.** `repairHistory` keeps block objects as they are, so an extra field on a block would
  reach Anthropic and fail with a 400.
- **C# keeps it.** `ChatDocument` and `WorkspaceChats` store the thread as a `JObject`, so unknown fields survive.
  `StripOldImages` only touches `content`. `Session.WriteTranscript` (`Session.cs:281-286`) copies only `role` and
  `content`.

Rules:

- **Claude never writes `replay`.** Its blocks are complete: thinking blocks carry their signatures, and server-tool
  results are blocks.
- **The OpenAI adapter writes `replay` on every assistant message it produces.** `data.output` is the final
  `response.output` array, unchanged.
- **The canonical `content` of an OpenAI message is derived from `output`.** Section 6.5 gives the exact mapping. Each
  `message` item gives one `text` block per `output_text` part. Each `function_call` gives one `tool_use` block. Reasoning
  and `web_search_call` items give no blocks.
- **Nothing edits assistant content today.** `resendFrom` truncates, and `StripOldImages` only replaces images, which
  only occur in user content and tool results. So `content` and `replay` stay consistent. The OpenAI adapter still checks
  that they match before using `replay` (section 6.3).
- **Size:** `replay` roughly doubles an OpenAI assistant message: arguments and text appear twice, plus encrypted
  reasoning of a few KB per step. It holds no images, so it stays small next to the screenshots. No cap in v1.

### 3.3 Stop vocabulary

`AgentRunResult.stop` and the persisted `TurnMeta.stop` keep Anthropic's strings. Adapters return one of `end_turn`,
`tool_use`, `max_tokens`, `refusal` or `pause_turn` (Claude only). `runAgent` adds `aborted`, `hook_stop` and
`iteration_limit` as today. Everything downstream is unchanged: Stop hooks, `flushQueue`, `speakReply`, `wakesMayFollow`,
and the sub-agent's `is_error` (`runAgent.ts:712`, `store.ts:949, 1149, 1341, 2309, 2321`).

### 3.4 Switching provider inside a chat

This is allowed. The chat's model can change between turns (`setThreadModel`), and a tool tier run or a sub-agent can use
another provider. Each adapter translates the whole canonical history every time:

- **To Claude:** `repairHistory` as today. OpenAI-origin messages are plain `text` and `tool_use` blocks. Their ids
  (`call_...`) fit Anthropic's id pattern. Their `replay` is dropped by the repair. Claude accepts earlier assistant turns
  without thinking blocks.
- **To OpenAI:** messages with matching OpenAI `replay` are replayed verbatim. Everything else is translated from
  blocks (section 6.3), and Claude-only blocks are dropped: `thinking`, `redacted_thinking`, `server_tool_use`,
  `web_search_tool_result` and `web_fetch_tool_result`.
- **A run never switches mid-loop.** One `runAgent` call uses one model, apart from sub-agents, which are separate loops
  with their own history.

## 4. The provider layer (`ui/src/agent/providers/`)

### 4.1 Files

| File | What |
|---|---|
| `types.ts` | The interfaces below (contract, written in step C0, section 9.1). |
| `index.ts` | `providerOf(modelId)`, `getProvider(id)`, `PROVIDERS` (the adapter list; a third provider is added here). |
| `transport.ts` | `livenessFetch(liveness)`, today's wrapper from `runAgent.ts:315-332` moved unchanged; `redactKeys(text)`. |
| `errors.ts` | `ProviderError`, `MalformedOutputError`, `AbortedError`; the user texts for each error kind (section 4.4). |
| `anthropic.ts` | The Claude adapter (section 5). Uses `webTools` / `webBetas` from `lib/creator.ts`, unchanged. |
| `openai/client.ts` | The `OpenAI` client, the request body (section 6.2), the non-retryable 429 handling. |
| `openai/history.ts` | Canonical history to Responses `input` items (section 6.3), tool definitions (6.4), clearing (6.8). |
| `openai/stream.ts` | SSE events to `LiveBlock[]`; the final `Response` to a canonical message, stop and usage (6.5, 6.10). |
| `openai/display.ts` | `displayParts` for OpenAI messages: progress notes and web steps (section 4.6). |
| `fake.ts` | `fakeFetch(script)`: serves SSE fixtures for both hosts. Used by the checks and the mock host. |
| `fixtures/*.sse`, `fixtures/golden/*.json` | Hand-written streams; Claude golden requests captured from today's code (section 5.2). |

### 4.2 Interfaces

```ts
// ui/src/agent/providers/types.ts
import type { Effort, ProviderId, StoredMessage, ToolDef, Usage } from "../../lib/types";
import type { ModelInfo } from "../../lib/models";
import type { LiveBlock } from "../runAgent";

export type StopReason = "end_turn" | "tool_use" | "max_tokens" | "refusal" | "pause_turn";

/** Today's CLEAR_OLD_TOOL_USES, with exclude_tools already filtered to this run's tools. */
export interface ClearingRules {
  triggerTokens: number;      // 40000
  keepToolUses: number;       // 6
  clearAtLeastTokens: number; // 12000
  clearInputs: boolean;       // true
  excludeTools: string[];
}

export interface TurnRequest {
  model: ModelInfo;
  effort: Effort;
  system: string;
  /** Add-in tools then local tools, canonical shape (input_schema), in today's order. */
  tools: ToolDef[];
  /** The thread as stored (unrepaired): adapters repair and translate it. */
  messages: StoredMessage[];
  /** Web research for this run: main agent only and the setting on (today's `web`). */
  web: boolean;
  longRun: boolean;
  clearing: ClearingRules;
  /** OpenAI speed setting; adapters that have no such thing ignore it. */
  serviceTier?: "fast" | "ultrafast";
}

export interface TurnResult {
  /** Canonical assistant message to append (with `replay` when the provider uses it). */
  message: StoredMessage;
  stop: StopReason;
  /** A refusal's category, when the provider gives one. */
  stopDetail?: string;
  usage: Usage;
  /** The speed tier actually served (OpenAI), when not the standard one. */
  servedTier?: string;
}

export interface TurnIO {
  /** Aborts this one request (the watchdog or the user). */
  signal: AbortSignal;
  /** The live blocks so far; the adapter calls it as events arrive (runAgent batches with requestAnimationFrame). */
  onLive(blocks: LiveBlock[]): void;
}

export interface ProviderSession {
  /**
   * One model request, streamed. Throws AbortedError (aborted by the signal), MalformedOutputError (the reply could
   * not be read: unparseable tool JSON and the like; runAgent re-sends up to 2 times), or ProviderError.
   */
  turn(req: TurnRequest, io: TurnIO): Promise<TurnResult>;
}

export interface Provider {
  id: ProviderId;
  /** Names in user-facing text: "Claude" / "OpenAI" for the service, "Anthropic" / "OpenAI" for the account. */
  label: string;
  vendor: string;
  /** Exact origin for the CSP check, e.g. "https://api.anthropic.com". */
  origin: string;
  /** The environment variable the add-in falls back to, e.g. "ANTHROPIC_API_KEY". */
  envKey: string;
  keyPlaceholder: string;   // "sk-ant-…" / "sk-proj-…"
  keysUrl: string;          // where to get a key
  /** No bytes for this long means the connection is dead (Claude 90 s, as today). */
  stallMs: number;
  open(apiKey: string, liveness: { last: number }): ProviderSession;
  /** A free call that checks a key (GET /v1/models). Network: only from the Options "Test" button. */
  testKey(apiKey: string): Promise<{ ok: true } | { ok: false; error: ProviderError }>;
  /** How a stored assistant message shows; omitted = one part per block (Claude). */
  display?(m: StoredMessage): DisplayPart[];
}

export type DisplayPart =
  | { kind: "block"; index: number }            // render content[index] as today
  | { kind: "progress"; text: string }          // a progress note (like Claude thinking with showProgress)
  | { kind: "web"; title: string; detail?: string; isError?: boolean };
```

```ts
// ui/src/agent/providers/errors.ts
export type ErrorKind = "auth" | "rate" | "quota" | "overloaded" | "context" | "policy" | "tier" | "network" | "other";

export class ProviderError extends Error {
  constructor(
    public provider: ProviderId,
    public kind: ErrorKind,
    public status: number | undefined, // HTTP status, kept for the existing `err.status` checks
    message: string,                   // already redacted (redactKeys)
    public requestId?: string,         // x-request-id / request-id: may be logged, never the key
  ) { super(message); }
}
export class MalformedOutputError extends Error {}
export class AbortedError extends Error {}
```

### 4.3 The neutral loop in `runAgent.ts`

`runAgent` keeps everything that isn't a request: the local tools, hooks, approval, validation, sub-agents, steering,
step limit and watchdog. Only lines 311-333 (the client), 336-351 (betas and tools), 392-475 (request and stream) and
480-492 (usage and stop) move into adapters. The loop becomes:

```ts
const m = modelOption(opts.model);
const provider = getProvider(m.provider);
const key = await opts.keys.get(m.provider);
if (!key) return { stop: "error", error: noKeyText(provider, m) };   // store.ts normally checks first
const liveness = { last: Date.now() };
const session = provider.open(key, liveness);
const web = !opts.system && !!opts.webResearch;           // unchanged rule
const stallMs = opts.stallMs ?? provider.stallMs;         // stallMs: test-only override
// tools, names, clearing, system: built exactly as today (lines 343-370), minus the Anthropic tool shape
for (let iteration = 0; iteration < maxSteps; iteration++) {
  // abort check, per-request controller, onRequest, watchdog with stallMs: as today
  let r: TurnResult;
  try {
    r = await session.turn({ model: m, effort: opts.effort, system, tools, messages: cb.getMessages(), web,
                             longRun: !!opts.longRun, clearing, serviceTier: opts.serviceTier }, { signal, onLive: schedule });
    malformedRetries = 0; stallRetries = 0;
  } catch (err) {
    // aborted -> { stop: "aborted" }; stalled -> today's two retries with
    // `The connection to ${provider.label} went quiet - retrying (n/2)...` and the same final error text;
    // MalformedOutputError -> up to 2 re-sends; anything else -> throw
  }
  cb.addUsage(r.usage);
  if (r.stop === "refusal") return { stop: "refusal", error: `The model declined this request${r.stopDetail ? ` (${r.stopDetail})` : ""}. Try rephrasing it.` };
  const toolUses = blocks(r.message).filter(isToolUse);
  if (r.stop === "max_tokens" && toolUses.length > 0) return { stop: "max_tokens", error: /* today's text */ };
  await cb.append(r.message);
  if (r.stop === "pause_turn") continue;
  if (toolUses.length === 0) return { stop: r.stop === "tool_use" ? "end_turn" : r.stop };
  // tool execution, hooks, steering, append of the user message: unchanged (lines 503-520)
}
```

With `provider.label = "Claude"`, the status and error texts are byte-identical to today's.

`AgentRunOptions` changes:

- `apiKey: string` becomes `keys: { get(p: ProviderId): Promise<string | null>; has(p: ProviderId): boolean }`.
  `has` reads the key sources the host reported. `get` calls `apiKey.get {provider}`.
- new `serviceTier?: "fast" | "ultrafast"`, which store.ts sets from the setting when the model supports it;
- new `stallMs?: number`, for tests only;
- new `onServedTier?(tier: string)`, which store.ts puts into `TurnMeta.serviceTier`.

`executeWithHooks`, `executeTool` and `runSubAgent` take a canonical `ToolUseBlockParam` instead of the SDK's
`BetaToolUseBlock` type. The shape is the same.

### 4.4 Errors and retries

- **Retries** stay inside each SDK: `maxRetries: 3`, backoff, and `retry-after`.
- **Abort:** the SDK's own abort error becomes `AbortedError`.
- **Malformed output:** today, any error that isn't an `APIError` is re-sent up to 2 times (`runAgent.ts:473`). The Claude
  adapter keeps that exactly: an error that isn't an `Anthropic.APIError` and isn't an abort becomes
  `MalformedOutputError`. The OpenAI adapter throws it for unparseable function arguments and for a stream that ends
  without a terminal event.
- **Errors are mapped to a kind.** `store.ts` shows the text for that kind. Anthropic's texts stay exactly as today.

| Kind | Anthropic source | OpenAI source | User text (`{vendor}` = provider.vendor) |
|---|---|---|---|
| auth | 401 | 401 | Anthropic: today's "The API key was rejected. Check it in Settings." OpenAI: "The OpenAI API key was rejected. Check it in Settings." |
| access | none | 403 for a country, region or IP block (the key is fine) | "OpenAI refused this request from your location or network. Check your OpenAI account's access." |
| rate | 429 | 429 `rate_limit_exceeded`, `slow_down` | Today's "Rate limited by the API. Wait a moment and press Retry." |
| quota | none | 429 `credit_balance_exhausted`, `organization_spend_limit_exceeded`, `project_spend_limit_exceeded`, `organization_usage_limit_exceeded` | "Your OpenAI account is out of credit or over its spend limit. Check billing on platform.openai.com, then press Retry." |
| overloaded | 529, 503 | 503 `server_is_overloaded`, 500 | Today's "The API is overloaded right now. Press Retry in a moment." |
| context | 400 "prompt is too long" | 400 `context_length_exceeded` | "This chat is too long for the model. Start a new chat, or ask for a summary in a new one." |
| policy | none | 403 `misalignment_policy_violation`, or `response.failed` with `error.misalignment` | "OpenAI stopped this run after a safety check. Review the agent's changes before you continue." Never retried. |
| tier | none | 400 invalid `service_tier` | "Your OpenAI project doesn't allow the {tier} speed. Set Speed to Standard in Options." |
| network | fetch failure after retries | same | the SDK's message |
| other | anything else | anything else | the provider's message, redacted |

- **Non-retryable 429s on OpenAI.** The OpenAI SDK retries every 429. The OpenAI `fetch` wrapper reads a 429 body (from a
  clone). When the error code is one of the quota codes, it returns the response with `x-should-retry: false` added,
  which the SDK honours **(verify in the pinned SDK's source; fallback: `maxRetries: 0` plus our own retry loop for
  `rate` and `overloaded`)**.
- **Redaction.** OpenAI's 401 message echoes part of the key ("Incorrect API key provided: sk-proj-...abcd").
  `redactKeys` replaces `/\b(sk|rk|sess)-[A-Za-z0-9_\-*.]{6,}/g` with `[key]` in every provider message before it reaches
  `TurnMeta.error`, a notice, the console or a log. Anthropic messages contain no keys, so nothing changes for Claude.
- **Request ids.** `requestId` may be written to the console on errors (`store.ts:802` logs there today). Never headers,
  never bodies.

### 4.5 Liveness and the stall watchdog

- `livenessFetch` is today's wrapper, moved unchanged. Every byte refreshes `liveness.last`.
- Claude keeps `stallMs: 90_000`. It sends keep-alive pings while it thinks.
- OpenAI documents no keep-alive events for Responses SSE. So OpenAI uses `stallMs: 180_000` and always asks for
  reasoning summaries (`summary: "auto"`), whose deltas arrive while the model reasons. **(verify: section 11, H5,
  measures the longest silence at `max` effort on Astra. If it is over 150 s, raise `stallMs` or move to background
  mode.)**
- The status and error texts use `provider.label`: "The connection to OpenAI went quiet - retrying (1/2)...".

### 4.6 What the rest of the app asks instead of model names

| Call site | Today | New |
|---|---|---|
| `runAgent.ts:334-341` | `modelOption(id).fallbacks` / `.progressUpdates`; `webBetas(m.id)` | Inside the Claude adapter, unchanged. |
| `store.ts:2542` tool runs | `tierModel(tier)` (always Claude) | `tierModel(tier, toolProvider())` (section 7.3). |
| `store.ts:1584`, `runAgent.ts:669` | `modelFromAlias(alias)` | `resolveModel(alias, ctx)` (section 7.4). |
| `store.ts:792-796` | one key | the key of the run's model's provider (section 7.6). |
| `ChatView.tsx:986` footer | "billed at 10%" | `billed at ${Math.round(model.caps.cacheReadRatio * 100)}%` (Claude 0.1, so the same text). |
| `ChatView.tsx:339-432` `buildItems` | walks `content` | walks `displayParts(message)`: `providerOf(replay?.provider)?.display?.(m) ?? content.map((_, i) => ({kind:"block", index:i}))`. Claude messages give the same parts in the same order, so they render as today. |
| Composer and Settings effort lists | `EFFORTS` | `EFFORTS.filter(e => model.caps.efforts.includes(e.id))` |
| `systemPrompt.ts` `creator(web)` | one wording | `creator(web, webTools)`: the run's `caps.web` picks the wording, `"anthropic"` today's text, `"openai"` a search-only one (section 6.9). The working-style notes come from a per-provider table (`STYLE`). |
| `WebResearchSettings.tsx` | "through Anthropic" | Provider-neutral text (section 7.7). |
| Attachments (`FileAttachments.cs`, `store.ts:396-413`) | Anthropic limits | Unchanged. Anthropic's limits (5 MB per image, 32 MB per request) are the stricter ones, so they hold for OpenAI too (50 MB per file and per request). Comments now say "the strictest provider". |

## 5. The Claude adapter (`providers/anthropic.ts`)

### 5.1 What it must preserve, exactly

The request (`client.beta.messages.stream(params, { signal })`), with the keys in this order:

1. `model: m.id`
2. `max_tokens: 64000`
3. `system: [{ type: "text", text: system }]`
4. `tools`: each add-in and local tool as `{ name, description, input_schema, eager_input_streaming: true }`, in today's
   order, then `webTools(m.id, web)`
5. `messages: repairHistory(messages)`
6. `thinking: { type: "adaptive", display: m.caps.progressUpdates ? "updates" : "summarized", block_binding: { prefix_mismatch_behavior: "drop_block" } }`
7. `output_config: { effort }`
8. `cache_control: longRun ? { type: "ephemeral", ttl: "1h" } : { type: "ephemeral" }`
9. `context_management: { edits: [clearing] }`. The adapter rebuilds today's `CLEAR_OLD_TOOL_USES` object from
   `ClearingRules`: same keys, same order, `type: "clear_tool_uses_20250919"`.
10. `fallbacks: "default"` only when `m.caps.fallbacks`
11. `betas`: `"thinking-binding-controls-2026-08-01"`, `"context-management-2025-06-27"`, then
    `"thinking-display-updates-2026-08-18"` if progress updates, then `"server-side-fallback-2026-07-01"` if fallbacks,
    then `webBetas(m.id, web)`

The client: `new Anthropic({ apiKey, dangerouslyAllowBrowser: true, maxRetries: 3, fetch: livenessFetch(liveness) })`,
one per run. The SDK stays `@anthropic-ai/sdk` at the version the lock file has now.

Streaming:

- `content_block_start` and `content_block_delta` map to `LiveBlock` exactly as in `runAgent.ts:436-451`;
- `stream.finalMessage()` gives `content`, with no `replay`;
- usage as in lines 480-484;
- `stop_reason` is passed through (`?? "end_turn"`), and `stop_details?.category` becomes `stopDetail`.

Errors: `Anthropic.APIUserAbortError` becomes `AbortedError`. Another `Anthropic.APIError` becomes `ProviderError`
(`status` kept). Anything else becomes `MalformedOutputError`.

Also unchanged:

- `history.ts` (`repairHistory`, `withoutOpenServerCalls`);
- `creator.ts` `webTools` / `webBetas` and their check;
- the system prompt text when `provider` is `"anthropic"`;
- `pause_turn` continuing the loop;
- the tool-result message layout (results, then hook text, then steering).

### 5.2 The golden check (the regression guard)

This is step **G0**, built first, on `7a2bf2d`, before C0 and before any refactor. The CORE builder writes
`ui/src/lib/checks/agent.check.mjs`. It runs `runAgent` under node, with:

- `globalThis.requestAnimationFrame = (f) => setTimeout(f, 0)` and `cancelAnimationFrame = clearTimeout`;
- fake callbacks that record `append`, `addUsage`, `setLive` (final call only), `onStatus` and the result;
- `globalThis.fetch` stubbed. It records `url`, `method`, the exact `init.body` **string**, and the headers
  `anthropic-beta`, `anthropic-version` and `content-type`. It drops `x-api-key` (it asserts the value equals the fake key
  `test-key-not-real`), `authorization`, `user-agent` and `x-stainless-*`. It answers from the scenario's SSE fixture,
  and with `retry-after-ms: 0` on error responses so SDK retries are instant.

Scenarios, with fixtures in `ui/src/agent/providers/fixtures/anthropic-*.sse`:

| Id | What it covers |
|---|---|
| C1 | Opus 5.5, effort high, web on, main agent with a fixed `buildSystemPrompt` input; plain text reply with a thinking block. |
| C2 | A tool loop: `execute_csharp` `tool_use`, then a tool result with text plus an image (from a fake `runTool`), a PostToolUse hook adding context, and a "Send now" steering message. Two requests. |
| C3 | Sonnet 5.5, `longRun` (1 h TTL), web on (dynamic web tools, no web beta). |
| C4 | Haiku 5.5: no fallbacks, `summarized`, basic web tools plus the web-fetch beta. |
| C5 | `run_agent` with a plugin agent whose front matter says `model: sonnet`. The nested request has the agent prompt, no web, no local tools, a `read_user_tool`-less clearing exclude list. |
| C6 | A stored history with an unanswered `tool_use`, a stray `tool_result`, two user messages in a row, an open `server_tool_use` not last, a `document` block and images (all `repairHistory` paths). |
| C7 | `stop_reason: "refusal"` with a category; `max_tokens` with a `tool_use`; unparseable `input_json_delta` (two identical re-sends, then success). |
| C8 | 429 then 200 (SDK retry); 401 (thrown, `status` 401); a 529. |

The check writes `fixtures/golden/claude-C*.json` with the request body strings, the headers and the recorded callback
outputs. The goldens are committed in G0 and never regenerated after that. After the refactor, the same scenarios run
through the new code, and every body must be **string-equal** and every recorded output deep-equal. A changed golden
fails the Claude acceptance (section 9.2), whatever the reason.

Stall retries can't run in G0 (90 s each). They are covered after the refactor with the test-only `stallMs` (O13).

## 6. The OpenAI adapter (`providers/openai/`)

### 6.1 Client

- `new OpenAI({ apiKey, dangerouslyAllowBrowser: true, maxRetries: 3, fetch: openaiFetch(liveness) })`.
- `openaiFetch` is `livenessFetch` plus the 429 rule (section 4.4).
- `client.responses.create(body, { signal, stream: true })` gives the raw event stream. The adapter assembles events
  itself, without the `responses.stream()` helper, which changes between SDK versions.
- **Dependency:** `"openai": "<exact>"` in `ui/package.json`. Use the newest version published on or before 2026-10-09
  (`npm view openai time`), with no caret. The research saw a breaking change in a minor release (7.43.0). The request is
  built as a plain object and cast where the SDK's types lag (for example `prompt_cache_options`).
- **CSP:** `connect-src` adds `https://api.openai.com`. No custom base URL in v1 (Azure OpenAI, proxies): each would need
  its own CSP entry.
- **No** `OpenAI-Organization` or `OpenAI-Project` headers in v1. Project keys (`sk-proj-...`) carry their project.

### 6.2 The request body (key order fixed, so the body is byte-stable)

```jsonc
{
  "model": "gpt-6.1-sol",
  "input": [ /* section 6.3: developer message with the system prompt, then the history */ ],
  "tools": [ /* function tools in today's order (6.4), then {"type":"web_search", ...} when web */ ],
  "parallel_tool_calls": true,                    // constant: changing it breaks the cache; we still run calls in order
  "max_output_tokens": 64000,                     // same as Claude; covers reasoning + reply; models allow 128K
  "reasoning": { "effort": "high", "summary": "auto" },
  "store": false,                                 // always: nothing kept server-side; stateless replay
  "stream": true,
  "include": ["web_search_call.action.sources"],  // only when web
  "max_tool_calls": 18,                           // only when web: Claude's 8 searches + 10 fetches as one cap
  "prompt_cache_options": { "ttl": "12h" },       // only when longRun and model.caps.cacheTtlLong === "12h"
  "service_tier": "fast"                          // only when the Speed setting is Fast/Ultrafast and the model supports it
}
```

Not sent:

- `instructions` (it can't be a cache boundary);
- `previous_response_id`, `conversation` and `background`;
- `truncation` (the default is off, so an overflow is a 400, mapped to `context`);
- `context_management` (compaction isn't used);
- `temperature`, `top_p`, `text`, `tool_choice` (auto), `metadata`, `safety_identifier` and `prompt_cache_key`.

`include: ["reasoning.encrypted_content"]` isn't needed: with `store: false`, reasoning items carry `encrypted_content`
by default. If one doesn't, it is not replayed (section 6.6).

Effort: `req.effort` if `model.caps.efforts` has it, else the nearest supported level (the list order is low to max, and
ties go down). No `configuration_update` items in v1: changing effort mid-chat rewrites the cache once, as a model change
does.

### 6.3 History translation (`toInput(req)`)

1. `const repaired = repairHistory(req.messages)`. This is the same pure function the Claude adapter uses, so pairing
   rules are shared.
2. Build `replayOf: Map<ContentBlockParam, ProviderReplay>` from the **unrepaired** messages: every block object of a
   message with `replay` maps to that replay. `repairHistory` keeps block objects (it only rebuilds message objects and
   arrays), so the map finds them.
3. For each repaired message, in order:

| Canonical | Responses `input` items |
|---|---|
| The system prompt | First item: `{ type: "message", role: "developer", content: [{ type: "input_text", text: system }] }` |
| User `text` | `input_text` part. Tagged panel text (`<sw_context>`, `<model_file`, `<watch_log`, `<voice_timeline`, `<hook_context`, `<system_event`) passes through as text. |
| User `image` (base64) | `{ type: "input_image", image_url: "data:<media_type>;base64,<data>", detail: "auto" }` |
| User `document` (base64 PDF) | `{ type: "input_file", filename: title ?? "document.pdf", file_data: "data:application/pdf;base64,<data>" }`. A text-source document becomes `input_text`. |
| User message | One `{ type: "message", role: "user", content: [...] }` holding its non-tool-result blocks in order, after the `function_call_output` items for its `tool_result` blocks. That is the split from inventory item 4.5: results first, then hook text and steering. |
| `tool_result` | `{ type: "function_call_output", call_id: tool_use_id, output }`. A string or all-text content becomes one string (text parts joined with `"\n\n"`). Content with images or documents becomes an array of `input_text` / `input_image` / `input_file`. With `is_error`, the first text becomes `"Error: " + text`, or an `input_text` "Error:" is put first. |
| Assistant message whose blocks all map to one OpenAI `replay` of a compatible model (same `family` in the registry), and whose derived blocks match `content` (same count, and each `function_call.call_id` equals the `tool_use.id` at its position) | `replay.data.output` items, verbatim, in order, minus reasoning items that can't be replayed (section 6.6). |
| Any other assistant message: Claude-origin, foreign model family, or failed check | `text` becomes the plain input form `{ type: "message", role: "assistant", content: text, phase }` (an `output_text` part needs the `id`, `status` and `logprobs` of OpenAI's own items). `phase` is `"commentary"` when the message has any `tool_use`, else `"final_answer"`. `tool_use` becomes `{ type: "function_call", call_id: id, name, arguments: JSON.stringify(input) }`. `thinking`, `redacted_thinking`, `server_tool_use` and `web_*_tool_result` are dropped. `citations` on text are dropped. |

- **Ids from Claude.** `call_id` passes through. Claude's `toolu_...` ids are `[A-Za-z0-9_]`, under 64 characters. An id
  outside `/^[A-Za-z0-9_-]{1,64}$/` becomes `"c_" + fnv1a64hex(id)`, and so does its result's `call_id`. **(verify H7:
  OpenAI accepts foreign `call_id`s. Fallback: re-id every non-OpenAI call as `"c_" + hash`.)**
- **The function is pure.** The same history always gives the same items, which keeps the cache valid.

### 6.4 Tool definitions

```ts
{ type: "function", name: t.name, description: t.description, parameters: forOpenAI(t.input_schema), strict: false }
```

- **`strict: false` is explicit.** The add-in's schemas have open objects, optional properties, `default`, `oneOf` and
  `anyOf`, and MCP schemas are arbitrary, so strict mode can't hold. Leaving `strict` out makes Responses try to convert
  the schema to strict mode, which turns optional fields into required nullables (the model would start sending `null`).
- **`forOpenAI`** is a pure, deep, memoised (WeakMap) copy that changes only what OpenAI rejects:
  - `{ type: "array" }` without `items` gets `items: {}` (`ToolRegistry.cs:231` can produce one; MCP schemas too);
  - the root is forced to `type: "object"`, which `McpHost.SanitizeSchema` already does.
  
  Nothing else changes: `default`, `format` and `pattern` stay.
- **No description cap and no tool-count cap.** OpenAI documents neither, and the longest add-in description is 822
  characters. **(verify H8 with a plugin that has more than 128 MCP tools.)**
- **Order:** add-in tools, then local tools, then `web_search`. That is the same order as Claude, and it stays stable
  between requests.
- `eager_input_streaming` has no equivalent. Arguments always stream.

### 6.5 Streaming and the final message

Live blocks are keyed by `output_index`:

| Event | LiveBlock |
|---|---|
| `response.output_item.added`, item `message` | `{ type: "text", text: "" }` |
| item `reasoning` | `{ type: "thinking", text: "" }` |
| item `function_call` | `{ type: "tool_use", text: "", name, id: call_id, json: "" }` |
| item `web_search_call`, or anything else | `{ type: "other", text: "" }`, which shows the Thinking indicator as Claude's `server_tool_use` does |
| `response.output_text.delta` | text += delta |
| `response.refusal.delta` | text += delta |
| `response.reasoning_summary_text.delta` | text += delta (summary parts joined with `"\n\n"` on `reasoning_summary_part.added`) |
| `response.function_call_arguments.delta` | json += delta |
| `response.completed` / `response.incomplete` | end: take `event.response` |
| `response.failed` / `error` | throw `ProviderError` (`misalignment` gives `policy`) |
| stream ends with no terminal event | throw `MalformedOutputError` (re-sent up to 2 times) |

The final canonical message is built from `response.output`, in order:

- a `message` item gives one `{ type: "text", text }` per `output_text` part (annotations kept only in `replay`). A
  `refusal` part sets `refused`.
- a `function_call` gives `{ type: "tool_use", id: call_id, name, input: JSON.parse(arguments) }`.
  - A parse failure throws `MalformedOutputError`.
  - When the item's status is `incomplete`, `input` is `{}` and nothing is parsed.
- `reasoning` and `web_search_call` give no block.
- `replay = { provider: "openai", model: model.id, data: { output: response.output } }`.

**Reasoning items are taken from the final `response.output`**, not from `output_item.added`, whose copy may be
incomplete.

**An empty message** (no blocks, for example only reasoning before a cut-off) is still returned. `runAgent` appends it as
it does for Claude today, and `repairHistory` drops it from later requests.

### 6.6 Reasoning carried across turns

A reasoning item from `replay` is sent back unchanged when all of these hold:

- it has `encrypted_content`;
- the model of the request has the same `family` as `replay.model` (OpenAI drops reasoning across families anyway);
- client-side clearing (6.8) cleared no call in that assistant message (a changed call might not match its reasoning).

Otherwise it is left out. The other items of the message are still sent. This keeps whole-chat replay stable, so the
cache holds, and follows OpenAI's rule to send back every reasoning item since the last user message.

### 6.7 Prompt caching

- **Implicit mode, the default.** The first `developer` message (the system prompt) ends at a lookup boundary, so its
  prefix is reused across chats. Each user message and the last tool output of a group are further boundaries.
- **What must not change between requests of a chat:**
  - the model;
  - the tools: names, descriptions, schemas, order, and `strict: false`;
  - `parallel_tool_calls`;
  - effort;
  - the body's key order.
  
  All of these are fixed by sections 6.2-6.4. The system prompt is already kept byte-stable for Claude, and the same
  text order serves OpenAI.
- **Long runs:** `prompt_cache_options.ttl: "12h"` (writes cost 2.5x instead of 1.25x), only when
  `model.caps.cacheTtlLong === "12h"`. The 30-minute default is a minimum that every read refreshes, so normal chats need
  nothing. **(verify H6: the create reference and the pinned SDK list only "30m".)** As built, every GPT row ships
  with `cacheTtlLong: null`, so nothing is sent and the 30-minute default applies; once H6 shows "12h" is accepted, set
  it on the rows (no code change).
- **Usage:** `cached_tokens` and `cache_write_tokens` feed the footer (section 6.10).

### 6.8 Client-side context clearing (`clearOldCalls(items, rules)`)

This is the counterpart of `clear_tool_uses_20250919` with the same `ClearingRules`. It is a pure function over the
translated `input` items, run last. The stored history is never changed.

1. **Estimate tokens**, deterministically (no tokenizer):
   - text, arguments and output strings: `ceil(chars / 4)`;
   - `input_image`: 1,600;
   - `input_file`: 3,000;
   - reasoning `encrypted_content`: `ceil(chars / 8)`.
   
   `total` is the sum over all items, plus the tool definitions (`ceil(JSON chars / 4)`).
2. If `total <= triggerTokens` (40,000), change nothing.
3. **Calls:** every `function_call` with its `function_call_output`, in order. The last `keepToolUses` (6) calls are kept,
   counted over all calls. Calls whose name is in `excludeTools` are kept. Each other call's size is its arguments plus
   its output.
4. **Target**, in steps: `target = ceil((total - triggerTokens) / clearAtLeastTokens) * clearAtLeastTokens`. Clear the
   shortest prefix of clearable calls (in order) whose sizes add up to at least `target`, or all of them.
5. **Clearing a call:**
   - its output becomes `"[Cleared to save context: an older tool result. Run the tool again if you need it.]"`, and
     its images and files are dropped;
   - with `clearInputs`, its `arguments` becomes `"{\"cleared\":true}"`;
   - the reasoning items of the same assistant message are dropped (6.6), and the rest of that message goes as if
     translated (6.3): its `function_call` and `message` items lose their item `id` and `status`, and its other items
     (`web_search_call`) are left out. OpenAI may refuse an output item replayed without the reasoning it came with.

Because the target moves in 12K steps, the set of cleared calls only grows when the estimate crosses another step. Each
growth costs one cache rewrite, as `clear_at_least` does on Claude, and requests in between are byte-identical.

Requests stay near 40-52K input tokens, far below the 272K long-context price step. There is no server compaction in v1.
If the 400 for an over-long context appears anyway, the `context` error kind tells the user what to do.

### 6.9 Web research

Tool, when `web && model.caps.web === "openai"`:

```json
{ "type": "web_search", "user_location": { "type": "approximate" }, "search_context_size": "medium" }
```

`user_location: approximate` avoids the US default. Reasoning models can open a page from the results (`open_page`,
`find_in_page`), which covers most of what `web_fetch` did. There is no tool to open an arbitrary URL.

- **Prompt:** `creator(web, "openai")` gives the same rules with these changes:
  - "web_search" names the only tool;
  - "you can open pages from search results through web_search, but not a link you were given";
  - for a link from the user, ask for the text or a screenshot.
  
  `creator(web, "anthropic")` returns today's text byte for byte.
- **`create-plugin/TOOL.md`** (EDGES) adds one sentence: "If you have no web_fetch tool, open pages through web_search
  results."
- **Display:** `openai/display.ts` turns each `web_search_call` item into
  `{ kind: "web", title, detail, isError }`:
  - `action.type: "search"` gives "Searched the web" with `action.query`;
  - `"open_page"` gives "Opened a page" with `action.url`;
  - `"find_in_page"` gives "Searched in a page" with `action.pattern`;
  - a status other than `completed` is an error.
  
  The renderer shows these as the same step cards as `server_tool_use` (`ToolCard.tsx:58-60`). `workSummary`'s
  `MINOR_TOOLS` already treats `web_search` as minor.
- **Citations:** `url_citation` annotations stay in `replay`. The text renders without inline citations, as Claude's does
  today (`citations` aren't rendered).
- **No client-side `web_fetch` in v1.** It would need a C# HTTP fetch with SSRF rules, an allow-list and the "only links
  from the chat or search results" rule, and it would change the CSP story in `creator-v2.md`. That is listed in
  section 12.

### 6.10 Stop, usage, served tier

| Response | `stop` | Notes |
|---|---|---|
| `completed`, a refusal part and no calls | `refusal` | `stopDetail` undefined. runAgent shows today's text. |
| `completed`, one or more `function_call` | `tool_use` | |
| `completed`, no calls | `end_turn` | |
| `incomplete`, `max_output_tokens` | `max_tokens` | With a cut-off call, runAgent shows today's "cut off in the middle of a tool call" error. |
| `incomplete`, `content_filter` | `refusal` | `stopDetail: "content filter"` |
| `incomplete`, other reason | `max_tokens` | `stopDetail` is the reason. No error, as Claude's `max_tokens` without tools. |
| `failed` | throws | Section 4.4. |

Usage, from the research's mapping:

- `input = input_tokens - cached_tokens - cache_write_tokens`
- `cacheRead = cached_tokens`
- `cacheWrite = cache_write_tokens` (0 when absent)
- `output = output_tokens` (reasoning included)
- `context = input_tokens`

`servedTier`: `response.service_tier` when it isn't `default` or `auto`. store.ts saves it as `TurnMeta.serviceTier`, and
the footer shows "Fast" or "Ultrafast" next to the effort.

### 6.11 What degrades on OpenAI, and what the user sees

| Missing on OpenAI | What happens | What the UI says |
|---|---|---|
| `web_fetch` (open a given link) | The agent searches, and opens pages only from results. | Options > Web research note (section 7.7). The prompt tells the agent to ask for the page text. |
| Server-side refusal fallback | A refusal ends the turn. | Today's "The model declined this request. Try rephrasing it." |
| `thinking.display: "updates"` | Reasoning summaries show as progress notes instead. | Nothing: same setting, "Show progress". |
| `pause_turn` | Not needed: hosted search finishes within one response. | Nothing. |
| Fine-grained tool streaming flag | Arguments stream anyway. | Nothing. |
| Refusal category | Not given (except "content filter"). | The refusal text without a category. |
| Server-side tool clearing | Done client-side (6.8). | Nothing. |
| 1-hour cache | 30 min by default, 12 h for long runs. | Nothing. |

## 7. Models, keys, settings

### 7.1 The registry (`ui/src/lib/models.ts`)

```ts
export interface ModelCaps {
  efforts: Effort[];             // offered levels, low..max order
  vision: boolean;               // images in and in tool results
  pdf: boolean;                  // document / input_file
  web: "anthropic" | "openai" | null;
  progressUpdates: boolean;      // Claude: thinking.display "updates"
  fallbacks: boolean;            // Claude: server-side refusal fallback
  cacheReadRatio: number;        // price of a cached input token vs uncached (footer text)
  cacheTtlLong: "1h" | "12h" | null;
  serviceTiers: ("fast" | "ultrafast")[];
  contextWindow?: number;       // informational; nothing reads it in v1
  maxOutput: number;
}
export interface ModelInfo {
  id: string;
  provider: ProviderId;
  label: string;
  note: string;
  /** Reasoning compatibility and alias mapping: models of one family share reasoning. */
  family: string;
  /** Which tier this model stands for when a plugin names it but its provider has no key. */
  tier: Tier;
  caps: ModelCaps;
  /** USD per 1M tokens (prompts up to 272K for OpenAI); for later cost display, not shown in v1. */
  price?: { input: number; cachedInput: number; cacheWrite: number; output: number };
}
export const MODELS: ModelInfo[];          // the order is the picker order: Claude first, then OpenAI
export function modelOption(id: string): ModelInfo;      // unknown id: inferred provider, conservative caps
export function inferProvider(id: string): ProviderId;   // registry, then /^claude-/ anthropic, /^(gpt-|o\d|chatgpt-)/ openai, else anthropic
```

`fallbacks` and `progressUpdates` move under `caps`. The Claude adapter reads `m.caps.fallbacks`. `ModelOption` stays as
an alias of `ModelInfo` until every caller is updated.

Entries:

| id | provider | label | note | family | tier | efforts | web | progress | fallbacks | cacheReadRatio | cacheTtlLong | serviceTiers | context / maxOutput |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| claude-opus-5-5 | anthropic | Opus 5.5 | Default · best balance for CAD work | claude | high | all 5 | anthropic | yes | yes | 0.1 | 1h | - | - / 64000 |
| claude-fable-5-1 | anthropic | Fable 5.1 | Most capable · about 2.5× the cost | claude | max | all 5 | anthropic | yes | yes | 0.1 | 1h | - | - / 64000 |
| claude-sonnet-5-5 | anthropic | Sonnet 5.5 | Faster · half the cost | claude | balanced | all 5 | anthropic | yes | yes | 0.1 | 1h | - | - / 64000 |
| claude-haiku-5-5 | anthropic | Haiku 5.5 | Fastest · for quick questions | claude | fast | all 5 | anthropic | no | no | 0.1 | 1h | - | - / 64000 |
| gpt-6.1-sol | openai | GPT-6.1 Sol | OpenAI default · best balance | gpt-6 | balanced | all 5 | openai | no | no | 0.05 | 12h | fast, ultrafast | 1.05M / 128000 |
| gpt-6-astra | openai | GPT-6 Astra | Most capable · about 5× the cost of Sol | gpt-6 | high | all 5 | openai | no | no | 0.1 | 12h | fast, ultrafast | 1.05M / 128000 |
| gpt-6-luna | openai | GPT-6 Luna | Fastest · for quick questions | gpt-6 | fast | all 5 | openai | no | no | 0.1 | 12h | fast | 1.05M / 128000 |

- **Claude rows** keep today's ids, labels and notes. Their context sizes are left out until checked; nothing reads them in v1.
- **Unknown Claude ids** get today's conservative behaviour: no fallbacks, no progress updates. `webTools` keeps its own
  regex.
- **Unknown OpenAI ids:** all five efforts, `web: "openai"`, no long cache TTL, no speed tiers.
- **Prices** are from the research (for example Sol: 2 / 0.10 / 2.50 / 10). Claude prices are left out until checked.
- **Luna's speed tiers:** Fast only (OpenAI's fast-mode guide and pricing list Luna; Ultrafast is Astra and 6.1 Sol).
- **As built, `cacheTtlLong` is null on every GPT row** until H6 (6.7); the 12h in this table is the target.
- **Families:** `gpt-6` covers 6 and 6.1 because OpenAI says reasoning carries within a family. **(verify H9: if 6.1 Sol
  drops Astra's reasoning, nothing breaks; the items are only wasted input.)**

### 7.2 Effort

`EFFORTS` stays as it is: ids, labels and notes. Pickers filter by `caps.efforts`. A thread's effort that the model
doesn't offer is shown as the nearest one and sent as that (6.2). Settings keep `defaultEffort: "high"`.

### 7.3 Tiers and ribbon tool runs

```ts
export const TIERS: Record<Tier, { label: string; note: string; anthropic: TierModel; openai: TierModel }> = {
  fast:     { /* label, note as today */ anthropic: { model: "claude-haiku-5-5", effort: "low" },     openai: { model: "gpt-6-luna",  effort: "low" } },
  balanced: { /* ... */                  anthropic: { model: "claude-sonnet-5-5", effort: "medium" }, openai: { model: "gpt-6.1-sol", effort: "medium" } },
  high:     { /* ... */                  anthropic: { model: "claude-opus-5-5", effort: "medium" },   openai: { model: "gpt-6-astra", effort: "medium" } },
  max:      { /* ... */                  anthropic: { model: "claude-opus-5-5", effort: "high" },     openai: { model: "gpt-6-astra", effort: "high" } },
};
export function tierModel(tier: Tier | undefined, provider: ProviderId = "anthropic"): TierModel;
```

`TierModel.effort` widens to `Effort`. `toolProvider()` in store.ts picks:

1. the provider of `settings.defaultModel`, if it has a key;
2. otherwise the first provider in `PROVIDERS` order that has a key;
3. otherwise `"anthropic"`, and the no-key notice follows.

`intelligence: max` tools (the mold tool, Smart Rebuild) run on Astra at high effort on OpenAI.

### 7.4 Model names from plugins, commands and sub-agents

`resolveModel(name, ctx)` replaces `modelFromAlias`. Here `ctx = { chatModel: string; hasKey(p): boolean }`, and it
returns a model id or `null` (use the chat's model).

| Input | Chat on Claude | Chat on OpenAI |
|---|---|---|
| empty, `inherit` | null | null |
| `haiku`, `sonnet`, `opus`, `fable` | The Claude family model, as today. | The tier model on OpenAI: haiku is fast, sonnet balanced, opus high, fable max. So `sonnet` gives `gpt-6.1-sol`. |
| A registry id or label (`claude-haiku-5-5`, `GPT-6 Astra`) | That model if its provider has a key, else the tier model of that model's `tier` on the chat's provider. | same |
| An unknown id with a known prefix (`claude-...`, `gpt-...`) | As written, if that provider has a key, else null. | same |
| Anything else | null | null |

`modelFromAlias` stays as a thin wrapper, `resolveModel(a, { chatModel: "claude-opus-5-5", hasKey: () => true })`, so
existing callers and checks keep their meaning during the build. lib.check's `modelFromAlias("gpt-5") === null` becomes
`resolveModel("gpt-5", {chatModel: "claude-opus-5-5", hasKey: p => p === "openai"}) === "gpt-5"` and
`... hasKey: p => p === "anthropic" ... === null`.

Callers:

- `runSubAgent` (`runAgent.ts:669`): `resolveModel(agent.model, { chatModel: opts.model, hasKey: opts.keys.has }) ?? opts.model`.
  The sub-agent gets its own provider's key through `opts.keys` (section 4.3).
- `store.ts:1584`, commands: the same, with the thread's model.
- `/model`: `resolveModel`, then a label or id substring over `MODELS`. Hints come from the registry.

**Usage across providers in one turn** adds up as tokens. The footer shows the turn's own model, as today.

### 7.5 Key storage (C#, `Infrastructure/SettingsStore.cs`)

```csharp
internal sealed class ProviderKeyInfo { public string Id, FileName, Entropy, EnvVar; }
internal static class ProviderKeys
{
    // Adding a provider: one line here, one in SecretEnv (7.8), one adapter in the panel.
    public static readonly ProviderKeyInfo[] All =
    {
        new ProviderKeyInfo { Id = "anthropic", FileName = "apikey.bin",        Entropy = "SwAgent.ApiKey.v1",        EnvVar = "ANTHROPIC_API_KEY" },
        new ProviderKeyInfo { Id = "openai",    FileName = "apikey-openai.bin", Entropy = "SwAgent.ApiKey.openai.v1", EnvVar = "OPENAI_API_KEY" },
    };
    public static ProviderKeyInfo Get(string id); // null/"" = anthropic; unknown id throws ArgumentException
}
// SettingsStore:
public static string GetApiKey(string provider = null);      // stored, else env, else null
public static string ApiKeySource(string provider = null);   // "stored" | "environment" | "none"
public static JObject ApiKeySources();                       // { "anthropic": "...", "openai": "..." }
public static void SetApiKey(string provider, string key);   // "" deletes the file
```

- **Files** are under `Paths.RoamingDir`, so `SWAGENT_DATA_DIR` redirects them in tests. DPAPI `CurrentUser` scope.
- **Writes:** temp file next to the target, then `AtomicFile.Replace`, and the temp is deleted on failure. That applies
  to the new file **and** to the existing `apikey.bin` and `settings.json` writes (lines 35 and 72 write directly today).
  The file format doesn't change, so older add-ins still read both files.
- **The Anthropic file name, entropy and format are unchanged.** A key saved by today's add-in decrypts in the new one and
  the other way round.
- **Logging:** "Could not decrypt stored API key ({provider})". Never the key or its length.
- **The uninstaller** leaves key files, as today.

### 7.6 Host RPCs

| RPC | Change |
|---|---|
| `apiKey.get` | `{ provider?: string }` gives `{ key }`. With no provider: Anthropic, so older panels keep working. |
| `apiKey.set` | `{ provider?: string, key: string }` gives `{ source, sources }`. `source` is that provider's. |
| `app.getState` | Adds `apiKeySources: { anthropic, openai }`. Keeps `apiKeySource` (Anthropic) for compatibility. |

`HostState` gains `apiKeySources: Record<ProviderId, "stored" | "environment" | "none">`. The store keeps it in state
(`apiKeySources`), updates it from `apiKey.set`, and derives `keys.has(p)` from it.

`startRun` (`store.ts:792-796`) works out the run's model (`run.model ?? thread.model`) and its provider:

- if `keys.has(provider)` is false: no run, Options opens with the provider's section expanded, and a notice:
  - Anthropic: "Add your Anthropic API key to start chatting." (today's text, when no provider has a key at all);
  - otherwise: "{model label} needs an {vendor} API key. Add one in Options, or pick another model.";
- else `runAgent` is called with `keys: { get: (p) => rpc("apiKey.get", { provider: p }).then(r => r.key), has }`.

Boot (`store.ts:1887-1903`) opens Options when **no** provider has a key and it isn't the first run, as today for
Anthropic.

### 7.7 Settings UI, pickers, first run, wording

**Options > "AI providers"** replaces the "Anthropic API key" rollout. It has one row per provider, in `PROVIDERS`
order:

- title "Claude (Anthropic)" / "OpenAI";
- status: "Saved", "Using the {ENV} environment variable", or "Not set";
- password field with the provider's placeholder;
- Save, Test and Remove buttons;
- "Get a key…" opening `keysUrl` through `app.openExternal`:
  - Anthropic: `https://platform.claude.com/settings/keys`, as today;
  - OpenAI: `https://platform.openai.com/api-keys` **(verify the page still lives there)**.

Test calls `provider.testKey`. It shows "Key works." or the mapped error text. The OpenAI row also has **Speed**
(Standard / Fast, about 2× the cost / Ultrafast, about 6× the cost), with the note "Fast and Ultrafast apply to models
that support them."

**Model pickers** (Composer, Options default model):

- list the models of providers that have a key, plus the chat's current model;
- group them under "Claude" and "OpenAI" headers when more than one provider is listed;
- with one provider, look exactly as today.

**Effort pickers** filter by `caps.efforts`.

**Tour:**

- the "Connect your Anthropic account" step becomes "Connect an AI provider", shown when no provider has a key;
- it offers two choices, "Claude (Anthropic)" and "OpenAI", then the key field for the one picked;
- picking OpenAI and saving a key sets `defaultModel: "gpt-6.1-sol"`, when the default is still a Claude model and no
  Anthropic key exists;
- the "Opus at High is the default..." line takes the default model's label from the registry.

**Text changes** (all user-facing, plain, sentence case):

| Where | New text |
|---|---|
| `SettingsPanel.tsx:37-77` | As above. Body text: "Paste an API key from {vendor}. It's encrypted for your Windows account and never stored in your files." |
| `Tour.tsx:292-330` | "The agent runs on Claude or OpenAI models. Pick one and paste an API key - it's encrypted for your Windows account and never stored in your files." |
| `WebResearchSettings.tsx:15` | "The agent can look things up, e.g. a project you want a plugin for. Searches run through your AI provider and cost a little extra. With OpenAI, the agent can open pages from search results but not a link you give it." |
| `commands.ts:21-22` | `/model <name>` hint lists the labels of models with a key; `/effort` unchanged. |
| `UserTools.cs:260` (`save_user_tool.model`) | "Workflows: a model id from Options (e.g. claude-sonnet-5-5 or gpt-6.1-sol), or omit to use the intelligence tier." |
| `UserTools.cs:276, 367` (`intelligence`) | "Maps to the user's provider (e.g. Claude Haiku / Sonnet / Opus, or GPT-6 Luna / 6.1 Sol / 6 Astra)." |
| `SettingsPanel.tsx:110, 435`, voice "about 1,000 tokens" | Unchanged: still right for both providers (a 1024 px image is about 1,229 tokens on OpenAI). |

### 7.8 Settings fields

`Settings` gains one field: `openaiSpeed?: "standard" | "fast" | "ultrafast"`, default `"standard"`. Default settings
merge over stored ones as today. `SETTINGS_VERSION` stays 2, with no migration: `defaultModel` stays whatever it is.
`TurnMeta` gains `serviceTier?: string`.

### 7.9 Secrets away from plugins, hooks and git (C#)

New `Infrastructure/SecretEnv.cs`:

```csharp
internal static class SecretEnv
{
    // Provider key prefixes: hidden from every child process, and ${...} of them expands to "".
    public static readonly string[] ProviderPrefixes = { "ANTHROPIC_", "OPENAI_" };
    public static bool IsProviderSecret(string name);
}
```

- **`ChildEnv.Hidden`** (`PluginProcess.cs:389`) uses `SecretEnv.IsProviderSecret` instead of the `ANTHROPIC_` test. The
  Claude Code markers stay as they are.
- **`PluginManager.Expand`** (`PluginManager.cs:409`): `${OPENAI_*}` becomes `""`, as `${ANTHROPIC_*}` does.
- **`PluginManager.IsBlockedEnv` is unchanged.** It stays `ANTHROPIC_*` and `CLAUDE_CODE_*`. A plugin may set
  `OPENAI_API_KEY` for its own MCP server from its own `${user_config.key}`: that is the plugin's secret, not the user's
  agent key, and blocking it would break real OpenAI-using MCP servers. The user's own `OPENAI_API_KEY` can't leak that
  way: the variable is hidden from the child, and `${OPENAI_API_KEY}` expands to "".
- **`Marketplaces.cs:485`:** git's environment drops every `SecretEnv` match, not only `ANTHROPIC_API_KEY`.
- **`CreatorTools` `check_prerequisites`** runs third-party programs, so it starts them with `ChildEnv.Base()`.
- **`ExecutableResolver`, `SpeechEngine`, `Updater` and `RestartHelper`** start our own helpers or Windows tools. They are
  unchanged (existing behaviour, low risk).
- **In-process code** (`execute_csharp` cells, approved plugin tool `.cs`) runs as the same Windows user. It could read
  environment variables or decrypt the DPAPI files. That is existing exposure, the same for every provider, and stated
  in `authoring.md`.

## 8. Every inventory item, with its decision

Inventory sections A-S, item by item. The track is CORE (C), EDGES (E) or the contract step C0.

| Inventory item | Decision | Track |
|---|---|---|
| A. `@anthropic-ai/sdk` | Kept, same version, Claude adapter only. | C |
| A. `openai` package | Added, exact pin (6.1). | C |
| A. `runAgent.ts:311-333` client and liveness fetch | Moved to `transport.ts` and the adapters, unchanged. | C |
| A. CSP `ui/index.html:7` | Adds `https://api.openai.com`. A check asserts every `Provider.origin` is in it. | E (the check: C) |
| A. WebView2 navigation allow-list | No change (CSP governs fetch). CORS from `https://swagent.app`: OpenAI supports browser calls **(verify H1)**. | - |
| A. HANDOFF dev stub | Replaced by `fake.ts` in the mock host (`?fake`). | E (fake.ts: C) |
| B. Betas, thinking, `output_config`, `cache_control`, `context_management`, `fallbacks`, `max_tokens`, system block, tool shape | Claude adapter, unchanged (5.1). OpenAI mapping in 6.2-6.4. | C |
| B. No temperature, `tool_choice`, etc. | Still none, on both. | C |
| C. `LiveBlock` | Kept as the adapters' live shape. | C |
| C. Event handling 419-452 | Claude adapter. OpenAI per 6.5. | C |
| C. Malformed JSON retries | Kept in runAgent via `MalformedOutputError`. | C |
| C. Stall watchdog and its "Claude" texts | Neutral loop with `provider.label` and `stallMs` (4.5). | C |
| C. ChatView "Thinking..." timer and hint | Unchanged. | - |
| C. ChatView `liveItems` | Unchanged (OpenAI maps onto the same live types). | - |
| D. Usage semantics | Normalised per adapter (6.10). | C |
| D. Refusal | `stop: "refusal"` from both. | C |
| D. `max_tokens` with a tool | Unchanged rule. | C |
| D. `max_tokens` without tools (silent truncation) | Kept as is for both (existing behaviour). | - |
| D. `pause_turn` | Claude only, loop unchanged. | C |
| D. Stop vocabulary and the downstream `end_turn` tests | Canonical Anthropic strings (3.3). | C |
| D. Error mapping `store.ts:952-958` | By `ProviderError.kind` (4.4). Anthropic texts identical. | E (texts in `errors.ts`: C) |
| D. Footer "billed at 10%" | From `cacheReadRatio` (4.6). | C |
| E. `ToolResult` and Anthropic image blocks in C# | Canonical, unchanged. | - |
| E. `Definitions()` in Claude shape | Canonical. The OpenAI adapter maps it (6.4). | C |
| E. Open object schemas | `strict: false`; `forOpenAI` fixes arrays without items. | C |
| E. Description lengths, tool count | No caps (6.4), verify H8. | C |
| E. MCP names and `SanitizeSchema` | Unchanged (64-char charset fits OpenAI). | - |
| E. `is_error` | "Error: " prefix on OpenAI. | C |
| E. Images in tool results | `function_call_output` arrays (6.3). | C |
| E. Hooks, `validateInput`, `WRITE_TOOLS` | Unchanged. | - |
| F. `run_agent` reuses the parent's key | `opts.keys` per provider (4.3). | C |
| F. `modelFromAlias` | `resolveModel` (7.4). | E (the runAgent call site: C) |
| F. Agent and command front matter | Data, unchanged. Mapping per 7.4. | E |
| F. `create-plugin/TOOL.md` model text | Adds "aliases resolve to the user's provider". | E |
| F. `docs/plugins/design.md` and `authoring.md` model docs | Same note. | E |
| F. `UserTools.cs:260, 276, 367` | New wording (7.7). | E |
| G. Canonical history, `Thread.messages` | Kept. `StoredMessage` with `replay` (3.2). | C0 |
| G. `TurnMeta` | Adds `serviceTier?`. Provider derived from `model`. | C0 |
| G. `repairHistory` | Unchanged, shared by both adapters. | - |
| G. `withoutOpenServerCalls` | Unchanged (Claude). The OpenAI translation drops server blocks. | - |
| G. Block types in stored history | Translation table 6.3. | C |
| G. ChatView `buildItems` and `promptItem` | `displayParts` (4.6). `promptItem` unchanged. | C |
| G. `ToolCard`, `toolRun.ts`, `ToolRunBanner`, store counters | Canonical, unchanged. | - |
| G. `resendFrom` and `retry` | Unchanged. Stateless replay tolerates truncation. | - |
| G. `ChatDocument` schema, `StripOldImages`, `WorkspaceChats`, `importDocChats` | Unchanged. No schema bump. `replay` holds no images. | - |
| G. `Session.WriteTranscript` | Unchanged (role and content only). | - |
| G. Mid-chat provider switch | Allowed (3.4). | C |
| H. `FileAttachments` limits | Unchanged (strictest provider). Comments updated. | E |
| H. Image and PDF blocks everywhere | Canonical. `input_image` / `input_file` (6.3). | C |
| H. `capture_views` token text | Unchanged (7.7). | - |
| H. Vision and PDF need | `caps.vision` / `caps.pdf`. Every v1 model is true for both. | C0 |
| I. `systemPrompt.ts` stability and order | Unchanged for Claude. `provider` param (below). | C |
| I. `creator(web)` | `webTools` variant (6.9). | C |
| I. OpenAI prompting notes (Astra asks more, heavy Markdown) | For `provider: "openai"`, a short "Working style" paragraph after the core section: act without asking when the answer can be inferred, carry the task through to the end, keep Markdown light (short paragraphs, lists only for steps). The text is fixed in the check. | C |
| I. "Some skills were written for Claude Code" and MCP heading | Kept (plugin compatibility). | - |
| I. `workSummary` `MINOR_TOOLS` | Unchanged. | - |
| I. `runAgent.ts:466, 469` | `provider.label`. | C |
| I. `store.ts:795, 1778` notices | Per provider (7.6). | E |
| I. SettingsPanel, Tour, WebResearchSettings | 7.7. | E |
| I. ChatView comment line 410 | "Web research runs on the provider's side". | C |
| I. PluginsScreen "Claude Code marketplaces" | Kept. | - |
| I. agent-bridge overlay `SKILL.md:40, 121` | "from your AI provider or from the system"; never send "your AI provider API keys". The fixture copies under `tests/SwAgent.Tests/Plugins/Fixtures/agent-bridge/**` are updated in the same commit if a test compares them. | E |
| J. `ModelOption`, `MODELS` | Registry (7.1). | C0 |
| J. `EFFORTS` | Kept, filtered per model. | E |
| J. `SETTINGS_VERSION`, `DEFAULT_SETTINGS` | No bump. Adds `openaiSpeed`. | C0 |
| J. `modelOption` unknown ids | `inferProvider` (7.1). | C0 |
| J. `TIERS` and `tierModel` | Adds `openai`, a provider argument, `toolProvider()` (7.3). | C0 (data), E (store) |
| J. Composer, Settings selects, `/model`, `/effort` | 7.7. | E |
| J. `setThreadModel` and `Effort`, new threads | Unchanged. | - |
| J. Tool runs `store.ts:2542` | `tierModel(tier, toolProvider())`. | E |
| J. C# tiers and built-in tools' `intelligence: max` | Unchanged (Astra high on OpenAI). | - |
| K. `webTools` / `webBetas` | Stay in `creator.ts`, Claude adapter only. | - |
| K. OpenAI web search | 6.9. | C |
| K. `FETCH_ERRORS` and `webResultSummary` | Claude only. OpenAI summaries in `openai/display.ts`. | C |
| K. `web_fetch` on OpenAI | Not in v1 (6.9, section 12). | - |
| K. `creator.check.mjs` | Unchanged (Claude). OpenAI web cases go in `providers.check.mjs`. | C |
| K. `Settings.webResearch` | Kept, same default. Text per 7.7. | E |
| K. `creator-v2.md:84-85` CSP note | Adds: "On OpenAI, search runs on OpenAI's side too; there is no fetch tool." | E |
| L. `clear_tool_uses` | Claude unchanged. OpenAI 6.8. | C |
| L. `longRun` cache TTL | 1 h on Claude, 12 h on OpenAI (6.7). | C |
| L. Prompt cache order, knowledge index rewrite | Same for both. | - |
| L. `count_tokens` | Not used (an estimate on OpenAI, 6.8). | - |
| M. `SettingsStore` | Per-provider keys, atomic writes (7.5). | E |
| M. RPCs and `getState` | 7.6. | E |
| M. store key state, boot, `startRun` | 7.6. | E |
| M. Key UI and Tour | 7.7. | E |
| M. mockHost keys | `swagent-mock:apiKey` stays Anthropic. Adds `swagent-mock:apiKey:openai`. | E |
| M. README | 7.7 wording plus a section "Model providers". | E |
| N. Env hiding, `Expand`, git, `check_prerequisites` | 7.9. | E |
| N. `authoring.md:206-207` | "Your AI provider keys are never in a plugin's environment." | E |
| N. Env-hiding tests | OpenAI cases added (9.3). | E |
| N. Logs and telemetry | Unchanged, plus `redactKeys` (4.4). | C |
| O. Claude Code plugin format and hook protocol | Unchanged on purpose. | - |
| P. Question timeout and ask flow | Unchanged. | - |
| Q. Voice | Unchanged (local Whisper and SAPI). `speakReply` gated on canonical `end_turn`. | - |
| R. Mock host | `fake.ts` streams, per-provider keys, a demo thread with one OpenAI turn (with `replay`). | E (fake.ts and fixtures: C) |
| R. `lib.check.mjs` | `modelFromAlias` cases updated (7.4). | E |
| R. C# tests asserting Anthropic shapes | Still valid (canonical). | - |
| R. No agent-loop tests | `agent.check.mjs` (G0) and `providers.check.mjs`. | C |
| S. README, HANDOFF, plugin docs | Updated. | E |
| S. Compression-mold research notes | Historical, unchanged. | - |
| S. NIGHTLY and RELEASE notes | Written at release time, not in this build. | - |
| S. Installer, updater, CI | No change. | - |

## 9. The build

### 9.1 Order

1. **G0 (CORE builder, first, on `7a2bf2d`):** `agent.check.mjs`, the Anthropic SSE fixtures and the golden files
   (5.2). Commit: "Golden Claude requests before the provider refactor". Nothing else changes.
2. **C0, the contract (CORE builder, right after G0):**
   - `ui/src/lib/types.ts`: `ProviderId`, `ProviderReplay`, `StoredMessage`, `Thread.messages: StoredMessage[]`,
     `TurnMeta.serviceTier`, `Settings.openaiSpeed`, `HostState.apiKeySources`, `KeySource`.
   - `ui/src/lib/models.ts`: `ModelInfo`, `ModelCaps`, the full `MODELS` table (7.1), `inferProvider`, `TIERS` with
     `openai`, `tierModel(tier, provider = "anthropic")`, `resolveModel`, `modelFromAlias` as the wrapper.
     `modelOption` returns `ModelInfo`.
   - `ui/src/agent/providers/types.ts` and `errors.ts` (4.2).
   - `runAgent.ts` only as needed to compile (`m.caps.fallbacks`).
   
   After C0, `agent.check.mjs` must still pass. Commit: "Provider contract: types, model registry".
3. **CORE and EDGES in parallel**, in separate worktrees branched from C0. Each owns its files (9.2, 9.3). EDGES can start
   its C# part before C0 lands: that part doesn't depend on it.
4. **Integration:** merge both, then:
   - the full `dotnet test tests/SwAgent.Tests`;
   - the Debug build;
   - `npx tsc -b && npm run build`;
   - every `node ui/src/lib/checks/*.check.mjs`;
   - the mock host in a browser with `?fake`, on a Claude model and an OpenAI model.

### 9.2 CORE track

**Owns:**

- `ui/src/agent/providers/**` (all new files, fixtures and goldens);
- `ui/src/agent/runAgent.ts`, `ui/src/agent/history.ts` (expected unchanged) and `ui/src/agent/systemPrompt.ts`;
- `ui/src/components/ChatView.tsx`;
- `ui/package.json` and `ui/package-lock.json` (the `openai` pin);
- `ui/src/lib/checks/agent.check.mjs` and `ui/src/lib/checks/providers.check.mjs`.

**Reads but doesn't change:** `ui/src/lib/models.ts`, `ui/src/lib/types.ts` (after C0) and `ui/src/lib/creator.ts`.

**Contract to EDGES:**

- `runAgent(opts)` takes `keys`, `serviceTier`, `onServedTier` and the optional `stallMs`;
- `ProviderError` and `errorText(err: ProviderError, ctx)` from `errors.ts`;
- `getProvider(id)` and `PROVIDERS` (with `label`, `vendor`, `origin`, `envKey`, `keyPlaceholder`, `keysUrl`, `testKey`);
- `fakeFetch(script)` and `defaultFakeScript`.

**Acceptance** (all under node, no network):

1. **Claude goldens.** `agent.check.mjs` passes on the refactored code: every request body string-equal to G0, and
   every recorded callback deep-equal.
2. **`providers.check.mjs`** covers these scenarios:

| Id | What it checks |
|---|---|
| O1 | Text reply with a reasoning summary. The request body equals a committed golden (developer message first, key order per 6.2, `store: false`). Live blocks give thinking, then text. The appended message is canonical text plus `replay`. Usage is normalised. Stop is `end_turn`. |
| O2 | A tool loop: reasoning (with `encrypted_content`), commentary text and a `function_call`. The tool result holds an image, a hook adds context, and steering is queued. The second request replays the output items verbatim, then a `function_call_output` with an `input_image` array, then a user message with the hook text and the steering, in that order. |
| O3 | A refusal part gives `refusal` and today's error text, and nothing is appended. |
| O4 | `incomplete` / `max_output_tokens` with a cut-off call gives the "cut off" error. Without a call: `max_tokens`, no error. |
| O5 | `web_search_call` and `url_citation`: no canonical blocks for the call, and the display parts show a web step. The request has the `web_search` tool, `include` and `max_tool_calls: 18` only when `web`. A sub-agent never gets them. |
| O6 | `response.failed` with misalignment gives a `ProviderError` of kind `policy`, and there is one request (no retry). |
| O7 | 429 `credit_balance_exhausted` gives kind `quota` after exactly one request. |
| O8 | 429 `rate_limit_exceeded`, then 200: retried, and it succeeds. |
| O9 | Unparseable arguments are re-sent twice, then succeed. Three failures throw. |
| O10 | Provider switch to OpenAI. A Claude history (thinking with signature, `server_tool_use` and a web result, `document`, `tool_result` with images, `toolu_` ids, an interrupted call) gives the committed golden `input`. The Claude-only blocks are gone, and the ids are preserved. |
| O11 | Provider switch to Claude. A history with OpenAI turns, sent through the Claude adapter, gives messages with no `replay` and with the `call_` ids as `tool_use` ids. |
| O12 | Clearing. A synthetic history grows one tool call at a time. The cleared set is a prefix, grows only when the estimate crosses a 12K step, never touches excluded tools or the last 6 calls, and drops reasoning only in turns with cleared calls. Requests between steps are string-equal in their prefix. |
| O13 | Stall, with `stallMs: 50`. A hanging stream retries twice with "The connection to OpenAI went quiet - retrying (n/2)...", then throws the final text. The same happens on Claude with "Claude". |
| O14 | `redactKeys` on an OpenAI 401 body: the message has no `sk-` fragment. |
| O15 | Speed. `serviceTier: "fast"` on Sol adds `service_tier`, and on Luna; `"ultrafast"` on Luna adds nothing. A served `fast` reaches `onServedTier`. |
| O16 | Long run. `ttl: "12h"` appears only with `longRun` and `cacheTtlLong: "12h"`. |
| O17 | Effort clamp. A model whose efforts list lacks `max` sends `xhigh`. |
| O18 | The system prompt. `buildSystemPrompt({...})` with `provider` omitted or `"anthropic"` equals G0's system text. `"openai"` differs only by the working-style paragraph and the web wording. |
| O19 | CSP. Every `PROVIDERS[i].origin` is in `ui/index.html`'s `connect-src` (fails until EDGES adds OpenAI, so it runs at integration). |

3. **ChatView.** A Claude message renders the same items as before. A check runs `buildItems` (exported for the check)
   over the mock host's demo thread before and after. An OpenAI message shows its progress notes and web steps in output
   order.
4. **Builds:** `npx tsc -b` and `npm run build` are clean.

### 9.3 EDGES track

**Owns:**

- C#:
  - `src/SwAgent/Infrastructure/SettingsStore.cs`, plus new `ProviderKeys` (same file or `ProviderKeys.cs`) and
    `SecretEnv.cs`;
  - `src/SwAgent/AgentController.cs` (only the `apiKey.*` and `getState` lines);
  - `src/SwAgent/Plugins/Hooks/PluginProcess.cs` (`ChildEnv.Hidden`);
  - `src/SwAgent/Plugins/PluginManager.cs` (`Expand`);
  - `src/SwAgent/Plugins/Marketplaces.cs`;
  - `src/SwAgent/Plugins/CreatorTools.cs` (`check_prerequisites` env);
  - `src/SwAgent/Tools/UserTools.cs` (text);
  - `src/SwAgent/ModelFiles/FileAttachments.cs` (comments);
  - tests under `tests/SwAgent.Tests/` for these.
- UI:
  - `ui/src/store.ts`;
  - `ui/src/lib/models.ts` after C0 (helpers only; the registry data is frozen by C0 unless a verify item changes it);
  - `ui/src/lib/commands.ts`, `ui/src/lib/mockHost.ts`;
  - `ui/src/components/SettingsPanel.tsx`, `Tour.tsx`, `Composer.tsx`, `WebResearchSettings.tsx`;
  - `ui/index.html`;
  - `ui/src/lib/checks/lib.check.mjs` and new `ui/src/lib/checks/models.check.mjs`.
- Content and docs:
  - `README.md`, `HANDOFF.md`;
  - `docs/plugins/authoring.md`, `design.md`, `creator-v2.md`;
  - `plugins/agent-bridge-solidworks/skills/solidworks-bridge/SKILL.md` (and its test fixture copy);
  - `tools/builtin/create-plugin/TOOL.md`.

**Acceptance:**

1. **C# tests** (`dotnet test`, focused while iterating, full at the end):
   - **`SettingsStoreTests` (new)**, all under `SWAGENT_DATA_DIR`:
     - per-provider round trip;
     - an `apikey.bin` written with today's code (bytes made with entropy `SwAgent.ApiKey.v1`) reads as the Anthropic
       key;
     - the OpenAI file uses its own entropy (decrypting it with the Anthropic entropy fails);
     - `SetApiKey(p, "")` deletes only that provider's file;
     - the sources report stored, environment or none (the environment variable is set in the test and restored in
       `finally`);
     - an unknown provider throws;
     - no `.tmp` files are left;
     - the key text never appears in the log file.
   - **RPCs:** `apiKey.get` without `provider` returns the Anthropic key, and `getState` has both `apiKeySource` and
     `apiKeySources`.
   - **Env:** `ChildEnv.Base()` has no `OPENAI_API_KEY` or `OPENAI_ORG_ID` when the process has them; `Expand` turns
     `${OPENAI_API_KEY}` into `""`; a plugin's MCP `env` may set `OPENAI_API_KEY` from `${user_config.k}`
     (`IsBlockedEnv` false); git's environment has neither secret; `check_prerequisites` gets `ChildEnv.Base()`. These
     go next to the existing tests in `HookRunnerTests`, `HookProtocolTests`, `PluginManagerTests` and
     `PluginReviewTests`.
   - **The whole suite passes:** at least 710 passed, 0 failed, 4 skipped.
2. **`models.check.mjs`:**
   - every registry row is valid (provider known, efforts a subset of `EFFORTS`, tier set);
   - `inferProvider` cases;
   - the `resolveModel` table in 7.4, every cell;
   - `tierModel` for both providers;
   - `toolProvider` choices (default model's provider with a key, else the first with a key, else Anthropic);
   - the Composer's grouping helper (one provider: no headers);
   - `lib.check.mjs` passes with its updated alias cases.
3. **Browser** (`npm --prefix ui run dev`, mock host, `?fake`):
   - Options shows both provider rows; saving, testing (faked) and removing work;
   - the Tour offers both providers;
   - with only an OpenAI key, a Claude chat shows the "needs an Anthropic API key" notice;
   - an OpenAI chat runs a faked tool loop;
   - a ribbon tool run on OpenAI uses the tier's OpenAI model;
   - `/model sonnet` on an OpenAI chat picks GPT-6.1 Sol;
   - the narrow panel at 280 px is fine.
4. **Text:** grep `ui/src` and `src/SwAgent` for "Anthropic" and "Claude". What remains is either provider-specific
   (the Claude row, the Claude adapter, model labels) or about the Claude Code plugin format.
5. **Debug build** and `npx tsc -b && npm run build` are clean.

### 9.4 Rules for both tracks

- The project's hard rules apply. In particular:
  - no Release build, no registering, no touching SolidWorks;
  - tests never touch real data or the network;
  - writes go through `AtomicFile`;
  - no key in any log or output;
  - commits end with the co-author trailer;
  - no `--no-verify` and no bare `git stash`;
  - `docs/compression-mold/research/existing-mold-knowledge.md` is never added.
- **G0 goldens are never edited.** A Claude change that alters a golden is a regression unless the user approves it.
- **Match the surrounding style:** comment density, naming, plain short user text.

## 10. How a third provider is added later

1. **Adapter:** `ui/src/agent/providers/<id>/` implementing `Provider` (4.2), and added to `PROVIDERS`.
2. **Models:** rows in `MODELS`, a `<id>` column in `TIERS`, its default model in `DEFAULT_MODELS`, its id prefix in
   `PREFIXES`, and an `UNKNOWN_MODEL` entry (family and conservative caps for ids not in the registry). The Record types
   ask for each of these.
3. **Prompt:** a new kind of web tools (`ModelCaps.web`) needs its wording in `systemPrompt.ts` `WEB` (the Record type
   asks for it); working-style notes, if any, go in `STYLE`.
4. **Keys:** a line in `ProviderKeys.All` and an entry in `SecretEnv.ProviderPrefixes` (C#).
5. **CSP:** its origin in `ui/index.html` (the O19 check fails until it is there).
6. **Tests:** fixtures and scenarios in `providers.check.mjs`.

Settings rows, pickers, the Tour, error texts and alias resolution come from the registry with no further changes. A new
`ErrorKind` is only needed when the provider has a failure the table in 4.4 doesn't cover.

## 11. Hand checks (need the user and real keys; there is no OpenAI key on this PC)

| Id | Check |
|---|---|
| H1 | An OpenAI request from the panel (origin `https://swagent.app`) passes CORS and the CSP. |
| H2 | A Claude chat after the build behaves as before: a reply, a tool loop with a screenshot, web research, a long tool run, a sub-agent, a refusal message. Cache reads show in the footer from the second request. |
| H3 | An OpenAI chat: a reply, a tool loop with `capture_views` (the model describes the image), an attached PDF drawing, web research, a mold-tool run at tier max (Astra high). |
| H4 | Switching mid-chat between Claude and OpenAI both ways, including a chat with Claude web results and thinking. No 400. |
| H5 | The longest silence in an OpenAI stream at `max` effort on Astra (set the console to log gaps over 30 s). This decides `stallMs` (4.5). |
| H6 | `prompt_cache_options.ttl: "12h"` is accepted (6.7). |
| H7 | Foreign `call_id`s (`toolu_...`) are accepted (6.3). |
| H8 | A plugin with many MCP tools, and a long description, is accepted (6.4). |
| H9 | Cached tokens on the second request of an OpenAI chat (the cache works), and across a GPT-6.1 Sol / Astra switch (reasoning carry). |
| H10 | Speed Fast and Ultrafast: accepted, and the footer shows the served tier. A project that forbids a tier shows the `tier` text. |
| H11 | A wrong OpenAI key: the message has no key fragment. An empty-credit account shows the `quota` text without retry delays. |
| H12 | The keys page URL for OpenAI. |

## 12. Not in v1 (decided, can be added later)

- **A client-side `web_fetch` tool** for providers without one: C# fetch, SSRF rules, and only links from the chat or
  search results.
- **OpenAI WebSocket mode:** needs a C# relay that sets the auth header. It is about 40% faster on long tool loops.
- **Server compaction** (`context_management: compaction`, `/responses/compact`) as a fallback near the context limit.
- **`configuration_update`** for effort changes without a cache rewrite. It can't be combined with compaction.
- **Custom base URLs** (Azure OpenAI, OpenAI-compatible servers) and organisation or project headers. Each needs CSP
  entries.
- **Cost in dollars** in the footer (the registry has OpenAI prices; Claude prices need checking first).
- **Strict mode** for individual add-in tools after converting their schemas.
- **Cloud speech** (OpenAI transcription or TTS). Voice stays local.
- **`reasoning.mode: "pro"`**, and async or programmatic tool calling.

## 13. Open items to verify (also in section 11)

- OpenAI CORS from WebView2 (H1).
- Keep-alive or silence during long reasoning (H5).
- `ttl: "12h"` acceptance (H6).
- Foreign `call_id`s (H7).
- Tool-count and description limits (H8).
- Reasoning carry within the `gpt-6` family (H9).
- Speed tiers per model (7.1; Luna now Fast only, from the docs).
- `x-should-retry` support in the pinned `openai` SDK (4.4: read its source after install, no network needed).
- The OpenAI keys page URL (H12).

Each has a fallback above, so none blocks the build.
