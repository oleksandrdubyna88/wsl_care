import type { ProcessResult } from '../process/runner';
import { signed32 } from '../process/runner';
import { noticeText } from '../text/safeText';
import { durationSeconds, GUARD_EXIT, guardSummary, type GuardOptions } from './guardTask';
import { FAILURES } from './windowsTimeFix';

/**
 * What the panel says about the Windows Time guard (PLAN_windows_time_task.md D5, D8): the status query's answer parsed
 * into ONE closed state, and the line and buttons derived from it — pure, so every row of the plan's table is a unit test.
 * The truth is Task Scheduler's (re-read on every panel open and refresh); the only thing this window adds is the pending
 * elevated run it persisted itself (`guardPending.ts`), so a reload shows the same answer (`common.durable-status`).
 */

export type Channel = 'enabled' | 'disabled' | 'unknown';

export type GuardState =
  | { readonly kind: 'unknown'; readonly reason: string }
  | { readonly kind: 'absent'; readonly channel: Channel }
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

interface Tagged {
  readonly tags: ReadonlyMap<string, string>;
  readonly summary: readonly string[];
}

const SUMMARY_TAG = 'summary:';

/** A `name=value` line's tag; the FIRST occurrence of a name wins (a value printed twice is never re-read). */
function tagOf(line: string, tags: Map<string, string>): void {
  const at = line.indexOf('=');
  const name = line.slice(0, at);
  if (at > 0 && !tags.has(name)) {
    tags.set(name, line.slice(at + 1).trim());
  }
}

function tagged(stdout: string): Tagged {
  const tags = new Map<string, string>();
  const lines = stdout.split(/\r?\n/).filter((l) => l.trim() !== '');
  lines.filter((l) => !l.startsWith(SUMMARY_TAG)).forEach((line) => tagOf(line, tags));

  return { tags, summary: lines.filter((l) => l.startsWith(SUMMARY_TAG)).map((l) => l.slice(SUMMARY_TAG.length)) };
}

function tag(t: Tagged, name: string): string {
  return t.tags.get(name) ?? '';
}

function channelOf(value: string | undefined): Channel {
  return value === 'enabled' || value === 'disabled' ? value : 'unknown';
}

const INTEGER = /^-?\d+$/;

function present(t: Tagged, channel: Channel): GuardState {
  const result = tag(t, 'lastResult');
  if (!INTEGER.test(result) || tag(t, 'lastRunUtc') === '') {
    return { kind: 'unknown', reason: 'the Task Scheduler query answered without the last run' };
  }

  return { kind: 'present', enabled: tag(t, 'enabled') === 'True', lastRunUtc: tag(t, 'lastRunUtc'), lastResult: Number(result) >>> 0, summary: t.summary, channel };
}

const BY_GUARD: Readonly<Record<string, (t: Tagged, channel: Channel) => GuardState>> = {
  absent: (_t, channel) => ({ kind: 'absent', channel }),
  unreadable: (t, channel) => ({ kind: 'unreadable', hresult: tag(t, 'hresult') || '?', channel }),
  present,
};

/** The query's stdout as one state; anything outside the tagged shape is `unknown` with the reason, never a guess. */
export function parseGuardAnswer(stdout: string): GuardState {
  const t = tagged(stdout);
  const read = Object.hasOwn(BY_GUARD, tag(t, 'guard')) ? BY_GUARD[tag(t, 'guard')] : undefined;

  return read === undefined ? { kind: 'unknown', reason: 'the Task Scheduler query printed no answer' } : read(t, channelOf(t.tags.get('channel')));
}

const NOT_ANSWERED: { readonly [K in Exclude<ProcessResult['kind'], 'exited'>]: (r: Extract<ProcessResult, { kind: K }>) => string } = {
  timedOut: (r) => `the Task Scheduler query did not answer within ${Math.round(r.timeoutMs / 1000)} s (wslCare.timeouts.windowsTimeGuardQuerySeconds)`,
  failedToStart: (r) => `the Task Scheduler query could not start: ${r.reason}`,
  signalled: (r) => `the Task Scheduler query was stopped (${r.signal})`,
  tooMuchOutput: () => 'the Task Scheduler query printed more than the extension reads',
};

/** The query's process result as one state. */
export function guardStateOf(result: ProcessResult): GuardState {
  if (result.kind !== 'exited') {
    return { kind: 'unknown', reason: (NOT_ANSWERED[result.kind] as (r: ProcessResult) => string)(result) };
  }
  const code = signed32(result.code);

  return code === 0 ? parseGuardAnswer(result.stdout.toString('utf8')) : { kind: 'unknown', reason: `the Task Scheduler query exited ${code}` };
}

/** Task Scheduler's own result codes the guard's last run can carry, beside the guard's exits (D8 o9). */
export const SCHEDULER_RESULTS: Readonly<Record<number, string>> = {
  0x41301: 'running now',
  0x41303: 'not run yet',
  0x41306: 'stopped by its time limit (wslCare.windowsTime.guard.timeLimitMinutes)',
  0x8004131f: 'skipped — a run of it was still going',
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
  return code !== GUARD_EXIT.done && code !== GUARD_EXIT.rateLimited && code !== 0x41301 && code !== 0x41303;
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

function presentView(state: Extract<GuardState, { kind: 'present' }>, options: GuardOptions, formatInstant: (iso: string) => string): Omit<GuardView, 'buttons'> & { readonly ids: readonly GuardAction[] } {
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

function settledView(state: GuardState, options: GuardOptions, formatInstant: (iso: string) => string): Omit<GuardView, 'buttons'> & { readonly ids: readonly GuardAction[] } {
  switch (state.kind) {
    case 'unknown':
      return { line: unknownLine(state), level: 'unknown', ids: [] };
    case 'absent':
      return { line: `not installed — it would start the Windows Time service by itself whenever it stops${channelNote(state.channel)}`, level: 'none', ids: ['installWindowsTimeGuard'] };
    case 'unreadable':
      return { line: `Task Scheduler does not let this account read it (${state.hresult}) — installed or not cannot be told`, level: 'unknown', ids: ['installWindowsTimeGuard', 'removeWindowsTimeGuard'] };
    default:
      return presentView(state, options, formatInstant);
  }
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
export function guardView(state: GuardState, options: GuardOptions, pending: PendingGuardOp | undefined, formatInstant: (iso: string) => string, checking = false): GuardView {
  const settled = settledView(state, options, formatInstant);
  if (pending !== undefined) {
    return { line: noticeText(PREFIX + WAITING[pending.op]), level: 'none', buttons: buttons(['installWindowsTimeGuard', 'removeWindowsTimeGuard'], false) };
  }

  return { line: noticeText(PREFIX + settled.line + checkingNote(state, checking)), level: settled.level, buttons: buttons(settled.ids, true) };
}

/** While Task Scheduler is asked again, an answer already shown says so (codex: a stale line must not look current). */
function checkingNote(state: GuardState, checking: boolean): string {
  return checking && state !== NOT_ASKED ? ' (checking again…)' : '';
}
