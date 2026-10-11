import { FULL_CHECK_ACTIONS, type CleanupController } from '../root/cleanupController';
import { rootFailureText } from '../root/rootFailureText';
import type { ActionIds, RunId } from '../root/rootIds';
import type { HandOffOutcome, HeldPreview, PreviewedAction, PreviewOutcome, RootFailure } from '../root/rootOutcome';
import { DEFAULT_NUMBERS, type Numbers } from '../settings/numbers';
import { safeText } from '../text/safeText';
import type { CleanupJournal, JournalEntry, NewEntry } from './journal';
import { firstModal, missingFromPreview, secondModal, stopModal, type Modal } from './modalText';
import { ARCHIVE_RUN, handOffNotice, RELAY_RUN, type NoticeLevel } from './resultText';
import type { RowId } from './rowIds';
import type { RunFollower } from './runFollower';
import { runWordsOf } from './runWords';

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
 *    minutes, it is taken again and the modals shown over the new numbers (at most `wslCare.cleanup.previewRounds` times);
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
  readonly controller: Pick<CleanupController, 'preview' | 'previewRestore' | 'confirm' | 'stop' | 'runFullCheck'>;
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
  /** The number settings, read at each use (the preview's expiry, the journal's budget in its words); the defaults when absent. */
  readonly numbers?: () => Numbers;
}

/** m8 / §15k #12: a preview older than this when the last modal resolves is taken again before anything is confirmed — the DEFAULT of `wslCare.cleanup.previewExpiryMinutes`. */
export const PREVIEW_EXPIRY_MS = DEFAULT_NUMBERS.previewExpiryMinutes * 60_000;

/** How many times an expired preview is taken again before the flow gives up and says so. */
/** The DEFAULT of `wslCare.cleanup.previewRounds`. */
export const PREVIEW_ROUNDS = DEFAULT_NUMBERS.previewRounds;

export const RETRY_LABEL = 'Retry';

/** *Archive now*'s one id (E10.S1b): A13 alone, never beside a cleanup row. */
const ARCHIVE_IDS: ActionIds = ['A13'];

/** *Stop client-gone relays*'s one id (S7b.2b): A21 alone, never beside a cleanup row. */
const RELAY_IDS: ActionIds = ['A21'];

const BASIS_MAX = 400;

/** The label *Restore* is told by (E10.S2b): its row of `runWords.ts`, never "Cleaning A20". */
const RESTORE_RUN = runWordsOf(['A20'])?.label ?? 'the restore run';

/** How one preview is taken: the ids of a cleanup, *Archive now* or the relay stop, or *Restore*'s entry ids — each through its own controller road. */
type Take = () => Promise<PreviewOutcome>;

/** A wedged run the daemon can stop, as the host read it from `status.running`. */
export interface StopTarget {
  readonly runId: RunId;
  readonly actions: readonly string[];
  /** `status.running.trigger` — only a `manual` run is one the panel confirmed (S7b.2b own review #3: the watch runs A21 alone too). */
  readonly trigger?: string;
}

export type FlowOutcome =
  | { readonly kind: 'busy' }
  | { readonly kind: 'declined' }
  | { readonly kind: 'expired' }
  | { readonly kind: 'refused'; readonly failure: RootFailure }
  | { readonly kind: 'handedOff'; readonly outcome: HandOffOutcome; readonly entry: JournalEntry | undefined }
  | { readonly kind: 'noStatus' }
  /** The journal holds its budget of unshown entries (review C12): nothing was started. */
  | { readonly kind: 'journalFull' }
  /** The preview did not describe every id the confirm would act on (review A3): nothing was confirmed. */
  | { readonly kind: 'incomplete'; readonly missing: readonly string[] }
  /**
   * A preview that leaves nothing to confirm — no modal: S7b.2b, A21's selected nothing (its basis told) or could not read the relays
   * (its reason told); E10.S2b, A20's offers no entry still restorable.
   */
  | { readonly kind: 'nothing'; readonly sentence: string };

/** One pass of a flow, and whether the person asked to run it again. */
interface Pass {
  readonly result: FlowOutcome;
  readonly retry: boolean;
}

