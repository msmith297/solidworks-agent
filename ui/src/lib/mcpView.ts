/**
 * How MCP tools and agent-bridge messages read in the chat: plain names instead of `mcp__server__tool`, and a wake as
 * "Martin Smith: create a 100mm cube" instead of agent-bridge's instructions to the model. Pure, so the panel checks
 * can run it (checks/lib.check.mjs).
 */

/** `mcp__<server>__<tool>` split; null for other tools. Plugin servers are `plugin_<plugin>_<server>`. */
export function mcpParts(name: string): { server: string; tool: string } | null {
  const m = /^mcp__(.+?)__(.+)$/.exec(name);
  if (!m) return null;
  const p = /^plugin_(.+)_(.+)$/.exec(m[1]);
  return { server: p ? (p[1] === p[2] ? p[1] : `${p[1]}/${p[2]}`) : m[1], tool: m[2] };
}

/** An agent-bridge tool: housekeeping the chat folds away (messages count in the step summary). */
export function isBridgeTool(name: string): boolean {
  return mcpParts(name)?.server === "agent-bridge";
}

const words = (s: string) => s.replace(/[_-]+/g, " ").trim();
const cap = (s: string) => s.charAt(0).toUpperCase() + s.slice(1);

/** A readable title for an MCP tool call: agent-bridge's own tools by what they do, others as "Tool (server)". */
export function mcpTitle(name: string, input: any): string | null {
  const parts = mcpParts(name);
  if (!parts) return null;
  if (parts.server === "agent-bridge") {
    const to = typeof input?.to === "string" && input.to.trim() ? input.to.trim() : null;
    const chat = input?.chat != null ? `chat #${input.chat}` : null;
    switch (parts.tool) {
      case "send_message":
        return `${input?.reply_to != null ? "Reply to" : "Message"} ${to === "*" ? "everyone" : (to ?? chat ?? "agent-bridge")}`;
      case "check_inbox":
        return "Check messages";
      case "wait_for_message":
        return "Wait for a reply";
      case "list_peers":
        return "See who is online";
      case "listen":
        return input?.minutes === 0 ? "Stop listening" : input?.always || input?.minutes ? "Listen for messages" : "Check listening";
      case "set_profile":
        return input?.name ? `Rename to ${input.name}` : "Update bridge status";
      case "connect":
        return "Connect to agent-bridge";
      default:
        return `${cap(words(parts.tool))}${input?.action ? `: ${words(String(input.action))}` : ""} (agent-bridge)`;
    }
  }
  return `${cap(words(parts.tool))} (${parts.server})`;
}

/** One message in an agent-bridge wake: who sent it and what it says (the first lines). */
export interface BridgeMessage {
  from: string;
  console: boolean;
  body: string;
}

const HEADER = /^#\d+ from (.+?)(?: \[([^\]]+)\])?(?: in chat #\d+.*?)? \([^)\n]*\)(?: \[reply to #\d+\])?:$/;

/**
 * The messages in agent-bridge's wake text ("New agent-bridge messages (...):" then "#id from X [machine] (...):"
 * headers and bodies split by "---", then instructions for the model); [] when it isn't that format.
 */
export function bridgeMessages(text: string): BridgeMessage[] {
  const t = text.replace(/^agent-bridge:\s*/, "");
  if (!t.startsWith("New agent-bridge messages")) return [];
  const start = t.indexOf("\n\n#");
  if (start < 0) return [];
  const chunks = t.slice(start + 2).split("\n\n---\n\n");
  const out: BridgeMessage[] = [];
  chunks.forEach((c, i) => {
    const [head, ...rest] = c.split("\n");
    const m = HEADER.exec(head);
    if (!m) return;
    let body = rest.join("\n");
    // The last message is followed by agent-bridge's instructions to the model ("\n\nHandle them, ...").
    if (i === chunks.length - 1) body = body.replace(/\n\n(Handle them|You are still|You are not in listening)[\s\S]*$/, "");
    out.push({ from: m[1], console: m[2] === "Console", body: body.trim() });
  });
  return out;
}

/** The people an agent-bridge Stop reminder is about ("- #3876 from Martin Smith: ..."), for a one-line note. */
export function reminderSenders(text: string): string[] {
  if (!/^agent-bridge: before you finish/.test(text)) return [];
  return [...new Set([...text.matchAll(/^- #\d+ from (.+?)(?: in chat #\d+[^:]*)?: "/gm)].map((m) => m[1]))];
}
