import type { DurableStore } from '../cleanup/journal';
import { noticeText } from '../text/safeText';
import { enableGpu, type EnableResult } from './gpuEnable';
import { clearGpuPending, readGpuPending } from './gpuPending';
import type { FileInfoOf } from '../powershell/elevatedEngine';
import { DISABLED_CODE, queryRequest } from './gpuScripts';
import { gpuBarMark, gpuStateOf, gpuView, NOT_ASKED, pwshFactsOf, type GpuBarMark, type GpuState, type GpuView } from './gpuState';
import type { GpuUi } from './gpuRecorder';
import { ollamaLogPath, ollamaReadOf, readTail, type OllamaRead, type TailRead } from './ollamaLog';

/**
 * The host side of the GPU story (PLAN_gpu_health.md D1, D2, D5, D6): the adapters Windows last reported, ONE query in flight
 * at a time (a refresh asked during one runs one more after it — the Windows Time guard host's rule), Ollama's last
 * inference-compute line, ONE enable at a time (and, through the persisted pending run, across windows and reloads), and the
 * panel's alert and the bar's mark derived from all of it. It queries at start, on panel open and *Refresh*, after an enable,
 * and every `wslCare.gpu.checkMinutes` (0 = only those).
 */

export interface GpuHostDeps {
  readonly env: Readonly<Record<string, string | undefined>>;
  /** Off Windows there is no Windows to read: the host stays silent (no alert, no mark). */
  readonly platform: NodeJS.Platform;
  /** Node's `lstat` of a program the enable might elevate (the MSI pwsh), or `undefined` when it cannot be read. */
  readonly fileInfo: FileInfoOf;
  readonly queryTimeoutMs: () => number;
  readonly enableTimeoutMs: () => number;
  readonly enableSettleSeconds: () => number;
  readonly ollamaTailBytes: () => number;
  readonly checkMinutes: () => number;
  readonly ui: GpuUi;
  readonly durable: DurableStore;
  readonly nowUtcMs: () => number;
  readonly every: (ms: number, run: () => void) => () => void;
  /** ONE call after `ms` — the pending enable's deadline (own review M2); answers its cancel. */
  readonly after: (ms: number, run: () => void) => () => void;
  /** The presentation edge's local-time formatter. */
  readonly formatTime: (utcMs: number) => string;
  readonly readTail?: (file: string, bytes: number) => TailRead;
}

/** The results after which Windows is read again at once: the alert the page showed no longer matches the host's reading. */
const REREAD: ReadonlySet<EnableResult> = new Set<EnableResult>(['notOffered', 'stale']);

/** Told when a button from an older read is pressed (code round 2) — the panel is re-rendered from the current read. */
const STALE_BUTTON = 'The display adapters were read again since that choice was shown — the panel is refreshed; choose it again.';

/** Whether Windows' answer shows the device a pending enable was for as no longer at code 22 (or gone). */
function noLongerDisabled(state: GpuState, id: string): boolean {
  return state.kind === 'read' && state.adapters.find((a) => a.id === id)?.code !== DISABLED_CODE;
}

export class GpuHost {
  private state: GpuState = NOT_ASKED;
  private ollama: OllamaRead | undefined;
  private querying: Promise<void> | undefined;
  private again = false;
  private enabling = false;
  private stopTimer: (() => void) | undefined;
  private stopDeadline: (() => void) | undefined;
  private timerMinutes = 0;
  private readonly listeners = new Set<() => void>();

  constructor(private readonly deps: GpuHostDeps) {}

  /** Whether this host has anything to read: Windows only. */
  active(): boolean {
    return this.deps.platform === 'win32';
  }

  /**
   * The first query, the periodic one at `wslCare.gpu.checkMinutes` (re-read by `settingsChanged`), and — after a reload
   * mid-run — the re-render at a pending enable's deadline (own review 2, F3).
   */
  start(): void {
    if (!this.active()) {
      return;
    }
    void this.refresh();
    this.settingsChanged();
    this.watchDeadline();
  }

  /** The periodic timer follows the setting; the deadline re-render is not the setting's and stands (own review 2, F3). */
  settingsChanged(): void {
    const minutes = this.deps.checkMinutes();
    if (this.timerRunsAt(minutes)) {
      return;
    }
    this.stopTimer?.();
    this.timerMinutes = minutes;
    this.stopTimer = this.periodic(minutes);
  }

  /** The periodic read every `minutes`, on Windows and above 0 — or none. */
  private periodic(minutes: number): (() => void) | undefined {
    return this.active() && minutes > 0 ? this.deps.every(minutes * 60_000, () => { void this.refresh(); }) : undefined;
  }

  private timerRunsAt(minutes: number): boolean {
    return minutes === this.timerMinutes && this.stopTimer !== undefined;
  }

  dispose(): void {
    this.stopTimer?.();
    this.stopTimer = undefined;
    this.stopDeadline?.();
    this.stopDeadline = undefined;
  }

  current(): GpuState {
    return this.state;
  }

