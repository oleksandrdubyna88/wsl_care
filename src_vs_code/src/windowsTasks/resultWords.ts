import { DAEMON_EXIT } from '../client/exitCodes';
import { SCHEDULER_RESULT, SCHEDULER_RESULT_WORDS } from '../scheduler/schedulerAnswer';

/**
 * A task's last result in words (WR-E2, WR-T3): ONE table for the task lines (`tasksView.ts`) and the last-cleanup line (`cleanupRecord.ts`), so the
 * two cannot word the same exit differently.
 */

/** The last result of a task's run in words: the daemon's own exits first, then Task Scheduler's codes, else the bare code. */
export const RESULT_WORDS: Readonly<Record<number, string>> = {
  [DAEMON_EXIT.ok]: 'succeeded',
  [DAEMON_EXIT.runFailed]: 'the run failed (exit 1)',
  [DAEMON_EXIT.observeOnly]: 'the configuration is in error (exit 78)',
  [DAEMON_EXIT.busy]: 'skipped — another run held the run lock (normal)',
  [DAEMON_EXIT.notAsRoot]: 'refused — it ran elevated, which it never may (exit 81); with UAC off (EnableLUA=0) or on the built-in Administrator account every process is elevated — run it from an account that is not an administrator, or turn UAC on',
  [DAEMON_EXIT.taskEnded]: 'ended at its deadline — its whole tree was ended (exit 84)',
  ...SCHEDULER_RESULT_WORDS,
};

/** The results that are the task working as built: a success, a skipped overlap, a busy lock. */
export const NORMAL_RESULTS: ReadonlySet<number> = new Set([DAEMON_EXIT.ok, DAEMON_EXIT.busy, SCHEDULER_RESULT.skipped]);

export function resultWords(code: number): string {
  const known = RESULT_WORDS[code];
  if (known !== undefined) {
    return known;
  }

  return code > 0xffff ? `result 0x${code.toString(16).toUpperCase().padStart(8, '0')}` : `exit ${code}`;
}
