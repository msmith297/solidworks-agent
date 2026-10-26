import type { Effort, ProviderId, Settings } from "./types";
import type { Tier } from "./types";

/** What a model can do (docs/providers/design.md 7.1). The app asks these instead of testing model names. */
export interface ModelCaps {
  /** Offered effort levels, low..max order. */
  efforts: Effort[];
  /** Images in messages and in tool results. */
  vision: boolean;
  /** PDF documents (document blocks / input_file). */
  pdf: boolean;
  /** Whose web research tools the model gets, if any. */
  web: "anthropic" | "openai" | null;
  /** Claude: supports `thinking.display: "updates"` (progress notes between tool calls). */
  progressUpdates: boolean;
  /** Claude: server-side refusal fallback (`fallbacks: "default"`). */
  fallbacks: boolean;
  /** Price of a cached input token against an uncached one (the footer's "billed at"). */
  cacheReadRatio: number;
  /** The longer prompt cache a long tool run asks for. */
  cacheTtlLong: "1h" | "12h" | null;
  /** OpenAI speed tiers (`service_tier`) the model accepts. */
  serviceTiers: ("fast" | "ultrafast")[];
  /** Informational; nothing reads it in v1. */
  contextWindow?: number;
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
  /** USD per 1M tokens (prompts up to 272K for OpenAI); for a later cost display, not shown in v1. */
  price?: { input: number; cachedInput: number; cacheWrite: number; output: number };
}

/** @deprecated The registry row; use ModelInfo. Kept while callers move over. */
export type ModelOption = ModelInfo;

const ALL_EFFORTS: Effort[] = ["low", "medium", "high", "xhigh", "max"];

const claudeCaps = (progressUpdates: boolean, fallbacks: boolean): ModelCaps => ({
  efforts: ALL_EFFORTS,
  vision: true,
  pdf: true,
  web: "anthropic",
  progressUpdates,
  fallbacks,
  cacheReadRatio: 0.1,
  cacheTtlLong: "1h",
  serviceTiers: [],
  maxOutput: 64000,
});

const gptCaps = (cacheReadRatio: number, serviceTiers: ("fast" | "ultrafast")[]): ModelCaps => ({
  efforts: ALL_EFFORTS,
  vision: true,
  pdf: true,
  web: "openai",
  progressUpdates: false,
  fallbacks: false,
  cacheReadRatio,
  // The SDK and the API reference list only "30m" (the default, which every read refreshes). Set "12h" once hand check
  // H6 shows the API takes it (docs/providers/design.md 6.7); client.ts already sends it then.
  cacheTtlLong: null,
  serviceTiers,
  contextWindow: 1_050_000,
  maxOutput: 128000,
});

/** Every model the app offers. The order is the picker order: Claude first, then OpenAI. */
export const MODELS: ModelInfo[] = [
  { id: "claude-opus-5-5", provider: "anthropic", label: "Opus 5.5", note: "Default · best balance for CAD work", family: "claude", tier: "high", caps: claudeCaps(true, true) },
  { id: "claude-fable-5-1", provider: "anthropic", label: "Fable 5.1", note: "Most capable · about 2.5× the cost", family: "claude", tier: "max", caps: claudeCaps(true, true) },
  { id: "claude-sonnet-5-5", provider: "anthropic", label: "Sonnet 5.5", note: "Faster · half the cost", family: "claude", tier: "balanced", caps: claudeCaps(true, true) },
  { id: "claude-haiku-5-5", provider: "anthropic", label: "Haiku 5.5", note: "Fastest · for quick questions", family: "claude", tier: "fast", caps: claudeCaps(false, false) },
  {
    id: "gpt-6.1-sol",
    provider: "openai",
    label: "GPT-6.1 Sol",
    note: "OpenAI default · best balance",
    family: "gpt-6",
    tier: "balanced",
    caps: gptCaps(0.05, ["fast", "ultrafast"]),
    price: { input: 2, cachedInput: 0.1, cacheWrite: 2.5, output: 10 },
  },
  { id: "gpt-6-astra", provider: "openai", label: "GPT-6 Astra", note: "Most capable · about 5× the cost of Sol", family: "gpt-6", tier: "high", caps: gptCaps(0.1, ["fast", "ultrafast"]) },
  { id: "gpt-6-luna", provider: "openai", label: "GPT-6 Luna", note: "Fastest · for quick questions", family: "gpt-6", tier: "fast", caps: gptCaps(0.1, ["fast"]) },
];

