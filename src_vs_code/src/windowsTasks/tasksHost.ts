import type { WindowsCareClient } from '../client/WindowsCareClient';
import type { RenderResult } from '../client/windowsTasks';
import { Listeners } from '../shared/listeners';
import { SingleFlight } from '../shared/singleFlight';
import type { SchedulerHost } from './schedulerHost';
import { NOT_PROBED, windowsTasksState, type CopyState, type WindowsTasksState } from './tasksState';
import { windowsTasksView, type WindowsTasksView } from './tasksView';

/**
 * The host of the Windows tasks' part of the panel (WR-E2 of PLAN_windows_runner.md §3.8): the staged copy's last probe, ONE probe
 * in flight, and the part's lines derived from that and from the shared Task Scheduler read (`schedulerHost.ts`). E3 widens it with
 * the install and remove flows; this story only READS.
 *
 * <p><b>The trigger.</b> {@link refresh} is the only way anything is asked, and the panel's refresh is its only caller
 * (`extension.ts`, `panelRefresh`: the panel opening, becoming visible, or its *Refresh* — the same moments the guard's read has
 * always run at). It owns no timer and polls nothing. One refresh is ONE Task Scheduler read (the guard consumes the same one) and
 * ONE probe of the staged copy.</p>
 *
 * <p><b>The probe.</b> The client hashes the bundled exe itself ({@link TasksClient.bundledDigest}, a file read) and `tasks render
 * watch` is asked of the staged copy named by that digest. If it ANSWERS (registrable or not, or the daemon refusing its
 * configuration) the copy is staged and `tasks render archive` is asked too; if the file is not there ("not found") nothing is
 * staged and NOTHING further is rendered; any other ending — a blocked start, a timeout, a refusal — is an unreadable copy with its
 * reason, never "not staged". A failure of the probe never rejects the refresh and never touches the Task Scheduler read.</p>
 */

/** The two calls of the client the probe makes. */
export type TasksClient = Pick<WindowsCareClient, 'bundledDigest' | 'tasksRender'>;

export interface TasksHostDeps {
  /** The ONE Task Scheduler read — the guard's too. */
  readonly scheduler: SchedulerHost;
  readonly client: TasksClient;
  /** `BUNDLED_DAEMON`: the version of the build this extension carries. */
  readonly pin: string;
  /** The presentation edge's local-time formatter for a last run. */
  readonly formatInstant: (iso: string) => string;
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
  private readonly listeners = new Listeners();
  /** ONE probe in flight; a refresh during a flight is answered by ONE more after it (the same rule as the shared read). */
  private readonly probing = new SingleFlight(() => this.probeOnce(), { ended: () => { this.listeners.tell(); } });

  constructor(private readonly deps: TasksHostDeps) {
    // The shared read started or answered: the lines follow it.
    deps.scheduler.onChange(() => { this.listeners.tell(); });
  }

  state(): WindowsTasksState {
    return windowsTasksState(this.deps.scheduler.current(), this.copy);
  }

  view(): WindowsTasksView {
    return windowsTasksView(this.state(), this.deps.pin, this.deps.formatInstant);
  }

  onChange(listener: () => void): () => void {
    return this.listeners.add(listener);
  }

  /**
   * The panel's refresh: the shared Task Scheduler read and the probe of the staged copy, side by side. Each follows the
   * {@link SingleFlight} rule — a call while one is in flight is answered by ONE more after it, further calls share that one.
   */
  async refresh(): Promise<void> {
    await Promise.all([this.deps.scheduler.refresh(), this.probing.request()]);
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
