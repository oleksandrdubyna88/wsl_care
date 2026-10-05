import type { ReadOutcome, VerbOutcome } from '../client/outcome';
import type { RunRead } from '../client/verbs';
import { runningOf } from '../root/rootAnswers';
import type { RunId } from '../root/rootIds';
import type { RunningBlock } from '../root/rootOutcome';
import type { CleanupJournal, JournalEntry } from './journal';
import { isTerminal, parseRuns, parseRunShow, type RunShow } from './runAnswers';
import { adoptable, CLOCK_SKEW_MS, inFlightNaming, isQueuedOrLive, matches, mustWait, windowOf } from './runMatching';

/**
 * The panel's DURABLE poll (E6.S3, plan §15j M6, §15k #3 / #4, `common.durable-status` rules 3 and 4; the E6.S3 review round)
 * — the one place that decides when a cleanup in flight is asked about, and the one follower of a run:
 *
 * - it polls ONLY while something is in flight: a journal entry this window may follow (its own always, another window's
 *   only while focused — the same predicate decides which entries a tick SETTLES, review C5), or — focused — a run the
 *   daemon reports queued / live, read from the store's newest status so the poll starts from idle (review C4);
 * - every `FOLLOW_POLL.intervalMs` (4 s), `status` only — and, when a followed run is no longer in flight, ONE `runs show`; a
 *   non-terminal answer is not asked again until status names the run in flight once more (review C16);
 * - a terminal state is shown ONCE — the journal re-read first, so a result another window showed is not shown again
 *   (review C6) — and the entry removed; a tick that ended anything re-reads the panel ONCE (review C14);
 * - an unresolved confirm is adopted from `status.running` (trigger `manual`, exactly its actions, queued / live / wedged),
 *   WAITED on while a matching run may still be in flight or the block cannot be read (review B4), and otherwise — after the
 *   request grace — resolved from `runs` over its window, both ends widened by the clock skew (review B3);
 * - it NEVER sticks, and never ends an entry on no evidence: past `FOLLOW_POLL.ceilingMs` an entry ends "state unknown" with
 *   its run id — but only once a status has answered for its distribution AND its record was read once (review B1); a record
 *   that cannot be read is tried `READ_TRIES` times with backoff, then ends "the record could not be read" (review C8);
 * - entries are settled concurrently, at most `SETTLE_AT_ONCE` at a time (review C13), each in its own `try` (review A5: one
 *   bad entry never stops the others); a disposed follower shows, removes and re-reads nothing (review B5).
 *
 * It stops itself when nothing is in flight. The run-log churn it costs the daemon is measured by `runFollower.test.ts`.
 */

export const FOLLOW_POLL = {
  /** M6: every 3–5 s. */
  intervalMs: 4_000,
  /** §15k #4: the hard ceiling of one entry, then "state unknown". */
  ceilingMs: 30 * 60_000,
  /** The daemon's request grace (60 s, `RequestSweep.Grace`) and a margin: before it, an unresolved confirm is not listed. */
  graceMs: 90_000,
} as const;

/** How often a record read that fails is tried (review C8), and the waits before the second and the third try. */
export const READ_TRIES = 3;
const READ_BACKOFF_MS: readonly number[] = [0, 8_000, 16_000];

/** At most this many entries are settled at once (review C13). */
export const SETTLE_AT_ONCE = 4;

/** A terminal answer the follower SHOWS — after which the entry is gone. */
export type RunResult =
  | { readonly kind: 'run'; readonly entry: JournalEntry; readonly show: RunShow }
  | { readonly kind: 'ceiling'; readonly entry: JournalEntry }
  | { readonly kind: 'neverRan'; readonly entry: JournalEntry }
  | { readonly kind: 'ambiguous'; readonly entry: JournalEntry; readonly candidates: readonly RunId[] }
  | { readonly kind: 'unreadable'; readonly entry: JournalEntry; readonly reason: string };

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
  /** After a tick that ended anything: the panel is re-read once (`preview`'s totals become "Docker after"). */
  readonly afterTerminal: () => Promise<void>;
  readonly focused: () => boolean;
  /** The running block of the store's NEWEST status, whoever asked it — so a poll can start from idle (review C4). */
  readonly running: () => RunningBlock | undefined;
  /** Wall-clock milliseconds — the journal's instants are wall-clock (they must survive a reload). */
  readonly wallNow: () => number;
  readonly timers: OneShot;
  /** A poll or an entry that threw (a journal write refused, a defect): reported, never swallowed. */
  readonly fault: (error: unknown) => void;
}

