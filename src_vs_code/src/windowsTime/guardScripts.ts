import type { ProcessRequest } from '../process/runner';
import { GUARD_FOLDER, GUARD_NAME, guardTaskXml, MODULES, STAMP_KEY, STAMP_PARENT, SUMMARY_FUNCTION, TIME_SERVICE_CHANNEL, type GuardOptions } from './guardTask';
import { powerShellOf } from '../powershell/elevatedLauncher';
import { TASK_NAMES } from '../client/verbs';

/** Every task this extension installs under `\wsl-care`: the two Windows tasks and the guard — the names the read must find or say it could not. */
const EXPECTED_TASKS: readonly string[] = [...TASK_NAMES, GUARD_NAME];

/**
 * The three PowerShell texts around the guard's task (PLAN_windows_time_task.md D3, D5, D8) — install and remove, each run
 * ONCE elevated through story 1's launcher (`elevatedRequest` → `outerScript`: `Process.Start` with `runas`, 1223 =
 * declined, 13 = launch failed), and the UNELEVATED status query. All three are constants of this module or pure
 * functions of the settings: nothing from the daemon, the page or a setting's TEXT reaches them (a setting only picks a
 * number or a variant).
 */

/** The install / remove scripts' own exit codes (the launcher's 13 and 1223, and PowerShell's 1, are read beside them). */
export const OP_EXIT = { registerFailed: 30, unregisterFailed: 31, cleanupFailed: 32, importFailed: 33 } as const;

/**
 * `Register-ScheduledTask` by its module's ABSOLUTE path (PLAN_pwsh_engine_windows_time.md D4, measured 2026-10-11): with
 * the module path pinned to `$PSHOME\Modules`, PowerShell 7 does not hold ScheduledTasks (Windows PowerShell 5.1 does, at
 * this same System32 path). `[Environment]::SystemDirectory` is GetSystemDirectory, not a variable the elevated process
 * inherits — G1's `PnpDevice` line, the same pattern.
 */
export const IMPORT_SCHEDULED_TASKS = `try { Import-Module -Name (Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\\v1.0\\Modules\\ScheduledTasks') -ErrorAction Stop } catch { exit ${OP_EXIT.importFailed} }`;

/** `0x80070002` as PowerShell prints an `HResult` — "the system cannot find the file specified": no such folder or task. */
export const NOT_FOUND_HRESULT = -2147024894;

/** The here-string's terminator — a line of the XML that started with it would end the string early. */
export const HERE_END = "'@";

/**
 * The elevated install (D3, D8 g0): the `\wsl-care` folder through `Schedule.Service` when it is missing (a fresh machine,
 * or after *Remove*), then `Register-ScheduledTask -Xml … -Force` — create or replace, so install is also update. The XML
 * the person was shown travels INSIDE this text (a single-quoted here-string: nothing in it is expanded), so no file
 * exists that something could swap between showing and registering.
 */
export function installScript(options: GuardOptions): string {
  return [
    MODULES,
    IMPORT_SCHEDULED_TASKS,
    "$ProgressPreference = 'SilentlyContinue'",
    "$xml = @'",
    guardTaskXml(options),
    HERE_END,
    `try { $service = New-Object -ComObject Schedule.Service; $service.Connect() } catch { exit ${OP_EXIT.registerFailed} }`,
    `try { $null = $service.GetFolder('\\${GUARD_FOLDER}') } catch { try { $null = $service.GetFolder('\\').CreateFolder('${GUARD_FOLDER}') } catch { exit ${OP_EXIT.registerFailed} } }`,
    `try { Register-ScheduledTask -TaskPath '\\${GUARD_FOLDER}\\' -TaskName '${GUARD_NAME}' -Xml $xml -Force -ErrorAction Stop | Out-Null } catch { exit ${OP_EXIT.registerFailed} }`,
    'exit 0',
  ].join('\n');
}

/**
 * The elevated removal (D3, D8 g2): every step "if present" — the task (absent is already removed), the folder only when it
 * holds nothing else, the stamp's key, its parent only when empty. The service's start type is NOT reverted: it was the
 * person's fix (story 1, or the guard while the setting was on).
 */
export const REMOVE_SCRIPT = [
  MODULES,
  `try { $service = New-Object -ComObject Schedule.Service; $service.Connect() } catch { exit ${OP_EXIT.unregisterFailed} }`,
  `$folder = $null; try { $folder = $service.GetFolder('\\${GUARD_FOLDER}') } catch { $folder = $null }`,
  'if ($null -ne $folder) {',
  `  try { $folder.DeleteTask('${GUARD_NAME}', 0) } catch { if ($_.Exception.HResult -ne ${NOT_FOUND_HRESULT}) { exit ${OP_EXIT.unregisterFailed} } }`,
  `  try { if (@($folder.GetTasks(1)).Count -eq 0 -and @($folder.GetFolders(0)).Count -eq 0) { $service.GetFolder('\\').DeleteFolder('${GUARD_FOLDER}', 0) } } catch { exit ${OP_EXIT.cleanupFailed} }`,
  '}',
  `try { if (Test-Path -Path '${STAMP_KEY}') { Remove-Item -Path '${STAMP_KEY}' -Recurse -Force -ErrorAction Stop } } catch { exit ${OP_EXIT.cleanupFailed} }`,
  `try { if ((Test-Path -Path '${STAMP_PARENT}') -and @(Get-ChildItem -Path '${STAMP_PARENT}').Count -eq 0 -and @((Get-Item -Path '${STAMP_PARENT}').Property).Count -eq 0) { Remove-Item -Path '${STAMP_PARENT}' -Force -ErrorAction Stop } } catch { exit ${OP_EXIT.cleanupFailed} }`,
  'exit 0',
].join('\n');

