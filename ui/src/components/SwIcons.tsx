/**
 * Icons drawn in the SolidWorks command-icon idiom: 16 px grid, multicolour with soft vertical gradients,
 * crisp darker outlines, the SolidWorks yellow / blue / grey family, green-check OK and red-cross Cancel.
 */
import { useId, type ReactNode } from "react";

type P = { size?: number; className?: string; title?: string };

const C = {
  yTop: "#FFE07A", yBot: "#F0B429", yLine: "#9A6B00",
  bTop: "#7CC0FF", bBot: "#2F7FD6", bLine: "#164E8C",
  gTop: "#F4F5F7", gBot: "#B9BEC5", gLine: "#5C636B",
  grTop: "#86DC7A", grBot: "#2E9E3E", grLine: "#1B6326",
  rTop: "#FF8A80", rBot: "#D63A3A", rLine: "#8A1C1C",
  paper: "#FFFFFF", paperLine: "#7A828C",
};

function Svg({ size = 16, className, title, children }: P & { children: ReactNode }) {
  return (
    <svg width={size} height={size} viewBox="0 0 16 16" className={className} aria-hidden={title ? undefined : true} role={title ? "img" : undefined} shapeRendering="geometricPrecision">
      {title && <title>{title}</title>}
      {children}
    </svg>
  );
}

function Grad({ id, top, bot }: { id: string; top: string; bot: string }) {
  return (
    <linearGradient id={id} x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stopColor={top} />
      <stop offset="1" stopColor={bot} />
    </linearGradient>
  );
}

function useIds(...names: string[]) {
  const base = useId().replace(/:/g, "");
  return Object.fromEntries(names.map((n) => [n, `${base}${n}`])) as Record<string, string>;
}

/** Sheet of paper with folded corner (base for document-type icons). */
function Paper({ fill = C.paper }: { fill?: string }) {
  return (
    <>
      <path d="M3.5 1.5h6l3 3v10h-9z" fill={fill} stroke={C.paperLine} strokeLinejoin="round" />
      <path d="M9.5 1.5v3h3" fill="#E3E6EA" stroke={C.paperLine} strokeLinejoin="round" />
    </>
  );
}

export function IconPart(p: P) {
  const id = useIds("t", "s", "f");
  return (
    <Svg {...p}>
      <defs>
        <Grad id={id.t} top="#FFF3B8" bot="#FFD95A" />
        <Grad id={id.s} top="#F5B83A" bot="#D18B12" />
        <Grad id={id.f} top="#FFD46B" bot="#E9A320" />
      </defs>
      <path d="M8 1.5 14.5 4.8 8 8.1 1.5 4.8z" fill={`url(#${id.t})`} stroke={C.yLine} strokeLinejoin="round" />
      <path d="M1.5 4.8 8 8.1v6.4L1.5 11.2z" fill={`url(#${id.f})`} stroke={C.yLine} strokeLinejoin="round" />
      <path d="M14.5 4.8 8 8.1v6.4l6.5-3.3z" fill={`url(#${id.s})`} stroke={C.yLine} strokeLinejoin="round" />
    </Svg>
  );
}

export function IconAssembly(p: P) {
  const id = useIds("y", "b");
  return (
    <Svg {...p}>
      <defs>
        <Grad id={id.y} top={C.yTop} bot={C.yBot} />
        <Grad id={id.b} top={C.bTop} bot={C.bBot} />
      </defs>
      <path d="M5 1.5 9.5 3.8 5 6.1.5 3.8z" transform="translate(1 0)" fill={`url(#${id.b})`} stroke={C.bLine} strokeLinejoin="round" />
      <path d="M1.5 3.8 6 6.1v4.6L1.5 8.4z" fill={`url(#${id.b})`} stroke={C.bLine} strokeLinejoin="round" />
      <path d="M10.5 3.8 6 6.1v4.6l4.5-2.3z" fill={C.bBot} stroke={C.bLine} strokeLinejoin="round" />
      <path d="M10 6.5 14.5 8.8 10 11.1 5.5 8.8z" fill={`url(#${id.y})`} stroke={C.yLine} strokeLinejoin="round" />
      <path d="M5.5 8.8 10 11.1v3.4L5.5 12.2z" fill={C.yTop} stroke={C.yLine} strokeLinejoin="round" />
      <path d="M14.5 8.8 10 11.1v3.4l4.5-2.3z" fill={C.yBot} stroke={C.yLine} strokeLinejoin="round" />
    </Svg>
  );
}