export const EFFORTS: { id: Effort; label: string; note: string }[] = [
  { id: "low", label: "Low", note: "Quick answers" },
  { id: "medium", label: "Medium", note: "Routine edits" },
  { id: "high", label: "High", note: "Default · multi-step modelling" },
  { id: "xhigh", label: "Extra high", note: "Hard modelling problems" },
  { id: "max", label: "Max", note: "When correctness matters most" },
];

export const SETTINGS_VERSION = 2;

export const DEFAULT_SETTINGS: Settings = {
  settingsVersion: SETTINGS_VERSION,
  defaultModel: "claude-opus-5-5",
  defaultEffort: "high",
  approval: "ask",
  theme: "solidworks",
  markDocumentDirty: true,
  showProgress: true,
  realisticAppearance: true,
  verbosity: "medium",
  shareView: true,
  muteErrorSounds: true,
  zoom: 1,
  voiceEnabled: false,
  voiceKey: "vk:192",
  voiceModel: "base.en",
  voiceSend: "release",
  voiceViews: true,
  speakReplies: "off",
  speakRate: 0,
  // The add-in reads these from settings.json with the same defaults when a key is missing (plugins, MCP, tool forms).
  pluginAutoUpdate: true,
  pluginHooks: true,
  pluginMcp: true,
  pluginWakesPerHour: 20,
  trustConsoleWakes: true,
  trustAgentWakes: true,
  pluginEventsInChat: true,
  toolInputs: "panel",
  questionTimeoutSec: 60,
  webResearch: true,
  openaiSpeed: "standard",
};

/** Ids whose provider is known from the name alone. */
const PREFIXES: [RegExp, ProviderId][] = [
  [/^claude-/, "anthropic"],
  [/^(gpt-|o\d|chatgpt-)/, "openai"],
];

/** The provider of a model id: the registry, then the id's prefix; anything else is taken as Anthropic. */
export function inferProvider(id: string): ProviderId {
  const known = MODELS.find((m) => m.id === id);
  if (known) return known.provider;
  return PREFIXES.find(([re]) => re.test(id))?.[1] ?? "anthropic";
}

/**
 * What an id the registry doesn't know gets, per provider: its family (reasoning is replayed only within one) and
 * conservative capabilities. A new provider needs its entry here (the Record type asks for it).
 */
const UNKNOWN_MODEL: Record<ProviderId, (id: string) => Pick<ModelInfo, "family" | "caps">> = {
  anthropic: () => ({ family: "claude", caps: claudeCaps(false, false) }),
  openai: (id) => ({ family: id, caps: { ...gptCaps(0.1, []), cacheTtlLong: null, contextWindow: undefined } }),
};

/** A model's registry row; an unknown id gets its inferred provider and conservative capabilities. */
export function modelOption(id: string): ModelInfo {
  const known = MODELS.find((m) => m.id === id);
  if (known) return known;
  const provider = inferProvider(id);
  return { id, provider, label: id, note: "", tier: "balanced", ...UNKNOWN_MODEL[provider](id) };
}

