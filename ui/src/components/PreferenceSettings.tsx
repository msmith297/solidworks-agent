import { useState, type ReactElement, type ReactNode } from "react";
import { IconFx } from "./SwIcons";
import { useStore } from "../store";
import { rpc } from "../lib/bridge";
import type { PreferenceItem } from "../lib/preferences";

type RolloutProps = { title: string; icon?: ReactNode; children: ReactNode };

/**
 * Options > "Preferred parameters" (docs/preferred-parameters.md): the design values the agent uses by default, by topic,
 * each editable and deletable, and the file itself for editing by hand.
 */
export function PreferenceSettings({ Rollout }: { Rollout: (p: RolloutProps) => ReactElement }) {
  const prefs = useStore((s) => s.prefs);
  const [error, setError] = useState<string | null>(null);
  const act = async (fn: () => Promise<unknown>) => {
    setError(null);
    try {
      await fn();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  };
  const topics = prefs?.topics ?? [];

  return (
    <Rollout title="Preferred parameters" icon={<IconFx size={14} />}>
      <p className="text-[11px] text-text-3">
        Sizes and values you like to use, such as hole sizes for a screw in printed parts. The agent uses them by default and adds to them when you choose or
        correct a value.
      </p>
      {prefs === null && <p className="py-1 text-[11px] text-text-3">Not available with this version of the add-in.</p>}
      {prefs !== null && topics.length === 0 && <p className="py-1 text-[11px] text-text-3">Nothing saved yet.</p>}
      {topics.map((t) => (
        <div key={t.topic} className="mt-1.5">
          <div className="text-[11.5px] font-semibold text-text">{t.topic}</div>
          {t.items.map((p) => (
            <PrefRow key={p.id} item={p} onSave={(text) => act(() => rpc("prefs.edit", { id: p.id, text }))} onDelete={() => act(() => rpc("prefs.forget", { id: p.id }))} />
          ))}
        </div>
      ))}
      {prefs !== null && (
        <div className="mt-1.5 flex items-center gap-1.5">
          <button type="button" className="sw-btn" onClick={() => act(() => rpc("prefs.openFile"))} title={prefs.file}>
            Open the file
          </button>
          <span className="text-[10.5px] text-text-3">{Math.round((100 * prefs.used) / prefs.cap)}% full</span>
        </div>
      )}
      {error && <p className="mt-1 text-[11px] text-err">{error}</p>}
    </Rollout>
  );
}

function PrefRow({ item, onSave, onDelete }: { item: PreferenceItem; onSave: (text: string) => Promise<void>; onDelete: () => Promise<void> }) {
  const [editing, setEditing] = useState(false);
  const [text, setText] = useState(item.text);
  if (editing)
    return (
      <form
        className="flex flex-col gap-1 py-0.5"
        onSubmit={async (e) => {
          e.preventDefault();
          if (text.trim() && text.trim() !== item.text) await onSave(text.trim());
          setEditing(false);
        }}
      >
        <textarea value={text} onChange={(e) => setText(e.target.value)} rows={2} maxLength={300} className="sw-input min-w-0 px-1.5 py-1 text-[12px]" autoFocus />
        <div className="flex justify-end gap-1">
          <button type="button" className="sw-btn !h-[20px] !px-1.5" onClick={() => (setText(item.text), setEditing(false))}>
            Cancel
          </button>
          <button type="submit" className="sw-btn sw-btn-primary !h-[20px] !px-1.5" disabled={!text.trim()}>
            Save
          </button>
        </div>
      </form>
    );
  return (
    <div className="group flex items-start gap-1.5 py-0.5 text-[12px]">
      <span className="min-w-0 flex-1 text-text-2">
        {item.text}
        {item.date && <span className="text-[10.5px] text-text-3"> · {item.date}</span>}
      </span>
      <button type="button" className="sw-btn !h-[20px] shrink-0 !px-1.5" title="Change this value" onClick={() => (setText(item.text), setEditing(true))}>
        Edit
      </button>
      <button type="button" className="sw-btn !h-[20px] shrink-0 !px-1.5" title="Delete this value" onClick={() => void onDelete()}>
        Delete
      </button>
    </div>
  );
}
