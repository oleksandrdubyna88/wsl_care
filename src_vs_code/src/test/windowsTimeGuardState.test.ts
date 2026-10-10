import assert from 'node:assert/strict';
import { test } from 'node:test';

import type { ProcessResult } from '../process/runner';
import { clearPending, PENDING_KEY, readPending, sweepPending, writePending } from '../windowsTime/guardPending';
import { finishedBy, guardStateOf, guardView, lastResultText, parseGuardAnswer, summariesMatch, type GuardState } from '../windowsTime/guardState';
import { durationSeconds, guardSummary, type GuardOptions } from '../windowsTime/guardTask';
import { MapStore } from './support/memento';

/**
 * What the panel says about the Windows Time guard (PLAN_windows_time_task.md D5, D8): every row of the plan's status table
 * from a tagged query answer, the last result in words, and the pending elevated run that holds both buttons — all pure.
 */

const OPTIONS: GuardOptions = { setAutomaticStart: true, everyHours: 4, minMinutesBetweenStarts: 10, delaySeconds: 60, timeLimitMinutes: 5 };
const LOCAL = (iso: string): string => `<${iso}>`;

function answer(lines: readonly string[]): string {
  return `${lines.join('\r\n')}\r\n`;
}

function presentAnswer(overrides: { enabled?: string; lastRunUtc?: string; lastResult?: string; summary?: readonly string[]; channel?: string } = {}): string {
  return answer([
    'guard=present',
    `enabled=${overrides.enabled ?? 'True'}`,
    `lastRunUtc=${overrides.lastRunUtc ?? '2026-10-08T09:43:20.0000000Z'}`,
    `lastResult=${overrides.lastResult ?? '0'}`,
    ...(overrides.summary ?? guardSummary(OPTIONS)).map((l) => `summary:${l}`),
    `channel=${overrides.channel ?? 'enabled'}`,
  ]);
}

function exited(stdout: string, code = 0): ProcessResult {
  return { kind: 'exited', code, stdout: Buffer.from(stdout), stderr: Buffer.alloc(0) };
}

function viewOf(state: GuardState, options: GuardOptions = OPTIONS): ReturnType<typeof guardView> {
  return guardView(state, options, undefined, LOCAL);
}

test('the query\'s answers become one closed state each — never a guess', () => {
  assert.deepEqual(parseGuardAnswer(answer(['guard=absent', 'channel=enabled'])), { kind: 'absent', channel: 'enabled' });
  assert.deepEqual(parseGuardAnswer(answer(['guard=unreadable', 'hresult=0x80070005', 'channel=disabled'])), { kind: 'unreadable', hresult: '0x80070005', channel: 'disabled' });
  const present = parseGuardAnswer(presentAnswer({ lastResult: '-2147216609' }));
  assert.equal(present.kind, 'present');
  assert.equal(present.kind === 'present' ? present.lastResult : 0, 0x8004131f, 'a negative HRESULT is read as the unsigned code Task Scheduler means');
  assert.deepEqual(present.kind === 'present' ? present.summary : [], guardSummary(OPTIONS));
  assert.equal(parseGuardAnswer('').kind, 'unknown');
  assert.equal(parseGuardAnswer(answer(['guard=present', 'enabled=True'])).kind, 'unknown', 'present without a last run is not read as installed');
  assert.equal(parseGuardAnswer(answer(['guard=absent'])).kind === 'absent' && (parseGuardAnswer(answer(['guard=absent'])) as { channel: string }).channel, 'unknown');
});

test('a query that did not answer is unknown, with its reason', () => {
  assert.match(JSON.stringify(guardStateOf({ kind: 'timedOut', timeoutMs: 30_000, stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) })), /did not answer within 30 s \(wslCare\.timeouts\.windowsTimeGuardQuerySeconds\)/);
  assert.match(JSON.stringify(guardStateOf({ kind: 'failedToStart', reason: 'ENOENT' })), /could not start: ENOENT/);
  assert.match(JSON.stringify(guardStateOf(exited('guard=absent', 1))), /exited 1/);
  assert.deepEqual(guardStateOf(exited('guard=absent\r\nchannel=enabled\r\n')), { kind: 'absent', channel: 'enabled' });
});

