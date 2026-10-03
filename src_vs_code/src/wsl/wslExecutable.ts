import * as path from 'node:path';

/**
 * The one `wsl.exe` the extension starts: `%SystemRoot%\System32\wsl.exe`, by its absolute path.
 *
 * <p>Never a bare `wsl` / `wsl.exe`: a bare name is resolved the way `CreateProcess` does, which searches the CURRENT
 * DIRECTORY before `PATH` (CWE-426 — the family's credential-store extension met it). Measured on 2026-10-03: `PATH`
 * holds TWO launchers here, `C:\Windows\System32\wsl.exe` and the Store alias
 * `%LOCALAPPDATA%\Microsoft\WindowsApps\wsl.exe`. With no drive-absolute `SystemRoot` there is no launcher — no fallback
 * to a name, a UNC share or `PATH`; the caller says WSL cannot be found and starts nothing.</p>
 */
export function wslExecutable(env: Readonly<Record<string, string | undefined>>): string | undefined {
  const root = env.SystemRoot ?? '';

  return /^[A-Za-z]:\\/.test(root) ? path.win32.join(root, 'System32', 'wsl.exe') : undefined;
}
