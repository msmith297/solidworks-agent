import { useEffect, useState, type ReactElement } from "react";
import { X } from "lucide-react";
import { Markdown } from "./Markdown";
import { IconUpdate, Spinner } from "./SwIcons";
import { rpc } from "../lib/bridge";
import { resumePanel, useStore } from "../store";
import type { UpdateInfo } from "../lib/types";

const OFFERED: UpdateInfo["status"][] = ["available", "downloading", "ready"];

export function updateOffered(u: UpdateInfo | null): boolean {
  return !!u && OFFERED.includes(u.status);
}

/** Header button: stays visible while a newer version is available. */
export function UpdateButton() {
  const update = useStore((s) => s.update);
  if (!updateOffered(update)) return null;
  const ready = update!.status === "ready";
  return (
    <button
      type="button"
      onClick={() => useStore.setState({ updateDialog: true })}
      title={`Update to ${update!.latest} available${ready ? " (downloaded)" : ""} - click to install`}
      className="sw-tool relative"
    >
      <IconUpdate size={16} />
      {update!.status === "downloading" && <span className="absolute -bottom-0.5 left-0.5 right-0.5 h-[2px] bg-accent" style={{ transform: `scaleX(${update!.progress / 100})`, transformOrigin: "left" }} />}
    </button>
  );
}

type Unsaved = { title: string; path: string; untitled: boolean };

/** What's new + "Restart SolidWorks and update". */
export function UpdateDialog() {
  const open = useStore((s) => s.updateDialog);
  const update = useStore((s) => s.update);
  const running = useStore((s) => Object.keys(s.runs).length > 0);
  const [unsaved, setUnsaved] = useState<Unsaved[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!open) return;
    setError(null);
    rpc<Unsaved[]>("update.unsavedDocuments").then((u) => setUnsaved(u ?? [])).catch(() => setUnsaved([]));
  }, [open]);

  if (!open || !update) return null;
  const close = () => useStore.setState({ updateDialog: false });
  const untitled = unsaved.filter((d) => d.untitled);

  const install = async () => {
    setBusy(true);
    setError(null);
    const r = await rpc<{ ok: boolean; reason?: string; unsaved?: Unsaved[] }>("update.restartAndInstall", { saveDocuments: true, panel: resumePanel() }).catch((e) => ({
      ok: false,
      reason: String(e?.message ?? e),
    }));
    if (!r.ok) {
      setBusy(false);
      setError(r.reason ?? "The update couldn't start.");
      if ("unsaved" in r && r.unsaved) setUnsaved(r.unsaved);
    }
    // On success SolidWorks closes in a moment; the helper installs and starts it again.
  };

  return (
    <div className="absolute inset-0 z-40 flex items-start justify-center bg-black/40 p-3 pt-10" onClick={close}>
      <div className="sw-rollout w-full max-w-[420px] !bg-card shadow-[0_8px_24px_rgba(0,0,0,0.45)]" onClick={(e) => e.stopPropagation()}>
        <div className="sw-rollout-head">
          <IconUpdate size={15} />
          <span className="flex-1 text-[12px]">Update to version {update.latest}</span>
          <button className="text-text-3 hover:text-text" onClick={close} title="Close">
            <X size={13} />
          </button>
        </div>
        <div className="space-y-2 px-2.5 py-2 text-[12px] text-text">
          <p className="text-text-2">
            You have {update.current}. SolidWorks has to restart to install the update: it closes, the update installs (Windows asks for
            permission), and SolidWorks opens again.
          </p>
          {update.notes && (
            <div className="sw-input max-h-[200px] overflow-y-auto px-2 py-1.5 text-[11.5px]">
              <Markdown text={update.notes} />
            </div>
          )}
          {unsaved.length > 0 && untitled.length === 0 && (
            <p className="text-text-2">
              These documents will be saved first: <span className="text-text">{unsaved.map((d) => d.title).join(", ")}</span>
            </p>
          )}
          {untitled.length > 0 && (
            <p className="text-err">
              Save these first (they have never been saved, so they need a file name): {untitled.map((d) => d.title).join(", ")}
            </p>
          )}
          {running && <p className="text-text-2">The agent is still working - wait for it to finish, or stop it.</p>}
          {update.status === "downloading" && <p className="text-text-2">Downloading… {update.progress}%</p>}
          {error && <p className="text-err">{error}</p>}
        </div>
        <div className="sw-etch flex justify-end gap-1.5 bg-card-2 px-2 py-1.5">
          <button className="sw-btn" onClick={close}>
            Later
          </button>
          <button className="sw-btn sw-btn-primary" disabled={busy || running || untitled.length > 0} onClick={install}>
            {busy && <Spinner size={12} />}
            {unsaved.length > 0 ? "Save, restart and update" : "Restart SolidWorks and update"}
          </button>
        </div>
      </div>
    </div>
  );
}