export function IconDrawing(p: P) {
  return (
    <Svg {...p}>
      <Paper />
      <rect x="5" y="6" width="6" height="7" fill="none" stroke={C.bBot} strokeWidth="0.9" />
      <path d="M5 10.5h6M8 6v7" stroke={C.bBot} strokeWidth="0.7" />
    </Svg>
  );
}

/** Run Macro: a C# sheet with the green play triangle. */
export function IconMacro(p: P) {
  const id = useIds("g");
  return (
    <Svg {...p}>
      <defs>
        <Grad id={id.g} top={C.grTop} bot={C.grBot} />
      </defs>
      <Paper />
      <path d="M5 5.5h3M5 7.5h4.5M5 9.5h2.5" stroke="#9AA3AD" strokeWidth="0.9" strokeLinecap="round" />
      <path d="M8.5 9 15 12.25 8.5 15.5z" fill={`url(#${id.g})`} stroke={C.grLine} strokeLinejoin="round" />
    </Svg>
  );
}

export function IconCamera(p: P) {
  const id = useIds("b", "l");
  return (
    <Svg {...p}>
      <defs>
        <Grad id={id.b} top={C.gTop} bot={C.gBot} />
        <radialGradient id={id.l} cx="0.35" cy="0.35" r="0.75">
          <stop offset="0" stopColor="#BFE3FF" />
          <stop offset="1" stopColor={C.bBot} />
        </radialGradient>
      </defs>
      <path d="M5.5 3.5 6.5 2h3l1 1.5" fill={C.gBot} stroke={C.gLine} strokeLinejoin="round" />
      <rect x="1.5" y="3.5" width="13" height="10" rx="1.5" fill={`url(#${id.b})`} stroke={C.gLine} />
      <circle cx="8" cy="8.5" r="3.2" fill={`url(#${id.l})`} stroke={C.bLine} />
      <circle cx="12.3" cy="5.4" r="0.7" fill="#E0484F" />
    </Svg>
  );
}

/** FeatureManager design tree. */
export function IconTree(p: P) {
  const id = useIds("y", "b");
  return (
    <Svg {...p}>
      <defs>
        <Grad id={id.y} top={C.yTop} bot={C.yBot} />
        <Grad id={id.b} top={C.bTop} bot={C.bBot} />
      </defs>
      <path d="M3.5 4.5v9M3.5 8.5h3M3.5 12.5h3" stroke={C.gLine} strokeWidth="1" fill="none" />
      <rect x="1" y="1" width="5" height="4" rx="0.6" fill={`url(#${id.y})`} stroke={C.yLine} />
      <rect x="7" y="6.5" width="7.5" height="4" rx="0.6" fill={`url(#${id.b})`} stroke={C.bLine} />
      <rect x="7" y="10.5" width="7.5" height="4" rx="0.6" fill="#D6DADF" stroke={C.gLine} />
    </Svg>
  );
}

export function IconInfo(p: P) {
  const id = useIds("b");
  return (
    <Svg {...p}>
      <defs>
        <Grad id={id.b} top={C.bTop} bot={C.bBot} />
      </defs>
      <circle cx="8" cy="8" r="6.5" fill={`url(#${id.b})`} stroke={C.bLine} />
      <circle cx="8" cy="4.9" r="1.05" fill="#fff" />
      <path d="M8 7.2v4.6" stroke="#fff" strokeWidth="1.9" strokeLinecap="round" />
    </Svg>
  );
}

/** Selection: highlighted face with the pointer. */
export function IconSelect(p: P) {
  return (
    <Svg {...p}>
      <path d="M1.5 9.5 7 6.5l6 2.5-5.5 3.5z" fill="#7EC3FF" fillOpacity="0.85" stroke={C.bLine} strokeLinejoin="round" />
      <path d="M6.5 1.5v10.2l2.4-2.3 1.7 3.8 1.6-.7-1.7-3.7h3.3z" fill="#fff" stroke="#30353B" strokeLinejoin="round" />
    </Svg>
  );
}

