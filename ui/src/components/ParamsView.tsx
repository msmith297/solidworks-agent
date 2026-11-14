import { useEffect, useMemo, useState } from "react";
import { useStore } from "../store";
import { on, rpc } from "../lib/bridge";
import { mergeVParamEdit, vparamKey, visibleRows, type VParamEdit, type VParamGroup, type VParamRow, type VParamTable } from "../lib/vparams";
import { IconAssembly, IconFx, IconOk, IconPart, Spinner } from "./SwIcons";

/**
 * The virtual parameter table (docs/virtual-parameters.md): the document's parameters and, in an assembly, each part's,
 * from the ribbon's Parameters button, the P key (filtered to the selection) or the agent's show_vparams. Edits wait here
 * until Apply, which sends them to the agent as one message; the agent changes the model and records the new values.
 */
export function ParamsView() {
  const view = useStore((s) => s.paramsView);
  const { setPanel, applyVParamEdits, send } = useStore.getState();
  const [table, setTable] = useState<VParamTable | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [edits, setEdits] = useState<VParamEdit[]>([]);
  const [onlySelection, setOnlySelection] = useState(true);

  useEffect(() => {
    let live = true;
    const load = () =>
      rpc<VParamTable>("vparams.table", { document: view?.document ?? null, selection: !!view?.selection })
        .then((t) => live && (setTable(t), setError(null)))
        .catch((e) => live && setError(e instanceof Error ? e.message : String(e)));
    setOnlySelection(true);
    void load();
    const offChanged = on("vparams.changed", () => void load());
    const offDoc = on("doc.activated", () => void load());
    return () => {
      live = false;
      offChanged();
      offDoc();
    };
  }, [view?.tick, view?.document, view?.selection]);

  // Edits of values the agent has since recorded drop out on their own.
  useEffect(() => {
    if (!table) return;
    setEdits((list) => list.filter((e) => table.groups.some((g) => g.document === e.document && g.params.some((p) => p.name === e.name && p.value === e.from))));
  }, [table]);

  const highlight = useMemo(() => new Set((view?.highlight ?? []).map((h) => h.toLowerCase())), [view?.highlight]);
  const filtered = !!table?.filtered;
  const anyMatch = !!table?.groups.some((g) => g.params.some((p) => p.match));
  const showOnlySelection = filtered && onlySelection && anyMatch;
  const groups = (table?.groups ?? []).filter((g) => !showOnlySelection || g.params.some((p) => p.match) || (g.selected && g.params.length === 0));
  const edited = new Map(edits.map((e) => [vparamKey(e.document, e.name), e]));
  const nothing = !!table && table.groups.every((g) => g.params.length === 0);

  return (
    <div className="flex min-h-0 flex-1 flex-col bg-bg">
      <div className="min-h-0 flex-1 overflow-y-auto px-1.5 py-1.5">
        <div className="zoom-content mx-auto flex max-w-[520px] flex-col gap-1.5">
          <div className="flex items-center gap-1.5 px-1 pb-0.5">
            <IconFx size={18} />
            <span className="min-w-0 flex-1 truncate text-[13px] font-semibold text-text" title={table?.document ?? undefined}>
              Parameters{table?.document ? ` · ${table.document}` : ""}
            </span>
            <button onClick={() => setPanel("chat")} className="sw-btn" title="Back to chat">
              <IconOk size={12} /> Done
            </button>
          </div>

          {error && <p className="px-1 text-[11.5px] text-err">{error}</p>}
          {!table && !error && (
            <p className="flex items-center gap-1.5 px-1 py-1 text-[11.5px] text-text-3">
              <Spinner size={12} /> Loading…
            </p>
          )}
          {table && !table.document && <p className="px-1 py-1 text-[12px] text-text-2">Open a part or assembly to see its parameters.</p>}

          {filtered && table?.document && (
            <div className="flex items-center gap-1.5 rounded-[3px] border border-line bg-accent-soft px-2 py-1 text-[11.5px] text-text">
              <span className="min-w-0 flex-1">
                {anyMatch ? `The ones that drive the selection (${table.selection ?? "selection"})` : `None of these drive the selection (${table.selection ?? "selection"})`}
              </span>
              {anyMatch && (
                <button type="button" className="sw-btn !h-[20px] shrink-0 !px-1.5" onClick={() => setOnlySelection(!onlySelection)}>
                  {onlySelection ? "Show all" : "Only the selection"}
                </button>
              )}
            </div>
          )}

          {table?.document && nothing && (
            <div className="rounded-[3px] border border-line px-2 py-2 text-[12px] text-text-2">
              No parameters yet. The agent adds them as it models; you can also ask for them.
              <div className="mt-1.5">
                <button
                  type="button"
                  className="sw-btn"
                  onClick={() => {
                    setPanel("chat");
                    void send(`Add virtual parameters to ${table.document}: the values I'd want to change.`);
                  }}
                >
                  Ask the agent to add them
                </button>
              </div>
            </div>
          )}

          {!nothing &&
            groups.map((g) => (
              <Group
                key={`${g.scope}|${g.document}`}
                group={g}
                rows={showOnlySelection ? visibleRows(g, true) : g.params}
                highlight={highlight}
                edited={edited}
                onEdit={(row, to) => setEdits((list) => mergeVParamEdit(list, { document: g.document, name: row.name, from: row.value, to, unit: row.unit }))}
              />
            ))}
        </div>
      </div>
      {edits.length > 0 && (
        <div className="zoom-chrome flex shrink-0 items-center gap-1.5 border-t border-line bg-panel px-2 py-1.5 text-[12px]">
          <span className="min-w-0 flex-1 text-text-2">
            {edits.length} change{edits.length === 1 ? "" : "s"}: the agent updates the model when you apply.
          </span>
          <button type="button" className="sw-btn" onClick={() => setEdits([])}>
            Discard
          </button>
          <button
            type="button"
            className="sw-btn sw-btn-primary"
            onClick={() => {
              const list = edits;
              setEdits([]);
              void applyVParamEdits(list);
            }}
          >
            Apply
          </button>
        </div>
      )}
    </div>
  );
}

