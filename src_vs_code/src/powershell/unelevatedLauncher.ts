import type { ProcessRequest } from '../process/runner';
import { COMMAND_LINE_MAX, commandLineLength, encoded, quotablePath } from './commandLine';
import type { Engine } from './engineTypes';

/**
 * The UNELEVATED launcher (WR-E3 of PLAN_windows_runner.md §3.4, §3.8), beside the elevated one (`elevatedLauncher.ts`): ONE request
 * that starts the engine's PowerShell DIRECTLY — the runner's own ceiling and process-tree kill apply, and the child's stderr is
 * readable (the elevated road cannot read streams). There is no verb, no second PowerShell and no `Start-Process` in it: it runs as
 * the person's own account, not elevated, and Windows asks nothing.
 *
 * The script travels as `-EncodedCommand` (UTF-16LE base64), so no quoting layer lies between a module's text and PowerShell: the
 * task XML carries double quotes, which `-Command` would have to carry through `CommandLineToArgvW`.
 */

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

  return length < COMMAND_LINE_MAX ? request : `the PowerShell script is too long for one command line (${length} characters)`;
}
