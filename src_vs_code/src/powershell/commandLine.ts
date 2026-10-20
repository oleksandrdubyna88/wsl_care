import type { ProcessRequest } from '../process/runner';
import { windowsPowerShell } from '../wsl/wslExecutable';

/**
 * What every PowerShell this extension starts shares, ELEVATED or not (moved out of `elevatedLauncher.ts` by the coai code round of WR-E3,
 * findings 1, 2 and 5): the `-EncodedCommand` form, the path PowerShell reads inside single quotes, Windows PowerShell's absolute path, and the bound
 * on one command line. The unelevated side (the Windows tasks, the registration script, the Task Scheduler read) imports THIS module; nothing
 * reachable from it imports the elevated launcher (`unelevatedBoundary.test.ts`).
 */
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

/** Why no Windows PowerShell request can be built on this machine. */
export const NO_POWERSHELL = 'SystemRoot does not name a drive folder holding System32\\WindowsPowerShell\\v1.0\\powershell.exe';

/** The absolute Windows PowerShell, when `SystemRoot` names a drive folder PowerShell can quote — else why not. */
export function powerShellOf(env: Readonly<Record<string, string | undefined>>): string | { readonly reason: string } {
  const powerShell = windowsPowerShell(env);

  return powerShell === undefined || !QUOTABLE_PATH.test(powerShell) ? { reason: NO_POWERSHELL } : powerShell;
}

/**
 * The longest command line one run may need: `CreateProcess` caps a whole command line at 32 767 characters and an unelevated `ShellExecute`
 * carried 32 583 argument characters intact (research T16); the request is held well inside both. It bounds the elevated install and the unelevated
 * install alike (the guard's flows and tests still know it as `ELEVATED_COMMAND_LINE_MAX`, re-exported by `windowsTime/guardScripts.ts`).
 */
export const COMMAND_LINE_MAX = 16_000;

/** The command line `CreateProcess` receives for a request: the program and each argument, quoted as Node quotes them. */
export function commandLineLength(request: ProcessRequest): number {
  return [request.file, ...request.args].reduce((sum, arg) => sum + arg.length + 3, 0);
}