function Group({
  group,
  rows,
  highlight,
  edited,
  onEdit,
}: {
  group: VParamGroup;
  rows: VParamRow[];
  highlight: Set<string>;
  edited: Map<string, VParamEdit>;
  onEdit: (row: VParamRow, to: string) => void;
}) {
  const assembly = group.scope === "assembly";
  return (
    <div className="rounded-[3px] border border-line bg-card">
      <div className="flex items-center gap-1.5 border-b border-line px-2 py-1 text-[11.5px]">
        {assembly ? <IconAssembly size={14} /> : <IconPart size={14} />}
        <span className="min-w-0 flex-1 truncate font-semibold text-text" title={group.title}>
          {assembly ? "Assembly-wide" : group.title}
        </span>
        {group.components.length > 0 && (
          <span className="max-w-[45%] truncate text-[10.5px] text-text-3" title={group.components.join(", ")}>
            {group.components.join(", ")}
          </span>
        )}
      </div>
      {rows.length === 0 && <p className="px-2 py-1.5 text-[11.5px] text-text-3">{group.selected ? "Selected, but it has no parameters yet." : "None yet."}</p>}
      {rows.map((p) => (
        <Row
          key={p.name}
          row={p}
          lit={highlight.has(p.name.toLowerCase()) || !!p.match}
          edit={edited.get(vparamKey(group.document, p.name))}
          onEdit={(to) => onEdit(p, to)}
        />
      ))}
    </div>
  );
}

function Row({ row, lit, edit, onEdit }: { row: VParamRow; lit: boolean; edit?: VParamEdit; onEdit: (to: string) => void }) {
  const value = edit ? edit.to : row.value;
  const [draft, setDraft] = useState(value);
  useEffect(() => setDraft(value), [value]);
  const commit = (v: string) => {
    if (v.trim() === "") return setDraft(value);
    if (v !== value) onEdit(v);
  };
  const numeric = row.type === "length" || row.type === "angle" || row.type === "count" || row.type === "number";
  const tip = [row.note, row.drives.length ? `Drives: ${row.drives.join(", ")}` : "", row.links.length ? `Sets: ${row.links.map((l) => `${l.part} / ${l.param}`).join(", ")}` : ""]
    .filter(Boolean)
    .join("\n");
  return (
    <div className={`flex items-center gap-2 border-b border-line px-2 py-1 last:border-b-0 ${lit ? "bg-accent-soft" : ""}`} title={tip || undefined}>
      <div className="min-w-0 flex-1">
        <div className="truncate text-[12px] font-semibold text-text">{row.name}</div>
        {row.note && <div className="truncate text-[10.5px] text-text-3">{row.note}</div>}
        {edit && (
          <div className="text-[10.5px] text-text-3">
            was <span className="line-through">{row.unit ? `${row.value} ${row.unit}` : row.value}</span>
          </div>
        )}
      </div>
      <div className="flex shrink-0 items-center gap-1">
        {row.type === "choice" && row.options.length > 0 ? (
          <select
            className={`sw-input h-[22px] max-w-[140px] px-1 text-[12px] ${edit ? "font-semibold" : ""}`}
            value={draft}
            onChange={(e) => (setDraft(e.target.value), commit(e.target.value))}
          >
            {[...new Set([row.value, ...row.options])].map((o) => (
              <option key={o} value={o}>
                {o}
              </option>
            ))}
          </select>
        ) : (
          <input
            className={`sw-input h-[22px] px-1.5 text-[12px] ${numeric ? "w-[76px] text-right tabular-nums" : "w-[140px] text-left"} ${edit ? "font-semibold" : ""}`}
            value={draft}
            inputMode={numeric ? "decimal" : undefined}
            onChange={(e) => setDraft(e.target.value)}
            onBlur={(e) => commit(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === "Enter") commit((e.target as HTMLInputElement).value);
              if (e.key === "Escape") setDraft(value);
            }}
          />
        )}
        <span className="w-[26px] text-[11px] text-text-3">{row.unit}</span>
      </div>
    </div>
  );
}