/**
 * The UNELEVATED status query (D5, D8) — since WR-E2 the ONE read of every task under `\wsl-care` (PLAN_windows_runner.md §3.8),
 * the guard's own task among them. It lists the folder through `Schedule.Service` and prints tagged lines, in two parts:
 *
 * - the folder's: `channel=` (whether the stop event is logged at all), `stamp=` (the rate-limit stamp under HKLM), `user=<sid>`
 *   (the reader's own SID) and `folder=absent` (`0x80070002` — a machine nothing was installed on: an EMPTY task set, never a
 *   failure), `folder=unreadable` + `hresult=` (anything else, e.g. `0x80070005`) or `folder=present`;
 * - then, per task, a section that opens with `task=<name>` and holds `enabled=`, `state=`, `lastRunUtc=` (`never` for Task
 *   Scheduler's 1899 zero date), `lastResult=` and the definition's `summary:` lines — or, when that ONE task cannot be read,
 *   `read=unreadable` + `hresult=` and nothing else of it (one task's trouble is its own). A task's lines are collected before
 *   they are printed, so a failure half way leaves no half section.
 *
 * Control characters of a task's name and line breaks in a summary line become `?` / a space: whatever a task's author wrote,
 * it cannot add a line to the answer. It changes nothing (no word of the tripwire's mutating list is in it — asserted).
 */
export const QUERY_SCRIPT = [
  MODULES,
  SUMMARY_FUNCTION,
  `$log = $null; try { $log = Get-WinEvent -ListLog '${TIME_SERVICE_CHANNEL}' -ErrorAction Stop } catch { $log = $null }`,
  "$channel = 'channel=unknown'; if ($null -ne $log) { if ($log.IsEnabled) { $channel = 'channel=enabled' } else { $channel = 'channel=disabled' } }",
  // The folder's own lines first, ALL of them before the first task: the parser files every tag before the first `task=` into the
  // folder, so a `channel=` printed last would be read as the last task's (review B2).
  '$channel',
  `if (Test-Path -Path '${STAMP_KEY}') { 'stamp=present' } else { 'stamp=absent' }`,
  "'user=' + [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value",
  '$tasks = @()',
  `try { $service = New-Object -ComObject Schedule.Service; $service.Connect(); $folder = $service.GetFolder('\\${GUARD_FOLDER}'); $tasks = @($folder.GetTasks(1)) } catch { $code = $_.Exception.HResult; if ($code -eq ${NOT_FOUND_HRESULT}) { 'folder=absent' } else { 'folder=unreadable'; 'hresult=0x{0:X8}' -f $code }; exit 0 }`,
  "'folder=present'",
  // GetTasks skips a task this account may not read: each task this extension installs that the list lacks is asked for BY NAME —
  // not found is absent, any other refusal is that task's unreadable section (review M1).
  `foreach ($name in @(${EXPECTED_TASKS.map((n) => `'${n}'`).join(',')})) { if (@($tasks | Where-Object { $_.Name -eq $name }).Count -eq 0) { try { $tasks += $folder.GetTask($name) } catch { if ($_.Exception.HResult -ne ${NOT_FOUND_HRESULT}) { 'task=' + $name; 'read=unreadable'; 'hresult=0x{0:X8}' -f $_.Exception.HResult } } } }`,
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

/**
 * The longest command line the elevated install may need (D3): `CreateProcess` caps a whole command line at 32 767
 * characters and an unelevated `ShellExecute` carried 32 583 argument characters intact (research T16); the ELEVATED path
 * was not measured — it raises UAC — so the request is held well inside both.
 */
export const ELEVATED_COMMAND_LINE_MAX = 16_000;

/** The command line `CreateProcess` receives for a request: the program and each argument, quoted as Node quotes them. */
export function commandLineLength(request: ProcessRequest): number {
  return [request.file, ...request.args].reduce((sum, arg) => sum + arg.length + 3, 0);
}

/**
 * The read-only query, UNELEVATED: the absolute PowerShell with the fixed script — no `runas`, no encoded payload. At its ceiling
 * the runner ends the PowerShell's whole process TREE (`killTree`), not only the PowerShell (WR-E2, plan round 4fc3f328, finding 8).
 */
export function queryRequest(env: Readonly<Record<string, string | undefined>>, timeoutMs: number): ProcessRequest | string {
  const powerShell = powerShellOf(env);

  return typeof powerShell === 'string' ? { file: powerShell, args: ['-NoProfile', '-NonInteractive', '-Command', QUERY_SCRIPT], timeoutMs, killTree: true } : powerShell.reason;
}