/** What one status poll saw, when it answered: the distribution and its running block. */
interface Seen {
  readonly distro: string;
  readonly running: RunningBlock | undefined;
}

/** What this window learnt about an entry in this session (memory — a reload starts it afresh, which only re-asks). */
interface Track {
  /** A record read (runs show / the runs window) was made for it (review B1). */
  recordTried: boolean;
  /** One runs show was made since status last named the run in flight (review C16). */
  readSinceInFlight: boolean;
  failures: number;
  nextReadAt: number;
}

/** A run the daemon reported in flight that is not in the journal — the timer's, a terminal's (review C4). */
interface Observed {
  readonly runId: RunId;
  readonly distro: string;
  readonly running: RunningBlock;
  readonly track: Track;
  /** When this window first saw it — an observed run is dropped (quietly: it is not ours) past the ceiling. */
  readonly seenAt: number;
}

function newTrack(): Track {
  return { recordTried: false, readSinceInFlight: false, failures: 0, nextReadAt: 0 };
}

/** Why a read did not answer, in the failure's own kind (its words are the panel's to choose). */
function reasonOf(read: ReadOutcome): string {
  return read.kind === 'read' ? '' : read.kind;
}

export class RunFollower {
  /** The entries THIS window started — followed whether or not it is focused. */
  private readonly ours = new Set<string>();
  private readonly tracks = new Map<string, Track>();
  private readonly observed = new Map<string, Observed>();
  /** The runs whose entry ended in this session — never observed afterwards (a wedged run past the ceiling stays in status). */
  private readonly finished = new Set<string>();
  private cancel: (() => void) | undefined;
  private ticking = false;
  private disposed = false;
  private lastRunning: RunningBlock | undefined;
  private endedThisTick = 0;

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

