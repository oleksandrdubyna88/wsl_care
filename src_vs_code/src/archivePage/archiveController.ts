import type { JsonObject, ReadOutcome, VerbOutcome } from '../client/outcome';
import type { RunRead } from '../client/verbs';
import { failureText } from '../failureText';
import type { ViewLevel } from '../panel/view';
import { ARCHIVE_LIST_CAPABILITY } from '../shared/archiveCapabilities';
import { parseArchiveMessage, type ArchiveMessage, type IndexedMessage } from './archiveMessages';
import { keptIn, listEntries, monthPick, restoreCeiling, selectableId, selectableIds } from './archiveSelection';
import type { ArchivePageView } from './archiveView';
import { buildArchiveView, buildSelectionView, monthKey, monthsOf, type ArchiveHostState, type ListState, type MonthGroup, type RestoreFacts } from './archiveViewModel';

/**
 * The Archive page's host side (plan §15s D6, D9, E10.S2a; E10.S2b) — vscode-free, so every decision is tested without a window
 * (`archiveController.test.ts`); `archivePanel.ts` wires it to a real `WebviewPanel`. It makes ONE read, the client's
 * unprivileged `archive list --json` (`client/verbs.ts`); the one change it can start is a *Restore*, handed to the cleanup host.
 *
 * - **The read is the host's.** On the page's first `ready` and on *Refresh* — a returning tab is shown what is held and asks
 *   nothing (the Logs page's review C5). A daemon whose newest `status` does not advertise `archive.list` is not asked; the page
 *   says to update it. While `status` has not answered, the daemon is asked and its own refusal shown.
 * - **An answer to an older read is dropped** (a generation count): the newest read wins. The view carries that generation, and
 *   every index message must carry it back (E10.S2b, the S2a own review's #8): an index from an older view is refused, never
 *   applied to a list it was not chosen from.
 * - **A month is opened by its INDEX into the months the host holds**, checked against them here; it is kept open by agent and
 *   month (`monthKey`), so a refreshed list whose indexes moved keeps the same month open. An index the list no longer holds is
 *   told on the page and opens nothing.
 * - **The restore selection is held by ENTRY ID** (E10.S2b, `archiveSelection.ts`), replaced whole on every change; a new list
 *   keeps only the ids it still holds as selectable and says how many it dropped. *Restore* hands the ids as TEXT to the cleanup
 *   host (`options.restore.start`) — this module imports nothing of `root/`; the controller makes them `EntryIds`, the one road in.
 * - **A restore's state is the cleanup host's** (`options.restore.facts`: `status.running` and the journal, so it survives a
 *   reload). When it stops being in flight, the list is read again and the selection cleared — the restored entries then read
 *   *restored*.
 * - **The full session paths are the host's toggle**, off on every open (`ArchiveController` is built per window).
 * - A page message outside the closed set (`archiveMessages.ts`) is dropped before anything happens.
 */

/** The cleanup host's *Restore*, as the page's host sees it — wired by `extension.ts` (`CleanupHost.restore` and its facts). */
export interface ArchiveRestore {
  readonly facts: () => RestoreFacts;
  /** Starts the restore transaction over these entry ids (as text); resolves when its flow ended (handed off, declined, refused). */
  readonly start: (entryIds: readonly string[]) => Promise<unknown>;
}

export interface ArchiveControllerOptions {
  readonly read: (request: RunRead) => Promise<ReadOutcome>;
  /** The newest `status` outcome (the store's): the daemon's capabilities and limits. */
  readonly status: () => VerbOutcome | undefined;
  readonly post: (view: ArchivePageView) => void;
  readonly restore: ArchiveRestore;
}

const UNSUPPORTED = 'This daemon does not list its archive (archive.list) — update the daemon to browse it here.';
const STALE_INDEX = 'The archive list changed since the page was drawn — try again on the list shown now.';
const NO_CEILING = 'This daemon does not say how many sessions one restore takes — update the daemon to restore from here.';

const IDLE: ListState = { kind: 'idle' };
const READING: ListState = { kind: 'reading' };

function statusBody(status: VerbOutcome | undefined): JsonObject | undefined {
  return status?.kind === 'answered' && status.answer.verb === 'status' ? status.answer.body : undefined;
}

/** Whether the newest `status` says the daemon cannot list — `false` while no status answered (the daemon is then asked). */
export function cannotList(status: VerbOutcome | undefined): boolean {
  const capabilities = statusBody(status)?.capabilities;

  return Array.isArray(capabilities) && !capabilities.includes(ARCHIVE_LIST_CAPABILITY);
}

