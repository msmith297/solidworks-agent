/**
 * SolidWorks sessions as the panel has seen them (each session = when SolidWorks started), so the chat can mark where
 * earlier sessions end: "Last session", "3 sessions ago", or a date when SolidWorks stayed open for days. Kept in the
 * WebView's local storage (it survives restarts); a missing log just means fewer sessions are counted.
 */
const KEY = "solidworksAgent.sessions";
const MAX = 500;
const DAY = 24 * 60 * 60 * 1000;

function read(): number[] {
  try {
    const v = JSON.parse(localStorage.getItem(KEY) ?? "[]");
    return Array.isArray(v) ? v.filter((x) => typeof x === "number") : [];
  } catch {
    return [];
  }
}

/** Note the current session (called once at start-up). */
export function recordSession(start: number) {
  try {
    const list = read();
    if (!list.includes(start)) localStorage.setItem(KEY, JSON.stringify([...list, start].sort((a, b) => a - b).slice(-MAX)));
  } catch {
    /* storage unavailable: dividers fall back to dates */
  }
}

/** How many sessions have started since `session` (0 = it is the current one). */
export function sessionsSince(session: number, current: number): number {
  const later = new Set(read().filter((s) => s > session));
  if (current > session) later.add(current);
  return later.size;
}

const fmtDate = (ms: number) => new Date(ms).toLocaleDateString(undefined, { month: "short", day: "numeric", year: "numeric" });
const sameDay = (a: number, b: number) => new Date(a).toDateString() === new Date(b).toDateString();

/** Does a divider go between two turns? A different SolidWorks session, or more than a day apart. */
export function isBreak(prev: { at: string; session?: number }, next: { at: string; session?: number }, current: number): boolean {
  if (prev.session && next.session && prev.session !== next.session) return true;
  // Turns without a recorded session (older chats): did SolidWorks start (again) between them?
  const p = Date.parse(prev.at), n = Date.parse(next.at);
  const starts = [...read(), current, ...(next.session ? [next.session] : [])];
  if (!(prev.session && next.session) && starts.some((s) => s > p && s <= n)) return true;
  return n - p > DAY;
}

/** The label for the messages above a divider (their last turn). */
export function breakLabel(above: { at: string; session?: number }, current: number, now = Date.now()): string {
  const at = Date.parse(above.at);
  const date = fmtDate(at);
  // Without a recorded session (older chats), count the sessions that started after these messages.
  const ago = sessionsSince(above.session ?? at, current);
  if (ago === 0) return date; // same SolidWorks session, just days later
  const head = ago === 1 ? "Last session" : `${ago} sessions ago`;
  return sameDay(at, now) ? head : `${head}, on ${date}`;
}
