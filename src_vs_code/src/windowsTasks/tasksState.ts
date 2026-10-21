import { TASK_NAMES, type TaskName } from '../client/verbs';
import type { RenderAnswer, RenderResult } from '../client/windowsTasks';
import { summariesMatch } from '../scheduler/taskSummary';
import type { PendingTasksOp } from './tasksPending';
import type { PendingTimes } from '../state/pendingRun';
import { SCHEDULER_RESULT, taskOf, type LastRun, type SchedulerRead, type TaskRead } from '../scheduler/schedulerAnswer';

/**
 * The Windows tasks' part of the panel, as ONE pure state (WR-E2 of PLAN_windows_runner.md §3.8): what Task Scheduler's ONE read
 * says about each of the two tasks (`schedulerAnswer.ts`), what the daemon's staged copy says about what this version would
 * install (`tasks render <task>`, run on the staged copy by `tasksHost.ts`), and the copy's own state — joined into the plan's D5
 * rows. The truth is Task Scheduler's and the daemon's, re-read on every panel open and refresh; nothing here is a flag a window
 * holds (`common.durable-status`). Nothing is spelt for a person here: `tasksView.ts` does that.
 *
 * Precedence of a registered task's row: unreadable (its own read failed — its neighbours are untouched), disabled, running, then
 * its comparison with the staged copy ("install again to update"), then its last run.
 */

/** The staged copy of the bundled exe, as the panel's last probe found it. */
export type CopyState =
  /** Not Windows (or the extension is not on a local disk): the part says nothing at all. */
  | { readonly kind: 'unavailable' }
  /** Not probed yet. */
  | { readonly kind: 'checking' }
  /** The bundled exe's digest could not be computed, so no staged copy can even be named. */
  | { readonly kind: 'digestUnreadable'; readonly reason: string }
  /** `tasks render` on the staged copy found no such file: nothing is staged for this version. */
  | { readonly kind: 'notStaged'; readonly digest: string }
  /** The copy is there (or may be) but did not answer `tasks render` — a timeout, a blocked start, a refusal — with the reason. */
  | { readonly kind: 'unreadable'; readonly digest: string; readonly reason: string }
  /** `tasks render` answered on the staged copy: its answer per task asked (the second is not asked when the first found no copy). */
  | { readonly kind: 'staged'; readonly digest: string; readonly renders: Readonly<Partial<Record<TaskName, RenderResult>>> };

export const NOT_PROBED: CopyState = { kind: 'checking' };

export type TaskRow =
  | { readonly kind: 'checking' }
  /** The read gave no answer at all: why, and — when it was a timeout — the ceiling and whether the runner VERIFIED the tree ended. */
  | { readonly kind: 'unknown'; readonly reason: string; readonly timeoutMs?: number; readonly treeEnded?: boolean }
  | { readonly kind: 'notInstalled' }
  | { readonly kind: 'unreadable'; readonly reason: string }
  | { readonly kind: 'disabled' }
  /** Running; `byCode`: Task Scheduler's own "running" result (`0x41301`) is what its last result says. */
  | { readonly kind: 'running'; readonly byCode: boolean }
  /** Registered, but not as this version would register it — or not comparable: `differs`, `notStaged`, `cannotCompare` (with why). */
  | { readonly kind: 'updateNeeded'; readonly why: 'differs' | 'notStaged' | 'cannotCompare'; readonly detail: string }
  | { readonly kind: 'installed'; readonly lastRun: LastRun };

export interface WindowsTasksState {
  readonly copy: CopyState;
  readonly tasks: Readonly<Record<TaskName, TaskRow>>;
  /** The Windows dry state's sentence from a render that answered ('' when none did). */
  readonly dry: string;
}

const CHECKING: TaskRow = { kind: 'checking' };

function allTasks(row: (name: TaskName) => TaskRow): Readonly<Record<TaskName, TaskRow>> {
  return Object.fromEntries(TASK_NAMES.map((name) => [name, row(name)])) as Record<TaskName, TaskRow>;
}

/** The render's answer when the daemon gave one — registrable or not; every other ending is a gap. */
function answerOf(render: RenderResult | undefined): RenderAnswer | undefined {
  return render !== undefined && (render.kind === 'registrable' || render.kind === 'notRegistrable') ? render.answer : undefined;
}

/** Why a render gave no summary to compare with. */
function gapOf(render: RenderResult | undefined): string {
  return render === undefined || render.kind === 'registrable' || render.kind === 'notRegistrable' ? 'its render was not asked' : render.reason;
}

function stagedRow(task: Extract<TaskRead, { kind: 'present' }>, render: RenderResult | undefined): TaskRow {
  const answer = answerOf(render);
  if (answer === undefined) {
    return { kind: 'updateNeeded', why: 'cannotCompare', detail: gapOf(render) };
  }

  return summariesMatch(task.summary, answer.summary)
    ? { kind: 'installed', lastRun: task.lastRun }
    : { kind: 'updateNeeded', why: 'differs', detail: '' };
}

