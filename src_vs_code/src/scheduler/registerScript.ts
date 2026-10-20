import { MODULES } from '../powershell/modulePath';
import { NOT_FOUND_HRESULT } from './queryScript';

/**
 * The ONE place the extension spells how a scheduled task is registered and deleted (WR-E3 of PLAN_windows_runner.md §3.8; extracted,
 * text-preserving, from `windowsTime/guardScripts.ts` — reuse-first step 2.2, because the Windows tasks needed the same two scripts):
 * `Register-ScheduledTask`, `CreateFolder(`, `DeleteTask(` and `DeleteFolder(` are spelt here and nowhere else in the shipped sources
 * (`registrationPath.test.ts`). The guard runs these scripts ELEVATED, the Windows tasks UNELEVATED; this module cannot tell, and builds
 * no process request.
 *
 * Into a script go only: the folder and the task names (each of the shape `^[a-z-]+$`, else a `RangeError` — the callers pass
 * constants), and the task's XML inside a SINGLE-quoted here-string, where nothing is expanded. Nothing from a setting's text, the
 * page or a read reaches a script.
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

/** The here-string's terminator — a line of the XML that started with it would end the string early. */
export const HERE_END = "'@";

const WORD = /^[a-z-]+$/;

function word(kind: string, text: string): string {
  if (!WORD.test(text)) {
    const shown = JSON.stringify(text.slice(0, 40));
    throw new RangeError(`a ${kind} in a task script is lower-case letters and hyphens, not ${shown}`);
  }

  return text;
}

/** Whether a line of `xml` would end the single-quoted here-string early: PowerShell ends it at a line that BEGINS with the terminator. */
export function terminatesHereString(xml: string): boolean {
  return xml.split(/\r\n|\r|\n/).some((line) => line.startsWith(HERE_END));
}

/**
 * The install (D3, D8 g0): the folder through `Schedule.Service` when it is missing (a fresh machine, or after a removal), then
 * `Register-ScheduledTask -Xml … -Force` — create or replace, so install is also update. The XML the person was shown travels INSIDE
 * this text (a single-quoted here-string: nothing in it is expanded), so no file exists that something could swap between showing and
 * registering. An XML with a terminator line is refused with a `RangeError` (callers refuse it earlier, with a sentence).
 */
export function registerTaskScript(folder: string, name: string, xml: string): string {
  const safeFolder = word('folder', folder);
  const safeName = word('task name', name);
  if (terminatesHereString(xml)) {
    throw new RangeError(`the task XML has a line that starts with the here-string terminator ${HERE_END}, so it cannot be embedded in a script`);
  }

  return [
    MODULES,
    IMPORT_SCHEDULED_TASKS,
    "$ProgressPreference = 'SilentlyContinue'",
    "$xml = @'",
    xml,
    HERE_END,
    `try { $service = New-Object -ComObject Schedule.Service; $service.Connect() } catch { exit ${OP_EXIT.registerFailed} }`,
    `try { $null = $service.GetFolder('\\${safeFolder}') } catch { try { $null = $service.GetFolder('\\').CreateFolder('${safeFolder}') } catch { exit ${OP_EXIT.registerFailed} } }`,
    `try { Register-ScheduledTask -TaskPath '\\${safeFolder}\\' -TaskName '${safeName}' -Xml $xml -Force -ErrorAction Stop | Out-Null } catch { exit ${OP_EXIT.registerFailed} }`,
    'exit 0',
  ].join('\n');
}

/**
 * The removal's lines (D3, D8 g2): every step "if present" — each named task (absent is already removed; any OTHER failure is exit 31 and
 * the folder is then not touched), and the folder only when it holds nothing else (a failure there is exit 32: the tasks are gone). The
 * caller adds what is its own (the guard's stamp) and the final `exit 0`.
 */
export function deleteTasksLines(folder: string, names: readonly string[]): string[] {
  const safeFolder = word('folder', folder);

  return [
    `try { $service = New-Object -ComObject Schedule.Service; $service.Connect() } catch { exit ${OP_EXIT.unregisterFailed} }`,
    `$folder = $null; try { $folder = $service.GetFolder('\\${safeFolder}') } catch { $folder = $null }`,
    'if ($null -ne $folder) {',
    ...names.map((name) => `  try { $folder.DeleteTask('${word('task name', name)}', 0) } catch { if ($_.Exception.HResult -ne ${NOT_FOUND_HRESULT}) { exit ${OP_EXIT.unregisterFailed} } }`),
    `  try { if (@($folder.GetTasks(1)).Count -eq 0 -and @($folder.GetFolders(0)).Count -eq 0) { $service.GetFolder('\\').DeleteFolder('${safeFolder}', 0) } } catch { exit ${OP_EXIT.cleanupFailed} }`,
    '}',
  ];
}

/** A removal of `names` from `folder` and nothing else: the modules pinned, the deletes, a clean exit. */
export function removeTasksScript(folder: string, names: readonly string[]): string {
  return [MODULES, ...deleteTasksLines(folder, names), 'exit 0'].join('\n');
}
