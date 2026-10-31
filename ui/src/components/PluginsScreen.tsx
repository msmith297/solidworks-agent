import { useEffect, useMemo, useState, type ReactNode } from "react";
import { ArrowLeft } from "lucide-react";
import { Caret, IconCancel, IconInfo, IconOk, IconPlugin, IconRebuild, IconSearch, IconShield, IconUpdate, IconWarning, Spinner } from "./SwIcons";
import { Markdown } from "./Markdown";
import { ask, confirmAction } from "./Dialog";
import { draftKey, useStore } from "../store";
import { rpc } from "../lib/bridge";
import type { McpServerStatus, PluginActionResult, PluginComponents, PluginInfo, TrustRequest } from "../lib/types";

/**
 * The plugin store (docs/plugins/design.md 5.1), laid out like the Options page: Discover (every marketplace's
 * plugins), Installed (on/off, update, settings, MCP servers), Marketplaces (add, update, remove) and Create (the
 * plugin creator and the user's own plugins). Installing or enabling a plugin that runs programs (hooks, MCP servers)
 * shows exactly what it runs and asks first; only that dialog approves them.
 */
export function PluginsScreen() {
  const tab = useStore((s) => s.pluginsTab);
  const plugins = useStore((s) => s.plugins);
  const { setPluginsTab, setPanel, loadPlugins } = useStore.getState();
  const [detail, setDetail] = useState<string | null>(null);
  const installed = plugins.filter((p) => p.installed).length;

  // Fresh state each time the screen opens (marketplaces may have been updated in the background).
  useEffect(() => {
    void loadPlugins();
  }, [loadPlugins]);

  const shown = detail ? plugins.find((p) => p.id === detail) : undefined;
  return (
    <div className="min-h-0 flex-1 overflow-y-auto bg-bg px-1.5 py-1.5">
      <div className="zoom-content mx-auto flex max-w-[620px] min-w-0 flex-col gap-1.5">
        <div className="flex min-w-0 items-center gap-1.5 px-1 pb-0.5">
          <IconPlugin size={18} />
          <span className="min-w-0 flex-1 truncate text-[13px] font-semibold text-text">Plugins</span>
          <button onClick={() => setPanel("chat")} className="sw-btn shrink-0" title="Back to chat">
            <IconOk size={12} /> Done
          </button>
        </div>
        {shown ? (
          <PluginDetail p={shown} onBack={() => setDetail(null)} />
        ) : (
          <>
            <div className="flex flex-wrap gap-0.5 border-b border-line pb-1" role="tablist">
              {(
                [
                  ["discover", "Discover"],
                  ["installed", `Installed${installed ? ` (${installed})` : ""}`],
                  ["marketplaces", "Marketplaces"],
                  ["create", "Create"],
                ] as [typeof tab, string][]
              ).map(([id, label]) => (
                <button key={id} type="button" role="tab" aria-selected={tab === id} aria-pressed={tab === id} onClick={() => setPluginsTab(id)} className="sw-tool !px-2 text-[11.5px]">
                  {label}
                </button>
              ))}
            </div>
            {tab === "discover" && <Discover onOpen={setDetail} />}
            {tab === "installed" && <Installed onOpen={setDetail} />}
            {tab === "marketplaces" && <Marketplaces />}
            {tab === "create" && <Create />}
          </>
        )}
      </div>
    </div>
  );
}

// ---- actions ----

const label = (p: PluginInfo) => p.displayName || p.name;

/** The marketplace's name to show ("SolidWorks Agent", "Your plugins"); the id stays the value the RPCs take. */
const marketLabel = (p: PluginInfo) => (p.marketplace === "local" ? "Your plugins" : p.marketplaceDisplayName || p.marketplace);

/** Plugins that come along with another one (its marketplace entry's installWith) are shown under it in Discover. */
function companions(plugins: PluginInfo[]): Set<string> {
  return new Set(plugins.flatMap((p) => p.installWith ?? []));
}