const BUSY: FlowOutcome = { kind: 'busy' };
const DECLINED: FlowOutcome = { kind: 'declined' };
function journalFull(entries: number): string {
  return `AI OS Care already follows ${entries} cleanups whose result has not appeared yet; wait for one to end, then try again. Nothing was started.`;
}
const NO_STATUS = "AI OS Care has not read the daemon's status yet; press Refresh, then try again.";

/** The hand-off kinds that may leave a run behind; every other answer is a refusal that wrote nothing. */
const HANDED: ReadonlySet<string> = new Set(['accepted', 'acceptedObserved', 'outcomeUnknown', 'stopping']);

/** A run the panel itself confirmed (`manual`) — or one whose trigger the daemon did not say (an older daemon: adopted, as before). */
function isPanelRun(target: StopTarget): boolean {
  return target.trigger === undefined || target.trigger === 'manual';
}

function sameActions(a: readonly string[], b: readonly string[]): boolean {
  const sorted = [...b].sort();

  return a.length === b.length && [...a].sort().every((x, i) => x === sorted[i]);
}

export class CleanFlow {
  private inFlight = false;

  constructor(private readonly options: CleanFlowOptions) {}

  /** The number settings as they are now. */
  private numbers(): Numbers {
    return this.options.numbers?.() ?? DEFAULT_NUMBERS;
  }

  /** In-memory, OPTIMISTIC only (`common.durable-status`): a modal is open or a call is out in this window. */
  busy(): boolean {
    return this.inFlight;
  }

  /** A row's *Clean* (one id) or *Clean selected* (several) — ONE preview, ONE confirm either way. */
  async clean(rowIds: readonly RowId[], selected: boolean): Promise<FlowOutcome> {
    const pass = await this.exclusive(() => this.cleanOnce(() => this.options.controller.preview(rowIds), selected));

    return pass.retry ? this.clean(rowIds, selected) : pass.result;
  }

  /**
   * *Archive now* (E10.S1b): A13 through the SAME transaction — one flow at a time, the preview, its modal, the age re-check, the
   * journal entry before the confirm, the follower — told as the archive run.
   */
  async archive(): Promise<FlowOutcome> {
    const pass = await this.exclusive(() => this.cleanOnce(() => this.options.controller.preview(ARCHIVE_IDS), false, ARCHIVE_RUN));

    return pass.retry ? this.archive() : pass.result;
  }

  /**
   * *Stop client-gone relays* (S7b.2b): A21 through the SAME transaction — the preview, its modal, the age re-check, the journal
   * entry before the confirm, the follower — told as the relay stop. A preview that selected nothing is told with the daemon's
   * basis (every kept relay and why), one that could not read the relays with its reason; neither opens a modal.
   */
  async relays(): Promise<FlowOutcome> {
    const pass = await this.exclusive(() => this.cleanOnce(() => this.options.controller.preview(RELAY_IDS), false, RELAY_RUN));

    return pass.retry ? this.relays() : pass.result;
  }

