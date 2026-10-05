import type { DurableStore } from '../../cleanup/journal';

/**
 * A `globalState`-shaped store for the tests (E6.S3): values are deep-copied in and out, as VS Code serialises them, and
 * `update` resolves on a LATER turn — so a test can observe that a write had completed before the work that depends on
 * it started. Sharing one instance between two journals is what a reload looks like.
 */
export class MapStore implements DurableStore {
  readonly values = new Map<string, unknown>();
  writes = 0;

  get(key: string): unknown {
    return structuredClone(this.values.get(key));
  }

  update(key: string, value: unknown): Promise<void> {
    return new Promise((resolve) => {
      setImmediate(() => {
        this.values.set(key, structuredClone(value));
        this.writes += 1;
        resolve();
      });
    });
  }
}

/** A manual one-shot timer: what was armed, fired by the test. */
export class ManualTimers {
  readonly armed: { readonly ms: number; readonly run: () => void; cancelled: boolean }[] = [];

  after(ms: number, run: () => void): () => void {
    const entry = { ms, run, cancelled: false };
    this.armed.push(entry);
    return () => { entry.cancelled = true; };
  }

  /** The timers armed and neither fired nor cancelled. */
  pending(): number {
    return this.armed.filter((t) => !t.cancelled).length;
  }

  /** Fires the oldest pending timer (it is then spent); false when none is pending. */
  fire(): boolean {
    const next = this.armed.find((t) => !t.cancelled);
    if (next === undefined) {
      return false;
    }
    next.cancelled = true;
    next.run();
    return true;
  }
}