/** The programs a plugin will run, for the user to approve; true when they do. */
async function approveTrust(p: PluginInfo, req: TrustRequest, verb: string): Promise<boolean> {
  const by = p.author?.name ? `${p.author.name} (marketplace "${marketLabel(p)}")` : `the "${marketLabel(p)}" marketplace`;
  // Claude MCP / Codex MCP: the change to the other app's config comes first; a plugin with only that runs no code of its own.
  const links = req.links ?? [];
  const linkOnly = links.length > 0 && !req.hooks.length && !req.mcp.length && !(req.tools ?? []).length;
  const app = p.link?.label ?? "the other app";
  const provider = p.link?.client === "codex" ? "Codex's AI provider (OpenAI)" : `${app}'s AI provider`;
  const list = (title: string, note: string, items: string[]) =>
    items.length > 0 && (
      <div>
        <div className="text-[11.5px] font-semibold text-text">{title}</div>
        <div className="text-[11px] text-text-3">{note}</div>
        <div className="mt-1 flex flex-col gap-1">
          {items.map((c, i) => (
            <code key={i} className="selectable block max-h-[72px] overflow-auto whitespace-pre-wrap break-all rounded-[2px] border border-line bg-card-2 px-1.5 py-1 font-mono text-[10.5px] text-text">
              {c}
            </code>
          ))}
        </div>
      </div>
    );
  const choice = await ask({
    title: `${verb} ${label(p)}?`,
    icon: <IconShield size={15} />,
    message: (
      <div className="flex flex-col gap-2">
        {linkOnly ? (
          <p>
            <b className="text-text">{label(p)}</b> lets {app} on this PC send messages to the SolidWorks Agent and read your open model. Changes it asks for still need your OK in the SolidWorks Agent panel.
            Messages and answers go to {provider}.
          </p>
        ) : (
          <p>
            <b className="text-text">{label(p)}</b> runs code on this PC with your Windows account's rights. Only continue if you trust {by}.
          </p>
        )}
        {list(
          "Changes to another app",
          `SolidWorks Agent edits this file now and removes the entry when you turn the plugin off or uninstall it. ${
            p.link?.client === "codex" ? "Nothing else in the file changes." : "Other settings in it keep their values, though the file may be re-indented."
          }`,
          links,
        )}
        {list("Hooks", "Programs that run outside SolidWorks when something happens: you send a message, the agent uses a tool or finishes.", req.hooks)}
        {list("MCP servers", "Programs that run outside SolidWorks while the plugin is on and give the agent extra tools.", req.mcp)}
        {list("Tools with code", "C# that runs inside SolidWorks, with full access to your models, when the tool runs or shows a preview.", req.tools ?? [])}
        <p className="text-[11px] text-text-3">
          {linkOnly ? "If the settings file or the program changes, SolidWorks Agent asks again." : "If an update changes these, SolidWorks Agent asks again before they run."}
        </p>
      </div>
    ),
    actions: [{ value: "ok", label: verb, primary: true }],
  });
  return choice === "ok";
}

/**
 * Install, enable or update a plugin. When the add-in answers that it runs programs the user hasn't approved
 * (needsTrust), the trust dialog shows them, and the same call goes again with the approved hash.
 */
async function pluginAction(p: PluginInfo, action: "install" | "enable" | "update"): Promise<boolean> {
  const method = `plugins.${action}`;
  const params = action === "enable" ? { id: p.id, enabled: true } : { id: p.id };
  let res = await rpc<PluginActionResult | null>(method, params);
  if (res?.needsTrust) {
    const verb = action === "install" ? "Install" : action === "update" ? "Update" : "Turn on";
    if (!(await approveTrust(p, res.needsTrust, verb))) return false;
    res = await rpc<PluginActionResult | null>(method, { ...params, trustHash: res.hash });
  }
  const also = (res?.installed ?? []).map((id) => useStore.getState().plugins.find((x) => x.id === id)).filter(Boolean) as PluginInfo[];
  // Claude MCP / Codex MCP: the next step (restart the other app) right away, not only on the Installed tab.
  let linkNote: string | null = null;
  if (p.link && action !== "update" && !res?.note) {
    await useStore.getState().loadPlugins();
    linkNote = useStore.getState().plugins.find((x) => x.id === p.id && x.installed)?.link?.note ?? null;
  }
  const notes = [res?.note, linkNote, also.length ? `Also installed: ${also.map(label).join(", ")}.` : null].filter(Boolean);
  if (notes.length) useStore.setState({ notice: notes.join(" ") });
  return true;
}

/** Runs an action with a per-plugin busy flag; errors go to the notice bar. */
function useBusy() {
  const [busy, setBusy] = useState<Record<string, boolean>>({});
  const run = async (key: string, fn: () => Promise<unknown>) => {
    setBusy((b) => ({ ...b, [key]: true }));
    try {
      await fn();
    } catch (err: any) {
      useStore.setState({ notice: err?.message ?? String(err) });
    } finally {
      setBusy((b) => ({ ...b, [key]: false }));
      void useStore.getState().loadPlugins();
    }
  };
  return { busy, run };
}

// ---- pieces ----

export function componentsText(c: PluginComponents): string {
  const n = (count: number, one: string, many = `${one}s`) => (count > 0 ? `${count} ${count === 1 ? one : many}` : null);
  return [n(c.tools, "tool"), n(c.commands, "command"), n(c.skills, "skill"), n(c.agents, "agent"), n(c.mcpServers, "MCP server"), c.hooks > 0 ? "hooks" : null].filter(Boolean).join(" · ");
}

