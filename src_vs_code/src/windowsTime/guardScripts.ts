import type { ProcessRequest } from '../process/runner';
import { GUARD_FOLDER, GUARD_NAME, guardTaskXml, STAMP_KEY, STAMP_PARENT, SUMMARY_FUNCTION, TIME_SERVICE_CHANNEL, type GuardOptions } from './guardTask';
import { elevatedRequest, powerShellOf } from './windowsTimeFix';

/**
 * The three PowerShell texts around the guard's task (PLAN_windows_time_task.md D3, D5, D8) — install and remove, each run
 * ONCE elevated through story 1's launcher (`elevatedRequest` → `outerScript`: `Process.Start` with `runas`, 1223 =
 * declined, 13 = launch failed), and the UNELEVATED status query. All three are constants of this module or pure
 * functions of the settings: nothing from the daemon, the page or a setting's TEXT reaches them (a setting only picks a
 * number or a variant).
 */

/** The install / remove scripts' own exit codes (the launcher's 13 and 1223, and PowerShell's 1, are read beside them). */
export const OP_EXIT = { registerFailed: 30, unregisterFailed: 31, cleanupFailed: 32 } as const;

const MODULES = "$env:PSModulePath = Join-Path $PSHOME 'Modules'";

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
 * The UNELEVATED status query (D5, D8): reads the task through `Schedule.Service` and prints tagged lines — `guard=absent`
 * (`0x80070002`, measured for a missing folder and a missing task), `guard=unreadable` + `hresult=` (anything else, e.g.
 * `0x80070005`), or `guard=present` with `enabled=`, `lastRunUtc=` (`never` for Task Scheduler's 1899 zero date),
 * `lastResult=` and the definition's `summary:` lines; and always `channel=` — whether the stop event is logged at all.
 * It changes nothing (no word of the tripwire's mutating list is in it — asserted).
 */
export const QUERY_SCRIPT = [
  MODULES,
  SUMMARY_FUNCTION,
  `$log = $null; try { $log = Get-WinEvent -ListLog '${TIME_SERVICE_CHANNEL}' -ErrorAction Stop } catch { $log = $null }`,
  "$channel = 'channel=unknown'; if ($null -ne $log) { if ($log.IsEnabled) { $channel = 'channel=enabled' } else { $channel = 'channel=disabled' } }",
  '$task = $null',
  `try { $service = New-Object -ComObject Schedule.Service; $service.Connect(); $task = $service.GetFolder('\\${GUARD_FOLDER}').GetTask('${GUARD_NAME}') } catch { $code = $_.Exception.HResult; if ($code -eq ${NOT_FOUND_HRESULT}) { 'guard=absent' } else { 'guard=unreadable'; 'hresult=0x{0:X8}' -f $code }; $channel; exit 0 }`,
  "'guard=present'",
  "'enabled=' + $task.Enabled",
  "$run = $task.LastRunTime; if ($run.Year -lt 2000) { 'lastRunUtc=never' } else { 'lastRunUtc=' + $run.ToUniversalTime().ToString('o') }",
  "'lastResult=' + $task.LastTaskResult",
  "Get-WslCareSummary $task.Definition | ForEach-Object { 'summary:' + $_ }",
  '$channel',
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

export function installRequest(env: Readonly<Record<string, string | undefined>>, options: GuardOptions, timeoutMs: number): ProcessRequest | string {
  return elevatedRequest(env, installScript(options), timeoutMs);
}

export function removeRequest(env: Readonly<Record<string, string | undefined>>, timeoutMs: number): ProcessRequest | string {
  return elevatedRequest(env, REMOVE_SCRIPT, timeoutMs);
}

/** The read-only query, UNELEVATED: the absolute PowerShell with the fixed script — no `runas`, no encoded payload. */
export function queryRequest(env: Readonly<Record<string, string | undefined>>, timeoutMs: number): ProcessRequest | string {
  const powerShell = powerShellOf(env);

  return typeof powerShell === 'string' ? { file: powerShell, args: ['-NoProfile', '-NonInteractive', '-Command', QUERY_SCRIPT], timeoutMs } : powerShell.reason;
}