function stateOf(outcome: ReadOutcome): ListState {
  return outcome.kind === 'read' ? { kind: 'read', distro: outcome.distro, body: outcome.body } : { kind: 'failed', sentence: failureText(outcome).sentence };
}

/** What a new list dropped from the selection, said — or '' when it dropped nothing. */
function droppedNotice(dropped: number): string {
  if (dropped === 0) {
    return '';
  }

  return dropped === 1 ? '1 selected session can no longer be restored and was taken out of the selection.' : `${dropped} selected sessions can no longer be restored and were taken out of the selection.`;
}

function notSelectable(entry: unknown): string {
  return entry === undefined ? 'The archive no longer lists that session — Refresh to see what it holds now.' : 'That session cannot be restored from here — only verified sessions removed at their source can.';
}

/** What *Select month* says when it took fewer than the month offered — or ''. */
function cutNotice(pick: { readonly added: readonly string[]; readonly offered: number }, ceiling: number): string {
  return pick.added.length < pick.offered ? `That month holds ${pick.offered} sessions to restore; the newest ${pick.added.length} were selected — one restore takes at most ${ceiling}.` : '';
}

/** "select fewer" when the selection is past the ceiling — or ''. */
function overCeiling(size: number, ceiling: number | undefined): string {
  return ceiling !== undefined && size > ceiling ? `One restore takes at most ${ceiling} — select fewer.` : '';
}

const MONTH_GONE = 'The archive no longer lists that month — Refresh to see what it holds now.';

export class ArchiveController {
  private list: ListState = IDLE;
  /** The open months by `monthKey` — replaced whole on every change, never edited (the code round's #1). */
  private expanded: ReadonlySet<string> = new Set<string>();
  /** The selected entry ids (E10.S2b) — replaced whole on every change, never edited. */
  private selection: ReadonlySet<string> = new Set<string>();
  private pathsShown = false;
  private notice = '';
  private noticeLevel: ViewLevel = 'none';
  private generation = 0;
  private rendered: number | undefined;
  /** The read in flight, shared by every Refresh until it ends. */
  private reading: Promise<void> | undefined;
  /** A restore was in flight at the last look — its end reads the list again (E10.S2b). */
  private restoring = false;
  /** The selection bar last posted — a restore fact that changes nothing on the page posts nothing. */
  private postedSelection = '';
  /** *Restore* was pressed and the daemon's preview is not back yet (the code round's #1): page-local, optimistic only. */
  private starting = false;

  constructor(private readonly options: ArchiveControllerOptions) {}

  view(): ArchivePageView {
    return buildArchiveView(this.state());
  }

  /**
   * The panel was closed (own review #2): the next page starts afresh — the held answer, the open months, the selection and the
   * paths toggle are dropped, so its `ready` reads the list again and the full session paths are off on every open. A read still
   * in flight is dropped too (its generation is past).
   */
  pageClosed(): void {
    this.generation += 1;
    this.list = IDLE;
    this.reading = undefined;
    this.expanded = new Set<string>();
    this.selection = new Set<string>();
    this.pathsShown = false;
    this.notice = '';
  }

  lastRendered(): number | undefined {
    return this.rendered;
  }

  /**
   * The store's `status` changed (own review #3): the capability gate follows it — a daemon that now advertises `archive.list` is
   * read, one that no longer does ends the held list; any other change leaves the page as it is (no rebuild, no lost focus) unless
   * the restore's state moved (`restoreChanged`).
   */
  statusChanged(): Promise<void> {
    const cannot = cannotList(this.options.status());
    const shown = this.list.kind === 'unsupported';
    if (cannot === shown) {
      return this.restoreChanged();
    }

    return cannot ? Promise.resolve(this.unsupported()) : this.refresh();
  }

  /**
   * The cleanup host's restore facts may have moved (E10.S2b) — a run started or ended, the controls greyed. A restore that was in
   * flight and no longer is reads the list again and clears the selection; otherwise the page is posted only when its selection bar
   * changed. Nothing is read while no page holds a list (a closed page reads afresh on its `ready`).
   */
  restoreChanged(): Promise<void> {
    if (this.restoreEnded()) {
      this.selection = new Set<string>();
      return this.readAfterRestore();
    }
    if (this.list.kind !== 'idle' && JSON.stringify(buildSelectionView(this.state())) !== this.postedSelection) {
      this.post();
    }

    return Promise.resolve();
  }

  /**
   * The list read after a restore ended — AFTER a read already in flight, never sharing it (own review #4): that read may have been
   * answered before the index recorded the restore.
   */
  private readAfterRestore(): Promise<void> {
    return this.reading === undefined ? this.refresh() : this.reading.then(() => this.refresh());
  }

