import type { RunId } from '../root/rootIds';
import type { RunningBlock } from '../root/rootOutcome';
import { FULL_CHECK_ACTIONS, RUN_KINDS } from '../root/cleanupController';
import type { JournalEntry } from './journal';
import type { RunLine } from './runAnswers';
import { DAY_MS, RETENTION_DAYS, utcInstantOf } from '../shared/instants';

/**
 * The follower's pure decisions (E6.S3 and its review round): which running block names a followed run, which run an
 * unresolved confirm may adopt, which history lines can be that confirm's run, and the window a resolution reads. No clock,
 * no I/O — every value comes in.
 */

/** How far apart this window's clock (Windows) and the daemon's (WSL) may lie — the daemon's own `RequestSweep.FutureSkew` (review B3). */
export const CLOCK_SKEW_MS = 5 * 60_000;

/** The daemon keeps 90 days of history; a runs window never reaches further back (review B1). */
export const RETENTION_MS = RETENTION_DAYS * DAY_MS;

function stateOf(running: RunningBlock | undefined): string {
  return running !== undefined && running.state.kind === 'known' ? running.state.value : '';
}

export function isQueuedOrLive(running: RunningBlock | undefined): boolean {
  return ['queued', 'live'].includes(stateOf(running));
}

/** queued, live or wedged — a run that has not ended (a wedged one is followed until its stop or the ceiling). */
export function inFlight(running: RunningBlock | undefined): boolean {
  return ['queued', 'live', 'wedged'].includes(stateOf(running));
}

/** In flight, naming THIS run. */
export function inFlightNaming(running: RunningBlock | undefined, runId: RunId): boolean {
  return inFlight(running) && running?.runId === runId;
}

function sameActions(a: readonly string[], b: readonly string[]): boolean {
  const sorted = [...b].sort();

  return a.length === b.length && [...a].sort().every((x, i) => x === sorted[i]);
}

/** A run the panel started (`trigger: manual`) holding exactly the confirmed actions. */
function isPanelRunOf(running: RunningBlock, entry: JournalEntry): boolean {
  return running.trigger === 'manual' && sameActions(running.actions, entry.actions);
}

/** The run an unresolved confirm adopts from `status.running`: the panel's, exactly its actions, in flight (wedged too — review B4), with a run id. */
export function adoptable(running: RunningBlock | undefined, entry: JournalEntry): RunId | undefined {
  return running !== undefined && inFlight(running) && isPanelRunOf(running, entry) ? running.runId : undefined;
}

/**
 * Whether a resolution must WAIT (review B4): the running block cannot be read (`unknown` / `unreadable` — it may well be
 * this run), or it shows the panel's run of exactly these actions still in flight without a run id to adopt.
 */
export function mustWait(running: RunningBlock | undefined, entry: JournalEntry): boolean {
  if (running === undefined) {
    return false;
  }

  return unreadableBlock(running) || (inFlight(running) && isPanelRunOf(running, entry));
}

/** A block that cannot tell what runs: `unknown`, `unreadable`, or a state this build does not know. */
function unreadableBlock(running: RunningBlock): boolean {
  return running.state.kind === 'unknown' || ['unknown', 'unreadable'].includes(running.state.value);
}

/**
 * The reasons of history lines a full check must NOT be matched by although they carry no action (review B2): a request the
 * daemon could not use (`RequestSweep.Unusable`) and an orphaned detail the next run reconciled (`RunReconcile`). The
 * daemon's own words — `research/module_tests.md` cites where each is written.
 */
const NOT_A_FULL_CHECK: readonly ((reason: string) => boolean)[] = [
  (reason) => reason.startsWith('refused: its request could not be used'),
  (reason) => reason.startsWith('the run wrote its detail and ended before its history line'),
  (reason) => reason.startsWith('the run left a detail that cannot be read'),
];

/**
 * A full check's line by the rule of a daemon OLDER than plan §15o (review B2): `[]` when it completed or its measurement was
 * cut off (`CollectRun`), `["collect"]` when it was refused, cut off before it started or swept (`DetachedRuns`,
 * `RequestSweep`, `RunningSweep` copied the request's actions) — either, but never the shapes above.
 */
function legacyFullCheck(line: RunLine): boolean {
  const ids = line.actions.map((a) => a.id);

  return (ids.length === 0 || sameActions(ids, FULL_CHECK_ACTIONS)) && !NOT_A_FULL_CHECK.some((excluded) => excluded(line.reason));
}

/**
 * A full check's line, KIND FIRST (plan §15o, the extension half of its boundary): a line that names its kind is decided by
 * it alone — `collect` is the full check whatever its actions and reason, `act` never is; a line without one (an older
 * daemon, or a kind this build does not know — `runAnswers.ts` reads it as absent) keeps the old rule exactly.
 */
function isFullCheckLine(line: RunLine): boolean {
  return line.kind === undefined ? legacyFullCheck(line) : line.kind === RUN_KINDS.fullCheck;
}

/**
 * An act's line, symmetric with the full check's (review C7): a line that names its kind must be an `act`; one without a kind
 * (or with one this build does not know) keeps the old rule. The daemon stamps `act` on EVERY act-origin line — plan §15o on
 * `fix/wc-full-check-line-names-collect`: the terminal line, the request sweep, the running-state sweep and the refusal all
 * carry the request's kind — so a manual full check that happens to carry these ids never resolves an act entry.
 */
function isActLine(line: RunLine, entry: JournalEntry): boolean {
  return (line.kind === undefined || line.kind === RUN_KINDS.act) && sameActions(line.actions.map((a) => a.id), entry.actions);
}

/** A history line's actions as a confirm of this entry writes them: an act's line with its ids exactly, or a full check's line. */
function actionsMatch(line: RunLine, entry: JournalEntry): boolean {
  return entry.op === 'fullCheck' ? isFullCheckLine(line) : isActLine(line, entry);
}

/** A history line that can be this entry's run: the panel's, its actions, started no earlier than the confirm less the skew (B3). */
export function matches(line: RunLine, entry: JournalEntry): boolean {
  const since = Date.parse(entry.since) - CLOCK_SKEW_MS;

  return line.trigger === 'manual' && actionsMatch(line, entry) && Date.parse(line.startedAt) >= since;
}


/** One second past `ms`, whole — the window's end is exclusive. */
function ceilInstant(ms: number): string {
  return utcInstantOf((Math.ceil(ms / 1000) + 1) * 1000);
}

/** The runs window of an unresolved confirm: from the confirm less the skew (no further back than the retention) to now plus the skew. */
export function windowOf(entry: JournalEntry, now: number): { readonly from: string; readonly to: string } {
  return { from: utcInstantOf(Math.max(Date.parse(entry.since) - CLOCK_SKEW_MS, now - RETENTION_MS)), to: ceilInstant(now + CLOCK_SKEW_MS) };
}
