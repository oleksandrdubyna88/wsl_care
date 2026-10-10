import type { DaemonVersion, JsonObject } from '../client/outcome';
import { failureText } from '../failureText';
import type { CleanRow, CleanupControls, ViewLevel } from '../panel/view';
import { NO_CLEANUP } from '../panel/viewModel';
import { actionGate } from '../root/actionGate';
import { runningOf } from '../root/rootAnswers';
import { CAPABILITIES, FULL_CHECK_ACTIONS } from '../root/cleanupController';
import { rootFailureText } from '../root/rootFailureText';
import type { RunningBlock } from '../root/rootOutcome';
import type { Snapshot } from '../state/outcomeStore';
import { gb, minuteOf } from '../text/format';
import { safeText } from '../text/safeText';
import type { StopTarget } from './cleanFlow';
import type { JournalEntry } from './journal';
import type { Notice, NoticeLevel } from './resultText';
import { ROW_IDS } from './rowIds';
import { isArchiveOnly } from './modalText';

/**
 * The cleanup controls the panel shows (E6.S3, `common.durable-status` rules 1–4): DERIVED, every time, from what the
 * daemon reported — `status.running`, `status.capabilities`, `status.actions`, `preview`'s rows and totals,
 * `status.lastCleanup` — and from the host's persisted journal; the window's optimistic flag only ever adds "Confirming…".
 * So a reload shows the daemon's "Cleaning… A4", a dead run says it died and leaves the buttons enabled, and a run this
 * window confirmed greys them until its terminal answer was shown.
 */

/** What the host holds besides the store's snapshot. */
export interface CleanupState {
  readonly entries: readonly JournalEntry[];
  readonly results: readonly Notice[];
  readonly flowBusy: boolean;
}

export interface DerivedCleanup {
  readonly controls: CleanupControls;
  /** The runs a `stop` message's index names — what the host checks before it calls. */
  readonly stoppable: readonly StopTarget[];
}

/** What is in flight, as the controls say it: the words, their level, and — when the buttons are greyed — why. */
interface InFlight {
  readonly state: string;
  readonly level: ViewLevel;
  readonly reason: string;
}

const IDLE: InFlight = { state: '', level: 'none', reason: '' };

/** The triggers whose runs live in the daemon's own units — the only runs `act --stop` can stop (§15j M4). */
const STOPPABLE_TRIGGERS: ReadonlySet<string> = new Set(['manual', 'timer']);

const RUN_TEXT = 120;

interface Answered {
  readonly distro: string;
  readonly body: JsonObject;
  readonly version: DaemonVersion;
}

function answeredStatus(snapshot: Snapshot): Answered | string {
  const status = snapshot.status;
  if (status === undefined) {
    return 'checking…';
  }
  if (status.kind !== 'answered') {
    return failureText(status).label;
  }

  return { distro: status.distro, body: status.answer.verb === 'status' ? status.answer.body : {}, version: status.daemonVersion };
}

function isFullCheck(running: RunningBlock): boolean {
  return running.actions.length === FULL_CHECK_ACTIONS.length && running.actions.every((a, i) => a === FULL_CHECK_ACTIONS[i]);
}

/** "A4" (the action in flight), "A5, A4" (what is queued), or the full check by name. */
function whatOf(running: RunningBlock): string {
  if (isFullCheck(running)) {
    return 'the full check';
  }

  return safeText(running.current === '' ? running.actions.join(', ') : running.current, RUN_TEXT);
}

/** What a live run is doing, in its own words: the full check, the archive run (E10.S1b: A13 alone) or the cleanup of its ids. */
function liveState(running: RunningBlock): string {
  if (isFullCheck(running)) {
    return 'Checking… the full check';
  }

  return isArchiveOnly(running.actions) ? 'Archiving… the aged AI sessions' : `Cleaning… ${whatOf(running)}`;
}

type ByState = { readonly [K in 'none' | 'queued' | 'live' | 'wedged' | 'dead' | 'unknown' | 'unreadable']: (running: RunningBlock) => InFlight };

const BY_STATE: ByState = {
  none: () => IDLE,
  queued: (r) => ({ state: `Queued… ${whatOf(r)}`, level: 'none', reason: 'a run is queued' }),
  live: (r) => ({ state: liveState(r), level: 'none', reason: 'a run is in flight' }),
  wedged: (r) => ({ state: `Wedged: run ${r.runId ?? '(no run id)'} (${whatOf(r)}) — ${safeText(r.reason, RUN_TEXT)}`, level: 'critical', reason: 'a run is wedged' }),
  dead: (r) => ({ state: `Run ${r.runId ?? '(no run id)'} died; the next run records it interrupted.`, level: 'warn', reason: '' }),
  unknown: (r) => ({ state: safeText(r.reason, RUN_TEXT), level: 'warn', reason: 'the daemon cannot tell what runs' }),
  unreadable: (r) => ({ state: safeText(r.reason, RUN_TEXT), level: 'warn', reason: "the daemon's running state cannot be read" }),
};

