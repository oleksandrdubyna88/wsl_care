import { FULL_CHECK_ACTIONS, type CleanupController } from '../root/cleanupController';
import { rootFailureText } from '../root/rootFailureText';
import type { RunId } from '../root/rootIds';
import type { HandOffOutcome, HeldPreview, RootFailure } from '../root/rootOutcome';
import { MAX_ENTRIES, type CleanupJournal, type JournalEntry, type NewEntry } from './journal';
import { firstModal, missingFromPreview, secondModal, stopModal, type Modal } from './modalText';
import { handOffNotice, type NoticeLevel } from './resultText';
import type { RowId } from './rowIds';
import type { RunFollower } from './runFollower';

/**
 * The cleanup buttons' HOST transaction (E6.S3, plan §7.3, §15j M8, m8, m9; §15k #3, #12, #19). The page sends only a
 * closed message (`panel/messages.ts`); everything after it happens here, in this order, and nothing of it is the page's:
 *
 * 1. **one flow at a time in this window** (m9): a second press while a modal is open or a call is out is told so and
 *    starts nothing — the controller's per-distribution slot and the daemon's run lock stay the authorities behind it;
 * 2. the **preview** through the controller (`act <ids> --preview` as root), its answer HELD there;
 * 3. the **native modal** (`showWarningMessage({ modal: true })`): what each action removes, A4 bound to its list, A5 / A6 /
 *    A7 "re-checked at run time" — and a **second** modal naming the setting for A5, A6Unused, A8, A11, A12;
 * 4. the preview's **age re-checked after the LAST modal**, immediately before the confirm (§15k #12): older than five
 *    minutes, it is taken again and the modals shown over the new numbers (at most `PREVIEW_ROUNDS` times);
 * 5. the in-flight state **persisted** — an `unresolved` journal entry — BEFORE the call goes out (`common.durable-status`
 *    rule 1), then the **confirm** (ONE `act` call for every id: *Clean selected* is one run);
 * 6. the answer: a run id becomes a `run` entry the follower polls to its end; an unknown outcome without one stays
 *    `unresolved` for the follower to resolve; a refusal removes the entry and is told in its own words — the daemon
 *    refusing the piped list (its 10 s stdin ceiling, §15k #19) with a **Retry** that runs the whole flow again.
 *
 * <p>The in-memory flag (`busy()`) is OPTIMISTIC only: the panel's button state comes from the daemon's `status.running`
 * and the journal (`cleanupView.ts`), which a reload does not lose.</p>
 */

/** The two VS Code surfaces the flow uses: the native modal, and a notification with optional buttons. */
export interface CleanUi {
  confirm(modal: Modal): Promise<boolean>;
  notify(level: NoticeLevel, sentence: string, actions?: readonly string[]): Promise<string | undefined>;
}

export interface CleanFlowOptions {
  readonly controller: Pick<CleanupController, 'preview' | 'confirm' | 'stop' | 'runFullCheck'>;
  readonly journal: CleanupJournal;
  readonly follower: Pick<RunFollower, 'started' | 'kick'>;
  readonly ui: CleanUi;
  /** The controller's monotonic clock — a held preview's `takenAtMs` is on it. */
  readonly now: () => number;
  readonly wallNow: () => number;
  /** The distribution the newest status answered for — where a full check or a stop is written down. */
  readonly distro: () => string | undefined;
  /** The optimistic in-flight flag changed (the panel re-renders). */
  readonly changed: () => void;
}

/** m8 / §15k #12: a preview older than this when the last modal resolves is taken again before anything is confirmed. */
export const PREVIEW_EXPIRY_MS = 5 * 60_000;

/** How many times an expired preview is taken again before the flow gives up and says so. */
export const PREVIEW_ROUNDS = 3;

export const RETRY_LABEL = 'Retry';

/** A wedged run the daemon can stop, as the host read it from `status.running`. */
export interface StopTarget {
  readonly runId: RunId;
  readonly actions: readonly string[];
}

export type FlowOutcome =
  | { readonly kind: 'busy' }
  | { readonly kind: 'declined' }
  | { readonly kind: 'expired' }
  | { readonly kind: 'refused'; readonly failure: RootFailure }
  | { readonly kind: 'handedOff'; readonly outcome: HandOffOutcome; readonly entry: JournalEntry | undefined }
  | { readonly kind: 'noStatus' }
  /** The journal holds `MAX_ENTRIES` unshown entries (review C12): nothing was started. */
  | { readonly kind: 'journalFull' }
  /** The preview did not describe every id the confirm would act on (review A3): nothing was confirmed. */
  | { readonly kind: 'incomplete'; readonly missing: readonly string[] };