export function IconSearch(p: P) {
  const id = useIds("l");
  return (
    <Svg {...p}>
      <defs>
        <radialGradient id={id.l} cx="0.35" cy="0.3" r="0.8">
          <stop offset="0" stopColor="#EAF6FF" />
          <stop offset="1" stopColor="#8CC8FF" />
        </radialGradient>
      </defs>
      <path d="M10 10l4.5 4.5" stroke="#6B4A1E" strokeWidth="2.6" strokeLinecap="round" />
      <path d="M10 10l4.5 4.5" stroke="#B9874A" strokeWidth="1.2" strokeLinecap="round" />
      <circle cx="6.5" cy="6.5" r="4.7" fill={`url(#${id.l})`} stroke={C.gLine} strokeWidth="1.3" />
    </Svg>
  );
}

export function IconBook(p: P) {
  const id = useIds("b");
  return (
    <Svg {...p}>
      <defs>
        <Grad id={id.b} top={C.bTop} bot={C.bBot} />
      </defs>
      <path d="M3 2.5h9.5v11H3.8A1.3 1.3 0 0 1 2.5 12.2V3A.5.5 0 0 1 3 2.5z" fill={`url(#${id.b})`} stroke={C.bLine} />
      <path d="M3.8 11.5h8.7v2H3.8a1 1 0 0 1 0-2z" fill="#fff" stroke={C.bLine} />
      <path d="M5.5 5h5M5.5 7h3.5" stroke="#fff" strokeWidth="1" strokeLinecap="round" />
    </Svg>
  );
}

export function IconDocs(p: P) {
  return (
    <Svg {...p}>
      <path d="M6.5 0.5h5l2.5 2.5v8.5h-7.5z" fill="#E9ECEF" stroke={C.paperLine} strokeLinejoin="round" />
      <g transform="translate(-3 3)">
        <Paper />
      </g>
    </Svg>
  );
}

export function IconUndo(p: P) {
  const id = useIds("b");
  return (
    <Svg {...p}>
      <defs>
        <Grad id={id.b} top={C.bTop} bot={C.bBot} />
      </defs>
      <path
        d="M5.2 3.2 1.3 6.6l3.9 3.4V7.9h3.9c2 0 3.4 1.2 3.4 2.9 0 1-.4 1.8-1.2 2.5 2.4-.6 3.6-2.3 3.6-4.4 0-2.9-2.3-4.6-5.6-4.6H5.2z"
        fill={`url(#${id.b})`}
        stroke={C.bLine}
        strokeLinejoin="round"
      />
    </Svg>
  );
}

/** Rebuild - the SolidWorks traffic light. */
export function IconRebuild(p: P) {
  const id = useIds("b");
  return (
    <Svg {...p}>
      <defs>
        <Grad id={id.b} top="#5A6068" bot="#2B2F34" />
      </defs>
      <rect x="4.5" y="0.5" width="7" height="15" rx="2" fill={`url(#${id.b})`} stroke="#1D2024" />
      <circle cx="8" cy="3.6" r="1.6" fill="#E5463F" />
      <circle cx="8" cy="8" r="1.6" fill="#F5C33B" />
      <circle cx="8" cy="12.4" r="1.6" fill="#47C25A" />
    </Svg>
  );
}

export function IconQuestion(p: P) {
  const id = useIds("b");
  return (
    <Svg {...p}>
      <defs>
        <Grad id={id.b} top={C.bTop} bot={C.bBot} />
      </defs>
      <path d="M2.5 2h11a1 1 0 0 1 1 1v7.5a1 1 0 0 1-1 1H8l-3.5 3v-3H2.5a1 1 0 0 1-1-1V3a1 1 0 0 1 1-1z" fill={`url(#${id.b})`} stroke={C.bLine} strokeLinejoin="round" />
      <path d="M6.3 5.2c.2-1 1-1.5 1.9-1.5 1 0 1.9.7 1.9 1.6 0 1.3-1.6 1.4-1.6 2.6" fill="none" stroke="#fff" strokeWidth="1.3" strokeLinecap="round" />
      <circle cx="8.5" cy="9.5" r="0.75" fill="#fff" />
    </Svg>
  );
}

