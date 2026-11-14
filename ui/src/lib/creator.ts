import type { PluginInfo } from "./types";

/**
 * The plugin creator's panel side (docs/plugins/creator-v2.md): the web research server tools and the ask_secret card's
 * rules. Pure, so lib/checks can test it under node.
 */

/** What the model gets back from ask_secret: never the value. */
export const SECRET_SAVED = "saved";
export const SECRET_SKIPPED = "skipped";

/** An ask_secret request (the tool's input). */
export interface SecretAsk {
  plugin: string;
  key: string;
  label: string;
  help_url?: string;
}

/**
 * Anthropic's web search and web fetch, run on Anthropic's side (the panel's CSP doesn't change): the dynamic-filtering
 * versions on Opus, Sonnet and Fable, the basic ones on the other models. Off: none.
 */
const DYNAMIC_WEB = /^claude-(opus|sonnet|fable|mythos)-/;

export function webTools(model: string, enabled: boolean): { type: string; name: string; max_uses?: number }[] {
  if (!enabled) return [];
  const dynamic = DYNAMIC_WEB.test(model);
  return [
    { type: dynamic ? "web_search_20260209" : "web_search_20250305", name: "web_search", max_uses: 8 },
    { type: dynamic ? "web_fetch_20260209" : "web_fetch_20250910", name: "web_fetch", max_uses: 10 },
  ];
}

/** The beta header the basic web fetch needs (the 2026 versions need none). */
export function webBetas(model: string, enabled: boolean): string[] {
  return enabled && !DYNAMIC_WEB.test(model) ? ["web-fetch-2025-09-10"] : [];
}

/**
 * Which plugin option an ask_secret card stores into: the user's own or an installed plugin (by name or id) that declares
 * `key` in userConfig as sensitive. Anything else is an error for the agent, before a card is shown.
 */
export function secretTarget(plugins: PluginInfo[], ask: Partial<SecretAsk>): { id: string; title: string; set: boolean } | { error: string } {
  const name = String(ask.plugin ?? "").trim();
  const key = String(ask.key ?? "").trim();
  const p = plugins.find((x) => x.installed && (x.id === name || x.name === name));
  if (!p) return { error: `No installed plugin '${name}'. Create it first (plugin_create), then declare the key in its userConfig.` };
  const field = p.userConfig?.[key];
  if (!field)
    return { error: `'${p.name}' has no option '${key}'. Add it to userConfig in .claude-plugin/plugin.json first: "${key}": { "type": "string", "title": "...", "sensitive": true }.` };
  if (!field.sensitive) return { error: `'${p.name}' option '${key}' isn't marked "sensitive": true. Mark it, so the value is stored encrypted and never shown.` };
  const value = p.config?.[key];
  return { id: p.id, title: field.title || key, set: typeof value === "object" && value !== null && value.set === true };
}

/** A help link the card may show: http(s) only. */
export function safeHelpUrl(url: string | undefined): string | null {
  const u = String(url ?? "").trim();
  return /^https?:\/\/[^\s]+$/i.test(u) ? u : null;
}

/** The chat card's title for a web step. */
export function webTitle(name: string, input: any): string | null {
  if (name === "web_search") return `Search the web${input?.query ? `: ${input.query}` : ""}`;
  if (name === "web_fetch") return `Read ${input?.url ?? "a web page"}`;
  return null;
}

const FETCH_ERRORS: Record<string, string> = {
  url_not_accessible: "the page can't be opened (it may need a login, like X posts)",
  url_not_allowed: "that site isn't allowed",
  url_not_in_prior_context: "only links from the chat or search results can be opened",
  unsupported_content_type: "that kind of file can't be read",
  content_too_large: "the page is too large",
  too_many_requests: "the site refused, too many requests",
  max_uses_exceeded: "the limit of pages per step was reached",
};

/**
 * A web_search_tool_result / web_fetch_tool_result block, in short for the chat card: the result titles and links, the
 * page that was read, or what went wrong (server tools report errors as content, not as failed requests).
 */
export function webResultSummary(block: any): { text: string; isError: boolean } | null {
  const c = block?.content;
  if (block?.type === "web_search_tool_result") {
    if (!Array.isArray(c)) return { text: `The search failed (${c?.error_code ?? "error"}).`, isError: true };
    if (c.length === 0) return { text: "No results.", isError: false };
    return { text: c.slice(0, 8).map((r: any) => `${r.title ?? "(untitled)"} - ${r.url}`).join("\n"), isError: false };
  }
  if (block?.type === "web_fetch_tool_result") {
    if (!c || c.type === "web_fetch_tool_result_error" || c.error_code)
      return { text: `Couldn't read it: ${FETCH_ERRORS[c?.error_code] ?? c?.error_code ?? "error"}.`, isError: true };
    const title = c.content?.title;
    return { text: `${title ? `${title} - ` : ""}${c.url ?? ""}`.trim() || "Read.", isError: false };
  }
  return null;
}