/** One pass of a flow, and whether the person asked to run it again. */
interface Pass {
  readonly result: FlowOutcome;
  readonly retry: boolean;
}

const BUSY: FlowOutcome = { kind: 'busy' };
const DECLINED: FlowOutcome = { kind: 'declined' };
const JOURNAL_FULL = `WSL Care already follows ${MAX_ENTRIES} cleanups whose result has not appeared yet; wait for one to end, then try again. Nothing was started.`;
const NO_STATUS = "WSL Care has not read the daemon's status yet; press Refresh, then try again.";

/** The hand-off kinds that may leave a run behind; every other answer is a refusal that wrote nothing. */
const HANDED: ReadonlySet<string> = new Set(['accepted', 'acceptedObserved', 'outcomeUnknown', 'stopping']);

function sameActions(a: readonly string[], b: readonly string[]): boolean {
  const sorted = [...b].sort();

  return a.length === b.length && [...a].sort().every((x, i) => x === sorted[i]);
}

export class CleanFlow {
  private inFlight = false;

  constructor(private readonly options: CleanFlowOptions) {}

  /** In-memory, OPTIMISTIC only (`common.durable-status`): a modal is open or a call is out in this window. */
  busy(): boolean {
    return this.inFlight;
  }

  /** A row's *Clean* (one id) or *Clean selected* (several) — ONE preview, ONE confirm either way. */
  async clean(rowIds: readonly RowId[], selected: boolean): Promise<FlowOutcome> {
    const pass = await this.exclusive(() => this.cleanOnce(rowIds, selected));

    return pass.retry ? this.clean(rowIds, selected) : pass.result;
  }

  /** *Stop* — only ever for a wedged run the host read from `status.running` (`cleanupView.ts` decides which). */
  async stop(target: StopTarget): Promise<FlowOutcome> {
    return (await this.exclusive(() => this.stopOnce(target))).result;
  }

  /** *Run full check now* (§15j M9): no modal — a full run that is not the timer's measures and does not act. */
  async fullCheck(): Promise<FlowOutcome> {
    return (await this.exclusive(() => this.fullCheckOnce())).result;
  }

  private async exclusive(body: () => Promise<Pass>): Promise<Pass> {
    if (this.inFlight) {
      this.tell('warn', 'A cleanup is already being confirmed in this window; finish it first.');
      return { result: BUSY, retry: false };
    }
    this.flag(true);
    try {
      return await body();
    } finally {
      this.flag(false);
    }
  }

  private async cleanOnce(rowIds: readonly RowId[], selected: boolean): Promise<Pass> {
    const held = await this.confirmedPreview(rowIds, selected);
    if ('kind' in held) {
      return { result: held, retry: false };
    }
    return this.persisted({ kind: 'unresolved', op: 'clean', distro: held.distro, actions: held.ids, since: this.since() }, async (entry) => this.handedOff(entry, await this.options.controller.confirm(held), held.ids.join(', '), true));
  }

  /** Steps 2–4, at most `PREVIEW_ROUNDS` times: a preview confirmed in time, or why there is none. */
  private async confirmedPreview(rowIds: readonly RowId[], selected: boolean): Promise<HeldPreview | FlowOutcome> {
    for (let round = 0; round < PREVIEW_ROUNDS; round += 1) {
      const step = await this.previewRound(rowIds, selected);
      if (step !== 'expired') {
        return step;
      }
      this.tell('info', 'The preview was older than 5 minutes when it was confirmed, so it was taken again — check the new numbers.');
    }
    this.tell('warn', 'The preview kept expiring before it was confirmed; nothing was cleaned.');

    return { kind: 'expired' };
  }

  private async previewRound(rowIds: readonly RowId[], selected: boolean): Promise<HeldPreview | FlowOutcome | 'expired'> {
    const outcome = await this.options.controller.preview(rowIds);
    if (outcome.kind !== 'previewed') {
      this.tell('error', rootFailureText(outcome).sentence);
      return { kind: 'refused', failure: outcome };
    }
    const missing = missingFromPreview(outcome.preview);
    if (missing.length > 0) {
      this.tell('error', `The daemon's preview did not describe ${missing.join(', ')}; nothing was confirmed — Refresh and try again.`);
      return { kind: 'incomplete', missing };
    }

    return this.confirmedRound(outcome.preview, selected);
  }

  /** The modals, then the age check (§15k #12). */
  private async confirmedRound(preview: HeldPreview, selected: boolean): Promise<HeldPreview | FlowOutcome | 'expired'> {
    if (!(await this.modals(preview, selected))) {
      return DECLINED;
    }

    return this.options.now() - preview.takenAtMs > PREVIEW_EXPIRY_MS ? 'expired' : preview;
  }