/** System Options gear. */
export function IconGear(p: P) {
  const id = useIds("g");
  const teeth = Array.from({ length: 8 }, (_, i) => i * 45);
  return (
    <Svg {...p}>
      <defs>
        <Grad id={id.g} top="#E9ECEF" bot="#9AA1A9" />
      </defs>
      <g stroke={C.gLine} strokeLinejoin="round" fill={`url(#${id.g})`}>
        {teeth.map((a) => (
          <rect key={a} x="6.6" y="0.6" width="2.8" height="3.4" rx="0.5" transform={`rotate(${a} 8 8)`} />
        ))}
        <circle cx="8" cy="8" r="5.2" />
      </g>
      <circle cx="8" cy="8" r="2" fill="#5B8FD0" stroke={C.bLine} />
    </Svg>
  );
}

export function IconNewChat(p: P) {
  const id = useIds("w", "g");
  return (
    <Svg {...p}>
      <defs>
        <Grad id={id.w} top="#FFFFFF" bot="#DCE6F2" />
        <Grad id={id.g} top={C.grTop} bot={C.grBot} />
      </defs>
      <path d="M1.5 2.5h10a1 1 0 0 1 1 1v6a1 1 0 0 1-1 1H6l-3 2.5v-2.5H1.5a1 1 0 0 1-1-1v-6a1 1 0 0 1 1-1z" fill={`url(#${id.w})`} stroke={C.bLine} strokeLinejoin="round" />
      <circle cx="12" cy="11.5" r="3.6" fill={`url(#${id.g})`} stroke={C.grLine} />
      <path d="M12 9.6v3.8M10.1 11.5h3.8" stroke="#fff" strokeWidth="1.4" strokeLinecap="round" />
    </Svg>
  );
}

export function IconHistory(p: P) {
  const id = useIds("r");
  return (
    <Svg {...p}>
      <defs>
        <Grad id={id.r} top={C.bTop} bot={C.bBot} />
      </defs>
      <circle cx="8" cy="8" r="6.6" fill={`url(#${id.r})`} stroke={C.bLine} />
      <circle cx="8" cy="8" r="5" fill="#fff" />
      <path d="M8 4.6V8l2.4 1.5" fill="none" stroke="#30353B" strokeWidth="1.2" strokeLinecap="round" strokeLinejoin="round" />
    </Svg>
  );
}

/** PropertyManager OK. */
export function IconOk(p: P) {
  return (
    <Svg {...p}>
      <path d="M1.8 8.6 6.2 13 14.4 3.4" fill="none" stroke={C.grLine} strokeWidth="3.6" strokeLinecap="round" strokeLinejoin="round" />
      <path d="M1.8 8.6 6.2 13 14.4 3.4" fill="none" stroke="#3FBF52" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round" />
    </Svg>
  );
}

/** PropertyManager Cancel. */
export function IconCancel(p: P) {
  return (
    <Svg {...p}>
      <path d="M3 3l10 10M13 3 3 13" stroke={C.rLine} strokeWidth="3.6" strokeLinecap="round" />
      <path d="M3 3l10 10M13 3 3 13" stroke="#E5463F" strokeWidth="2.2" strokeLinecap="round" />
    </Svg>
  );
}

export function IconWarning(p: P) {
  const id = useIds("y");
  return (
    <Svg {...p}>
      <defs>
        <Grad id={id.y} top={C.yTop} bot={C.yBot} />
      </defs>
      <path d="M8 1.3 15 14.2H1z" fill={`url(#${id.y})`} stroke={C.yLine} strokeLinejoin="round" />
      <path d="M8 5.5v4.2" stroke="#3A2A00" strokeWidth="1.6" strokeLinecap="round" />
      <circle cx="8" cy="11.9" r="0.85" fill="#3A2A00" />
    </Svg>
  );
}

