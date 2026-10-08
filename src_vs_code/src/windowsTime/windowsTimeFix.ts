import type { ProcessRequest, ProcessResult, Runner } from '../process/runner';
import { signed32 } from '../process/runner';
import { noticeText } from '../text/safeText';
import { windowsPowerShell } from '../wsl/wslExecutable';

/**
 * *Start Windows Time* (PLAN_windows_time_guard.md D7, the incident of 2026-10-08: `w32time` stopped, Windows 2 h slow):
 * set the Windows Time service to start Automatic (while `wslCare.windowsTime.setAutomaticStart` is on — the owner's
 * decision, default on), start it, and resync — through ONE elevated Windows PowerShell, whose UAC prompt is the person's
 * own confirmation, after a modal that shows the exact commands.
 *
 * <p>What is fixed, and why:</p>
 * <ul>
 *   <li>the commands are MODULE CONSTANTS: nothing from the daemon, the page or a setting's text reaches them — a setting
 *       only picks one of the two variants. A value from WSL must never become an administrator's command on Windows;</li>
 *   <li>the elevated script finds nothing by name: its modules come from `$PSHOME\Modules` (a user's Documents folder is
 *       first in `PSModulePath` otherwise) and `w32tm.exe` from `%SystemRoot%\System32`;</li>
 *   <li>an elevated child's streams cannot be read across the integrity boundary (`Start-Process -Verb RunAs` refuses
 *       redirection), so each failure has its OWN exit code and the sentence is ours. A result file was rejected: an
 *       elevated write to a fixed path in the user's `%TEMP%` follows a link the user's own processes can plant;</li>
 *   <li>the inner script travels as `-EncodedCommand` (UTF-16LE base64), so no quoting layer exists between the constant
 *       and the elevated PowerShell; the outer one catches the UAC refusal, which `Start-Process` THROWS, as 1223.</li>
 * </ul>
 */

/** The exit codes of the elevated script and its launcher — the closed set this module reads. */
export const EXIT = { done: 0, setAutomaticFailed: 10, startFailed: 11, resyncFailed: 12, launchFailed: 13, declined: 1223 } as const;

const SET_AUTOMATIC = `try { Set-Service -Name w32time -StartupType Automatic -ErrorAction Stop } catch { exit ${EXIT.setAutomaticFailed} }`;

const BODY = [
  `try { Start-Service -Name w32time -ErrorAction Stop } catch { exit ${EXIT.startFailed} }`,
  "$w32tm = Join-Path $env:SystemRoot 'System32\\w32tm.exe'",
  `for ($i = 0; $i -lt 3; $i++) { & $w32tm /resync /force | Out-Null; if ($LASTEXITCODE -eq 0) { exit ${EXIT.done} }; Start-Sleep -Seconds 2 }`,
  `exit ${EXIT.resyncFailed}`,
] as const;

/** The script the ELEVATED PowerShell runs — shown verbatim in the modal. */
export function innerScript(setAutomaticStart: boolean): string {
  return ["$env:PSModulePath = \"$PSHOME\\Modules\"", ...(setAutomaticStart ? [SET_AUTOMATIC] : []), ...BODY].join('\n');
}

/** `-EncodedCommand`'s form: base64 of the UTF-16LE text, as PowerShell documents it. */
export function encoded(script: string): string {
  return Buffer.from(script, 'utf16le').toString('base64');
}

