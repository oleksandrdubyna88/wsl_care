import { readEnum } from '../client/enumValue';
import type { ViewLevel } from '../panel/view';
import { localMinuteOf, sizeText } from '../text/format';
import { safeText } from '../text/safeText';
import { isRestorableStatus, listEntries, selectableId } from './archiveSelection';
import type { ArchiveAgent, ArchiveEntryRow, ArchiveLine, ArchiveMonth, ArchivePageState, ArchivePageView, ArchiveSelectionView } from './archiveView';

/**
 * The Archive page's view model (plan §15s D6, D9, E10.S2a): the host's state — the `archive list --json` answer it read, the
 * months it holds open, the paths toggle — laid out as the page shows it. Pure. The daemon answers ENTRIES only (agent, month,
 * key, status, verified, files, bytes, archived at), so this module groups them by agent and month in the order the daemon sorted
 * them (`ArchiveList.List`: agent, month, key) and counts and sums each group's own entries; nothing else is computed, and every
 * daemon string passes `safeText`.
 *
 * <p>A session path holds a project's folder name, so it reads SHORT (`…/<last segment>`) until the person presses *Show full
 * session paths* — the owner's rule that arguments are never shown in full by default, carried to session paths.</p>
 *
 * <p>E10.S2b: each row says whether it may be selected for a restore and whether it is (`archiveSelection.ts` decides), each month
 * whether *Select month* may be pressed, and the selection bar what is selected, the ceiling, and the restore line — so the page
 * still computes nothing.</p>
 */

type Body = Readonly<Record<string, unknown>>;

/** The list read as the host holds it. */
export type ListState =
  | { readonly kind: 'idle' }
  | { readonly kind: 'reading' }
  | { readonly kind: 'read'; readonly distro: string; readonly body: Body }
  | { readonly kind: 'failed'; readonly sentence: string }
  /** The daemon does not advertise `archive.list`: it is not asked. */
  | { readonly kind: 'unsupported'; readonly reason: string };

/** What the cleanup host says of a restore (E10.S2b) — handed to the page's host through `ArchiveControllerOptions.restore`. */
export interface RestoreFacts {
  /** A restore runs (`status.running` or this window's journal holds a run of A20 alone). */
  readonly inFlight: boolean;
  /** The cleanup controls' words while it runs — "Restoring… archived sessions". */
  readonly state: string;
  /** Why *Restore* cannot start now, or '' (`CleanupHost.restoreBlocker`). */
  readonly blocker: string;
}

/** Everything the view is built from — the host's state, nothing the page holds. */
export interface ArchiveHostState {
  readonly list: ListState;
  /** The generation of the held list — what the page's index messages must carry back (E10.S2b). */
  readonly generation: number;
  /** The selected entry ids (E10.S2b). */
  readonly selection: ReadonlySet<string>;
  /** How many entries ONE restore takes now, or `undefined` when the list does not say. */
  readonly ceiling: number | undefined;
  readonly restore: RestoreFacts;
  /** The open months, by `monthKey(agent, month)` — so a refreshed list keeps what was open. */
  readonly expanded: ReadonlySet<string>;
  readonly pathsShown: boolean;
  readonly notice: string;
  readonly noticeLevel: ViewLevel;
}

/** One month of one agent, as the answer listed it. */
export interface MonthGroup {
  readonly agent: string;
  readonly month: string;
  readonly entries: readonly Body[];
  /** The index of its first entry in the answer's entries — a row's index is `start + i`. */
  readonly start: number;
}

/** D9: the page shows this distribution's archive only. */
export const ARCHIVE_SCOPE = "This distribution's archive only — the Windows archive is shown by a later version.";

/** The entry table's columns: the six cells of a row, then its *Select* toggle (E10.S2b — a row that cannot be restored has none). */
export const HEADERS: readonly string[] = ['Session', 'Files', 'Size', 'Status', 'Verified', 'Archived', 'Restore'];

const TEXT_MAX = 300;
const KEY_MAX = 400;

