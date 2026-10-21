import { firstLine } from '../client/windowsTasks';
import { tagsOf } from '../powershell/taggedAnswer';
import type { StagingFolder } from '../client/windowsTasks';
import type { TaskName } from '../client/verbs';
import { byProgram, withUnelevatedStoreNote, type Engine } from '../powershell/engineTypes';
import type { ProcessResult } from '../process/runner';
import { signed32 } from '../process/runner';
import { OP_EXIT } from '../scheduler/registerScript';
import { RUN_EXIT } from '../scheduler/runScript';
import { TASK_FOLDER } from '../scheduler/taskFolder';
import { noticeText, safeText } from '../text/safeText';
import { treeVerdict } from './tasksView';

/**
 * The closed outcomes of *Install the Windows <task> task* and *Remove the Windows tasks* (WR-E3 of PLAN_windows_runner.md §3.8), and the
 * one sentence each is told in — every sentence through `noticeText`, because it carries words the daemon, Task Scheduler or PowerShell
 * supplied. Pure: the flow decides, this module says.
 */

export type TasksOp = { readonly kind: 'install'; readonly task: TaskName } | { readonly kind: 'run'; readonly task: TaskName } | { readonly kind: 'remove' };

/** What a finished operation did, for its sentence. */
export type DoneSummary =
  | { readonly op: 'install'; readonly task: TaskName; readonly what: string; readonly cadence: string }
  | { readonly op: 'run'; readonly task: TaskName; readonly what: string }
  | {
      readonly op: 'remove';
      /** The folder: deleted, kept because another task is in it (named), or a delete that failed. */
      readonly folder: 'removed' | 'whenEmpty' | 'failed' | { readonly keptBy: readonly string[] };
      readonly copies: number;
      readonly kept: readonly StagingFolder[];
      readonly untouched: readonly string[];
    };

export type TasksOutcome =
  | { readonly kind: 'done'; readonly done: DoneSummary }
  /** The modal was declined; an install names the staged copy it leaves behind. */
  | { readonly kind: 'declined'; readonly stagedPath?: string }
  | { readonly kind: 'busy' }
  /** The task is already running (the read said so, or the script did at run time): nothing was started — information, not an error (R2). */
  | { readonly kind: 'alreadyRunning'; readonly task: TaskName }
  | { readonly kind: 'failed'; readonly sentence: string }
  /** `setting`: the ceiling that ended it, when it is not the PowerShell's own (`wslCare.timeouts.windowsTasksSeconds`). */
  | { readonly kind: 'timedOut'; readonly timeoutMs: number; readonly treeEnded: boolean | undefined; readonly setting?: string }
  /** Nothing ran; `engineStart` when the PowerShell itself could not be started (the Store package's note belongs to that case only). */
  | { readonly kind: 'notStarted'; readonly reason: string; readonly engineStart?: true }
  /** The read right before the launch disagreed with the modal (another program, or no answer): nothing ran. */
  | { readonly kind: 'notRun'; readonly sentence: string };

// ---- the exits of the ONE PowerShell ----

type FailureWords = (engine: Engine) => string;

/** The failing exits both operations share: PowerShell's own 1 and the module that would not load. */
const SHARED_FAILURES: Readonly<Record<number, FailureWords>> = {
  1: () => 'the PowerShell failed before its first step (exit 1)',
  [OP_EXIT.importFailed]: (engine) => `the PowerShell could not load its ScheduledTasks module (${engine.label})`,
};

/** Each failing exit of the script, by the operation that ran it. */
const FAILURES: { readonly [K in TasksOp['kind']]: Readonly<Record<number, FailureWords>> } = {
  install: { ...SHARED_FAILURES, [OP_EXIT.registerFailed]: () => `Task Scheduler did not register the task (creating the \\${TASK_FOLDER} folder or Register-ScheduledTask failed)` },
  remove: { ...SHARED_FAILURES, [OP_EXIT.unregisterFailed]: () => 'Task Scheduler did not delete a task' },
  run: {
    ...SHARED_FAILURES,
    [RUN_EXIT.notRegistered]: () => 'the task is not registered (any more) — install it first',
    [RUN_EXIT.disabled]: () => 'the task is disabled in Task Scheduler — install it again to enable it',
    [RUN_EXIT.runFailed]: () => 'Task Scheduler did not start the task',
  },
};