function Tags({ p, onTag }: { p: PluginInfo; onTag?: (t: string) => void }) {
  const tags = [p.category, ...(p.keywords ?? [])].filter((t, i, all): t is string => !!t && all.indexOf(t) === i);
  if (!tags.length) return null;
  return (
    <div className="mt-1 flex flex-wrap gap-1">
      {tags.map((t) => (
        <button
          key={t}
          type="button"
          disabled={!onTag}
          onClick={() => onTag?.(t)}
          title={onTag ? `Show plugins tagged "${t}"` : undefined}
          className="h-[18px] max-w-full truncate rounded-[2px] border border-line bg-card-2 px-1 text-[10.5px] text-text-2 enabled:hover:border-accent"
        >
          {t}
        </button>
      ))}
    </div>
  );
}

function Notes({ p }: { p: PluginInfo }) {
  const notes = [...(p.notes ?? []), ...(p.error ? [p.error] : [])];
  if (!notes.length) return null;
  return (
    <div className="mt-1 flex flex-col gap-0.5">
      {notes.map((n, i) => (
        <div key={i} className={`flex items-start gap-1 text-[11px] ${p.error && i === notes.length - 1 ? "text-err" : "text-text-3"}`}>
          <span className="mt-px shrink-0">{p.error && i === notes.length - 1 ? <IconWarning size={11} /> : <IconInfo size={11} />}</span>
          <span className="min-w-0">{n}</span>
        </div>
      ))}
    </div>
  );
}

/** Claude MCP / Codex MCP: the state of the "solidworks-agent" entry in the other app's config, one line. */
function LinkNote({ p }: { p: PluginInfo }) {
  const l = p.link;
  if (!l?.note) return null;
  const bad = !l.found || l.entry === "conflict" || l.entry === "unreadable" || (p.enabled && l.entry === "missing");
  return (
    <div className={`mt-1 flex items-start gap-1 text-[11px] ${bad ? "text-err" : "text-text-3"}`}>
      <span className="mt-px shrink-0">{bad ? <IconWarning size={11} /> : <IconInfo size={11} />}</span>
      <span className="min-w-0 break-words">{l.note}</span>
    </div>
  );
}

function Title({ p, onOpen }: { p: PluginInfo; onOpen?: (id: string) => void }) {
  return (
    <div className="min-w-0">
      <button type="button" className="block max-w-full truncate text-left text-[12.5px] font-semibold text-text hover:underline" onClick={() => onOpen?.(p.id)} title="Details">
        {label(p)}
      </button>
      <div className="truncate text-[10.5px] text-text-3">
        {p.name}
        {p.version ? ` · ${p.version}` : ""} · {p.marketplace === "local" ? "your plugin" : marketLabel(p)}
        {p.author?.name && p.author.name !== marketLabel(p) ? ` · ${p.author.name}` : ""}
      </div>
    </div>
  );
}

// ---- Discover ----

function Discover({ onOpen }: { onOpen: (id: string) => void }) {
  const plugins = useStore((s) => s.plugins);
  const [query, setQuery] = useState("");
  const [tag, setTag] = useState("");
  const [market, setMarket] = useState("");
  const { busy, run } = useBusy();
  const hidden = companions(plugins);
  const tags = useMemo(() => [...new Set(plugins.flatMap((p) => [p.category, ...(p.keywords ?? [])]).filter((t): t is string => !!t))].sort(), [plugins]);
  // id -> the name to show; the id stays the filter value.
  const markets = useMemo(() => [...new Map(plugins.map((p) => [p.marketplace, marketLabel(p)])).entries()], [plugins]);
  const q = query.trim().toLowerCase();
  const list = plugins.filter(
    (p) =>
      !hidden.has(p.id) &&
      (!market || p.marketplace === market) &&
      (!tag || p.category === tag || (p.keywords ?? []).includes(tag)) &&
      (!q || [p.name, p.displayName, p.description, p.category, ...(p.keywords ?? [])].some((x) => x?.toLowerCase().includes(q))),
  );

  return (
    <div className="flex flex-col gap-1.5">
      <div className="flex flex-wrap items-center gap-1">
        <label className="sw-input flex h-[24px] min-w-[150px] flex-1 items-center gap-1 px-1.5">
          <IconSearch size={13} />
          <input value={query} onChange={(e) => setQuery(e.target.value)} placeholder="Search plugins" className="min-w-0 flex-1 bg-transparent text-[12px] outline-none" />
        </label>
        <select value={tag} onChange={(e) => setTag(e.target.value)} className="sw-input h-[24px] min-w-0 max-w-full px-1 text-[11.5px]" title="Filter by tag">
          <option value="">All tags</option>
          {tags.map((t) => (
            <option key={t} value={t}>
              {t}
            </option>
          ))}
        </select>
        <select value={market} onChange={(e) => setMarket(e.target.value)} className="sw-input h-[24px] min-w-0 max-w-full px-1 text-[11.5px]" title="Filter by marketplace">
          <option value="">All marketplaces</option>
          {markets.map(([id, name]) => (
            <option key={id} value={id}>
              {name}
            </option>
          ))}
        </select>
      </div>
      {list.length === 0 && <p className="px-1 py-2 text-[11.5px] text-text-3">{plugins.length ? "No plugin matches." : "No plugins listed. Add a marketplace under Marketplaces."}</p>}
      {list.map((p) => {
        const with_ = (p.installWith ?? []).map((id) => plugins.find((x) => x.id === id)).filter(Boolean) as PluginInfo[];
        return (
          <div key={p.id} className="sw-rollout min-w-0 px-2 py-1.5">
            <div className="flex min-w-0 items-start gap-1.5">
              <span className="mt-0.5 shrink-0">
                <IconPlugin size={16} />
              </span>
              <div className="min-w-0 flex-1">
                <Title p={p} onOpen={onOpen} />
              </div>
              {p.installed ? (
                <span className="flex shrink-0 items-center gap-1 pt-0.5 text-[11px] text-text-3">
                  <IconOk size={11} /> {p.enabled ? "Installed" : "Off"}
                </span>
              ) : (
                <button type="button" className="sw-btn sw-btn-primary shrink-0" disabled={busy[p.id] || !!p.error} onClick={() => run(p.id, () => pluginAction(p, "install"))}>
                  {busy[p.id] && <Spinner size={12} />} Install
                </button>
              )}
            </div>
            {p.description && <p className="mt-1 line-clamp-3 text-[11.5px] text-text-2">{p.description}</p>}
            {componentsText(p.components) && <div className="mt-0.5 text-[10.5px] text-text-3">{componentsText(p.components)}</div>}
            {with_.length > 0 && <div className="mt-0.5 text-[10.5px] text-text-3">Comes with {with_.map(label).join(", ")}</div>}
            <Tags p={p} onTag={setTag} />
            <Notes p={p} />
          </div>
        );
      })}
    </div>
  );
}

