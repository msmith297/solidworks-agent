import { useEffect, useState, type ReactNode } from "react";
import { BrandMark } from "./Brand";
import { UpdateSettings } from "./Update";
import { WebResearchSettings } from "./WebResearchSettings";
import { MemorySettings } from "./MemorySettings";
import { ToolProfilesSettings } from "./ToolProfilesSettings";
import { stepZoom } from "../lib/zoom";
import { useStore } from "../store";
import { rpc } from "../lib/bridge";
import { clampEffort, effortsFor, groupModels, pickerModels } from "../lib/models";
import { PROVIDERS, getProvider } from "../agent/providers";
import { AiProviderSettings } from "./ProviderSettings";
import { SW_THEME_NAMES } from "../lib/theme";
import { Caret, IconAgent, IconGear, IconHistory, IconMic, IconOk, IconPart, IconPlugin, IconRebuild, IconShield, IconToolbox, IconUpdate, Spinner } from "./SwIcons";
import type { ApprovalMode, Effort, Settings, Theme } from "../lib/types";

/** Options page laid out like a SolidWorks PropertyManager: stacked rollouts. */
export function SettingsPanel() {
  const settings = useStore((s) => s.settings);
  const hasBridge = useStore((s) => s.plugins.some((p) => p.name === "agent-bridge" && p.installed));
  const host = useStore((s) => s.host);
  const swTheme = useStore((s) => s.swTheme);
  const { saveSettings, setPanel } = useStore.getState();
  const swName = swTheme ? SW_THEME_NAMES[swTheme.theme] : null;

  return (
    <div className="min-h-0 flex-1 overflow-y-auto bg-bg px-1.5 py-1.5">
      <div className="zoom-content mx-auto flex max-w-[520px] flex-col gap-1.5">
        <div className="flex items-center gap-1.5 px-1 pb-0.5">
          <BrandMark size={18} />
          <span className="flex-1 text-[13px] font-semibold text-text">Unify3D Agent Options</span>
          <button onClick={() => setPanel("chat")} className="sw-btn" title="Back to chat">
            <IconOk size={12} /> Done
          </button>
        </div>

        <AiProviderSettings />

        <Rollout title="Defaults for new chats" icon={<IconAgent size={14} />}>
          <Field label="Model">
            <ModelSelect value={settings.defaultModel} onChange={(v) => saveSettings({ defaultModel: v })} />
          </Field>
          <Field label="Effort">
            <Select<Effort>
              value={clampEffort(settings.defaultEffort, settings.defaultModel)}
              onChange={(v) => saveSettings({ defaultEffort: v })}
              options={effortsFor(settings.defaultModel).map((e) => [e.id, `${e.label} - ${e.note}`])}
            />
          </Field>
          <Field label="Replies">
            <Select<Settings["verbosity"]>
              value={settings.verbosity ?? "medium"}
              onChange={(v) => saveSettings({ verbosity: v })}
              options={[
                ["low", "Brief - just the result and what you need to do"],
                ["medium", "Normal - result, key choices, what to check"],
                ["high", "Detailed - full explanation, tests and alternatives"],
              ]}
            />
          </Field>
        </Rollout>

        <Rollout title="Modelling" icon={<IconPart size={14} />}>
          <Toggle
            checked={settings.realisticAppearance}
            onChange={(v) => saveSettings({ realisticAppearance: v })}
            label="Give parts real materials and realistic appearances"
            note="The agent assigns the actual engineering material (so mass is right) and a matching finish - colour, texture, coating - to the parts it creates."
          />
          <Toggle
            checked={settings.shareView}
            onChange={(v) => saveSettings({ shareView: v })}
            label="Send a screenshot of my view with each message"
            note="The agent sees exactly what you see, with your selection highlighted. Adds roughly 1,000 tokens per message."
          />
        </Rollout>

        <Rollout title="Voice" icon={<IconMic size={14} />}>
          <VoiceSettings />
        </Rollout>

        <Rollout title="Running code" icon={<IconShield size={14} />}>
          <div className="flex flex-col">
            {(
              [
                ["ask", "Ask before changing the model", "You approve each code run. Reading the model never asks."],
                ["auto", "Run automatically", "Fastest. Every change is still one Ctrl+Z undo step."],
                ["read_only", "Read-only", "The agent can inspect and explain, but not modify anything."],
              ] as [ApprovalMode, string, string][]
            ).map(([id, label, note]) => (
              <label key={id} className="flex cursor-default items-start gap-2 rounded-[2px] px-1 py-1 hover:bg-card-2">
                <input type="radio" name="approval" checked={settings.approval === id} onChange={() => saveSettings({ approval: id })} className="mt-0.5 accent-[var(--accent)]" />
                <span>
                  <span className="block text-[12px] text-text">{label}</span>
                  <span className="block text-[11px] text-text-3">{note}</span>
                </span>
              </label>
            ))}
          </div>
        </Rollout>

        <Rollout title="Tools" icon={<IconToolbox size={14} />}>
          <div className="text-[11.5px] text-text-2">Where a tool's inputs appear when you run it</div>
          <div className="flex flex-col">
            {(
              [
                ["panel", "In the chat panel", "A form in the chat with the tool's inputs, units and choices."],
                ["page", "In a PropertyManager page", "SolidWorks' own page, next to the feature tree."],
              ] as ["panel" | "page", string, string][]
            ).map(([id, label, note]) => (
              <label key={id} className="flex cursor-default items-start gap-2 rounded-[2px] px-1 py-1 hover:bg-card-2">
                <input type="radio" name="toolInputs" checked={(settings.toolInputs ?? "panel") === id} onChange={() => saveSettings({ toolInputs: id })} className="mt-0.5 accent-[var(--accent)]" />
                <span>
                  <span className="block text-[12px] text-text">{label}</span>
                  <span className="block text-[11px] text-text-3">{note}</span>
                </span>
              </label>
            ))}
          </div>
          <ToolProfilesSettings />
        </Rollout>

        <MemorySettings Rollout={Rollout} Toggle={Toggle} />

        <Rollout title="Plugins" icon={<IconPlugin size={14} />}>
          <Toggle
            checked={settings.pluginAutoUpdate !== false}
            onChange={(v) => saveSettings({ pluginAutoUpdate: v })}
            label="Update plugins automatically"
            note="At startup, in the background. Plugins from the built-in marketplace, and from other marketplaces where you turned updates on."
          />
          <Toggle
            checked={settings.pluginHooks !== false}
            onChange={(v) => saveSettings({ pluginHooks: v })}
            label="Allow plugin hooks"
            note="Programs a plugin runs when something happens (you send a message, the agent finishes). Each plugin shows what it runs and asks once."
          />
          <Toggle
            checked={settings.pluginMcp !== false}
            onChange={(v) => saveSettings({ pluginMcp: v })}
            label="Allow MCP servers"
            note="Programs that give the agent extra tools - from plugins, and from your own mcp.json."
          />
          <label className="flex cursor-default items-start gap-2 py-1">
            <input
              type="number"
              min={0}
              max={200}
              value={settings.pluginWakesPerHour ?? 20}
              onChange={(e) => {
                const n = Math.round(Number(e.target.value));
                if (Number.isFinite(n) && n >= 0) saveSettings({ pluginWakesPerHour: Math.min(200, n) });
              }}
              className="sw-input h-[22px] w-[52px] shrink-0 px-1 text-[12px]"
            />
            <span>
              <span className="block text-[12px] text-text">Automatic turns per hour</span>
              <span className="block text-[11px] text-text-3">How often plugins may start the agent by themselves (e.g. a message from another agent). Then they wait for your next message.</span>
            </span>
          </label>
          <Toggle
            checked={settings.pluginEventsInChat !== false}
            onChange={(v) => saveSettings({ pluginEventsInChat: v })}
            label="Show plugin events in the chat"
            note="What hooks add for the agent appears as small cards in the chat."
          />
          <Toggle
            checked={settings.trustAgentWakes !== false}
            onChange={(v) => saveSettings({ trustAgentWakes: v })}
            label="Trust messages from my agents"
            note="Requests from your other AI agents (agent-bridge) and from Claude Desktop or Codex follow your approval setting. Turn off to have them always ask before changing the model."
          />
          {hasBridge && (
            <PluginGroup name="Agent Bridge">
              <Toggle
                checked={settings.trustConsoleWakes !== false}
                onChange={(v) => saveSettings({ trustConsoleWakes: v })}
                label="Trust my agent-bridge console"
                note="Messages you send from the agent-bridge console follow your approval setting."
              />
            </PluginGroup>
          )}
          <button type="button" className="sw-btn mt-1.5" onClick={() => setPanel("plugins")}>
            <IconPlugin size={13} /> Manage plugins…
          </button>
        </Rollout>

        <WebResearchSettings />

        <Rollout title="While the agent works" icon={<IconRebuild size={14} />}>
          <Toggle
            checked={settings.muteErrorSounds}
            onChange={(v) => saveSettings({ muteErrorSounds: v })}
            label="Silence SolidWorks' rebuild-error sound"
            note="The agent often hits and fixes rebuild errors along the way. The sound comes back as soon as it finishes."
          />
          <div className="mt-2 text-[11.5px] text-text-2">When the agent asks you something and you don't answer</div>
          <Field label="After">
            <Select<string>
              value={String(settings.questionTimeoutSec ?? 60)}
              onChange={(v) => saveSettings({ questionTimeoutSec: Number(v) })}
              options={[
                ["30", "30 seconds, it decides on its own"],
                ["60", "1 minute, it decides on its own"],
                ["120", "2 minutes, it decides on its own"],
                ["300", "5 minutes, it decides on its own"],
                ["0", "Never - it waits for my answer"],
              ]}
            />
          </Field>
        </Rollout>

        <Rollout title="Chat history" icon={<IconHistory size={14} />}>
          <p className="py-1 text-[11px] text-text-3">
            Chats are kept on this PC and follow you from file to file - opening or switching documents never switches the chat. The chat list shows which files each one worked in. Chats that older versions saved inside your files are added to the list when you open them.
          </p>
          <Toggle checked={settings.showProgress} onChange={(v) => saveSettings({ showProgress: v })} label="Show the agent's progress notes" note="Short status lines the model writes between tool calls." />
        </Rollout>

        <Rollout title="Updates" icon={<IconUpdate size={14} />}>
          <UpdateSettings Toggle={Toggle} />
        </Rollout>

        <Rollout title="Appearance" icon={<IconGear size={14} />}>
          <Field label="Theme">
            <Select<Theme>
              value={settings.theme}
              onChange={(v) => saveSettings({ theme: v })}
              options={[
                ["solidworks", `Match SolidWorks${swName ? ` (${swName})` : ""}`],
                ["light", "Light"],
                ["dark", "Dark"],
                ["system", "Match Windows"],
              ]}
            />
          </Field>
          <p className="mt-1 text-[11px] text-text-3">Match SolidWorks follows Tools &gt; Options &gt; Colors &gt; Background and updates when you change it.</p>
          <Field label="Text size">
            <div className="flex items-center gap-1">
              <button type="button" className="sw-btn !h-[22px] !w-[24px] !p-0" title="Smaller (Ctrl -)" onClick={() => saveSettings({ zoom: stepZoom(settings.zoom ?? 1, -1) })}>
                −
              </button>
              <span className="w-[44px] text-center text-[12px] tabular-nums text-text">{Math.round((settings.zoom ?? 1) * 100)}%</span>
              <button type="button" className="sw-btn !h-[22px] !w-[24px] !p-0" title="Larger (Ctrl +)" onClick={() => saveSettings({ zoom: stepZoom(settings.zoom ?? 1, 1) })}>
                +
              </button>
              {(settings.zoom ?? 1) !== 1 && (
                <button type="button" className="sw-btn !h-[22px] !px-2" title="Reset (Ctrl 0)" onClick={() => saveSettings({ zoom: 1 })}>
                  Reset
                </button>
              )}
            </div>
          </Field>
          <p className="mt-1 text-[11px] text-text-3">Or press Ctrl + / Ctrl − (or Ctrl + mouse wheel) in the panel; Ctrl 0 resets.</p>
        </Rollout>

        <div className="flex items-center justify-between px-1 pt-0.5 text-[11px] text-text-3">
          <span>
            Agent {host?.version}
            {host?.solidworksVersion ? ` · SolidWorks ${host.solidworksVersion}` : ""}
          </span>
          <span className="flex gap-3">
            <button className="hover:text-text" onClick={() => useStore.getState().startTour()} title="The first-run tour: what the agent can do and where things are">
              Take the tour
            </button>
            <button className="hover:text-text" onClick={() => location.reload()} title="Reload the panel (F5)">
              Reload panel
            </button>
            <button className="hover:text-text" onClick={() => rpc("app.openLogs")}>
              Open logs
            </button>
          </span>
        </div>
      </div>
    </div>
  );
}

