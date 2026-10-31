import type { AgentInfo, CommandInfo, PluginInfo, SkillInfo, UserToolInfo } from "./types";

/**
 * Slash commands and @ tags in the composer (docs/plugins/design.md 4.2, 4.8, 5.2): what the popup lists for what is
 * typed, what a typed "/name args" runs, and the argument substitution of Claude Code command files. The add-in does
 * the real expansion of plugin commands (plugins.expandCommand reads @file includes); substituteArgs here is the same
 * rule, used by the browser mock and the checks. Pure, so it is easy to check (checks/lib.check.mjs).
 */

export interface BuiltinCommand {
  name: string;
  description: string;
  hint?: string;
}

/** SolidWorks Agent's own commands, listed first in the popup. */
export const BUILTIN_COMMANDS: BuiltinCommand[] = [
  { name: "help", description: "List the commands" },
  { name: "clear", description: "Start a new chat" },
  { name: "plugins", description: "Open the plugins screen" },
  { name: "model", description: "Set this chat's model", hint: "<opus | fable | sonnet | haiku>" }, // the hint: modelHint, when known
  { name: "effort", description: "Set this chat's effort", hint: "<low | medium | high | xhigh | max>" },
  { name: "tools", description: "List your tools and workflows" },
  { name: "run", description: "Run a tool or workflow", hint: "<tool> [--profile name]" },
  { name: "new-tool", description: "Make a new tool with the agent" },
  { name: "new-plugin", description: "Make a plugin with the agent (bundle your tools, commands, skills)" },
  { name: "settings", description: "Open Options" },
];

/** What the popup and a typed command can reach. */
export interface CommandSources {
  commands: CommandInfo[];
  skills: SkillInfo[];
  tools: Pick<UserToolInfo, "name" | "title" | "description" | "kind">[];
  plugins?: Pick<PluginInfo, "name" | "displayName" | "description" | "installed" | "enabled">[];
  agents?: Pick<AgentInfo, "id" | "description">[];
  /** The labels of the models /model can pick (those of providers with a key); its hint lists them. */
  models?: string[];
}

/** /model's hint: the models with a key, e.g. "<Opus 5.5 | Sonnet 5.5 | GPT-6.1 Sol>". */
export function modelHint(labels: string[]): string {
  return `<${labels.join(" | ")}>`;
}

export type SlashKind = "builtin" | "command" | "skill" | "tool" | "workflow";

export interface SlashRow {
  kind: SlashKind;
  /** What is inserted after "/": the short name when it is unique, else the plugin-qualified one. */
  name: string;
  description: string;
  /** Where it comes from: "SolidWorks Agent", a plugin name, "Your tools". */
  source: string;
  hint?: string;
}

const short = (id: string) => id.slice(id.indexOf(":") + 1);

/** How many of a list share each short name (a plugin command is "/name" only when no other has that name). */
function shortCounts(ids: string[]): Map<string, number> {
  const n = new Map<string, number>();
  for (const id of ids) n.set(short(id), (n.get(short(id)) ?? 0) + 1);
  return n;
}

/** Every slash row, in the popup's order: built-ins, plugin commands, skills, tools and workflows. */
export function allSlashRows(src: CommandSources): SlashRow[] {
  const builtins = new Set(BUILTIN_COMMANDS.map((b) => b.name));
  const cmdCount = shortCounts(src.commands.map((c) => c.id));
  const skillCount = shortCounts(src.skills.map((s) => s.id));
  const unique = (id: string, counts: Map<string, number>) => !builtins.has(short(id)) && counts.get(short(id)) === 1;
  return [
    ...BUILTIN_COMMANDS.map((b): SlashRow => ({
      kind: "builtin",
      name: b.name,
      description: b.description,
      source: "SolidWorks Agent",
      hint: b.name === "model" && src.models?.length ? modelHint(src.models) : b.hint,
    })),
    ...src.commands.map((c): SlashRow => ({
      kind: "command",
      name: unique(c.id, cmdCount) ? c.name : c.id,
      description: c.description ?? "",
      source: c.plugin,
      hint: c.argumentHint ?? undefined,
    })),
    ...src.skills.map((s): SlashRow => ({
      kind: "skill",
      name: unique(s.id, skillCount) && !cmdCount.has(short(s.id)) ? s.name : s.id,
      description: s.description ?? "",
      source: `${s.plugin} · skill`,
    })),
    ...src.tools.map((t): SlashRow => ({
      kind: t.kind === "tool" ? "tool" : "workflow",
      name: t.name,
      description: t.title && t.title !== t.name ? `${t.title} - ${t.description}` : t.description,
      source: t.kind === "tool" ? "Tool" : "Workflow",
    })),
  ];
}