// ---- Installed ----

const STATE_COLOR: Record<McpServerStatus["state"], string> = { running: "#2E9E3E", starting: "#F2B11F", restarting: "#F2B11F", error: "#D63A3A", stopped: "#8A9099" };

function McpRows({ servers }: { servers: McpServerStatus[] }) {
  const { busy, run } = useBusy();
  if (!servers.length) return null;
  return (
    <div className="mt-1 flex flex-col gap-0.5">
      {servers.map((m) => {
        const key = `${m.plugin ?? ""}/${m.server}`;
        return (
          <div key={key} className="flex min-w-0 items-start gap-1.5 rounded-[2px] bg-card-2 px-1.5 py-1 text-[11px]">
            <span className="mt-[4px] h-2 w-2 shrink-0 rounded-full" style={{ background: STATE_COLOR[m.state] ?? STATE_COLOR.stopped }} />
            <div className="min-w-0 flex-1">
              <div className="break-words text-text">
                MCP server <b>{m.server}</b> · {m.state}
                {m.state === "running" ? ` · ${m.toolCount} tool${m.toolCount === 1 ? "" : "s"}` : ""}
              </div>
              {m.error && <div className="break-words text-err">{m.error}</div>}
            </div>
            <button
              type="button"
              className="sw-tool !h-5 !min-w-5 shrink-0"
              title="Restart this server"
              disabled={busy[key]}
              onClick={() => run(key, () => rpc("mcp.restart", { plugin: m.plugin, server: m.server }))}
            >
              {busy[key] ? <Spinner size={10} /> : <IconRebuild size={12} />}
            </button>
          </div>
        );
      })}
    </div>
  );
}

/** Installed plugins that came with this one (installWith): they go when it is uninstalled. */
function companionsOf(p: PluginInfo): PluginInfo[] {
  const all = useStore.getState().plugins;
  return (p.installWith ?? []).map((id) => all.find((x) => x.id === id)).filter((x): x is PluginInfo => !!x?.installed && !x.local);
}

function Installed({ onOpen }: { onOpen: (id: string) => void }) {
  const plugins = useStore((s) => s.plugins).filter((p) => p.installed);
  const servers = useStore((s) => s.mcpServers);
  const mcpOn = useStore((s) => s.settings.pluginMcp !== false);
  const hooksOn = useStore((s) => s.settings.pluginHooks !== false);
  const user = servers.filter((m) => !m.plugin);
  // Newer versions, asked when the list opens and after any version changes; null while asking.
  const [updates, setUpdates] = useState<Record<string, string | null> | null>(null);
  const versions = plugins.map((p) => `${p.id}@${p.version}`).join(",");
  useEffect(() => {
    let live = true;
    setUpdates(null);
    rpc<Record<string, string | null>>("plugins.checkUpdates")
      .then((u) => live && setUpdates(u ?? {}))
      .catch(() => live && setUpdates({}));
    return () => {
      live = false;
    };
  }, [versions]);
  return (
    <div className="flex flex-col gap-1.5">
      {(!mcpOn || !hooksOn) && (
        <p className="flex items-start gap-1 px-1 text-[11px] text-text-3">
          <IconInfo size={12} />
          <span>
            {!hooksOn && "Plugin hooks are off. "}
            {!mcpOn && "MCP servers are off. "}
            Turn them on in Options &gt; Plugins.
          </span>
        </p>
      )}
      {plugins.length === 0 && <p className="px-1 py-2 text-[11.5px] text-text-3">No plugins installed yet. Discover lists what you can add - Agent Bridge connects this agent to other AI agents.</p>}
      {plugins.map((p) => (
        <InstalledRow key={p.id} p={p} servers={servers.filter((m) => m.plugin === p.name)} onOpen={onOpen} update={updates === null ? undefined : p.id in updates ? updates[p.id] : "unknown"} />
      ))}
      {user.length > 0 && (
        <section className="sw-rollout min-w-0 px-2 py-1.5">
          <div className="text-[12px] font-semibold text-text">Your MCP servers</div>
          <div className="text-[10.5px] text-text-3">From mcp.json in the add-in's settings folder (%APPDATA%\SwAgent).</div>
          <McpRows servers={user} />
        </section>
      )}
    </div>
  );
}