/** The daemon's index events (`ArchiveIndex.Events`) — an entry's status; any other value reads "unknown (<value>)". */
const STATUSES = ['archived', 'sourceRemoved', 'split', 'restored', 'superseded', 'damaged', 'recovered'] as const;

type Status = (typeof STATUSES)[number];

const STATUS_WORDS: { readonly [S in Status]: string } = {
  archived: 'archived — still at its source',
  sourceRemoved: 'archived — removed at its source',
  split: 'archived — partly back at its source',
  restored: 'restored',
  superseded: 'superseded by a later copy',
  damaged: 'damaged copy',
  recovered: 'recovered by a scan',
};

export const UNVERIFIED_RESTORE = 'no — restore it in a terminal with --accept-unverified';

function text(body: Body, key: string, max = TEXT_MAX): string {
  const value = body[key];

  return typeof value === 'string' ? safeText(value, max) : '';
}

function count(body: Body, key: string): number | undefined {
  const value = body[key];

  return typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value : undefined;
}

/** The key the host keeps an open month under — agent and month, never an index (a refreshed list moves the indexes). */
export function monthKey(agent: string, month: string): string {
  return `${agent}\u0000${month}`;
}

function entriesOf(body: Body): readonly Body[] {
  return listEntries(body);
}

/** Where each run of equal keys starts, in a list the daemon sorted by that key — one pass, nothing mutated. */
function runStarts<T>(items: readonly T[], keyOf: (item: T) => string): readonly number[] {
  return items.flatMap((item, i) => (i === 0 || keyOf(items[i - 1]) !== keyOf(item) ? [i] : []));
}

/** The runs of equal keys of `items`, each a slice with where it starts — linear in the list (the code round's #4: no copy per item). */
function runsOf<T>(items: readonly T[], keyOf: (item: T) => string): readonly { readonly start: number; readonly run: readonly T[] }[] {
  const starts = runStarts(items, keyOf);

  return starts.map((start, k) => ({ start, run: items.slice(start, starts[k + 1] ?? items.length) }));
}

function groupOf(entries: readonly Body[], start: number): MonthGroup {
  const first = entries[0] ?? {};

  return { agent: text(first, 'agent'), month: text(first, 'month'), entries, start };
}

/** The answer's entries grouped by agent and month, in the daemon's order (`ArchiveList.List` sorts by agent, month, key) — the months the page's indexes name. */
export function monthsOf(body: Body): readonly MonthGroup[] {
  return runsOf(entriesOf(body), (e) => monthKey(text(e, 'agent'), text(e, 'month'))).map(({ start, run }) => groupOf(run, start));
}

// ---- one entry ----

/** `…/<last segment>` — or the key itself when it has no folder. */
export function shortKey(key: string): string {
  const cut = Math.max(key.lastIndexOf('/'), key.lastIndexOf('\\'));

  return cut < 0 ? key : `…/${key.slice(cut + 1)}`;
}

function statusText(entry: Body): string {
  const status = readEnum(entry.status, STATUSES);

  return status.kind === 'known' ? STATUS_WORDS[status.value] : status.label;
}

/** Verified as the daemon said — a missing flag is "unknown", never "no" (own review #4: absent is not false). */
function verifiedText(entry: Body): string {
  if (typeof entry.verified !== 'boolean') {
    return 'unknown';
  }
  if (entry.verified) {
    return 'yes';
  }

  return isRestorableStatus(entry.status) ? UNVERIFIED_RESTORE : 'no';
}

function figure(value: number | undefined, spell: (n: number) => string): string {
  return value === undefined ? 'unknown' : spell(value);
}

function entryRow(entry: Body, index: number, state: ArchiveHostState): ArchiveEntryRow {
  const key = text(entry, 'key', KEY_MAX);
  const id = selectableId(entry);

  return {
    index,
    cells: [state.pathsShown ? key : shortKey(key), figure(count(entry, 'files'), String), figure(count(entry, 'bytes'), sizeText), statusText(entry), verifiedText(entry), localMinuteOf(text(entry, 'archivedAtUtc'), 'unknown')],
    selectable: id !== undefined,
    selected: id !== undefined && state.selection.has(id),
  };
}

