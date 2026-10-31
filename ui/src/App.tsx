import { useEffect, useState } from "react";
import { X } from "lucide-react";
import { IconInfo } from "./components/SwIcons";
import { applyTheme } from "./lib/theme";
import { applyZoom, stepZoom, zoomKey } from "./lib/zoom";
import { Header } from "./components/Header";
import { ChatView } from "./components/ChatView";
import { Composer } from "./components/Composer";
import { SettingsPanel } from "./components/SettingsPanel";
import { AutoUpdatePrompt, UpdateDialog } from "./components/Update";
import { Tour } from "./components/Tour";
import { VoicePrompt } from "./components/VoicePrompt";
import { ToolRunBanner } from "./components/ToolRunBanner";
import { CrashNotice } from "./components/Recovery";
import { DialogHost } from "./components/Dialog";
import { NewToolWizard } from "./components/NewToolWizard";
import { PluginsScreen } from "./components/PluginsScreen";
import { useStore } from "./store";

export default function App() {
  const booted = useStore((s) => s.booted);
  const bootError = useStore((s) => s.bootError);
  const panel = useStore((s) => s.panel);
  const notice = useStore((s) => s.notice);
  const theme = useStore((s) => s.settings.theme);
  const swTheme = useStore((s) => s.swTheme);
  const dismissNotice = useStore((s) => s.dismissNotice);
  const zoom = useStore((s) => s.settings.zoom);
  const touring = useStore((s) => s.tourStep !== null);
  // One first-run question at a time: voice (after the tour), then updates.
  const voiceAnswered = useStore((s) => !!s.settings.voiceAsked || !!s.settings.voiceEnabled || s.voiceStatus?.available === false);
  const [zoomBadge, setZoomBadge] = useState(false);

  useEffect(() => applyZoom(zoom), [zoom]);

  // Ctrl+ / Ctrl- / Ctrl+0 and Ctrl+wheel change the text size; a badge shows the new size briefly.
  useEffect(() => {
    let hide = 0;
    const change = (action: "in" | "out" | "reset") => {
      const { settings, saveSettings } = useStore.getState();
      const next = action === "reset" ? 1 : stepZoom(settings.zoom ?? 1, action === "in" ? 1 : -1);
      if (next !== settings.zoom) saveSettings({ zoom: next });
      setZoomBadge(true);
      clearTimeout(hide);
      hide = window.setTimeout(() => setZoomBadge(false), 1200);
    };
    const onKey = (e: KeyboardEvent) => {
      const action = zoomKey(e);
      if (!action) return;
      e.preventDefault();
      change(action);
    };
    const onWheel = (e: WheelEvent) => {
      if (!e.ctrlKey || e.deltaY === 0) return;
      e.preventDefault();
      change(e.deltaY < 0 ? "in" : "out");
    };
    window.addEventListener("keydown", onKey);
    window.addEventListener("wheel", onWheel, { passive: false });
    return () => {
      clearTimeout(hide);
      window.removeEventListener("keydown", onKey);
      window.removeEventListener("wheel", onWheel);
    };
  }, []);

  useEffect(() => {
    useStore.getState().boot();
  }, []);

  // F5 / Ctrl+R reloads the panel, picking up a rebuilt UI without restarting SolidWorks.
  // Skipped while the agent is working so a run isn't cut off.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const reload = e.key === 'F5' || ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'r');
      if (!reload) return;
      e.preventDefault();
      if (Object.keys(useStore.getState().runs).length === 0) location.reload();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  useEffect(() => {
    const media = window.matchMedia("(prefers-color-scheme: dark)");
    const apply = () => applyTheme(theme, swTheme, media.matches);
    apply();
    media.addEventListener("change", apply);
    return () => media.removeEventListener("change", apply);
  }, [theme, swTheme]);

  if (bootError) {
    return <div className="flex h-full items-center justify-center p-6 text-center text-[12px] text-err">SolidWorks Agent couldn't connect to SolidWorks: {bootError}</div>;
  }
  if (!booted) {
    return <div className="flex h-full items-center justify-center text-[12px] text-text-3 shimmer">Connecting to SolidWorks…</div>;
  }

  return (
    <div className="relative flex h-full flex-col bg-bg">
      {zoomBadge && (
        <div className="pointer-events-none absolute left-1/2 top-10 z-50 -translate-x-1/2 rounded-[3px] border border-line-strong bg-card px-2.5 py-1 text-[11.5px] text-text shadow-[0_4px_14px_rgba(0,0,0,0.35)]">
          Text size {Math.round((zoom ?? 1) * 100)}%{zoom !== 1 && <span className="text-text-3"> · Ctrl+0 to reset</span>}
        </div>
      )}
      <div className="zoom-chrome shrink-0">
        <Header />
        {!touring && <VoicePrompt />}
        {!touring && voiceAnswered && <AutoUpdatePrompt />}
        <CrashNotice />
        <ToolRunBanner />
      </div>
      <UpdateDialog />
      <DialogHost />
      <Tour />
      {notice && (
        <div className="flex shrink-0 items-start gap-2 border-b border-line bg-accent-soft px-2 py-1.5 text-[11.5px] text-text shadow-[inset_0_1px_0_var(--bevel-hi)]">
          <span className="mt-px shrink-0"><IconInfo size={14} /></span>
          <span className="flex-1">{notice}</span>
          <button onClick={dismissNotice} className="text-text-3 hover:text-text" title="Dismiss">
            <X size={13} />
          </button>
        </div>
      )}
      {panel === "settings" ? (
        <SettingsPanel />
      ) : panel === "newTool" ? (
        <NewToolWizard />
      ) : panel === "plugins" ? (
        <PluginsScreen />
      ) : (
        <>
          <ChatView />
          <div className="zoom-chrome shrink-0">
            <Composer />
          </div>
        </>
      )}
    </div>
  );
}