/**
 * update: the newer version the source offers; null when it is up to date; undefined while checking; "unknown" when it
 * couldn't be checked (Update stays available then).
 */
function InstalledRow({ p, servers, onOpen, update }: { p: PluginInfo; servers: McpServerStatus[]; onOpen: (id: string) => void; update?: string | null }) {
  const available = p.updateAvailable ?? (update && update !== "unknown" ? update : null);
  const { busy, run } = useBusy();
  const [settingsOpen, setSettingsOpen] = useState(false);
  const hasConfig = Object.keys(p.userConfig ?? {}).length > 0;
  return (
    <div className="sw-rollout min-w-0 px-2 py-1.5">
      <div className="flex min-w-0 items-start gap-1.5">
        <input
          type="checkbox"
          checked={p.enabled}
          disabled={busy[p.id]}
          title={p.enabled ? "On - click to turn it off" : "Off - click to turn it on"}
          onChange={(e) =>
            run(p.id, async () => {
              if (e.target.checked) return pluginAction(p, "enable");
              // Turning off a link plugin removes its entry from the other app's settings: say so when that didn't happen.
              const res = await rpc<{ note?: string | null } | null>("plugins.enable", { id: p.id, enabled: false });
              if (res?.note) useStore.setState({ notice: res.note });
            })
          }
          className="mt-1 shrink-0 accent-[var(--accent)]"
        />
        <div className="min-w-0 flex-1">
          <Title p={p} onOpen={onOpen} />
        </div>
        {busy[p.id] && <Spinner size={12} />}
      </div>
      <LinkNote p={p} />
      {componentsText(p.components) && <div className="mt-0.5 text-[10.5px] text-text-3">{componentsText(p.components)}</div>}
      {!p.trusted && (
        <div className="mt-1 flex flex-wrap items-center gap-1 text-[11px] text-text">
          <IconWarning size={12} />
          <span className="min-w-0 flex-1">
            {p.link
              ? `The ${p.link.label} settings file or the program it runs changed since you approved it. Approve it again before SolidWorks Agent changes that file.`
              : "Its hooks or MCP servers changed since you approved them: they don't run until you approve them again."}
          </span>
          <button type="button" className="sw-btn !h-5 !px-1.5 text-[11px]" onClick={() => run(p.id, () => pluginAction(p, "enable"))}>
            Review…
          </button>
        </div>
      )}
      <McpRows servers={servers} />
      <Notes p={p} />
      <div className="mt-1 flex flex-wrap gap-1">
        {available ? (
          <button type="button" className="sw-btn sw-btn-primary !h-[22px]" disabled={busy[p.id]} onClick={() => run(p.id, () => pluginAction(p, "update"))}>
            <IconUpdate size={12} /> Update to {available}
          </button>
        ) : (
          !p.local && (
            <button
              type="button"
              className="sw-btn !h-[22px]"
              title={update === undefined ? "Checking for a newer version" : update === null ? "This is the newest version" : "Couldn't check for updates: fetch the newest version from its source"}
              disabled={busy[p.id] || update !== "unknown"}
              onClick={() => run(p.id, () => pluginAction(p, "update"))}
            >
              <IconUpdate size={12} /> {update === undefined ? "Checking..." : update === null ? "Up to date" : "Update"}
            </button>
          )
        )}
        {hasConfig && (
          <button type="button" className="sw-btn !h-[22px]" aria-expanded={settingsOpen} onClick={() => setSettingsOpen((o) => !o)}>
            Settings <Caret open={settingsOpen} className="text-text-3" />
          </button>
        )}
        {p.link && p.enabled && p.link.found && p.link.entry === "missing" && (
          <button type="button" className="sw-btn sw-btn-primary !h-[22px]" title={`Add the "solidworks-agent" entry to ${p.link.label}'s settings again`} disabled={busy[p.id]} onClick={() => run(p.id, () => rpc("plugins.repairLink", { id: p.id }))}>
            Repair
          </button>
        )}
        {p.link && (
          <button type="button" className="sw-btn !h-[22px]" title={`Read ${p.link.label}'s settings again`} disabled={busy[p.id]} onClick={() => run(p.id, () => useStore.getState().loadPlugins())}>
            Check again
          </button>
        )}
        <button type="button" className="sw-btn !h-[22px]" onClick={() => onOpen(p.id)}>
          Details
        </button>
        {!p.local && (
          <button
            type="button"
            className="sw-btn !h-[22px]"
            disabled={busy[p.id]}
            onClick={() =>
              confirmAction(
                `Uninstall ${label(p)}?`,
                <p>
                  Its files are removed{companionsOf(p).length ? `, with ${companionsOf(p).map(label).join(", ")}` : ""}; its settings and data on this PC are kept for a reinstall.
                  {p.link ? ` It also removes the "solidworks-agent" entry from ${p.link.label}'s settings. Restart ${p.link.label} to drop its SolidWorks Agent tools.` : ""}
                </p>,
                "Uninstall",
                true,
              ).then(
                (ok) => {
                  if (ok)
                    void run(p.id, async () => {
                      const companions = companionsOf(p);
                      const res = await rpc<{ note?: string | null } | null>("plugins.uninstall", { id: p.id });
                      for (const c of companions) await rpc("plugins.uninstall", { id: c.id });
                      if (res?.note) useStore.setState({ notice: res.note });
                      else if (p.link) useStore.setState({ notice: `${label(p)} uninstalled. Restart ${p.link.label} to drop its SolidWorks Agent tools.` });
                    });
                },
              )
            }
          >
            <IconCancel size={9} /> Uninstall
          </button>
        )}
      </div>
      {settingsOpen && hasConfig && <ConfigForm p={p} onDone={() => setSettingsOpen(false)} />}
    </div>
  );
}