/** A path PowerShell reads inside single quotes with nothing to escape. */
const QUOTABLE_PATH = /^[A-Za-z]:\\[^'`$\r\n"]*$/;

/** The UNELEVATED launcher's script: start the elevated PowerShell and wait, catch the UAC refusal (Win32 error 1223,
 * thrown by `Start-Process`, possibly as an inner exception), hand back the elevated exit code. */
export function outerScript(powerShell: string, inner: string): string {
  return [
    `try { $p = Start-Process -FilePath '${powerShell}' -Verb RunAs -Wait -PassThru -WindowStyle Hidden -ArgumentList '-NoProfile','-NonInteractive','-EncodedCommand','${encoded(inner)}' }`,
    `catch { $e = $_.Exception; while ($null -ne $e) { if ($e.NativeErrorCode -eq ${EXIT.declined}) { exit ${EXIT.declined} }; $e = $e.InnerException }; exit ${EXIT.launchFailed} }`,
    'exit $p.ExitCode',
  ].join('\n');
}

/** The one request this module ever builds — or why none can be (no `SystemRoot` drive folder). */
export function fixRequest(env: Readonly<Record<string, string | undefined>>, setAutomaticStart: boolean, timeoutMs: number): ProcessRequest | string {
  const powerShell = windowsPowerShell(env);
  if (powerShell === undefined || !QUOTABLE_PATH.test(powerShell)) {
    return 'SystemRoot does not name a drive folder holding System32\\WindowsPowerShell\\v1.0\\powershell.exe';
  }

  return { file: powerShell, args: ['-NoProfile', '-NonInteractive', '-Command', outerScript(powerShell, innerScript(setAutomaticStart))], timeoutMs };
}

export type FixOutcome =
  | { readonly kind: 'done' }
  | { readonly kind: 'declined' }
  | { readonly kind: 'failed'; readonly sentence: string }
  | { readonly kind: 'timedOut'; readonly timeoutMs: number }
  | { readonly kind: 'notStarted'; readonly reason: string };

const FAILURES: Readonly<Record<number, string>> = {
  [EXIT.setAutomaticFailed]: 'Set-Service could not set the Windows Time service to start Automatic',
  [EXIT.startFailed]: 'the Windows Time service could not be started (Start-Service failed)',
  [EXIT.resyncFailed]: 'the Windows Time service runs, but `w32tm /resync /force` failed three times — it may not have found a time source yet; check again in a minute',
  [EXIT.launchFailed]: 'the elevated PowerShell could not be started',
};

/** The answer as one closed outcome — pure, so every exit is a unit test. */
const OF_RESULT: { readonly [K in ProcessResult['kind']]: (r: Extract<ProcessResult, { kind: K }>) => FixOutcome } = {
  exited: (r) => exitedOutcome(signed32(r.code)),
  timedOut: (r) => ({ kind: 'timedOut', timeoutMs: r.timeoutMs }),
  failedToStart: (r) => ({ kind: 'notStarted', reason: r.reason }),
  signalled: (r) => ({ kind: 'failed', sentence: `the PowerShell launcher was stopped (${r.signal})` }),
  tooMuchOutput: () => ({ kind: 'failed', sentence: 'the PowerShell launcher printed more than the extension reads' }),
};

export function outcomeOf(result: ProcessResult): FixOutcome {
  return (OF_RESULT[result.kind] as (r: ProcessResult) => FixOutcome)(result);
}

function exitedOutcome(code: number): FixOutcome {
  if (code === EXIT.done) {
    return { kind: 'done' };
  }

  return code === EXIT.declined ? { kind: 'declined' } : { kind: 'failed', sentence: FAILURES[code] ?? `the elevated PowerShell exited ${code}` };
}

export interface FixPrompt {
  readonly message: string;
  readonly detail: string;
  readonly confirm: string;
}

export const CONFIRM_LABEL = 'Run it (one UAC prompt follows)';

export function fixPrompt(setAutomaticStart: boolean): FixPrompt {
  return {
    message: 'Start the Windows Time service and resync the Windows clock?',
    detail: [
      'One Windows PowerShell runs ELEVATED — Windows asks you first (UAC) — and runs exactly this:',
      '',
      innerScript(setAutomaticStart),
      '',
      setAutomaticStart
        ? 'It also sets the service to start Automatic (setting wslCare.windowsTime.setAutomaticStart). Automatic does not restart it if other software stops it again.'
        : 'It does not change how the service starts (setting wslCare.windowsTime.setAutomaticStart is off).',
      'Nothing else on Windows is changed. Afterwards a full check runs, so the panel shows the clock as it then is.',
    ].join('\n'),
    confirm: CONFIRM_LABEL,
  };
}

export interface FixDeps {
  readonly env: Readonly<Record<string, string | undefined>>;
  readonly setAutomaticStart: () => boolean;
  readonly timeoutMs: () => number;
  readonly confirm: (prompt: FixPrompt) => Promise<boolean>;
  readonly run: Runner;
  readonly report: (message: string, failed: boolean) => void;
  /** After `done`: the existing *Run full check now*, so the persisted verdicts — the truth — turn green. */
  readonly afterDone: () => void;
}

const SENTENCE: { readonly [K in FixOutcome['kind']]: (o: Extract<FixOutcome, { kind: K }>) => string } = {
  done: () => 'The Windows Time service is running and the Windows clock was resynchronised. A full check runs now.',
  declined: () => 'Nothing was changed: the UAC prompt was declined.',
  failed: (o) => `The Windows Time fix failed: ${o.sentence}.`,
  timedOut: (o) => `The Windows Time fix did not finish within ${Math.round(o.timeoutMs / 1000)} s (wslCare.timeouts.windowsTimeFixSeconds) — if the UAC prompt is still open, answering it still runs the fix.`,
  notStarted: (o) => `The Windows Time fix could not start: ${o.reason}.`,
};

export function outcomeSentence(outcome: FixOutcome): string {
  return noticeText((SENTENCE[outcome.kind] as (o: FixOutcome) => string)(outcome));
}

/** The flow, in the order that makes it safe: the request is built, the modal shows the commands, ONLY its confirm starts
 * the one process, and its closed outcome is told. Nothing runs on a decline or when no request can be built. */
export async function startWindowsTime(deps: FixDeps): Promise<FixOutcome> {
  const setAutomatic = deps.setAutomaticStart();
  const request = fixRequest(deps.env, setAutomatic, deps.timeoutMs());
  if (typeof request === 'string') {
    return told(deps, { kind: 'notStarted', reason: request });
  }
  if (!(await deps.confirm(fixPrompt(setAutomatic)))) {
    return { kind: 'declined' };
  }
  const outcome = outcomeOf(await deps.run(request));
  if (outcome.kind === 'done') {
    deps.afterDone();
  }

  return told(deps, outcome);
}

function told(deps: FixDeps, outcome: FixOutcome): FixOutcome {
  deps.report(outcomeSentence(outcome), outcome.kind !== 'done' && outcome.kind !== 'declined');
  return outcome;
}
