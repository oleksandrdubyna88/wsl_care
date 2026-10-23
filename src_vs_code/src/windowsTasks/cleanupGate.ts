import { CLEANUP_TASK_SINCE, versionAtLeast } from '../client/handshake';

/**
 * The bundled daemon and the cleanup task (WR-T3): the Windows daemon the .vsix carries may predate WR-T2, and such a daemon answers `tasks render cleanup`,
 * `tasks preview cleanup` and `tasks run cleanup` with exit 2 (the wrong side) — a false-reason row on every refresh and three buttons that die when pressed.
 * ONE comparison, made with the existing `versionAtLeast`, gates all of it: the probe does not render the third task, the cleanup row and the three buttons say
 * so, and the flows of that task start nothing (the palette reaches them without the buttons).
 */

/** The one honest sentence of the closed gate: told by the cleanup row, the three buttons and any flow that is asked anyway. */
export const CLEANUP_UNAVAILABLE = 'the bundled daemon is older than the cleanup task; the next daemon release brings it';

/** '' when the bundled daemon `pin` has the cleanup task (is at or above `since`), else the sentence. */
export function cleanupGate(pin: string, since: string = CLEANUP_TASK_SINCE): string {
  return versionAtLeast(pin, since) ? '' : CLEANUP_UNAVAILABLE;
}