  private async modals(preview: HeldPreview, selected: boolean): Promise<boolean> {
    if (!(await this.options.ui.confirm(firstModal(preview, selected)))) {
      return false;
    }
    const second = secondModal(preview.ids);

    return second === undefined ? true : this.options.ui.confirm(second);
  }

  private async stopOnce(target: StopTarget): Promise<Pass> {
    const distro = this.options.distro();
    if (distro === undefined || !(await this.options.ui.confirm(stopModal(target.runId)))) {
      return this.notStarted(distro);
    }
    const followed = await this.followedFor(target, distro);
    const stop = async (entry: JournalEntry): Promise<Pass> => this.handedOff(entry, await this.options.controller.stop(target.runId), `run ${target.runId}`, followed === undefined);

    return followed === undefined ? this.persisted({ kind: 'run', op: 'stop', distro, actions: target.actions, since: this.since(), runId: target.runId }, stop) : stop(followed);
  }

  /**
   * The entry that already follows this run (review B4): its run entry — or an UNRESOLVED confirm of this distribution with
   * exactly its actions, which this run is, adopted here so the stop and the confirm are one entry, never two.
   */
  private async followedFor(target: StopTarget, distro: string): Promise<JournalEntry | undefined> {
    const entries = this.options.journal.entries();
    const run = entries.find((e) => e.kind === 'run' && e.runId === target.runId);
    const waiting = run === undefined ? entries.find((e) => e.kind === 'unresolved' && e.distro === distro && sameActions(e.actions, target.actions)) : undefined;
    if (waiting === undefined) {
      return run;
    }
    const { id, ...rest } = waiting;
    await this.options.journal.replace(id, { ...rest, kind: 'run', runId: target.runId });

    return { ...rest, id, kind: 'run', runId: target.runId };
  }

  private async fullCheckOnce(): Promise<Pass> {
    const distro = this.options.distro();
    if (distro === undefined) {
      return this.notStarted(distro);
    }
    return this.persisted({ kind: 'unresolved', op: 'fullCheck', distro, actions: FULL_CHECK_ACTIONS, since: this.since() }, async (entry) => this.handedOff(entry, await this.options.controller.runFullCheck(), 'the full check', true));
  }

  /** No status yet (told), or a declined modal. */
  private notStarted(distro: string | undefined): Pass {
    if (distro === undefined) {
      this.tell('warn', NO_STATUS);
      return { result: { kind: 'noStatus' }, retry: false };
    }

    return { result: DECLINED, retry: false };
  }

  /** Step 5: written BEFORE the call — or, with the journal full (review C12), nothing is started and the person is told. */
  private async persisted(entry: NewEntry, call: (entry: JournalEntry) => Promise<Pass>): Promise<Pass> {
    const added = await this.options.journal.add(entry);
    if (added === undefined) {
      this.tell('warn', JOURNAL_FULL);
      return { result: { kind: 'journalFull' }, retry: false };
    }
    this.options.follower.started(added.id);

    return call(added);
  }

  /** Step 6. `owned`: this flow wrote the entry, so a refusal removes it — a stop of a run already followed leaves it. */
  private async handedOff(entry: JournalEntry, outcome: HandOffOutcome, label: string, owned: boolean): Promise<Pass> {
    const kept = owned ? await this.settled(entry, outcome) : entry;
    this.options.follower.kick();
    const notice = handOffNotice(outcome, label, entry.op);
    const result: FlowOutcome = { kind: 'handedOff', outcome, entry: kept };
    if (outcome.kind !== 'shownListRefused') {
      this.tell(notice.level, notice.sentence);
      return { result, retry: false };
    }

    return { result, retry: (await this.options.ui.notify(notice.level, notice.sentence, [RETRY_LABEL])) === RETRY_LABEL };
  }

  /** The entry after the answer: removed on a refusal, the run once it has a run id, unresolved while it has none. */
  private async settled(entry: JournalEntry, outcome: HandOffOutcome): Promise<JournalEntry | undefined> {
    if (!HANDED.has(outcome.kind)) {
      await this.options.journal.remove(entry.id);
      return undefined;
    }
    const runId = (outcome as { readonly runId?: RunId }).runId;
    if (runId === undefined) {
      return entry;
    }
    const { id, ...rest } = entry;
    await this.options.journal.replace(id, { ...rest, kind: 'run', runId });

    return { ...rest, id, kind: 'run', runId };
  }

  /** A notification is never awaited unless its answer is needed: VS Code resolves it only when it is dismissed. */
  private tell(level: NoticeLevel, sentence: string): void {
    void this.options.ui.notify(level, sentence);
  }

  private flag(value: boolean): void {
    this.inFlight = value;
    this.options.changed();
  }

  private since(): string {
    return new Date(this.options.wallNow()).toISOString();
  }
}

