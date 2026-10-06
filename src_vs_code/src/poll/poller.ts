import type { Failure, VerbOutcome } from '../client/outcome';
import type { Verb } from '../client/verbs';
import type { RunOptions } from '../client/WslCareClient';
import type { OutcomeStore, PanelVerb } from '../state/outcomeStore';

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
 * Each `status` run opens one run-log file in the daemon (M1); the numbers per window-day are measured by
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
/** One day — the schema's `maximum` too. `setInterval` takes a 32-bit delay: above 2^31-1 ms (~24.8 days) Node clamps it to
 * 1 ms, so an unclamped huge setting would poll the daemon every millisecond. */
export const MAX_REFRESH_SECONDS = 86_400;

/** A repeating timer, injectable so tests drive a manual clock; the returned function disarms it. */
export interface Timers {
  every(ms: number, run: () => void): () => void;
}

export interface PollerOptions {
  readonly run: (verb: Verb, options?: RunOptions) => Promise<VerbOutcome>;
  readonly store: OutcomeStore;
  readonly focused: () => boolean;
  readonly refreshSeconds: () => unknown;
  readonly timers: Timers;
  /** What the daemon is asked about — the `wslCare.distro` setting as the client reads it (empty = WSL's default). */
  readonly target: () => string;
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
  'stopped', 'notWindows', 'wslMissing', 'distroRefused', 'noDefaultDistro', 'wslFailed', 'notInstalled', 'unsupportedDistro',
]);

function stopsTheOthers(outcome: VerbOutcome): boolean {
  return outcome.kind !== 'answered' && TARGET_FAILURES.has(outcome.kind);
}

function reasonOf(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

export class Poller {
  private disarm: (() => void) | undefined;
  private readonly pending = new Set<Promise<unknown>>();
  /** Bumped when a round starts for another target than the previous round; a round of an older number is obsolete. */
  private generation = 0;
  private lastTarget: string | undefined;

  constructor(private readonly options: PollerOptions) {}

  /** At activation: a focused window asks once and arms the interval. */
  start(): void {
    this.focusChanged(this.options.focused());
  }

  focusChanged(focused: boolean): void {
    this.stopTimer();
    if (focused) {
      void this.tick();
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

  /** What the interval does: one `status`, and only while focused (the timer can fire just after focus was lost). */
  tick(): Promise<void> {
    if (!this.options.focused()) {
      return Promise.resolve();
    }

    return this.track(this.ask(this.begin(), 'status').then(() => undefined));
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
      const status = await this.ask(round, 'status', options);
      await this.afterStatus(round, status);
    } finally {
      this.settle(round, () => this.options.store.setChecking(false));
    }
  }

  /** `preview` and `doctor` — unless `status` met a stop, or a round for another target has started since. */
  private async afterStatus(round: number, status: VerbOutcome): Promise<void> {
    if (round !== this.generation) {
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
    const outcome = await this.options.run(verb, options).catch((error: unknown): VerbOutcome => ({ kind: 'unknownFailure', code: undefined, messages: [reasonOf(error)], verb }));
    this.settle(round, () => this.options.store.set(verb, outcome));

    return outcome;
  }

  /**
   * Starts a round: its number, after making every older round obsolete when the target changed since the previous
   * round (and clearing what they stored, which is about the previous target).
   */
  private begin(): number {
    const target = this.options.target();
    if (this.lastTarget !== undefined && target !== this.lastTarget) {
      this.generation += 1;
      this.options.store.clear();
    }
    this.lastTarget = target;

    return this.generation;
  }

  /** Writes to the store only while `round` is still about the current target. */
  private settle(round: number, write: () => void): void {
    if (round === this.generation) {
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
