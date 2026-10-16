import { DAEMON_EXIT } from '../client/exitCodes';
import type { TaskName } from '../client/verbs';
import type { ViewLevel } from '../panel/view';
import { noticeText } from '../text/safeText';
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

export interface WindowsTasksView {
  /** "Windows daemon copy: 0.4.0 (0123456789ab) staged" — '' off Windows. */
  readonly copy: { readonly line: string; readonly level: ViewLevel };
  readonly tasks: readonly WindowsTaskLine[];
  /** The Windows dry state, as the daemon's render states it ('' when no render answered). */
  readonly dry: string;
}

/** Nothing to say (off Windows, or before a caller derived anything): the page draws no part. */
export const NO_WINDOWS_TASKS: WindowsTasksView = { copy: { line: '', level: 'none' }, tasks: [], dry: '' };

const COPY_PREFIX = 'Windows daemon copy: ';

const DIGEST_SHOWN = 12;

const LABELS: { readonly [K in TaskName]: string } = { watch: 'Windows watch task: ', archive: 'Windows archive task: ' };

// ---- the copy line ----

function digestOf(copy: { readonly digest: string }): string {
  return copy.digest.slice(0, DIGEST_SHOWN);
}

/** What the copy's first render answered beyond "registrable": a copy the daemon will not let a task name, or a configuration it refuses. */
function stagedLine(copy: Extract<CopyState, { kind: 'staged' }>, pin: string): { readonly line: string; readonly level: ViewLevel } {
  const head = `${COPY_PREFIX}${pin} (${digestOf(copy)}) staged`;
  const first = copy.renders.watch ?? copy.renders.archive;
  switch (first?.kind) {
    case 'notRegistrable':
      return { line: `${head} — not registrable: ${noticeText(first.reason)}`, level: 'warn' };
    case 'configurationInError':
      return { line: `${head} — the daemon refuses its configuration: ${noticeText(first.reason)}`, level: 'warn' };
    default:
      return { line: head, level: 'ok' };
  }
}

function copyLine(copy: CopyState, pin: string): { readonly line: string; readonly level: ViewLevel } {
  switch (copy.kind) {
    case 'unavailable':
      return { line: '', level: 'none' };
    case 'checking':
      return { line: `${COPY_PREFIX}checking…`, level: 'none' };
    case 'digestUnreadable':
      return { line: `${COPY_PREFIX}not known — ${noticeText(copy.reason)}`, level: 'warn' };
    case 'notStaged':
      return { line: `${COPY_PREFIX}${pin} (${digestOf(copy)}) not staged`, level: 'none' };
    case 'unreadable':
      return { line: `${COPY_PREFIX}${pin} (${digestOf(copy)}) could not be checked — ${noticeText(copy.reason)}`, level: 'warn' };
    default:
      return stagedLine(copy, pin);
  }
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
  0x41301: 'running now',
  0x41306: 'stopped by its time limit',
  0x8004131f: 'skipped — a run of it was still going',
};

/** The results that are the task working as built: a success, a skipped overlap, a busy lock. */
const NORMAL_RESULTS: ReadonlySet<number> = new Set([DAEMON_EXIT.ok, DAEMON_EXIT.busy, 0x8004131f]);

function resultWords(code: number): string {
  const known = RESULT_WORDS[code];
  if (known !== undefined) {
    return known;
  }

  return code > 0xffff ? `result 0x${code.toString(16).toUpperCase().padStart(8, '0')}` : `exit ${code}`;
}

type Line = { readonly line: string; readonly level: ViewLevel };

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

function unknownLine(row: Extract<TaskRow, { kind: 'unknown' }>): string {
  if (row.timeoutMs === undefined) {
    return `unknown — ${noticeText(row.reason)}`;
  }

  return `unknown — the Task Scheduler read timed out after ${Math.round(row.timeoutMs / 1000)} s (wslCare.timeouts.windowsTimeGuardQuerySeconds); its PowerShell and everything it started were ended`;
}

function taskLine(row: TaskRow, formatInstant: (iso: string) => string): Line {
  switch (row.kind) {
    case 'checking':
      return { line: 'checking…', level: 'none' };
    case 'unknown':
      return { line: unknownLine(row), level: 'unknown' };
    case 'notInstalled':
      return { line: 'not installed', level: 'none' };
    case 'unreadable':
      return { line: `unreadable — ${noticeText(row.reason)}`, level: 'unknown' };
    case 'disabled':
      return { line: 'installed, but disabled in Task Scheduler — install again to enable it', level: 'warn' };
    case 'running':
      return { line: 'installed, running (0x41301)', level: 'ok' };
    case 'updateNeeded':
      return { line: UPDATE_SENTENCE[row.why](row.detail), level: 'warn' };
    default:
      return installedLine(row, formatInstant);
  }
}

/** The part's lines; `pin` is `BUNDLED_DAEMON`, the build the extension carries; `formatInstant` the presentation edge's local-time formatter. */
export function windowsTasksView(state: WindowsTasksState, pin: string, formatInstant: (iso: string) => string): WindowsTasksView {
  const copy = copyLine(state.copy, pin);
  if (copy.line === '') {
    return NO_WINDOWS_TASKS;
  }
  const tasks = (Object.keys(LABELS) as TaskName[]).map((id) => {
    const { line, level } = taskLine(state.tasks[id], formatInstant);

    return { id, line: `${LABELS[id]}${line}`, level };
  });

  return { copy, tasks, dry: state.dry === '' ? '' : `Windows tasks, dry run: ${noticeText(state.dry)}` };
}