/** The effort a model is sent: the one asked for if it offers it, else the nearest offered one (ties go down). */
export function effortFor(model: ModelInfo, effort: Effort): Effort {
  const offered = model.caps.efforts;
  if (offered.includes(effort) || offered.length === 0) return effort;
  const at = ALL_EFFORTS.indexOf(effort);
  let best = offered[0];
  for (const e of offered) {
    const d = Math.abs(ALL_EFFORTS.indexOf(e) - at);
    const bd = Math.abs(ALL_EFFORTS.indexOf(best) - at);
    if (d < bd || (d === bd && ALL_EFFORTS.indexOf(e) < ALL_EFFORTS.indexOf(best))) best = e;
  }
  return best;
}

export interface TierModel {
  model: string;
  effort: Effort;
}

/**
 * Intelligence tiers for tools, per provider. A tool stores only the tier, so it keeps working when the user
 * switches provider.
 */
export const TIERS: Record<Tier, { label: string; note: string } & Record<ProviderId, TierModel>> = {
  fast: { label: "Fast", note: "Quickest - simple, mechanical jobs", anthropic: { model: "claude-haiku-5-5", effort: "low" }, openai: { model: "gpt-6-luna", effort: "low" } },
  balanced: { label: "Balanced", note: "Default - most tools", anthropic: { model: "claude-sonnet-5-5", effort: "medium" }, openai: { model: "gpt-6.1-sol", effort: "medium" } },
  high: { label: "High", note: "Careful geometry work", anthropic: { model: "claude-opus-5-5", effort: "medium" }, openai: { model: "gpt-6-astra", effort: "medium" } },
  max: { label: "Max", note: "Hardest geometry reasoning", anthropic: { model: "claude-opus-5-5", effort: "high" }, openai: { model: "gpt-6-astra", effort: "high" } },
};

export function tierModel(tier: Tier | undefined, provider: ProviderId = "anthropic"): TierModel {
  return (TIERS[tier ?? "balanced"] ?? TIERS.balanced)[provider];
}

/** Claude Code's model aliases, and the tier each stands for on another provider. A Map: "constructor" is no alias. */
const ALIAS_TIERS = new Map<string, Tier>([
  ["haiku", "fast"],
  ["sonnet", "balanced"],
  ["opus", "high"],
  ["fable", "max"],
]);

export interface ResolveContext {
  /** The model of the chat (or run) the name is resolved for: its provider is the one aliases map to. */
  chatModel: string;
  hasKey(p: ProviderId): boolean;
}

/**
 * A command's, agent's or workflow's `model` (a Claude Code alias, a model id or label) as a model id for a chat on
 * `ctx.chatModel`; null means the chat's own model. Aliases resolve inside the chat's provider; a model whose provider
 * has no key falls back to its tier on the chat's provider (docs/providers/design.md 7.4).
 */
export function resolveModel(name: string | null | undefined, ctx: ResolveContext): string | null {
  const a = (name ?? "").trim().toLowerCase();
  if (!a || a === "inherit") return null;
  const chatProvider = inferProvider(ctx.chatModel);
  const aliasTier = ALIAS_TIERS.get(a);
  if (aliasTier) {
    if (chatProvider === "anthropic") return MODELS.find((m) => m.id.startsWith(`claude-${a}-`))?.id ?? null;
    return tierModel(aliasTier, chatProvider).model;
  }
  // A family name with a version ("opus-5", "sonnet-5"): that Claude model on Claude, its tier elsewhere.
  const byFamily = MODELS.find((m) => m.provider === "anthropic" && m.id.startsWith(`claude-${a}-`));
  if (byFamily) return chatProvider === "anthropic" ? byFamily.id : tierModel(byFamily.tier, chatProvider).model;
  const known = MODELS.find((m) => m.id === a || m.label.toLowerCase() === a);
  if (known) return ctx.hasKey(known.provider) ? known.id : tierModel(known.tier, chatProvider).model;
  const prefixed = PREFIXES.find(([re]) => re.test(a));
  if (prefixed) return ctx.hasKey(prefixed[1]) ? a : null;
  return null;
}

