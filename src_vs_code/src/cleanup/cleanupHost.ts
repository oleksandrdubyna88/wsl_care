import type { ReadOutcome, VerbOutcome } from '../client/outcome';
import type { RunRead } from '../client/verbs';
import type { CleanupControls } from '../panel/view';
import { actionGate, type GateOpen } from '../root/actionGate';
import { confirmNeeds, previewNeeds, type CleanupController } from '../root/cleanupController';
import { rootFailureText } from '../root/rootFailureText';
import { runningOf } from '../root/rootAnswers';
import { runIdOf } from '../root/rootIds';
import type { RootFailure, RunningBlock } from '../root/rootOutcome';
import type { OutcomeStore, Snapshot } from '../state/outcomeStore';
import { statusBodyOf } from '../state/statusBody';
import { CleanFlow, type CleanUi, type FlowOutcome } from './cleanFlow';
import { deriveCleanup } from './cleanupView';
import { CleanupJournal, type DurableStore } from './journal';
import { resultNotice, type Notice } from './resultText';
import type { RowId } from './rowIds';
import { DEFAULT_TARGET, type DistroTarget } from '../wsl/distroTarget';
import { noticeText, unlinked } from '../text/safeText';
import { RunFollower, type OneShot, type RunResult } from './runFollower';
import { runWordsOf } from './runWords';
import { DEFAULT_NUMBERS, type Numbers } from '../settings/numbers';
import { deriveRelays, stopRelaysBlocker, type RelaysControls } from '../relays/relaysView';
import { daemonLimitsOf, type DaemonLimits } from '../shared/daemonLimits';

/**
 * The host side of the cleanup buttons in one place (E6.S3): the journal over `globalState`, the durable poll, the host
 * transaction, the results this window showed — and the controls the panel renders, derived from all of it and the
 * store's snapshot. `extension.ts` builds one and hands its methods to the panel; nothing here touches a webview.
 */

export interface CleanupHostOptions {
  readonly durable: DurableStore;
  readonly controller: Pick<CleanupController, 'preview' | 'previewRestore' | 'confirm' | 'stop' | 'runFullCheck'>;
  readonly read: (request: RunRead, target: DistroTarget) => Promise<ReadOutcome>;
  readonly outcomes: OutcomeStore;
  /** `status` through the poller and the store, whatever the focus — the follower's one question. */
  readonly askStatus: (target: DistroTarget) => Promise<VerbOutcome>;
  /** The panel's round (`status`, `preview`, `doctor`) — asked once after a terminal answer, for "Docker after". */
  readonly refreshPanel: () => Promise<void>;
  readonly focused: () => boolean;
  readonly ui: CleanUi;
  readonly timers: OneShot;
  readonly now: () => number;
  readonly wallNow: () => number;
  /** One line to the extension's log (its output channel). */
  readonly log: (line: string) => void;
  /** The number settings (`settings/numbers.ts`), read at each use; the defaults when absent. */
  readonly numbers?: () => Numbers;
}

/** The DEFAULT of `wslCare.cleanup.resultsKept`: the terminal answers a window keeps for its *Last cleanup* section — the newest few, in memory (the daemon keeps the rest). */
export const RESULTS_KEPT = DEFAULT_NUMBERS.resultsKept;

/** Every cleanup surface through the ONE road (review A1): a notification's sentence sanitised and unlinked, a modal unlinked. */
function sanitised(ui: CleanUi): CleanUi {
  return {
    confirm: (modal) => ui.confirm({ ...modal, message: unlinked(modal.message), detail: unlinked(modal.detail) }),
    notify: (level, sentence, actions) => ui.notify(level, noticeText(sentence), actions),
  };
}

/** The store's newest `status` body, or `undefined` while none answered — through the one reader, `state/statusBody.ts`. */
function statusInStore(outcomes: OutcomeStore): Readonly<Record<string, unknown>> | undefined {
  return statusBodyOf(outcomes.snapshot());
}

/** The daemon's own retention and clock-skew allowance, from the store's newest status (`shared/daemonLimits.ts`: the fallback until it says). */
function limitsInStore(outcomes: OutcomeStore): DaemonLimits {
  return daemonLimitsOf(statusInStore(outcomes));
}

/** Everything A20's preview AND its confirm need (E10.S2b) — the controller's own definitions, asked once. */
const RESTORE_NEEDS: readonly string[] = [...new Set([...previewNeeds(['A20']), ...confirmNeeds(['A20'])])];