test('the panel line per row of the status table (D5): not installed → Install; installed as these settings → Remove', () => {
  const absent = viewOf({ kind: 'absent', channel: 'enabled' });
  assert.match(absent.line, /^Windows Time guard: not installed/);
  assert.deepEqual(absent.buttons.map((b) => [b.id, b.enabled]), [['installWindowsTimeGuard', true]]);

  const current = viewOf(parseGuardAnswer(presentAnswer()));
  assert.equal(current.line, 'Windows Time guard: installed — last run <2026-10-08T09:43:20.0000000Z>: the Windows Time service runs and the clock was resynchronised');
  assert.equal(current.level, 'ok');
  assert.deepEqual(current.buttons.map((b) => b.id), ['removeWindowsTimeGuard']);
  assert.match(viewOf(parseGuardAnswer(presentAnswer({ lastRunUtc: 'never', lastResult: '267011' }))).line, /installed — not run yet$/);
});

test('installed, but not as the current settings would install it — any baked-in setting changed, or an edit in Task Scheduler', () => {
  const registered = parseGuardAnswer(presentAnswer());
  for (const changed of [{ everyHours: 5 }, { minMinutesBetweenStarts: 11 }, { delaySeconds: 0 }, { timeLimitMinutes: 6 }, { setAutomaticStart: false }] as const) {
    const view = viewOf(registered, { ...OPTIONS, ...changed });
    assert.match(view.line, /not as the current settings would install it/, JSON.stringify(changed));
    assert.equal(view.level, 'warn');
    assert.deepEqual(view.buttons.map((b) => b.id), ['installWindowsTimeGuard', 'removeWindowsTimeGuard']);
  }
  const edited = guardSummary(OPTIONS).map((l) => (l.startsWith('principal=') ? 'principal=S-1-5-21-1-1001 logon=3 level=0' : l));
  assert.match(viewOf(parseGuardAnswer(presentAnswer({ summary: edited }))).line, /not as the current settings would install it/, 'codex c5: a principal changed in Task Scheduler is not "this version"');
});

/** The summary as SUMMARY_FUNCTION printed it over the REGISTERED task on the owner's machine (2026-10-10, AI OS Care 0.3.0):
 * Task Scheduler stores the trigger delay `PT60S` as `PT1M`. Its in-memory parse of the XML does not normalise, which is why the
 * Windows-leg test passed while every real install read as "not as the current settings would install it". */
function registeredForm(options: GuardOptions = OPTIONS): readonly string[] {
  return guardSummary(options).map((l) => l.replaceAll('delay=PT60S', 'delay=PT1M'));
}

test('a guard Task Scheduler registered — its delay stored as PT1M — reads as installed, not as "install it again" (2026-10-10)', () => {
  assert.ok(registeredForm().some((l) => l.includes('delay=PT1M')), 'the fixture carries the measured spelling');
  const view = viewOf(parseGuardAnswer(presentAnswer({ summary: registeredForm() })));
  assert.equal(view.line, 'Windows Time guard: installed — last run <2026-10-08T09:43:20.0000000Z>: the Windows Time service runs and the clock was resynchronised');
  assert.equal(view.level, 'ok');
  assert.deepEqual(view.buttons.map((b) => b.id), ['removeWindowsTimeGuard']);
});

test('an install that ends with the registered form completes the pending run', () => {
  assert.equal(finishedBy(parseGuardAnswer(presentAnswer({ summary: registeredForm() })), { op: 'install' }, OPTIONS), true);
});

