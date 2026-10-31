import { useEffect, useLayoutEffect, useRef, useState, type ReactNode } from "react";
import { BrandMark } from "./Brand";
import { IconAgent, IconKey, IconMarkup, IconMic, IconOk, IconSend, IconShield, IconToolbox, IconUndo } from "./SwIcons";
import { anyKey, draftKey, talkKeyName, useStore } from "../store";
import { rpc } from "../lib/bridge";
import { EFFORTS, clampEffort, modelOption } from "../lib/models";
import { PROVIDERS, getProvider } from "../agent/providers";
import type { ProviderId } from "../lib/types";
import { providerTitle } from "./ProviderSettings";

/**
 * The first-run tour: a few cards, each pointing at the real control it explains (a spotlight cut out of a dimmed
 * panel). Skippable at every step; finishing or skipping marks it done, and Options > Take the tour replays it.
 * Elements opt in with data-tour="name"; a step whose element isn't on screen shows its card in the middle.
 */
interface Step {
  id: string;
  target?: string;
  icon: ReactNode;
  title: string;
  body: ReactNode;
  /** Only shown when this is true (e.g. the API key step). */
  when?: () => boolean;
}

const STARTERS = [
  "Summarize this model and check it for rebuild errors",
  "Add a 2 mm fillet to the selected edges",
  "Make a 120 × 80 × 10 mm plate with an M6 clearance hole 15 mm in from each corner",
];

