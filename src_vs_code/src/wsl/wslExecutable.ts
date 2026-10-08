import * as path from 'node:path';

/**
 * A program under `%SystemRoot%\System32`, by its absolute path — or `undefined` when `SystemRoot` is not a drive folder.
 *
 * <p>Never a bare name: a bare name is resolved the way `CreateProcess` does, which searches the CURRENT DIRECTORY before
 * `PATH` (CWE-426 — the family's credential-store extension met it). With no drive-absolute `SystemRoot` there is no
 * program — no fallback to a name, a UNC share or `PATH`; the caller says it cannot be found and starts nothing.</p>
 */
export function system32File(env: Readonly<Record<string, string | undefined>>, ...segments: readonly string[]): string | undefined {
  const root = env.SystemRoot ?? '';

  return /^[A-Za-z]:\\/.test(root) ? path.win32.join(root, 'System32', ...segments) : undefined;
}

/**
 * The one `wsl.exe` the extension starts: `%SystemRoot%\System32\wsl.exe`, by its absolute path. Measured on 2026-10-03:
 * `PATH` holds TWO launchers here, `C:\Windows\System32\wsl.exe` and the Store alias
 * `%LOCALAPPDATA%\Microsoft\WindowsApps\wsl.exe` — which is why it is never a bare name.
 */
export function wslExecutable(env: Readonly<Record<string, string | undefined>>): string | undefined {
  return system32File(env, 'wsl.exe');
}

/** Windows PowerShell 5.1 — `%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe` — the launcher of the Windows
 * Time fix (`windowsTime/windowsTimeFix.ts`). */
export function windowsPowerShell(env: Readonly<Record<string, string | undefined>>): string | undefined {
  return system32File(env, 'WindowsPowerShell', 'v1.0', 'powershell.exe');
}
