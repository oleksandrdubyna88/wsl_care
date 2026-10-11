import type { ProcessRequest } from '../process/runner';
import { windowsPowerShell } from '../wsl/wslExecutable';

/**
 * The ONE elevated launcher (PLAN_windows_time_guard.md D7, built for *Start Windows Time*; moved here by
 * PLAN_pwsh_engine_windows_time.md §11 R2 so the engine resolver and every elevated flow can share it without an import
 * cycle): the absolute UNELEVATED Windows PowerShell runs {@link outerScript}, which starts the elevated program through
 * `System.Diagnostics.Process.Start` with the `runas` verb — the inner script as `-EncodedCommand` (UTF-16LE base64), so no
 * quoting layer exists between a module constant and the elevated PowerShell. An elevated child's streams cannot be read
 * across the integrity boundary, so the launcher reads only exit codes: {@link LAUNCH_EXIT}.
 */

/** The launcher's own exits: the elevated program's 0, any launch failure 13, and the UAC refusal as the 1223 Process.Start throws. */
export const LAUNCH_EXIT = { done: 0, launchFailed: 13, declined: 1223 } as const;

/** `-EncodedCommand`'s form: base64 of the UTF-16LE text, as PowerShell documents it. */
export function encoded(script: string): string {
  return Buffer.from(script, 'utf16le').toString('base64');
}

/**
 * A path PowerShell reads inside single quotes with nothing to escape — no quote, backtick, `$`, double quote or line
 * break, and none of PowerShell's SMART quotes (U+2018–U+201F close a single- or double-quoted string just as the ASCII
 * ones do; a curly apostrophe is a legal character in a Windows user name — own review 2, F2).
 */
const QUOTABLE_PATH = /^[A-Za-z]:\\[^'`$\r\n"‘-‟]*$/;

/** Whether PowerShell reads `file` inside single quotes with nothing to escape — the test every program this launcher starts passes. */
export function quotablePath(file: string): boolean {
  return QUOTABLE_PATH.test(file);
}

/**
 * The UNELEVATED launcher's script: start the elevated PowerShell through `System.Diagnostics.Process.Start` with the
 * `runas` verb, wait, and hand back its exit code. The UAC refusal is the `Win32Exception` (1223) that `Process.Start`
 * throws — NOT `Start-Process -Verb RunAs`, which rethrows it as an InvalidOperationException carrying only the message, so
 * 1223 could never be read (own code review #1); any other launch failure exits 13.
 */
export function outerScript(powerShell: string, inner: string, elevated: string = powerShell): string {
  return [
    `$psi = New-Object System.Diagnostics.ProcessStartInfo -ArgumentList '${elevated}'`,
    "$psi.Verb = 'runas'; $psi.UseShellExecute = $true; $psi.WindowStyle = 'Hidden'",
    `$psi.Arguments = '-NoProfile -NonInteractive -EncodedCommand ${encoded(inner)}'`,
    `try { $p = [System.Diagnostics.Process]::Start($psi) } catch [System.ComponentModel.Win32Exception] { if ($_.Exception.NativeErrorCode -eq ${LAUNCH_EXIT.declined}) { exit ${LAUNCH_EXIT.declined} }; exit ${LAUNCH_EXIT.launchFailed} } catch { exit ${LAUNCH_EXIT.launchFailed} }`,
    `if ($null -eq $p) { exit ${LAUNCH_EXIT.launchFailed} }`,
    '$p.WaitForExit(); exit $p.ExitCode',
  ].join('\n');
}

/** Why no Windows PowerShell request can be built on this machine. */
export const NO_POWERSHELL = 'SystemRoot does not name a drive folder holding System32\\WindowsPowerShell\\v1.0\\powershell.exe';

/** The absolute Windows PowerShell, when `SystemRoot` names a drive folder PowerShell can quote — else why not. */
export function powerShellOf(env: Readonly<Record<string, string | undefined>>): string | { readonly reason: string } {
  const powerShell = windowsPowerShell(env);

  return powerShell === undefined || !QUOTABLE_PATH.test(powerShell) ? { reason: NO_POWERSHELL } : powerShell;
}

/**
 * ONE elevated run of `inner` — the request *Start Windows Time*, the Windows Time guard's install / remove and *Enable
 * <GPU>* build (PLAN_windows_time_task.md D3, PLAN_gpu_health.md D4): the absolute unelevated PowerShell runs `outerScript`,
 * which starts the elevated one. `elevated` names the PowerShell that runs ELEVATED — by default the same Windows PowerShell
 * as the launcher; *Enable <GPU>* passes the MSI `pwsh` when it is installed. It must be a path PowerShell can quote, or no
 * request is built.
 */
export function elevatedRequest(env: Readonly<Record<string, string | undefined>>, inner: string, timeoutMs: number, elevated?: string): ProcessRequest | string {
  const powerShell = powerShellOf(env);
  if (typeof powerShell !== 'string') {
    return powerShell.reason;
  }
  const target = elevated ?? powerShell;

  return QUOTABLE_PATH.test(target)
    ? { file: powerShell, args: ['-NoProfile', '-NonInteractive', '-Command', outerScript(powerShell, inner, target)], timeoutMs }
    : `the elevated PowerShell's path cannot be quoted safely: ${target}`;
}


/** A failing exit of an elevated run: its sentence, and whether the elevated program itself could not be started (13). */
export interface FailedExit {
  readonly kind: 'failed';
  readonly sentence: string;
  readonly launchFailed?: true;
}

/**
 * A failing exit as ONE outcome, shared by every elevated flow (PLAN_pwsh_engine_windows_time.md, its own code review): the
 * sentence from the caller's table, else "exited <code>"; `launchFailed` on 13 — the Store note's case.
 */
export function failedExit(failures: Readonly<Record<number, string>>, code: number): FailedExit {
  const sentence = failures[code] ?? `the elevated PowerShell exited ${code}`;

  return code === LAUNCH_EXIT.launchFailed ? { kind: 'failed', sentence, launchFailed: true } : { kind: 'failed', sentence };
}