export function Tour() {
  const step = useStore((s) => s.tourStep);
  const hasAnyKey = useStore(anyKey);
  const defaultModel = useStore((s) => modelOption(s.settings.defaultModel).label);
  const defaultEffort = useStore((s) => EFFORTS.find((e) => e.id === clampEffort(s.settings.defaultEffort, s.settings.defaultModel))?.label ?? "High");
  const talkKey = useStore((s) => talkKeyName(s.settings.voiceKey));
  const { setTourStep, endTour } = useStore.getState();

  const steps: Step[] = [
    {
      id: "welcome",
      icon: <BrandMark size={30} />,
      title: "Welcome to SolidWorks Agent",
      body: (
        <>
          <p>An AI engineer that works inside SolidWorks. It reads and changes your models through the SolidWorks API, builds one-click tools for your shop, and tells you what it did.</p>
          <ul className="mt-2 flex flex-col gap-1.5">
            <Bullet icon={<IconSend size={13} />}>Ask in plain words - or hold a key and just talk</Bullet>
            <Bullet icon={<IconMarkup size={13} />}>Point at what you mean: select it, draw on it, show it the view</Bullet>
            <Bullet icon={<IconUndo size={13} />}>Every change is one Ctrl+Z</Bullet>
          </ul>
        </>
      ),
    },
    {
      id: "key",
      when: () => !hasAnyKey,
      icon: <IconKey size={22} />,
      title: "Connect an AI provider",
      body: <ApiKeyStep />,
    },
    {
      id: "ask",
      target: "composer",
      icon: <IconSend size={20} />,
      title: "Ask for anything",
      body: (
        <>
          <p>
            Describe a change or ask a question: <Q>add a 2 mm fillet to these edges</Q>, <Q>why won't this rebuild?</Q>, <Q>make a 120 × 80 plate with four M6 holes</Q>.
          </p>
          <p className="mt-1.5">The agent sees the open document, what you've selected and your view. Messages you type while it works wait in line - or reach it at its next step with Send now.</p>
        </>
      ),
    },
    {
      id: "point",
      target: "markup",
      icon: <IconMarkup size={20} />,
      title: "Point at what you mean",
      body: (
        <>
          <p>
            Select faces, edges or features before you ask and say <Q>this</Q> - each one becomes a reference like <b>[S1]</b> the agent acts on exactly, even after you click elsewhere.
          </p>
          <p className="mt-1.5">To show more, draw on the view with the pen: circle it, arrow it, scribble. The pen stays on while you turn the model and keeps every view you mark. Esc when you're done.</p>
        </>
      ),
    },
    {
      id: "voice",
      icon: <IconMic size={22} />,
      title: "Or just talk",
      body: (
        <>
          <p>
            Hold <b>{talkKey}</b> and speak while you select, sketch and turn the model. Your words appear as you say them, and what you did goes along with them, in order - so <Q>this face, about here</Q> means what it should.
          </p>
          <p className="mt-1.5">Speech is recognised on your PC. You'll be asked whether to set it up when the tour ends.</p>
        </>
      ),
    },
    {
      id: "model",
      target: "model",
      icon: <IconAgent size={20} />,
      title: "Choose how hard it thinks",
      body: <p>{defaultModel} at {defaultEffort} is the default for modelling. Lower effort is quicker and cheaper for simple questions; Max is for the hardest problems. Each chat remembers its own choice.</p>,
    },
    {
      id: "tools",
      target: "tools",
      icon: <IconToolbox size={20} />,
      title: "Tools and workflows",
      body: (
        <>
          <p>Ask the agent to build a tool and it becomes a button on the SolidWorks Agent ribbon tab - run it on a selection or give it a hotkey.</p>
          <p className="mt-1.5">
            Built in: <b>Smart Rebuild</b> for deep design changes, and <b>Watch me</b>, which records what you do and turns it into a tool.
          </p>
        </>
      ),
    },
    {
      id: "control",
      target: "options",
      icon: <IconShield size={20} />,
      title: "You stay in control",
      body: (
        <>
          <p>Code that changes the model asks first (you can change that here in Options). Every change is a single Ctrl+Z, and <b>Undo all agent changes</b> rolls back a whole answer.</p>
          <p className="mt-1.5">Chats follow you from file to file - opening or switching documents never switches the chat - and the chat list shows which files each one worked in.</p>
        </>
      ),
    },
    {
      id: "done",
      icon: <IconOk size={22} />,
      title: "You're ready",
      body: <DoneStep />,
    },
  ].filter((s) => !s.when || s.when());

  const index = step === null ? -1 : Math.min(step, steps.length - 1);
  const current = index >= 0 ? steps[index] : null;
  const last = index === steps.length - 1;
  const next = () => (last ? endTour() : setTourStep(index + 1));
  const back = () => index > 0 && setTourStep(index - 1);

  useEffect(() => {
    if (!current) return;
    const onKey = (e: KeyboardEvent) => {
      if ((e.target as HTMLElement)?.tagName === "INPUT") return;
      if (e.key === "Escape") endTour();
      else if (e.key === "ArrowRight" || e.key === "Enter") next();
      else if (e.key === "ArrowLeft") back();
      else return;
      e.preventDefault();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  });

  const rect = useTargetRect(current?.target);
  if (!current) return null;

  return (
    <div className="fixed inset-0 z-[60]" role="dialog" aria-modal aria-label="SolidWorks Agent tour">
      {/* The dimmed panel, with the step's control cut out of it (an even-odd path: robust where huge box-shadows aren't). */}
      <svg className="absolute inset-0 h-full w-full" aria-hidden>
        <path
          fillRule="evenodd"
          fill="rgba(8,12,18,0.62)"
          d={
            `M0 0H${window.innerWidth}V${window.innerHeight}H0Z` +
            (rect ? ` M${rect.left - 4} ${rect.top - 4}h${rect.width + 8}v${rect.height + 8}h${-(rect.width + 8)}Z` : "")
          }
        />
      </svg>
      {rect && (
        <div
          className="pointer-events-none absolute rounded-[4px] ring-2 ring-accent shadow-[0_0_14px_var(--accent)]"
          style={{ left: rect.left - 4, top: rect.top - 4, width: rect.width + 8, height: rect.height + 8 }}
        />
      )}
      <Card rect={rect} key={current.id}>
        <div className="flex items-start gap-2.5">
          <span className="mt-0.5 flex h-8 w-8 shrink-0 items-center justify-center rounded-[6px] border border-line bg-card-2 text-accent">{current.icon}</span>
          <div className="min-w-0 flex-1">
            <div className="text-[10.5px] uppercase tracking-wide text-text-3">
              {index === 0 ? "Quick tour · about a minute" : `Step ${index} of ${steps.length - 1}`}
            </div>
            <h2 className="text-[14px] font-semibold leading-tight text-text">{current.title}</h2>
          </div>
        </div>
        <div className="mt-2 text-[12px] leading-[1.45] text-text-2">{current.body}</div>
        <div className="mt-3 flex items-center gap-1.5">
          <div className="flex flex-1 items-center gap-1" aria-hidden>
            {steps.map((s, i) => (
              <button
                key={s.id}
                type="button"
                tabIndex={-1}
                onClick={() => setTourStep(i)}
                className={`h-1.5 rounded-full transition-all ${i === index ? "w-4 bg-accent" : i < index ? "w-1.5 bg-accent/60" : "w-1.5 bg-line-strong"}`}
              />
            ))}
          </div>
          {!last && (
            <button type="button" onClick={endTour} className="px-1 text-[11px] text-text-3 hover:text-text" title="Skip the tour (Esc). Options > Take the tour shows it again.">
              Skip tour
            </button>
          )}
          {index > 0 && !last && (
            <button type="button" onClick={back} className="sw-btn !h-6 !px-2">
              Back
            </button>
          )}
          <button type="button" onClick={next} className="sw-btn sw-btn-primary !h-6 !px-3" autoFocus>
            {index === 0 ? "Show me around" : last ? "Finish" : "Next"}
          </button>
        </div>
      </Card>
    </div>
  );
}

/** The step's card: under or over the spotlighted control (whichever side has room), or in the middle. */
function Card({ rect, children }: { rect: DOMRect | null; children: ReactNode }) {
  const ref = useRef<HTMLDivElement>(null);
  const [pos, setPos] = useState<{ left: number; top: number } | null>(null);
  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;
    const w = el.offsetWidth;
    const h = el.offsetHeight;
    const vw = window.innerWidth;
    const vh = window.innerHeight;
    if (!rect) {
      setPos({ left: Math.max(8, (vw - w) / 2), top: Math.max(8, (vh - h) / 2) });
      return;
    }
    const below = rect.bottom + 12;
    const above = rect.top - 12 - h;
    const top = below + h <= vh - 8 ? below : above >= 8 ? above : Math.max(8, vh - h - 8);
    const left = Math.min(Math.max(8, rect.left + rect.width / 2 - w / 2), vw - w - 8);
    setPos({ left, top });
  }, [rect]);
  return (
    <div
      ref={ref}
      className="tour-card absolute w-[min(360px,calc(100vw-16px))] rounded-[6px] border border-line-strong bg-card p-3 shadow-[0_10px_34px_rgba(0,0,0,0.45)]"
      style={{ left: pos?.left ?? 0, top: pos?.top ?? 0, visibility: pos ? "visible" : "hidden" }}
    >
      {children}
    </div>
  );
}

