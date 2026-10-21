import { MODULES } from '../powershell/modulePath';
import { NOT_FOUND_HRESULT } from './queryScript';
import { PRINCIPAL_POLICY } from './principalPolicy';
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
export const RUN_EXIT = { notRegistered: 34, alreadyRunning: 35, disabled: 36, runFailed: 37, notYours: 38 } as const;

/**
 * The script's one way to print a refusal's HRESULT, as a PowerShell snippet for a `catch` block: the INNERMOST exception's HResult — a COM call made through
 * PowerShell throws a method-invocation wrapper (0x80131501) around Task Scheduler's own error. `-f` with `X8` formats an Int32 as its two's-complement
 * eight digits, so a negative HRESULT needs no cast (a `[uint32]` cast of it would throw). The shared scripts (registration, query) keep their own, unchanged.
 */
export const HRESULT_PRINT = "$ex = $_.Exception; while ($ex.InnerException) { $ex = $ex.InnerException }; 'hresult=0x{0:X8}' -f $ex.HResult";

/** `LastTaskResult` Task Scheduler gives a task that is running (`SCHED_S_TASK_RUNNING`), the number the panel's "running" row also reads. */
const RUNNING_RESULT = SCHEDULER_RESULT.running;

/**
 * Start `name` of `folder` now. Order, and why: connect; get the task (not found is exit 34, any other refusal is 37 with the HRESULT printed);
 * print `state=` BEFORE any decision; refuse a disabled task (a `Run` on one fails with 0x80041326 — the script says why instead); refuse a task
 * that is ALREADY running (State 4 or the result 0x41301: the same test as the panel's row) — nothing is started, exit 35, and the overlap is
 * never silent (a state that cannot be read is exit 37, never a fall through to the start: PowerShell continues after a statement-terminating error, so every
 * verdict starts REFUSED and is lifted only by a completed read); the PRINCIPAL of the task as it is registered RIGHT NOW — the reader's own SID (the same call the status query prints), an interactive
 * token (LogonType 3), the least privilege (RunLevel 0) — exit 38 when it is anything else, nothing started (the flow checks the same from its earlier
 * read, as the early answer; this check is the authoritative one, because the modal and the press come between that read and this `Run`); only then `Run($null)`, an exit 37 with the HRESULT when it throws, and exit 0.
 */
export function runTaskScript(folder: string, name: string): string {
  const safeFolder = taskWord('folder', folder);
  const safeName = taskWord('task name', name);

  return [
    MODULES,
    `try { $service = New-Object -ComObject Schedule.Service; $service.Connect() } catch { ${HRESULT_PRINT}; exit ${RUN_EXIT.runFailed} }`,
    `try { $task = $service.GetFolder('\\${safeFolder}').GetTask('${safeName}') } catch { if ($_.Exception.HResult -eq ${NOT_FOUND_HRESULT}) { exit ${RUN_EXIT.notRegistered} }; ${HRESULT_PRINT}; exit ${RUN_EXIT.runFailed} }`,
    "'state=' + [int]$task.State",
    `if (-not $task.Enabled) { exit ${RUN_EXIT.disabled} }`,
    // REFUSE BY DEFAULT: PowerShell goes on with the NEXT statement after a statement-terminating error, so a read or a compare that throws outside a try would
    // fall through to the running test and the Run. The verdicts start refused and only a completed, matching read lifts them.
    `$ok = $false; try { $p = $task.Definition.Principal; $id = [string]$p.UserId; $sid = $id; if ($id -ne '') { try { $sid = (New-Object System.Security.Principal.NTAccount($id)).Translate([System.Security.Principal.SecurityIdentifier]).Value } catch { $sid = $id } }; $me = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value; $ok = ($sid -eq $me -and [int]$p.LogonType -eq ${PRINCIPAL_POLICY.logonType} -and [int]$p.RunLevel -eq ${PRINCIPAL_POLICY.runLevel}) } catch { $ok = $false }`,
    `if (-not $ok) { exit ${RUN_EXIT.notYours} }`,
    `$running = $true; try { $running = ([int]$task.State -eq 4 -or $task.LastTaskResult -eq ${RUNNING_RESULT}) } catch { ${HRESULT_PRINT}; exit ${RUN_EXIT.runFailed} }`,
    `if ($running) { exit ${RUN_EXIT.alreadyRunning} }`,
    `try { $null = $task.Run($null) } catch { ${HRESULT_PRINT}; exit ${RUN_EXIT.runFailed} }`,
    'exit 0',
  ].join('\n');
}