test('a duration reads as its length in seconds; anything else is no duration', () => {
  assert.equal(durationSeconds('PT60S'), 60);
  assert.equal(durationSeconds('PT1M'), 60);
  assert.equal(durationSeconds('PT1M30S'), 90);
  assert.equal(durationSeconds('PT1H'), 3600);
  assert.equal(durationSeconds('PT3600S'), 3600);
  assert.equal(durationSeconds('P1D'), 86400);
  assert.equal(durationSeconds('P1DT1H'), 90000);
  for (const none of ['', 'PT', 'P', '60', 'PT1Y', 'PT-1M', 'pt1m', 'PT1M ', 'PT1.5M']) {
    assert.equal(durationSeconds(none), undefined, JSON.stringify(none));
  }
});

test('durations compare by value only in the delay, interval and limit fields; a real difference still differs', () => {
  const registered = parseGuardAnswer(presentAnswer({ summary: registeredForm() }));
  for (const changed of [{ delaySeconds: 120 }, { delaySeconds: 0 }, { everyHours: 5 }, { timeLimitMinutes: 6 }] as const) {
    assert.match(viewOf(registered, { ...OPTIONS, ...changed }).line, /not as the current settings would install it/, JSON.stringify(changed));
  }
  const interval = registeredForm().map((l) => l.replace('interval=PT4H', 'interval=PT240M').replace('limit=PT5M', 'limit=PT300S'));
  assert.match(viewOf(parseGuardAnswer(presentAnswer({ summary: interval }))).line, /: installed — /, 'PT240M is PT4H, PT300S is PT5M');
  // coai plan round 990e7d9a: a duration-like text OUTSIDE those fields is compared byte for byte.
  const action = guardSummary(OPTIONS).map((l) => (l.startsWith('action ') ? `${l} -Wait PT60S` : l));
  const respelled = guardSummary(OPTIONS).map((l) => (l.startsWith('action ') ? `${l} -Wait PT1M` : l));
  assert.equal(summariesMatch(action, respelled), false, 'an action argument respelled is an edited action');
  const inSubscription = guardSummary(OPTIONS).map((l) => (l.includes('subscription=') ? `${l} delay=PT60S` : l));
  const subscriptionRespelled = guardSummary(OPTIONS).map((l) => (l.includes('subscription=') ? `${l} delay=PT1M` : l));
  assert.equal(summariesMatch(inSubscription, subscriptionRespelled), false, 'only the trigger\'s own delay field is a duration — text in the subscription is compared as text');
  assert.equal(summariesMatch(registeredForm(), guardSummary(OPTIONS)), true);
  // Code round: a structured token added before the delay keeps the delay a duration field (the line's schema, not a count).
  const widened = (lines: readonly string[]): string[] => lines.map((l) => l.replace('trigger=boot enabled=True', 'trigger=boot source=x enabled=True'));
  assert.equal(summariesMatch(widened(registeredForm()), widened(guardSummary(OPTIONS))), true);
});

test('disabled, unreadable, unknown and a failing last run each say so', () => {
  assert.match(viewOf(parseGuardAnswer(presentAnswer({ enabled: 'False' }))).line, /disabled in Task Scheduler/);
  const unreadable = viewOf({ kind: 'unreadable', hresult: '0x80070005', channel: 'enabled' });
  assert.match(unreadable.line, /does not let this account read it \(0x80070005\)/);
  assert.deepEqual(unreadable.buttons.map((b) => b.id), ['installWindowsTimeGuard', 'removeWindowsTimeGuard']);
  const unknown = viewOf({ kind: 'unknown', reason: 'SystemRoot does not name a drive folder' });
  assert.equal(unknown.line, 'Windows Time guard: unknown — SystemRoot does not name a drive folder');
  assert.deepEqual(unknown.buttons, [], 'nothing to press when nothing is known');
  const failing = viewOf(parseGuardAnswer(presentAnswer({ lastResult: '11' })));
  assert.equal(failing.level, 'warn');
  assert.match(failing.line, /could not be started/);
  assert.equal(viewOf(parseGuardAnswer(presentAnswer({ lastResult: '20' }))).level, 'ok', 'rate-limited is the guard working, not failing');
});