/** The first line of the PowerShell's stderr, printable and bounded: the unelevated child's stderr IS readable (the elevated road's is not). */
function saidOf(stderr: Buffer): string {
  return safeText(firstLine(stderr), 300);
}

function withSaid(sentence: string, stderr: Buffer): string {
  const said = saidOf(stderr);

  return said === '' ? sentence : `${sentence}: ${said}`;
}

/** What the ONE PowerShell's exit means: done (0), the removal's folder that stayed (32), or a failure with the words of its exit and its stderr. */
export type RunStep = { readonly step: 'ok' } | { readonly step: 'folderStays' } | { readonly step: 'alreadyRunning' } | { readonly step: 'ended'; readonly outcome: TasksOutcome };

/** The exits, besides 0, that are not a failure — each op's own: the removal's folder that could not go while its tasks did, the run's task that was already running. */
const PASSING: { readonly [K in TasksOp['kind']]: Readonly<Record<number, RunStep>> } = {
  install: {},
  run: { [RUN_EXIT.alreadyRunning]: { step: 'alreadyRunning' } },
  remove: { [OP_EXIT.cleanupFailed]: { step: 'folderStays' } },
};

function passed(op: TasksOp, code: number): RunStep | undefined {
  return code === 0 ? { step: 'ok' } : PASSING[op.kind][code];
}

/** Task Scheduler's own HRESULT, when the script printed one (`hresult=0x…`): the run-now script does, on every refusal it can name. */
function hresultWords(stdout: Buffer): string {
  const hresult = tagsOf(stdout.toString('utf8')).tags.get('hresult');

  return hresult === undefined || hresult === '' ? '' : ` (${safeText(hresult, 40)})`;
}

function exitedStep(op: TasksOp, engine: Engine, code: number, output: { readonly stdout: Buffer; readonly stderr: Buffer }): RunStep {
  const words = (FAILURES[op.kind][code]?.(engine) ?? `the PowerShell exited ${code}`) + hresultWords(output.stdout);

  return passed(op, code) ?? { step: 'ended', outcome: { kind: 'failed', sentence: withSaid(words, output.stderr) } };
}

const ENDED: { readonly [K in Exclude<ProcessResult['kind'], 'exited'>]: (r: Extract<ProcessResult, { kind: K }>) => TasksOutcome } = {
  timedOut: (r) => ({ kind: 'timedOut', timeoutMs: r.timeoutMs, treeEnded: r.treeEnded }),
  failedToStart: (r) => ({ kind: 'notStarted', reason: r.reason, engineStart: true }),
  signalled: (r) => ({ kind: 'failed', sentence: `the PowerShell was stopped (${r.signal})` }),
  tooMuchOutput: () => ({ kind: 'failed', sentence: 'the PowerShell printed more than the extension reads' }),
};

/** The ONE PowerShell's ending as the next step of the flow. */
export function stepOf(result: ProcessResult, op: TasksOp, engine: Engine): RunStep {
  if (result.kind === 'exited') {
    return exitedStep(op, engine, signed32(result.code), result);
  }

  return { step: 'ended', outcome: (ENDED[result.kind] as (r: ProcessResult) => TasksOutcome)(result) };
}

// ---- the sentences ----

const SUBJECT: { readonly [K in TasksOp['kind']]: (op: Extract<TasksOp, { kind: K }>) => string } = {
  install: (op) => `The install of the Windows ${op.task} task`,
  run: (op) => `The start of the Windows ${op.task} task`,
  remove: () => 'The removal of the Windows tasks',
};

function subject(op: TasksOp): string {
  return (SUBJECT[op.kind] as (o: TasksOp) => string)(op);
}

function installedSentence(done: Extract<DoneSummary, { op: 'install' }>, engine: Engine): string {
  return `The Windows ${done.task} task is installed: Task Scheduler runs ${done.what} every ${done.cadence} while you are logged on.${byProgram(engine)}`;
}

