import type { TaskName } from '../client/verbs';
import type { ViewLevel } from '../shared/viewLevel';
import { noticeText } from '../text/safeText';
import { SCHEDULER_RESULT } from '../scheduler/schedulerAnswer';
import { NORMAL_RESULTS, resultWords } from './resultWords';
import { lastCleanupLine, type CleanupRecordRead } from './cleanupRecord';
import type { PendingTasksOp } from './tasksPending';
import type { CopyState, TaskRow, WindowsTasksState } from './tasksState';

/**
 * The lines the panel shows for the Windows tasks (WR-E2 of PLAN_windows_runner.md §3.8): the copy line, one line per task and the
 * dry line, derived by the host from `tasksState.ts` — never a page flag, and every word a task, Task Scheduler or the daemon supplied
 * passes `noticeText` (printable, bounded, no markdown link) before it is put into a sentence.
 */

export interface WindowsTaskLine {
  readonly id: TaskName;
  readonly line: string;
  readonly level: ViewLevel;
}

/** The three buttons of the Windows tasks (WR-E3), each posting its bare id — the page never sends a task, a path or a value. */
export type TasksAction =
  | 'installWindowsWatchTask'
  | 'installWindowsArchiveTask'
  | 'removeWindowsTasks'
  | 'runWindowsWatchTask'
  | 'runWindowsArchiveTask'
  | 'installWindowsCleanupTask'
  | 'previewWindowsTempCleanup'
  | 'runWindowsCleanupTask';

export interface WindowsTasksButton {
  readonly id: TasksAction;
  readonly label: string;
  readonly enabled: boolean;
}

export interface WindowsTasksView {
  /** "Windows daemon copy: 0.4.0 (0123456789ab) staged" — '' off Windows. */
  readonly copy: { readonly line: string; readonly level: ViewLevel };
  readonly tasks: readonly WindowsTaskLine[];
  /** WR-T3: the last-cleanup line — '' when the cleanup task is not registered or its read did not answer. */
  readonly cleanup: { readonly line: string; readonly level: ViewLevel };
  /** The Windows dry state, as the daemon's render states it ('' when no render answered). */
  readonly dry: string;
  /** The install and remove buttons, derived from the state, the persisted pending run and this window's flow — none off Windows. */
  readonly buttons: readonly WindowsTasksButton[];
}

/** Nothing to say (off Windows, or before a caller derived anything): the page draws no part. */
export const NO_WINDOWS_TASKS: WindowsTasksView = { copy: { line: '', level: 'none' }, tasks: [], cleanup: { line: '', level: 'none' }, dry: '', buttons: [] };

/** What holds the buttons besides the state: the persisted pending run (any window, across a reload) and a flow running in THIS window. */
export interface TasksControls {
  readonly pending?: PendingTasksOp | undefined;
  readonly busy?: boolean;
  /** WR-T3: the record read for the cleanup task's last run (the host's), when it has answered. */
  readonly cleanupRecord?: CleanupRecordRead | undefined;
  /** '' when the bundled daemon has the cleanup task, else why not (`cleanupGate.ts`). */
  readonly cleanupGate?: string;
}

type Line = { readonly line: string; readonly level: ViewLevel };

const COPY_PREFIX = 'Windows daemon copy: ';

const DIGEST_SHOWN = 12;

const LABELS: { readonly [K in TaskName]: string } = { watch: 'Windows watch task: ', archive: 'Windows archive task: ', cleanup: 'Windows cleanup task: ' };

// ---- the copy line ----

function digestOf(copy: { readonly digest: string }): string {
  return copy.digest.slice(0, DIGEST_SHOWN);
}

/** The render that speaks for the copy: the first task's, else the second's. */
function firstRender(copy: Extract<CopyState, { kind: 'staged' }>): Extract<CopyState, { kind: 'staged' }>['renders'][TaskName] {
  return copy.renders.watch ?? copy.renders.archive;
}

/** What the copy's first render answered beyond "registrable": a copy the daemon will not let a task name, or a configuration it refuses. */
function stagedLine(copy: Extract<CopyState, { kind: 'staged' }>, pin: string): Line {
  const head = `${COPY_PREFIX}${pin} (${digestOf(copy)}) staged`;
  const first = firstRender(copy);
  switch (first?.kind) {
    case 'notRegistrable':
      return { line: `${head} — not registrable: ${noticeText(first.reason)}`, level: 'warn' };
    case 'configurationInError':
      return { line: `${head} — the daemon refuses its configuration: ${noticeText(first.reason)}`, level: 'warn' };
    default:
      return { line: head, level: 'ok' };
  }
}

type CopyKind = Exclude<CopyState['kind'], 'staged'>;

/** One sentence per copy state that is not a staged copy. */
const COPY_LINES: { readonly [K in CopyKind]: (copy: Extract<CopyState, { kind: K }>, pin: string) => Line } = {
  unavailable: () => ({ line: '', level: 'none' }),
  checking: () => ({ line: `${COPY_PREFIX}checking…`, level: 'none' }),
  digestUnreadable: (copy) => ({ line: `${COPY_PREFIX}not known — ${noticeText(copy.reason)}`, level: 'warn' }),
  notStaged: (copy, pin) => ({ line: `${COPY_PREFIX}${pin} (${digestOf(copy)}) not staged`, level: 'none' }),
  unreadable: (copy, pin) => ({ line: `${COPY_PREFIX}${pin} (${digestOf(copy)}) could not be checked — ${noticeText(copy.reason)}`, level: 'warn' }),
};

