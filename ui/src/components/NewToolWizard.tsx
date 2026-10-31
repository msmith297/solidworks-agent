import { useEffect, useRef, useState, type ReactNode } from "react";
import { X } from "lucide-react";
import { Caret, IconHistory, IconSend, IconToolbox, IconWatch } from "./SwIcons";
import { EMPTY, currentThread, useStore, type NewToolSpec } from "../store";

/**
 * "New tool" (ribbon button, command search, Workflows menu): a short PropertyManager-style page instead of a chat
 * prompt. It collects what the user knows up front - what the tool does, where to start from, how it's used, how it
 * should run - and hands it to the agent, which proposes the rest in one questionnaire, builds, tests and saves it.
 */
const EXAMPLES = [
  "Cut flash grooves around the cavity on the selected face",
  "Fillet every sharp edge of the selected body, R1 mm",
  "Add a counterbored hole pattern on the selected face",
  "Shell the part and add ribs under the top face",
];

const USAGE: { id: string; label: string; hint: string }[] = [
  { id: "I select geometry first (faces, edges, a body...)", label: "I select something first", hint: "It works on what's selected when you click it" },
  { id: "show a PropertyManager page with values I can change", label: "Ask me for values", hint: "A PropertyManager page with sizes, counts, options" },
  { id: "show a live preview while I set values", label: "Live preview", hint: "See the result as a hologram before OK" },
  { id: "let me add a note or reference files (images, PDFs, 3D models) each run", label: "Notes / reference files", hint: "Type or dictate instructions, attach references" },
];

const STYLES: { id: NewToolSpec["style"]; label: string; hint: string; build: string }[] = [
  { id: "auto", label: "Recommend one", hint: "The agent suggests a style after hearing what the tool does", build: "" },
  {
    id: "hardcoded",
    label: "Hard coded",
    hint: "The macro does it - fastest and repeatable. Stops on edge cases it wasn't written for.",
    build: "Long to build for complex tasks (tens of minutes)",
  },
  {
    id: "agent",
    label: "Agent in the loop",
    hint: "The tool holds a skill; the agent writes the code each run from your context.",
    build: "Quick to build (minutes); each run takes ~30-90 s",
  },
  {
    id: "hybrid",
    label: "Hybrid",
    hint: "The macro runs first; when it meets something it can't handle, the agent adapts the code - and the tool learns. Most versatile.",
    build: "Longest to build",
  },
];

