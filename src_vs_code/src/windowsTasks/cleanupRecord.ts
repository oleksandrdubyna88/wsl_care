import { parseRuns, type RunLine } from '../cleanup/runAnswers';
import type { WindowsCareClient } from '../client/WindowsCareClient';
import type { JsonObject } from '../client/outcome';
import type { WindowsReadOutcome } from '../client/windowsTasks';
import type { LastRun } from '../scheduler/schedulerAnswer';
import type { ViewLevel } from '../shared/viewLevel';
import { noticeText } from '../text/safeText';
import { NORMAL_RESULTS, resultWords } from './resultWords';
import { RUN_TOLERANCE_MS } from './tasksState';

/**
 * The *last cleanup* line of the Windows tasks' part (WR-T3 of PLAN_windows_runner.md §3.12, R2): what the newest run record of the cleanup (action A23)
 * says, combined with Task Scheduler's own last result for the task. The record is read through the client's closed run reads — `runs` over a window
 * that starts at Task Scheduler's last-run instant, then `runs show` for the one run — and nothing else: never a daemon file. Every figure the line
 * states is a field the daemon wrote: "N deleted" and "logical bytes" are typed members of the history line; "entries kept in use" is counted from the
 * run's detail because no typed field carries it, and said "not recorded" when the detail is not there.
 */

/** The cleanup's action id in the run history. */
const A23 = 'A23';

/** What the record read found: the newest A23 run (with the entries it kept in use, when its detail said), no such run, or why the history could not be read. */
export type CleanupRecordRead =
  | { readonly kind: 'found'; readonly line: RunLine; readonly keptInUse: number | undefined }
  | { readonly kind: 'none' }
  | { readonly kind: 'unreadable'; readonly reason: string };

export interface CleanupLine {
  readonly line: string;
  readonly level: ViewLevel;
}

const NO_LINE: CleanupLine = { line: '', level: 'none' };

// ---- reading the record ----

const instantOf = (ms: number): string => new Date(ms).toISOString().replace(/\.\d{3}Z$/, 'Z');

/**
 * The window of the `runs` read: from Task Scheduler's last-run instant less its truncation (it keeps whole seconds), to now — both as the
 * `yyyy-MM-ddTHH:mm:ssZ` the client checks, and never ending before it starts (a last run in the future, after a clock was set back, still ends at itself).
 * Nothing when the instant is not a time.
 */
export function recordWindow(lastRunUtc: string, nowUtcMs: number): { readonly from: string; readonly to: string } | undefined {
  const lastMs = Date.parse(lastRunUtc);

  return Number.isNaN(lastMs) ? undefined : { from: instantOf(lastMs - RUN_TOLERANCE_MS), to: instantOf(Math.max(nowUtcMs, lastMs)) };
}

const startedMs = (line: RunLine): number => {
  const ms = Date.parse(line.startedAt);

  return Number.isNaN(ms) ? Number.NEGATIVE_INFINITY : ms;
};

/** The newest line of the window whose actions include the cleanup — whoever started it (the timer, or a person's own run). */
export function pickRecord(lines: readonly RunLine[]): RunLine | undefined {
  return lines.filter((line) => line.actions.some((a) => a.id === A23)).reduce<RunLine | undefined>((newest, line) => (newest === undefined || startedMs(line) > startedMs(newest) ? line : newest), undefined);
}

const KEPT_IN_USE = 'kept in use:';

/** The entries of the cleanup's run kept because something held them: the `notRemoved[]` items of A23 whose note begins `kept in use:` — or nothing when the detail does not say. */
export function keptInUseCount(show: JsonObject): number | undefined {
  const actions = asList(member(show.detail, 'actions'));
  const notRemoved = member(member(actions.find((a) => member(a, 'id') === A23), 'run'), 'notRemoved');

  return Array.isArray(notRemoved) ? notRemoved.filter(isKept).length : undefined;
}

function isKept(item: unknown): boolean {
  const note = member(item, 'note');

  return typeof note === 'string' && note.startsWith(KEPT_IN_USE);
}

function asList(value: unknown): unknown[] {
  return Array.isArray(value) ? value : [];
}

/** The member `key` of `value` when it is an object — else nothing; a daemon's detail is read, never assumed. */
function member(value: unknown, key: string): unknown {
  return typeof value === 'object' && value !== null && !Array.isArray(value) ? (value as Record<string, unknown>)[key] : undefined;
}

function whyNot(outcome: Exclude<WindowsReadOutcome, { kind: 'read' }>): string {
  return outcome.kind === 'readRefused' ? outcome.detail : outcome.reason;
}

/** The entries kept in use of one run: ONE `runs show`, only when the history line says its detail file is present — else, or when the read fails, not recorded. */
async function keptInUseOf(client: Pick<WindowsCareClient, 'read'>, line: RunLine): Promise<number | undefined> {
  if (line.detailState !== 'present' || line.runId === undefined) {
    return undefined;
  }
  const show = await client.read({ read: 'runsShow', runId: line.runId });

  return show.kind === 'read' ? keptInUseCount(show.body) : undefined;
}

