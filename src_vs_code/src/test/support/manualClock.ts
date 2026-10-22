import type { Timers } from '../../poll/poller';

/** A clock the test advances by hand; `every` registers a repeating timer like `setInterval`. */
export class ManualClock implements Timers {
  now = 0;
  readonly nowMs = (): number => this.now;
  private timers: { at: number; ms: number; run: () => void; live: boolean }[] = [];

  every(ms: number, run: () => void): () => void {
    const timer = { at: this.now + ms, ms, run, live: true };
    this.timers.push(timer);
    return () => { timer.live = false; };
  }

  armed(): number[] {
    return this.timers.filter((t) => t.live).map((t) => t.ms);
  }

  /** Move time forward, firing every due timer in order. */
  advance(ms: number): void {
    const end = this.now + ms;
    for (;;) {
      const due = this.timers.filter((t) => t.live && t.at <= end).sort((a, b) => a.at - b.at)[0];
      if (due === undefined) {
        break;
      }
      this.now = due.at;
      due.at += due.ms;
      due.run();
    }
    this.now = end;
  }
}