export function IconError(p: P) {
  const id = useIds("r");
  return (
    <Svg {...p}>
      <defs>
        <Grad id={id.r} top={C.rTop} bot={C.rBot} />
      </defs>
      <circle cx="8" cy="8" r="6.6" fill={`url(#${id.r})`} stroke={C.rLine} />
      <path d="M5.5 5.5l5 5M10.5 5.5l-5 5" stroke="#fff" strokeWidth="1.7" strokeLinecap="round" />
    </Svg>
  );
}

export function IconCopy(p: P) {
  return (
    <Svg {...p}>
      <rect x="5.5" y="1.5" width="9" height="10" rx="0.8" fill="#E9ECEF" stroke={C.paperLine} />
      <rect x="1.5" y="4.5" width="9" height="10" rx="0.8" fill="#fff" stroke={C.paperLine} />
      <path d="M3.5 7.5h5M3.5 9.5h5M3.5 11.5h3" stroke="#9AA3AD" strokeWidth="0.9" strokeLinecap="round" />
    </Svg>
  );
}

/** Edit - the yellow pencil. */
export function IconEdit(p: P) {
  const id = useIds("y");
  return (
    <Svg {...p}>
      <defs>
        <linearGradient id={id.y} x1="0" y1="0" x2="1" y2="1">
          <stop offset="0" stopColor={C.yTop} />
          <stop offset="1" stopColor={C.yBot} />
        </linearGradient>
      </defs>
      <path d="M11.2 1.6l3.2 3.2-8.6 8.6-3.2-3.2z" fill={`url(#${id.y})`} stroke={C.yLine} strokeLinejoin="round" />
      <path d="M11.2 1.6l1.5-1.1 2.8 2.8-1.1 1.5z" fill="#E88A8A" stroke="#8A3B3B" strokeLinejoin="round" />
      <path d="M2.6 10.2l3.2 3.2-4.3 1.1z" fill="#F2D3A7" stroke={C.yLine} strokeLinejoin="round" />
      <path d="M1.5 14.5l.6-2.2 1.6 1.6z" fill="#30353B" />
    </Svg>
  );
}

export function IconResend(p: P) {
  const id = useIds("g");
  return (
    <Svg {...p}>
      <defs>
        <Grad id={id.g} top={C.grTop} bot={C.grBot} />
      </defs>
      <path d="M13.6 7.2A5.6 5.6 0 1 0 12 12" fill="none" stroke={C.grLine} strokeWidth="3.4" strokeLinecap="round" />
      <path d="M13.6 7.2A5.6 5.6 0 1 0 12 12" fill="none" stroke={`url(#${id.g})`} strokeWidth="1.9" strokeLinecap="round" />
      <path d="M15.3 3.2l-.6 5.3-4.6-2.6z" fill={C.grBot} stroke={C.grLine} strokeLinejoin="round" />
    </Svg>
  );
}

export function IconKey(p: P) {
  const id = useIds("y");
  return (
    <Svg {...p}>
      <defs>
        <Grad id={id.y} top={C.yTop} bot={C.yBot} />
      </defs>
      <path d="M7.3 8.7 13.5 14.9M11 12.4l1.6-1.6M12.8 14.2l1.6-1.6" stroke={C.yLine} strokeWidth="2.6" strokeLinecap="round" />
      <path d="M7.3 8.7 13.5 14.9M11 12.4l1.6-1.6M12.8 14.2l1.6-1.6" stroke={C.yBot} strokeWidth="1.2" strokeLinecap="round" />
      <circle cx="5" cy="5" r="4" fill={`url(#${id.y})`} stroke={C.yLine} />
      <circle cx="4" cy="4" r="1.1" fill="#fff" stroke={C.yLine} strokeWidth="0.7" />
    </Svg>
  );
}

export function IconShield(p: P) {
  const id = useIds("b");
  return (
    <Svg {...p}>
      <defs>
        <Grad id={id.b} top={C.bTop} bot={C.bBot} />
      </defs>
      <path d="M8 1 14 3.3v4.2c0 3.7-2.6 6.2-6 7.5-3.4-1.3-6-3.8-6-7.5V3.3z" fill={`url(#${id.b})`} stroke={C.bLine} strokeLinejoin="round" />
      <path d="M5.2 8l2 2 3.7-4" fill="none" stroke="#fff" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" />
    </Svg>
  );
}

