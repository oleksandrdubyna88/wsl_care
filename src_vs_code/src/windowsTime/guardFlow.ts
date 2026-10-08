import type { DurableStore } from '../cleanup/journal';
import type { ProcessResult, Runner } from '../process/runner';
import { signed32 } from '../process/runner';
import { noticeText } from '../text/safeText';
import { clearPending, readPending, writePending } from './guardPending';
import { installScript, OP_EXIT, REMOVE_SCRIPT } from './guardScripts';
import { GUARD_FOLDER, GUARD_NAME, guardScript, type GuardOptions } from './guardTask';
import { elevatedRequest, EXIT, FAILURES, type FixPrompt } from './windowsTimeFix';

/**
 * *Install the Windows Time guard* and *Remove the Windows Time guard* (PLAN_windows_time_task.md D3, D4, D8) — in the order
 * that makes them safe: the request is built (or the reason none can be is told); for an install the EXACT elevated script —
 * the task's XML inside it — is opened read-only first; a modal names what will run; ONLY its confirm persists the pending
 * run and starts the ONE elevated PowerShell; its closed outcome is told; the status is read again. Nothing runs on a
 * decline, and nothing starts while another window's elevated run still stands (`guardPending.ts`).
 */

export type GuardOp = 'install' | 'remove';

export type GuardOutcome =
  | { readonly kind: 'done' }
  | { readonly kind: 'declined' }
  | { readonly kind: 'busy' }
  | { readonly kind: 'failed'; readonly sentence: string }
  | { readonly kind: 'timedOut'; readonly timeoutMs: number }
  | { readonly kind: 'notStarted'; readonly reason: string };

/** Each failing exit of the elevated run, by the operation that ran (D3, D8 o8b). */
const OP_FAILURES: Readonly<Record<number, string>> = {
  1: 'the elevated PowerShell failed before its first step (exit 1)',
  [EXIT.launchFailed]: FAILURES[EXIT.launchFailed] ?? '',
  [OP_EXIT.registerFailed]: 'Task Scheduler did not register the task (creating the \\wsl-care folder or Register-ScheduledTask failed)',
  [OP_EXIT.unregisterFailed]: 'Task Scheduler did not delete the task',
  [OP_EXIT.cleanupFailed]: 'the task is gone, but its empty \\wsl-care folder or its stamp under HKLM\\SOFTWARE\\wsl-care could not be removed',
};

function exitedOutcome(code: number): GuardOutcome {
  if (code === EXIT.done) {
    return { kind: 'done' };
  }

  return code === EXIT.declined ? { kind: 'declined' } : { kind: 'failed', sentence: OP_FAILURES[code] ?? `the elevated PowerShell exited ${code}` };
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

const SENTENCE: { readonly [K in GuardOutcome['kind']]: (o: Extract<GuardOutcome, { kind: K }>, op: GuardOp) => string } = {
  done: (_o, op) => (op === 'install' ? 'The Windows Time guard is installed: Task Scheduler now starts the Windows Time service and resyncs by itself.' : 'The Windows Time guard is removed — its task, its folder and its stamp.'),
  declined: () => 'Nothing was changed: the UAC prompt was declined.',
  busy: () => 'An elevated install or removal of the Windows Time guard is still waiting (its UAC prompt, or the run itself) — nothing new was started.',
  failed: (o, op) => `The Windows Time guard's ${NAMES[op]} failed: ${o.sentence}.`,
  timedOut: (o, op) => `The Windows Time guard's ${NAMES[op]} did not finish within ${Math.round(o.timeoutMs / 1000)} s (wslCare.timeouts.windowsTimeGuardSeconds) — if the UAC prompt is still open, answering it still runs it; the panel shows what Task Scheduler holds.`,
  notStarted: (o, op) => `The Windows Time guard's ${NAMES[op]} could not start: ${o.reason}.`,
};

export function guardOutcomeSentence(outcome: GuardOutcome, op: GuardOp): string {
  return noticeText((SENTENCE[outcome.kind] as (o: GuardOutcome, op: GuardOp) => string)(outcome, op));
}

export const INSTALL_CONFIRM = 'Install it (one UAC prompt follows)';
export const REMOVE_CONFIRM = 'Remove it (one UAC prompt follows)';

export function installPrompt(options: GuardOptions): FixPrompt {
  const delay = options.delaySeconds > 0 ? ` (each ${options.delaySeconds} s after the event)` : '';
  return {
    message: 'Install the Windows Time guard — a scheduled task that runs as SYSTEM?',
    detail: [
      `One Windows PowerShell runs ELEVATED — Windows asks you first (UAC) — and registers ONE scheduled task, \\${GUARD_FOLDER}\\${GUARD_NAME}, exactly as the read-only editor tab beside this dialog shows.`,
      '',
      `The task runs as SYSTEM at startup, at logon, when the Windows Time service logs that it is stopping, when its start type is changed${delay}, and every ${options.everyHours} h. Each run does only this:`,
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

export function removePrompt(): FixPrompt {
  return {
    message: 'Remove the Windows Time guard?',
    detail: [
      `One Windows PowerShell runs ELEVATED — Windows asks you first (UAC) — and runs exactly this: it deletes the task \\${GUARD_FOLDER}\\${GUARD_NAME}, the folder when nothing else is in it, and the rate-limit stamp.`,
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
}

function told(deps: GuardFlowDeps, op: GuardOp, outcome: GuardOutcome): GuardOutcome {
  deps.report(guardOutcomeSentence(outcome, op), outcome.kind === 'failed' || outcome.kind === 'timedOut' || outcome.kind === 'notStarted');
  return outcome;
}

/** The pending run is kept past a TIMEOUT (the elevated child may still run), and cleared on every other answer. */
async function launched(deps: GuardFlowDeps, op: GuardOp, request: Exclude<ReturnType<typeof elevatedRequest>, string>): Promise<GuardOutcome> {
  const startedAtUtcMs = deps.nowUtcMs();
  // Again, AFTER the modal: another window may have confirmed its own run while this modal was open (own code review k3).
  if (readPending(deps.durable, startedAtUtcMs) !== undefined) {
    return { kind: 'busy' };
  }
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

/** What a person must see and confirm before the run: the install's exact script in its own tab, then each one's modal. */
async function confirmed(op: GuardOp, deps: GuardFlowDeps, options: GuardOptions, inner: string): Promise<boolean> {
  if (op === 'install') {
    await deps.show(inner);
  }

  return deps.confirm(op === 'install' ? installPrompt(options) : removePrompt());
}

export async function runGuardOp(op: GuardOp, deps: GuardFlowDeps): Promise<GuardOutcome> {
  if (readPending(deps.durable, deps.nowUtcMs()) !== undefined) {
    return told(deps, op, { kind: 'busy' });
  }
  const options = deps.options();
  const inner = innerOf(op, options);
  const request = elevatedRequest(deps.env, inner, deps.timeoutMs());
  if (typeof request === 'string') {
    return told(deps, op, { kind: 'notStarted', reason: request });
  }

  return (await confirmed(op, deps, options, inner)) ? told(deps, op, await launched(deps, op, request)) : { kind: 'declined' };
}

/** The script the ONE elevated PowerShell runs for `op` — and, for an install, the text shown before it. */
function innerOf(op: GuardOp, options: GuardOptions): string {
  return op === 'install' ? installScript(options) : REMOVE_SCRIPT;
}
