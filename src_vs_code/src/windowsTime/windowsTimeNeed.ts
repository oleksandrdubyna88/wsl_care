/**
 * Whether the panel offers *Start Windows Time* (PLAN_windows_time_guard.md D7): the newest `status` carries the daemon's
 * own verdicts from its newest full run — `clock.timeService` at `warn` or `critical` (not running, or not starting
 * Automatic), or `clock.reference` at `critical` (the reference names WINDOWS as the wrong clock). Derived from the
 * daemon's persisted verdicts, never from a flag of this window, so a reload shows the same answer
 * (`common.durable-status`); a status without these verdicts (a daemon older than the guard) offers nothing.
 */

export const TIME_SERVICE_VERDICT = 'clock.timeService';
export const REFERENCE_VERDICT = 'clock.reference';

interface VerdictLike {
  readonly id?: unknown;
  readonly level?: unknown;
}

function verdictsOf(body: unknown): readonly VerdictLike[] {
  const verdicts = typeof body === 'object' && body !== null ? (body as { verdicts?: unknown }).verdicts : undefined;

  return Array.isArray(verdicts) ? verdicts.filter((v): v is VerdictLike => typeof v === 'object' && v !== null) : [];
}

function levelOf(verdicts: readonly VerdictLike[], id: string): unknown {
  return verdicts.find((v) => v.id === id)?.level;
}

export function windowsTimeNeedsFix(statusBody: unknown): boolean {
  const verdicts = verdictsOf(statusBody);
  const service = levelOf(verdicts, TIME_SERVICE_VERDICT);

  return service === 'warn' || service === 'critical' || levelOf(verdicts, REFERENCE_VERDICT) === 'critical';
}
