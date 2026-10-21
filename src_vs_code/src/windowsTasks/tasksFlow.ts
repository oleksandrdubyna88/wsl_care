import type { DurableStore } from '../cleanup/journal';
import { TASK_NAMES, type TaskName } from '../client/verbs';
import { staged, type WindowsCareClient } from '../client/WindowsCareClient';
import type { RemoveResult, RenderAnswer, StageResult } from '../client/windowsTasks';
import { engineProblem, type EngineSource } from '../powershell/engineSource';
import { NO_ENGINE, type Engine } from '../powershell/engineTypes';
import { unelevatedRequest } from '../powershell/unelevatedLauncher';
import type { ProcessRequest, ProcessResult } from '../process/runner';
import { principalLine } from '../scheduler/principalPolicy';
import { taskOf, type SchedulerRead, type TaskRead } from '../scheduler/schedulerAnswer';
import { isFailure, stepOf, tasksOutcomeSentence, type DoneSummary, type RunStep, type TasksOp, type TasksOutcome } from './tasksOutcome';
import { clearPending, readPending, writePending, type PendingTasksOp } from './tasksPending';
import { cadenceOf, installPrompt, removePrompt, runPrompt, whatOf, type TasksPrompt } from './tasksPrompts';
import { registrationProblem } from './tasksGuards';
import { installTaskScript, removeTasksScript, runNowScript } from './tasksScripts';

export { installPrompt, removePrompt, runPrompt, INSTALL_CONFIRM, REMOVE_CONFIRM, RUN_CONFIRM, type TasksPrompt } from './tasksPrompts';
export { tasksOutcomeSentence, type TasksOp, type TasksOutcome } from './tasksOutcome';

/**
 * *Install the Windows watch / archive task* and *Remove the Windows tasks* (WR-E3 of PLAN_windows_runner.md §3.4, §3.8) — in the order
 * that makes them safe, the guard's (`windowsTime/guardFlow.ts`) with the stage and the render before the engine:
 *
 * 1. another window's pending run is looked for — it holds everything;
 * 2. an install STAGES the bundled exe (always — the daemon's own `reused` says it was not needed; a busy run lock is retried ONCE after
 *    a setting's wait) and asks the daemon for a FRESH render of the task from that copy;
 * 3. the render is checked field by field (`tasksGuards.ts`): the reader's own account, an interactive token at the least privilege, the
 *    staged copy's absolute path, nothing of SYSTEM, a service, a group or the extension folder — a problem registers nothing;
 * 4. the engine is read; the exact script — the XML inside it — is opened read-only, then the modal asks;
 * 5. after the confirm the engine is read AGAIN (a disagreement runs nothing), the time is taken, another window's pending run is
 *    looked for once more, and only then is the pending run persisted and the ONE unelevated PowerShell started;
 * 6. its closed outcome is told (a removal then asks the daemon to remove its staged copies, only after exit 0 or 32); Task Scheduler
 *    and the copy are read again.
 *
 * Nothing runs on a decline, a busy, a refused render or an engine that disagrees; the pending record is cleared on EVERY answer but a
 * timeout. It never builds an argv: the client's closed tails and the launcher's fixed switches are the only ones.
 */

/** The surfaces the flow uses — real, or recorded in Test mode (`tasksUi.ts`). */
export interface TasksUi {
  /** Opens `text` read-only — the script the person is about to confirm, byte for byte; `task` names the tab. */
  readonly show: (text: string, task: TaskName) => Promise<void>;
  readonly confirm: (prompt: TasksPrompt) => Promise<boolean>;
  /** The ONE unelevated PowerShell, under a progress notification titled `title`. */
  readonly run: (request: ProcessRequest, title: string) => Promise<ProcessResult>;
  readonly report: (message: string, failed: boolean) => void;
}