export function NewToolWizard() {
  const hasChat = useStore((s) => (currentThread(s)?.messages.length ?? 0) > 0);
  const running = useStore((s) => !!s.runs[currentThread(s)?.id ?? ""]);
  const canWatch = useStore((s) => s.docs[s.activeDocKey ?? ""]?.type === "part" || s.docs[s.activeDocKey ?? ""]?.type === "assembly");
  const selection = useStore((s) => s.selection[s.activeDocKey ?? ""] ?? EMPTY);
  const [what, setWhat] = useState("");
  const [from, setFrom] = useState<NewToolSpec["from"]>("describe");
  const [usage, setUsage] = useState<string[]>([]);
  const [style, setStyle] = useState<NewToolSpec["style"]>("auto");
  const [name, setName] = useState("");
  const ref = useRef<HTMLTextAreaElement>(null);

  useEffect(() => ref.current?.focus(), []);

  const close = () => useStore.setState({ panel: "chat" });
  const ready = !running && (what.trim().length > 0 || from !== "describe");
  const start = () => {
    if (!ready) return;
    void useStore.getState().startNewTool({ what, from, usage, style, name });
  };

  const fromOptions: { id: NewToolSpec["from"]; label: string; hint: string; icon: ReactNode; disabled?: string }[] = [
    { id: "describe", label: "Describe it", hint: "Tell the agent what it should do", icon: <IconToolbox size={15} /> },
    {
      id: "chat",
      label: "What we just did",
      hint: "Turn the steps in this chat into a tool",
      icon: <IconHistory size={15} />,
      disabled: hasChat ? undefined : "This chat has nothing to turn into a tool yet",
    },
    {
      id: "watch",
      label: "Watch me do it",
      hint: "Record your own steps, then turn them into a tool",
      icon: <IconWatch size={15} />,
      disabled: canWatch ? undefined : "Open a part or assembly to record",
    },
  ];

  return (
    <div
      className="min-h-0 flex-1 overflow-y-auto bg-bg px-1.5 py-1.5"
      onKeyDown={(e) => {
        if (e.key === "Escape") close();
        if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) start();
      }}
    >
      <div className="zoom-content mx-auto flex max-w-[520px] flex-col gap-1.5">
        <div className="flex items-center gap-1.5 px-1 pb-0.5">
          <IconToolbox size={18} />
          <span className="flex-1 text-[13px] font-semibold text-text">New tool</span>
          <button onClick={close} className="text-text-3 hover:text-text" title="Cancel (Esc)">
            <X size={15} />
          </button>
        </div>

        <Rollout title="What should it do?">
          <textarea
            ref={ref}
            rows={3}
            value={what}
            onChange={(e) => setWhat(e.target.value)}
            placeholder={from === "describe" ? "e.g. Cut flash grooves 0.125 in from the cavity, 0.5 wide, runners every 2 in out to the edge…" : "Anything to add (optional)…"}
            className="sw-input block w-full resize-none px-2 py-1 text-[12px] leading-[1.42]"
          />
          <div className="mt-1.5 flex flex-wrap gap-1">
            {EXAMPLES.map((x) => (
              <button key={x} className="rounded-[10px] border border-line bg-card-2 px-2 py-0.5 text-[11px] text-text-2 hover:border-accent hover:text-text" onClick={() => setWhat(x)}>
                {x}
              </button>
            ))}
          </div>
          <p className="mt-1.5 text-[11px] text-text-3">Rules, sizes and exceptions you mention here make the tool right the first time.</p>
        </Rollout>

        <Rollout title="Start from">
          <div className="grid grid-cols-3 gap-1">
            {fromOptions.map((o) => (
              <button
                key={o.id}
                disabled={!!o.disabled}
                title={o.disabled ?? o.hint}
                onClick={() => setFrom(o.id)}
                className={`flex flex-col items-start gap-0.5 rounded-[3px] border px-1.5 py-1 text-left disabled:opacity-40 ${
                  from === o.id ? "border-accent bg-accent-soft" : "border-line bg-card-2 hover:border-line-strong"
                }`}
              >
                <span className="flex items-center gap-1 text-[11.5px] font-semibold text-text">
                  {o.icon} {o.label}
                </span>
                <span className="text-[10.5px] leading-snug text-text-3">{o.hint}</span>
              </button>
            ))}
          </div>
          {from === "watch" && (
            <p className="mt-1.5 text-[11px] text-text-2">
              Recording starts when you press Start. Model it the way you normally would, then press <b>Stop &amp; attach</b> in the bar and send.
            </p>
          )}
        </Rollout>

        <Rollout title="When I run it" defaultOpen>
          <div className="flex flex-col gap-0.5">
            {USAGE.map((u) => {
              const on = usage.includes(u.id);
              return (
                <label key={u.id} className="flex cursor-pointer items-start gap-1.5 rounded-[2px] px-0.5 py-0.5 hover:bg-card-2" title={u.hint}>
                  <input type="checkbox" className="mt-[3px]" checked={on} onChange={() => setUsage(on ? usage.filter((x) => x !== u.id) : [...usage, u.id])} />
                  <span className="text-[12px] text-text">
                    {u.label} <span className="text-[11px] text-text-3">- {u.hint}</span>
                  </span>
                </label>
              );
            })}
          </div>
          <p className="mt-1 text-[11px] text-text-3">Leave these empty and the agent proposes what fits.</p>
        </Rollout>

        <Rollout title="How it runs">
          <div className="grid grid-cols-2 gap-1">
            {STYLES.map((s) => (
              <button
                key={s.id}
                onClick={() => setStyle(s.id)}
                className={`flex flex-col items-start rounded-[3px] border px-1.5 py-1 text-left ${
                  style === s.id ? "border-accent bg-accent-soft" : "border-line bg-card-2 hover:border-line-strong"
                }`}
              >
                <span className="text-[11.5px] font-semibold text-text">{s.label}</span>
                <span className="text-[10.5px] leading-snug text-text-2">{s.hint}</span>
                {s.build && <span className="mt-0.5 text-[10px] leading-snug text-text-3">{s.build}</span>}
              </button>
            ))}
          </div>
        </Rollout>

        <Rollout title="Name (optional)" defaultOpen={false}>
          <input
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder="Shown on the SolidWorks Agent tab - the agent suggests one if empty"
            className="sw-input h-[22px] w-full px-1.5 text-[12px]"
          />
        </Rollout>

        <div className="flex items-center gap-1.5 px-1 pt-0.5">
          <span className="flex-1 text-[11px] text-text-3">
            {running
              ? "The agent is busy in this chat - wait or stop it first."
              : selection.length > 0
                ? `Your selection (${selection.length} item${selection.length === 1 ? "" : "s"}) goes along as an example.`
                : "The agent asks a few quick questions, builds and tests it, then adds it to the SolidWorks Agent tab."}
          </span>
          <button className="sw-btn" onClick={close}>
            Cancel
          </button>
          <button className="sw-btn sw-btn-primary" disabled={!ready} onClick={start} title="Start building (Ctrl+Enter)">
            {from === "watch" ? <IconWatch size={13} /> : <IconSend size={13} />} {from === "watch" ? "Start recording" : "Start building"}
          </button>
        </div>
      </div>
    </div>
  );
}

function Rollout({ title, children, defaultOpen = true }: { title: string; children: ReactNode; defaultOpen?: boolean }) {
  const [open, setOpen] = useState(defaultOpen);
  return (
    <section className="sw-rollout">
      <button type="button" className="sw-rollout-head" onClick={() => setOpen((o) => !o)} aria-expanded={open}>
        <span className="flex-1 text-[12px]">{title}</span>
        <Caret open={open} className="text-text-3" />
      </button>
      {open && <div className="px-2 py-1.5">{children}</div>}
    </section>
  );
}