/** The record of the cleanup's last run: ONE `runs` read of the window on the bundled exe, then — for the newest A23 line — ONE `runs show`. */
export async function readCleanupRecord(client: Pick<WindowsCareClient, 'read'>, lastRunUtc: string, nowUtcMs: number): Promise<CleanupRecordRead> {
  const window = recordWindow(lastRunUtc, nowUtcMs);
  if (window === undefined) {
    return { kind: 'unreadable', reason: 'Task Scheduler\'s last-run instant is not a time' };
  }
  const runs = await client.read({ read: 'runs', ...window });
  if (runs.kind !== 'read') {
    return { kind: 'unreadable', reason: whyNot(runs) };
  }
  const line = pickRecord(parseRuns(runs.body));

  return line === undefined ? { kind: 'none' } : { kind: 'found', line, keptInUse: await keptInUseOf(client, line) };
}

// ---- the line ----

interface Part {
  readonly text: string;
  readonly level: ViewLevel;
}

const RANK: Readonly<Record<ViewLevel, number>> = { none: 0, ok: 1, unknown: 2, warn: 3, critical: 4 };

function worst(parts: readonly Part[]): ViewLevel {
  return parts.reduce<ViewLevel>((level, part) => (RANK[part.level] > RANK[level] ? part.level : level), 'none');
}

function keptWords(keptInUse: number | undefined): string {
  return keptInUse === undefined ? 'kept in use: not recorded' : `${keptInUse} entries kept in use`;
}

/** What the run's outcome adds to the line: nothing for a completed run, a warning for every other ending (an interrupted run is NEVER called completed). */
function outcomePart(outcome: string): Part {
  if (outcome === 'completed') {
    return { text: '', level: 'ok' };
  }
  if (outcome === 'interrupted') {
    return { text: ' — interrupted: the next run resumes the half-deleted entry first', level: 'warn' };
  }

  return { text: ` — ${outcome === '' ? 'outcome not recorded' : noticeText(outcome)}`, level: 'warn' };
}

/** A run that deleted: its counts (typed fields of the history line), then its ending. A dry run deleted nothing and says only that. */
function foundPart(line: RunLine, keptInUse: number | undefined): Part[] {
  if (line.dryRun === true) {
    return [{ text: ' — dry run: previewed only, nothing deleted', level: 'ok' }];
  }
  const action = line.actions.find((a) => a.id === A23);
  const counts = action === undefined ? '' : `, ${action.count} deleted, ${keptWords(keptInUse)}, ${action.freedBytes} logical bytes`;

  return [{ text: counts, level: 'ok' }, outcomePart(line.outcome)];
}

/** What each answer of the record read adds to the line. */
const RECORD_PARTS: { readonly [K in CleanupRecordRead['kind']]: (record: Extract<CleanupRecordRead, { kind: K }>) => Part[] } = {
  none: () => [{ text: " — no run record in the daemon's history", level: 'none' }],
  unreadable: (record) => [{ text: ` — the run history could not be read (${noticeText(record.reason)})`, level: 'unknown' }],
  found: (record) => foundPart(record.line, record.keptInUse),
};

function recordParts(record: CleanupRecordRead | undefined): Part[] {
  return record === undefined ? [] : (RECORD_PARTS[record.kind] as (r: CleanupRecordRead) => Part[])(record);
}

/** Task Scheduler's last result, in the shared words (`resultWords.ts` — the task lines word every code the same way): nothing for a clean run, a warning unless it is the task working as built. */
function resultPart(code: number): Part {
  if (code === 0) {
    return { text: '', level: 'none' };
  }

  return { text: ` — ${resultWords(code)}`, level: NORMAL_RESULTS.has(code) ? 'ok' : 'warn' };
}

/** The head of the line: when the cleanup last ran — the record's own start when it was found (whoever started it), else Task Scheduler's — and who started it when it was not the timer. */
function headOf(run: Extract<LastRun, { kind: 'ran' }>, record: CleanupRecordRead | undefined, formatInstant: (iso: string) => string): string {
  const line = record?.kind === 'found' ? record.line : undefined;

  return `last cleanup: ${noticeText(formatInstant(startedOf(run, line)))}${triggerNote(line)}`;
}

/** The record's own start when it is a time (the same test `startedMs` makes when it picks), else Task Scheduler's instant. */
function startedOf(run: Extract<LastRun, { kind: 'ran' }>, line: RunLine | undefined): string {
  return line !== undefined && !Number.isNaN(Date.parse(line.startedAt)) ? line.startedAt : run.utc;
}

function triggerNote(line: RunLine | undefined): string {
  return line === undefined || line.trigger === '' || line.trigger === 'timer' ? '' : ` (trigger: ${noticeText(line.trigger)})`;
}

/**
 * The line for the cleanup task's last run (`run`: the task's last-run instant and result as Task Scheduler read them; nothing when the task is not
 * registered or its read did not answer), with the record read for it — `undefined` while that read has not answered, when the line says only what Task
 * Scheduler said. A read that failed is said and leaves the task's own line untouched.
 */
export function lastCleanupLine(run: LastRun | undefined, record: CleanupRecordRead | undefined, formatInstant: (iso: string) => string): CleanupLine {
  if (run === undefined) {
    return NO_LINE;
  }
  if (run.kind === 'never') {
    return { line: 'last cleanup: never (the task has not run yet)', level: 'none' };
  }
  const parts = [...recordParts(record), resultPart(run.result)];

  return { line: headOf(run, record, formatInstant) + parts.map((p) => p.text).join(''), level: worst(parts) };
}