/** How well a name matches the query: 0 prefix, 1 prefix of the part after "plugin:", 2 inside the name, 3 in the description. */
function score(row: { name: string; description: string }, q: string): number {
  const name = row.name.toLowerCase();
  if (!q || name.startsWith(q)) return 0;
  if (short(name).startsWith(q)) return 1;
  if (name.includes(q)) return 2;
  if (q.length >= 3 && row.description.toLowerCase().includes(q)) return 3;
  return -1;
}

const ORDER: SlashKind[] = ["builtin", "command", "skill", "tool", "workflow"];

/** The popup's rows for what follows "/": name matches first (in the popup's order), then the rest. */
export function slashRows(src: CommandSources, query: string, limit = 50): SlashRow[] {
  const q = query.trim().toLowerCase();
  return allSlashRows(src)
    .map((row, i) => ({ row, i, s: score(row, q) }))
    .filter((r) => r.s >= 0)
    .sort((a, b) => (a.s <= 1 ? 0 : 1) - (b.s <= 1 ? 0 : 1) || ORDER.indexOf(a.row.kind === "workflow" ? "tool" : a.row.kind) - ORDER.indexOf(b.row.kind === "workflow" ? "tool" : b.row.kind) || a.s - b.s || a.i - b.i)
    .slice(0, limit)
    .map((r) => r.row);
}

/** The text after "/" while the caret is still in the first word of a message that starts with "/", else null. */
export function slashQuery(text: string, caret: number): string | null {
  const m = /^\/([^\s/]*)(?=\s|$)/.exec(text); // "/path/to/file" is text, not a command
  if (!m || caret < 1 || caret > m[0].length) return null;
  return m[1];
}

/** A typed "/name args" (the name: letters, digits, - _ . and one plugin ":"), or null for ordinary text. */
export function parseSlash(text: string): { name: string; args: string } | null {
  const m = /^\/([a-z0-9][\w.-]*(?::[a-z0-9][\w.-]*)?)(?:\s+([\s\S]*))?$/i.exec(text.trim());
  return m ? { name: m[1], args: (m[2] ?? "").trim() } : null;
}

export type SlashTarget =
  | { kind: "builtin"; name: string }
  | { kind: "command"; command: CommandInfo }
  | { kind: "skill"; skill: SkillInfo }
  | { kind: "tool"; tool: CommandSources["tools"][number] }
  | { kind: "ambiguous"; options: string[] }
  | { kind: "unknown" };

/**
 * What "/name" runs: a built-in; a plugin command ("plugin:cmd", or "cmd" when only one plugin has it); a skill (the
 * same way); a tool or workflow by its name. Commands win over skills and skills over tools with the same name.
 */
export function resolveSlash(name: string, src: CommandSources): SlashTarget {
  const n = name.toLowerCase();
  if (BUILTIN_COMMANDS.some((b) => b.name === n)) return { kind: "builtin", name: n };
  const byId = <T extends { id: string }>(list: T[]) => list.find((x) => x.id.toLowerCase() === n);
  const byShort = <T extends { id: string }>(list: T[]) => list.filter((x) => short(x.id).toLowerCase() === n);
  const cmd = byId(src.commands);
  if (cmd) return { kind: "command", command: cmd };
  const skill = byId(src.skills);
  if (skill) return { kind: "skill", skill };
  if (!n.includes(":")) {
    const cmds = byShort(src.commands);
    if (cmds.length === 1) return { kind: "command", command: cmds[0] };
    if (cmds.length > 1) return { kind: "ambiguous", options: cmds.map((c) => c.id) };
    const skills = byShort(src.skills);
    if (skills.length === 1) return { kind: "skill", skill: skills[0] };
    if (skills.length > 1) return { kind: "ambiguous", options: skills.map((s) => s.id) };
  }
  const tool = src.tools.find((t) => t.name.toLowerCase() === n);
  return tool ? { kind: "tool", tool } : { kind: "unknown" };
}

