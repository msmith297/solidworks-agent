// Checks for the model registry and its panel helpers (lib/models.ts; docs/providers/design.md 7.1-7.4, 9.3).
// Run from the ui folder:  node src/lib/checks/models.check.mjs   (lib.check.mjs imports it too)
// Not part of the app: nothing imports this file, and tsc / vite ignore .mjs files.
import assert from "node:assert/strict";
import {
  MODELS,
  EFFORTS,
  TIERS,
  DEFAULT_SETTINGS,
  SPEEDS,
  inferProvider,
  modelOption,
  tierModel,
  resolveModel,
  modelFromAlias,
  pickToolProvider,
  keyedDefaultModel,
  DEFAULT_MODELS,
  pickerModels,
  groupModels,
  effortsFor,
  clampEffort,
  findModel,
  speedLabel,
} from "../models.ts";
import { slashRows, modelHint } from "../commands.ts";

let n = 0;
const t = (name, fn) => {
  fn();
  n++;
  console.log("ok -", name);
};

const PROVIDERS = ["anthropic", "openai"];
const EFFORT_IDS = EFFORTS.map((e) => e.id);
const has = (...ps) => (p) => ps.includes(p);

t("every registry row is valid", () => {
  const ids = new Set();
  for (const m of MODELS) {
    assert.ok(!ids.has(m.id), `duplicate ${m.id}`);
    ids.add(m.id);
    assert.ok(PROVIDERS.includes(m.provider), `${m.id}: provider`);
    assert.ok(m.label && m.note && m.family, `${m.id}: label, note, family`);
    assert.ok(Object.keys(TIERS).includes(m.tier), `${m.id}: tier`);
    assert.ok(m.caps.efforts.length > 0 && m.caps.efforts.every((e) => EFFORT_IDS.includes(e)), `${m.id}: efforts`);
    assert.deepEqual(m.caps.efforts, EFFORT_IDS.filter((e) => m.caps.efforts.includes(e)), `${m.id}: efforts in low..max order`);
    assert.ok(m.caps.web === null || m.caps.web === m.provider, `${m.id}: web tools of its own provider`);
    assert.ok(m.caps.cacheReadRatio > 0 && m.caps.cacheReadRatio < 1, `${m.id}: cache ratio`);
    assert.ok(m.caps.maxOutput >= 64000, `${m.id}: max output`);
    assert.ok(m.caps.serviceTiers.every((s) => s === "fast" || s === "ultrafast"), `${m.id}: speed tiers`);
    if (m.provider === "anthropic") assert.deepEqual(m.caps.serviceTiers, [], `${m.id}: no speed tiers on Claude`);
  }
  // The picker order: Claude first, then OpenAI.
  const firstOpenAi = MODELS.findIndex((m) => m.provider === "openai");
  assert.ok(MODELS.slice(firstOpenAi).every((m) => m.provider === "openai"));
  // Claude rows keep today's ids, labels, notes and flags (the Claude adapter's betas depend on them).
  assert.deepEqual(
    MODELS.filter((m) => m.provider === "anthropic").map((m) => [m.id, m.label, m.caps.fallbacks, m.caps.progressUpdates, m.caps.cacheTtlLong]),
    [
      ["claude-opus-5-5", "Opus 5.5", true, true, "1h"],
      ["claude-fable-5-2", "Fable 5.2", true, true, "1h"],
      ["claude-sonnet-5-5", "Sonnet 5.5", true, true, "1h"],
      ["claude-haiku-5-5", "Haiku 5.5", false, false, "1h"],
    ],
  );
  assert.deepEqual(MODELS.filter((m) => m.provider === "openai").map((m) => [m.id, m.tier, m.family, m.caps.serviceTiers.join("+"), m.caps.cacheTtlLong]), [
    // No 12 h cache until hand check H6 shows the API takes it (the SDK lists only "30m"); Luna has Fast mode.
    ["gpt-6.1-sol", "balanced", "gpt-6", "fast+ultrafast", null],
    ["gpt-6-astra", "high", "gpt-6", "fast+ultrafast", null],
    ["gpt-6-luna", "fast", "gpt-6", "fast", null],
  ]);
  assert.equal(modelOption("claude-opus-5-5").caps.cacheReadRatio, 0.1); // the footer still says "billed at 10%"
  assert.equal(DEFAULT_SETTINGS.defaultModel, "claude-opus-5-5");
  assert.equal(DEFAULT_SETTINGS.openaiSpeed, "standard");
  assert.equal(DEFAULT_SETTINGS.settingsVersion, 2);
});