/** Why a registered task cannot be compared with a copy that gave no render to compare with. */
function copyGap(copy: Exclude<CopyState, { kind: 'staged' | 'checking' }>): Extract<TaskRow, { kind: 'updateNeeded' }> {
  switch (copy.kind) {
    case 'notStaged':
      return { kind: 'updateNeeded', why: 'notStaged', detail: '' };
    case 'unreadable':
    case 'digestUnreadable':
      return { kind: 'updateNeeded', why: 'cannotCompare', detail: copy.reason };
    default:
      return { kind: 'updateNeeded', why: 'cannotCompare', detail: 'this version\'s copy is not known here' };
  }
}

/** A registered, enabled, idle task against the copy: the comparison, or why none could be made — or "checking…" while the copy is probed. */
function comparedRow(task: Extract<TaskRead, { kind: 'present' }>, name: TaskName, copy: CopyState): TaskRow {
  switch (copy.kind) {
    case 'staged':
      return stagedRow(task, copy.renders[name]);
    case 'checking':
      return CHECKING;
    default:
      return copyGap(copy);
  }
}

/** Running by Task Scheduler's own result code, not only by its state. */
function runningRow(task: Extract<TaskRead, { kind: 'present' }>): TaskRow {
  return { kind: 'running', byCode: task.lastRun.kind === 'ran' && task.lastRun.result === SCHEDULER_RESULT.running };
}

function presentRow(task: Extract<TaskRead, { kind: 'present' }>, name: TaskName, copy: CopyState): TaskRow {
  if (!task.enabled) {
    return { kind: 'disabled' };
  }

  return task.running ? runningRow(task) : comparedRow(task, name, copy);
}

function rowOf(read: Extract<SchedulerRead, { kind: 'read' }>, name: TaskName, copy: CopyState): TaskRow {
  const task = taskOf(read, name);
  switch (task.kind) {
    case 'absent':
      return { kind: 'notInstalled' };
    case 'unreadable':
      return { kind: 'unreadable', reason: task.reason };
    default:
      return presentRow(task, name, copy);
  }
}

function unknownRow(read: Extract<SchedulerRead, { kind: 'unknown' }>): TaskRow {
  if (read.cause === 'checking') {
    return CHECKING;
  }

  const { reason, timeoutMs, treeEnded } = read;

  return { kind: 'unknown', reason, ...(timeoutMs === undefined ? {} : { timeoutMs }), ...(treeEnded === undefined ? {} : { treeEnded }) };
}

/** The dry state's sentence of the first render that answered on a staged copy. */
function dryOf(copy: CopyState): string {
  if (copy.kind !== 'staged') {
    return '';
  }

  return TASK_NAMES.map((name) => answerOf(copy.renders[name])?.dry.reason).find((reason) => reason !== undefined && reason !== '') ?? '';
}

/** The Task Scheduler read and the copy's probe, as the part's state. */
export function windowsTasksState(read: SchedulerRead, copy: CopyState): WindowsTasksState {
  const tasks = read.kind === 'unknown' ? allTasks(() => unknownRow(read)) : allTasks((name) => rowOf(read, name, copy));

  return { copy, tasks, dry: dryOf(copy) };
}

/** Tolerance for Task Scheduler's last-run instant: it truncates to the second, so a run that began at the record's instant can read up to a second earlier. */
const RUN_TOLERANCE_MS = 5_000;

/** A start-now record is over when the task is running, or ran at or after the record started (less the tolerance) — the read's evidence, never a flow's word. */
function startedOrRan(row: TaskRow, startedAtUtcMs: number): boolean {
  if (row.kind === 'running') {
    return true;
  }

  return row.kind === 'installed' && row.lastRun.kind === 'ran' && Date.parse(row.lastRun.utc) + RUN_TOLERANCE_MS >= startedAtUtcMs;
}

/**
 * Whether Task Scheduler's read and the copy's probe already show a pending run FINISHED (WR-E3): an install of one task is finished when
 * THAT task's row is `installed` — registered, enabled, idle and its summary equal to the one the staged copy's render of THAT task gives
 * (`summariesMatch`, in `stagedRow`); a removal when both tasks are `notInstalled` and the copy probe found no staged copy. A task that is
 * running, disabled, unreadable or different is not finished — the record then stands to its deadline, the conservative end.
 */
export function finishedBy(state: WindowsTasksState, pending: PendingTasksOp & Partial<PendingTimes>): boolean {
  return (FINISHED[pending.op] as (s: WindowsTasksState, p: PendingTasksOp & Partial<PendingTimes>) => boolean)(state, pending);
}

/** The evidence each op's end leaves in the read: an install's row is `installed`, a start's task is running (or ran since), a removal's tasks are gone and its copy too. */
const FINISHED: { readonly [K in PendingTasksOp['op']]: (state: WindowsTasksState, pending: Extract<PendingTasksOp, { op: K }> & Partial<PendingTimes>) => boolean } = {
  install: (state, pending) => state.tasks[pending.task].kind === 'installed',
  run: (state, pending) => startedOrRan(state.tasks[pending.task], pending.startedAtUtcMs ?? Number.POSITIVE_INFINITY),
  remove: (state) => TASK_NAMES.every((name) => state.tasks[name].kind === 'notInstalled') && state.copy.kind === 'notStaged',
};
