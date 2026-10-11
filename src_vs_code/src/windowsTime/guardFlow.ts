import type { DurableStore } from '../cleanup/journal';
import type { ProcessRequest, ProcessResult, Runner } from '../process/runner';
import { signed32 } from '../process/runner';
import { noticeText } from '../text/safeText';
import { byEngine, elevatedArg, elevatedProgramLine, withStoreNote, type Engine } from '../powershell/elevatedEngine';
import { elevatedRequest } from '../powershell/elevatedLauncher';
import { engineProblem, type EngineSource } from '../powershell/engineHost';
import { clearPending, readPending, writePending } from './guardPending';
import { installScript, OP_EXIT, REMOVE_SCRIPT } from './guardScripts';
import { GUARD_FOLDER, GUARD_NAME, guardScript, type GuardOptions } from './guardTask';
import { EXIT, FAILURES, type FixPrompt } from './windowsTimeFix';

/**
 * *Install the Windows Time guard* and *Remove the Windows Time guard* (PLAN_windows_time_task.md D3, D4, D8) — in the order
 * that makes them safe: the engine is read and the request built (or the reason none can be is told); for an install the
 * EXACT elevated script — the task's XML inside it — is opened read-only first; a modal names what will run and the program
 * that runs it; after its confirm the engine is read AGAIN (PLAN_pwsh_engine_windows_time.md D3 — another program, or no
 * answer, starts nothing), the time is taken, another window's pending run is looked for once more, and only then is the
 * pending run persisted and the ONE elevated PowerShell started (§11 R5); its closed outcome is told; the status is read
 * again. Nothing runs on a decline, and nothing starts while another window's elevated run still stands (`guardPending.ts`).
 */

export type GuardOp = 'install' | 'remove';

export type GuardOutcome =
  | { readonly kind: 'done' }
  | { readonly kind: 'declined' }
  | { readonly kind: 'busy' }
  | { readonly kind: 'failed'; readonly sentence: string; readonly launchFailed?: true }
  | { readonly kind: 'timedOut'; readonly timeoutMs: number }
  | { readonly kind: 'notStarted'; readonly reason: string }
  /** The read right before the launch disagreed with the modal (another program, or no answer): nothing ran. */
  | { readonly kind: 'notRun'; readonly sentence: string };

/** Each failing exit of the elevated run, by the operation that ran (D3, D8 o8b). */
const OP_FAILURES: Readonly<Record<number, string>> = {
  1: 'the elevated PowerShell failed before its first step (exit 1)',
  [EXIT.launchFailed]: FAILURES[EXIT.launchFailed] ?? '',
  [OP_EXIT.registerFailed]: 'Task Scheduler did not register the task (creating the \\wsl-care folder or Register-ScheduledTask failed)',
  [OP_EXIT.unregisterFailed]: 'Task Scheduler did not delete the task',
  [OP_EXIT.cleanupFailed]: 'the task is gone, but its empty \\wsl-care folder or its stamp under HKLM\\SOFTWARE\\wsl-care could not be removed',
  [OP_EXIT.importFailed]: 'the elevated PowerShell could not load its ScheduledTasks module',
};

/** A failing exit: its sentence, and whether the elevated program itself could not be started (13 — the Store note's case). */
function failedOutcome(code: number): GuardOutcome {
  const sentence = OP_FAILURES[code] ?? `the elevated PowerShell exited ${code}`;

  return code === EXIT.launchFailed ? { kind: 'failed', sentence, launchFailed: true } : { kind: 'failed', sentence };
}

function exitedOutcome(code: number): GuardOutcome {
  if (code === EXIT.done) {
    return { kind: 'done' };
  }

  return code === EXIT.declined ? { kind: 'declined' } : failedOutcome(code);
}

const OF_RESULT: { readonly [K in ProcessResult['kind']]: (r: Extract<ProcessResult, { kind: K }>) => GuardOutcome } = {
  exited: (r) => exitedOutcome(signed32(r.code)),
  timedOut: (r) => ({ kind: 'timedOut', timeoutMs: r.timeoutMs }),
  failedToStart: (r) => ({ kind: 'notStarted', reason: r.reason }),
  signalled: (r) => ({ kind: 'failed', sentence: `the PowerShell launcher was stopped (${r.signal})` }),
  tooMuchOutput: () => ({ kind: 'failed', sentence: 'the PowerShell launcher printed more than the extension reads' }),
};