/** The Agent mark (matches the ribbon icon). */
export function IconAgent(p: P) {
  const id = useIds("b");
  return (
    <Svg {...p}>
      <defs>
        <Grad id={id.b} top="#5FA9F2" bot="#1E6FD9" />
      </defs>
      <path d="M2.5 1.5h11a1.5 1.5 0 0 1 1.5 1.5v7.5a1.5 1.5 0 0 1-1.5 1.5H7.5l-3.8 3v-3H2.5A1.5 1.5 0 0 1 1 10.5V3a1.5 1.5 0 0 1 1.5-1.5z" fill={`url(#${id.b})`} stroke="#124B92" strokeLinejoin="round" />
      <path d="M8 3.4l.9 2.4 2.4.9-2.4.9L8 10l-.9-2.4-2.4-.9 2.4-.9z" fill="#fff" />
    </Svg>
  );
}

export function IconStop(p: P) {
  const id = useIds("r");
  return (
    <Svg {...p}>
      <defs>
        <Grad id={id.r} top={C.rTop} bot={C.rBot} />
      </defs>
      <rect x="2.5" y="2.5" width="11" height="11" rx="1.5" fill={`url(#${id.r})`} stroke={C.rLine} />
    </Svg>
  );
}

export function IconSend(p: P) {
  return (
    <Svg {...p}>
      <path d="M8 13.5V3M3.5 7.5 8 3l4.5 4.5" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
    </Svg>
  );
}

/** SolidWorks rollout caret (points down when expanded). */
export function Caret({ open, className = "" }: { open: boolean; className?: string }) {
  return (
    <svg width="9" height="9" viewBox="0 0 9 9" className={`shrink-0 transition-transform ${open ? "" : "-rotate-90"} ${className}`} aria-hidden>
      <path d="M1 2.5h7L4.5 7z" fill="currentColor" />
    </svg>
  );
}

/** Small spinning progress ring in the accent colour. */
export function Spinner({ size = 14 }: { size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 16 16" className="animate-spin" aria-label="Working">
      <circle cx="8" cy="8" r="6" fill="none" stroke="var(--line-strong)" strokeWidth="2" />
      <path d="M8 2a6 6 0 0 1 6 6" fill="none" stroke="var(--accent)" strokeWidth="2" strokeLinecap="round" />
    </svg>
  );
}

/** Microphone (push-to-talk voice messages). */
export function IconMic(p: P & { live?: boolean }) {
  const { live, ...rest } = p;
  return (
    <Svg {...rest}>
      <rect x="5.5" y="1.5" width="5" height="8.5" rx="2.5" fill={live ? "#E5302A" : "var(--card-2, #eef1f4)"} stroke={live ? "#A31F1A" : "currentColor"} />
      <path d="M3.5 7.5a4.5 4.5 0 0 0 9 0M8 12v2.5M5.5 14.5h5" fill="none" stroke="currentColor" strokeLinecap="round" />
    </Svg>
  );
}

/** Markup pen (draw on the view). */
export function IconMarkup(p: P) {
  const id = useIds("r");
  return (
    <Svg {...p}>
      <defs>
        <linearGradient id={id.r} x1="0" y1="0" x2="1" y2="1">
          <stop offset="0" stopColor={C.rTop} />
          <stop offset="1" stopColor={C.rBot} />
        </linearGradient>
      </defs>
      <path d="M1.5 14.2c1.4-1.6 2.6-1.6 4 0s2.6 1.6 4 0" fill="none" stroke="#2F84D6" strokeWidth="1.4" strokeLinecap="round" />
      <path d="M12.3 2.2l2.3 2.3-6.8 6.8-2.3-2.3z" fill={`url(#${id.r})`} stroke={C.rLine} strokeLinejoin="round" />
      <path d="M5.5 9l2.3 2.3-3.1.8z" fill="#F2D3A7" stroke={C.rLine} strokeLinejoin="round" />
    </Svg>
  );
}

