import type { ReadOutcome, VerbOutcome } from '../client/outcome';
import type { RunRead } from '../client/verbs';
import type { CleanupControls } from '../panel/view';
import type { CleanupController } from '../root/cleanupController';
import { runIdOf } from '../root/rootIds';
import type { OutcomeStore } from '../state/outcomeStore';
import { CleanFlow, type CleanUi, type FlowOutcome } from './cleanFlow';
import { deriveCleanup } from './cleanupView';
import { CleanupJournal, type DurableStore } from './journal';
import { resultNotice, type Notice } from './resultText';
import type { RowId } from './rowIds';
import { noticeText, unlinked } from '../text/safeText';
import { FOLLOW_POLL, RunFollower, type OneShot, type RunResult } from './runFollower';

/**
 * The host side of the cleanup buttons in one place (E6.S3): the journal over `globalState`, the durable poll, the host
 * transaction, the results this window showed — and the controls the panel renders, derived from all of it and the
 * store's snapshot. `extension.ts` builds one and hands its methods to the panel; nothing here touches a webview.
 */

export interface CleanupHostOptions {
  readonly durable: DurableStore;
  readonly controller: Pick<CleanupController, 'preview' | 'confirm' | 'stop' | 'runFullCheck'>;
  readonly read: (request: RunRead) => Promise<ReadOutcome>;
  readonly outcomes: OutcomeStore;
  /** `status` through the poller and the store, whatever the focus — the follower's one question. */
  readonly askStatus: () => Promise<VerbOutcome>;
  /** The panel's round (`status`, `preview`, `doctor`) — asked once after a terminal answer, for "Docker after". */
  readonly refreshPanel: () => Promise<void>;
  readonly focused: () => boolean;
  readonly ui: CleanUi;
  readonly timers: OneShot;
  readonly now: () => number;
  readonly wallNow: () => number;
  /** One line to the extension's log (its output channel). */
  readonly log: (line: string) => void;
}

/** The terminal answers a window keeps for its *Last cleanup* section — the newest few, in memory (the daemon keeps the rest). */
export const RESULTS_KEPT = 5;

/** Every cleanup surface through the ONE road (review A1): a notification's sentence sanitised and unlinked, a modal unlinked. */
function sanitised(ui: CleanUi): CleanUi {
  return {
    confirm: (modal) => ui.confirm({ ...modal, message: unlinked(modal.message), detail: unlinked(modal.detail) }),
    notify: (level, sentence, actions) => ui.notify(level, noticeText(sentence), actions),
  };
}

function reasonOf(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

export class CleanupHost {
  readonly journal: CleanupJournal;
  readonly follower: RunFollower;
  readonly flow: CleanFlow;
  private results: readonly Notice[] = [];
  private readonly listeners = new Set<() => void>();
  private readonly unsubscribe: readonly (() => void)[];
  private readonly ui: CleanUi;

  constructor(private readonly options: CleanupHostOptions) {
    this.ui = sanitised(options.ui);
    this.journal = new CleanupJournal(options.durable);
    this.follower = new RunFollower({
      journal: this.journal, status: options.askStatus, read: options.read, show: (result) => this.shown(result),
      afterTerminal: options.refreshPanel, focused: options.focused, wallNow: options.wallNow, timers: options.timers,
      fault: (error) => this.faulted('following a cleanup', error),
    });
    this.flow = new CleanFlow({
      controller: options.controller, journal: this.journal, follower: this.follower, ui: this.ui, now: options.now, wallNow: options.wallNow,
      distro: () => this.distro(), changed: () => this.emit(),
    });
    this.unsubscribe = [this.journal.onChange(() => this.emit()), options.outcomes.onChange(() => this.follower.kick())];
  }

  /** At activation and on a focus gain: follow whatever the journal or the daemon says is in flight. */
  start(): void {
    this.follower.kick();
  }

  controls(): CleanupControls {
    return deriveCleanup(this.options.outcomes.snapshot(), this.state()).controls;
  }

  /** A row's *Clean* or *Clean selected* — never rejects: a fault is told and logged (review C11, the detached edge). */
  clean(rowIds: readonly RowId[], selected: boolean): Promise<FlowOutcome | undefined> {
    return this.edge('a cleanup', () => this.flow.clean(rowIds, selected));
  }

  /** *Run full check now* — refused here when the controls grey it (review A4), whatever the page sent. */
  runFullCheck(): Promise<FlowOutcome | undefined> {
    const controls = this.controls();
    if (!controls.fullCheck) {
      void this.ui.notify('warn', `Run full check now is not available: ${controls.reason === '' ? controls.state : controls.reason}.`);
      return Promise.resolve(undefined);
    }

    return this.edge('a full check', () => this.flow.fullCheck());
  }

  /** `index` names a run of the stoppable list the host derives NOW; its run id is checked once more before the call. */
  stop(index: number): Promise<FlowOutcome | undefined> {
    const target = deriveCleanup(this.options.outcomes.snapshot(), this.state()).stoppable[index];
    if (target === undefined || runIdOf(target.runId) === undefined) {
      void this.ui.notify('warn', 'That run can no longer be stopped from here — the daemon reports it differently now.');
      return Promise.resolve(undefined);
    }

    return this.edge('a stop', () => this.flow.stop(target));
  }

  onChange(listener: () => void): () => void {
    this.listeners.add(listener);
    return () => { this.listeners.delete(listener); };
  }

  dispose(): void {
    this.follower.dispose();
    this.unsubscribe.forEach((unsubscribe) => unsubscribe());
  }

  /** The detached edge of a button's work: whatever it throws is told (sanitised) and logged, never swallowed. */
  private async edge(what: string, work: () => Promise<FlowOutcome>): Promise<FlowOutcome | undefined> {
    try {
      return await work();
    } catch (error) {
      this.faulted(what, error);
      return undefined;
    }
  }

  private faulted(what: string, error: unknown): void {
    this.options.log(`cleanup: ${what} failed: ${reasonOf(error)}${error instanceof Error && error.stack !== undefined ? `\n${error.stack}` : ''}`);
    void this.ui.notify('error', `WSL Care could not complete ${what}: ${reasonOf(error)}`);
  }

  private state(): { entries: ReturnType<CleanupJournal['entries']>; results: readonly Notice[]; flowBusy: boolean } {
    return { entries: this.journal.entries(), results: this.results, flowBusy: this.flow.busy() };
  }

  private shown(result: RunResult): void {
    const notice = resultNotice(result, FOLLOW_POLL.ceilingMs);
    this.results = [notice, ...this.results].slice(0, RESULTS_KEPT);
    void this.ui.notify(notice.level, notice.sentence);
    this.emit();
  }

  private distro(): string | undefined {
    const status = this.options.outcomes.snapshot().status;

    return status !== undefined && status.kind === 'answered' ? status.distro : undefined;
  }

  private emit(): void {
    this.listeners.forEach((listener) => listener());
  }
}