t("inferProvider: the registry, then the id's prefix, else Anthropic", () => {
  assert.equal(inferProvider("claude-opus-5-5"), "anthropic");
  assert.equal(inferProvider("gpt-6-astra"), "openai");
  assert.equal(inferProvider("claude-opus-9"), "anthropic");
  assert.equal(inferProvider("gpt-7-nova"), "openai");
  assert.equal(inferProvider("o5-mini"), "openai");
  assert.equal(inferProvider("chatgpt-6-latest"), "openai");
  assert.equal(inferProvider("mistral-large"), "anthropic");
  assert.equal(inferProvider(""), "anthropic");
});

t("unknown ids get conservative capabilities", () => {
  const c = modelOption("claude-opus-9");
  assert.equal(c.provider, "anthropic");
  assert.equal(c.label, "claude-opus-9");
  assert.equal(c.caps.fallbacks, false);
  assert.equal(c.caps.progressUpdates, false);
  const o = modelOption("gpt-7-nova");
  assert.equal(o.provider, "openai");
  assert.deepEqual(o.caps.efforts, EFFORT_IDS);
  assert.equal(o.caps.web, "openai");
  assert.equal(o.caps.cacheTtlLong, null);
  assert.deepEqual(o.caps.serviceTiers, []);
  assert.equal(o.family, "gpt-7-nova"); // reasoning never carries to or from an unknown model
  assert.equal(c.family, "claude");
  assert.equal(c.caps.web, "anthropic");
  assert.equal(c.caps.cacheTtlLong, "1h");
});

t("tierModel for both providers", () => {
  assert.deepEqual(tierModel("fast"), { model: "claude-haiku-5-5", effort: "low" });
  assert.deepEqual(tierModel("balanced", "anthropic"), { model: "claude-sonnet-5-5", effort: "medium" });
  assert.deepEqual(tierModel("high", "anthropic"), { model: "claude-opus-5-5", effort: "medium" });
  assert.deepEqual(tierModel("max", "anthropic"), { model: "claude-opus-5-5", effort: "high" });
  assert.deepEqual(tierModel(undefined), { model: "claude-sonnet-5-5", effort: "medium" });
  assert.deepEqual(tierModel("fast", "openai"), { model: "gpt-6-luna", effort: "low" });
  assert.deepEqual(tierModel("balanced", "openai"), { model: "gpt-6.1-sol", effort: "medium" });
  assert.deepEqual(tierModel("high", "openai"), { model: "gpt-6-astra", effort: "medium" });
  assert.deepEqual(tierModel("max", "openai"), { model: "gpt-6-astra", effort: "high" }); // the mold tool and Smart Rebuild
  assert.deepEqual(tierModel("bogus", "openai"), { model: "gpt-6.1-sol", effort: "medium" });
  for (const tier of Object.values(TIERS)) for (const p of PROVIDERS) assert.equal(inferProvider(tier[p].model), p);
});

