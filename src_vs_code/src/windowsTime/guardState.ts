import type { ProcessResult } from '../process/runner';
import { noticeText } from '../text/safeText';
import { parseSchedulerAnswer, SCHEDULER_RESULT, SCHEDULER_RESULT_WORDS, schedulerReadOf, taskOf, type Channel, type SchedulerRead, type TaskRead } from '../windowsTasks/schedulerAnswer';
import { durationSeconds, GUARD_EXIT, GUARD_NAME, guardSummary, type GuardOptions } from './guardTask';
import { FAILURES } from './windowsTimeFix';

/**
 * What the panel says about the Windows Time guard (PLAN_windows_time_task.md D5, D8): the status query's answer parsed
 * into ONE closed state, and the line and buttons derived from it — pure, so every row of the plan's table is a unit test.
 * The truth is Task Scheduler's (re-read on every panel open and refresh); the only thing this window adds is the pending
 * elevated run it persisted itself (`guardPending.ts`), so a reload shows the same answer (`common.durable-status`).
 *
 * Since WR-E2 (PLAN_windows_runner.md §3.8) the query is the ONE read of every task under `\wsl-care`
 * (`windowsTasks/schedulerAnswer.ts` parses it): the guard is one of the tasks listed, read out by its NAME, so its state here
 * is a function of that read and nothing else.
 */

export type { Channel };

export type GuardState =
  | { readonly kind: 'unknown'; readonly reason: string }
  /** `stampLeft`: the task is gone but its rate-limit stamp under HKLM is still there (§11 R4 of PLAN_pwsh_engine_windows_time.md). */
  | { readonly kind: 'absent'; readonly channel: Channel; readonly stampLeft?: true }
  | { readonly kind: 'unreadable'; readonly hresult: string; readonly channel: Channel }
  | {
    readonly kind: 'present';
    readonly enabled: boolean;
    /** ISO UTC, or 'never'. */
    readonly lastRunUtc: string;
    /** Task Scheduler's last result, as the unsigned 32-bit value it reports. */
    readonly lastResult: number;
    readonly summary: readonly string[];
    readonly channel: Channel;
  };

export const NOT_ASKED: GuardState = { kind: 'unknown', reason: 'checking…' };

/** A task that answered, as the guard's `present`: a never-run task carries `never` and Task Scheduler's own "not run yet" code. */
function presentState(task: Extract<TaskRead, { kind: 'present' }>, channel: Channel): GuardState {
  const { lastRun } = task;

  return {
    kind: 'present',
    enabled: task.enabled,
    lastRunUtc: lastRun.kind === 'never' ? 'never' : lastRun.utc,
    lastResult: lastRun.kind === 'never' ? SCHEDULER_RESULT.notRunYet : lastRun.result,
    summary: task.summary,
    channel,
  };
}

/** No such task — and, when its rate-limit stamp outlived it, saying so. */
function absentState(read: Extract<SchedulerRead, { kind: 'read' }>): GuardState {
  return read.stampLeft ? { kind: 'absent', channel: read.channel, stampLeft: true } : { kind: 'absent', channel: read.channel };
}

/** A task Task Scheduler would not let this account read: its HRESULT, or — when its fields did not parse — the reason. */
function unreadableState(task: Extract<TaskRead, { kind: 'unreadable' }>, channel: Channel): GuardState {
  return task.hresult === undefined ? { kind: 'unknown', reason: task.reason } : { kind: 'unreadable', hresult: task.hresult, channel };
}

function stateOfTask(task: TaskRead, read: Extract<SchedulerRead, { kind: 'read' }>): GuardState {
  switch (task.kind) {
    case 'absent':
      return absentState(read);
    case 'unreadable':
      return unreadableState(task, read.channel);
    default:
      return presentState(task, read.channel);
  }
}

/** The ONE read as the guard's state: its task read out of the folder's tasks by name, or why the read gave no answer. */
export function guardStateFromRead(read: SchedulerRead, task: string = GUARD_NAME): GuardState {
  if (read.kind === 'unknown') {
    return read.cause === 'checking' ? NOT_ASKED : { kind: 'unknown', reason: read.reason };
  }

  return stateOfTask(taskOf(read, task), read);
}

/** The query's stdout as one state; anything outside the tagged shape is `unknown` with the reason, never a guess. */
export function parseGuardAnswer(stdout: string, task: string = GUARD_NAME): GuardState {
  return guardStateFromRead(parseSchedulerAnswer(stdout), task);
}

/** The query's process result as one state. */
export function guardStateOf(result: ProcessResult): GuardState {
  return guardStateFromRead(schedulerReadOf(result));
}

/** Task Scheduler's own result codes the guard's last run can carry, beside the guard's exits (D8 o9). */
export const SCHEDULER_RESULTS: Readonly<Record<number, string>> = {
  ...SCHEDULER_RESULT_WORDS,
  [SCHEDULER_RESULT.timeLimit]: 'stopped by its time limit (wslCare.windowsTime.guard.timeLimitMinutes)',
};