/** Asked once, the first time the panel is used. */
export function AutoUpdatePrompt() {
  const asked = useStore((s) => s.settings.autoUpdate !== undefined && s.settings.autoUpdate !== null);
  const saveSettings = useStore((s) => s.saveSettings);
  if (asked) return null;
  return (
    <div className="flex shrink-0 items-start gap-2 border-b border-line bg-accent-soft px-2 py-1.5 text-[11.5px] text-text shadow-[inset_0_1px_0_var(--bevel-hi)]">
      <span className="mt-px shrink-0">
        <IconUpdate size={14} />
      </span>
      <span className="flex-1">
        <span className="font-semibold">Keep SolidWorks Agent up to date automatically?</span>{" "}
        <span className="text-text-2">New versions download in the background and install when you close SolidWorks. You can change this in Options.</span>
        <span className="mt-1 flex gap-1.5">
          <button className="sw-btn sw-btn-primary !h-[22px]" onClick={() => saveSettings({ autoUpdate: true })}>
            Update automatically
          </button>
          <button className="sw-btn !h-[22px]" onClick={() => saveSettings({ autoUpdate: false })}>
            Ask me first
          </button>
        </span>
      </span>
    </div>
  );
}

/** Options > Updates. */
export function UpdateSettings({ Toggle }: { Toggle: (p: { checked: boolean; onChange: (v: boolean) => void; label: string; note?: string }) => ReactElement }) {
  const update = useStore((s) => s.update);
  const version = useStore((s) => s.host?.version);
  const auto = useStore((s) => s.settings.autoUpdate === true);
  const saveSettings = useStore((s) => s.saveSettings);
  const [checking, setChecking] = useState(false);

  const check = async () => {
    setChecking(true);
    const u = await rpc<UpdateInfo>("update.check").catch(() => null);
    if (u) useStore.setState({ update: u });
    setChecking(false);
  };

  const status =
    !update ? "" :
    update.status === "uptodate" ? "You have the latest version." :
    update.status === "available" ? `Version ${update.latest} is available.` :
    update.status === "downloading" ? `Downloading ${update.latest}… ${update.progress}%` :
    update.status === "ready" ? `Version ${update.latest} is downloaded${auto ? " and installs when you close SolidWorks" : ""}.` :
    update.status === "error" ? update.error ?? "Update check failed." :
    "";

  return (
    <>
      <Toggle
        checked={auto}
        onChange={(v) => saveSettings({ autoUpdate: v })}
        label="Update automatically"
        note="Download new versions in the background and install them when you close SolidWorks."
      />
      <div className="mt-1 flex items-center gap-2 text-[11.5px] text-text-2">
        <span>Version {update?.current ?? version}</span>
        <button className="sw-btn !h-[22px]" disabled={checking} onClick={check}>
          {checking && <Spinner size={11} />} Check for updates
        </button>
        {updateOffered(update) && (
          <button className="sw-btn sw-btn-primary !h-[22px]" onClick={() => useStore.setState({ updateDialog: true })}>
            Update…
          </button>
        )}
      </div>
      {status && <p className={`mt-1 text-[11px] ${update?.status === "error" ? "text-err" : "text-text-3"}`}>{status}</p>}
    </>
  );
}