/** @deprecated Use resolveModel. A model name on a Claude chat with every key present (today's meaning). */
export function modelFromAlias(alias: string | null | undefined): string | null {
  return resolveModel(alias, { chatModel: "claude-opus-5-5", hasKey: () => true });
}

// ---- helpers for the panel: store, pickers, Options ----

/**
 * The provider ribbon tool runs use: the default model's when it has a key, else the first provider (in the adapters'
 * order) that has one, else Anthropic (and the no-key notice follows).
 */
export function pickToolProvider(defaultModel: string, hasKey: (p: ProviderId) => boolean, order: ProviderId[]): ProviderId {
  const own = inferProvider(defaultModel);
  if (hasKey(own)) return own;
  return order.find(hasKey) ?? "anthropic";
}

/** Each provider's default model: what new chats start on when only that provider has a key. */
export const DEFAULT_MODELS: Record<ProviderId, string> = { anthropic: "claude-opus-5-5", openai: "gpt-6.1-sol" };

/**
 * The default model to switch to when the current default's provider has no key but another provider has one (an
 * OpenAI key only, saved or from the environment): that provider's default. Null keeps the current default.
 */
export function keyedDefaultModel(defaultModel: string, hasKey: (p: ProviderId) => boolean, order: ProviderId[]): string | null {
  if (hasKey(inferProvider(defaultModel))) return null;
  const p = order.find(hasKey);
  return p ? DEFAULT_MODELS[p] : null;
}

/** The efforts a model offers, as picker rows. */
export function effortsFor(model: string) {
  const offered = modelOption(model).caps.efforts;
  return EFFORTS.filter((e) => offered.includes(e.id));
}

/** The nearest effort a model offers (the list runs low to max; a tie goes down). */
export function clampEffort(effort: Effort, model: string): Effort {
  return effortFor(modelOption(model), effort);
}

/**
 * What a model picker lists: the models of providers that have a key, plus the current model (whatever it is). With no
 * key at all, the current model's provider (the first run looks as before).
 */
export function pickerModels(current: string, hasKey: (p: ProviderId) => boolean): ModelInfo[] {
  const keyed = [...new Set(MODELS.map((m) => m.provider))].filter(hasKey);
  const shown = keyed.length ? keyed : [inferProvider(current)];
  const list = MODELS.filter((m) => shown.includes(m.provider) || m.id === current);
  if (current && !list.some((m) => m.id === current)) list.push(modelOption(current));
  return list;
}

/** The picker's groups, in provider order; `header` is false when only one provider is listed (it looks as before). */
export function groupModels(models: ModelInfo[], order: ProviderId[]): { provider: ProviderId; header: boolean; models: ModelInfo[] }[] {
  const providers = order.filter((p) => models.some((m) => m.provider === p));
  for (const m of models) if (!providers.includes(m.provider)) providers.push(m.provider);
  const header = providers.length > 1;
  return providers.map((provider) => ({ provider, header, models: models.filter((m) => m.provider === provider) }));
}

/** OpenAI's speed setting (service_tier): Standard sends nothing. */
export const SPEEDS: { id: NonNullable<Settings["openaiSpeed"]>; label: string; note: string }[] = [
  { id: "standard", label: "Standard", note: "" },
  { id: "fast", label: "Fast", note: "about 2× the cost" },
  { id: "ultrafast", label: "Ultrafast", note: "about 6× the cost" },
];

export function speedLabel(speed: Settings["openaiSpeed"]): string {
  return SPEEDS.find((s) => s.id === speed)?.label ?? "Standard";
}

/** /model: a name per resolveModel, else a label or id containing it. Only registry models. */
export function findModel(query: string, ctx: ResolveContext): ModelInfo | null {
  const a = query.trim().toLowerCase();
  if (!a) return null;
  const id = resolveModel(a, ctx) ?? MODELS.find((x) => x.label.toLowerCase().includes(a) || x.id.includes(a))?.id;
  return MODELS.find((m) => m.id === id) ?? null;
}
