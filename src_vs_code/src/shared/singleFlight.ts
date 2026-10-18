/**
 * ONE piece of asynchronous work in flight at a time — the rule the panel's reads follow (the guard's Task Scheduler query always
 * did, `guardHost.ts` own code review k4): a request made while the work runs is NOT answered by the flight itself, which may have
 * started before the caller's change, but by ONE more run that starts after it; every further request meanwhile shares that one
 * more. So a burst of refreshes is at most two runs, and the last one always starts after the last request.
 *
 * `started` is called when a flight begins and `ended` when its last run is over; `running` is already true in the first and
 * already false in the second. A run that rejects does NOT drop a run queued behind it (the coai code round, finding 4): the queue is
 * drained, then the LAST error any run threw rejects the request (and the shared flight), and the next request starts afresh.
 */
export class SingleFlight {
  private flight: Promise<void> | undefined;
  private again = false;

  constructor(private readonly work: () => Promise<void>, private readonly hooks: { readonly started?: () => void; readonly ended?: () => void } = {}) {}

  /** Whether a flight is in progress. */
  get running(): boolean {
    return this.flight !== undefined;
  }

  /** Asks for a run that starts after this call: the flight's, or the one more it will make. */
  request(): Promise<void> {
    if (this.flight !== undefined) {
      this.again = true;

      return this.flight;
    }
    this.flight = this.runs().finally(() => {
      this.flight = undefined;
      this.hooks.ended?.();
    });
    this.hooks.started?.();

    return this.flight;
  }

  private async runs(): Promise<void> {
    let failure: { readonly error: unknown } | undefined;
    do {
      this.again = false;
      try {
        await this.work();
      } catch (error: unknown) {
        // A run that rejects must not drop the run queued behind it: the queue is drained first, the last error surfaced after.
        failure = { error };
      }
    } while (this.again);
    if (failure !== undefined) {
      throw failure.error;
    }
  }
}
