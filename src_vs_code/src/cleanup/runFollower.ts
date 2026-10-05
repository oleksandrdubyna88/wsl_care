import type { ReadOutcome, VerbOutcome } from '../client/outcome';
import type { RunRead } from '../client/verbs';
import { runningOf } from '../root/rootAnswers';
import type { RunId } from '../root/rootIds';
import type { RunningBlock } from '../root/rootOutcome';
import type { CleanupJournal, JournalEntry } from './journal';
import { isTerminal, parseRuns, parseRunShow, type RunLine, type RunShow } from './runAnswers';

/**
 * The panel's DURABLE poll (E6.S3, plan §15j M6, §15k #3 / #4, `common.durable-status` rules 3 and 4) — the one place that
 * decides when a cleanup in flight is asked about, and the one follower of a run (the controller follows only a detach
 * that named no run id, and only for its minute — E6.S2 review M3):
 *
 * - it polls ONLY while something is in flight: a journal entry (a run this extension started that has had no terminal
 *   answer shown, or an unresolved confirm), or — in the FOCUSED window — `status.running` queued / live;
 * - an entry another window started is followed only while this window is focused; one this window started, always;
 * - every `FOLLOW_POLL.intervalMs` (4 s, M6's 3–5 s), `status` only — and ONE `runs show` when a followed run is no longer
 *   in flight (queued / live / wedged naming it); a terminal state (done, refused, interrupted, unknown, a value this build
 *   does not know) is shown and the entry removed, then the panel is re-read once (the "Docker after" totals);
 * - an unresolved confirm (the controller's `outcomeUnknown` with no run id, or one interrupted by a reload before its
 *   answer) is RESOLVED from the daemon's own records: a queued / live run of `trigger: manual` holding exactly the
 *   confirmed actions is adopted; after the request grace, `runs --from <the confirm> --to <now>` — one match is the run,
 *   none means it never ran, several are shown as candidates (the coai E6.S2 plan round #3 contract);
 * - it NEVER sticks: past `FOLLOW_POLL.ceilingMs` (30 minutes, §15k #4) an entry ends as "state unknown" with its run id.
 *
 * It stops itself when nothing is in flight. The run-log churn it costs the daemon is measured by `runFollower.test.ts`
 * and recorded for the owner (research/2026-10-04_extension_poll_churn.md, § E6.S3).
 */

export const FOLLOW_POLL = {
  /** M6: every 3–5 s. */
  intervalMs: 4_000,
  /** §15k #4: the hard ceiling of one entry, then "state unknown". */
  ceilingMs: 30 * 60_000,
  /** The daemon's request grace (60 s, `RequestSweep.Grace`) and a margin: before it, an unresolved confirm is not listed. */
  graceMs: 90_000,
} as const;

/** A terminal answer the follower SHOWS — after which the entry is gone. */
export type RunResult =
  | { readonly kind: 'run'; readonly entry: JournalEntry; readonly show: RunShow }
  | { readonly kind: 'ceiling'; readonly entry: JournalEntry }
  | { readonly kind: 'neverRan'; readonly entry: JournalEntry }
  | { readonly kind: 'ambiguous'; readonly entry: JournalEntry; readonly candidates: readonly RunId[] };

/** A one-shot timer; the returned function cancels it. */
export interface OneShot {
  after(ms: number, run: () => void): () => void;
}

export interface FollowerOptions {
  readonly journal: CleanupJournal;
  /** Asks `status` — through the store, so the bar and the panel see the same answer. */
  readonly status: () => Promise<VerbOutcome>;
  readonly read: (request: RunRead) => Promise<ReadOutcome>;
  /** Shows a terminal answer (a notification, and the panel's *Last cleanup*). */
  readonly show: (result: RunResult) => void;
  /** After a terminal answer: the panel is re-read once (`preview`'s totals become "Docker after"). */
  readonly afterTerminal: () => Promise<void>;
  readonly focused: () => boolean;
  /** Wall-clock milliseconds — the journal's instants are wall-clock (they must survive a reload). */
  readonly wallNow: () => number;
  readonly timers: OneShot;
  /** A poll that threw (a journal write refused, a defect): reported, never swallowed — the poll then goes on or stops by M6. */
  readonly fault: (error: unknown) => void;
}

/** What one status poll saw, when it answered: the distribution and its running block. */
interface Seen {
  readonly distro: string;
  readonly running: RunningBlock | undefined;
}

