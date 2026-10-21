import { MODULES } from '../powershell/modulePath';
import { NOT_FOUND_HRESULT } from './queryScript';
import { taskWord } from './registerScript';
import { SCHEDULER_RESULT } from './schedulerAnswer';

/**
 * The ONE place the extension spells how a registered task is STARTED now (WR-E4 of PLAN_windows_runner.md §3.8): `$task.Run($null)` on the
 * person's own task under the extension's folder — the same call Task Scheduler's own "Run" makes, so the run is exactly what the next trigger
 * would start (the registered copy, the person's account, an interactive token, the least privilege). A peer of `registerScript.ts`, which stays
 * the one home of register and delete (E3, rejected finding 3): a run is another verb with its own exits. The tripwire's own pattern for the
 * start of a task (`test/support/noRealWsl.ts` `TASK_RUN`) is held to this module by `registrationPath.test.ts`.
 *
 * The folder and the task name are the only variable text, both through {@link taskWord}. The script is COM only (`Schedule.Service`): a
 * run needs no cmdlet, so there is no module to import (no exit 33) and no PowerShell 7 module-path risk. It names no daemon, no argv and no
 * other task.
 */

/** The run script's own exit codes — after `OP_EXIT`'s 30–33, so the two tables never alias in `FAILURES[op.kind]`. */
export const RUN_EXIT = { notRegistered: 34, alreadyRunning: 35, disabled: 36, runFailed: 37 } as const;

/** `LastTaskResult` Task Scheduler gives a task that is running (`SCHED_S_TASK_RUNNING`), the number the panel's "running" row also reads. */
const RUNNING_RESULT = SCHEDULER_RESULT.running;

/**
 * Start `name` of `folder` now. Order, and why: connect; get the task (not found is exit 34, any other refusal is 37 with the HRESULT printed);
 * print `state=` BEFORE any decision; refuse a disabled task (a `Run` on one fails with 0x80041326 — the script says why instead); refuse a task
 * that is ALREADY running (State 4 or the result 0x41301: the same test as the panel's row) — nothing is started, exit 35, and the overlap is
 * never silent; only then `Run($null)`, an exit 37 with the HRESULT when it throws, and exit 0.
 */
export function runTaskScript(folder: string, name: string): string {
  const safeFolder = taskWord('folder', folder);
  const safeName = taskWord('task name', name);

  return [
    MODULES,
    `try { $service = New-Object -ComObject Schedule.Service; $service.Connect() } catch { 'hresult=0x{0:X8}' -f $_.Exception.HResult; exit ${RUN_EXIT.runFailed} }`,
    `try { $task = $service.GetFolder('\\${safeFolder}').GetTask('${safeName}') } catch { if ($_.Exception.HResult -eq ${NOT_FOUND_HRESULT}) { exit ${RUN_EXIT.notRegistered} }; 'hresult=0x{0:X8}' -f $_.Exception.HResult; exit ${RUN_EXIT.runFailed} }`,
    "'state=' + [int]$task.State",
    `if (-not $task.Enabled) { exit ${RUN_EXIT.disabled} }`,
    `if ([int]$task.State -eq 4 -or $task.LastTaskResult -eq ${RUNNING_RESULT}) { exit ${RUN_EXIT.alreadyRunning} }`,
    `try { $null = $task.Run($null) } catch { 'hresult=0x{0:X8}' -f $_.Exception.HResult; exit ${RUN_EXIT.runFailed} }`,
    'exit 0',
  ].join('\n');
}