const TALK_KEYS: [string, string][] = [
  ["vk:192", "` (the key left of 1)"],
  ["vk:20", "Caps Lock"],
  ["vk:163", "Right Ctrl"],
  ["vk:119", "F8"],
  ["vk:19", "Pause"],
  ["mouse:x1", "Mouse side button (Back)"],
  ["mouse:x2", "Mouse side button (Forward)"],
];

/** Push-to-talk: the key, the speech model (downloaded once), what happens on release, and spoken replies. */
function VoiceSettings() {
  const settings = useStore((s) => s.settings);
  const status = useStore((s) => s.voiceStatus);
  const want = useStore((s) => s.voiceWant);
  const { saveSettings, enableVoice, disableVoice, downloadVoiceModel } = useStore.getState();
  const [voices, setVoices] = useState<{ name: string; culture: string }[]>([]);
  const speaking = (settings.speakReplies ?? "off") !== "off";
  useEffect(() => {
    if (speaking && voices.length === 0) rpc<{ name: string; culture: string }[]>("voice.voices").then((v) => setVoices(v ?? [])).catch(() => {});
  }, [speaking, voices.length]);

  if (status && !status.available) {
    return <p className="text-[11.5px] text-text-3">The speech component isn't installed. Reinstall Unify3D Agent to use voice.</p>;
  }
  const modelId = settings.voiceModel ?? "base.en";
  const models = status?.models ?? [];
  const anyInstalled = models.some((m) => m.installed);
  // Voice is only on with a model on disk; while the model to turn it on with downloads, the box shows that.
  const on = settings.voiceEnabled === true && models.some((m) => m.id === modelId && m.installed);
  const turningOn = !on && !!want && status?.download?.id === want;
  const progress = Math.round((status?.download?.progress ?? 0) * 100);
  return (
    <>
      <label className="flex cursor-default items-start gap-2 py-1">
        <input
          type="checkbox"
          checked={on || turningOn}
          onChange={(e) => (e.target.checked ? enableVoice(anyInstalled && !models.some((m) => m.id === modelId && m.installed) ? models.find((m) => m.installed)!.id : modelId) : disableVoice())}
          className="mt-0.5 accent-[var(--accent)]"
        />
        <span>
          <span className="block text-[12px] text-text">Hold a key and talk to the agent</span>
          <span className="block text-[11px] text-text-3">
            {turningOn
              ? `Downloading the speech model (${progress}%) - voice turns on when it's done.`
              : "Your words appear as you speak and go out when you let go. What you select, change, sketch or mark up while talking goes along with them, in order. Speech is turned into text on this PC - the audio never leaves it."}
          </span>
          {!on && !turningOn && !anyInstalled && <span className="block text-[11px] text-text-3">Turning it on downloads the speech model below ({models.find((m) => m.id === modelId)?.mb ?? 141} MB, once).</span>}
        </span>
      </label>

      <Field label="Key">
        <Select value={settings.voiceKey ?? "vk:192"} onChange={(v) => saveSettings({ voiceKey: v })} options={TALK_KEYS} />
      </Field>
      <p className="mt-0.5 text-[11px] text-text-3">
        Works while SolidWorks is in front. {(settings.voiceKey ?? "vk:192").startsWith("vk:") ? "A quick tap still types the key. " : ""}Esc while holding it discards the message.
      </p>

      <div className="mt-2 text-[11.5px] text-text-2">Speech model (downloaded once)</div>
      <div className="mt-0.5 flex flex-col">
        {models.map((m) => {
          const downloading = status?.download?.id === m.id;
          return (
            <label key={m.id} className={`flex cursor-default items-start gap-2 rounded-[2px] px-1 py-1 hover:bg-card-2 ${m.installed ? "" : "opacity-90"}`}>
              <input
                type="radio"
                name="voiceModel"
                checked={modelId === m.id}
                disabled={!m.installed}
                title={m.installed ? undefined : "Download it first"}
                onChange={() => saveSettings({ voiceModel: m.id })}
                className="mt-0.5 accent-[var(--accent)]"
              />
              <span className="min-w-0 flex-1">
                <span className="block text-[12px] text-text">
                  {m.label} <span className="text-text-3">· {m.mb} MB</span>
                </span>
                <span className="block text-[11px] text-text-3">{m.note}</span>
                {downloading && (
                  <span className="mt-1 block h-[4px] w-full overflow-hidden rounded-full bg-card-2">
                    <span className="block h-full bg-accent transition-[width]" style={{ width: `${progress}%` }} />
                  </span>
                )}
              </span>
              {m.installed ? (
                <span className="flex shrink-0 items-center gap-1 text-[11px] text-text-3">
                  <IconOk size={11} /> Installed
                  {modelId !== m.id && (
                    <button type="button" className="ml-1 text-text-3 hover:text-err" onClick={() => rpc("voice.deleteModel", { id: m.id })}>
                      Remove
                    </button>
                  )}
                </span>
              ) : downloading ? (
                <button type="button" className="sw-btn !h-5 shrink-0 !px-1.5 text-[11px]" onClick={() => (on ? rpc("voice.cancelDownload") : disableVoice())}>
                  {progress}% · Cancel
                </button>
              ) : (
                <button type="button" className="sw-btn !h-5 shrink-0 !px-1.5 text-[11px]" disabled={!!status?.download} onClick={() => (on || anyInstalled ? downloadVoiceModel(m.id) : enableVoice(m.id))}>
                  Download
                </button>
              )}
            </label>
          );
        })}
      </div>
      {status?.downloadError && <p className="mt-0.5 text-[11px] text-err">{status.downloadError}</p>}
      {on && (
        <p className="mt-0.5 flex items-center gap-1 text-[11px] text-text-3">
          {status?.engine === "loading" && (
            <>
              <Spinner size={11} /> Loading the speech model…
            </>
          )}
          {status?.engine === "ready" && "Ready - hold the key and talk."}
          {status?.engine === "off" && "Starts the first time you hold the key."}
          {status?.engine === "error" && <span className="text-err">{status.engineError}</span>}
        </p>
      )}
      <p className="mt-0.5 text-[11px] text-text-3">Models come from the whisper.cpp project on Hugging Face.</p>

      <Field label="Send">
        <Select<"release" | "review">
          value={settings.voiceSend ?? "release"}
          onChange={(v) => saveSettings({ voiceSend: v })}
          options={[
            ["release", "Send it right away"],
            ["review", "Put it in the message box to check first"],
          ]}
        />
      </Field>
      <Toggle
        checked={settings.voiceViews !== false}
        onChange={(v) => saveSettings({ voiceViews: v })}
        label="Include the view as I turn and change the model"
        note="Each time the view settles while you talk, a picture of it goes along (up to 10 per message, about 1,000 tokens each)."
      />
      <Field label="Speak">
        <Select<"off" | "voice" | "always">
          value={settings.speakReplies ?? "off"}
          onChange={(v) => saveSettings({ speakReplies: v })}
          options={[
            ["off", "Don't read replies aloud"],
            ["voice", "Read replies aloud after a voice message"],
            ["always", "Read every reply aloud"],
          ]}
        />
      </Field>
      {speaking && (
        <>
          <Field label="Voice">
            <Select
              value={settings.speakVoice ?? ""}
              onChange={(v) => saveSettings({ speakVoice: v })}
              options={[["", "Windows default"], ...voices.map((v): [string, string] => [v.name, `${v.name}${v.culture ? ` (${v.culture})` : ""}`])]}
            />
          </Field>
          <Field label="Speed">
            <input
              type="range"
              min={-5}
              max={5}
              step={1}
              value={settings.speakRate ?? 0}
              onChange={(e) => saveSettings({ speakRate: Number(e.target.value) })}
              className="min-w-0 flex-1 accent-[var(--accent)]"
            />
            <button type="button" className="sw-btn !h-[22px] !px-2" onClick={() => rpc("voice.speak", { text: "This is how replies will sound. Talking or pressing Stop interrupts me." })}>
              Test
            </button>
          </Field>
        </>
      )}
    </>
  );
}

