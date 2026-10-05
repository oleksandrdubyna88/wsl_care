/**
 * Instants as the daemon's history reads them (E6.S4, extracted from `cleanup/runMatching.ts` so the Logs page and the
 * durable poll write them ONE way): UTC, whole seconds, `yyyy-MM-ddTHH:mm:ssZ` — the shape `shared/shapes.ts`
 * `UTC_INSTANT_SHAPE` checks and `LogPeriod.ParseInstants` takes.
 */

/** The days of history the daemon keeps (`RunRetention.RetentionDays`): no window reaches further back. */
export const RETENTION_DAYS = 90;

export const DAY_MS = 86_400_000;

/** `ms` as the client's instant, rounded DOWN to its whole second. */
export function utcInstantOf(ms: number): string {
  return new Date(Math.floor(ms / 1000) * 1000).toISOString().replace('.000Z', 'Z');
}