function copyLine(copy: CopyState, pin: string): Line {
  return copy.kind === 'staged' ? stagedLine(copy, pin) : (COPY_LINES[copy.kind] as (c: CopyState, pin: string) => Line)(copy, pin);
}

// ---- one task's line ----

function installedLine(row: Extract<TaskRow, { kind: 'installed' }>, formatInstant: (iso: string) => string): Line {
  const { lastRun } = row;
  if (lastRun.kind === 'never') {
    return { line: 'installed, never run', level: 'ok' };
  }

  return { line: `installed, last run ${noticeText(formatInstant(lastRun.utc))}: ${resultWords(lastRun.result)}`, level: NORMAL_RESULTS.has(lastRun.result) ? 'ok' : 'warn' };
}

const UPDATE_SENTENCE: { readonly [K in Extract<TaskRow, { kind: 'updateNeeded' }>['why']]: (detail: string) => string } = {
  differs: () => 'installed, but not as this version would install it (another version, a changed setting or an edit in Task Scheduler) — install again to update',
  notStaged: () => 'installed — this version\'s copy is not staged, install again to update',
  cannotCompare: (detail) => `installed — it cannot be compared with this version's copy (${noticeText(detail)}), install again to update`,
};

/** What the runner could say of a process tree at a ceiling: ended only when it VERIFIED it — the one wording of every timeout line. */
export function treeVerdict(treeEnded: boolean | undefined, who = 'its PowerShell'): string {
  return treeEnded === true ? `${who} and everything it started were ended` : `the extension could not confirm that ${who} and everything it started were ended`;
}

function unknownLine(row: Extract<TaskRow, { kind: 'unknown' }>): string {
  if (row.timeoutMs === undefined) {
    return `unknown — ${noticeText(row.reason)}`;
  }

  const ended = treeVerdict(row.treeEnded);

  return `unknown — the Task Scheduler read timed out after ${Math.round(row.timeoutMs / 1000)} s (wslCare.timeouts.windowsTimeGuardQuerySeconds); ${ended}`;
}

/** Running, said with Task Scheduler's own code only when that code is what its last result says. */
function runningLine(row: Extract<TaskRow, { kind: 'running' }>): string {
  return row.byCode ? `installed, running (0x${SCHEDULER_RESULT.running.toString(16).toUpperCase()})` : 'installed, running';
}

/** One line per row kind; `installed` is the one that needs the presentation edge's time formatter. */
const TASK_LINES: { readonly [K in TaskRow['kind']]: (row: Extract<TaskRow, { kind: K }>, formatInstant: (iso: string) => string) => Line } = {
  checking: () => ({ line: 'checking…', level: 'none' }),
  unknown: (row) => ({ line: unknownLine(row), level: 'unknown' }),
  notInstalled: () => ({ line: 'not installed', level: 'none' }),
  unreadable: (row) => ({ line: `unreadable — ${noticeText(row.reason)}`, level: 'unknown' }),
  disabled: () => ({ line: 'installed, but disabled in Task Scheduler — install again to enable it', level: 'warn' }),
  running: (row) => ({ line: runningLine(row), level: 'ok' }),
  updateNeeded: (row) => ({ line: UPDATE_SENTENCE[row.why](row.detail), level: 'warn' }),
  installed: installedLine,
};

function taskLine(row: TaskRow, formatInstant: (iso: string) => string): Line {
  return (TASK_LINES[row.kind] as (r: TaskRow, f: (iso: string) => string) => Line)(row, formatInstant);
}

// ---- the buttons (WR-E3) ----

/** The three buttons' labels — the words the panel shows and the palette titles echo. */
export const TASKS_LABELS: { readonly [K in TasksAction]: string } = {
  installWindowsWatchTask: 'Install the Windows watch task',
  installWindowsArchiveTask: 'Install the Windows archive task',
  removeWindowsTasks: 'Remove the Windows tasks',
  runWindowsWatchTask: 'Run the Windows watch now',
  runWindowsArchiveTask: 'Archive Windows sessions now',
  installWindowsCleanupTask: 'Install the Windows cleanup task',
  previewWindowsTempCleanup: 'Preview the Windows TEMP cleanup',
  runWindowsCleanupTask: 'Clean Windows TEMP now',
};

const ACTIONS: readonly TasksAction[] = ['installWindowsWatchTask', 'installWindowsArchiveTask', 'removeWindowsTasks', 'runWindowsWatchTask', 'runWindowsArchiveTask', 'installWindowsCleanupTask', 'previewWindowsTempCleanup', 'runWindowsCleanupTask'];

/** The task each run button starts. */
const RUN_OF: Readonly<Partial<Record<TasksAction, TaskName>>> = { runWindowsWatchTask: 'watch', runWindowsArchiveTask: 'archive', runWindowsCleanupTask: 'cleanup' };