function startedSentence(done: Extract<DoneSummary, { op: 'run' }>, engine: Engine): string {
  return `The Windows ${done.task} task was started: Task Scheduler runs ${done.what} now, as you, not elevated. The panel shows it running until it ends.${byProgram(engine)}`;
}

function folderWords(folder: Extract<DoneSummary, { op: 'remove' }>['folder']): string {
  if (folder === 'removed') {
    return 'the folder';
  }
  if (folder === 'whenEmpty') {
    return 'the folder (when it was empty)';
  }

  return folder === 'failed' ? 'the folder could not be removed' : `the folder stays: another task is in it (${folder.keptBy.join(', ')}; the Windows Time guard's task keeps it)`;
}

function copiesWords(count: number): string {
  return `${count} staged ${count === 1 ? 'copy' : 'copies'}`;
}

function keptWords(kept: readonly StagingFolder[]): string {
  return kept.length === 0 ? '' : ` Kept (an exe still executing): ${kept.map((k) => `${k.path} — ${k.why}`).join('; ')}; the next install or removal retries it.`;
}

function untouchedWords(untouched: readonly string[]): string {
  return untouched.length === 0 ? '' : ` Left alone (not a staged copy): ${untouched.join(', ')}.`;
}

function removedSentence(done: Extract<DoneSummary, { op: 'remove' }>, engine: Engine): string {
  const head = `The Windows tasks are removed — \\${TASK_FOLDER}\\watch, \\${TASK_FOLDER}\\archive, ${folderWords(done.folder)}, and ${copiesWords(done.copies)}.${byProgram(engine)}`;

  return head + keptWords(done.kept) + untouchedWords(done.untouched);
}

type Sentence<K extends TasksOutcome['kind']> = (o: Extract<TasksOutcome, { kind: K }>, op: TasksOp, engine: Engine) => string;

const DONE: { readonly [K in DoneSummary['op']]: (done: Extract<DoneSummary, { op: K }>, engine: Engine) => string } = {
  install: installedSentence,
  run: startedSentence,
  remove: removedSentence,
};

const SENTENCE: { readonly [K in TasksOutcome['kind']]: Sentence<K> } = {
  done: (o, _op, engine) => (DONE[o.done.op] as (d: DoneSummary, e: Engine) => string)(o.done, engine),
  alreadyRunning: (o) => `The Windows ${o.task} task is already running (Task Scheduler reports it); nothing new was started — a second instance would be dropped by the task itself (IgnoreNew).`,
  declined: (o) => (o.stagedPath === undefined ? '' : `Nothing was registered. The staged copy stays at ${o.stagedPath}; *Remove the Windows tasks* deletes it.`),
  busy: () => 'An install, removal or start of the Windows tasks is still waiting (its PowerShell, or its stage) — nothing new was started.',
  failed: (o, op, engine) => `${subject(op)} failed: ${o.sentence}.${byProgram(engine)}`,
  timedOut: (o, op, engine) => `${subject(op)} did not finish within ${Math.round(o.timeoutMs / 1000)} s (${o.setting ?? 'wslCare.timeouts.windowsTasksSeconds'}); ${treeVerdict(o.treeEnded)}. The panel shows what Task Scheduler holds.${byProgram(engine)}`,
  notStarted: (o, op, engine) => `${subject(op)} could not start: ${o.engineStart === true ? withUnelevatedStoreNote(o.reason, engine) : o.reason}.`,
  notRun: (o) => o.sentence,
};

/** The one sentence an outcome is told in (`''` for a removal that was declined: nothing is said). */
export function tasksOutcomeSentence(outcome: TasksOutcome, op: TasksOp, engine: Engine): string {
  return noticeText((SENTENCE[outcome.kind] as Sentence<TasksOutcome['kind']>)(outcome, op, engine));
}

/** Whether an outcome is reported as a failure (the error notification) rather than as information. */
export function isFailure(outcome: TasksOutcome): boolean {
  return outcome.kind === 'failed' || outcome.kind === 'timedOut' || outcome.kind === 'notStarted' || outcome.kind === 'notRun';
}
