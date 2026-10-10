import type { DurableStore } from '../cleanup/journal';
import { enableGpu, type EnableResult } from './gpuEnable';
import { clearGpuPending, readGpuPending } from './gpuPending';
import { DISABLED_CODE, queryRequest } from './gpuScripts';
import { gpuBarMark, gpuStateOf, gpuView, NOT_ASKED, type GpuBarMark, type GpuState, type GpuView } from './gpuState';
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
  readonly exists: (file: string) => boolean;
  readonly queryTimeoutMs: () => number;
  readonly enableTimeoutMs: () => number;
  readonly enableSettleSeconds: () => number;
  readonly ollamaTailBytes: () => number;
  readonly checkMinutes: () => number;
  readonly ui: GpuUi;
  readonly durable: DurableStore;
  readonly nowUtcMs: () => number;
  readonly every: (ms: number, run: () => void) => () => void;
  /** The presentation edge's local-time formatter. */
  readonly formatTime: (utcMs: number) => string;
  readonly readTail?: (file: string, bytes: number) => TailRead;
}

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
  private timerMinutes = 0;
  private readonly listeners = new Set<() => void>();

  constructor(private readonly deps: GpuHostDeps) {}

  /** Whether this host has anything to read: Windows only. */
  active(): boolean {
    return this.deps.platform === 'win32';
  }

  /** The first query, and the periodic one at `wslCare.gpu.checkMinutes` (re-read by `settingsChanged`). */
  start(): void {
    if (!this.active()) {
      return;
    }
    void this.refresh();
    this.settingsChanged();
  }

  settingsChanged(): void {
    const minutes = this.deps.checkMinutes();
    if (this.timerRunsAt(minutes)) {
      return;
    }
    this.dispose();
    this.timerMinutes = minutes;
    this.stopTimer = this.active() && minutes > 0 ? this.deps.every(minutes * 60_000, () => { void this.refresh(); }) : undefined;
  }

  private timerRunsAt(minutes: number): boolean {
    return minutes === this.timerMinutes && this.stopTimer !== undefined;
  }

  dispose(): void {
    this.stopTimer?.();
    this.stopTimer = undefined;
  }

  current(): GpuState {
    return this.state;
  }

  view(): GpuView {
    return this.active() ? gpuView(this.state, this.ollama, readGpuPending(this.deps.durable, this.deps.nowUtcMs()), this.deps.formatTime) : { alerts: [], line: '' };
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
    this.querying = this.queries().finally(() => { this.querying = undefined; this.changed(); });
    this.changed();
    return this.querying;
  }

  /** ONE enable at a time in this window — the adapter at `index` of the host's own reading. */
  async enable(index: number): Promise<EnableResult | undefined> {
    if (this.enabling || !this.active()) {
      return undefined;
    }
    this.enabling = true;
    try {
      const adapter = this.state.kind === 'read' ? this.state.adapters[index] : undefined;
      return await enableGpu(adapter, {
        env: this.deps.env,
        exists: this.deps.exists,
        timeoutMs: this.deps.enableTimeoutMs,
        settleSeconds: this.deps.enableSettleSeconds,
        confirm: this.deps.ui.confirm,
        run: this.deps.ui.run,
        report: this.deps.ui.report,
        durable: { get: (key) => this.deps.durable.get(key), update: async (key, value) => { await this.deps.durable.update(key, value); this.changed(); } },
        nowUtcMs: this.deps.nowUtcMs,
        reread: async () => { await this.refresh(); return this.state; },
      });
    } finally {
      this.enabling = false;
      this.changed();
    }
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
