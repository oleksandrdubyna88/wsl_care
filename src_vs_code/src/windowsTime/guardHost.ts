import type { DurableStore } from '../cleanup/journal';
import type { Runner } from '../process/runner';
import { guardEngineLine, type EngineSource } from '../powershell/engineHost';
import type { SchedulerHost } from '../windowsTasks/schedulerHost';
import type { FixPrompt } from './windowsTimeFix';
import { runGuardOp, type GuardOp, type GuardOutcome } from './guardFlow';
import { clearPending, readPending } from './guardPending';
import { finishedBy, guardStateFromRead, guardView, type GuardState, type GuardView } from './guardState';
import type { GuardOptions } from './guardTask';

/**
 * The host side of the Windows Time guard (PLAN_windows_time_task.md D4, D5, D8): the state Task Scheduler last answered,
 * ONE install / remove at a time in this window (and, through the persisted pending run, across windows and reloads), and the
 * panel's line derived from both. It never polls: the read runs when the panel opens or is refreshed and after an elevated
 * run answered.
 *
 * Since WR-E2 (PLAN_windows_runner.md §3.8) the Task Scheduler read is NOT this host's: it is the ONE read of every task under
 * `\wsl-care` (`windowsTasks/schedulerHost.ts`), held in flight once for the guard and for the Windows tasks' part alike. The
 * guard consumes it — its state is that read's guard task, by name — and settles its pending run whenever any answer arrives.
 */

/** The surfaces the guard uses — real, or recorded in Test mode (`guardUi.ts`). */
export interface GuardUi {
  readonly show: (text: string) => Promise<void>;
  readonly confirm: (prompt: FixPrompt) => Promise<boolean>;
  /** The ELEVATED install / remove. */
  readonly run: Runner;
  /** The unelevated status query — the runner the shared Task Scheduler read (`SchedulerHost`) is given. */
  readonly query: Runner;
  readonly report: (message: string, failed: boolean) => void;
}

export interface GuardHostDeps {
  readonly env: Readonly<Record<string, string | undefined>>;
  readonly options: () => GuardOptions;
  readonly opTimeoutMs: () => number;
  readonly ui: GuardUi;
  /** The ONE Task Scheduler read (WR-E2), shared with the Windows tasks' part. */
  readonly scheduler: SchedulerHost;
  readonly durable: DurableStore;
  readonly nowUtcMs: () => number;
  /** The presentation edge's local-time formatter for the last run. */
  readonly formatInstant: (iso: string) => string;
  /** Which PowerShell runs the install / remove elevated (`powershell/engineHost.ts`, shared with *Start Windows Time*). */
  readonly engine: EngineSource;
}

export class WindowsTimeGuardHost {
  private opRunning = false;
  private readonly listeners = new Set<() => void>();
  private readonly durable: DurableStore;

  constructor(private readonly deps: GuardHostDeps) {
    // Every write of the pending run re-derives the panel's line at once (the buttons grey before the UAC prompt opens).
    this.durable = { get: (key) => deps.durable.get(key), update: async (key, value) => { await deps.durable.update(key, value); this.changed(); } };
    // Whoever asks Task Scheduler — this host, or the panel's refresh — the guard's line follows, and a pending run it shows finished is cleared.
    deps.scheduler.onChange(() => { this.schedulerChanged(); });
  }

  view(): GuardView {
    return guardView(this.current(), this.deps.options(), readPending(this.deps.durable, this.deps.nowUtcMs()), this.deps.formatInstant, this.deps.scheduler.checking(), guardEngineLine(this.deps.engine.last()));
  }

  /** A setting changed: the comparison with what the current settings would install is re-derived at once (gemini). */
  settingsChanged(): void {
    this.changed();
  }

  current(): GuardState {
    return guardStateFromRead(this.deps.scheduler.current());
  }

  onChange(listener: () => void): () => void {
    this.listeners.add(listener);
    return () => { this.listeners.delete(listener); };
  }

  /**
   * Reads Task Scheduler again — the shared read: a call while one is in flight is answered by ONE more read after it (never by
   * the flight itself, which may have started before the caller's change — own code review k4); further calls share that one.
   */
  async refresh(): Promise<void> {
    await this.deps.scheduler.refresh();
    await this.settlePending();
    this.changed();
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
        engine: this.deps.engine,
      });
    } finally {
      this.opRunning = false;
    }
  }

  /** The shared read started or answered: the line follows, and an answer settles a pending run Task Scheduler shows finished. */
  private schedulerChanged(): void {
    this.changed();
    if (!this.deps.scheduler.checking()) {
      void this.settlePending().then(() => { this.changed(); });
    }
  }

  /** A pending run whose end Task Scheduler already shows (after a reload, say) is cleared, not waited out. */
  private async settlePending(): Promise<void> {
    const pending = readPending(this.deps.durable, this.deps.nowUtcMs());
    if (pending !== undefined && !this.opRunning && finishedBy(this.current(), pending, this.deps.options())) {
      await clearPending(this.deps.durable);
    }
  }

  private changed(): void {
    this.listeners.forEach((listener) => listener());
  }
}