export function Rollout({ title, icon, children }: { title: string; icon?: ReactNode; children: ReactNode }) {
  const [open, setOpen] = useState(true);
  return (
    <section className="sw-rollout">
      <button type="button" className={`sw-rollout-head ${open ? "" : "collapsed"}`} onClick={() => setOpen((o) => !o)} aria-expanded={open}>
        {icon}
        <span className="flex-1 text-[12px]">{title}</span>
        <Caret open={open} className="text-text-3" />
      </button>
      {open && <div className="px-2 py-1.5">{children}</div>}
    </section>
  );
}

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label className="mt-1 flex items-center gap-2 first:mt-0">
      <span className="w-12 shrink-0 text-[11.5px] text-text-2">{label}</span>
      {children}
    </label>
  );
}

/** The default model: the models of providers with a key (plus the current one), under provider headers when several. */
function ModelSelect({ value, onChange }: { value: string; onChange: (v: string) => void }) {
  const sources = useStore((s) => s.apiKeySources);
  const groups = groupModels(pickerModels(value, (p) => (sources[p] ?? "none") !== "none"), PROVIDERS.map((p) => p.id));
  const option = (m: { id: string; label: string; note: string }) => (
    <option key={m.id} value={m.id}>
      {m.note ? `${m.label} - ${m.note}` : m.label}
    </option>
  );
  return (
    <select value={value} onChange={(e) => onChange(e.target.value)} className="sw-input h-[22px] min-w-0 flex-1 px-1 text-[12px]">
      {groups.map((g) =>
        g.header ? (
          <optgroup key={g.provider} label={getProvider(g.provider).label}>
            {g.models.map(option)}
          </optgroup>
        ) : (
          g.models.map(option)
        ),
      )}
    </select>
  );
}

