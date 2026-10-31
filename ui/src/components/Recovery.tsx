import { useState } from "react";
import { X } from "lucide-react";
import { IconError, IconRebuild, Spinner } from "./SwIcons";
import { useStore } from "../store";

/**
 * Shown after SolidWorks closed unexpectedly while running a tool's preview or macro, or the agent's code. The crash
 * guard already paused the part that crashed; this offers to have the agent find and fix the cause.
 */
export function CrashNotice() {
  const crash = useStore((s) => s.crash);
  const { askAboutCrash, dismissCrash } = useStore.getState();
  if (!crash) return null;
  const what =
    crash.kind === "preview" ? `the preview of “${crash.title ?? crash.tool}”`
    : crash.kind === "addin" ? `an error in SolidWorks Agent itself`
    : crash.kind === "tool" ? `the agent's “${crash.title ?? "tool"}” step`
    : crash.kind === "page" ? `the page of the tool “${crash.title ?? crash.tool}”`
    : crash.kind === "macro" ? `the tool “${crash.title ?? crash.tool}”`
    : `the agent's step “${crash.title ?? "C# code"}”`;
  return (
    <div className="flex shrink-0 items-start gap-2 border-b border-line bg-err-soft px-2 py-1.5 text-[11.5px] text-text shadow-[inset_0_1px_0_var(--bevel-hi)]">
      <span className="mt-px shrink-0">
        <IconError size={14} />
      </span>
      <span className="flex-1">
        <span className="font-semibold">SolidWorks closed unexpectedly while running {what}.</span>{" "}
        <span className="text-text-2">
          {crash.paused ? `I paused that ${crash.paused} so it can't happen again. ` : ""}
          {crash.helpersDisabled?.length ? `The agent's helper ${crash.helpersDisabled.map((h) => h.name).join(", ")} was involved in two crashes, so it is switched off. ` : ""}
          SolidWorks Agent can find the cause and fix it.
        </span>
        <span className="mt-1 flex gap-1.5">
          <button className="sw-btn sw-btn-primary !h-[22px]" onClick={() => void askAboutCrash()}>
            Find and fix the cause
          </button>
          <button className="sw-btn !h-[22px]" onClick={dismissCrash}>
            Dismiss
          </button>
        </span>
      </span>
      <button className="text-text-3 hover:text-text" onClick={dismissCrash} title="Dismiss">
        <X size={12} />
      </button>
    </div>
  );
}

/** After a tool is saved: SolidWorks only adds ribbon buttons at start-up. */
/** "Save and restart SolidWorks": asked for by the agent (reason), or after saving a tool while the ribbon is out of date. */
export function RestartCard({ title, reason, onDismiss }: { title?: string; reason?: string; onDismiss?: () => void }) {
  const stale = useStore((s) => s.ribbonStale);
  const saveAndRestart = useStore((s) => s.saveAndRestart);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  if (!reason && !stale) return null;
  return (
    <div className="sw-rollout flex flex-col gap-1.5 !border-accent px-2.5 py-2 text-[12px]">
      <div className="flex items-start gap-2">
        <span className="mt-px shrink-0">
          <IconRebuild size={14} />
        </span>
        <span className="text-text">
          {reason ? (
            <span className="font-semibold">{reason}</span>
          ) : (
            <>
              <span className="font-semibold">Restart SolidWorks to add {title ? `“${title}”` : "the new tool"} to the SolidWorks Agent tab.</span>{" "}
              <span className="text-text-2">SolidWorks only loads ribbon buttons when it starts.</span>
            </>
          )}{" "}
          <span className="text-text-2">Your documents are saved and reopened where you left off.</span>
        </span>
      </div>
      {error && <div className="text-err">{error}</div>}
      <div className="flex justify-end gap-1.5">
        {onDismiss && (
          <button className="sw-btn" disabled={busy} onClick={onDismiss} title="Hide this - restart later yourself">
            Not now
          </button>
        )}
        <button
          className="sw-btn sw-btn-primary"
          disabled={busy}
          onClick={async () => {
            setBusy(true);
            setError(null);
            const problem = await saveAndRestart();
            if (problem) {
              setError(problem);
              setBusy(false);
            }
          }}
        >
          {busy && <Spinner size={12} />} Save and restart SolidWorks
        </button>
      </div>
    </div>
  );
}