  /** One poll: `status`, then each entry this window may follow settled. Exposed for the tests and the Test-mode API; one at a time. */
  async tick(): Promise<void> {
    if (this.ticking || this.disposed) {
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
   * poll armed while something was in flight asks nothing when that ended meanwhile (M6 — found by the extension-host tier).
   */
  private fired(): void {
    this.cancel = undefined;
    if (!this.shouldPoll()) {
      return;
    }
    void this.tick().catch((error: unknown) => this.options.fault(error)).finally(() => this.kick());
  }

  /** May this window settle `entry`? Its own always; another window's only while focused (review C5). */
  private mayFollow(entry: JournalEntry, focused: boolean): boolean {
    return focused || this.ours.has(entry.id);
  }

  /** M6: an entry this window may follow, a run it observed, or — focused — a run the daemon reports queued / live. */
  private shouldPoll(): boolean {
    const focused = this.options.focused();
    const entries = this.options.journal.entries().some((entry) => this.mayFollow(entry, focused));

    return entries || (focused && this.daemonInFlight());
  }

  /** A run this window observes, or one the store's newest status (or this follower's last) reports queued / live. */
  private daemonInFlight(): boolean {
    return this.observed.size > 0 || isQueuedOrLive(this.options.running() ?? this.lastRunning);
  }

  private async pollOnce(): Promise<void> {
    const seen = seenOf(await this.options.status());
    this.lastRunning = seen?.running;
    const focused = this.options.focused();
    this.endedThisTick = 0;
    const entries = this.options.journal.entries().filter((e) => this.mayFollow(e, focused));
    await eachAtMost(SETTLE_AT_ONCE, entries, (entry) => this.guarded(() => this.settle(entry, seen)));
    await this.guarded(() => this.observe(seen, focused));
    if (this.endedThisTick > 0 && !this.disposed) {
      await this.options.afterTerminal();
    }
  }

  /** Review A5: one entry's fault is reported and the others go on. */
  private async guarded(work: () => Promise<void>): Promise<void> {
    try {
      await work();
    } catch (error) {
      this.options.fault(error);
    }
  }

  private trackOf(id: string): Track {
    const known = this.tracks.get(id);
    if (known !== undefined) {
      return known;
    }
    const track = newTrack();
    this.tracks.set(id, track);

    return track;
  }

  /** Only a status that answered for the entry's distribution is evidence; past the ceiling only once its record was read (B1). */
  private async settle(entry: JournalEntry, seen: Seen | undefined): Promise<void> {
    if (!answeredFor(seen, entry.distro)) {
      return;
    }
    const track = this.trackOf(entry.id);
    const ended = await this.evaluated(entry, seen.running, track);
    if (!ended && this.mayEndOnCeiling(entry, track)) {
      await this.end({ kind: 'ceiling', entry });
    }
  }

  private evaluated(entry: JournalEntry, running: RunningBlock | undefined, track: Track): Promise<boolean> {
    return entry.kind === 'run' ? this.settleRun(entry, running, track) : this.settleUnresolved(entry, running, track);
  }

  /** B1: past the ceiling AND its record read once — never on the ceiling alone. */
  private mayEndOnCeiling(entry: JournalEntry, track: Track): boolean {
    return this.pastCeiling(entry) && track.recordTried;
  }

  private ageOf(entry: JournalEntry): number {
    return this.options.wallNow() - Date.parse(entry.since);
  }

  /** An age that is not a number (review A5) or past the ceiling. */
  private pastCeiling(entry: JournalEntry): boolean {
    const age = this.ageOf(entry);

    return !Number.isFinite(age) || age > FOLLOW_POLL.ceilingMs;
  }

  private pastGrace(entry: JournalEntry): boolean {
    const age = this.ageOf(entry);

    return !Number.isFinite(age) || age >= FOLLOW_POLL.graceMs;
  }

  /** In flight → wait (an entry past the ceiling still gets its one record read); otherwise ONE `runs show` per leaving flight. */
  private async settleRun(entry: Extract<JournalEntry, { kind: 'run' }>, running: RunningBlock | undefined, track: Track): Promise<boolean> {
    if (inFlightNaming(running, entry.runId)) {
      track.readSinceInFlight = false;
      return this.needsLastLook(entry, track) ? this.readRun(entry, track) : false;
    }

    return track.readSinceInFlight ? false : this.readRun(entry, track);
  }

  /** An entry past the ceiling whose run is still in flight gets ONE record read before the ceiling may end it (B1). */
  private needsLastLook(entry: JournalEntry, track: Track): boolean {
    return this.pastCeiling(entry) && !track.recordTried;
  }

  private async readRun(entry: Extract<JournalEntry, { kind: 'run' }>, track: Track): Promise<boolean> {
    const read = await this.boundedRead(track, { read: 'runsShow', runId: entry.runId });
    if (read === undefined || read.kind !== 'read') {
      return this.unreadable(entry, track, read);
    }
    const show = parseRunShow(read.body);
    track.readSinceInFlight = !isTerminal(show.state);

    return isTerminal(show.state) ? this.end({ kind: 'run', entry, show }) : false;
  }

  /** Adopted; waited on (B4); before the grace (and not past the ceiling), kept; otherwise resolved from the runs of its window. */
  private async settleUnresolved(entry: JournalEntry, running: RunningBlock | undefined, track: Track): Promise<boolean> {
    const adopted = adoptable(running, entry);
    if (adopted !== undefined) {
      await this.adopt(entry, adopted);
      return false;
    }

    return mustWait(running, entry) ? this.waited(entry, track) : this.afterGrace(entry, track);
  }

  private waited(entry: JournalEntry, track: Track): Promise<boolean> {
    return this.needsLastLook(entry, track) ? this.lastLook(entry, track) : Promise.resolve(false);
  }

  private afterGrace(entry: JournalEntry, track: Track): Promise<boolean> {
    return this.pastGrace(entry) ? this.listed(entry, track) : Promise.resolve(false);
  }

  /**
   * Past the ceiling while its run may still be in flight (B4) — ONE runs window (B1): exactly one match is adopted; anything
   * else decides nothing here, and the ceiling then ends it "state unknown" — never "never ran" for a run that may be running.
   */
  private async lastLook(entry: JournalEntry, track: Track): Promise<boolean> {
    const candidates = candidatesOf(await this.boundedRead(track, { read: 'runs', ...windowOf(entry, this.options.wallNow()) }), entry);
    const [only] = candidates;
    if (candidates.length === 1 && only !== undefined) {
      await this.adopt(entry, only);
    }

    return false;
  }

  private async listed(entry: JournalEntry, track: Track): Promise<boolean> {
    const read = await this.boundedRead(track, { read: 'runs', ...windowOf(entry, this.options.wallNow()) });
    if (read === undefined || read.kind !== 'read') {
      return this.unreadable(entry, track, read);
    }
    return this.resolved(entry, candidatesOf(read, entry));
  }

  /** A read — or `undefined` while the backoff says to wait; a failure is counted (review C8). */
  private async boundedRead(track: Track, request: RunRead): Promise<ReadOutcome | undefined> {
    if (this.options.wallNow() < track.nextReadAt) {
      return undefined;
    }
    const read = await this.options.read(request);
    track.recordTried = true;
    track.failures = read.kind === 'read' ? 0 : track.failures + 1;
    track.nextReadAt = this.options.wallNow() + (READ_BACKOFF_MS[track.failures] ?? 0);

    return read;
  }

  /** A read that did not come (yet): after `READ_TRIES` failures the entry ends, saying why. */
  private async unreadable(entry: JournalEntry, track: Track, read: ReadOutcome | undefined): Promise<boolean> {
    if (read === undefined || track.failures < READ_TRIES) {
      return false;
    }

    return this.end({ kind: 'unreadable', entry, reason: reasonOf(read) });
  }

  private async resolved(entry: JournalEntry, candidates: readonly RunId[]): Promise<boolean> {
    const [only] = candidates;
    if (candidates.length === 1 && only !== undefined) {
      await this.adopt(entry, only);
      return false;
    }

    return this.end(candidates.length === 0 ? { kind: 'neverRan', entry } : { kind: 'ambiguous', entry, candidates });
  }

  private async adopt(entry: JournalEntry, runId: RunId): Promise<void> {
    if (this.disposed) {
      return;
    }
    const { id, ...rest } = entry;
    await this.options.journal.replace(id, { ...rest, kind: 'run', runId });
  }

  /**
   * The entry is CLAIMED (removed, the tombstone naming this window) and then shown — by the one window whose claim stands, so
   * two windows ending it at once show it once (review C6). Not when this follower was disposed (B5). The order is inverted
   * from "show, then remove" for exactly that: a crash between the claim and the notification loses the notification, never
   * the result — *Last cleanup* reads it from `status.lastCleanup`. Returns whether the entry ended (here or elsewhere).
   */
  private async end(result: RunResult): Promise<boolean> {
    if (this.disposed) {
      return false;
    }
    const claimed = await this.options.journal.claim(result.entry.id);
    this.forget(result.entry);
    if (claimed && !this.disposed) {
      this.options.show(result);
      this.endedThisTick += 1;
    }

    return true;
  }

  private forget(entry: JournalEntry): void {
    this.ours.delete(entry.id);
    this.tracks.delete(entry.id);
    if (entry.kind === 'run') {
      this.finished.add(entry.runId);
    }
  }

  /** Review C4: a run in flight that no journal entry names is watched (focused only) and its result shown ONCE when it ends. */
  private async observe(seen: Seen | undefined, focused: boolean): Promise<void> {
    if (seen === undefined || !focused) {
      return;
    }
    this.watch(seen);
    for (const observed of [...this.observed.values()].filter((o) => o.distro === seen.distro)) {
      await this.settleObserved(observed, seen.running);
    }
  }

  private watch(seen: Seen): void {
    const running = seen.running;
    if (running !== undefined && running.runId !== undefined && this.isNew(running, running.runId)) {
      this.observed.set(running.runId, { runId: running.runId, distro: seen.distro, running, track: newTrack(), seenAt: this.options.wallNow() });
    }
  }

  /** Queued / live, not watched yet, not ended in this session, and no journal entry follows it. */
  private isNew(running: RunningBlock, runId: RunId): boolean {
    const known = this.observed.has(runId) || this.finished.has(runId);

    return isQueuedOrLive(running) && !known && !this.options.journal.entries().some((e) => e.kind === 'run' && e.runId === runId);
  }

  private async settleObserved(observed: Observed, running: RunningBlock | undefined): Promise<void> {
    if (this.options.wallNow() - observed.seenAt > FOLLOW_POLL.ceilingMs) {
      this.observed.delete(observed.runId);
      return;
    }
    if (!inFlightNaming(running, observed.runId)) {
      await this.readObserved(observed);
    }
  }

  /** ONE runs show; a terminal state shown once; a non-terminal answer or the read's last failure ends the watch quietly. */
  private async readObserved(observed: Observed): Promise<void> {
    const show = showOf(await this.boundedRead(observed.track, { read: 'runsShow', runId: observed.runId }));
    if (this.disposed || stillTrying(show, observed.track)) {
      return;
    }
    this.observed.delete(observed.runId);
    this.showObserved(observed, show);
  }

  private showObserved(observed: Observed, show: RunShow | undefined): void {
    if (show !== undefined && isTerminal(show.state)) {
      this.options.show({ kind: 'run', entry: observedEntry(observed, this.options.wallNow()), show });
      this.endedThisTick += 1;
    }
  }
}

/** An observed run as the words of a result need it — never written to the journal. */
function observedEntry(observed: Observed, now: number): JournalEntry {
  return { id: `observed:${observed.runId}`, kind: 'run', op: 'clean', distro: observed.distro, actions: observed.running.actions, since: new Date(now - CLOCK_SKEW_MS).toISOString(), runId: observed.runId };
}

/** The history lines of a runs window that can be this entry's run — their run ids. */
function candidatesOf(read: ReadOutcome | undefined, entry: JournalEntry): readonly RunId[] {
  return read !== undefined && read.kind === 'read' ? parseRuns(read.body).filter((line) => matches(line, entry)).flatMap((line) => line.runId ?? []) : [];
}

function answeredFor(seen: Seen | undefined, distro: string): seen is Seen {
  return seen !== undefined && seen.distro === distro;
}

function showOf(read: ReadOutcome | undefined): RunShow | undefined {
  return read !== undefined && read.kind === 'read' ? parseRunShow(read.body) : undefined;
}

/** No answer yet and tries left: the watch goes on. */
function stillTrying(show: RunShow | undefined, track: Track): boolean {
  return show === undefined && track.failures < READ_TRIES;
}

function seenOf(status: VerbOutcome): Seen | undefined {
  if (status.kind !== 'answered') {
    return undefined;
  }

  return { distro: status.distro, running: status.answer.verb === 'status' ? runningOf(status.answer.body) : undefined };
}

/** `work` over `items`, at most `limit` at a time (review C13). */
async function eachAtMost<T>(limit: number, items: readonly T[], work: (item: T) => Promise<void>): Promise<void> {
  let next = 0;
  const worker = async (): Promise<void> => {
    while (next < items.length) {
      const item = items[next] as T;
      next += 1;
      await work(item);
    }
  };
  await Promise.all(Array.from({ length: Math.min(limit, items.length) }, worker));
}