// ---- the groups ----

/** The bytes of `entries` — or `undefined` when one of them has no size (the code round's #3: absent is not zero, never a partial sum). */
function bytesOf(entries: readonly Body[]): number | undefined {
  const sizes = entries.map((e) => count(e, 'bytes'));

  return sizes.every((s) => s !== undefined) ? sizes.reduce<number>((sum, s) => sum + (s ?? 0), 0) : undefined;
}

function sizeOf(entries: readonly Body[]): string {
  const bytes = bytesOf(entries);

  return bytes === undefined ? 'size unknown' : sizeText(bytes);
}

function monthSummary(entries: readonly Body[]): string {
  const verified = entries.filter((e) => e.verified === true).length;

  return `${entries.length} listed · ${verified} verified · ${sizeOf(entries)}`;
}

/** How many more entries the selection takes before one restore is full — 0 without a ceiling or while a restore runs. */
function roomOf(state: ArchiveHostState): number {
  return state.ceiling === undefined || state.restore.inFlight ? 0 : Math.max(0, state.ceiling - state.selection.size);
}

function selectableLeft(group: MonthGroup, selection: ReadonlySet<string>): boolean {
  return group.entries.some((e) => { const id = selectableId(e); return id !== undefined && !selection.has(id); });
}

function monthOf(group: MonthGroup, index: number, state: ArchiveHostState): ArchiveMonth {
  const expanded = state.expanded.has(monthKey(group.agent, group.month));

  return {
    index, month: group.month, summary: monthSummary(group.entries), expanded,
    selectEnabled: roomOf(state) > 0 && selectableLeft(group, state.selection),
    rows: expanded ? group.entries.map((e, i) => entryRow(e, group.start + i, state)) : [],
  };
}

/** One month as the view shows it, beside the group it was built from. */
interface Placed {
  readonly group: MonthGroup;
  readonly month: ArchiveMonth;
}

function agentOf(run: readonly Placed[]): ArchiveAgent {
  const months = run.length;

  return {
    agent: run[0]?.group.agent ?? '',
    summary: `${months} month${months === 1 ? '' : 's'} · ${run.reduce((n, p) => n + p.group.entries.length, 0)} listed · ${sizeOf(run.flatMap((p) => p.group.entries))}`,
    months: run.map((p) => p.month),
  };
}

function agentsOf(body: Body, state: ArchiveHostState): readonly ArchiveAgent[] {
  const placed = monthsOf(body).map((group, index): Placed => ({ group, month: monthOf(group, index, state) }));

  return runsOf(placed, (p) => p.group.agent).map(({ run }) => agentOf(run));
}

// ---- the selection (E10.S2b) ----

const NO_SELECTION: ArchiveSelectionView = { count: 0, summary: '', restoreEnabled: false, clearEnabled: false, line: '', lineLevel: 'none' };

function selectionSummary(count: number, ceiling: number | undefined): string {
  if (ceiling === undefined) {
    return 'This daemon does not say how many sessions one restore takes — update the daemon to restore from here.';
  }
  if (count === 0) {
    return `Select verified sessions removed at their source to restore them — one restore takes at most ${ceiling}.`;
  }

  return `${count} selected — one restore takes at most ${ceiling}${count > ceiling ? '; select fewer' : ''}.`;
}

function restoreLine(restore: RestoreFacts): Pick<ArchiveSelectionView, 'line' | 'lineLevel'> {
  if (restore.inFlight) {
    return { line: safeText(restore.state, TEXT_MAX), lineLevel: 'none' };
  }

  return restore.blocker === '' ? { line: '', lineLevel: 'none' } : { line: `Restore is not available: ${safeText(restore.blocker, TEXT_MAX)}.`, lineLevel: 'warn' };
}

function fits(count: number, ceiling: number | undefined): boolean {
  return ceiling !== undefined && count >= 1 && count <= ceiling;
}

