import { daemonLimitsOf } from '../shared/daemonLimits';
import { ENTRY_ID_SHAPE, MAX_RESTORE_ENTRIES } from '../shared/shapes';

/**
 * The Archive page's restore selection (plan §15s, E10.S2b story 3) — pure, vscode-free, root-free. The host holds the selection
 * by ENTRY ID (the plan round's finding 0: an index moves when the list is read again, an id does not), replaced whole on every
 * change; the page only ever names an entry by its index into the list the host holds, and this module turns that index into the id.
 *
 * - **Selectable** is what A20 restores without a person's terminal decision: `verified === true`, status `sourceRemoved` or
 *   `split` (`RestoreAction.Restorable`), and an entry id of the daemon's shape (`ArchiveIndex.IsEntryId`). An unverified entry is
 *   never selectable — `--accept-unverified` stays a terminal decision (§15r D6).
 * - **The ceiling** is `min(restoreCeiling, status.limits.maxShownNames)` (the plan round's #0): the list's own `restoreCeiling`
 *   (`archive.maxRestoreEntries`) and the longest list the daemon's detached request takes (`act.maxShownNames`). A list without a
 *   readable `restoreCeiling` (an older daemon) has NO ceiling — *Restore* is greyed and the page says to update the daemon.
 */

type Body = Readonly<Record<string, unknown>>;

/** What A20 (`RestoreAction.Restorable`) and `archive restore` take: an entry whose source is gone, whole or in part. */
const RESTORABLE: ReadonlySet<string> = new Set(['sourceRemoved', 'split']);

/** The status is one A20 restores — the one test of it, for the selection and the view model's *Verified* cell. */
export function isRestorableStatus(status: unknown): boolean {
  return typeof status === 'string' && RESTORABLE.has(status);
}

function isBody(value: unknown): value is Body {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** The answer's entries, in the daemon's order — what an entry index names. */
export function listEntries(body: Body): readonly Body[] {
  return Array.isArray(body.entries) ? body.entries.filter(isBody) : [];
}

/** The entry's id when the entry may be selected for a restore — else `undefined`. */
export function selectableId(entry: Body): string | undefined {
  return entry.verified === true && isRestorableStatus(entry.status) ? entryIdText(entry.entryId) : undefined;
}

/** The value when it is an entry id of the daemon's shape. */
function entryIdText(value: unknown): string | undefined {
  return typeof value === 'string' && ENTRY_ID_SHAPE.test(value) ? value : undefined;
}

/** The selectable ids of `entries`, each once, in their order. */
export function selectableIds(entries: readonly Body[]): readonly string[] {
  return [...new Set(entries.flatMap((e) => selectableId(e) ?? []))];
}

/** The list's `restoreCeiling` when it is a whole number the controller takes (1 to `MAX_RESTORE_ENTRIES`), else `undefined`. */
function restoreCeilingOf(body: Body): number | undefined {
  const value = body.restoreCeiling;

  return Number.isInteger(value) && (value as number) >= 1 ? Math.min(value as number, MAX_RESTORE_ENTRIES) : undefined;
}

/** How many entries ONE restore takes now — or `undefined` when the list does not say (an older daemon). */
export function restoreCeiling(list: Body, status: Body | undefined): number | undefined {
  const ceiling = restoreCeilingOf(list);

  return ceiling === undefined ? undefined : Math.min(ceiling, daemonLimitsOf(status).maxShownNames);
}

/** The selection after a new list: the ids it still holds as selectable, and how many were dropped. */
export function keptIn(selection: ReadonlySet<string>, list: Body): { readonly kept: ReadonlySet<string>; readonly dropped: number } {
  const present = new Set(selectableIds(listEntries(list)));
  const kept = new Set([...selection].filter((id) => present.has(id)));

  return { kept, dropped: selection.size - kept.size };
}

function archivedAt(entry: Body): string {
  return typeof entry.archivedAtUtc === 'string' ? entry.archivedAtUtc : '';
}

/** The newest-archived first, ties in the daemon's order — what *Select month* takes when the month does not fit the room left. */
export function newestFirst(entries: readonly Body[]): readonly Body[] {
  const placed = entries.map((entry, i) => ({ entry, i, at: instantOf(entry) }));

  return [...placed].sort((a, b) => (a.at === b.at ? a.i - b.i : b.at - a.at)).map((x) => x.entry);
}

/** The entry's archive instant in ms — an unreadable one sorts last. */
function instantOf(entry: Body): number {
  const at = Date.parse(archivedAt(entry));

  return Number.isNaN(at) ? Number.NEGATIVE_INFINITY : at;
}

/** What *Select month* adds: the month's selectable ids not yet selected — all of them, or the newest that fit `room`. */
export function monthPick(entries: readonly Body[], selection: ReadonlySet<string>, room: number): { readonly added: readonly string[]; readonly offered: number } {
  const open = newestFirst(entries).filter((e) => { const id = selectableId(e); return id !== undefined && !selection.has(id); });
  const ids = selectableIds(open);

  return { added: ids.slice(0, Math.max(0, room)), offered: ids.length };
}