export function guardOutcomeOf(result: ProcessResult): GuardOutcome {
  return (OF_RESULT[result.kind] as (r: ProcessResult) => GuardOutcome)(result);
}

const NAMES: { readonly [K in GuardOp]: string } = { install: 'install', remove: 'removal' };

type Sentence<K extends GuardOutcome['kind']> = (o: Extract<GuardOutcome, { kind: K }>, op: GuardOp, engine: Engine) => string;

const SENTENCE: { readonly [K in GuardOutcome['kind']]: Sentence<K> } = {
  done: (_o, op, engine) => (op === 'install' ? 'The Windows Time guard is installed: Task Scheduler now starts the Windows Time service and resyncs by itself.' : 'The Windows Time guard is removed — its task, its folder and its stamp.') + byEngine(engine),
  declined: () => 'Nothing was changed: the UAC prompt was declined.',
  busy: () => 'An elevated install or removal of the Windows Time guard is still waiting (its UAC prompt, or the run itself) — nothing new was started.',
  failed: (o, op, engine) => `The Windows Time guard's ${NAMES[op]} failed: ${withStoreNote(o.sentence, o.launchFailed === true, engine)}.${byEngine(engine)}`,
  timedOut: (o, op, engine) => `The Windows Time guard's ${NAMES[op]} did not finish within ${Math.round(o.timeoutMs / 1000)} s (wslCare.timeouts.windowsTimeGuardSeconds) — if the UAC prompt is still open, answering it still runs it; the panel shows what Task Scheduler holds.${byEngine(engine)}`,
  notStarted: (o, op) => `The Windows Time guard's ${NAMES[op]} could not start: ${o.reason}.`,
  notRun: (o) => o.sentence,
};

export function guardOutcomeSentence(outcome: GuardOutcome, op: GuardOp, engine: Engine): string {
  return noticeText((SENTENCE[outcome.kind] as Sentence<GuardOutcome['kind']>)(outcome, op, engine));
}

export const INSTALL_CONFIRM = 'Install it (one UAC prompt follows)';
export const REMOVE_CONFIRM = 'Remove it (one UAC prompt follows)';

export function installPrompt(options: GuardOptions, engine: Engine): FixPrompt {
  const delay = options.delaySeconds > 0 ? ` (each ${options.delaySeconds} s after the event)` : '';
  return {
    message: 'Install the Windows Time guard — a scheduled task that runs as SYSTEM?',
    detail: [
      `One PowerShell runs ELEVATED — Windows asks you first (UAC) — and registers ONE scheduled task, \\${GUARD_FOLDER}\\${GUARD_NAME}, exactly as the read-only editor tab beside this dialog shows.`,
      elevatedProgramLine(engine),
      '',
      `The task runs as SYSTEM in Windows PowerShell 5.1 at startup, at logon, when the Windows Time service logs that it is stopping, when its start type is changed${delay}, and every ${options.everyHours} h. Each run does only this:`,
      '',
      guardScript(options),
      '',
      `It starts the service at most once every ${options.minMinutesBetweenStarts} min (a stamp under HKLM\\SOFTWARE\\wsl-care), so it never loops against software that stops it again.`,
      options.setAutomaticStart ? 'It also sets the service to start Automatic when it does not already (setting wslCare.windowsTime.setAutomaticStart) — so a start type set to Disabled is undone.' : 'It does not change how the service starts (setting wslCare.windowsTime.setAutomaticStart is off).',
      'Remove it at any time with "Remove the Windows Time guard". Nothing else on Windows is changed.',
    ].join('\n'),
    confirm: INSTALL_CONFIRM,
  };
}

export function removePrompt(engine: Engine): FixPrompt {
  return {
    message: 'Remove the Windows Time guard?',
    detail: [
      `One PowerShell runs ELEVATED — Windows asks you first (UAC) — and runs exactly this: it deletes the task \\${GUARD_FOLDER}\\${GUARD_NAME}, the folder when nothing else is in it, and the rate-limit stamp.`,
      elevatedProgramLine(engine),
      '',
      REMOVE_SCRIPT,
      '',
      'The Windows Time service\'s start type is NOT changed back — it was your fix. Nothing else on Windows is changed.',
    ].join('\n'),
    confirm: REMOVE_CONFIRM,
  };
}

