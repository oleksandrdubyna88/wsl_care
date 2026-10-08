import type { DurableStore } from '../cleanup/journal';
import type { Runner } from '../process/runner';
import type { FixPrompt } from './windowsTimeFix';
import { runGuardOp, type GuardOp, type GuardOutcome } from './guardFlow';
import { clearPending, readPending } from './guardPending';
import { queryRequest } from './guardScripts';
import { finishedBy, guardStateOf, guardView, NOT_ASKED, type GuardState, type GuardView } from './guardState';
import type { GuardOptions } from './guardTask';

/**
 * The host side of the Windows Time guard (PLAN_windows_time_task.md D4, D5, D8): the state Task Scheduler last answered,
 * ONE status query in flight at a time, ONE install / remove at a time in this window (and, through the persisted pending
 * run, across windows and reloads), and the panel's line derived from both. It never polls: the query runs when the panel
 * opens or is refreshed and after an elevated run answered.
 */

/** The surfaces the guard uses — real, or recorded in Test mode (`guardUi.ts`). */
export interface GuardUi {
  readonly show: (text: string) => Promise<void>;
  readonly confirm: (prompt: FixPrompt) => Promise<boolean>;
  /** The ELEVATED install / remove. */
  readonly run: Runner;
  /** The unelevated status query. */
  readonly query: Runner;
  readonly report: (message: string, failed: boolean) => void;
}

export interface GuardHostDeps {
  readonly env: Readonly<Record<string, string | undefined>>;
  readonly options: () => GuardOptions;
  readonly opTimeoutMs: () => number;
  readonly queryTimeoutMs: () => number;
  readonly ui: GuardUi;
  readonly durable: DurableStore;
  readonly nowUtcMs: () => number;
  /** The presentation edge's local-time formatter for the last run. */
  readonly formatInstant: (iso: string) => string;
}

export class WindowsTimeGuardHost {
  private state: GuardState = NOT_ASKED;
  private querying: Promise<void> | undefined;
  /** A refresh asked while a query was in flight: that query may predate what the caller just did, so one more runs. */
  private again = false;
  private opRunning = false;
  private readonly listeners = new Set<() => void>();
  private readonly durable: DurableStore;

  constructor(private readonly deps: GuardHostDeps) {
    // Every write of the pending run re-derives the panel's line at once (the buttons grey before the UAC prompt opens).
    this.durable = { get: (key) => deps.durable.get(key), update: async (key, value) => { await deps.durable.update(key, value); this.changed(); } };
  }

  view(): GuardView {
    return guardView(this.state, this.deps.options(), readPending(this.deps.durable, this.deps.nowUtcMs()), this.deps.formatInstant, this.querying !== undefined);
  }

  /** A setting changed: the comparison with what the current settings would install is re-derived at once (gemini). */
  settingsChanged(): void {
    this.changed();
  }

  current(): GuardState {
    return this.state;
  }

  onChange(listener: () => void): () => void {
    this.listeners.add(listener);
    return () => { this.listeners.delete(listener); };
  }

  /**
   * Reads Task Scheduler again. A call while one is in flight is answered by ONE more query after it (never by the flight
   * itself, which may have started before the caller's change — own code review k4); further calls share that one.
   */
  refresh(): Promise<void> {
    if (this.querying !== undefined) {
      this.again = true;
      return this.querying;
    }
    this.querying = this.queries().finally(() => { this.querying = undefined; this.changed(); });
    this.changed();
    return this.querying;
  }

  private async queries(): Promise<void> {
    do {
      this.again = false;
      await this.query();
    } while (this.again);
  }

  /** One install / remove at a time in this window; a second press while one runs starts nothing. */
  async run(op: GuardOp): Promise<GuardOutcome | undefined> {
    if (this.opRunning) {
      return undefined;
    }
    this.opRunning = true;
    try {
      return await runGuardOp(op, {
        env: this.deps.env,
        options: this.deps.options,
        timeoutMs: this.deps.opTimeoutMs,
        show: this.deps.ui.show,
        confirm: this.deps.ui.confirm,
        run: this.deps.ui.run,
        report: this.deps.ui.report,
        durable: this.durable,
        nowUtcMs: this.deps.nowUtcMs,
        afterRun: () => { void this.refresh(); },
      });
    } finally {
      this.opRunning = false;
    }
  }

  private async query(): Promise<void> {
    const request = queryRequest(this.deps.env, this.deps.queryTimeoutMs());
    this.state = typeof request === 'string' ? { kind: 'unknown', reason: request } : guardStateOf(await this.deps.ui.query(request));
    await this.settlePending();
    this.changed();
  }

  /** A pending run whose end Task Scheduler already shows (after a reload, say) is cleared, not waited out. */
  private async settlePending(): Promise<void> {
    const pending = readPending(this.deps.durable, this.deps.nowUtcMs());
    if (pending !== undefined && !this.opRunning && finishedBy(this.state, pending, this.deps.options())) {
      await clearPending(this.deps.durable);
    }
  }

  private changed(): void {
    this.listeners.forEach((listener) => listener());
  }
}