/**
 * What the newest `status` does not offer of a restore (E10.S2b) — '' when it offers everything: the controller's own gate
 * (`actionGate`, the capabilities the authority) over the preview's and the confirm's needs, and A20 reported in `status.actions`.
 */
function restoreUnoffered(snapshot: Snapshot): string {
  const status = snapshot.status;
  if (status?.kind !== 'answered' || status.answer.verb !== 'status') {
    return 'the daemon has not answered status yet';
  }

  return gateWords(actionGate(status.answer.body, status.daemonVersion, RESTORE_NEEDS));
}

function gateWords(gate: GateOpen | RootFailure): string {
  if (gate.kind !== 'open') {
    return gate.kind === 'actionsUnavailable' ? `this daemon does not offer ${gate.missing.join(', ')} — update the daemon` : rootFailureText(gate).label;
  }

  return gate.reported.includes('A20') ? '' : 'this daemon does not report A20 — update the daemon';
}

/** A run of A20 alone — a restore (`runWords.ts`). */
function isRestoreRun(actions: readonly string[]): boolean {
  return runWordsOf(actions)?.id === 'A20';
}

/** The running states in which a run still holds the daemon's run lock. */
const HOLDING: ReadonlySet<string> = new Set(['queued', 'live', 'wedged']);

function daemonRestoring(running: RunningBlock | undefined): boolean {
  return running !== undefined && running.state.kind === 'known' && HOLDING.has(running.state.value) && isRestoreRun(running.actions);
}