  /** A restore was in flight at the last look and is no longer, on a page that holds a list — the remembered state moved on. */
  private restoreEnded(): boolean {
    const inFlight = this.options.restore.facts().inFlight;
    const ended = this.restoring && !inFlight;
    this.restoring = inFlight;

    return ended && this.list.kind !== 'idle';
  }

  /** A message from the page: anything outside the closed set is dropped here, before anything starts. */
  receive(raw: unknown): Promise<void> {
    const message = parseArchiveMessage(raw);

    return message === undefined ? Promise.resolve() : this.handle(message);
  }

  /**
   * Reads the list again — *Refresh*, the first `ready`, the page opened from the panel, or a restore's end. A read already in
   * flight is SHARED, never doubled (own review #5: each read is a `wsl.exe` with a 630 s ceiling, and a reader stuck on a stalled
   * share is not freed by killing it).
   */
  refresh(): Promise<void> {
    if (cannotList(this.options.status())) {
      this.unsupported();
      return Promise.resolve();
    }
    this.reading ??= this.readOnce().finally(() => { this.reading = undefined; });

    return this.reading;
  }

  private async readOnce(): Promise<void> {
    const generation = ++this.generation;
    this.list = READING;
    this.post();
    const outcome = await this.options.read({ read: 'archiveList' });
    if (generation === this.generation) {
      this.held(stateOf(outcome));
    }
  }

  /** A new list replaces the held one: the selection keeps the ids it still holds as selectable, and the dropped ones are said. */
  private held(list: ListState): void {
    this.list = list;
    const { kept, dropped } = list.kind === 'read' ? keptIn(this.selection, list.body) : { kept: new Set<string>(), dropped: 0 };
    this.selection = kept;
    this.notice = droppedNotice(dropped);
    this.noticeLevel = this.notice === '' ? 'none' : 'warn';
    this.post();
  }

  /** The daemon cannot list: said once, as the page's note; a read in flight is dropped (own review #7: its generation is past). */
  private unsupported(): void {
    this.generation += 1;
    this.list = { kind: 'unsupported', reason: UNSUPPORTED };
    this.post();
  }

  private handle(message: ArchiveMessage): Promise<void> {
    const handlers: { readonly [K in ArchiveMessage['type']]: (m: Extract<ArchiveMessage, { type: K }>) => Promise<void> } = {
      ready: () => (this.list.kind === 'idle' ? this.refresh() : this.posted()),
      refresh: () => this.refresh(),
      showPaths: () => this.paths(true),
      hidePaths: () => this.paths(false),
      expandMonth: (m) => this.current(m, () => this.month(m.index, true)),
      collapseMonth: (m) => this.current(m, () => this.month(m.index, false)),
      selectMonth: (m) => this.current(m, () => this.selectMonth(m.index)),
      toggleEntry: (m) => this.current(m, () => this.toggleEntry(m.index)),
      clearSelection: () => this.selected(new Set<string>()),
      restore: () => this.restore(),
      rendered: (m) => { this.rendered = m.blocks; return Promise.resolve(); },
    };

    return (handlers[message.type] as (m: ArchiveMessage) => Promise<void>)(message);
  }

  /** An index message from the view of the list held NOW — or refused, told, and nothing applied. */
  private current(message: IndexedMessage, apply: () => Promise<void>): Promise<void> {
    if (message.generation !== this.generation) {
      this.tell(STALE_INDEX);
      return Promise.resolve();
    }

    return apply();
  }

  private paths(shown: boolean): Promise<void> {
    this.pathsShown = shown;

    return this.posted();
  }

  /** Opens or closes the month at `index` of the months the host holds NOW — by its agent and month, never by the index. */
  private month(index: number, open: boolean): Promise<void> {
    const group = this.monthAt(index);
    if (group === undefined) {
      this.tell(MONTH_GONE);
      return Promise.resolve();
    }
    const key = monthKey(group.agent, group.month);
    this.expanded = open ? new Set([...this.expanded, key]) : new Set([...this.expanded].filter((k) => k !== key));

    return this.posted();
  }

  // ---- the restore selection (E10.S2b) ----

  /** How many entries one restore takes now — `undefined` while no list is held or it does not say. */
  private ceiling(): number | undefined {
    return this.list.kind === 'read' ? restoreCeiling(this.list.body, statusBody(this.options.status())) : undefined;
  }

