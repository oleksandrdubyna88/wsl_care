import type { Failure, VerbOutcome } from '../client/outcome';
import type { Verb } from '../client/verbs';
import type { RunOptions } from '../client/WslCareClient';
import type { OutcomeStore, PanelVerb } from '../state/outcomeStore';
import { DEFAULT_TARGET, type DistroTarget } from '../wsl/distroTarget';

/**
 * WHEN the daemon is asked (plan §15f #8, §15g M1, m3) — the one place that decides it:
 *
 * - only the FOCUSED window polls (`window.state.focused`): once when it gains focus (and at start, if focused), then
 *   every `wslCare.refreshSeconds` (default 120, never below 30, never above a day — setInterval overflows past
 *   2^31-1 ms) while it keeps it; losing focus disarms the timer;
 * - a poll asks `status` ONLY — `preview` and `doctor` are asked when the panel opens or Refresh is pressed;
 * - every call goes through the client, which runs `wsl.exe --list --running --quiet` first and makes NO `-d` call when
 *   the distribution is not running — so neither a poll nor opening the panel ever starts the VM. A panel refresh that
 *   finds the distribution stopped does not ask `preview` / `doctor` at all (they would stop at the same check).
 *   Remaining race, stated: a distribution that stops between the running check and the `-d` call is started again by
 *   that call; the window is the ~50 ms between two `wsl.exe` starts (measured: each answers in 46–59 ms);
 * - the one call that MAY start it is "Start WSL and check": `startIfStopped` on `status`, a call the user asked for.
 *
 * E19.S2b: on a Windows host the same tick (and a panel refresh) also reads the BUNDLED Windows daemon's `status` into its own slot
 * (`OutcomeStore.windowsStatus`) — the 5-minute disk reading the bar needs. The two reads are independent: each is stored on its own
 * answer, a failure of one never touches the other's slot (WSL stopped still shows the Windows reading), the Windows read is no
 * business of the distribution target (a switch keeps it), and a window that is not focused reads neither. The Windows read is
 * SHARED while it runs: a tick or a Refresh that finds the exe still running joins that read — one start, one store write.
 *
 * Each `status` run opens one run-log file in the daemon (M1) — and, since E19.S2b, one in the WINDOWS binary's own log folder
 * too, so a Windows window-day costs about TWICE the files at the default (721 + 721). An interval tick reads the Windows status only when
 * the last Windows read STARTED about 120 s ago (`Timers.nowMs`, with half a tick of slack for timer jitter; at the default 120 s or slower every tick reads, no clock compare), so at the 30 s floor the Windows side stays at 721
 * a day (the distro's 2 881 follows the setting); a panel Refresh and a regained focus always read. The numbers per window-day are measured by
 * `poller.test.ts` over this very class and recorded in research/2026-10-04_extension_poll_churn.md.
 *
 * Every round is stamped with the TARGET it was started for (`target`, the `wslCare.distro` value). A round started for
 * another target than the previous one clears the store first and makes every older round obsolete: an obsolete round
 * stores nothing, asks nothing more, and leaves the "checking" state to the current one — so a `preview` still running
 * for the previous distribution can never land under the new one (retro review of PR #9, `distroSwitch.test.ts`). A round
 * for the SAME target never makes another obsolete, so a status poll never drops a `preview` in flight.
 */

export const DEFAULT_REFRESH_SECONDS = 120;
export const MIN_REFRESH_SECONDS = 30;
/** The Windows `status` read on an interval tick happens at most this often — the 120 s default, whatever the (shorter) refresh interval (E19.S2b). */
export const WINDOWS_READ_MIN_SECONDS = DEFAULT_REFRESH_SECONDS;
/** One day — the schema's `maximum` too. `setInterval` takes a 32-bit delay: above 2^31-1 ms (~24.8 days) Node clamps it to
 * 1 ms, so an unclamped huge setting would poll the daemon every millisecond. */
export const MAX_REFRESH_SECONDS = 86_400;

