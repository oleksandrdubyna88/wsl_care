import type { Runner } from '../process/runner';
import { Listeners } from '../shared/listeners';
import { SingleFlight } from '../shared/singleFlight';
import { queryRequest } from '../windowsTime/guardScripts';
import { NOT_ASKED_READ, schedulerReadOf, type SchedulerRead } from './schedulerAnswer';

/**
 * The ONE Task Scheduler read in flight (WR-E2 of PLAN_windows_runner.md §3.8; the `guardHost.ts` shape it was lifted from): the
 * newest answer of the query that lists every task under `\wsl-care`, and at most ONE query running at a time. The Windows Time
 * guard and the Windows tasks' part both consume it — neither asks Task Scheduler itself, so one panel refresh is one read.
 *
 * It never polls and owns no timer: the read runs when somebody calls {@link refresh} — the panel opening, becoming visible
 * or being refreshed (`extension.ts`, `panelRefresh`), and an elevated run answering — and never otherwise. The query is
 * bounded by `wslCare.timeouts.windowsTimeGuardQuerySeconds`; on that ceiling the runner ends the PowerShell's process tree
 * (`killTree`, `process/runner.ts` — verified, by pid and creation time, never by image name) and says whether it did, and the read
 * says it timed out.
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
  private readonly listeners = new Listeners();
  /** One query in flight; a refresh during a flight is answered by ONE more after it (the guard's own code review k4). */
  private readonly flight = new SingleFlight(() => this.query(), { started: () => { this.listeners.tell(); }, ended: () => { this.listeners.tell(); } });

  constructor(private readonly deps: SchedulerHostDeps) {}

  /** The newest answer — "checking…" (an `unknown` read) before the first one arrived. */
  current(): SchedulerRead {
    return this.read;
  }

  /** Whether a query is in flight now. */
  checking(): boolean {
    return this.flight.running;
  }

  /** Reads Task Scheduler again (see {@link SingleFlight}): the flight's answer, or the one more it makes. */
  refresh(): Promise<void> {
    return this.flight.request();
  }

  onChange(listener: () => void): () => void {
    return this.listeners.add(listener);
  }

  private async query(): Promise<void> {
    const request = queryRequest(this.deps.env, this.deps.queryTimeoutMs());
    this.read = typeof request === 'string' ? { kind: 'unknown', cause: 'failed', reason: request } : schedulerReadOf(await this.deps.query(request));
    this.listeners.tell();
  }
}