  private monthAt(index: number): MonthGroup | undefined {
    return this.list.kind === 'read' ? monthsOf(this.list.body)[index] : undefined;
  }

  private entryAt(index: number): Readonly<Record<string, unknown>> | undefined {
    return this.list.kind === 'read' ? listEntries(this.list.body)[index] : undefined;
  }

  /** Selects or unselects the entry at `index` of the entries the host holds NOW — by its id. */
  private toggleEntry(index: number): Promise<void> {
    const entry = this.entryAt(index);
    const id = entry === undefined ? undefined : selectableId(entry);
    if (id === undefined) {
      return this.refused(notSelectable(entry));
    }

    return this.selection.has(id) ? this.selected(new Set([...this.selection].filter((s) => s !== id))) : this.added(id);
  }

  /** One more id in the selection — or why there is no room. */
  private added(id: string): Promise<void> {
    const full = this.fullBecause();

    return full === '' ? this.selected(new Set([...this.selection, id])) : this.refused(full);
  }

  /** Why nothing more can be selected — '' while there is room. */
  private fullBecause(): string {
    const ceiling = this.ceiling();
    if (ceiling === undefined) {
      return NO_CEILING;
    }

    if (this.options.restore.facts().inFlight) {
      return 'A restore is running — select again when it ends.';
    }

    return this.selection.size >= ceiling ? `One restore takes at most ${ceiling} — restore or clear the ones selected first.` : '';
  }

  /** *Select month*: the month's selectable sessions not yet selected — all of them, or the newest that fit the room left. */
  private selectMonth(index: number): Promise<void> {
    const group = this.monthAt(index);
    const ceiling = this.ceiling();
    if (group === undefined || ceiling === undefined) {
      return this.refused(group === undefined ? MONTH_GONE : NO_CEILING);
    }

    return this.pickMonth(group, ceiling);
  }

  private pickMonth(group: MonthGroup, ceiling: number): Promise<void> {
    const pick = monthPick(group.entries, this.selection, ceiling - this.selection.size);
    if (pick.added.length === 0) {
      return this.refused(pick.offered === 0 ? 'That month holds no session left to select for a restore.' : `One restore takes at most ${ceiling} — restore or clear the ones selected first.`);
    }
    this.selection = new Set([...this.selection, ...pick.added]);
    this.notice = cutNotice(pick, ceiling);
    this.noticeLevel = 'none';

    return this.posted();
  }

  /** *Restore*: the selected ids, in the list's order, handed to the cleanup host — or why not, told. */
  private async restore(): Promise<void> {
    const why = this.restoreRefusal();
    if (why !== '') {
      return this.refused(why);
    }
    const ids = this.list.kind === 'read' ? selectableIds(listEntries(this.list.body)).filter((id) => this.selection.has(id)) : [];
    this.starting = true;
    this.post();
    try {
      await this.options.restore.start(ids);
    } finally {
      this.starting = false;
    }

    return this.restoreChanged();
  }

  private restoreRefusal(): string {
    const ceiling = this.ceiling();
    const facts = this.options.restore.facts();
    const over = overCeiling(this.selection.size, ceiling);
    const reasons: readonly [boolean, string][] = [
      [this.list.kind !== 'read', 'Read the archive first.'],
      [ceiling === undefined, NO_CEILING],
      [this.selection.size === 0, 'Select at least one session to restore.'],
      [over !== '', over],
      [this.starting, 'A restore is already being previewed — its modal opens when the daemon answers.'],
      [facts.inFlight, 'A restore is already running.'],
      [facts.blocker !== '', `Restore is not available: ${facts.blocker}.`],
    ];

    return reasons.find(([holds]) => holds)?.[1] ?? '';
  }

  private selected(selection: ReadonlySet<string>): Promise<void> {
    this.selection = selection;
    this.notice = '';

    return this.posted();
  }

  private refused(sentence: string): Promise<void> {
    this.tell(sentence);

    return Promise.resolve();
  }

  private tell(sentence: string): void {
    this.notice = sentence;
    this.noticeLevel = 'warn';
    this.post();
  }

  private posted(): Promise<void> {
    this.post();

    return Promise.resolve();
  }

  private post(): void {
    const view = this.view();
    this.postedSelection = JSON.stringify(view.selection);
    this.options.post(view);
  }

  private state(): ArchiveHostState {
    return {
      list: this.list, generation: this.generation, expanded: this.expanded, selection: this.selection, ceiling: this.ceiling(), restore: this.options.restore.facts(), starting: this.starting,
      pathsShown: this.pathsShown, notice: this.notice, noticeLevel: this.noticeLevel,
    };
  }
}