/** A repeating timer, injectable so tests drive a manual clock; the returned function disarms it. */
export interface Timers {
  every(ms: number, run: () => void): () => void;
  /**
   * A monotonic clock in milliseconds, injected with the timers (E19.S2b): the Windows read's throttle measures from it. Absent, the
   * Windows read is not throttled — every tick reads.
   */
  nowMs?(): number;
}

export interface PollerOptions {
  readonly run: (verb: Verb, target: DistroTarget, options?: RunOptions) => Promise<VerbOutcome>;
  readonly store: OutcomeStore;
  readonly focused: () => boolean;
  readonly refreshSeconds: () => unknown;
  readonly timers: Timers;
  /** What the daemon is asked about — the `wslCare.distro` setting as the client reads it (empty = WSL's default). */
  readonly target: () => string;
  /**
   * The Windows `status` read (E19.S2b). `usable` is the client's own answer to "can the bundled exe run here" — off Windows, or for an
   * extension not on a local disk, nothing is asked. Absent: the poller reads the distribution alone.
   */
  readonly windows?: WindowsStatusSource;
}

/** What the poller needs of the Windows `status` client. */
export interface WindowsStatusSource {
  readonly usable: () => boolean;
  readonly status: () => Promise<VerbOutcome>;
}

/** The interval in whole seconds: the setting when it is a number, between the floor and one day; the default otherwise. */
export function effectiveSeconds(raw: unknown): number {
  if (typeof raw !== 'number' || !Number.isFinite(raw)) {
    return DEFAULT_REFRESH_SECONDS;
  }

  return Math.min(MAX_REFRESH_SECONDS, Math.max(MIN_REFRESH_SECONDS, Math.floor(raw)));
}

/** Failures that say where the daemon is (or is not) — `preview` / `doctor` would end the same way, so they are not asked. */
const TARGET_FAILURES: ReadonlySet<Failure['kind']> = new Set<Failure['kind']>([
  'stopped', 'notWindows', 'wslMissing', 'distroRefused', 'noDefaultDistro', 'wslFailed', 'notInstalled', 'unsupportedDistro', 'wsl1',
]);

function stopsTheOthers(outcome: VerbOutcome): boolean {
  return outcome.kind !== 'answered' && TARGET_FAILURES.has(outcome.kind);
}

