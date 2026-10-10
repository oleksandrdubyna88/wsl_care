import type { JsonObject, ReadOutcome, VerbOutcome } from '../client/outcome';
import type { RunRead } from '../client/verbs';
import { failureText } from '../failureText';
import type { ViewLevel } from '../panel/view';
import { parseArchiveMessage, type ArchiveMessage } from './archiveMessages';
import type { ArchivePageView } from './archiveView';
import { buildArchiveView, monthKey, monthsOf, type ArchiveHostState, type ListState } from './archiveViewModel';

/**
 * The Archive page's host side (plan §15s D6, D9, E10.S2a) — vscode-free, so every decision is tested without a window
 * (`archiveController.test.ts`); `archivePanel.ts` wires it to a real `WebviewPanel`. Browse only: it makes ONE read, the client's
 * unprivileged `archive list --json` (`client/verbs.ts`), and changes nothing.
 *
 * - **The read is the host's.** On the page's first `ready` and on *Refresh* — a returning tab is shown what is held and asks
 *   nothing (the Logs page's review C5). A daemon whose newest `status` does not advertise `archive.list` is not asked; the page
 *   says to update it. While `status` has not answered, the daemon is asked and its own refusal shown.
 * - **An answer to an older read is dropped** (a generation count): the newest read wins.
 * - **A month is opened by its INDEX into the months the host holds**, checked against them here; it is kept open by agent and
 *   month (`monthKey`), so a refreshed list whose indexes moved keeps the same month open. An index the list no longer holds is
 *   told on the page and opens nothing.
 * - **The full session paths are the host's toggle**, off on every open (`ArchiveController` is built per window).
 * - A page message outside the closed set (`archiveMessages.ts`) is dropped before anything happens.
 */

export interface ArchiveControllerOptions {
  readonly read: (request: RunRead) => Promise<ReadOutcome>;
  /** The newest `status` outcome (the store's): the daemon's capabilities. */
  readonly status: () => VerbOutcome | undefined;
  readonly post: (view: ArchivePageView) => void;
}

/** The capability `archive list --json` needs (`Capabilities.ArchiveList`, E9.S3). */
export const ARCHIVE_LIST_CAPABILITY = 'archive.list';

const UNSUPPORTED = 'This daemon does not list its archive (archive.list) — update the daemon to browse it here.';

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

export class ArchiveController {
  private list: ListState = IDLE;
  private readonly expanded = new Set<string>();
  private pathsShown = false;
  private notice = '';
  private noticeLevel: ViewLevel = 'none';
  private generation = 0;
  private rendered: number | undefined;

  constructor(private readonly options: ArchiveControllerOptions) {}

  view(): ArchivePageView {
    return buildArchiveView(this.state());
  }

  lastRendered(): number | undefined {
    return this.rendered;
  }

  /** The store's `status` changed: the capability gate follows it — the view is re-posted. */
  statusChanged(): void {
    this.post();
  }

  /** A message from the page: anything outside the closed set is dropped here, before anything starts. */
  receive(raw: unknown): Promise<void> {
    const message = parseArchiveMessage(raw);

    return message === undefined ? Promise.resolve() : this.handle(message);
  }

  /** Reads the list again — *Refresh*, or the page opened from the panel. */
  async refresh(): Promise<void> {
    if (cannotList(this.options.status())) {
      this.list = { kind: 'unsupported', reason: UNSUPPORTED };
      this.tell(UNSUPPORTED);
      return;
    }
    const generation = ++this.generation;
    this.list = READING;
    this.post();
    const outcome = await this.options.read({ read: 'archiveList' });
    if (generation === this.generation) {
      this.list = stateOf(outcome);
      this.notice = '';
      this.post();
    }
  }

  private handle(message: ArchiveMessage): Promise<void> {
    const handlers: { readonly [K in ArchiveMessage['type']]: (m: Extract<ArchiveMessage, { type: K }>) => Promise<void> } = {
      ready: () => (this.list.kind === 'idle' ? this.refresh() : this.posted()),
      refresh: () => this.refresh(),
      showPaths: () => this.paths(true),
      hidePaths: () => this.paths(false),
      expandMonth: (m) => this.month(m.index, true),
      collapseMonth: (m) => this.month(m.index, false),
      rendered: (m) => { this.rendered = m.blocks; return Promise.resolve(); },
    };

    return (handlers[message.type] as (m: ArchiveMessage) => Promise<void>)(message);
  }

  private paths(shown: boolean): Promise<void> {
    this.pathsShown = shown;

    return this.posted();
  }

  /** Opens or closes the month at `index` of the months the host holds NOW — by its agent and month, never by the index. */
  private month(index: number, open: boolean): Promise<void> {
    const group = this.list.kind === 'read' ? monthsOf(this.list.body)[index] : undefined;
    if (group === undefined) {
      this.tell('The archive no longer lists that month — Refresh to see what it holds now.');
      return Promise.resolve();
    }
    const key = monthKey(group.agent, group.month);
    if (open) {
      this.expanded.add(key);
    } else {
      this.expanded.delete(key);
    }

    return this.posted();
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
    this.options.post(this.view());
  }

  private state(): ArchiveHostState {
    return { list: this.list, expanded: new Set(this.expanded), pathsShown: this.pathsShown, notice: this.notice, noticeLevel: this.noticeLevel };
  }
}
