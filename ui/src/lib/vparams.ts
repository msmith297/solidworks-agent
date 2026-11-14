/**
 * Virtual parameters (docs/virtual-parameters.md): named design values stored in each part or assembly (the add-in's
 * vparams.table). The table edits wait as one chip and go to the agent, which changes the model and records the new values.
 * Pure, so the panel checks can run it.
 */

export interface VParamRow {
  name: string;
  type: "length" | "angle" | "count" | "number" | "text" | "choice" | string;
  value: string;
  unit: string;
  note: string;
  drives: string[];
  options: string[];
  links: { part: string; param: string }[];
  updated: string;
  /** Drives what the selection touches (the P key). */
  match?: boolean;
}

export interface VParamGroup {
  scope: "assembly" | "part";
  title: string;
  document: string;
  components: string[];
  /** Something in this file is selected. */
  selected: boolean;
  params: VParamRow[];
}

/** vparams.table: the document's parameters and, in an assembly, each part's. */
export interface VParamTable {
  document: string | null;
  kind?: "part" | "assembly";
  /** Opened from the selection (P): rows that drive it are marked. */
  filtered?: boolean;
  /** "2 faces, 1 edge". */
  selection?: string | null;
  groups: VParamGroup[];
}

/** One change in the table. */
export interface VParamEdit {
  document: string;
  name: string;
  from: string;
  to: string;
  unit: string;
}

export const VPARAM_TAG = "<vparam_edits";

export const vparamKey = (document: string, name: string) => `${document}|${name}`;

/** The edits with this one in place of an earlier edit of the same parameter; back to its old value takes it off. */
export function mergeVParamEdit(edits: VParamEdit[], edit: VParamEdit): VParamEdit[] {
  const rest = edits.filter((e) => vparamKey(e.document, e.name) !== vparamKey(edit.document, edit.name));
  return edit.to.trim() === edit.from.trim() ? rest : [...rest, edit];
}

export function vparamEditsName(edits: VParamEdit[]): string {
  return `${edits.length} parameter change${edits.length === 1 ? "" : "s"}`;
}

const withUnit = (v: string, unit: string) => (unit ? `${v} ${unit}` : v);

/** One edit in words: "Rail Collar.SLDPRT · Wall thickness: 20 mm → 18 mm". */
export function vparamEditLine(e: VParamEdit): string {
  return `${e.document} · ${e.name}: ${withUnit(e.from, e.unit)} → ${withUnit(e.to.trim(), e.unit)}`;
}

/** The edits for the agent. */
export function vparamEditsText(edits: VParamEdit[]): string {
  return (
    `${VPARAM_TAG} count="${edits.length}">\n` +
    "The user changed these virtual parameters in the table. Change the model so each one holds (rebuild what it drives, keep the design intent), verify, then record the new values with vparams set. If one can't work, say why and keep its old value.\n" +
    `${edits.map((e, i) => `${i + 1}. ${vparamEditLine(e)}`).join("\n")}\n</vparam_edits>`
  );
}

/** Rows to show: everything, or with "only the selection" on, the ones that drive it (all when none do). */
export function visibleRows(group: VParamGroup, onlySelection: boolean): VParamRow[] {
  if (!onlySelection) return group.params;
  return group.params.filter((p) => p.match);
}