/** The rows that say nothing is known yet: a run button waits for the read; every other row leaves it enabled and the PRESS says what is wrong (not installed, disabled, running…). */
const NOT_KNOWN: ReadonlySet<TaskRow['kind']> = new Set(['checking', 'unknown']);

/** The copy states in which no install can be asked: its digest is unknown, it is being probed, or this is not Windows. */
const NO_INSTALL: ReadonlySet<CopyState['kind']> = new Set(['digestUnreadable', 'checking', 'unavailable']);

/** The task rows that say nothing is registered (or that nothing is known yet): a removal has nothing to delete there. */
const NOTHING_REGISTERED: ReadonlySet<TaskRow['kind']> = new Set(['notInstalled', 'checking', 'unknown']);

function removable(state: WindowsTasksState): boolean {
  const registered = Object.values(state.tasks).some((row) => !NOTHING_REGISTERED.has(row.kind));

  return registered || state.copy.kind === 'staged' || state.copy.kind === 'unreadable';
}

function allowed(id: TasksAction, state: WindowsTasksState): boolean {
  const run = RUN_OF[id];
  if (run !== undefined) {
    return !NOT_KNOWN.has(state.tasks[run].kind);
  }

  return id === 'removeWindowsTasks' ? removable(state) : !NO_INSTALL.has(state.copy.kind);
}

/** The cleanup task's three buttons: off while the bundled daemon is older than the task (`cleanupGate.ts`). */
const CLEANUP_BUTTONS: ReadonlySet<TasksAction> = new Set<TasksAction>(['installWindowsCleanupTask', 'previewWindowsTempCleanup', 'runWindowsCleanupTask']);

/** The buttons: every one disabled while a run stands or runs, the cleanup's while the gate is closed; otherwise each by the state. */
function tasksButtons(state: WindowsTasksState, held: boolean, gate: string): readonly WindowsTasksButton[] {
  return ACTIONS.map((id) => ({ id, label: TASKS_LABELS[id], enabled: !held && !(gate !== '' && CLEANUP_BUTTONS.has(id)) && allowed(id, state) }));
}

/** The copy line while a pending run stands — the guard's "waiting" shape: what the PowerShell is doing, in every window and after a reload. */
function waitingLine(pending: PendingTasksOp): Line {
  return { line: `Waiting for the PowerShell that ${(WAITING[pending.op] as (p: PendingTasksOp) => string)(pending)}…`, level: 'none' };
}

/** What the pending PowerShell is doing, by its record: one sentence per op. */
const WAITING: { readonly [K in PendingTasksOp['op']]: (pending: Extract<PendingTasksOp, { op: K }>) => string } = {
  install: (pending) => `installs the Windows ${pending.task} task`,
  run: (pending) => `starts the Windows ${pending.task} task`,
  remove: () => 'removes the Windows tasks',
};

/** The part's lines; `pin` is `BUNDLED_DAEMON`, the build the extension carries; `formatInstant` the presentation edge's local-time formatter. */
export function windowsTasksView(state: WindowsTasksState, pin: string, formatInstant: (iso: string) => string, controls: TasksControls = {}): WindowsTasksView {
  const stated = copyLine(state.copy, pin);
  if (stated.line === '') {
    return NO_WINDOWS_TASKS;
  }
  const gate = controls.cleanupGate ?? '';
  const tasks = (Object.keys(LABELS) as TaskName[]).map((id) => {
    const { line, level } = rowLine(id, state, formatInstant, gate);

    return { id, line: `${LABELS[id]}${line}`, level };
  });

  return { copy: copyOrWaiting(stated, controls), tasks, cleanup: cleanupLineOf(state, controls, formatInstant), dry: dryLine(state.dry), buttons: tasksButtons(state, held(controls), gate) };
}

/** The last-cleanup line — none while the bundled daemon is older than the cleanup task. */
function cleanupLineOf(state: WindowsTasksState, controls: TasksControls, formatInstant: (iso: string) => string): Line {
  return (controls.cleanupGate ?? '') === '' ? lastCleanupLine(state.cleanupRun, controls.cleanupRecord, formatInstant) : { line: '', level: 'none' };
}

/** One task's line: its row — except the cleanup's while the bundled daemon is older than the task, which says so instead of a state it cannot know. */
function rowLine(id: TaskName, state: WindowsTasksState, formatInstant: (iso: string) => string, gate: string): Line {
  return id === 'cleanup' && gate !== '' ? { line: `unavailable — ${gate}`, level: 'none' } : taskLine(state.tasks[id], formatInstant);
}

/** The copy line, or — while a pending run stands — what that run is doing. */
function copyOrWaiting(stated: Line, controls: TasksControls): Line {
  return controls.pending === undefined ? stated : waitingLine(controls.pending);
}

function dryLine(dry: string): string {
  return dry === '' ? '' : `Windows tasks, dry run: ${noticeText(dry)}`;
}

/** Whether the buttons are held: a pending run in any window, or a flow running in this one. */
function held(controls: TasksControls): boolean {
  return controls.pending !== undefined || controls.busy === true;
}
