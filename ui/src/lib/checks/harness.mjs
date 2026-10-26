// Shared set-up for the agent loop checks (agent.check.mjs, providers.check.mjs): runs the panel's TypeScript under
// node, with no network, no real keys and synchronous animation frames. Not a check itself.
import "./resolve.mjs"; // the panel's extensionless imports

// Nothing from this PC's environment may change the requests (base URLs, keys).
for (const k of Object.keys(process.env)) if (k.startsWith("ANTHROPIC_") || k.startsWith("OPENAI_")) delete process.env[k];
// Synchronous frames: every live update is flushed at once, so the last live state before a request ends is complete.
globalThis.requestAnimationFrame = (f) => {
  f(0);
  return 0;
};
globalThis.cancelAnimationFrame = () => {};

export const { runAgent } = await import("../../agent/runAgent.ts");

export const KEY = "test-key-not-real";
export const OPENAI_KEY = "test-openai-key-not-real";
export const FIXTURES = new URL("../../agent/providers/fixtures/", import.meta.url);

/** A check runner: `await t(name, fn)`; `t.count` is the number passed. */
export function runner() {
  const t = async (name, fn) => {
    await fn();
    t.count++;
    console.log("ok -", name);
  };
  t.count = 0;
  return t;
}

/** Callbacks that record, in order, what the loop reports; `live` is the last live state before each clear. */
export function recorder(messages, o = {}) {
  const events = [];
  let live = null;
  let steer = o.steer ?? null;
  const cb = {
    getMessages: () => messages,
    takeSteering: o.steer
      ? () => {
          const s = steer;
          steer = null;
          events.push({ takeSteering: s });
          return s;
        }
      : undefined,
    append: async (m) => {
      messages.push(m);
      events.push({ append: m });
    },
    setLive: (b) => {
      if (b) live = b;
      else if (live) {
        events.push({ live });
        live = null;
      }
    },
    setRunningTool: (id) => events.push({ runningTool: id }),
    addUsage: (u) => events.push({ usage: u }),
    approve: async (req) => {
      events.push({ approve: req });
      return { decision: "run" };
    },
    ask: async (id, question) => {
      events.push({ ask: { id, question } });
      return "Yes";
    },
    askSelection: async (id) => {
      events.push({ askSelection: id });
      return "[S1] Face";
    },
    askSecret: async (id) => {
      events.push({ askSecret: id });
      return { text: "skipped" };
    },
    onRequest: () => events.push({ request: true }),
    onStatus: (s) => events.push({ status: s }),
    runTool: async (name, input, id) => {
      events.push({ runTool: { name, input, id } });
      return (typeof o.toolResult === "function" ? o.toolResult(name, input) : o.toolResult) ?? { content: [{ type: "text", text: "ok" }], isError: false };
    },
    isAutoApproved: () => false,
    setAutoApproved: () => events.push({ setAutoApproved: true }),
    setSubAgent: (p) => events.push({ subAgent: p }),
    hook: o.hook
      ? async (event, call) => {
          events.push({ hook: { event, call } });
          return o.hook(event, call);
        }
      : undefined,
  };
  return { cb, events };
}

// ---- the fixed inputs (a realistic main-agent run; the system prompt is built from these) ----

export const TOOLS = [
  {
    name: "execute_csharp",
    description: "Run a C# cell inside SolidWorks. Each call is one undo step named after its title.",
    input_schema: {
      type: "object",
      properties: { title: { type: "string", description: "Short undo-step title." }, code: { type: "string", description: "The cell." } },
      required: ["title", "code"],
    },
  },
  { name: "get_model_context", description: "The active document, its feature tree and the selection.", input_schema: { type: "object", properties: {} } },
  {
    name: "capture_views",
    description: "Render views of the model.",
    input_schema: { type: "object", properties: { views: { type: "array", items: { type: "string" } }, max_size: { type: "integer" } } },
  },
  { name: "read_user_tool", description: "Read a tool's TOOL.md.", input_schema: { type: "object", properties: { name: { type: "string" } }, required: ["name"] } },
];

export const BASE = {
  keys: { get: async (p) => (p === "anthropic" ? KEY : OPENAI_KEY), has: () => true },
  model: "claude-opus-5-5",
  effort: "high",
  tools: TOOLS,
  approval: "ask",
  realisticAppearance: true,
  verbosity: "medium",
  userTools: [{ name: "flash-grooves", title: "Flash grooves", description: "Cuts flash grooves around the cavities.", kind: "tool", category: "Molding" }],
  skills: [],
  agents: [],
  mcpInstructions: [],
  sessionContext: [],
  knowledge: null,
  webResearch: true,
  memory: null,
};

export const PNG = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";
export const PDF = "JVBERi0xLjQKJcfsj6IKMSAwIG9iago8PD4+CmVuZG9iagp0cmFpbGVyCjw8Pj4KJSVFT0YK";
export const prompt = (text) => ({ role: "user", content: [{ type: "text", text: `${text}\n\n<sw_context>\nActive document: Part1.SLDPRT\n</sw_context>` }] });