  /**
   * *Restore* (E10.S2b): A20 through the SAME transaction, told as the restore run. `texts` are the archive page's selected entry
   * ids as the page sent them — the controller checks their shape (`entryIdsOf`) and refuses before any call; the confirm pipes
   * the ids the HELD preview showed, never these. A preview that offers none still restorable is told and opens no modal.
   */
  async restore(texts: readonly string[]): Promise<FlowOutcome> {
    const pass = await this.exclusive(() => this.cleanOnce(() => this.options.controller.previewRestore(texts), false, RESTORE_RUN));

    return pass.retry ? this.restore(texts) : pass.result;
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

  private async cleanOnce(take: Take, selected: boolean, label?: string): Promise<Pass> {
    const held = await this.confirmedPreview(take, selected);
    if ('kind' in held) {
      return { result: held, retry: false };
    }
    return this.persisted({ kind: 'unresolved', op: 'clean', distro: held.distro, actions: held.ids, since: this.since() }, async (entry) => this.handedOff(entry, await this.options.controller.confirm(held), label ?? held.ids.join(', '), true));
  }

  /** Steps 2–4, at most `previewRounds` times: a preview confirmed in time, or why there is none. */
  private async confirmedPreview(take: Take, selected: boolean): Promise<HeldPreview | FlowOutcome> {
    const rounds = this.numbers().previewRounds;
    for (let round = 0; round < rounds; round += 1) {
      const step = await this.previewRound(take, selected);
      if (step !== 'expired') {
        return step;
      }
      this.tell('info', `The preview was older than ${this.numbers().previewExpiryMinutes} minutes when it was confirmed, so it was taken again — check the new numbers.`);
    }
    this.tell('warn', 'The preview kept expiring before it was confirmed; nothing was started.');

    return { kind: 'expired' };
  }

  private async previewRound(take: Take, selected: boolean): Promise<HeldPreview | FlowOutcome | 'expired'> {
    const outcome = await take();
    if (outcome.kind !== 'previewed') {
      this.tell('error', rootFailureText(outcome).sentence);
      return { kind: 'refused', failure: outcome };
    }
    const missing = missingFromPreview(outcome.preview);
    if (missing.length > 0) {
      this.tell('error', `The daemon's preview did not describe ${missing.join(', ')}; nothing was confirmed — Refresh and try again.`);
      return { kind: 'incomplete', missing };
    }

    return this.nothingOutcome(outcome.preview) ?? this.confirmedRound(outcome.preview, selected);
  }

  /** A relay stop (S7b.2b) or a restore (E10.S2b) whose preview leaves nothing to confirm is told here and ends — no modal; otherwise `undefined`. */
  private nothingOutcome(preview: HeldPreview): FlowOutcome | undefined {
    const nothing = nothingOf(preview);
    if (nothing === '') {
      return undefined;
    }
    this.tell('info', nothing);

    return { kind: 'nothing', sentence: nothing };
  }

  /** The modals, then the age check (§15k #12). */
  private async confirmedRound(preview: HeldPreview, selected: boolean): Promise<HeldPreview | FlowOutcome | 'expired'> {
    if (!(await this.modals(preview, selected))) {
      return DECLINED;
    }

    return this.options.now() - preview.takenAtMs > this.numbers().previewExpiryMinutes * 60_000 ? 'expired' : preview;
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
    const waiting = run === undefined && isPanelRun(target) ? entries.find((e) => e.kind === 'unresolved' && e.distro === distro && sameActions(e.actions, target.actions)) : undefined;
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
      this.tell('warn', journalFull(this.numbers().journalEntries));
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

/** Why a single-action preview leaves nothing to confirm (`runWords.ts` names the run) — '' when it does. */
function nothingOf(preview: HeldPreview): string {
  const id = runWordsOf(preview.ids)?.id;
  if (id === 'A21') {
    return nothingToStop(preview);
  }

  return id === 'A20' ? nothingToRestore(preview) : '';
}

/** E10.S2b: a restore preview that offers no entry still restorable — never an empty `--entry -` list. */
function nothingToRestore(preview: HeldPreview): string {
  return preview.restore?.names.length === 0 ? 'None of the selected archived sessions can be restored now: the daemon offers none that is still removed at its source and verified. Nothing was started — the archive page shows each one\'s state.' : '';
}

/**
 * S7b.2b: why A21's preview leaves nothing to confirm — '' when it holds relays to stop. Unavailable: the daemon's reason. Empty:
 * its basis, which names every kept relay and why ("its stdio is a socket" …) — the answer the person pressed the button for.
 */
function nothingToStop(preview: HeldPreview): string {
  const action = preview.actions.find((a) => a.id === 'A21');

  return action === undefined || !action.available ? unreadRelays(action) : emptyPreview(preview, action);
}

function unreadRelays(action: PreviewedAction | undefined): string {
  return `The daemon could not read the relays: ${safeText(action === undefined ? 'it did not describe A21' : action.reason, BASIS_MAX)}. Nothing was stopped.`;
}

function emptyPreview(preview: HeldPreview, action: PreviewedAction): string {
  if (preview.processes === undefined) {
    return "The daemon's preview carried no relay list, so nothing can be stopped from it — Refresh and try again.";
  }

  return preview.processes.names.length === 0 ? `No relay's client is shown gone, so nothing was stopped. The daemon says: ${safeText(action.basis ?? '', BASIS_MAX)}` : '';
}
