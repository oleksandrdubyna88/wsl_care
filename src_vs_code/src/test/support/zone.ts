/**
 * Runs `body` with this process's time zone set to `zone` (an IANA name), then puts the zone back — so a test can exercise
 * the REAL local-day code (`logsPage/period.ts`, `text/format.ts`) in a summer-time zone, a zone whose midnight does not exist
 * and a zone 14 hours east, on any machine. Node reads `process.env.TZ` afresh when it is SET (Node 13 and later, full ICU);
 * each test file runs in its own process, so the change never reaches another file's tests.
 *
 * Review K1: an ASYNC body keeps the zone across its awaits — the zone is put back when its promise settles, resolved or
 * rejected (the one helper for both cases; the controller tests' own copy is gone). Putting it back SETS the zone the machine
 * had before deleting the variable: Node caches the zone, and a bare `delete process.env.TZ` leaves the deleted zone in
 * that cache (measured 2026-10-05 — the clock still read Asia/Kolkata), which leaked a zone into every later test of a file.
 */
export function withZone<T>(zone: string, body: () => T): T {
  const restore = zoneRestorer();
  process.env.TZ = zone;
  let result: T;
  try {
    result = body();
  } catch (error) {
    restore();
    throw error;
  }

  return result instanceof Promise ? (result.finally(restore) as T) : (restore(), result);
}

/** What puts this process's zone back as it is now: the variable, and the zone Node's cache must read again. */
function zoneRestorer(): () => void {
  const before = process.env.TZ;
  const machine = Intl.DateTimeFormat().resolvedOptions().timeZone;

  return () => {
    process.env.TZ = before ?? machine;
    if (before === undefined) {
      delete process.env.TZ;
    }
  };
}
