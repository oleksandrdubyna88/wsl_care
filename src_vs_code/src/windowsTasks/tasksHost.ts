import type { DurableStore } from '../cleanup/journal';
import type { WindowsCareClient } from '../client/WindowsCareClient';
import type { RenderResult } from '../client/windowsTasks';
import type { EngineSource } from '../powershell/engineSource';
import { Listeners } from '../shared/listeners';
import { SingleFlight } from '../shared/singleFlight';
import type { SchedulerHost } from '../scheduler/schedulerHost';
import { runTasksOp, type TasksOp, type TasksOutcome, type TasksUi } from './tasksFlow';
import { ceilingPassed, clearPending, readPending, type PendingRecord } from './tasksPending';
import { finishedBy, NOT_PROBED, windowsTasksState, type CopyState, type WindowsTasksState } from './tasksState';
import { windowsTasksView, type WindowsTasksView } from './tasksView';

/**
 * The host of the Windows tasks' part of the panel (WR-E2 of PLAN_windows_runner.md §3.8, widened by WR-E3 with the install and remove
 * flows): the staged copy's last probe, ONE probe in flight, the part's lines and buttons derived from that, from the shared Task
 * Scheduler read (`scheduler/schedulerHost.ts`) and from the pending run the flows persist (`tasksPending.ts`) — and ONE flow at a time
 * in this window (across windows and reloads the persisted record holds the buttons).
 *
 * <p><b>The trigger.</b> {@link refresh} probes the staged copy and NOTHING else: the Task Scheduler read is the scheduler's, asked
 * once by the panel's refresh (`extension.ts`, `panelRefresh`: the panel opening, becoming visible, or its *Refresh* — the same
 * moments the guard's read has always run at), and this host, like the guard host, only FOLLOWS its change (the coai code round,
 * finding 0). It owns no timer and polls nothing. One panel refresh is ONE Task Scheduler read and ONE probe of the staged copy; after a
 * flow answered, the host asks for one read and one probe itself (`run`'s `afterRun`) — the panel's line follows the READ, never
 * the flow's own outcome.</p>
 *
 * <p><b>The probe.</b> The client hashes the bundled exe itself ({@link TasksClient.bundledDigest}, a file read) and `tasks render
 * watch` is asked of the staged copy named by that digest. If it ANSWERS (registrable or not, or the daemon refusing its
 * configuration) the copy is staged and `tasks render archive` is asked too; if the file is not there ("not found") nothing is
 * staged and NOTHING further is rendered; any other ending — a blocked start, a timeout, a refusal — is an unreadable copy with its
 * reason, never "not staged". A failure of the probe never rejects the refresh and never touches the Task Scheduler read.</p>
 */

/** The calls of the client the probe and the flows make. */
export type TasksClient = Pick<WindowsCareClient, 'bundledDigest' | 'tasksRender' | 'tasksStage' | 'tasksStageRemove' | 'exe'>;

export interface TasksHostDeps {
  /** The ONE Task Scheduler read — the guard's too. */
  readonly scheduler: SchedulerHost;
  readonly client: TasksClient;
  /** `BUNDLED_DAEMON`: the version of the build this extension carries. */
  readonly pin: string;
  /** The presentation edge's local-time formatter for a last run. */
  readonly formatInstant: (iso: string) => string;
  /** The flows' surfaces — real, or recorded in Test mode (`tasksUi.ts`). */
  readonly ui: TasksUi;
  /** Which PowerShell runs the install and remove (`powershell/engineHost.ts`, shared with the elevated flows). */
  readonly engine: EngineSource;
  /** `globalState`: where a pending run is persisted. */
  readonly durable: DurableStore;
  readonly nowUtcMs: () => number;
  readonly sleep: (ms: number) => Promise<void>;
  /** `wslCare.timeouts.windowsTasksSeconds`, in milliseconds, read at every flow. */
  readonly opTimeoutMs: () => number;
  /** `wslCare.timeouts.windowsTasksStageSeconds`, in milliseconds. */
  readonly stageTimeoutMs: () => number;
  /** `wslCare.windowsTasks.busyRetrySeconds`, in milliseconds. */
  readonly busyRetryMs: () => number;
  /** The folder the extension runs from — no task may name it. */
  readonly extensionFolder: string;
  /** The extension's log (`LogOutputChannel.error`). */
  readonly log: (line: string) => void;
}

/** What the daemon gives for a render it ANSWERED: an answer (registrable or not), or the refusal of the configuration. */
function answered(render: RenderResult): render is Extract<RenderResult, { readonly kind: 'registrable' | 'notRegistrable' | 'configurationInError' }> {
  return render.kind === 'registrable' || render.kind === 'notRegistrable' || render.kind === 'configurationInError';
}

/** A start that found no file — Node's `ENOENT` — is "not staged"; every other failure to start (a blocked image, say) is not. */
function notFound(render: RenderResult): boolean {
  return render.kind === 'failed' && /\bENOENT\b/.test(render.reason);
}

