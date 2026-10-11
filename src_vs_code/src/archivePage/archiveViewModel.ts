import { readEnum } from '../client/enumValue';
import type { ViewLevel } from '../panel/view';
import { localMinuteOf, sizeText } from '../text/format';
import { safeText } from '../text/safeText';
import type { ArchiveAgent, ArchiveEntryRow, ArchiveLine, ArchiveMonth, ArchivePageState, ArchivePageView } from './archiveView';

/**
 * The Archive page's view model (plan §15s D6, D9, E10.S2a): the host's state — the `archive list --json` answer it read, the
 * months it holds open, the paths toggle — laid out as the page shows it. Pure. The daemon answers ENTRIES only (agent, month,
 * key, status, verified, files, bytes, archived at), so this module groups them by agent and month in the order the daemon sorted
 * them (`ArchiveList.List`: agent, month, key) and counts and sums each group's own entries; nothing else is computed, and every
 * daemon string passes `safeText`.
 *
 * <p>A session path holds a project's folder name, so it reads SHORT (`…/<last segment>`) until the person presses *Show full
 * session paths* — the owner's rule that arguments are never shown in full by default, carried to session paths.</p>
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

/** Everything the view is built from — the host's state, nothing the page holds. */
export interface ArchiveHostState {
  readonly list: ListState;
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
}

/** D9: the page shows this distribution's archive only. */
export const ARCHIVE_SCOPE = "This distribution's archive only — the Windows archive is shown by a later version.";

export const HEADERS: readonly string[] = ['Session', 'Files', 'Size', 'Status', 'Verified', 'Archived'];

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

/** What A20 (`RestoreAction.Restorable`) and `archive restore` take: an entry whose source is gone, whole or in part. */
const RESTORABLE: ReadonlySet<string> = new Set(['sourceRemoved', 'split']);

export const UNVERIFIED_RESTORE = 'no — restore it in a terminal with --accept-unverified';

function isBody(value: unknown): value is Body {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

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
  return Array.isArray(body.entries) ? body.entries.filter(isBody) : [];
}

/** Where each run of equal keys starts, in a list the daemon sorted by that key — one pass, nothing mutated. */
function runStarts<T>(items: readonly T[], keyOf: (item: T) => string): readonly number[] {
  return items.flatMap((item, i) => (i === 0 || keyOf(items[i - 1]) !== keyOf(item) ? [i] : []));
}

/** The runs of equal keys of `items`, each a slice — linear in the list (the code round's #4: no copy per item). */
function runsOf<T>(items: readonly T[], keyOf: (item: T) => string): readonly (readonly T[])[] {
  const starts = runStarts(items, keyOf);

  return starts.map((start, k) => items.slice(start, starts[k + 1] ?? items.length));
}

function groupOf(entries: readonly Body[]): MonthGroup {
  const first = entries[0] ?? {};

  return { agent: text(first, 'agent'), month: text(first, 'month'), entries };
}

/** The answer's entries grouped by agent and month, in the daemon's order (`ArchiveList.List` sorts by agent, month, key) — the months the page's indexes name. */
export function monthsOf(body: Body): readonly MonthGroup[] {
  return runsOf(entriesOf(body), (e) => monthKey(text(e, 'agent'), text(e, 'month'))).map(groupOf);
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

  return RESTORABLE.has(text(entry, 'status')) ? UNVERIFIED_RESTORE : 'no';
}

function figure(value: number | undefined, spell: (n: number) => string): string {
  return value === undefined ? 'unknown' : spell(value);
}

function entryRow(entry: Body, pathsShown: boolean): ArchiveEntryRow {
  const key = text(entry, 'key', KEY_MAX);

  return {
    cells: [pathsShown ? key : shortKey(key), figure(count(entry, 'files'), String), figure(count(entry, 'bytes'), sizeText), statusText(entry), verifiedText(entry), localMinuteOf(text(entry, 'archivedAtUtc'), 'unknown')],
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

function monthOf(group: MonthGroup, index: number, state: ArchiveHostState): ArchiveMonth {
  const expanded = state.expanded.has(monthKey(group.agent, group.month));

  return { index, month: group.month, summary: monthSummary(group.entries), expanded, rows: expanded ? group.entries.map((e) => entryRow(e, state.pathsShown)) : [] };
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

  return runsOf(placed, (p) => p.group.agent).map(agentOf);
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
    scope: ARCHIVE_SCOPE,
    notice: safeText(state.notice, TEXT_MAX),
    noticeLevel: state.notice === '' ? 'none' : state.noticeLevel,
    ...body(state),
    pathsShown: state.pathsShown,
    pathsLabel: 'Show full session paths',
    refreshEnabled: state.list.kind !== 'reading',
    headers: HEADERS,
  };
}