/** A plugin's userConfig fields. Sensitive values are never shown: leave the box empty to keep the saved one. */
function ConfigForm({ p, onDone }: { p: PluginInfo; onDone: () => void }) {
  const fields = Object.entries(p.userConfig ?? {});
  const [values, setValues] = useState<Record<string, string | number | boolean>>(() => {
    const v: Record<string, string | number | boolean> = {};
    for (const [k, f] of fields) {
      const cur = p.config?.[k];
      if (f.sensitive) v[k] = "";
      else if (cur !== null && cur !== undefined && typeof cur !== "object") v[k] = cur;
      else if (f.default !== null && f.default !== undefined) v[k] = f.default;
      else v[k] = f.type === "boolean" ? false : "";
    }
    return v;
  });
  const [saving, setSaving] = useState(false);
  const save = async () => {
    setSaving(true);
    try {
      const out: Record<string, string | number | boolean> = {};
      for (const [k, f] of fields) {
        const v = values[k];
        if (f.sensitive && v === "") continue; // keep the saved secret
        out[k] = f.type === "number" ? Number(v) : v;
      }
      await rpc("plugins.setConfig", { id: p.id, values: out });
      onDone();
    } catch (err: any) {
      useStore.setState({ notice: `Couldn't save the settings: ${err.message}` });
    } finally {
      setSaving(false);
    }
  };
  return (
    <div className="mt-1.5 flex flex-col gap-1.5 border-t border-line pt-1.5">
      {fields.map(([k, f]) => {
        const saved = f.sensitive && typeof p.config?.[k] === "object" && (p.config?.[k] as { set?: boolean })?.set;
        return (
          <label key={k} className="flex min-w-0 flex-col gap-0.5">
            <span className="text-[11.5px] text-text-2">
              {f.title || k}
              {f.required ? " *" : ""}
            </span>
            {f.type === "boolean" ? (
              <input type="checkbox" checked={!!values[k]} onChange={(e) => setValues((v) => ({ ...v, [k]: e.target.checked }))} className="accent-[var(--accent)]" />
            ) : (
              <input
                type={f.sensitive ? "password" : f.type === "number" ? "number" : "text"}
                value={String(values[k] ?? "")}
                placeholder={saved ? "Saved - type to replace" : f.type === "directory" || f.type === "file" ? "A path on this PC" : ""}
                autoComplete="off"
                spellCheck={false}
                onChange={(e) => setValues((v) => ({ ...v, [k]: e.target.value }))}
                className="sw-input h-[22px] min-w-0 px-1.5 text-[12px]"
              />
            )}
            {f.description && <span className="text-[10.5px] text-text-3">{f.description}</span>}
          </label>
        );
      })}
      <div className="flex justify-end gap-1">
        <button type="button" className="sw-btn" onClick={onDone}>
          Cancel
        </button>
        <button type="button" className="sw-btn sw-btn-primary" disabled={saving} onClick={save}>
          {saving && <Spinner size={12} />} Save
        </button>
      </div>
      <p className="text-[10.5px] text-text-3">Changes reach the plugin's hooks and MCP servers when they next start (MCP servers restart now).</p>
    </div>
  );
}