export interface GuardFlowDeps {
  readonly env: Readonly<Record<string, string | undefined>>;
  readonly options: () => GuardOptions;
  readonly timeoutMs: () => number;
  /** Opens `text` read-only — the elevated script the person is about to confirm, byte for byte. */
  readonly show: (text: string) => Promise<void>;
  readonly confirm: (prompt: FixPrompt) => Promise<boolean>;
  readonly run: Runner;
  readonly report: (message: string, failed: boolean) => void;
  readonly durable: DurableStore;
  readonly nowUtcMs: () => number;
  /** After the run answered: read Task Scheduler again — the truth the panel shows. */
  readonly afterRun: () => void;
  /** Which PowerShell runs elevated — before the modal, and again right before the launch (`powershell/engineHost.ts`). */
  readonly engine: EngineSource;
}

function told(deps: GuardFlowDeps, op: GuardOp, outcome: GuardOutcome, engine: Engine): GuardOutcome {
  deps.report(guardOutcomeSentence(outcome, op, engine), outcome.kind === 'failed' || outcome.kind === 'timedOut' || outcome.kind === 'notStarted' || outcome.kind === 'notRun');
  return outcome;
}

/** The ONE run under its pending record: kept past a TIMEOUT (the elevated child may still run), cleared on every other answer. */
async function pendingRun(deps: GuardFlowDeps, op: GuardOp, request: ProcessRequest, startedAtUtcMs: number): Promise<GuardOutcome> {
  await writePending(deps.durable, { op, startedAtUtcMs, deadlineUtcMs: startedAtUtcMs + 2 * request.timeoutMs });
  let outcome: GuardOutcome = { kind: 'failed', sentence: 'the launcher did not answer' };
  try {
    outcome = guardOutcomeOf(await deps.run(request));
  } finally {
    if (outcome.kind !== 'timedOut') {
      await clearPending(deps.durable);
    }
  }
  deps.afterRun();

  return outcome;
}

/**
 * After the modal, in this order (§11 R5): the engine read AGAIN — a disagreement runs nothing and writes nothing — then the
 * time, then another window's pending run looked for once more (own code review k3), and only then the pending record and
 * the run.
 */
async function launched(deps: GuardFlowDeps, op: GuardOp, request: ProcessRequest, engine: Engine): Promise<GuardOutcome> {
  const problem = engineProblem(engine, await deps.engine.reread());
  if (problem !== undefined) {
    return { kind: 'notRun', sentence: problem };
  }
  const startedAtUtcMs = deps.nowUtcMs();

  return readPending(deps.durable, startedAtUtcMs) === undefined ? pendingRun(deps, op, request, startedAtUtcMs) : { kind: 'busy' };
}

/** What a person must see and confirm before the run: the install's exact script in its own tab, then each one's modal. */
async function confirmed(op: GuardOp, deps: GuardFlowDeps, options: GuardOptions, inner: string, engine: Engine): Promise<boolean> {
  if (op === 'install') {
    await deps.show(inner);
  }

  return deps.confirm(op === 'install' ? installPrompt(options, engine) : removePrompt(engine));
}

/** No engine was read: a run refused before any read (another window's run stands) names none. */
const NO_ENGINE: Engine = { file: '', label: '', kind: 'windows' };

export async function runGuardOp(op: GuardOp, deps: GuardFlowDeps): Promise<GuardOutcome> {
  if (readPending(deps.durable, deps.nowUtcMs()) !== undefined) {
    return told(deps, op, { kind: 'busy' }, NO_ENGINE);
  }
  const options = deps.options();
  const inner = innerOf(op, options);
  const { engine } = await deps.engine.engine();
  const request = elevatedRequest(deps.env, inner, deps.timeoutMs(), elevatedArg(engine));
  if (typeof request === 'string') {
    return told(deps, op, { kind: 'notStarted', reason: request }, engine);
  }

  return (await confirmed(op, deps, options, inner, engine)) ? told(deps, op, await launched(deps, op, request, engine), engine) : { kind: 'declined' };
}

/** The script the ONE elevated PowerShell runs for `op` — and, for an install, the text shown before it. */
function innerOf(op: GuardOp, options: GuardOptions): string {
  return op === 'install' ? installScript(options) : REMOVE_SCRIPT;
}