function Select<T extends string>({ value, onChange, options }: { value: T; onChange: (v: T) => void; options: [T, string][] }) {
  return (
    <select value={value} onChange={(e) => onChange(e.target.value as T)} className="sw-input h-[22px] min-w-0 flex-1 px-1 text-[12px]">
      {options.map(([id, label]) => (
        <option key={id} value={id}>
          {label}
        </option>
      ))}
    </select>
  );
}

/** One plugin's own settings inside Options > Plugins, under its name. */
function PluginGroup({ name, children }: { name: string; children: ReactNode }) {
  return (
    <div className="mt-2 border-t border-line pt-1.5">
      <div className="flex items-center gap-1.5 text-[12px] font-semibold text-text">
        <IconPlugin size={13} /> {name}
      </div>
      <div className="pl-1">{children}</div>
    </div>
  );
}

export function Toggle({ checked, onChange, label, note }: { checked: boolean; onChange: (v: boolean) => void; label: string; note?: string }) {
  return (
    <label className="flex cursor-default items-start gap-2 py-1">
      <input type="checkbox" checked={checked} onChange={(e) => onChange(e.target.checked)} className="mt-0.5 accent-[var(--accent)]" />
      <span>
        <span className="block text-[12px] text-text">{label}</span>
        {note && <span className="block text-[11px] text-text-3">{note}</span>}
      </span>
    </label>
  );
}