export class RunFollower {
  /** The entries THIS window started — followed whether or not it is focused. */
  private readonly ours = new Set<string>();
  private cancel: (() => void) | undefined;
  private ticking = false;
  private disposed = false;
  private lastRunning: RunningBlock | undefined;

  constructor(private readonly options: FollowerOptions) {}

  /** A run this window started: followed even while the window is not focused. */
  started(entryId: string): void {
    this.ours.add(entryId);
  }

  /** Something may be in flight: start polling if it is and nothing polls yet. */
  kick(): void {
    if (this.cancel === undefined && !this.disposed && this.shouldPoll()) {
      this.cancel = this.options.timers.after(FOLLOW_POLL.intervalMs, () => this.fired());
    }
  }

  /** One poll: `status`, then each entry settled. Exposed for the tests and the Test-mode API; one at a time. */
  async tick(): Promise<void> {
    if (this.ticking) {
      return;
    }
    this.ticking = true;
    try {
      await this.pollOnce();
    } finally {
      this.ticking = false;
    }
  }

  /** Whether a poll is armed — what the tests read. */
  armed(): boolean {
    return this.cancel !== undefined;
  }

  dispose(): void {
    this.disposed = true;
    this.cancel?.();
    this.cancel = undefined;
  }

  /**
   * The detached edge (`common.reliability`): the poll's fault is reported, and the next kick decides whether to go on. A
   * poll armed while something was in flight asks nothing when that ended meanwhile (M6 — found by the extension-host tier:
   * a timer armed by a confirm fired after the run had ended, and asked one status too many).
   */
  private fired(): void {
    this.cancel = undefined;
    if (!this.shouldPoll()) {
      return;
    }
    void this.tick().catch((error: unknown) => this.options.fault(error)).finally(() => this.kick());
  }

  /** M6: an entry this window may follow, or — focused — a run the daemon reports queued / live. */
  private shouldPoll(): boolean {
    const focused = this.options.focused();
    const entries = this.options.journal.entries().some((entry) => focused || this.ours.has(entry.id));

    return entries || (focused && isQueuedOrLive(this.lastRunning));
  }

  private async pollOnce(): Promise<void> {
    const seen = seenOf(await this.options.status());
    this.lastRunning = seen?.running;
    for (const entry of this.options.journal.entries()) {
      await this.settle(entry, seen);
    }
  }

  /** Evaluated first (a long-closed window still gets its answer); only an entry that did not end meets the ceiling. */
  private async settle(entry: JournalEntry, seen: Seen | undefined): Promise<void> {
    const ended = await this.evaluated(entry, seen);
    if (!ended && this.age(entry) > FOLLOW_POLL.ceilingMs) {
      await this.end({ kind: 'ceiling', entry });
    }
  }

  /** Only a status that answered for the entry's own distribution is evidence about it. */
  private evaluated(entry: JournalEntry, seen: Seen | undefined): Promise<boolean> {
    if (seen === undefined || seen.distro !== entry.distro) {
      return Promise.resolve(false);
    }

    return entry.kind === 'run' ? this.settleRun(entry, seen.running) : this.settleUnresolved(entry, seen.running);
  }

  /** In flight → wait; otherwise ONE `runs show`, and a terminal state ends it. */
  private async settleRun(entry: Extract<JournalEntry, { kind: 'run' }>, running: RunningBlock | undefined): Promise<boolean> {
    if (inFlightNaming(running, entry.runId)) {
      return false;
    }
    const show = terminalOf(await this.options.read({ read: 'runsShow', runId: entry.runId }));
    if (show !== undefined) {
      await this.end({ kind: 'run', entry, show });
    }

    return show !== undefined;
  }

  /** Adopted from `status.running`; else, past the grace, resolved from the runs of its window. */
  private async settleUnresolved(entry: JournalEntry, running: RunningBlock | undefined): Promise<boolean> {
    const adopted = adoptable(running, entry);
    if (adopted !== undefined) {
      await this.adopt(entry, adopted);
      return false;
    }

    return this.age(entry) < FOLLOW_POLL.graceMs ? false : this.listed(entry);
  }

  private async listed(entry: JournalEntry): Promise<boolean> {
    const read = await this.options.read({ read: 'runs', from: floorInstant(Date.parse(entry.since)), to: ceilInstant(this.options.wallNow()) });
    if (read.kind !== 'read') {
      return false;
    }
    const candidates = parseRuns(read.body).filter((line) => matches(line, entry)).flatMap((line) => line.runId ?? []);

    return this.resolved(entry, candidates);
  }