// ---- Marketplaces ----

function Marketplaces() {
  const markets = useStore((s) => s.marketplaces);
  const [source, setSource] = useState("");
  const { busy, run } = useBusy();
  const add = () => {
    const src = source.trim();
    if (!src) return;
    void run("+add", async () => {
      await rpc("plugins.addMarketplace", { source: src });
      setSource("");
    });
  };
  return (
    <div className="flex flex-col gap-1.5">
      <form
        className="sw-rollout flex min-w-0 flex-col gap-1 px-2 py-1.5"
        onSubmit={(e) => {
          e.preventDefault();
          add();
        }}
      >
        <div className="text-[12px] font-semibold text-text">Add a marketplace</div>
        <div className="flex min-w-0 flex-wrap gap-1">
          <input
            value={source}
            onChange={(e) => setSource(e.target.value)}
            placeholder="owner/repo, a git or https URL, or a folder"
            spellCheck={false}
            className="sw-input h-[22px] min-w-[140px] flex-1 px-1.5 text-[12px]"
          />
          <button type="submit" className="sw-btn sw-btn-primary" disabled={!source.trim() || busy["+add"]}>
            {busy["+add"] && <Spinner size={12} />} Add
          </button>
        </div>
        <p className="text-[10.5px] text-text-3">A marketplace is a folder or repository with .claude-plugin/marketplace.json - Claude Code marketplaces work as they are.</p>
      </form>
      {markets.map((m) => (
        <div key={m.name} className="sw-rollout flex min-w-0 items-start gap-1.5 px-2 py-1.5">
          <div className="min-w-0 flex-1">
            <div className="truncate text-[12px] font-semibold text-text">{m.displayName || m.name}</div>
            <div className="truncate text-[10.5px] text-text-3" title={m.source}>
              {m.displayName && m.displayName !== m.name ? `${m.name} · ` : ""}
              {m.source}
            </div>
            <div className="text-[10.5px] text-text-3">
              {m.pluginCount} plugin{m.pluginCount === 1 ? "" : "s"}
              {m.updatedAt ? ` · updated ${new Date(m.updatedAt).toLocaleDateString()}` : ""}
              {m.builtIn ? " · built in" : m.autoUpdate ? " · updates automatically" : ""}
            </div>
            {m.error && <div className="break-words text-[11px] text-err">{m.error}</div>}
            {!m.builtIn && (
              <label className="mt-0.5 flex items-center gap-1 text-[11px] text-text-2" title="Update this marketplace's installed plugins at startup, in the background">
                <input type="checkbox" checked={!!m.autoUpdate} disabled={busy[m.name]} onChange={(e) => { const autoUpdate = e.target.checked; void run(m.name, () => rpc("plugins.setMarketplaceAutoUpdate", { name: m.name, autoUpdate })); }} className="accent-[var(--accent)]" />
                Update its plugins automatically
              </label>
            )}
          </div>
          <div className="flex shrink-0 flex-wrap justify-end gap-1">
            {m.name !== "local" && (
              <button type="button" className="sw-tool !h-[22px] !min-w-[22px]" title="Update: fetch the marketplace's plugin list again" disabled={busy[m.name]} onClick={() => run(m.name, () => rpc("plugins.updateMarketplace", { name: m.name }))}>
                {busy[m.name] ? <Spinner size={11} /> : <IconRebuild size={13} />}
              </button>
            )}
            {!m.builtIn && (
              <button
                type="button"
                className="sw-tool !h-[22px] !min-w-[22px]"
                title="Remove the marketplace and uninstall its plugins"
                disabled={busy[m.name]}
                onClick={() => confirmAction(`Remove "${m.name}"?`, <p>Plugins installed from it are uninstalled too. Their data folders stay.</p>, "Remove", true).then((ok) => { if (ok) void run(m.name, () => rpc("plugins.removeMarketplace", { name: m.name })); })}
              >
                <IconCancel size={10} />
              </button>
            )}
          </div>
        </div>
      ))}
    </div>
  );
}

// ---- Create ----

