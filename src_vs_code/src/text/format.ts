/**
 * The two formats several views share (E6.S3 review, coai #8 / #12 / #14) — defined ONCE: decimal gigabytes, as
 * `docker system df` and `df -H` print them, and an instant to the minute in UTC, the clock the daemon's records use.
 */

export function gb(bytes: number): string {
  return `${(bytes / 1e9).toFixed(1)} GB`;
}

/** `2026-10-05 10:00 UTC` — or `fallback` when the text is not an instant. */
export function minuteOf(instant: string, fallback = 'an unknown time'): string {
  const ms = Date.parse(instant);

  return Number.isFinite(ms) ? `${new Date(ms).toISOString().slice(0, 16).replace('T', ' ')} UTC` : fallback;
}