export interface TasksFlowDeps {
  readonly client: Pick<WindowsCareClient, 'tasksStage' | 'tasksRender' | 'tasksStageRemove' | 'exe'>;
  /** The ONE Task Scheduler read's newest answer: the reader's own SID, and what else is in the folder. */
  readonly read: () => SchedulerRead;
  readonly engine: EngineSource;
  readonly ui: TasksUi;
  readonly durable: DurableStore;
  readonly nowUtcMs: () => number;
  /** The wait before the ONE retry of a busy stage or removal — a seam, so a test waits for nothing. */
  readonly sleep: (ms: number) => Promise<void>;
  /** `wslCare.timeouts.windowsTasksSeconds`, read at every call. */
  readonly timeoutMs: () => number;
  /** `wslCare.timeouts.windowsTasksStageSeconds` (the client's own ceiling) — only to size a removal's pending deadline. */
  readonly stageTimeoutMs: () => number;
  /** `wslCare.windowsTasks.busyRetrySeconds`. */
  readonly busyRetryMs: () => number;
  /** After the run answered: read Task Scheduler and the staged copy again — the truth the panel shows. */
  readonly afterRun: () => void;
  /** The folder the extension runs from — no task may name it. */
  readonly extensionFolder: string;
  /** The extension's log: a fault at an edge that must not stop the outcome from being told. */
  readonly log: (line: string) => void;
}

const BUSY: TasksOutcome = { kind: 'busy' };

function notStarted(reason: string): TasksOutcome {
  return { kind: 'notStarted', reason };
}

