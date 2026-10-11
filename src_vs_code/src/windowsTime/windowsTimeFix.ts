import type { ProcessRequest, ProcessResult, Runner } from '../process/runner';
import { signed32 } from '../process/runner';
import { noticeText } from '../text/safeText';
import { byEngine, elevatedArg, elevatedProgramLine, withStoreNote, type Engine } from '../powershell/elevatedEngine';
import { elevatedRequest, failedExit, LAUNCH_EXIT, type FailedExit } from '../powershell/elevatedLauncher';
import { engineProblem, type EngineSource } from '../powershell/engineHost';

/**
 * *Start Windows Time* (PLAN_windows_time_guard.md D7, the incident of 2026-10-08: `w32time` stopped, Windows 2 h slow):
 * set the Windows Time service to start Automatic (while `wslCare.windowsTime.setAutomaticStart` is on — the owner's
 * decision, default on), start it, and resync — through ONE elevated PowerShell, whose UAC prompt is the person's own
 * confirmation, after a modal that shows the exact commands.
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
 *   <li>the launcher (`powershell/elevatedLauncher.ts`) carries the inner script as `-EncodedCommand`, so no quoting layer
 *       exists between the constant and the elevated PowerShell;</li>
 *   <li>WHICH PowerShell runs elevated is the shared resolver's choice (PLAN_pwsh_engine_windows_time.md): PowerShell 7
 *       resolved and signature-checked, else Windows PowerShell 5.1 — read before the modal, which names it, and read AGAIN
 *       right before the launch; a different program, or a read that did not answer, starts nothing.</li>
 * </ul>
 */

/** The exit codes of the elevated script and its launcher — the closed set this module reads. */
export const EXIT = { ...LAUNCH_EXIT, setAutomaticFailed: 10, startFailed: 11, resyncFailed: 12 } as const;

/** Story 1's start-type line — reused verbatim by the Windows Time guard's task (`guardTask.ts`). */
export const SET_AUTOMATIC = `try { Set-Service -Name w32time -StartupType Automatic -ErrorAction Stop } catch { exit ${EXIT.setAutomaticFailed} }`;

/** Start, then resync three times — the lines the Windows Time guard's task shares (`guardTask.ts`). */
export const BODY = [
  `try { Start-Service -Name w32time -ErrorAction Stop } catch { exit ${EXIT.startFailed} }`,
  "$w32tm = Join-Path $env:SystemRoot 'System32\\w32tm.exe'",
  `for ($i = 0; $i -lt 3; $i++) { & $w32tm /resync /force | Out-Null; if ($LASTEXITCODE -eq 0) { exit ${EXIT.done} }; Start-Sleep -Seconds 2 }`,
  `exit ${EXIT.resyncFailed}`,
] as const;

/** The script the ELEVATED PowerShell runs — shown verbatim in the modal. */
export function innerScript(setAutomaticStart: boolean): string {
  return ["$env:PSModulePath = \"$PSHOME\\Modules\"", ...(setAutomaticStart ? [SET_AUTOMATIC] : []), ...BODY].join('\n');
}

/** The one request this module ever builds — for the ELEVATED program `elevated` (5.1 when absent) — or why none can be. */
export function fixRequest(env: Readonly<Record<string, string | undefined>>, setAutomaticStart: boolean, timeoutMs: number, elevated?: string): ProcessRequest | string {
  return elevatedRequest(env, innerScript(setAutomaticStart), timeoutMs, elevated);
}

export type FixOutcome =
  | { readonly kind: 'done' }
  | { readonly kind: 'declined' }
  | FailedExit
  | { readonly kind: 'timedOut'; readonly timeoutMs: number }
  | { readonly kind: 'notStarted'; readonly reason: string }
  /** The read right before the launch disagreed with the modal (another program, or no answer): nothing ran. */
  | { readonly kind: 'notRun'; readonly sentence: string };

/** The sentence of each failing exit of the shared lines — the guard's last result reads the same ones. */
export const FAILURES: Readonly<Record<number, string>> = {
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

  return code === EXIT.declined ? { kind: 'declined' } : failedExit(FAILURES, code);
}

export interface FixPrompt {
  readonly message: string;
  readonly detail: string;
  readonly confirm: string;
}

export const CONFIRM_LABEL = 'Run it (one UAC prompt follows)';

/** The modal: the exact elevated script, and the program that runs it (the engine the read chose). */
export function fixPrompt(setAutomaticStart: boolean, engine: Engine): FixPrompt {
  return {
    message: 'Start the Windows Time service and resync the Windows clock?',
    detail: [
      'One PowerShell runs ELEVATED — Windows asks you first (UAC) — and runs exactly this:',
      elevatedProgramLine(engine),
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
  /** Which PowerShell runs elevated — before the modal, and again right before the launch (`powershell/engineHost.ts`). */
  readonly engine: EngineSource;
}

const SENTENCE: { readonly [K in FixOutcome['kind']]: (o: Extract<FixOutcome, { kind: K }>, engine: Engine) => string } = {
  done: (_o, engine) => `The Windows Time service is running and the Windows clock was resynchronised. A full check runs now.${byEngine(engine)}`,
  declined: () => 'Nothing was changed: the UAC prompt was declined.',
  failed: (o, engine) => `The Windows Time fix failed: ${withStoreNote(o.sentence, o.launchFailed === true, engine)}.${byEngine(engine)}`,
  timedOut: (o, engine) => `The Windows Time fix did not finish within ${Math.round(o.timeoutMs / 1000)} s (wslCare.timeouts.windowsTimeFixSeconds) — if the UAC prompt is still open, answering it still runs the fix.${byEngine(engine)}`,
  notStarted: (o) => `The Windows Time fix could not start: ${o.reason}.`,
  notRun: (o) => o.sentence,
};

export function outcomeSentence(outcome: FixOutcome, engine: Engine): string {
  return noticeText((SENTENCE[outcome.kind] as (o: FixOutcome, engine: Engine) => string)(outcome, engine));
}

/** The flow, in the order that makes it safe: the engine is read and the request built, the modal shows the commands and the
 * program, ONLY its confirm leads on to the launch, and its closed outcome is told. Nothing runs on a decline or when no
 * request can be built. */
export async function startWindowsTime(deps: FixDeps): Promise<FixOutcome> {
  const setAutomatic = deps.setAutomaticStart();
  const { engine } = await deps.engine.engine();
  const request = fixRequest(deps.env, setAutomatic, deps.timeoutMs(), elevatedArg(engine));
  if (typeof request === 'string') {
    return told(deps, { kind: 'notStarted', reason: request }, engine);
  }
  if (!(await deps.confirm(fixPrompt(setAutomatic, engine)))) {
    return { kind: 'declined' };
  }

  return launchedFix(deps, request, engine);
}

/** The confirmed half (§11 R12): the engine read AGAIN right before the launch — a disagreement runs nothing — then the ONE run. */
async function launchedFix(deps: FixDeps, request: ProcessRequest, engine: Engine): Promise<FixOutcome> {
  const problem = engineProblem(engine, await deps.engine.reread());
  if (problem !== undefined) {
    return told(deps, { kind: 'notRun', sentence: problem }, engine);
  }
  const outcome = outcomeOf(await deps.run(request));
  if (outcome.kind === 'done') {
    deps.afterDone();
  }

  return told(deps, outcome, engine);
}

function told(deps: FixDeps, outcome: FixOutcome, engine: Engine): FixOutcome {
  deps.report(outcomeSentence(outcome, engine), outcome.kind !== 'done' && outcome.kind !== 'declined');
  return outcome;
}