/** The running block of the store's newest status, whoever asked it (review C4: the poll can start from idle). */
function runningInStore(outcomes: OutcomeStore): RunningBlock | undefined {
  const body = statusInStore(outcomes);

  return body === undefined ? undefined : runningOf(body);
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
    const numbers = (): Numbers => options.numbers?.() ?? DEFAULT_NUMBERS;
    const limits = (): DaemonLimits => limitsInStore(options.outcomes);
    this.journal = new CleanupJournal(options.durable, options.wallNow, numbers, () => limits().futureSkewMs);
    this.follower = new RunFollower({
      journal: this.journal, status: options.askStatus, read: options.read, show: (result) => this.shown(result),
      afterTerminal: options.refreshPanel, focused: options.focused, running: () => runningInStore(options.outcomes), wallNow: options.wallNow, timers: options.timers,
      fault: (error) => this.faulted('following a cleanup', error), numbers, limits,
    });
    this.flow = new CleanFlow({
      controller: options.controller, journal: this.journal, follower: this.follower, ui: this.ui, now: options.now, wallNow: options.wallNow,
      distro: () => this.distro(), changed: () => this.emit(), numbers,
    });
    this.unsubscribe = [this.journal.onChange(() => this.emit()), options.outcomes.onChange(() => this.follower.kick())];
  }

  /** At activation and on a focus gain: follow whatever the journal or the daemon says is in flight. */
  start(): void {
    this.follower.kick();
  }

  /** The controls derived from `aim`'s own `status` (E18.W1a review): the default's unless a PICKED distribution is named. */
  controls(aim: DistroTarget = DEFAULT_TARGET): CleanupControls {
    return deriveCleanup(this.snapshotOf(aim), this.state()).controls;
  }

  /** A row's *Clean* or *Clean selected* — never rejects: a fault is told and logged (review C11, the detached edge). */
  clean(rowIds: readonly RowId[], selected: boolean, target: DistroTarget): Promise<FlowOutcome | undefined> {
    return this.edge('a cleanup', () => this.flow.clean(rowIds, selected, target));
  }

  /**
   * *Archive now* (E10.S1b) — refused here, and told, while the cleanup controls are greyed (a run in flight, the journal full);
   * the archive's own conditions (a folder set, no archive flow busy, A13 offered with its capabilities) are `ArchiveHost.archiveNow`'s.
   */
  archiveNow(target: DistroTarget): Promise<FlowOutcome | undefined> {
    const controls = this.controls(target);
    if (!controls.enabled) {
      void this.ui.notify('warn', `Archive now is not available: ${controls.reason === '' ? controls.state : controls.reason}.`);
      return Promise.resolve(undefined);
    }

    return this.edge('the archive run', () => this.flow.archive(target));
  }

  /** S7b.2b: the client-gone relays' line and button — the daemon's `interopRelays` and this host's own controls. */
  relays(): RelaysControls {
    const controls = this.controls();

    return deriveRelays(this.options.outcomes.snapshot(), { enabled: controls.enabled, reason: controls.reason === '' ? controls.state : controls.reason });
  }

  /**
   * *Stop client-gone relays* (S7b.2b) — refused here, and told, on the same conditions the button greys on: the palette's command
   * and a stale page send the same bare request.
   */
  stopRelays(target: DistroTarget): Promise<FlowOutcome | undefined> {
    const controls = this.controls(target);
    const why = stopRelaysBlocker(this.snapshotOf(target), { enabled: controls.enabled, reason: controls.reason === '' ? controls.state : controls.reason });
    if (why !== '') {
      void this.ui.notify('warn', `Stop client-gone relays is not available: ${why}.`);
      return Promise.resolve(undefined);
    }

    return this.edge('the relay stop', () => this.flow.relays(target));
  }

  /**
   * Why *Restore* cannot start now (E10.S2b) — `''` when it can: the newest `status` does not offer A20 with every capability its
   * preview and confirm need, or the cleanup controls are greyed (a run in flight, the journal full, no status yet). The archive page
   * greys its button by this and the host refuses by it again, whatever the page sent; the controller's gate checks again at the call.
   */
  restoreBlocker(aim: DistroTarget = DEFAULT_TARGET): string {
    const unoffered = restoreUnoffered(this.snapshotOf(aim));

    return unoffered === '' ? this.greyedWords(aim) : unoffered;
  }

  /** Why the cleanup controls of `aim` are greyed — '' when they are not. */
  private greyedWords(aim: DistroTarget): string {
    const controls = this.controls(aim);
    if (controls.enabled) {
      return '';
    }

    return controls.reason === '' ? controls.state : controls.reason;
  }

  /** *Restore* the archive page's selected entry ids (E10.S2b) — refused here, and told, while `restoreBlocker()` names a reason. */
  restore(texts: readonly string[], target: DistroTarget): Promise<FlowOutcome | undefined> {
    const blocker = this.restoreBlocker(target);
    if (blocker !== '') {
      void this.ui.notify('warn', `Restore is not available: ${blocker}.`);
      return Promise.resolve(undefined);
    }

    return this.edge('the restore run', () => this.flow.restore(texts, target));
  }

  /**
   * Whether a restore is in flight (E10.S2b, `common.durable-status`): this window's journal follows a run of A20 alone, or the
   * daemon's `status.running` holds one (another window's, or one started before a reload). The archive page reads it again when it
   * turns false — the restored sessions' states change only then.
   */
  restoreInFlight(): boolean {
    return daemonRestoring(runningInStore(this.options.outcomes)) || this.journal.entries().some((entry) => entry.op === 'clean' && isRestoreRun(entry.actions));
  }

  /** *Run full check now* — refused here when the controls grey it (review A4), whatever the page sent. */
  runFullCheck(target: DistroTarget): Promise<FlowOutcome | undefined> {
    const controls = this.controls(target);
    if (!controls.fullCheck) {
      void this.ui.notify('warn', `Run full check now is not available: ${controls.reason === '' ? controls.state : controls.reason}.`);
      return Promise.resolve(undefined);
    }

    return this.edge('a full check', () => this.flow.fullCheck(target));
  }

  /** `index` names a run of the stoppable list the host derives NOW; its run id is checked once more before the call. */
  stop(index: number, aim: DistroTarget): Promise<FlowOutcome | undefined> {
    const target = deriveCleanup(this.snapshotOf(aim), this.state()).stoppable[index];
    if (target === undefined || runIdOf(target.runId) === undefined) {
      void this.ui.notify('warn', 'That run can no longer be stopped from here — the daemon reports it differently now.');
      return Promise.resolve(undefined);
    }

    return this.edge('a stop', () => this.flow.stop(target, aim));
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
    void this.ui.notify('error', `AI OS Care could not complete ${what}: ${reasonOf(error)}`);
  }

  /** The snapshot a target's controls read: the default's, or the picked distribution's own (`OutcomeStore.snapshotOf`). */
  private snapshotOf(aim: DistroTarget): Snapshot {
    return aim.kind === 'picked' ? this.options.outcomes.snapshotOf(aim.distro) : this.options.outcomes.snapshot();
  }

  private state(): { entries: ReturnType<CleanupJournal['entries']>; results: readonly Notice[]; flowBusy: boolean } {
    return { entries: this.journal.entries(), results: this.results, flowBusy: this.flow.busy() };
  }

  private shown(result: RunResult): void {
    const notice = resultNotice(result, this.follower.poll().ceilingMs);
    this.results = [notice, ...this.results].slice(0, (this.options.numbers?.() ?? DEFAULT_NUMBERS).resultsKept);
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