/** Command arguments split like a shell: whitespace separates, "double" or 'single' quotes keep spaces. */
export function splitArgs(args: string): string[] {
  const out: string[] = [];
  const re = /"([^"]*)"|'([^']*)'|(\S+)/g;
  for (const m of args.matchAll(re)) out.push(m[1] ?? m[2] ?? m[3]);
  return out;
}

/**
 * A command body with its arguments filled in: $ARGUMENTS is everything after the command, $1..$9 the positional
 * ones (missing ones become empty). Arguments the body never refers to are appended as "ARGUMENTS: ...", so they
 * aren't lost.
 */
export function substituteArgs(body: string, args: string): string {
  const parts = splitArgs(args);
  let used = false;
  const out = body
    .replace(/\$ARGUMENTS\b/g, () => {
      used = true;
      return args;
    })
    .replace(/\$([1-9])(?!\d)/g, (_, d: string) => {
      used = true;
      return parts[Number(d) - 1] ?? "";
    });
  return !used && args ? `${out.trimEnd()}\n\nARGUMENTS: ${args}` : out;
}

/** The @relative/path includes of a command body (a path has a "." or "/"; "@user" mentions are left alone). */
export function fileRefs(body: string): string[] {
  const out: string[] = [];
  for (const m of body.matchAll(/(^|\s)@([\w.\-/\\]+)/g)) {
    const p = m[2].replace(/[.,;:]+$/, "");
    if (/[./\\]/.test(p) && !out.includes(p)) out.push(p);
  }
  return out;
}

// ---- @ tags ----

export type TagKind = "plugin" | "tool" | "skill" | "agent";

export interface TagRow {
  kind: TagKind;
  name: string;
  description: string;
}

/** The "@word" being typed at the caret (after a space or at the start), or null. */
export function atQuery(text: string, caret: number): { start: number; query: string } | null {
  const before = text.slice(0, caret);
  const m = /(^|\s)@([\w:.-]*)$/.exec(before);
  return m ? { start: caret - m[2].length - 1, query: m[2] } : null;
}

/** The @ popup's rows: enabled plugins, then tools, skills and agents. */
export function tagRows(src: CommandSources, query: string, limit = 40): TagRow[] {
  const all: TagRow[] = [
    ...(src.plugins ?? []).filter((p) => p.installed && p.enabled).map((p): TagRow => ({ kind: "plugin", name: p.name, description: p.description ?? p.displayName ?? "" })),
    ...src.tools.map((t): TagRow => ({ kind: "tool", name: t.name, description: t.title || t.description })),
    ...src.skills.map((s): TagRow => ({ kind: "skill", name: s.id, description: s.description ?? "" })),
    ...(src.agents ?? []).map((a): TagRow => ({ kind: "agent", name: a.id, description: a.description ?? "" })),
  ];
  const q = query.trim().toLowerCase();
  return all
    .map((row, i) => ({ row, i, s: score(row, q) }))
    .filter((r) => r.s >= 0)
    .sort((a, b) => a.s - b.s || a.i - b.i)
    .slice(0, limit)
    .map((r) => r.row);
}

/** The lines tags become when the message is sent ("[Use plugin: agent-bridge]"); the system prompt explains them. */
export function tagLines(tags: { kind: TagKind; name: string }[]): string {
  return tags.map((t) => `[Use ${t.kind}: ${t.name}]`).join("\n");
}

/** A command's "[Command: /name args]" head line: the chat shows it as a chip, the body stays folded. */
export function commandHead(name: string, args: string): string {
  return `[Command: /${name}${args ? ` ${args.replace(/\s+/g, " ")}` : ""}]`;
}