test('a guard installed by the previous version — four triggers, no start-type one — reads as "install it again", never as current', () => {
  const fourTriggers = guardSummary(OPTIONS).filter((l) => !l.includes('EventID=7040'));
  assert.equal(fourTriggers.length, guardSummary(OPTIONS).length - 1, 'the current summary carries exactly one start-type trigger line');
  const view = viewOf(parseGuardAnswer(presentAnswer({ summary: fourTriggers })));
  assert.match(view.line, /installed, but not as the current settings would install it .* install it again to update it$/);
  assert.equal(view.level, 'warn');
  assert.deepEqual(view.buttons.map((b) => b.id), ['installWindowsTimeGuard', 'removeWindowsTimeGuard']);
});

test('a disabled stop-event channel is named on the line (gemini g3: reported, never changed) — a start-type change is still caught', () => {
  assert.match(viewOf(parseGuardAnswer(presentAnswer({ channel: 'disabled' }))).line, /stop event is not logged on this machine, so a stop is caught by the timed, boot and logon runs only$/);
  assert.doesNotMatch(viewOf(parseGuardAnswer(presentAnswer())).line, /not logged/);
});

test('the last result in words: the guard\'s exits, Task Scheduler\'s own codes, or the bare code', () => {
  assert.match(lastResultText(20), /minMinutesBetweenStarts/);
  assert.match(lastResultText(21), /stamp/);
  assert.match(lastResultText(22), /does not exist/);
  assert.match(lastResultText(12), /failed three times/);
  assert.equal(lastResultText(0x41306), 'stopped by its time limit (wslCare.windowsTime.guard.timeLimitMinutes)');
  assert.equal(lastResultText(0x41301), 'running now');
  assert.equal(lastResultText(0x80070005), 'result 0x80070005');
  assert.equal(lastResultText(7), 'exit 7');
});

test('a pending elevated run holds BOTH buttons, whatever Task Scheduler said, and says what it waits for', () => {
  const view = guardView({ kind: 'absent', channel: 'enabled' }, OPTIONS, { op: 'install' }, LOCAL);
  assert.match(view.line, /Waiting for the elevated PowerShell that installs it/);
  assert.deepEqual(view.buttons.map((b) => [b.id, b.enabled]), [['installWindowsTimeGuard', false], ['removeWindowsTimeGuard', false]]);
});

test('the pending run is durable, bounded by its deadline, and a malformed or expired record is swept', async () => {
  const store = new MapStore();
  assert.equal(readPending(store, 1_000), undefined);
  await writePending(store, { op: 'remove', startedAtUtcMs: 1_000, deadlineUtcMs: 5_000 });
  assert.deepEqual(readPending(store, 4_999), { op: 'remove', startedAtUtcMs: 1_000, deadlineUtcMs: 5_000 });
  assert.equal(readPending(store, 5_000), undefined, 'past its deadline it no longer holds anything');
  await sweepPending(store, 4_000);
  assert.notEqual(store.get(PENDING_KEY), undefined, 'a standing record is not swept');
  await sweepPending(store, 6_000);
  assert.equal(store.get(PENDING_KEY), undefined);
  await store.update(PENDING_KEY, { op: 'format-disk', startedAtUtcMs: 0, deadlineUtcMs: 9e15 });
  assert.equal(readPending(store, 1), undefined, 'an unknown op is no op');
  await sweepPending(store, 1);
  assert.equal(store.get(PENDING_KEY), undefined);
  await writePending(store, { op: 'install', startedAtUtcMs: 1, deadlineUtcMs: 2 });
  await clearPending(store);
  assert.equal(store.get(PENDING_KEY), undefined);
});

test('codex: while Task Scheduler is asked again, a line already shown says so — and the first read is just "checking…"', () => {
  assert.match(guardView({ kind: 'absent', channel: 'enabled' }, OPTIONS, undefined, LOCAL, true).line, /not installed.*\(checking again…\)$/);
  assert.doesNotMatch(guardView({ kind: 'absent', channel: 'enabled' }, OPTIONS, undefined, LOCAL, false).line, /checking/);
});