function reasonOf(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

/** The first render's meaning for the copy, before the second task is asked: staged (an answer), not staged, or unreadable. */
function copyAfterFirst(first: RenderResult, digest: string): CopyState | undefined {
  if (answered(first)) {
    return undefined;
  }
  if (notFound(first)) {
    return { kind: 'notStaged', digest };
  }

  return { kind: 'unreadable', digest, reason: first.reason };
}

export class TasksHost {
  private copy: CopyState = NOT_PROBED;
  private opRunning = false;
  private readonly listeners = new Listeners();
  /** The persisted record, seen through this host: every write of it re-derives the part's lines at once (the buttons grey before the PowerShell starts). */
  private readonly durable: DurableStore;
  /** ONE probe in flight; a refresh during a flight is answered by ONE more after it (the same rule as the shared read). */
  private readonly probing = new SingleFlight(() => this.probeOnce(), { ended: () => { this.settled(); } });

  constructor(private readonly deps: TasksHostDeps) {
    this.durable = { get: (key) => deps.durable.get(key), update: async (key, value) => { await deps.durable.update(key, value); this.listeners.tell(); } };
    // The shared read started or answered: the lines follow it, and an answer settles a pending run Task Scheduler shows finished.
    deps.scheduler.onChange(() => { this.schedulerChanged(); });
  }

  state(): WindowsTasksState {
    return windowsTasksState(this.deps.scheduler.current(), this.copy);
  }

  view(): WindowsTasksView {
    return windowsTasksView(this.state(), this.deps.pin, this.deps.formatInstant, { pending: readPending(this.deps.durable, this.deps.nowUtcMs()), busy: this.opRunning });
  }

  onChange(listener: () => void): () => void {
    return this.listeners.add(listener);
  }

  /**
   * The probe of the staged copy, for the panel's refresh (which also asks the shared Task Scheduler read, once, itself). It follows
   * the {@link SingleFlight} rule — a call while one is in flight is answered by ONE more after it, further calls share that one.
   */
  refresh(): Promise<void> {
    return this.probing.request();
  }

  /** One install / remove at a time in this window; a second press while one runs starts nothing. */
  async run(op: TasksOp): Promise<TasksOutcome | undefined> {
    if (this.opRunning) {
      return undefined;
    }
    this.opRunning = true;
    this.listeners.tell();
    try {
      return await runTasksOp(op, {
        client: this.deps.client,
        read: () => this.deps.scheduler.current(),
        engine: this.deps.engine,
        ui: this.deps.ui,
        durable: this.durable,
        nowUtcMs: this.deps.nowUtcMs,
        sleep: this.deps.sleep,
        timeoutMs: this.deps.opTimeoutMs,
        stageTimeoutMs: this.deps.stageTimeoutMs,
        busyRetryMs: this.deps.busyRetryMs,
        afterRun: () => { this.readAgain(); },
        extensionFolder: this.deps.extensionFolder,
        log: this.deps.log,
      });
    } finally {
      this.opRunning = false;
      this.listeners.tell();
    }
  }

  /** After a flow answered: ONE Task Scheduler read and ONE probe of the copy, detached — a fault of either is the read's own, never the flow's. */
  private readAgain(): void {
    void this.deps.scheduler.refresh().catch(() => undefined);
    void this.refresh().catch(() => undefined);
  }

  private schedulerChanged(): void {
    this.listeners.tell();
    if (!this.deps.scheduler.checking()) {
      this.settled();
    }
  }

  /** The probe or the read ended: a pending run they already show finished is cleared (detached — a store that refuses leaves it for the next read). */
  private settled(): void {
    void this.settlePending().catch(() => undefined).then(() => { this.listeners.tell(); });
  }

  /** A pending run whose end Task Scheduler and the probe already show (after a reload, say) is cleared, not waited out. */
  private async settlePending(): Promise<void> {
    const pending = readPending(this.deps.durable, this.deps.nowUtcMs());
    if (pending !== undefined && this.settleable(pending)) {
      await clearPending(this.durable);
    }
  }

  /**
   * Whether the read may settle `pending`: no flow of this window runs, the record's PowerShell must already have answered or timed out (another
   * window's live run is not ours to release) and the read and the probe show the end.
   */
  private settleable(pending: PendingRecord): boolean {
    return !this.opRunning && ceilingPassed(pending, this.deps.nowUtcMs()) && finishedBy(this.state(), pending);
  }

  private async probeOnce(): Promise<void> {
    this.copy = await this.copyOnce();
    this.listeners.tell();
  }

  private async copyOnce(): Promise<CopyState> {
    try {
      const digest = await this.deps.client.bundledDigest();
      switch (digest.kind) {
        case 'unavailable':
          return { kind: 'unavailable' };
        case 'failed':
          return { kind: 'digestUnreadable', reason: digest.reason };
        default:
          return await this.copyOf(digest.digest);
      }
    } catch (e: unknown) {
      return { kind: 'digestUnreadable', reason: reasonOf(e) };
    }
  }

  private async copyOf(digest: string): Promise<CopyState> {
    try {
      const first = await this.deps.client.tasksRender('watch', digest);
      const early = copyAfterFirst(first, digest);

      return early ?? { kind: 'staged', digest, renders: { watch: first, archive: await this.deps.client.tasksRender('archive', digest) } };
    } catch (e: unknown) {
      return { kind: 'unreadable', digest, reason: reasonOf(e) };
    }
  }
}