function fromDaemon(running: RunningBlock | undefined): InFlight {
  if (running === undefined) {
    return IDLE;
  }

  return running.state.kind === 'known' ? BY_STATE[running.state.value](running) : { state: safeText(running.state.label, RUN_TEXT), level: 'warn', reason: 'the running state is one this extension does not know' };
}

function entryLabel(entry: JournalEntry): string {
  const what = entry.op === 'fullCheck' ? 'the full check' : (isArchiveOnly(entry.actions) ? 'the archive run' : entry.actions.join(', '));

  return entry.kind === 'run' ? `run ${entry.runId} (${what})` : what;
}

/** The daemon's word first; then this distribution's journal; then the window's optimistic flag. */
function inFlightOf(running: RunningBlock | undefined, entries: readonly JournalEntry[], flowBusy: boolean): InFlight {
  const daemon = fromDaemon(running);
  if (daemon.reason !== '') {
    return daemon;
  }
  const [pending] = entries;
  if (pending !== undefined) {
    return { state: `Waiting for the result of ${entryLabel(pending)}…`, level: 'none', reason: 'a cleanup is being followed' };
  }

  return flowBusy ? { state: 'Confirming…', level: 'none', reason: 'a cleanup is being confirmed' } : daemon;
}

function isObject(value: unknown): value is JsonObject {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** A figure the daemon answered — or `undefined`: absent is not zero (review C2), it is shown as "?". */
function known(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value : undefined;
}

function bytesText(value: unknown): string {
  const bytes = known(value);

  return bytes === undefined ? '? GB' : gb(bytes);
}

function previewBody(snapshot: Snapshot): JsonObject | undefined {
  const preview = snapshot.preview;

  return preview !== undefined && preview.kind === 'answered' && preview.answer.verb === 'preview' ? preview.answer.body : undefined;
}

function rowNote(row: JsonObject): string {
  if (row.available === false) {
    return `unavailable — ${safeText(typeof row.reason === 'string' ? row.reason : '', RUN_TEXT)}`;
  }

  return countNote(known(row.count), row.reclaimableBytes);
}

function countNote(count: number | undefined, bytes: unknown): string {
  if (count === undefined) {
    return `? objects · ${bytesText(bytes)}`;
  }

  return count > 0 ? `${count} · ${bytesText(bytes)}` : 'nothing to clean';
}

/** Something to clean, as the daemon counted it: readable and a known count above 0 (an unread count is no button). */
function hasSomething(row: JsonObject): boolean {
  return row.available !== false && (known(row.count) ?? 0) > 0;
}

function cleanRow(row: JsonObject, offered: boolean, reported: readonly string[]): CleanRow {
  const id = String(row.id);
  const can = offered && hasSomething(row) && reported.includes(id);

  return { rowId: id, label: `Clean ${id}`, enabled: can, note: rowNote(row) };
}

function rowsOf(preview: JsonObject | undefined, offered: boolean, reported: readonly string[]): CleanRow[] {
  const rows = preview !== undefined && Array.isArray(preview.rows) ? preview.rows.filter(isObject) : [];

  return rows.filter((row) => ROW_IDS.some((id) => id === row.id)).map((row) => cleanRow(row, offered, reported));
}

function strings(value: unknown): readonly string[] {
  return Array.isArray(value) ? value.filter((v): v is string => typeof v === 'string') : [];
}

function isWedged(running: RunningBlock | undefined): running is RunningBlock {
  return running !== undefined && running.state.kind === 'known' && running.state.value === 'wedged';
}

/** A run of the daemon's own units (the timer's, or one the panel started), on a daemon that advertises `act.stop`. */
function inDaemonUnits(running: RunningBlock, capabilities: readonly string[]): boolean {
  return STOPPABLE_TRIGGERS.has(running.trigger) && capabilities.includes('act.stop');
}

/** §15j M4: a wedged run the daemon can stop — the only run a Stop button is offered for. */
function stoppableOf(running: RunningBlock | undefined, capabilities: readonly string[]): StopTarget[] {
  if (!isWedged(running) || running.runId === undefined) {
    return [];
  }

  return inDaemonUnits(running, capabilities) ? [{ runId: running.runId, actions: running.actions }] : [];
}

function stopTextOf(running: RunningBlock | undefined, stoppable: readonly StopTarget[]): string {
  if (!isWedged(running) || stoppable.length > 0) {
    return '';
  }

  return `Run ${running.runId ?? '(no run id)'} is wedged (pid ${pidText(running)}) and the daemon cannot stop it from here (it runs outside the daemon's units, or this daemon has no stop); stop it by hand in the distribution.`;
}

function pidText(running: RunningBlock): string {
  return running.pid === undefined ? 'unknown' : String(running.pid);
}

function stopOf(stoppable: readonly StopTarget[]): CleanupControls['stop'] {
  const [first] = stoppable;

  return first === undefined ? undefined : { index: 0, label: `Stop run ${first.runId}` };
}

/** The reclaimable total — and how many types Docker could not size, which the text then says (review C2). */
function reclaimable(preview: JsonObject): { readonly bytes: number; readonly unread: number } {
  const totals = isObject(preview.totals) ? preview.totals : {};
  const types = Array.isArray(totals.types) ? totals.types.filter(isObject) : [];
  const sizes = types.map((t) => known(isObject(t.reclaimable) ? t.reclaimable.bytes : undefined));

  return { bytes: sizes.reduce<number>((sum, b) => sum + (b ?? 0), 0), unread: sizes.filter((b) => b === undefined).length };
}

function reclaimableText(total: { readonly bytes: number; readonly unread: number }): string {
  return total.unread === 0 ? `${gb(total.bytes)} reclaimable` : `at least ${gb(total.bytes)} reclaimable, ${total.unread} ${total.unread === 1 ? 'type' : 'types'} not read`;
}

/** M7: "Docker after" — the preview's totals, labelled with the time they were read, and whether that is after the last cleanup. */
function dockerAfterOf(preview: JsonObject | undefined, status: JsonObject): string {
  if (preview === undefined) {
    return '';
  }
  const read = typeof preview.sampledAt === 'string' ? preview.sampledAt : '';
  const after = Date.parse(read) > lastCleanupStart(status);

  return `${after ? 'Docker after the last cleanup' : 'Docker now'}: ${reclaimableText(reclaimable(preview))} (docker system df, read at ${minuteOf(read)})`;
}

/** When the newest recorded cleanup started — +∞ when there is none, so nothing reads as "after" it. */
function lastCleanupStart(status: JsonObject): number {
  const last = isObject(status.lastCleanup) ? status.lastCleanup.startedAt : undefined;
  const ms = typeof last === 'string' ? Date.parse(last) : Number.NaN;

  return Number.isFinite(ms) ? ms : Number.POSITIVE_INFINITY;
}

const LEVEL: { readonly [L in NoticeLevel]: ViewLevel } = { info: 'ok', warn: 'warn', error: 'critical' };

function resultsOf(results: readonly Notice[]): CleanupControls['results'] {
  return results.map((r) => ({ sentence: r.sentence, level: LEVEL[r.level] }));
}

/** Greyed with this reason, or '' when the daemon may be asked: the capabilities first (the authority, §15j M5), then what is in flight. */
function blockedBy(status: Answered, flight: InFlight, capabilities: readonly string[]): string {
  const gate = actionGate(status.body, status.version, CAPABILITIES.confirm);
  if (gate.kind !== 'open') {
    return rootFailureText(gate).sentence;
  }

  return flight.reason !== '' ? flight.reason : (capabilities.length === 0 ? 'the daemon advertises no capability' : '');
}

function derived(snapshot: Snapshot, status: Answered, state: CleanupState): DerivedCleanup {
  const running = runningOf(status.body);
  const capabilities = strings(status.body.capabilities);
  const flight = inFlightOf(running, state.entries.filter((e) => e.distro === status.distro), state.flowBusy);
  const reason = blockedBy(status, flight, capabilities);
  const preview = previewBody(snapshot);
  const stoppable = stoppableOf(running, capabilities);
  const open = reason === '';
  const controls: CleanupControls = {
    enabled: open, state: flight.state, stateLevel: flight.level, reason,
    rows: rowsOf(preview, open, strings(status.body.actions)),
    fullCheck: open && actionGate(status.body, status.version, CAPABILITIES.fullCheck).kind === 'open',
    stop: stopOf(stoppable), stopText: stopTextOf(running, stoppable), results: resultsOf(state.results), dockerAfter: dockerAfterOf(preview, status.body),
  };

  return { controls, stoppable };
}

export function deriveCleanup(snapshot: Snapshot, state: CleanupState): DerivedCleanup {
  const status = answeredStatus(snapshot);

  return typeof status === 'string' ? { controls: { ...NO_CLEANUP, reason: status, results: resultsOf(state.results) }, stoppable: [] } : derived(snapshot, status, state);
}