export function IconToolPen(p: P) {
  return (
    <Svg {...p}>
      <path d="M2 12c1.5-4 3-5 5-3s3.5 1 5-4" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" />
    </Svg>
  );
}

export function IconToolArrow(p: P) {
  return (
    <Svg {...p}>
      <path d="M2.5 13.5 12 4" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" />
      <path d="M13.8 2.2 13 8.4 7.6 3z" fill="currentColor" />
    </Svg>
  );
}

export function IconToolCircle(p: P) {
  return (
    <Svg {...p}>
      <ellipse cx="8" cy="8" rx="6" ry="4.6" fill="none" stroke="currentColor" strokeWidth="1.8" />
    </Svg>
  );
}

export function IconToolHighlighter(p: P) {
  return (
    <Svg {...p}>
      <path d="M2 11.5h12" stroke="currentColor" strokeOpacity="0.45" strokeWidth="5" strokeLinecap="round" />
      <path d="M9.5 2.5l3 3-4.5 4.5H5V7z" fill="currentColor" />
    </Svg>
  );
}

export function IconEye(p: P) {
  return (
    <Svg {...p}>
      <path d="M1 8s2.6-4.5 7-4.5S15 8 15 8s-2.6 4.5-7 4.5S1 8 1 8z" fill="#fff" stroke={C.gLine} />
      <circle cx="8" cy="8" r="2.6" fill={C.bBot} stroke={C.bLine} />
      <circle cx="7.2" cy="7.2" r="0.8" fill="#fff" />
    </Svg>
  );
}

/** Toolbox (user tools). */
export function IconToolbox(p: P) {
  const id = useIds("r", "y");
  return (
    <Svg {...p}>
      <defs>
        <Grad id={id.r} top={C.rTop} bot={C.rBot} />
        <Grad id={id.y} top={C.yTop} bot={C.yBot} />
      </defs>
      <path d="M5.5 4.5V3a1 1 0 0 1 1-1h3a1 1 0 0 1 1 1v1.5" fill="none" stroke={C.gLine} strokeWidth="1.2" />
      <rect x="1.5" y="4.5" width="13" height="9.5" rx="1.2" fill={`url(#${id.r})`} stroke={C.rLine} />
      <path d="M1.5 8.5h13" stroke={C.rLine} />
      <rect x="6.5" y="7.3" width="3" height="2.4" rx="0.4" fill={`url(#${id.y})`} stroke={C.yLine} strokeWidth="0.8" />
    </Svg>
  );
}

/** Icon for a user tool by its saved icon name. */
/** Update available: green disc with a down arrow. */
export function IconUpdate(p: P) {
  const id = useIds("g");
  return (
    <Svg {...p}>
      <defs>
        <Grad id={id.g} top="#5DCB6E" bot="#2E9E44" />
      </defs>
      <circle cx="8" cy="8" r="6.5" fill={`url(#${id.g})`} stroke="#1F7A31" />
      <path d="M8 4.2v6M5.4 7.9 8 10.6l2.6-2.7" fill="none" stroke="#fff" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" />
      <path d="M5 12h6" stroke="#fff" strokeWidth="1.2" strokeLinecap="round" opacity="0.85" />
    </Svg>
  );
}

export function ToolIcon({ name, size = 16 }: { name: string; size?: number }) {
  switch (name) {
    case "part": return <IconPart size={size} />;
    case "assembly": return <IconAssembly size={size} />;
    case "drawing": return <IconDrawing size={size} />;
    case "tree": return <IconTree size={size} />;
    case "select": return <IconSelect size={size} />;
    case "camera": return <IconCamera size={size} />;
    case "rebuild": return <IconRebuild size={size} />;
    case "gear": return <IconGear size={size} />;
    case "book": return <IconBook size={size} />;
    case "markup": return <IconMarkup size={size} />;
    case "shield": return <IconShield size={size} />;
    default: return <IconMacro size={size} />;
  }
}

