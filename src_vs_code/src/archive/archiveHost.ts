import type { Failure, ReadOutcome } from '../client/outcome';
import type { RunRead } from '../client/verbs';
import type { Runner } from '../process/runner';
import { failureText } from '../failureText';
import { DEFAULT_NUMBERS, type Numbers } from '../settings/numbers';
import { FALLBACK_LIMITS, type DaemonLimits } from '../shared/daemonLimits';
import type { OutcomeStore } from '../state/outcomeStore';
import { noticeText, unlinked } from '../text/safeText';
import { chooseArchiveFolder, stopArchiving, type ArchiveFlowDeps, type ArchiveFlowOutcome, type ArchiveUi, type ConfigTarget } from './archiveFlow';
import { deriveArchive, type ArchiveControls } from './archiveView';

/**
 * The host side of the archive (E10.S1, plan §15s): the two reads of the panel's own refresh (`archive status`, `archive preview`)
 * — asked only when the panel is refreshed, never on a timer, and only of a daemon whose `status` advertises them — ONE flow at a
 * time in this window, and the controls derived from both. After a flow wrote, the reads are asked again, so the panel shows the
 * daemon's own reading of the key.
 *
 * <p>The flow's "busy" text is optimistic only and lives while the call runs: the state shown afterwards is the daemon's
 * (`common.durable-status`: the base folder in force is persisted by the daemon, read back here, and survives a reload).</p>
 */

export interface ArchiveHostOptions {
  readonly read: (request: RunRead) => Promise<ReadOutcome>;
  readonly target: () => Promise<ConfigTarget | Failure>;
  readonly runner: Runner;
  readonly outcomes: OutcomeStore;
  readonly ui: ArchiveUi;
  readonly log: (line: string) => void;
  readonly numbers?: () => Numbers;
  readonly limits?: () => DaemonLimits;
  /** E10.S1b: the cleanup controls are enabled (no run in flight, the journal not full) — *Archive now* is greyed otherwise. */
  readonly cleanupFree?: () => boolean;
  /** E10.S1b: the cleanup's state changed (its journal, a result, its flow); returns an unsubscribe. */
  readonly onCleanupChange?: (listener: () => void) => () => void;
}

/** The capabilities the store's newest answered status advertises — `undefined` until one answered. */
export function capabilitiesOf(outcomes: OutcomeStore): readonly string[] | undefined {
  const body = statusBody(outcomes);
  if (body === undefined) {
    return undefined;
  }
  const listed = body.capabilities;

  return Array.isArray(listed) ? listed.filter((c): c is string => typeof c === 'string') : [];
}

/** E10.S1b: the newest answered status offers A13 in `status.actions`. */
export function offersA13(outcomes: OutcomeStore): boolean {
  const actions = statusBody(outcomes)?.actions;

  return Array.isArray(actions) && actions.includes('A13');
}

/** The short label of the panel's own `status` failure, or '' — what the archive line says instead of a lasting "checking…" (own review #1). */
export function statusFailureOf(outcomes: OutcomeStore): string {
  const status = outcomes.snapshot().status;

  return status === undefined || status.kind === 'answered' ? '' : failureText(status).label;
}

function statusBody(outcomes: OutcomeStore): Readonly<Record<string, unknown>> | undefined {
  const status = outcomes.snapshot().status;

  return status?.kind === 'answered' && status.answer.verb === 'status' ? status.answer.body : undefined;
}

/** Each read and the capability its daemon must advertise for it to be asked. */
const READS: readonly { readonly request: RunRead; readonly capability: string }[] = [
  { request: { read: 'archiveStatus' }, capability: 'archive.run' },
  { request: { read: 'archivePreview' }, capability: 'archive.preview' },
];

/**
 * After which flow endings the daemon is asked again (code round #4): a write, a write whose ending is unknown (timed out — the
 * key may be in force) and a failure (it may have been the write's). Not after a cancel or a refused folder: nothing was written.
 */
const ASKED_AGAIN: ReadonlySet<ArchiveFlowOutcome> = new Set<ArchiveFlowOutcome>(['written', 'unknown', 'failed']);

