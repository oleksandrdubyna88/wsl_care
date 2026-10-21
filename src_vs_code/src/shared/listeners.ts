/**
 * A list of change listeners whose faults stay their own: every listener hears {@link Listeners.tell}, and one that throws neither
 * stops the ones after it nor throws at the caller — a read that has already answered must not be undone by a panel that failed
 * to draw it (WR-E2, the Opus own review m3).
 */
export class Listeners {
  private readonly all = new Set<() => void>();

  /** Adds a listener; the returned function removes it. */
  add(listener: () => void): () => void {
    this.all.add(listener);

    return () => { this.all.delete(listener); };
  }

  tell(): void {
    this.all.forEach((listener) => {
      try {
        listener();
      } catch {
        // A listener's own fault is its own; the state it was told about stands.
      }
    });
  }
}