function selectionOf(state: ArchiveHostState): ArchiveSelectionView {
  if (state.list.kind !== 'read') {
    return NO_SELECTION;
  }
  const count = state.selection.size;
  const free = !state.restore.inFlight && state.restore.blocker === '';

  return { count, summary: selectionSummary(count, state.ceiling), restoreEnabled: free && fits(count, state.ceiling), clearEnabled: count > 0, ...restoreLine(state.restore) };
}
// ---- the head ----

function linesOf(list: Extract<ListState, { kind: 'read' }>): ArchiveLine[] {
  const body = list.body;
  const base = text(body, 'baseFolder');

  return [
    { id: 'distro', label: 'Distribution', value: safeText(list.distro, 64) },
    { id: 'base', label: 'Archive folder', value: base === '' ? 'none is chosen' : base },
    { id: 'side', label: 'This side', value: `${text(body, 'side') || 'unknown'} (${text(body, 'sideFolder') || 'no side folder'})` },
  ];
}

function outcomeNote(body: Body): string[] {
  const outcome = text(body, 'outcome');

  return outcome === 'done' ? [] : [`The daemon could not list the archive: ${outcome || 'no outcome'}${text(body, 'note') === '' ? '' : ` — ${text(body, 'note')}`}`];
}

/** The answer's own notes — an unreadable month, a changed mount — each the daemon's sentence. */
function ownNotes(body: Body): string[] {
  return Array.isArray(body.notes) ? body.notes.filter((n): n is string => typeof n === 'string').map((n) => safeText(n, TEXT_MAX)) : [];
}

function tornNote(body: Body): string[] {
  const skipped = count(body, 'skippedLines') ?? 0;

  return skipped > 0 ? [`${skipped} index line(s) could not be read and were skipped`] : [];
}

/** A complete answer with no entry — said, so an empty page is never a failure in disguise. */
function emptyNote(body: Body): string[] {
  return entriesOf(body).length === 0 && text(body, 'outcome') === 'done' ? ['Nothing is archived here yet.'] : [];
}

function answerNotes(body: Body): string[] {
  return [...outcomeNote(body), ...ownNotes(body), ...tornNote(body), ...emptyNote(body)];
}

const PENDING: { readonly [K in Exclude<ListState['kind'], 'read'>]: (list: Extract<ListState, { kind: K }>) => { state: ArchivePageState; note: string } } = {
  idle: () => ({ state: 'reading', note: 'Not asked yet.' }),
  reading: () => ({ state: 'reading', note: 'Reading the archive…' }),
  failed: (list) => ({ state: 'failed', note: `unavailable — ${safeText(list.sentence, TEXT_MAX)}` }),
  unsupported: (list) => ({ state: 'unavailable', note: safeText(list.reason, TEXT_MAX) }),
};

function pendingOf(list: Exclude<ListState, { kind: 'read' }>): { state: ArchivePageState; note: string } {
  return (PENDING[list.kind] as (l: ListState) => { state: ArchivePageState; note: string })(list);
}

function body(state: ArchiveHostState): Pick<ArchivePageView, 'state' | 'lines' | 'notes' | 'agents'> {
  const list = state.list;
  if (list.kind !== 'read') {
    const pending = pendingOf(list);
    return { state: pending.state, lines: [], notes: [pending.note], agents: [] };
  }

  return { state: 'answered', lines: linesOf(list), notes: answerNotes(list.body), agents: agentsOf(list.body, state) };
}

export function buildArchiveView(state: ArchiveHostState): ArchivePageView {
  return {
    heading: 'AI OS Care — Archive',
    generation: state.generation,
    scope: ARCHIVE_SCOPE,
    notice: safeText(state.notice, TEXT_MAX),
    noticeLevel: state.notice === '' ? 'none' : state.noticeLevel,
    ...body(state),
    pathsShown: state.pathsShown,
    pathsLabel: 'Show full session paths',
    refreshEnabled: state.list.kind !== 'reading',
    headers: HEADERS,
    selection: selectionOf(state),
  };
}
