import { DAEMON_EXIT } from '../client/exitCodes';
import type { TaskName } from '../client/verbs';
import type { ViewLevel } from '../shared/viewLevel';
import { noticeText } from '../text/safeText';
import { SCHEDULER_RESULT, SCHEDULER_RESULT_WORDS } from '../scheduler/schedulerAnswer';
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
export type TasksAction = 'installWindowsWatchTask' | 'installWindowsArchiveTask' | 'removeWindowsTasks';

export interface WindowsTasksButton {
  readonly id: TasksAction;
  readonly label: string;
  readonly enabled: boolean;
}

export interface WindowsTasksView {
  /** "Windows daemon copy: 0.4.0 (0123456789ab) staged" — '' off Windows. */
  readonly copy: { readonly line: string; readonly level: ViewLevel };
  readonly tasks: readonly WindowsTaskLine[];
  /** The Windows dry state, as the daemon's render states it ('' when no render answered). */
  readonly dry: string;
  /** The install and remove buttons, derived from the state, the persisted pending run and this window's flow — none off Windows. */
  readonly buttons: readonly WindowsTasksButton[];
}

/** Nothing to say (off Windows, or before a caller derived anything): the page draws no part. */
export const NO_WINDOWS_TASKS: WindowsTasksView = { copy: { line: '', level: 'none' }, tasks: [], dry: '', buttons: [] };

/** What holds the buttons besides the state: the persisted pending run (any window, across a reload) and a flow running in THIS window. */
export interface TasksControls {
  readonly pending?: PendingTasksOp | undefined;
  readonly busy?: boolean;
}

type Line = { readonly line: string; readonly level: ViewLevel };

const COPY_PREFIX = 'Windows daemon copy: ';

const DIGEST_SHOWN = 12;

const LABELS: { readonly [K in TaskName]: string } = { watch: 'Windows watch task: ', archive: 'Windows archive task: ' };

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

/** The last result of a task's run in words: the daemon's own exits first, then Task Scheduler's codes, else the bare code. */
const RESULT_WORDS: Readonly<Record<number, string>> = {
  [DAEMON_EXIT.ok]: 'succeeded',
  [DAEMON_EXIT.runFailed]: 'the run failed (exit 1)',
  [DAEMON_EXIT.observeOnly]: 'the configuration is in error (exit 78)',
  [DAEMON_EXIT.busy]: 'skipped — another run held the run lock (normal)',
  [DAEMON_EXIT.notAsRoot]: 'refused — it ran elevated, which it never may (exit 81)',
  [DAEMON_EXIT.taskEnded]: 'ended at its deadline — its whole tree was ended (exit 84)',
  ...SCHEDULER_RESULT_WORDS,
};

/** The results that are the task working as built: a success, a skipped overlap, a busy lock. */
const NORMAL_RESULTS: ReadonlySet<number> = new Set([DAEMON_EXIT.ok, DAEMON_EXIT.busy, SCHEDULER_RESULT.skipped]);

function resultWords(code: number): string {
  const known = RESULT_WORDS[code];
  if (known !== undefined) {
    return known;
  }

  return code > 0xffff ? `result 0x${code.toString(16).toUpperCase().padStart(8, '0')}` : `exit ${code}`;
}

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
export function treeVerdict(treeEnded: boolean | undefined): string {
  return treeEnded === true ? 'its PowerShell and everything it started were ended' : 'the extension could not confirm that its PowerShell and everything it started were ended';
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
};

const ACTIONS: readonly TasksAction[] = ['installWindowsWatchTask', 'installWindowsArchiveTask', 'removeWindowsTasks'];

/** The copy states in which no install can be asked: its digest is unknown, it is being probed, or this is not Windows. */
const NO_INSTALL: ReadonlySet<CopyState['kind']> = new Set(['digestUnreadable', 'checking', 'unavailable']);

/** The task rows that say nothing is registered (or that nothing is known yet): a removal has nothing to delete there. */
const NOTHING_REGISTERED: ReadonlySet<TaskRow['kind']> = new Set(['notInstalled', 'checking', 'unknown']);

function removable(state: WindowsTasksState): boolean {
  const registered = Object.values(state.tasks).some((row) => !NOTHING_REGISTERED.has(row.kind));

  return registered || state.copy.kind === 'staged' || state.copy.kind === 'unreadable';
}

function allowed(id: TasksAction, state: WindowsTasksState): boolean {
  return id === 'removeWindowsTasks' ? removable(state) : !NO_INSTALL.has(state.copy.kind);
}

/** The three buttons: every one disabled while a run stands or runs; otherwise each by the state. */
function tasksButtons(state: WindowsTasksState, held: boolean): readonly WindowsTasksButton[] {
  return ACTIONS.map((id) => ({ id, label: TASKS_LABELS[id], enabled: !held && allowed(id, state) }));
}

/** The copy line while a pending run stands — the guard's "waiting" shape: what the PowerShell is doing, in every window and after a reload. */
function waitingLine(pending: PendingTasksOp): Line {
  const what = pending.op === 'install' ? `installs the Windows ${pending.task} task` : 'removes the Windows tasks';

  return { line: `Waiting for the PowerShell that ${what}…`, level: 'none' };
}

/** The part's lines; `pin` is `BUNDLED_DAEMON`, the build the extension carries; `formatInstant` the presentation edge's local-time formatter. */
export function windowsTasksView(state: WindowsTasksState, pin: string, formatInstant: (iso: string) => string, controls: TasksControls = {}): WindowsTasksView {
  const stated = copyLine(state.copy, pin);
  if (stated.line === '') {
    return NO_WINDOWS_TASKS;
  }
  const tasks = (Object.keys(LABELS) as TaskName[]).map((id) => {
    const { line, level } = taskLine(state.tasks[id], formatInstant);

    return { id, line: `${LABELS[id]}${line}`, level };
  });

  return { copy: copyOrWaiting(stated, controls), tasks, dry: dryLine(state.dry), buttons: tasksButtons(state, held(controls)) };
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
