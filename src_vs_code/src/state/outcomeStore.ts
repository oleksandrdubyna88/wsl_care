import type { VerbOutcome } from '../client/outcome';
import type { DistroName } from '../wsl/distroTarget';

/**
 * The newest outcome of each verb the views read — the one place the status bar and the panel take their data from,
 * so they can never disagree about what the daemon last said. Read-only verbs only: nothing here is a status a user
 * action changed, so nothing needs to survive a reload — the next poll or panel open asks again.
 *
 * <p>E18.W1a: `snapshot()`, `set`, `setWindowsStatus`, `setChecking` and `clear()` are the DEFAULT distribution's, exactly as before;
 * beside them the store holds one snapshot per PICKED distribution (`snapshotOf`, `setPicked`, `setPickedChecking`). A setting change
 * clears the default's and nothing else; a pick clears nothing; a name leaves only when WSL's list no longer holds it (`dropUnlisted`) —
 * so the picked map is bounded by the machine's distro count (about 60 KB a distribution in memory, never persisted).</p>
 */

export type PanelVerb = 'status' | 'preview' | 'doctor';

export interface Snapshot {
  readonly status: VerbOutcome | undefined;
  readonly preview: VerbOutcome | undefined;
  readonly doctor: VerbOutcome | undefined;
  /**
   * The BUNDLED Windows daemon's `status` (E19.S2b) — its own reading, never mixed with the distribution's `status` above: a failure of
   * one never touches the other. Absent when the host cannot run the exe or nothing was read yet.
   */
  readonly windowsStatus?: VerbOutcome | undefined;
  /** A panel refresh is in flight. */
  readonly checking: boolean;
}

export const EMPTY: Snapshot = { status: undefined, preview: undefined, doctor: undefined, checking: false };

export class OutcomeStore {
  private current: Snapshot = EMPTY;
  /** One snapshot per picked distribution; replaced, never edited. In memory only: nothing in flight survives a crash. */
  private picked: ReadonlyMap<DistroName, Snapshot> = new Map();
  /** The names of the last `dropUnlisted` — the only distributions a write may name. */
  private listed: ReadonlySet<string> = new Set();
  private readonly listeners = new Set<(snapshot: Snapshot) => void>();

  snapshot(): Snapshot {
    return this.current;
  }

  set(verb: PanelVerb, outcome: VerbOutcome): void {
    this.replace({ ...this.current, [verb]: outcome });
  }

  /** The newest Windows `status` reading — its own slot, so a distribution's outcome is never replaced by it nor it by that. */
  setWindowsStatus(outcome: VerbOutcome): void {
    this.replace({ ...this.current, windowsStatus: outcome });
  }

  setChecking(checking: boolean): void {
    this.replace({ ...this.current, checking });
  }

  /**
   * Forget every outcome of the distribution — it is no longer the one asked about (`Poller.begin`). The Windows reading is about
   * the Windows side, not a distribution, so a changed target keeps it.
   */
  clear(): void {
    const windowsStatus = this.current.windowsStatus;
    this.replace(windowsStatus === undefined ? EMPTY : { ...EMPTY, windowsStatus });
  }

  /**
   * The newest outcomes of one PICKED distribution (E18.W1a) — `EMPTY` when nothing was written for it (or the last list dropped it),
   * the same `EMPTY` the default starts from, so a reader needs no new case.
   */
  snapshotOf(distro: DistroName): Snapshot {
    return this.picked.get(distro) ?? EMPTY;
  }

  /**
   * Stores `outcome` under the picked distribution the read was started FOR — never the answer's own name, so two distributions' answers
   * cannot land in one slot. Ignored for a name the last `dropUnlisted` did not hold: a read that resolves after its distribution was
   * unregistered re-inserts nothing (the gate's finding 4), and a store told no list yet holds no picked snapshot at all.
   */
  setPicked(distro: DistroName, verb: PanelVerb, outcome: VerbOutcome): void {
    this.writePicked(distro, (snapshot) => ({ ...snapshot, [verb]: outcome }));
  }

  /** A picked distribution's read is in flight — the same "checking" flag the default's panel refresh raises. */
  setPickedChecking(distro: DistroName, checking: boolean): void {
    this.writePicked(distro, (snapshot) => ({ ...snapshot, checking }));
  }

  /** The distributions that hold a picked snapshot (at most the machine's distros: bounded by `dropUnlisted`). */
  pickedNames(): readonly DistroName[] {
    return [...this.picked.keys()];
  }

  /**
   * `listed` is what WSL reports NOW (the poller's `listDistros()`): a picked snapshot of any other name is forgotten, and writes for
   * any other name are ignored from here on. There is no `clearPicked` — a snapshot is replaced by the next read, verb by verb, and
   * removed only here.
   */
  dropUnlisted(listed: readonly DistroName[]): void {
    this.listed = new Set<string>(listed);
    const kept = [...this.picked].filter(([name]) => this.listed.has(name));
    if (kept.length !== this.picked.size) {
      this.picked = new Map(kept);
      this.notify();
    }
  }

  /** Called with every new snapshot; the returned function unsubscribes. */
  onChange(listener: (snapshot: Snapshot) => void): () => void {
    this.listeners.add(listener);
    return () => { this.listeners.delete(listener); };
  }

  private writePicked(distro: DistroName, change: (snapshot: Snapshot) => Snapshot): void {
    if (this.listed.has(distro)) {
      this.picked = new Map(this.picked).set(distro, change(this.snapshotOf(distro)));
      this.notify();
    }
  }

  private replace(next: Snapshot): void {
    this.current = next;
    this.notify();
  }

  /** Every write — the default's or a picked one — tells the listeners the DEFAULT snapshot, as the bar and the panel read only that. */
  private notify(): void {
    this.listeners.forEach((listener) => listener(this.current));
  }
}
