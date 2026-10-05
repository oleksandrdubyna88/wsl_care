import { RETENTION_DAYS } from './instants';

/**
 * The values the extension MIRRORS from the daemon — never a second, separate setting that could drift from it (the owner,
 * 2026-10-05): the days of history the daemon keeps (`runs.historyRetentionDays` from E7.S2c; `RunRetention.RetentionDays`
 * today) and the clock skew it allows a request (`RequestSweep.FutureSkew`). The daemon's `status --json` answer carries
 * them as `limits { historyRetentionDays, requestFutureSkewSeconds }` — the shape this extension EXPECTS from E7.S2c (plan
 * §15p); until a daemon answers it, every field falls back to today's value, one by one. The path that prefers the
 * daemon's value is the only path: a status without `limits` simply takes the fallback.
 */

export interface DaemonLimits {
  readonly historyRetentionDays: number;
  readonly futureSkewMs: number;
}

/** Today's values — what every daemon before E7.S2c keeps and allows. */
export const FALLBACK_LIMITS: DaemonLimits = { historyRetentionDays: RETENTION_DAYS, futureSkewMs: 5 * 60_000 };

/** The ranges a daemon value must lie in to be believed (the E7 inventory's key range for the retention; up to an hour of skew). */
const MAX_RETENTION_DAYS = 3650;
const MAX_SKEW_SECONDS = 3600;

function isWhole(value: unknown): value is number {
  return typeof value === 'number' && Number.isInteger(value);
}

function wholeIn(value: unknown, min: number, max: number): number | undefined {
  return isWhole(value) && value >= min && value <= max ? value : undefined;
}

type Fields = Readonly<Record<string, unknown>>;

function isFields(value: unknown): value is Fields {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function limitsBlock(body: unknown): Fields {
  const limits = isFields(body) ? body.limits : undefined;

  return isFields(limits) ? limits : {};
}

/** The daemon's limits from a `status` body — each field the daemon's when it is a whole number in range, else the fallback. */
export function daemonLimitsOf(body: unknown): DaemonLimits {
  const limits = limitsBlock(body);
  const skewSeconds = wholeIn(limits.requestFutureSkewSeconds, 0, MAX_SKEW_SECONDS);

  return {
    historyRetentionDays: wholeIn(limits.historyRetentionDays, 1, MAX_RETENTION_DAYS) ?? FALLBACK_LIMITS.historyRetentionDays,
    futureSkewMs: skewSeconds === undefined ? FALLBACK_LIMITS.futureSkewMs : skewSeconds * 1000,
  };
}