/** Every archive surface through the one road (`cleanupHost.ts`' rule): a notification sanitised and unlinked, a modal unlinked. */
function sanitised(ui: ArchiveUi): ArchiveUi {
  return {
    pickFolder: () => ui.pickFolder(),
    confirm: (modal) => ui.confirm({ ...modal, message: unlinked(modal.message), detail: unlinked(modal.detail) }),
    notify: (level, sentence) => ui.notify(level, noticeText(sentence)),
  };
}

function reasonOf(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

export class ArchiveHost {
  private status: ReadOutcome | undefined;
  private preview: ReadOutcome | undefined;
  private busyText = '';
  /** A refresh finished at least once (own review #1: after it, a missing answer is no longer "checking"). */
  private asked = false;
  private flowRunning = false;
  private refreshing: Promise<void> | undefined;
  private readonly listeners = new Set<() => void>();
  private readonly ui: ArchiveUi;

  constructor(private readonly options: ArchiveHostOptions) {
    this.ui = sanitised(options.ui);
    options.onCleanupChange?.(() => this.changed());
  }

  view(): ArchiveControls {
    const outcomes = this.options.outcomes;

    return deriveArchive({
      status: this.status, preview: this.preview, capabilities: capabilitiesOf(outcomes), busy: this.busyText,
      reading: this.refreshing !== undefined, asked: this.asked, unavailable: statusFailureOf(outcomes), cleanupFree: this.options.cleanupFree?.() ?? false, a13Offered: offersA13(outcomes),
    });
  }

  onChange(listener: () => void): () => void {
    this.listeners.add(listener);
    return () => { this.listeners.delete(listener); };
  }

  /** The panel's refresh: both reads, of a daemon that advertises them; a refresh already running is shared. */
  refresh(): Promise<void> {
    if (this.refreshing === undefined) {
      this.refreshing = this.reads().finally(() => { this.refreshing = undefined; this.changed(); });
      // The panel says the lines are the previous answer while the reads run (code round #5: a preview may take minutes).
      this.changed();
    }

    return this.refreshing;
  }

  /** *Choose archive folder…* — never rejects: a fault is told and logged. */
  choose(): Promise<ArchiveFlowOutcome | undefined> {
    return this.flow('choosing the archive folder', chooseArchiveFolder);
  }

  /** *Stop archiving* — never rejects. */
  stop(): Promise<ArchiveFlowOutcome | undefined> {
    return this.flow('stopping the archive', stopArchiving);
  }

  private async reads(): Promise<void> {
    const capabilities = capabilitiesOf(this.options.outcomes) ?? [];
    const [status, preview] = await Promise.all(READS.map((r) => (capabilities.includes(r.capability) ? this.options.read(r.request) : Promise.resolve(undefined))));
    this.status = status;
    this.preview = preview;
    this.asked = true;
  }

  /** One flow at a time: a second press while one runs is told, and starts nothing. */
  private async flow(what: string, run: (deps: ArchiveFlowDeps) => Promise<ArchiveFlowOutcome>): Promise<ArchiveFlowOutcome | undefined> {
    if (this.flowRunning) {
      await this.ui.notify('warn', 'The archive folder is already being changed — wait for it to finish.');
      return undefined;
    }
    this.flowRunning = true;
    try {
      return await this.ran(what, run);
    } finally {
      this.flowRunning = false;
      this.busy('');
    }
  }

  private async ran(what: string, run: (deps: ArchiveFlowDeps) => Promise<ArchiveFlowOutcome>): Promise<ArchiveFlowOutcome | undefined> {
    try {
      const outcome = await run(this.deps());
      await (ASKED_AGAIN.has(outcome) ? this.refresh() : Promise.resolve());
      return outcome;
    } catch (error) {
      this.options.log(`archive: ${what} failed: ${reasonOf(error)}`);
      await this.ui.notify('error', `AI OS Care could not complete ${what}: ${reasonOf(error)}`);
      return undefined;
    }
  }

  private deps(): ArchiveFlowDeps {
    return {
      ui: this.ui,
      read: this.options.read,
      target: this.options.target,
      runner: this.options.runner,
      numbers: () => this.options.numbers?.() ?? DEFAULT_NUMBERS,
      limits: () => this.options.limits?.() ?? FALLBACK_LIMITS,
      busy: (text) => this.busy(text),
    };
  }

  private busy(text: string): void {
    if (this.busyText !== text) {
      this.busyText = text;
      this.changed();
    }
  }

  private changed(): void {
    this.listeners.forEach((listener) => listener());
  }
}