  private async resolved(entry: JournalEntry, candidates: readonly RunId[]): Promise<boolean> {
    const [only] = candidates;
    if (candidates.length === 1 && only !== undefined) {
      await this.adopt(entry, only);
      return false;
    }
    await this.end(candidates.length === 0 ? { kind: 'neverRan', entry } : { kind: 'ambiguous', entry, candidates });

    return true;
  }

  private async adopt(entry: JournalEntry, runId: RunId): Promise<void> {
    const { id, ...rest } = entry;
    await this.options.journal.replace(id, { ...rest, kind: 'run', runId });
  }

  /** Shown FIRST, then removed (§15k #4: kept until a terminal answer was shown), then the panel re-read once. */
  private async end(result: RunResult): Promise<void> {
    this.options.show(result);
    await this.options.journal.remove(result.entry.id);
    this.ours.delete(result.entry.id);
    await this.options.afterTerminal();
  }

  private age(entry: JournalEntry): number {
    return this.options.wallNow() - Date.parse(entry.since);
  }
}

function seenOf(status: VerbOutcome): Seen | undefined {
  if (status.kind !== 'answered') {
    return undefined;
  }

  return { distro: status.distro, running: status.answer.verb === 'status' ? runningOf(status.answer.body) : undefined };
}

function stateOf(running: RunningBlock | undefined): string {
  return running !== undefined && running.state.kind === 'known' ? running.state.value : '';
}

function isQueuedOrLive(running: RunningBlock | undefined): boolean {
  return ['queued', 'live'].includes(stateOf(running));
}

/** queued, live or wedged, naming THIS run: still in flight (a wedged run is followed until its stop or the ceiling). */
function inFlightNaming(running: RunningBlock | undefined, runId: RunId): boolean {
  return ['queued', 'live', 'wedged'].includes(stateOf(running)) && running?.runId === runId;
}

/** A `runs show` that answered a state polling may stop at — or nothing. */
function terminalOf(read: ReadOutcome): RunShow | undefined {
  const show = read.kind === 'read' ? parseRunShow(read.body) : undefined;

  return show !== undefined && isTerminal(show.state) ? show : undefined;
}

function sameActions(a: readonly string[], b: readonly string[]): boolean {
  const sorted = [...b].sort();

  return a.length === b.length && [...a].sort().every((x, i) => x === sorted[i]);
}

/** A queued / live run the panel started (`trigger: manual`) holding exactly the confirmed actions — its run id. */
function adoptable(running: RunningBlock | undefined, entry: JournalEntry): RunId | undefined {
  return running !== undefined && isPanelRunOf(running, entry) ? running.runId : undefined;
}

function isPanelRunOf(running: RunningBlock, entry: JournalEntry): boolean {
  return isQueuedOrLive(running) && running.trigger === 'manual' && sameActions(running.actions, entry.actions);
}

/**
 * The actions a HISTORY line of this entry carries: the confirmed ids — and none for a full check, whose `status.running`
 * names `["collect"]` but whose line records no action (a full run that is not the timer's does not act: `CollectRun`'s
 * `TimerPassAsync` returns before the engine for any other trigger).
 */
function lineActions(entry: JournalEntry): readonly string[] {
  return entry.op === 'fullCheck' ? [] : entry.actions;
}

/** A history line that can be this entry's run: the panel's, exactly its actions, started at or after the confirm. */
function matches(line: RunLine, entry: JournalEntry): boolean {
  const since = Math.floor(Date.parse(entry.since) / 1000) * 1000;

  return line.trigger === 'manual' && sameActions(line.actions.map((a) => a.id), lineActions(entry)) && Date.parse(line.startedAt) >= since;
}

/** `yyyy-MM-ddTHH:mm:ssZ` at or before `ms` — the window's start, inclusive. */
function floorInstant(ms: number): string {
  return new Date(Math.floor(ms / 1000) * 1000).toISOString().replace('.000Z', 'Z');
}

/** One second past `ms`, whole — the window's end is exclusive, so a run that started this second is inside it. */
function ceilInstant(ms: number): string {
  return new Date((Math.ceil(ms / 1000) + 1) * 1000).toISOString().replace('.000Z', 'Z');
}