t("resolveModel: every cell of the table in design 7.4", () => {
  const claude = (keys) => ({ chatModel: "claude-opus-5-5", hasKey: keys });
  const openai = (keys) => ({ chatModel: "gpt-6.1-sol", hasKey: keys });
  const both = has("anthropic", "openai");
  for (const ctx of [claude(both), openai(both)]) {
    // empty, inherit: the chat's own
    assert.equal(resolveModel("", ctx), null);
    assert.equal(resolveModel(null, ctx), null);
    assert.equal(resolveModel(undefined, ctx), null);
    assert.equal(resolveModel(" inherit ", ctx), null);
    // anything else
    assert.equal(resolveModel("mistral-large", ctx), null);
    assert.equal(resolveModel("best", ctx), null);
    assert.equal(resolveModel("constructor", ctx), null);
  }
  // Aliases: the Claude family model on Claude, the tier model on OpenAI.
  assert.equal(resolveModel("haiku", claude(both)), "claude-haiku-5-5");
  assert.equal(resolveModel("Sonnet", claude(both)), "claude-sonnet-5-5");
  assert.equal(resolveModel("opus", claude(both)), "claude-opus-5-5");
  assert.equal(resolveModel("fable", claude(both)), "claude-fable-5-2");
  assert.equal(resolveModel("haiku", openai(both)), "gpt-6-luna");
  assert.equal(resolveModel("sonnet", openai(both)), "gpt-6.1-sol");
  assert.equal(resolveModel("opus", openai(both)), "gpt-6-astra");
  assert.equal(resolveModel("fable", openai(both)), "gpt-6-astra"); // max tier
  assert.equal(resolveModel("sonnet", openai(has("openai"))), "gpt-6.1-sol");
  // A registry id or label: that model if its provider has a key, else its tier's model on the chat's provider.
  assert.equal(resolveModel("claude-haiku-5-5", claude(both)), "claude-haiku-5-5");
  assert.equal(resolveModel("claude-haiku-5-5", openai(both)), "claude-haiku-5-5");
  assert.equal(resolveModel("claude-haiku-5-5", openai(has("openai"))), "gpt-6-luna");
  assert.equal(resolveModel("GPT-6 Astra", claude(both)), "gpt-6-astra");
  assert.equal(resolveModel("GPT-6 Astra", openai(both)), "gpt-6-astra");
  assert.equal(resolveModel("gpt-6-astra", claude(has("anthropic"))), "claude-opus-5-5"); // high tier on Claude
  assert.equal(resolveModel("gpt-6-luna", claude(has("anthropic"))), "claude-haiku-5-5");
  assert.equal(resolveModel("Sonnet 5.5", openai(has("openai"))), "gpt-6.1-sol");
  // An unknown id with a known prefix: as written if its provider has a key, else null.
  assert.equal(resolveModel("claude-opus-9", claude(both)), "claude-opus-9");
  assert.equal(resolveModel("claude-opus-9", openai(has("openai"))), null);
  assert.equal(resolveModel("gpt-7-nova", claude(both)), "gpt-7-nova");
  assert.equal(resolveModel("gpt-7-nova", claude(has("anthropic"))), null);
  assert.equal(resolveModel("gpt-5", { chatModel: "claude-opus-5-5", hasKey: (p) => p === "openai" }), "gpt-5");
  assert.equal(resolveModel("gpt-5", { chatModel: "claude-opus-5-5", hasKey: (p) => p === "anthropic" }), null);
  // modelFromAlias keeps its meaning: a Claude chat with every key.
  assert.equal(modelFromAlias("sonnet"), "claude-sonnet-5-5");
  assert.equal(modelFromAlias("inherit"), null);
  // A family name with a version, as before the provider layer: that Claude model on Claude, its tier elsewhere.
  assert.equal(resolveModel("opus-5", claude(both)), "claude-opus-5-5");
  assert.equal(resolveModel("Sonnet-5", claude(has("anthropic"))), "claude-sonnet-5-5");
  assert.equal(resolveModel("fable-5", claude(both)), "claude-fable-5-2");
  assert.equal(modelFromAlias("opus-5"), "claude-opus-5-5");
  assert.equal(resolveModel("opus-5", openai(both)), "gpt-6-astra");
  assert.equal(resolveModel("haiku-5", openai(has("openai"))), "gpt-6-luna");
  assert.equal(resolveModel("opus-9", claude(both)), null);
});

t("keyedDefaultModel: only another provider has a key (an environment key too), so new chats start on its default", () => {
  assert.deepEqual(DEFAULT_MODELS, { anthropic: "claude-opus-5-5", openai: "gpt-6.1-sol" });
  for (const p of PROVIDERS) assert.ok(MODELS.some((m) => m.id === DEFAULT_MODELS[p] && m.provider === p));
  assert.equal(keyedDefaultModel("claude-opus-5-5", has("openai"), PROVIDERS), "gpt-6.1-sol");
  assert.equal(keyedDefaultModel("gpt-6-astra", has("anthropic"), PROVIDERS), "claude-opus-5-5");
  assert.equal(keyedDefaultModel("claude-opus-5-5", has("anthropic", "openai"), PROVIDERS), null);
  assert.equal(keyedDefaultModel("claude-sonnet-5-5", has("anthropic"), PROVIDERS), null);
  assert.equal(keyedDefaultModel("gpt-6.1-sol", has("openai"), PROVIDERS), null);
  assert.equal(keyedDefaultModel("claude-opus-5-5", has(), PROVIDERS), null, "no key at all: the default stays (the no-key notice follows)");
});

t("toolProvider: the default model's provider with a key, else the first with a key, else Anthropic", () => {
  assert.equal(pickToolProvider("claude-opus-5-5", has("anthropic", "openai"), PROVIDERS), "anthropic");
  assert.equal(pickToolProvider("gpt-6.1-sol", has("anthropic", "openai"), PROVIDERS), "openai");
  assert.equal(pickToolProvider("claude-opus-5-5", has("openai"), PROVIDERS), "openai");
  assert.equal(pickToolProvider("gpt-6.1-sol", has("anthropic"), PROVIDERS), "anthropic");
  assert.equal(pickToolProvider("gpt-6.1-sol", has(), PROVIDERS), "anthropic");
  assert.equal(pickToolProvider("claude-opus-5-5", has(), PROVIDERS), "anthropic");
  assert.equal(pickToolProvider("mistral-large", has("openai"), ["openai", "anthropic"]), "openai");
  // The ribbon tool's tier then maps onto that provider.
  assert.equal(tierModel("max", pickToolProvider("claude-opus-5-5", has("openai"), PROVIDERS)).model, "gpt-6-astra");
});

