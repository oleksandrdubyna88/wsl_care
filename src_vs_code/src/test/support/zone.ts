/**
 * Runs `body` with this process's time zone set to `zone` (an IANA name), then puts the zone back — so a test can exercise
 * the REAL local-day code (`logsPage/period.ts`, `text/format.ts`) in a summer-time zone, a zone whose midnight does not exist
 * and a zone 14 hours east, on any machine. Node reads `process.env.TZ` afresh when it changes (Node 13 and later, full ICU);
 * each test file runs in its own process, so the change never reaches another file's tests.
 */
export function withZone<T>(zone: string, body: () => T): T {
  const before = process.env.TZ;
  process.env.TZ = zone;
  try {
    return body();
  } finally {
    if (before === undefined) {
      delete process.env.TZ;
    } else {
      process.env.TZ = before;
    }
  }
}
