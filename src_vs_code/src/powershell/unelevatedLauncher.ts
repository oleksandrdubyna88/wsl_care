import type { ProcessRequest } from '../process/runner';
import type { Engine } from './elevatedEngine';
import { encoded, quotablePath } from './elevatedLauncher';

/**
 * The UNELEVATED launcher (WR-E3 of PLAN_windows_runner.md §3.4, §3.8), beside the elevated one (`elevatedLauncher.ts`): ONE request
 * that starts the engine's PowerShell DIRECTLY — the runner's own ceiling and process-tree kill apply, and the child's stderr is
 * readable (the elevated road cannot read streams). There is no verb, no second PowerShell and no `Start-Process` in it: it runs as
 * the person's own account, not elevated, and Windows asks nothing.
 *
 * The script travels as `-EncodedCommand` (UTF-16LE base64), so no quoting layer lies between a module's text and PowerShell: the
 * task XML carries double quotes, which `-Command` would have to carry through `CommandLineToArgvW`.
 */

/**
 * The longest command line one run may need (D3): `CreateProcess` caps a whole command line at 32 767 characters and an unelevated
 * `ShellExecute` carried 32 583 argument characters intact (research T16); the request is held well inside both. Moved here from
 * `windowsTime/guardScripts.ts`, which re-exports it for the guard's flows and tests.
 */
export const ELEVATED_COMMAND_LINE_MAX = 16_000;

/** The command line `CreateProcess` receives for a request: the program and each argument, quoted as Node quotes them. */
export function commandLineLength(request: ProcessRequest): number {
  return [request.file, ...request.args].reduce((sum, arg) => sum + arg.length + 3, 0);
}

/** The first three arguments of every unelevated run — `unelevatedRequest` builds them and nothing else builds this request. */
export const UNELEVATED_SWITCHES: readonly string[] = ['-NoProfile', '-NonInteractive', '-EncodedCommand'];

/**
 * ONE unelevated run of `inner` by `engine`: its absolute program, the fixed switches, the encoded script, the ceiling and the tree
 * kill at it. A request is not built (the reason is the answer) when no engine can be named, its path cannot be quoted by PowerShell,
 * or the command line would be too long.
 */
export function unelevatedRequest(engine: Engine, inner: string, timeoutMs: number): ProcessRequest | string {
  if (engine.file === '') {
    return 'no PowerShell can be named on this machine (SystemRoot does not name a drive folder holding Windows PowerShell)';
  }
  if (!quotablePath(engine.file)) {
    return `the PowerShell's path cannot be quoted safely: ${engine.file}`;
  }
  const request: ProcessRequest = { file: engine.file, args: [...UNELEVATED_SWITCHES, encoded(inner)], timeoutMs, killTree: true };
  const length = commandLineLength(request);

  return length < ELEVATED_COMMAND_LINE_MAX ? request : `the PowerShell script is too long for one command line (${length} characters)`;
}