/** Where the step's control is on screen, kept up to date while the panel resizes. */
function useTargetRect(target: string | undefined): DOMRect | null {
  const [rect, setRect] = useState<DOMRect | null>(null);
  useEffect(() => {
    if (!target) {
      setRect(null);
      return;
    }
    const measure = () => {
      const el = document.querySelector(`[data-tour="${target}"]`);
      const r = el?.getBoundingClientRect();
      setRect(r && r.width > 0 && r.height > 0 ? r : null);
    };
    measure();
    const id = window.setInterval(measure, 400); // cheap, and catches layout changes a resize observer would miss
    window.addEventListener("resize", measure);
    return () => {
      clearInterval(id);
      window.removeEventListener("resize", measure);
    };
  }, [target]);
  return rect;
}

function Bullet({ icon, children }: { icon: ReactNode; children: ReactNode }) {
  return (
    <li className="flex items-center gap-2">
      <span className="flex h-5 w-5 shrink-0 items-center justify-center rounded-[4px] bg-card-2">{icon}</span>
      <span className="text-text">{children}</span>
    </li>
  );
}

function Q({ children }: { children: ReactNode }) {
  return <span className="text-text">“{children}”</span>;
}

function ApiKeyStep() {
  const { setApiKey } = useStore.getState();
  const saved = useStore(anyKey);
  const [picked, setPicked] = useState<ProviderId>("anthropic");
  const [key, setKey] = useState("");
  const p = getProvider(picked);
  if (saved) {
    return (
      <p className="flex items-center gap-1.5 text-text">
        <IconOk size={13} /> Key saved - encrypted for your Windows account, never stored in your files.
      </p>
    );
  }
  return (
    <>
      <p>The agent runs on Claude or OpenAI models. Pick one and paste an API key - it's encrypted for your Windows account and never stored in your files.</p>
      <div className="mt-2 flex gap-1" role="radiogroup" aria-label="AI provider">
        {PROVIDERS.map((x) => (
          <button
            key={x.id}
            type="button"
            role="radio"
            aria-checked={picked === x.id}
            onClick={() => setPicked(x.id)}
            className={`sw-btn !h-6 flex-1 justify-center ${picked === x.id ? "sw-btn-primary" : ""}`}
          >
            {providerTitle(x)}
          </button>
        ))}
      </div>
      <form
        className="mt-1.5 flex gap-1"
        onSubmit={async (e) => {
          e.preventDefault();
          if (key.trim()) await setApiKey(picked, key.trim());
        }}
      >
        <input
          type="password"
          value={key}
          onChange={(e) => setKey(e.target.value)}
          placeholder={p.keyPlaceholder}
          autoComplete="off"
          spellCheck={false}
          aria-label={`${providerTitle(p)} API key`}
          className="sw-input h-[24px] min-w-0 flex-1 px-1.5 font-mono text-[11.5px]"
        />
        <button type="submit" disabled={!key.trim()} className="sw-btn sw-btn-primary !h-6">
          Save
        </button>
      </form>
      <button type="button" className="mt-1.5 text-[11px] text-accent hover:underline" onClick={() => rpc("app.openExternal", { url: p.keysUrl })}>
        Get a key…
      </button>
      <span className="ml-2 text-[11px] text-text-3">or add it later in Options</span>
    </>
  );
}

function DoneStep() {
  const hasDoc = useStore((s) => !!s.docs[s.activeDocKey ?? ""]);
  const { endTour } = useStore.getState();
  const tryIt = (text: string) => {
    endTour();
    useStore.setState((s) => ({ drafts: { ...s.drafts, [draftKey(s)]: text }, focusTick: s.focusTick + 1 }));
  };
  return (
    <>
      <p>Try one of these{hasDoc ? "" : " once a part is open"} - it goes in the message box for you to send:</p>
      <div className="mt-2 flex flex-col gap-1">
        {STARTERS.map((s) => (
          <button key={s} type="button" onClick={() => tryIt(s)} className="sw-btn !h-auto justify-start whitespace-normal !py-1 text-left text-[11.5px]">
            {s}
          </button>
        ))}
      </div>
      <p className="mt-2 text-[11px] text-text-3">Options › Take the tour shows this again.</p>
    </>
  );
}