t("model pickers: providers with a key, the chat's model, headers only for several providers", () => {
  const ids = (list) => list.map((m) => m.id);
  const claudeIds = MODELS.filter((m) => m.provider === "anthropic").map((m) => m.id);
  const openaiIds = MODELS.filter((m) => m.provider === "openai").map((m) => m.id);
  // One provider: exactly as before (the Claude models, no headers).
  const one = groupModels(pickerModels("claude-opus-5-5", has("anthropic")), PROVIDERS);
  assert.equal(one.length, 1);
  assert.equal(one[0].header, false);
  assert.deepEqual(ids(one[0].models), claudeIds);
  // No key at all: the current model's provider (the first run looks as before).
  assert.deepEqual(ids(pickerModels("claude-opus-5-5", has())), claudeIds);
  // Both: grouped in provider order with headers.
  const two = groupModels(pickerModels("claude-opus-5-5", has("anthropic", "openai")), PROVIDERS);
  assert.deepEqual(two.map((g) => [g.provider, g.header]), [["anthropic", true], ["openai", true]]);
  assert.deepEqual(ids(two[1].models), openaiIds);
  // Only OpenAI, on a chat still on Opus: the chat's model stays listed (under its own header).
  const keep = groupModels(pickerModels("claude-opus-5-5", has("openai")), PROVIDERS);
  assert.deepEqual(keep.map((g) => [g.provider, ids(g.models).join(",")]), [["anthropic", "claude-opus-5-5"], ["openai", openaiIds.join(",")]]);
  // An unknown id of the chat is listed as itself.
  assert.ok(ids(pickerModels("gpt-7-nova", has("anthropic"))).includes("gpt-7-nova"));
});

t("effort pickers follow the model's offered levels", () => {
  assert.deepEqual(effortsFor("claude-opus-5-5").map((e) => e.id), EFFORT_IDS);
  assert.deepEqual(effortsFor("gpt-6-luna").map((e) => e.id), EFFORT_IDS);
  assert.equal(clampEffort("high", "gpt-6.1-sol"), "high");
  // A model without max: the nearest offered level (ties go down) - checked on a patched row, restored after.
  const row = MODELS.find((m) => m.id === "gpt-6-luna");
  const saved = row.caps.efforts;
  try {
    row.caps.efforts = ["low", "medium", "high", "xhigh"];
    assert.equal(clampEffort("max", "gpt-6-luna"), "xhigh");
    row.caps.efforts = ["low", "high"];
    assert.equal(clampEffort("medium", "gpt-6-luna"), "low");
    assert.equal(clampEffort("max", "gpt-6-luna"), "high");
    assert.deepEqual(effortsFor("gpt-6-luna").map((e) => e.id), ["low", "high"]);
  } finally {
    row.caps.efforts = saved;
  }
});

t("/model finds models by alias, label or id, and its hint lists the models with a key", () => {
  const ctx = (chatModel, ...keys) => ({ chatModel, hasKey: has(...keys) });
  assert.equal(findModel("sonnet", ctx("gpt-6-astra", "openai"))?.id, "gpt-6.1-sol"); // /model sonnet on an OpenAI chat
  assert.equal(findModel("sonnet", ctx("claude-opus-5-5", "anthropic"))?.id, "claude-sonnet-5-5");
  assert.equal(findModel("astra", ctx("claude-opus-5-5", "anthropic", "openai"))?.id, "gpt-6-astra");
  assert.equal(findModel("luna", ctx("claude-opus-5-5", "anthropic", "openai"))?.id, "gpt-6-luna");
  assert.equal(findModel("5.5", ctx("claude-opus-5-5", "anthropic"))?.id, "claude-opus-5-5");
  assert.equal(findModel("", ctx("claude-opus-5-5", "anthropic")), null);
  assert.equal(findModel("zzz", ctx("claude-opus-5-5", "anthropic")), null);
  assert.equal(modelHint(["Opus 5.5", "GPT-6.1 Sol"]), "<Opus 5.5 | GPT-6.1 Sol>");
  const src = { commands: [], skills: [], tools: [] };
  assert.equal(slashRows(src, "model")[0].hint, "<opus | fable | sonnet | haiku>"); // no key known: as before
  assert.equal(slashRows({ ...src, models: ["Opus 5.5", "GPT-6 Luna"] }, "model")[0].hint, "<Opus 5.5 | GPT-6 Luna>");
});

t("OpenAI speed setting", () => {
  assert.deepEqual(SPEEDS.map((s) => s.id), ["standard", "fast", "ultrafast"]);
  assert.equal(speedLabel("fast"), "Fast");
  assert.equal(speedLabel(undefined), "Standard");
});

console.log(`${n} model checks passed`);