/** The sentence of each exit of the guard's own script (D2). */
export const GUARD_RESULTS: Readonly<Record<number, string>> = {
  [GUARD_EXIT.done]: 'the Windows Time service runs and the clock was resynchronised',
  [GUARD_EXIT.setAutomaticFailed]: FAILURES[GUARD_EXIT.setAutomaticFailed] ?? '',
  [GUARD_EXIT.startFailed]: FAILURES[GUARD_EXIT.startFailed] ?? '',
  [GUARD_EXIT.resyncFailed]: FAILURES[GUARD_EXIT.resyncFailed] ?? '',
  [GUARD_EXIT.rateLimited]: 'not started: it was started less than wslCare.windowsTime.guard.minMinutesBetweenStarts ago and stopped again — the next run tries again',
  [GUARD_EXIT.stampUnwritable]: 'not started: its rate-limit stamp under HKLM\\SOFTWARE\\wsl-care could not be written',
  [GUARD_EXIT.serviceMissing]: 'the Windows Time service does not exist on this machine',
};

/** The last result as words: one of the guard's exits, one of Task Scheduler's codes, or the bare code. */
export function lastResultText(code: number): string {
  const known = GUARD_RESULTS[code] ?? SCHEDULER_RESULTS[code];
  if (known !== undefined) {
    return known;
  }

  return code > 0xffff ? `result 0x${code.toString(16).toUpperCase().padStart(8, '0')}` : `exit ${code}`;
}

/** Whether a last result is the guard failing (a run not yet made, still running or rate-limited is not a failure). */
export function lastResultFailed(code: number): boolean {
  return code !== GUARD_EXIT.done && code !== GUARD_EXIT.rateLimited && code !== SCHEDULER_RESULT.running && code !== SCHEDULER_RESULT.notRunYet;
}

export type GuardAction = 'installWindowsTimeGuard' | 'removeWindowsTimeGuard';

export const GUARD_LABELS: { readonly [K in GuardAction]: string } = {
  installWindowsTimeGuard: 'Install the Windows Time guard',
  removeWindowsTimeGuard: 'Remove the Windows Time guard',
};

export type GuardLevel = 'none' | 'ok' | 'warn' | 'critical' | 'unknown';

export interface GuardView {
  readonly line: string;
  readonly level: GuardLevel;
  readonly buttons: readonly { readonly id: GuardAction; readonly label: string; readonly enabled: boolean }[];
  /**
   * Which PowerShell the Windows Time part's elevated runs use, and that the guard's task runs as SYSTEM in 5.1
   * (PLAN_pwsh_engine_windows_time.md D6) — the host's words from the engine read; '' when none is given.
   */
  readonly engine: string;
}

/** An elevated install / remove this window persisted before it started (`guardPending.ts`), while it stands. */
export interface PendingGuardOp {
  readonly op: 'install' | 'remove';
}

const PREFIX = 'Windows Time guard: ';

const NO_EVENT = ' — the service\'s stop event is not logged on this machine, so a stop is caught by the timed, boot and logon runs only';

function channelNote(channel: Channel): string {
  return channel === 'disabled' ? NO_EVENT : '';
}

function buttons(ids: readonly GuardAction[], enabled: boolean): GuardView['buttons'] {
  return ids.map((id) => ({ id, label: GUARD_LABELS[id], enabled }));
}

/** The summary fields that hold a duration: `trigger=boot enabled=True delay=PT1M`, `trigger=time enabled=True interval=PT4H`,
 * `settings limit=PT5M …`. */
const DURATION_FIELDS = ['delay=', 'interval=', 'limit='] as const;

/** The summary fields whose value is FREE TEXT and runs to the end of its line (`SUMMARY_FUNCTION`): from the first of them on,
 * nothing is read as a duration — a subscription or an action argument is compared as text (coai plan round 990e7d9a), wherever
 * the structured fields before it stand (code round: tied to the line's schema, not to a token count). */
const FREE_TEXT_FIELDS = ['subscription=', 'path=', 'args='] as const;

/** One summary token with a duration field's value as its length in seconds (`delay=60s`); any other token as it is. */
function canonicalToken(token: string): string {
  const field = DURATION_FIELDS.find((f) => token.startsWith(f));
  const seconds = field === undefined ? undefined : durationSeconds(token.slice(field.length));

  return field === undefined || seconds === undefined ? token : `${field}${seconds}s`;
}

function canonicalLine(line: string): string {
  const tokens = line.split(' ');
  const freeText = tokens.findIndex((token) => FREE_TEXT_FIELDS.some((f) => token.startsWith(f)));
  const structured = freeText === -1 ? tokens.length : freeText;

  return tokens.map((token, i) => (i < structured ? canonicalToken(token) : token)).join(' ');
}

/**
 * Whether two summaries describe the same task: line for line, byte for byte — except the duration fields, compared by VALUE,
 * because Task Scheduler stores a registered `PT60S` as `PT1M` (2026-10-10: every real install of 0.3.0 read as "not as the
 * current settings would install it"). A field whose value is no duration is compared as text.
 */
