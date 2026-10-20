import { powerShellOf } from '../powershell/commandLine';
import { MODULES } from '../powershell/modulePath';
import type { ProcessRequest } from '../process/runner';
import { SUMMARY_FUNCTION } from './taskSummary';

/**
 * The UNELEVATED query of the ONE Task Scheduler read (PLAN_windows_time_task.md D5, D8; since WR-E2 — PLAN_windows_runner.md §3.8 —
 * of every task of one folder), built here once for its two consumers: the Windows Time guard (`windowsTime/guardScripts.ts` gives
 * it its own facts and the names to look for) and the Windows tasks' part (which reads the same answer). It lists the folder
 * through `Schedule.Service` and prints tagged lines, in two parts:
 *
 * - the folder's: the CALLER's `facts` first (the guard's `channel=` and `stamp=`), then `user=<sid>` (the reader's own SID) and
 *   `folder=absent` (`0x80070002` — a machine nothing was installed on: an EMPTY task set, never a failure), `folder=unreadable` +
 *   `hresult=` (anything else, e.g. `0x80070005`) or `folder=present`;
 * - then, per task, a section that opens with `task=<name>` and holds `enabled=`, `state=`, `lastRunUtc=` (`never` for Task
 *   Scheduler's 1899 zero date), `lastResult=` and the definition's `summary:` lines — or, when that ONE task cannot be read,
 *   `read=unreadable` + `hresult=` and nothing else of it (one task's trouble is its own). A task's lines are collected before
 *   they are printed, so a failure half way leaves no half section.
 *
 * Every fact of the folder is printed BEFORE the first task: the parser files each tag before the first `task=` into the folder, so a
 * fact printed after a task would be read as that task's (the Opus own review B2). Control characters of a task's name and line
 * breaks in a summary line become `?` / a space: whatever a task's author wrote, it cannot add a line to the answer. The script
 * changes nothing (no word of the tripwire's mutating list is in it — asserted).
 */

/** `0x80070002` as PowerShell prints an `HResult` — "the system cannot find the file specified": no such folder or task. */
export const NOT_FOUND_HRESULT = -2147024894;

export interface QueryScriptOptions {
  /** The folder whose tasks are listed, without its leading backslash. */
  readonly folder: string;
  /** Every task the extension installs there: each the list lacks is asked for by name (a task the account may not read is skipped by the list). */
  readonly expectedTasks: readonly string[];
  /** PowerShell lines printed first, each a tagged fact of the folder's (nothing here may print a `task=` line). */
  readonly facts: readonly string[];
}

/** The query as one script: the module path pinned, the canonical summary function, the caller's facts, then the folder and its tasks. */
export function queryScript(options: QueryScriptOptions): string {
  return [
    MODULES,
    SUMMARY_FUNCTION,
    ...options.facts,
    "'user=' + [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value",
    '$tasks = @()',
    `try { $service = New-Object -ComObject Schedule.Service; $service.Connect(); $folder = $service.GetFolder('\\${options.folder}'); $tasks = @($folder.GetTasks(1)) } catch { $code = $_.Exception.HResult; if ($code -eq ${NOT_FOUND_HRESULT}) { 'folder=absent' } else { 'folder=unreadable'; 'hresult=0x{0:X8}' -f $code }; exit 0 }`,
    "'folder=present'",
    // GetTasks skips a task this account may not read: each task this extension installs that the list lacks is asked for BY NAME —
    // not found is absent, any other refusal is that task's unreadable section (review M1).
    `foreach ($name in @(${options.expectedTasks.map((n) => `'${n}'`).join(',')})) { if (@($tasks | Where-Object { $_.Name -eq $name }).Count -eq 0) { try { $tasks += $folder.GetTask($name) } catch { if ($_.Exception.HResult -ne ${NOT_FOUND_HRESULT}) { 'task=' + $name; 'read=unreadable'; 'hresult=0x{0:X8}' -f $_.Exception.HResult } } } }`,
    'foreach ($task in $tasks) {',
    "'task=' + ($task.Name -replace '[\\x00-\\x1f]', '?')",
    'try {',
    "$run = $task.LastRunTime; $runText = 'lastRunUtc=never'; if ($run.Year -ge 2000) { $runText = 'lastRunUtc=' + $run.ToUniversalTime().ToString('o') }",
    "$lines = @(('enabled=' + $task.Enabled), ('state=' + [int]$task.State), $runText, ('lastResult=' + $task.LastTaskResult))",
    "$lines += @(Get-WslCareSummary $task.Definition | ForEach-Object { 'summary:' + ($_ -replace '[\\r\\n]+', ' ') })",
    '$lines',
    "} catch { 'read=unreadable'; 'hresult=0x{0:X8}' -f $_.Exception.HResult }",
    '}',
    'exit 0',
  ].join('\n');
}

/**
 * The read-only query as a request, UNELEVATED: the absolute PowerShell with the fixed script — no `runas`, no encoded payload. At its
 * ceiling the runner ends the PowerShell's whole process TREE (`killTree`, verified — `process/treeKill.ts`), not only the PowerShell.
 */
export function queryRequest(env: Readonly<Record<string, string | undefined>>, script: string, timeoutMs: number): ProcessRequest | string {
  const powerShell = powerShellOf(env);

  return typeof powerShell === 'string' ? { file: powerShell, args: ['-NoProfile', '-NonInteractive', '-Command', script], timeoutMs, killTree: true } : powerShell.reason;
}