function Create() {
  const local = useStore((s) => s.plugins).filter((p) => p.marketplace === "local");
  const { runNamedTool, send, setPanel } = useStore.getState();
  const prefill = (text: string) => {
    setPanel("chat");
    useStore.setState((s) => ({ focusTick: s.focusTick + 1, drafts: { ...s.drafts, [draftKey(s)]: text } }));
  };
  return (
    <div className="flex flex-col gap-1.5">
      <section className="sw-rollout min-w-0 px-2 py-1.5">
        <div className="text-[12px] font-semibold text-text">Make a plugin with the agent</div>
        <p className="mt-0.5 text-[11.5px] text-text-2">
          The agent asks what it is for, bundles the tools and workflows you made, writes its commands, skills and agents, checks it and turns it on. You can share it as a zip that works as a marketplace.
        </p>
        <button type="button" className="sw-btn sw-btn-primary mt-1.5" onClick={() => void runNamedTool("create-plugin")}>
          <IconPlugin size={13} /> Create a plugin…
        </button>
      </section>
      <div className="px-1 pt-1 text-[11.5px] font-semibold text-text-2">Your plugins</div>
      {local.length === 0 && <p className="px-1 text-[11.5px] text-text-3">None yet.</p>}
      {local.map((p) => (
        <div key={p.id} className="sw-rollout flex min-w-0 flex-wrap items-start gap-1.5 px-2 py-1.5">
          <div className="min-w-[120px] flex-1">
            <Title p={p} />
            {componentsText(p.components) && <div className="text-[10.5px] text-text-3">{componentsText(p.components)}</div>}
          </div>
          <div className="flex shrink-0 gap-1">
            <button type="button" className="sw-btn !h-[22px]" title="Change it with the agent" onClick={() => prefill(`Update my plugin "${p.name}": `)}>
              Edit
            </button>
            <button
              type="button"
              className="sw-btn !h-[22px]"
              title="Zip it to share (with a marketplace.json, so the folder works as a marketplace)"
              onClick={() => {
                setPanel("chat");
                void send(`Package my plugin "${p.name}" with plugin_package, then tell me where the zip is and how others add it.`, { fresh: true });
              }}
            >
              Package
            </button>
          </div>
        </div>
      ))}
    </div>
  );
}

// ---- Details ----

function PluginDetail({ p, onBack }: { p: PluginInfo; onBack: () => void }) {
  const [readme, setReadme] = useState<string | null>(null);
  const { busy, run } = useBusy();
  const servers = useStore((s) => s.mcpServers).filter((m) => m.plugin === p.name);
  useEffect(() => {
    let live = true;
    rpc<{ text: string } | null>("plugins.readme", { id: p.id })
      .then((r) => live && setReadme(r?.text ?? ""))
      .catch(() => live && setReadme(""));
    return () => {
      live = false;
    };
  }, [p.id]);
  const link = (href: string | null | undefined, text: string): ReactNode =>
    href ? (
      <button type="button" className="text-accent hover:underline" onClick={() => rpc("app.openExternal", { url: href })}>
        {text}
      </button>
    ) : null;
  return (
    <div className="flex min-w-0 flex-col gap-1.5">
      <button type="button" className="sw-tool self-start !px-1.5 text-[11.5px]" onClick={onBack}>
        <ArrowLeft size={13} /> Back
      </button>
      <section className="sw-rollout min-w-0 px-2 py-1.5">
        <div className="flex min-w-0 items-start gap-1.5">
          <span className="mt-0.5 shrink-0">
            <IconPlugin size={18} />
          </span>
          <div className="min-w-0 flex-1">
            <Title p={p} />
          </div>
          {!p.installed ? (
            <button type="button" className="sw-btn sw-btn-primary shrink-0" disabled={busy[p.id] || !!p.error} onClick={() => run(p.id, () => pluginAction(p, "install"))}>
              {busy[p.id] && <Spinner size={12} />} Install
            </button>
          ) : !p.enabled ? (
            <button type="button" className="sw-btn shrink-0" disabled={busy[p.id]} onClick={() => run(p.id, () => pluginAction(p, "enable"))}>
              {busy[p.id] && <Spinner size={12} />} Turn on
            </button>
          ) : (
            <span className="flex shrink-0 items-center gap-1 pt-0.5 text-[11px] text-text-3">
              <IconOk size={11} /> On
            </span>
          )}
        </div>
        {p.description && <p className="mt-1 text-[11.5px] text-text-2">{p.description}</p>}
        {componentsText(p.components) && <div className="mt-0.5 text-[10.5px] text-text-3">Contains {componentsText(p.components)}</div>}
        <Tags p={p} />
        <Notes p={p} />
        {p.installed && <LinkNote p={p} />}
        {p.link?.configPath && (
          <div className="mt-1 text-[11px] text-text-3">
            {p.link.label} settings file: <span className="selectable break-all font-mono text-[10.5px] text-text-2">{p.link.configPath}</span>
          </div>
        )}
        <McpRows servers={servers} />
        <div className="mt-1 flex flex-wrap gap-x-3 text-[11px]">
          {link(p.homepage, "Homepage")}
          {link(p.repository, "Source")}
          {p.license && <span className="text-text-3">License: {p.license}</span>}
        </div>
      </section>
      <section className="sw-rollout min-w-0 px-2.5 py-2 text-[12px] text-text">
        {readme === null ? <Spinner size={14} /> : readme.trim() ? <Markdown text={readme} /> : <span className="text-text-3">No README.</span>}
      </section>
    </div>
  );
}