/** Paper clip (attach files). */
export function IconAttach(p: P) {
  return (
    <Svg {...p}>
      <path
        d="M10.8 4.6 5.6 9.8a1.4 1.4 0 0 0 2 2l5.4-5.4a2.6 2.6 0 0 0-3.7-3.7L3.8 8.2a3.8 3.8 0 0 0 5.4 5.4l4.3-4.3"
        fill="none"
        stroke="#8C96A2"
        strokeWidth="1.6"
        strokeLinecap="round"
        strokeLinejoin="round"
      />
    </Svg>
  );
}

/** Eye with a record dot ("Watch me"). Pass recording to make the dot glow. */
export function IconWatch({ recording, ...p }: P & { recording?: boolean }) {
  return (
    <Svg {...p}>
      <path d="M1 7.5s2.4-4 6.5-4 6.5 4 6.5 4-2.4 4-6.5 4S1 7.5 1 7.5z" fill="#fff" stroke={C.gLine} />
      <circle cx="7.5" cy="7.5" r="2.3" fill={C.bBot} stroke={C.bLine} />
      <circle cx="12.6" cy="12.6" r="2.6" fill={recording ? "#E5302A" : "#C9CDD2"} stroke={recording ? C.rLine : C.gLine} />
    </Svg>
  );
}

/** Shaded cube (a 3D model file). */
export function IconModelFile(p: P) {
  const id = useIds("t", "l", "r");
  return (
    <Svg {...p}>
      <defs>
        <Grad id={id.t} top="#E9EEF4" bot="#C7D2DF" />
        <Grad id={id.l} top="#B7C4D3" bot="#8496AB" />
        <Grad id={id.r} top="#94A6BA" bot="#667A91" />
      </defs>
      <path d="M8 1.5 14 4.7 8 8 2 4.7z" fill={`url(#${id.t})`} stroke="#4A5868" strokeLinejoin="round" />
      <path d="M2 4.7 8 8v6.5L2 11.3z" fill={`url(#${id.l})`} stroke="#4A5868" strokeLinejoin="round" />
      <path d="M14 4.7 8 8v6.5l6-3.2z" fill={`url(#${id.r})`} stroke="#4A5868" strokeLinejoin="round" />
    </Svg>
  );
}

/** A plugin: a blue puzzle piece. */
export function IconPlugin(p: P) {
  const id = useIds("b");
  return (
    <Svg {...p}>
      <defs>
        <Grad id={id.b} top={C.bTop} bot={C.bBot} />
      </defs>
      <path
        d="M2 5h3.4a1.7 1.7 0 1 1 3.2 0H12v3.1a1.7 1.7 0 1 1 0 3.2V14.5H2v-3.2a1.7 1.7 0 1 0 0-3.2z"
        fill={`url(#${id.b})`}
        stroke={C.bLine}
        strokeLinejoin="round"
      />
    </Svg>
  );
}

/** Effort: a gauge with its needle. */
export function IconEffort(p: P) {
  const id = useIds("g");
  return (
    <Svg {...p}>
      <defs>
        <Grad id={id.g} top={C.gTop} bot={C.gBot} />
      </defs>
      <path d="M1.5 12a6.5 6.5 0 0 1 13 0z" fill={`url(#${id.g})`} stroke={C.gLine} strokeLinejoin="round" />
      <path d="M4 12a4 4 0 0 1 2-3.46M10 8.54A4 4 0 0 1 12 12" fill="none" stroke="#3FBF52" strokeWidth="1.2" />
      <path d="M8 12 11.2 6.6" stroke={C.rLine} strokeWidth="1.5" strokeLinecap="round" />
      <circle cx="8" cy="12" r="1.3" fill="#30353B" />
    </Svg>
  );
}

/** f(x): the virtual parameters (the ribbon's Parameters button). */
export function IconFx(p: P) {
  return (
    <Svg {...p}>
      <text x="0.6" y="12.6" fontFamily="Cambria, 'Times New Roman', serif" fontStyle="italic" fontWeight="700" fontSize="13" fill={C.bLine}>
        f
      </text>
      <text x="6" y="12" fontFamily="Cambria, 'Times New Roman', serif" fontStyle="italic" fontSize="8.6" fill="currentColor">
        (x)
      </text>
    </Svg>
  );
}