  /** The panel's part — while a query is in flight the last answer stays, and the working line says a check runs (code round 3). */
  view(): GpuView {
    return this.active() ? gpuView(this.state, this.ollama, readGpuPending(this.deps.durable, this.deps.nowUtcMs()), this.deps.formatTime, this.querying !== undefined) : { alerts: [], line: '' };
  }

  barMark(): GpuBarMark {
    return this.active() ? gpuBarMark(this.state) : gpuBarMark(NOT_ASKED);
  }

  onChange(listener: () => void): () => void {
    this.listeners.add(listener);
    return () => { this.listeners.delete(listener); };
  }

  /** Reads Windows (and Ollama's log) again; a call during a query is answered by ONE more query after it. */
  refresh(): Promise<void> {
    if (!this.active()) {
      return Promise.resolve();
    }
    if (this.querying !== undefined) {
      this.again = true;
      return this.querying;
    }
    // A query that THROWS (the runner, the store) is one unknown state, never a rejection into the extension host (own review 2, note 11).
    this.querying = this.queries().catch((e: unknown) => { this.state = { kind: 'unknown', reason: `the display-adapter query failed: ${e instanceof Error ? e.message : String(e)}` }; }).finally(() => { this.querying = undefined; this.changed(); });
    this.changed();
    return this.querying;
  }

  /**
   * ONE enable at a time in this window — the adapter at `index` of the host's own reading, the one the button was rendered
   * from: `readAt` is that read's time stamp, and an index from an OLDER read acts on nothing (code round 2) — Windows'
   * adapter order can change between a render and a click.
   */
  async enable(index: number, readAt: number): Promise<EnableResult | undefined> {
    if (this.enabling || !this.active()) {
      return undefined;
    }
    this.enabling = true;
    try {
      const result = await this.enableAt(index, readAt);
      if (REREAD.has(result)) {
        void this.refresh();
      }
      return result;
    } finally {
      this.enabling = false;
      this.watchDeadline();
      this.changed();
    }
  }

  /**
   * A pending enable that outlived the launcher (a timeout: its UAC prompt may still be open) leaves "Waiting…" on the panel
   * until its deadline — one re-render AT the deadline ends it, also with `wslCare.gpu.checkMinutes` 0 (own review M2).
   */
  private watchDeadline(): void {
    this.stopDeadline?.();
    const pending = readGpuPending(this.deps.durable, this.deps.nowUtcMs());
    this.stopDeadline = pending === undefined ? undefined : this.deps.after(pending.deadlineUtcMs - this.deps.nowUtcMs() + 1, () => this.changed());
  }

  /** Whether `readAt` names an older read than the host's current one — a button the panel rendered before the last query. */
  private staleAt(readAt: number): boolean {
    return this.state.kind === 'read' && this.state.atUtcMs !== readAt;
  }

  /** The host's reading, when it is one. */
  private read(): Extract<GpuState, { kind: 'read' }> | undefined {
    return this.state.kind === 'read' ? this.state : undefined;
  }

  private enableAt(index: number, readAt: number): Promise<EnableResult> {
    if (this.staleAt(readAt)) {
      this.deps.ui.report(noticeText(STALE_BUTTON), false);
      return Promise.resolve('stale');
    }
    const read = this.read();

    return enableGpu(read?.adapters[index], {
      env: this.deps.env,
      fileInfo: this.deps.fileInfo,
      pwsh: pwshFactsOf(this.state),
      timeoutMs: this.deps.enableTimeoutMs,
      settleSeconds: this.deps.enableSettleSeconds,
      confirm: this.deps.ui.confirm,
      run: this.deps.ui.run,
      report: this.deps.ui.report,
      durable: { get: (key) => this.deps.durable.get(key), update: async (key, value) => { await this.deps.durable.update(key, value); this.changed(); } },
      nowUtcMs: this.deps.nowUtcMs,
      reread: async () => { await this.refresh(); return this.state; },
    });
  }

  private async queries(): Promise<void> {
    do {
      this.again = false;
      await this.query();
    } while (this.again);
  }

  private async query(): Promise<void> {
    const request = queryRequest(this.deps.env, this.deps.queryTimeoutMs());
    this.state = typeof request === 'string' ? { kind: 'unknown', reason: request } : gpuStateOf(await this.deps.ui.query(request), this.deps.nowUtcMs());
    this.ollama = this.readOllama();
    await this.settlePending();
    this.watchDeadline();
    this.changed();
  }

  private readOllama(): OllamaRead | undefined {
    const file = ollamaLogPath(this.deps.env);
    const read = this.deps.readTail ?? readTail;

    return file === undefined ? undefined : ollamaReadOf(read(file, this.deps.ollamaTailBytes()));
  }

  /** A pending enable whose device Windows already shows not disabled (after a reload, say) is cleared, not waited out. */
  private async settlePending(): Promise<void> {
    const pending = readGpuPending(this.deps.durable, this.deps.nowUtcMs());
    if (pending !== undefined && !this.enabling && noLongerDisabled(this.state, pending.id)) {
      await clearGpuPending(this.deps.durable);
    }
  }

  private changed(): void {
    this.listeners.forEach((listener) => listener());
  }
}
