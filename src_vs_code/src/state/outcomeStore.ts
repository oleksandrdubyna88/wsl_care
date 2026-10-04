import type { VerbOutcome } from '../client/outcome';

/**
 * The newest outcome of each verb the views read — the one place the status bar and the panel take their data from,
 * so they can never disagree about what the daemon last said. Read-only verbs only: nothing here is a status a user
 * action changed, so nothing needs to survive a reload — the next poll or panel open asks again.
 */

export type PanelVerb = 'status' | 'preview' | 'doctor';

export interface Snapshot {
  readonly status: VerbOutcome | undefined;
  readonly preview: VerbOutcome | undefined;
  readonly doctor: VerbOutcome | undefined;
  /** A panel refresh is in flight. */
  readonly checking: boolean;
}

export const EMPTY: Snapshot = { status: undefined, preview: undefined, doctor: undefined, checking: false };

export class OutcomeStore {
  private current: Snapshot = EMPTY;
  private readonly listeners = new Set<(snapshot: Snapshot) => void>();

  snapshot(): Snapshot {
    return this.current;
  }

  set(verb: PanelVerb, outcome: VerbOutcome): void {
    this.replace({ ...this.current, [verb]: outcome });
  }

  setChecking(checking: boolean): void {
    this.replace({ ...this.current, checking });
  }

  /** Called with every new snapshot; the returned function unsubscribes. */
  onChange(listener: (snapshot: Snapshot) => void): () => void {
    this.listeners.add(listener);
    return () => { this.listeners.delete(listener); };
  }

  private replace(next: Snapshot): void {
    this.current = next;
    this.listeners.forEach((listener) => listener(next));
  }
}
