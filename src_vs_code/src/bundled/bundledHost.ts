import type { BundledRead } from '../client/WindowsCareClient';
import { windowsDaemonView, type WindowsDaemonView } from './bundledView';

/**
 * The host of the panel's Windows row (E7.S5a, todo/PLAN_bundle_windows_binary.md §3.7): the last `--version` of the bundled
 * exe and ONE read in flight. It starts nothing by itself — never at activation: the panel's refresh asks (its first render
 * included), because nothing is started unasked.
 */

export interface BundledHostDeps {
  /** `WindowsCareClient.version` — the bundled exe, through the client's seam. */
  readonly version: () => Promise<BundledRead>;
  /** `BUNDLED_DAEMON`, the build this extension was packaged with. */
  readonly pin: string;
}

export class BundledHost {
  private read: BundledRead | undefined;
  private reading: Promise<void> | undefined;
  private readonly listeners = new Set<() => void>();

  constructor(private readonly deps: BundledHostDeps) {}

  view(): WindowsDaemonView {
    return windowsDaemonView(this.read, this.deps.pin);
  }

  /** A read for the panel: the one in flight, or a new one. */
  refresh(): Promise<void> {
    return this.reading ?? this.start();
  }

  onChange(listener: () => void): () => void {
    this.listeners.add(listener);
    return () => { this.listeners.delete(listener); };
  }

  private start(): Promise<void> {
    const reading = this.readOnce();
    this.reading = reading;
    return reading;
  }

  private async readOnce(): Promise<void> {
    try {
      this.read = await this.deps.version();
    } catch (e: unknown) {
      this.read = { kind: 'failed', reason: `it could not start: ${String(e)}` };
    } finally {
      this.reading = undefined;
      this.tell();
    }
  }

  /** Every listener hears the read ended; one that throws neither stops the others nor rejects the refresh. */
  private tell(): void {
    this.listeners.forEach((listener) => {
      try {
        listener();
      } catch {
        // A listener's own fault is its own; the read stands.
      }
    });
  }
}