export function summariesMatch(a: readonly string[], b: readonly string[]): boolean {
  return a.length === b.length && a.every((line, i) => canonicalLine(line) === canonicalLine(b[i] ?? ''));
}

function sameSummary(a: readonly string[], b: readonly string[]): boolean {
  return summariesMatch(a, b);
}

function lastRun(state: Extract<GuardState, { kind: 'present' }>, formatInstant: (iso: string) => string): string {
  return state.lastRunUtc === 'never' ? 'not run yet' : `last run ${formatInstant(state.lastRunUtc)}: ${lastResultText(state.lastResult)}`;
}

function presentView(state: Extract<GuardState, { kind: 'present' }>, options: GuardOptions, formatInstant: (iso: string) => string): Omit<GuardView, 'buttons' | 'engine'> & { readonly ids: readonly GuardAction[] } {
  if (!state.enabled) {
    return { line: 'installed, but disabled in Task Scheduler — install it again to enable it', level: 'warn', ids: ['installWindowsTimeGuard', 'removeWindowsTimeGuard'] };
  }
  if (!sameSummary(state.summary, guardSummary(options))) {
    return { line: 'installed, but not as the current settings would install it (another version, a changed setting or an edit in Task Scheduler) — install it again to update it', level: 'warn', ids: ['installWindowsTimeGuard', 'removeWindowsTimeGuard'] };
  }
  return { line: `installed — ${lastRun(state, formatInstant)}${channelNote(state.channel)}`, level: lastRunLevel(state), ids: ['removeWindowsTimeGuard'] };
}

function lastRunLevel(state: Extract<GuardState, { kind: 'present' }>): GuardLevel {
  return state.lastRunUtc !== 'never' && lastResultFailed(state.lastResult) ? 'warn' : 'ok';
}

function unknownLine(state: Extract<GuardState, { kind: 'unknown' }>): string {
  return state === NOT_ASKED ? 'checking…' : `unknown — ${state.reason}`;
}

function settledView(state: GuardState, options: GuardOptions, formatInstant: (iso: string) => string): Omit<GuardView, 'buttons' | 'engine'> & { readonly ids: readonly GuardAction[] } {
  switch (state.kind) {
    case 'unknown':
      return { line: unknownLine(state), level: 'unknown', ids: [] };
    case 'absent':
      return absentView(state);
    case 'unreadable':
      return { line: `Task Scheduler does not let this account read it (${state.hresult}) — installed or not cannot be told`, level: 'unknown', ids: ['installWindowsTimeGuard', 'removeWindowsTimeGuard'] };
    default:
      return presentView(state, options, formatInstant);
  }
}

/** Not installed — and, when its rate-limit stamp outlived the task (a removal the Store engine may have virtualised, §11 R4), saying so and offering Remove again. */
function absentView(state: Extract<GuardState, { kind: 'absent' }>): Omit<GuardView, 'buttons' | 'engine'> & { readonly ids: readonly GuardAction[] } {
  const line = `not installed — it would start the Windows Time service by itself whenever it stops${channelNote(state.channel)}`;

  return state.stampLeft === true
    ? { line: `${line}; its rate-limit stamp under HKLM\\SOFTWARE\\wsl-care is still there — Remove the guard again to clear it`, level: 'none', ids: ['installWindowsTimeGuard', 'removeWindowsTimeGuard'] }
    : { line, level: 'none', ids: ['installWindowsTimeGuard'] };
}

/** Whether Task Scheduler already shows the end a pending run was for: installed as these settings, or gone. */
export function finishedBy(state: GuardState, pending: PendingGuardOp, options: GuardOptions): boolean {
  return pending.op === 'install' ? state.kind === 'present' && sameSummary(state.summary, guardSummary(options)) : state.kind === 'absent';
}

const WAITING: { readonly [K in PendingGuardOp['op']]: string } = {
  install: 'Waiting for the elevated PowerShell that installs it (answer the UAC prompt, or wait for it to end)…',
  remove: 'Waiting for the elevated PowerShell that removes it (answer the UAC prompt, or wait for it to end)…',
};

/**
 * The guard's line and buttons (D5, D8). While an elevated run this window persisted stands, the line says so and both
 * buttons are disabled; otherwise the state decides. `formatInstant` is the presentation edge's local-time formatter.
 */
export function guardView(state: GuardState, options: GuardOptions, pending: PendingGuardOp | undefined, formatInstant: (iso: string) => string, checking = false, engine = ''): GuardView {
  const settled = settledView(state, options, formatInstant);
  if (pending !== undefined) {
    return { line: noticeText(PREFIX + WAITING[pending.op]), level: 'none', buttons: buttons(['installWindowsTimeGuard', 'removeWindowsTimeGuard'], false), engine };
  }

  return { line: noticeText(PREFIX + settled.line + checkingNote(state, checking)), level: settled.level, buttons: buttons(settled.ids, true), engine };
}

/** While Task Scheduler is asked again, an answer already shown says so (codex: a stale line must not look current). */
function checkingNote(state: GuardState, checking: boolean): string {
  return checking && state !== NOT_ASKED ? ' (checking again…)' : '';
}