function messageOf(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

/** The one outcome of a call that told the person: its sentence reported (a removal that was declined says nothing), then the outcome. */
function told(deps: TasksFlowDeps, op: TasksOp, outcome: TasksOutcome, engine: Engine): TasksOutcome {
  const sentence = tasksOutcomeSentence(outcome, op, engine);
  if (sentence !== '') {
    deps.ui.report(sentence, isFailure(outcome));
  }

  return outcome;
}

/** A call that answered `busy` once is made ONCE more after the wait — never looped; the second answer stands. */
async function retriedOnce<T extends { readonly kind: string }>(call: () => Promise<T>, deps: TasksFlowDeps): Promise<T> {
  const first = await call();
  if (first.kind !== 'busy') {
    return first;
  }
  await deps.sleep(deps.busyRetryMs());

  return call();
}

// ---- the stage and the render ----

interface StagedCopy {
  readonly digest: string;
  /** The staged exe the client names for the digest. */
  readonly file: string;
}

function isOutcome(value: object): value is TasksOutcome {
  return 'kind' in value;
}

function stageWords(result: StageResult): string {
  if (result.kind === 'busy') {
    return `the daemon's run lock was held twice (${result.reason}); try again in a minute — a watch run holds it for seconds`;
  }

  return 'reason' in result ? result.reason : 'the daemon staged no copy';
}

/** `tasks stage`, always: the daemon decides it is not needed (`reused`). The copy's exe is named by the client for the digest the daemon reported. */
async function stagedCopy(deps: TasksFlowDeps): Promise<StagedCopy | TasksOutcome> {
  const result = await retriedOnce(() => deps.client.tasksStage(), deps);
  if (result.kind !== 'staged' && result.kind !== 'reused') {
    return notStarted(stageWords(result));
  }
  const file = deps.client.exe(staged(result.report.digest));

  return typeof file === 'string' ? { digest: result.report.digest, file } : notStarted(file.reason);
}

/** A FRESH render of the task from the staged copy — never the probe's cached one: the modal shows what will be registered NOW. */
async function freshRender(task: TaskName, copy: StagedCopy, deps: TasksFlowDeps): Promise<RenderAnswer | TasksOutcome> {
  const render = await deps.client.tasksRender(task, copy.digest);
  switch (render.kind) {
    case 'registrable':
      return render.answer;
    case 'notRegistrable':
      return notStarted(`the daemon will not let a task name this copy: ${render.reason}`);
    default:
      return notStarted(render.reason);
  }
}

// ---- the ONE run, under its pending record ----

/** The run's outcome; a step that THROWS is a failed outcome, so it is told and its record cleared like any other answer. */
async function guarded(body: () => Promise<TasksOutcome>): Promise<TasksOutcome> {
  try {
    return await body();
  } catch (e: unknown) {
    return { kind: 'failed', sentence: `the run threw: ${messageOf(e)}` };
  }
}

/**
 * The ONE run under its pending record: kept past a TIMEOUT (the PowerShell the extension ended may have left a registration half done),
 * cleared on EVERY other answer — a failure, a crash, a thrown step included. Then the status is read again.
 */
async function underRecord(deps: TasksFlowDeps, record: PendingTasksOp, startedAtUtcMs: number, spanMs: number, body: () => Promise<TasksOutcome>): Promise<TasksOutcome> {
  await writePending(deps.durable, { ...record, startedAtUtcMs, deadlineUtcMs: startedAtUtcMs + spanMs });
  const outcome = await guarded(body);
  if (outcome.kind !== 'timedOut') {
    await clearedOrLogged(deps);
  }
  deps.afterRun();

  return outcome;
}

/** The record cleared — or, when the store refuses, the fault logged: the outcome is still told, and the sweep (or the deadline) ends the record later. */
async function clearedOrLogged(deps: TasksFlowDeps): Promise<void> {
  try {
    await clearPending(deps.durable);
  } catch (e: unknown) {
    deps.log(`Windows tasks: the pending record could not be cleared: ${messageOf(e)}`);
  }
}

/** The engine read AGAIN, the time, another window's pending run looked for once more — only then the record and the run. */
async function launched(deps: TasksFlowDeps, engine: Engine, record: PendingTasksOp, spanMs: number, body: () => Promise<TasksOutcome>): Promise<TasksOutcome> {
  const problem = engineProblem(engine, await deps.engine.reread());
  if (problem !== undefined) {
    return { kind: 'notRun', sentence: problem };
  }
  const startedAtUtcMs = deps.nowUtcMs();

  return readPending(deps.durable, startedAtUtcMs) === undefined ? underRecord(deps, record, startedAtUtcMs, spanMs, body) : BUSY;
}

/** The ONE PowerShell's ending; a runner that throws is a failed step, never an unreported one. */
async function psStep(deps: TasksFlowDeps, op: TasksOp, engine: Engine, request: ProcessRequest, title: string): Promise<RunStep> {
  try {
    return stepOf(await deps.ui.run(request, title), op, engine);
  } catch (e: unknown) {
    return { step: 'ended', outcome: { kind: 'failed', sentence: `the PowerShell could not be run: ${messageOf(e)}` } };
  }
}

// ---- install ----

async function installRun(deps: TasksFlowDeps, op: Extract<TasksOp, { kind: 'install' }>, engine: Engine, request: ProcessRequest, answer: RenderAnswer): Promise<TasksOutcome> {
  const step = await psStep(deps, op, engine, request, `Registering the Windows ${op.task} task…`);
  const done: DoneSummary = { op: 'install', task: op.task, what: whatOf(op.task), cadence: cadenceOf(op.task, answer) };

  return step.step === 'ended' ? step.outcome : { kind: 'done', done };
}

/** The request and the script for `answer`, or why none: the engine's program, its quotable path, the command line's length. */
function installRequest(task: TaskName, answer: RenderAnswer, engine: Engine, deps: TasksFlowDeps): { readonly script: string; readonly request: ProcessRequest } | string {
  const script = installTaskScript(task, answer.xml);
  const request = unelevatedRequest(engine, script, deps.timeoutMs());

  return typeof request === 'string' ? request : { script, request };
}

async function installConfirmed(deps: TasksFlowDeps, task: TaskName, copy: StagedCopy, answer: RenderAnswer, engine: Engine, built: { readonly script: string; readonly request: ProcessRequest }): Promise<TasksOutcome> {
  await deps.ui.show(built.script, task);
  if (!(await deps.ui.confirm(installPrompt(task, answer, engine)))) {
    return told(deps, { kind: 'install', task }, { kind: 'declined', stagedPath: copy.file }, engine);
  }
  const op: TasksOp = { kind: 'install', task };

  return told(deps, op, await launched(deps, engine, { op: 'install', task }, 2 * built.request.timeoutMs, () => installRun(deps, op, engine, built.request, answer)), engine);
}

interface Prepared {
  readonly copy: StagedCopy;
  readonly answer: RenderAnswer;
}

/** The stage, the fresh render and the check of it — or the outcome that stopped it. Nothing is shown or asked before all three passed. */
async function prepared(task: TaskName, deps: TasksFlowDeps): Promise<Prepared | TasksOutcome> {
  const copy = await stagedCopy(deps);
  if (isOutcome(copy)) {
    return copy;
  }
  const answer = await freshRender(task, copy, deps);
  if (isOutcome(answer)) {
    return answer;
  }
  const problem = registrationProblem(answer, task, deps.read(), copy.file, deps.extensionFolder);

  return problem === '' ? { copy, answer } : notStarted(problem);
}

async function install(task: TaskName, deps: TasksFlowDeps): Promise<TasksOutcome> {
  const op: TasksOp = { kind: 'install', task };
  const ready = await prepared(task, deps);
  if (isOutcome(ready)) {
    return told(deps, op, ready, NO_ENGINE);
  }
  const { engine } = await deps.engine.engine();
  const built = installRequest(task, ready.answer, engine, deps);

  return typeof built === 'string' ? told(deps, op, notStarted(built), engine) : installConfirmed(deps, task, ready.copy, ready.answer, engine, built);
}

// ---- remove ----

/** The tasks in the folder that are not the two Windows tasks — whoever else keeps the folder (the guard's task, when installed). */
function othersIn(read: SchedulerRead): string[] {
  return read.kind === 'read' ? [...read.sections.keys()].filter((name) => !(TASK_NAMES as readonly string[]).includes(name)) : [];
}

type FolderGuess = Extract<DoneSummary, { op: 'remove' }>['folder'];

/** What the read taken at the confirm says of the folder's fate: another task keeps it, it is empty — or the read did not answer, so it is only "when it was empty". */
function folderOf(read: SchedulerRead, keepers: readonly string[]): FolderGuess {
  if (read.kind !== 'read') {
    return 'whenEmpty';
  }

  return keepers.length > 0 ? { keptBy: keepers } : 'removed';
}

function removalWords(result: Extract<RemoveResult, { kind: 'busy' | 'wrongSide' | 'failed' | 'unavailable' }>): string {
  return result.kind === 'busy' ? `the daemon's run lock was held twice (${result.reason}) — press Remove again` : result.reason;
}

/** The staged copies' removal that did not finish: TYPED, so the pending record is kept to its deadline like the PowerShell's own timeout. */
function removalNotDone(result: Exclude<RemoveResult, { kind: 'removed' }>): TasksOutcome {
  return result.kind === 'timedOut'
    ? { kind: 'timedOut', timeoutMs: result.timeoutMs, treeEnded: result.treeEnded, setting: 'wslCare.timeouts.windowsTasksStageSeconds' }
    : { kind: 'failed', sentence: `the tasks are removed, but the staged copies were not: ${removalWords(result)}` };
}

/** The staged copies, after the tasks are gone: removed, or why not — the tasks stay removed either way. */
async function copiesRemoved(deps: TasksFlowDeps, folder: Extract<DoneSummary, { op: 'remove' }>['folder']): Promise<TasksOutcome> {
  const result = await retriedOnce(() => deps.client.tasksStageRemove(), deps);
  if (result.kind !== 'removed') {
    return removalNotDone(result);
  }
  const { removed, kept, untouched } = result.report;

  return { kind: 'done', done: { op: 'remove', folder, copies: removed.length, kept, untouched } };
}

/** The tasks' removal, then — only after exit 0 or 32 — the staged copies'. Exit 31 or any failure stops here: a registered task keeps its copy. */
async function removeRun(deps: TasksFlowDeps, engine: Engine, request: ProcessRequest, folder: FolderGuess): Promise<TasksOutcome> {
  const step = await psStep(deps, { kind: 'remove' }, engine, request, 'Removing the Windows tasks…');
  if (step.step === 'ended') {
    return step.outcome;
  }

  return copiesRemoved(deps, step.step === 'folderStays' ? 'failed' : folder);
}

async function remove(deps: TasksFlowDeps): Promise<TasksOutcome> {
  const op: TasksOp = { kind: 'remove' };
  const { engine } = await deps.engine.engine();
  const script = removeTasksScript();
  const request = unelevatedRequest(engine, script, deps.timeoutMs());
  if (typeof request === 'string') {
    return told(deps, op, notStarted(request), engine);
  }
  if (!(await deps.ui.confirm(removePrompt(script, engine)))) {
    return told(deps, op, { kind: 'declined' }, engine);
  }
  const read = deps.read();
  const keepers = othersIn(read);
  const span = 2 * (deps.timeoutMs() + 2 * deps.stageTimeoutMs() + deps.busyRetryMs());

  return told(deps, op, await launched(deps, engine, { op: 'remove' }, span, () => removeRun(deps, engine, request, folderOf(read, keepers))), engine);
}

// ---- run now ----

/**
 * The principal line a task of THIS extension has: the reader's own SID, an interactive token (`logon=3`), the least privilege (`level=0`) — the numbers
 * of the canonical summary (`SUMMARY_FUNCTION`, pinned by `summaryOfXml.ts`). A registered task whose line is anything else was registered elsewhere (an
 * elevated run level, another account, a stored password): a start now would run IT, while the modal says "your account, interactive, the least
 * privilege" — so it starts nothing, the install's rule ("a problem starts nothing") applied to a task that already exists.
 */
/** The refusal for a registered task that does not run as the reader (naming the principal line it has), or none. */
function principalRefusal(row: Extract<TaskRead, { kind: 'present' }>, user: string): TasksOutcome | undefined {
  const line = row.summary[0];

  return line === principalLine(user) ? undefined : notStarted(`the registered task does not run as you at the least privilege (${line ?? 'no principal line'}) — install it again`);
}

function presentBlock(task: TaskName, row: Extract<TaskRead, { kind: 'present' }>, user: string): TasksOutcome | undefined {
  if (!row.enabled) {
    return notStarted('the task is disabled in Task Scheduler — install it again to enable it');
  }
  const foreign = principalRefusal(row, user);
  if (foreign !== undefined) {
    return foreign;
  }

  return row.running ? { kind: 'alreadyRunning', task } : undefined;
}

const ROW_BLOCKS: { readonly [K in TaskRead['kind']]: (task: TaskName, row: Extract<TaskRead, { kind: K }>, user: string) => TasksOutcome | undefined } = {
  absent: (task) => notStarted(`install the Windows ${task} task first: it is not registered`),
  unreadable: (_task, row) => notStarted(row.reason),
  present: presentBlock,
};

/** The read, asked BEFORE anything is started (R2): not answered, not installed, unreadable, disabled — and already running, which starts nothing and is told as information. */
function runBlock(task: TaskName, read: SchedulerRead): TasksOutcome | undefined {
  if (read.kind !== 'read') {
    return notStarted('Task Scheduler has not answered what is registered; refresh the panel and try again');
  }
  const row = taskOf(read, task);

  return (ROW_BLOCKS[row.kind] as (t: TaskName, r: TaskRead, u: string) => TasksOutcome | undefined)(task, row, read.user);
}

/** The ONE PowerShell that asks Task Scheduler to start the task: exit 0 is started, 35 is "it was running" (nothing started), every other ending as the install's. */
async function runBody(deps: TasksFlowDeps, op: Extract<TasksOp, { kind: 'run' }>, engine: Engine, request: ProcessRequest): Promise<TasksOutcome> {
  const step = await psStep(deps, op, engine, request, `Starting the Windows ${op.task} task…`);
  if (step.step === 'ended') {
    return step.outcome;
  }

  return step.step === 'alreadyRunning' ? { kind: 'alreadyRunning', task: op.task } : { kind: 'done', done: { op: 'run', task: op.task, what: whatOf(op.task) } };
}

/**
 * *Run the Windows watch now* / *Archive Windows sessions now*: the install flow with the stage, the render and the guards taken out and ONE read put in
 * — no daemon call at all (the task's own `Run` starts the registered copy). The read first; the engine; ONE modal with the exact script; then the
 * install's own launch (the engine again, the record BEFORE the spawn, cleared on every answer but a timeout, the read again).
 */
async function runNow(task: TaskName, deps: TasksFlowDeps): Promise<TasksOutcome> {
  const op: Extract<TasksOp, { kind: 'run' }> = { kind: 'run', task };
  const blocked = runBlock(task, deps.read());
  if (blocked !== undefined) {
    return told(deps, op, blocked, NO_ENGINE);
  }
  const { engine } = await deps.engine.engine();
  const script = runNowScript(task);
  const request = unelevatedRequest(engine, script, deps.timeoutMs());
  if (typeof request === 'string') {
    return told(deps, op, notStarted(request), engine);
  }
  if (!(await deps.ui.confirm(runPrompt(task, script, engine)))) {
    return told(deps, op, { kind: 'declined' }, engine);
  }

  return told(deps, op, await launched(deps, engine, { op: 'run', task }, 2 * request.timeoutMs, () => runBody(deps, op, engine, request)), engine);
}

/**
 * One operation, end to end: the outcome, told once (the declined install's single notice included). `undefined` is never returned here —
 * the host's "one at a time in this window" answers that case before it gets this far.
 */
export async function runTasksOp(op: TasksOp, deps: TasksFlowDeps): Promise<TasksOutcome> {
  if (readPending(deps.durable, deps.nowUtcMs()) !== undefined) {
    return told(deps, op, BUSY, NO_ENGINE);
  }

  switch (op.kind) {
    case 'install':
      return install(op.task, deps);
    case 'run':
      return runNow(op.task, deps);
    default:
      return remove(deps);
  }
}
