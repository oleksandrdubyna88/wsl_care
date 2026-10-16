import type { Runner } from '../process/runner';
import { queryRequest } from '../windowsTime/guardScripts';
import { NOT_ASKED_READ, schedulerReadOf, type SchedulerRead } from './schedulerAnswer';

/**
 * The ONE Task Scheduler read in flight (WR-E2 of PLAN_windows_runner.md §3.8; the `guardHost.ts` shape it was lifted from): the
 * newest answer of the query that lists every task under `\wsl-care`, and at most ONE query running at a time. The Windows Time
 * guard and the Windows tasks' part both consume it — neither asks Task Scheduler itself, so one panel refresh is one read.
 *
 * It never polls and owns no timer: the read runs when somebody calls {@link refresh} — the panel opening, becoming visible
 * or being refreshed (`extension.ts`, `panelRefresh`), and an elevated run answering — and never otherwise. The query is
 * bounded by `wslCare.timeouts.windowsTimeGuardQuerySeconds`; on that ceiling the runner ends the PowerShell AND its process
 * tree (`killTree`, `process/runner.ts` — by pid, never by image name), and the read says it timed out.
 */

export interface SchedulerHostDeps {
  readonly env: Readonly<Record<string, string | undefined>>;
  /** `wslCare.timeouts.windowsTimeGuardQuerySeconds`, in milliseconds, read at every query. */
  readonly queryTimeoutMs: () => number;
  /** The unelevated runner — the real one, or the guard recorder's in Test mode. */
  readonly query: Runner;
}

export class SchedulerHost {
  private read: SchedulerRead = NOT_ASKED_READ;
  private querying: Promise<void> | undefined;
  /** A refresh asked while a query was in flight: that query may predate what the caller just did, so one more runs. */
  private again = false;
  private readonly listeners = new Set<() => void>();

  constructor(private readonly deps: SchedulerHostDeps) {}

  /** The newest answer — "checking…" (an `unknown` read) before the first one arrived. */
  current(): SchedulerRead {
    return this.read;
  }

  /** Whether a query is in flight now. */
  checking(): boolean {
    return this.querying !== undefined;
  }

  /**
   * Reads Task Scheduler again. A call while one is in flight is answered by ONE more query after it (never by the flight
   * itself, which may have started before the caller's change — own code review k4 of the guard); further calls share that one.
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

  onChange(listener: () => void): () => void {
    this.listeners.add(listener);
    return () => { this.listeners.delete(listener); };
  }

  private async queries(): Promise<void> {
    do {
      this.again = false;
      await this.query();
    } while (this.again);
  }

  private async query(): Promise<void> {
    const request = queryRequest(this.deps.env, this.deps.queryTimeoutMs());
    this.read = typeof request === 'string' ? { kind: 'unknown', cause: 'failed', reason: request } : schedulerReadOf(await this.deps.query(request));
    this.changed();
  }

  private changed(): void {
    this.listeners.forEach((listener) => listener());
  }
}