function reasonOf(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

/** The target the poller last saw and the generation of its rounds: a round of an older generation is obsolete. */
interface TargetState {
  readonly generation: number;
  readonly lastTarget: string | undefined;
}

/** The state after seeing `target`: the generation moves on only when the target differs from the last one seen. */
function nextTargets(state: TargetState, target: string): TargetState {
  const changed = state.lastTarget !== undefined && target !== state.lastTarget;

  return { generation: changed ? state.generation + 1 : state.generation, lastTarget: target };
}

export class Poller {
  private disarm: (() => void) | undefined;
  private readonly pending = new Set<Promise<unknown>>();
  /** The Windows `status` read in flight, shared by every ask until it ends (E19.S2b). */
  private windowsReading: Promise<void> | undefined;
  /** When the newest Windows read STARTED, on the injected clock — what the tick throttle measures from. */
  private windowsStartedAt: number | undefined;
  /** Replaced, never edited, at every observed target (`nextTargets`). */
  private targets: TargetState = { generation: 0, lastTarget: undefined };

  constructor(private readonly options: PollerOptions) {}

  /** At activation: a focused window asks once and arms the interval. */
  start(): void {
    this.focusChanged(this.options.focused());
  }

  focusChanged(focused: boolean): void {
    this.stopTimer();
    if (focused) {
      void this.tick(true);
      this.arm();
    }
  }

  /** `wslCare.refreshSeconds` changed: re-arm at the new interval (only if armed — an unfocused window stays idle). */
  settingsChanged(): void {
    if (this.disarm !== undefined) {
      this.stopTimer();
      this.arm();
    }
  }

  /** What the interval does: one `status` (and, on Windows, the bundled daemon's), and only while focused (the timer can fire just after focus was lost). An
   * unfocused tick still notices a changed target, so the previous distribution's answers are not left on show. */
  tick(regainedFocus = false): Promise<void> {
    this.observeTarget();
    if (!this.options.focused()) {
      return Promise.resolve();
    }

    return this.track(Promise.all([this.ask(this.begin(), 'status'), this.askWindows(regainedFocus)]).then(() => undefined));
  }

  /**
   * E6.S3: ONE `status`, whatever the focus — the cleanup follower's question (`cleanup/runFollower.ts` decides when, by
   * plan §15j M6: only while something is in flight). Recorded in the store, so the bar and the panel see the answer —
   * stamped with the current target's round like a tick (#23), so an answer for a previous distribution is never stored.
   */
  askStatus(): Promise<VerbOutcome> {
    return this.track(this.ask(this.begin(), 'status'));
  }

  /** Panel open / Refresh / "Start WSL and check": `status` first, then `preview` and `doctor` unless it stopped. */
  refreshPanel(options: RunOptions = {}): Promise<void> {
    return this.track(this.panelRound(options));
  }

  /** Resolves once everything started so far has finished — for tests and the extension-host scenarios. */
  async settled(): Promise<void> {
    while (this.pending.size > 0) {
      await Promise.allSettled([...this.pending]);
    }
  }

  dispose(): void {
    this.stopTimer();
  }

  private async panelRound(options: RunOptions): Promise<void> {
    const round = this.begin();
    this.options.store.setChecking(true);
    try {
      const asking = this.ask(round, 'status', options);
      const windows = this.askWindows(true);
      const status = await asking;
      // preview and doctor follow the distro's status at once — they never wait for the Windows exe (up to its ceiling); "checking" does.
      await this.afterStatus(round, status);
      await windows;
    } finally {
      this.settle(round, () => this.options.store.setChecking(false));
    }
  }

  /** `preview` and `doctor` — unless `status` met a stop, or a round for another target has started since. */
  private async afterStatus(round: number, status: VerbOutcome): Promise<void> {
    if (!this.isCurrent(round)) {
      return;
    }
    await (stopsTheOthers(status) ? this.mirror(round, status) : Promise.all([this.ask(round, 'preview'), this.ask(round, 'doctor')]));
  }

  /** The stop `status` met, recorded for `preview` and `doctor` too — so their rows say why, without a call. */
  private async mirror(round: number, status: VerbOutcome): Promise<void> {
    for (const verb of ['preview', 'doctor'] as const) {
      this.settle(round, () => this.options.store.set(verb, { ...status, verb }));
    }
  }

  private async ask(round: number, verb: PanelVerb, options: RunOptions = {}): Promise<VerbOutcome> {
    const outcome = await this.options.run(verb, DEFAULT_TARGET, options).catch((error: unknown): VerbOutcome => ({ kind: 'unknownFailure', code: undefined, messages: [reasonOf(error)], verb }));
    this.settle(round, () => this.options.store.set(verb, outcome));

    return outcome;
  }

  /**
   * The Windows `status` into its own slot — when the host can run the bundled exe. Never rejects and never reads the distribution's
   * outcome: whatever it ends in (a thrown client included) is stored as the Windows reading and nothing else. `always` is a panel
   * Refresh or a regained focus; an interval tick (`always` false) reads only when the last read STARTED at least
   * {@link WINDOWS_READ_MIN_SECONDS} or the refresh interval ago, so at the 30 s floor the Windows binary's run logs stay at 721 per
   * window-day, not 2 881. A `usable()` that throws is a defect of the probe: nothing is asked and an `unknownFailure` is stored, so it
   * shows in the slot rather than vanishing.
   */
  private askWindows(always: boolean): Promise<void> {
    const windows = this.options.windows;

    return windows === undefined ? Promise.resolve() : this.askUsable(windows, always);
  }

  private askUsable(windows: WindowsStatusSource, always: boolean): Promise<void> {
    const usable = this.usable(windows);
    if (usable === true) {
      return this.shareRead(windows, always);
    }
    if (usable !== false) {
      this.options.store.setWindowsStatus(usable);
    }

    return Promise.resolve();
  }

  /**
   * ONE read at a time, as the client shares one call per verb: a tick or a Refresh while the exe still runs joins it — one start, one
   * store write, so an older answer can never land after a newer one.
   */
  private shareRead(windows: WindowsStatusSource, always: boolean): Promise<void> {
    if (this.windowsWanted(always)) {
      this.windowsStartedAt = this.options.timers.nowMs?.();
      this.windowsReading = this.readWindows(windows).finally(() => { this.windowsReading = undefined; });
    }

    return this.windowsReading ?? Promise.resolve();
  }

  /** A new read starts when none runs and the ask is a Refresh / regained focus, or the tick's throttle has passed. */
  private windowsWanted(always: boolean): boolean {
    return this.windowsReading === undefined && (always || this.windowsDue());
  }

  /** `windows.usable()`: its answer, or the `unknownFailure` of a probe that threw. */
  private usable(windows: WindowsStatusSource): boolean | VerbOutcome {
    try {
      return windows.usable();
    } catch (error: unknown) {
      return { kind: 'unknownFailure', code: undefined, messages: [reasonOf(error)], verb: 'status' };
    }
  }

  /**
   * An interval tick may read the Windows status. At the default interval (120 s) or slower EVERY tick reads — the interval is already
   * the span, and comparing clocks there would skip a tick whenever a timer fires a hair early (a real `setInterval` drifts, and the
   * read's start stamp lands after its own tick), halving the reading to every 240 s. Below it (the 30 s floor) the span is measured from
   * the last read's start, with half a tick interval of slack so jitter cannot make it land every other span.
   */
  private windowsDue(): boolean {
    const intervalMs = effectiveSeconds(this.options.refreshSeconds()) * 1000;

    return intervalMs >= WINDOWS_READ_MIN_SECONDS * 1000 || this.sinceWindowsRead() + intervalMs / 2 >= WINDOWS_READ_MIN_SECONDS * 1000;
  }

  /** Milliseconds since the newest Windows read started, on the injected clock; infinite when there is no clock or no read yet. */
  private sinceWindowsRead(): number {
    const now = this.options.timers.nowMs?.();

    return now === undefined || this.windowsStartedAt === undefined ? Number.POSITIVE_INFINITY : now - this.windowsStartedAt;
  }

  private async readWindows(windows: WindowsStatusSource): Promise<void> {
    const outcome = await windows.status().catch((error: unknown): VerbOutcome => ({ kind: 'unknownFailure', code: undefined, messages: [reasonOf(error)], verb: 'status' }));
    this.options.store.setWindowsStatus(outcome);
  }
  /** Starts a round: its number, after `observeTarget`. */
  private begin(): number {
    this.observeTarget();

    return this.targets.generation;
  }

  /**
   * Notices a target change at ANY boundary — a round starting, a status answered, an answer about to be stored, an
   * unfocused tick — not only when the next round begins (the fix PR's own code round): a change makes every round
   * started so far obsolete and clears what they stored, which is about the previous target.
   */
  private observeTarget(): void {
    const next = nextTargets(this.targets, this.options.target());
    if (next.generation !== this.targets.generation) {
      this.options.store.clear();
    }
    this.targets = next;
  }

  /** `round` is still about the target the setting names now. */
  private isCurrent(round: number): boolean {
    this.observeTarget();

    return round === this.targets.generation;
  }

  /** Writes to the store only while `round` is still about the current target. */
  private settle(round: number, write: () => void): void {
    if (this.isCurrent(round)) {
      write();
    }
  }

  private arm(): void {
    const seconds = effectiveSeconds(this.options.refreshSeconds());
    this.disarm = this.options.timers.every(seconds * 1000, () => { void this.tick(); });
  }

  private stopTimer(): void {
    this.disarm?.();
    this.disarm = undefined;
  }

  private track<T>(work: Promise<T>): Promise<T> {
    this.pending.add(work);
    void work.finally(() => this.pending.delete(work)).catch(() => undefined);

    return work;
  }
}
