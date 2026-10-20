import type { ProcessRequest } from '../process/runner';
import { TASK_NAMES } from '../client/verbs';
import { COMMAND_LINE_MAX, commandLineLength } from '../powershell/commandLine';
import { NOT_FOUND_HRESULT, queryRequest as sharedQueryRequest, queryScript } from '../scheduler/queryScript';
import { deleteTasksLines, HERE_END, IMPORT_SCHEDULED_TASKS, OP_EXIT, registerTaskScript } from '../scheduler/registerScript';
import { GUARD_FOLDER, GUARD_NAME, guardTaskXml, MODULES, STAMP_KEY, STAMP_PARENT, TIME_SERVICE_CHANNEL, type GuardOptions } from './guardTask';

/** Every task this extension installs under `\wsl-care`: the two Windows tasks and the guard — the names the read must find or say it could not. */
const EXPECTED_TASKS: readonly string[] = [...TASK_NAMES, GUARD_NAME];

/**
 * The three PowerShell texts around the guard's task (PLAN_windows_time_task.md D3, D5, D8) — install and remove, each run
 * ONCE elevated through story 1's launcher (`elevatedRequest` → `outerScript`: `Process.Start` with `runas`, 1223 =
 * declined, 13 = launch failed), and the UNELEVATED status query. All three are constants of this module or pure
 * functions of the settings: nothing from the daemon, the page or a setting's TEXT reaches them (a setting only picks a
 * number or a variant).
 */

/**
 * The registration and removal scripts' own text — `OP_EXIT`, the ScheduledTasks import, the here-string terminator, the install and the
 * deletes of one task folder — live in the peer (`scheduler/registerScript.ts`) since WR-E3, where the Windows tasks use them too; the
 * guard's constants are re-exported here for its flows and tests. `NOT_FOUND_HRESULT` is the peer's constant of the query.
 */
export { HERE_END, IMPORT_SCHEDULED_TASKS, NOT_FOUND_HRESULT, OP_EXIT };

/**
 * The elevated install (D3, D8 g0) of the guard's task: the peer's registration of `GUARD_NAME` in `GUARD_FOLDER` with the guard's own
 * XML — shown to the person first, byte for byte, and carried inside the script.
 */
export function installScript(options: GuardOptions): string {
  return registerTaskScript(GUARD_FOLDER, GUARD_NAME, guardTaskXml(options));
}

/**
 * The elevated removal (D3, D8 g2): every step "if present" — the task (absent is already removed), the folder only when it
 * holds nothing else, the stamp's key, its parent only when empty. The service's start type is NOT reverted: it was the
 * person's fix (story 1, or the guard while the setting was on).
 */
export const REMOVE_SCRIPT = [
  MODULES,
  ...deleteTasksLines(GUARD_FOLDER, [GUARD_NAME]),
  `try { if (Test-Path -Path '${STAMP_KEY}') { Remove-Item -Path '${STAMP_KEY}' -Recurse -Force -ErrorAction Stop } } catch { exit ${OP_EXIT.cleanupFailed} }`,
  `try { if ((Test-Path -Path '${STAMP_PARENT}') -and @(Get-ChildItem -Path '${STAMP_PARENT}').Count -eq 0 -and @((Get-Item -Path '${STAMP_PARENT}').Property).Count -eq 0) { Remove-Item -Path '${STAMP_PARENT}' -Force -ErrorAction Stop } } catch { exit ${OP_EXIT.cleanupFailed} }`,
  'exit 0',
].join('\n');

/**
 * The facts of the ONE Task Scheduler read that are the Windows Time guard's: whether the stop event is logged at all (`channel=`) and
 * whether the rate-limit stamp under HKLM outlived the task (`stamp=`). The query itself — the folder, every task's section, the
 * by-name fallback — is `scheduler/queryScript.ts`, built once for the guard and the Windows tasks' part alike. The channel is
 * printed right after it is computed, before the stamp (the folder's lines all precede the first task — the Opus own review B2).
 */
const GUARD_FACTS: readonly string[] = [
  `$log = $null; try { $log = Get-WinEvent -ListLog '${TIME_SERVICE_CHANNEL}' -ErrorAction Stop } catch { $log = $null }`,
  "$channel = 'channel=unknown'; if ($null -ne $log) { if ($log.IsEnabled) { $channel = 'channel=enabled' } else { $channel = 'channel=disabled' } }",
  '$channel',
  `if (Test-Path -Path '${STAMP_KEY}') { 'stamp=present' } else { 'stamp=absent' }`,
];

/**
 * The UNELEVATED status query (D5, D8) — since WR-E2 the ONE read of every task under `\wsl-care` (PLAN_windows_runner.md §3.8), the
 * guard's own task among them, with the guard's facts. It changes nothing (no word of the tripwire's mutating list is in it — asserted).
 */
export const QUERY_SCRIPT = queryScript({ folder: GUARD_FOLDER, expectedTasks: EXPECTED_TASKS, facts: GUARD_FACTS });

/** The longest command line one run may need, and its measure — the unelevated launcher's (`powershell/unelevatedLauncher.ts`); re-exported for the guard's flows. */
export { commandLineLength, COMMAND_LINE_MAX as ELEVATED_COMMAND_LINE_MAX };

/** The read-only query, UNELEVATED: the shared query request (`scheduler/queryScript.ts`) for this script. */
export function queryRequest(env: Readonly<Record<string, string | undefined>>, timeoutMs: number): ProcessRequest | string {
  return sharedQueryRequest(env, QUERY_SCRIPT, timeoutMs);
}