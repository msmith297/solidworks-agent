import { useEffect, useRef } from "react";
import { IconAgent, IconBook, IconPlugin, IconToolbox, ToolIcon } from "./SwIcons";
import { BrandMark } from "./Brand";

/** One row of the composer's popup: a slash command, or something to tag with @. */
export interface MenuRow {
  key: string;
  kind: "builtin" | "command" | "skill" | "tool" | "workflow" | "plugin" | "agent";
  /** What the row inserts ("/name" or "@name" is shown). */
  name: string;
  description: string;
  /** Where it comes from (a plugin, "SolidWorks Agent", "Tool"). */
  source?: string;
  /** A command's argument hint, shown after the name. */
  hint?: string;
}

function KindIcon({ kind }: { kind: MenuRow["kind"] }) {
  switch (kind) {
    case "builtin":
      return <BrandMark size={14} />;
    case "command":
    case "plugin":
      return <IconPlugin size={14} />;
    case "skill":
      return <IconBook size={14} />;
    case "agent":
      return <IconAgent size={14} />;
    case "workflow":
      return <IconToolbox size={14} />;
    default:
      return <ToolIcon name="macro" size={14} />;
  }
}

/**
 * The popup above the message box while "/" or "@" is typed: rows filtered as the user types, ↑/↓ to move, Enter or
 * Tab to take one, Esc to close (the keys are handled by the composer, which owns the text box's focus).
 */
export function SlashMenu({
  rows,
  active,
  prefix,
  title,
  onPick,
  onHover,
}: {
  rows: MenuRow[];
  active: number;
  prefix: "/" | "@";
  title: string;
  onPick: (index: number) => void;
  onHover: (index: number) => void;
}) {
  const list = useRef<HTMLDivElement>(null);
  useEffect(() => {
    list.current?.querySelector<HTMLElement>(`[data-row="${active}"]`)?.scrollIntoView({ block: "nearest" });
  }, [active]);

  return (
    <div
      className="absolute bottom-full left-1.5 right-1.5 z-30 mb-1 overflow-hidden rounded-[3px] border border-line-strong bg-card shadow-[0_4px_14px_rgba(0,0,0,0.4)]"
      role="listbox"
      aria-label={title}
      // Keep the focus in the text box: clicking a row must not blur it.
      onMouseDown={(e) => e.preventDefault()}
    >
      <div className="sw-rollout-head !min-h-[20px] gap-1 text-[11px]">
        <span className="min-w-0 flex-1 truncate">{title}</span>
        <span className="shrink-0 font-normal text-text-3">↑↓ · Enter · Esc</span>
      </div>
      <div ref={list} className="max-h-[min(260px,45vh)] overflow-y-auto py-0.5">
        {rows.length === 0 && <div className="px-2 py-1.5 text-[11.5px] text-text-3">Nothing matches.</div>}
        {rows.map((r, i) => (
          <button
            key={r.key}
            type="button"
            role="option"
            aria-selected={i === active}
            data-row={i}
            onClick={() => onPick(i)}
            onMouseMove={() => i !== active && onHover(i)}
            className={`flex w-full min-w-0 items-start gap-1.5 px-2 py-1 text-left ${i === active ? "bg-accent-soft" : ""}`}
          >
            <span className="mt-px shrink-0">
              <KindIcon kind={r.kind} />
            </span>
            <span className="min-w-0 flex-1">
              <span className="flex min-w-0 items-baseline gap-1.5">
                <span className="truncate text-[12px] font-semibold text-text">
                  {prefix}
                  {r.name}
                </span>
                {r.hint && <span className="hidden min-w-0 truncate text-[11px] text-text-3 @min-[300px]:inline">{r.hint}</span>}
                {r.source && <span className="ml-auto max-w-[50%] shrink-0 truncate pl-1 text-[10.5px] text-text-3">{r.source}</span>}
              </span>
              {r.description && <span className="line-clamp-2 block text-[11px] text-text-3">{r.description}</span>}
            </span>
          </button>
        ))}
      </div>
    </div>
  );
}
