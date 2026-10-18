import assert from 'node:assert/strict';
import { test } from 'node:test';

import type { ProcessResult } from '../process/runner';
import { NOT_FOUND_HRESULT, QUERY_SCRIPT } from '../windowsTime/guardScripts';
import { parseSchedulerAnswer, schedulerReadOf, taskOf, type SchedulerRead } from '../windowsTasks/schedulerAnswer';
import { MUTATING } from './support/noRealWsl';
import { schedulerStdout, USER_SID } from './support/schedulerAnswers';

/**
 * The ONE Task Scheduler read (WR-E2 of PLAN_windows_runner.md §3.8): the guard's query widened to every task under `\wsl-care`, its
 * answer parsed into one closed read, and each task read out of it on its own — so one task's trouble is that task's, never the
 * whole read's. All pure: every state of the plan's table is a unit test over text.
 */

const SUMMARY = ['principal=S-1-5-21-1-2-3-1001 logon=3 level=0', 'trigger=logon enabled=True delay=PT1M interval=PT5M', 'settings limit=PT20M instances=2 whenAvailable=True battery=False enabled=True', 'action type=0 path=C:\\x\\wsl-care.exe args=tasks run watch --json'];

function readOf(stdout: string): Extract<SchedulerRead, { kind: 'read' }> {
  const read = parseSchedulerAnswer(stdout);
  assert.equal(read.kind, 'read', JSON.stringify(read));

  return read as Extract<SchedulerRead, { kind: 'read' }>;
}

function exited(stdout: string, code = 0): ProcessResult {
  return { kind: 'exited', code, stdout: Buffer.from(stdout), stderr: Buffer.alloc(0) };
}

// ---- one query reads every task under \wsl-care ----

test('one query reads every task under \\wsl-care: the folder is listed, not one task asked for by name — and the script only reads', () => {
  assert.ok(QUERY_SCRIPT.includes("GetFolder('\\wsl-care')"), 'the folder');
  assert.ok(QUERY_SCRIPT.includes('GetTasks(1)'), 'every task of it, hidden ones included');
  assert.ok(QUERY_SCRIPT.includes("'task=' + "), 'each task opens its own section');
  assert.ok(QUERY_SCRIPT.includes("'user=' + "), 'the reader\'s own SID');
  assert.equal(MUTATING.test(QUERY_SCRIPT), false);
});

/** The script's lines, to read the ORDER things are printed in. */
const SCRIPT_LINES = QUERY_SCRIPT.split('\n');

function lineIndex(startsWith: string): number {
  return SCRIPT_LINES.findIndex((line) => line.startsWith(startsWith));
}

test('the Opus own review B2: the stop-event channel is printed ONCE, before the stamp, the folder and every task — the parser files each tag before the first task= into the folder, so a trailing one would be the LAST task\'s', () => {
  const printed = SCRIPT_LINES.flatMap((line, i) => (line === '$channel' ? [i] : []));
  assert.equal(printed.length, 1, 'one place prints it');
  assert.ok(printed[0] !== undefined && printed[0] < lineIndex("if (Test-Path -Path 'HKLM:"), 'before stamp=');
  assert.ok(printed[0] < lineIndex("'folder=present'") && printed[0] < lineIndex('foreach ($task in $tasks)'), 'before the folder and the tasks');
  assert.ok(!QUERY_SCRIPT.includes('; $channel;'), 'not repeated by the folder\'s catch');
  assert.ok(lineIndex("'user=' + ") < lineIndex("'folder=present'"), 'the folder\'s own lines all come before the first task');
});

test('the fixtures print the folder\'s lines in the script\'s real order: channel, stamp, user, folder, then the tasks', () => {
  const lines = schedulerStdout({ tasks: [{ name: 'watch', summary: SUMMARY }] }).split('\r\n');
  assert.deepEqual(lines.slice(0, 4).map((l) => l.split('=')[0]), ['channel', 'stamp', 'user', 'folder']);
  assert.equal(lines[4], 'task=watch');
});

test('the Opus own review M1: GetTasks(1) skips a task this account cannot read, so each EXPECTED task missing from the list is fetched by name — absent only for 0x80070002, else unreadable with the HRESULT', () => {
  assert.ok(QUERY_SCRIPT.includes("foreach ($name in @('watch','archive','windows-time-guard'))"), 'the expected names, from the constants');
  assert.ok(QUERY_SCRIPT.includes('$folder.GetTask($name)'), 'fetched by name');
  assert.match(QUERY_SCRIPT, new RegExp(`\\$_\\.Exception\\.HResult -ne ${NOT_FOUND_HRESULT}\\) \\{ 'task=' \\+ \\$name; 'read=unreadable'`), 'a refusal that is not "not found" is that task\'s unreadable section');
  assert.equal(MUTATING.test(QUERY_SCRIPT), false);
});

test('the Opus own review M1: a task this account cannot read reads UNREADABLE with its HRESULT — not "not installed" — beside one that is simply not there', () => {
  const read = readOf(schedulerStdout({ tasks: [{ name: 'watch', unreadable: '0x80070005' }] }));
  assert.deepEqual(taskOf(read, 'watch'), { kind: 'unreadable', reason: 'Task Scheduler did not let this account read its definition (0x80070005)', hresult: '0x80070005' });
  assert.equal(taskOf(read, 'archive').kind, 'absent');
});

test('the answer holds every task, in the order printed, each with its own tags and summary lines', () => {
  const read = readOf(schedulerStdout({ tasks: [{ name: 'watch', summary: SUMMARY }, { name: 'archive', lastResult: '84', summary: SUMMARY.slice(0, 2) }, { name: 'windows-time-guard', enabled: 'False' }] }));
  assert.deepEqual([...read.sections.keys()], ['watch', 'archive', 'windows-time-guard']);
  assert.equal(read.sections.get('archive')?.tags.get('lastResult'), '84');
  assert.deepEqual(read.sections.get('watch')?.summary, SUMMARY);
  assert.deepEqual(read.sections.get('archive')?.summary, SUMMARY.slice(0, 2));
  assert.equal(read.user, USER_SID);
  assert.equal(read.channel, 'enabled');
});

test('the reader\'s SID, the stop-event channel and a stamp left by a removal are read from the same answer', () => {
  const read = readOf(schedulerStdout({ user: 'S-1-5-21-9-9-9-1000', channel: 'disabled', stamp: 'present' }));
  assert.equal(read.user, 'S-1-5-21-9-9-9-1000');
  assert.equal(read.channel, 'disabled');
  assert.equal(read.stampLeft, true);
  assert.equal(readOf(schedulerStdout({ channel: 'banana' })).channel, 'unknown');
  assert.equal(readOf(schedulerStdout()).stampLeft, false);
});

// ---- plan round 4fc3f328, 1: the folder does not exist on a machine never installed ----

test('plan round 1: a missing \\wsl-care folder (0x80070002) is an EMPTY task set — every task reads absent, nothing fails', () => {
  assert.equal(NOT_FOUND_HRESULT, -2147024894);
  assert.match(QUERY_SCRIPT, new RegExp(`\\$code -eq ${NOT_FOUND_HRESULT}\\) \\{ 'folder=absent' \\}`), 'the folder\'s catch maps the not-found HRESULT to folder=absent');
  const read = readOf(schedulerStdout({ folder: 'absent' }));
  assert.equal(read.folder.kind, 'absent');
  assert.deepEqual(['watch', 'archive', 'windows-time-guard'].map((name) => taskOf(read, name).kind), ['absent', 'absent', 'absent']);
});

test('a folder that is there with no task in it reads absent for every task too', () => {
  const read = readOf(schedulerStdout());
  assert.equal(read.folder.kind, 'present');
  assert.equal(taskOf(read, 'watch').kind, 'absent');
});

// ---- plan round 3: per-task isolation ----

test('plan round 3: a task whose own read failed is unreadable with its HRESULT — the other tasks read on', () => {
  const read = readOf(schedulerStdout({ tasks: [{ name: 'watch', unreadable: '0x80070005' }, { name: 'archive', summary: SUMMARY }] }));
  assert.deepEqual(taskOf(read, 'watch'), { kind: 'unreadable', reason: 'Task Scheduler did not let this account read its definition (0x80070005)', hresult: '0x80070005' });
  assert.equal(taskOf(read, 'archive').kind, 'present');
});

test('plan round 3: a task with an unparseable field is unreadable, naming the field — never its neighbours, never the whole read', () => {
  const read = readOf(schedulerStdout({
    tasks: [
      { name: 'watch', summary: SUMMARY, lastResult: 'banana' },
      { name: 'archive', summary: SUMMARY, enabled: 'maybe' },
      { name: 'windows-time-guard', summary: SUMMARY, lastRunUtc: 'yesterday-ish' },
    ],
  }));
  assert.match((taskOf(read, 'watch') as { reason: string }).reason, /lastResult/);
  assert.match((taskOf(read, 'archive') as { reason: string }).reason, /enabled/);
  assert.match((taskOf(read, 'windows-time-guard') as { reason: string }).reason, /lastRunUtc/);
  assert.deepEqual([taskOf(read, 'watch').kind, taskOf(read, 'archive').kind, taskOf(read, 'windows-time-guard').kind], ['unreadable', 'unreadable', 'unreadable']);
  const good = readOf(schedulerStdout({ tasks: [{ name: 'watch', summary: SUMMARY, lastResult: 'banana' }, { name: 'archive', summary: SUMMARY }] }));
  assert.equal(taskOf(good, 'archive').kind, 'present', 'one bad task leaves the next one readable');
});

test('plan round 3: a task with no summary lines at all (its definition printed nothing) is unreadable, not "installed"', () => {
  const read = readOf(schedulerStdout({ tasks: [{ name: 'watch' }] }));
  assert.match((taskOf(read, 'watch') as { reason: string }).reason, /summary/);
  assert.equal(taskOf(read, 'watch').kind, 'unreadable');
});

test('plan round 3: a hostile summary cannot open a task section — only a line that BEGINS task= does, and a repeated name keeps the first', () => {
  const read = readOf(schedulerStdout({ tasks: [{ name: 'watch', summary: [...SUMMARY, 'action type=0 path=x args=task=archive enabled=False'] }, { name: 'watch', enabled: 'False', summary: SUMMARY }] }));
  assert.deepEqual([...read.sections.keys()], ['watch']);
  assert.equal(read.sections.get('watch')?.tags.get('enabled'), 'True');
  assert.equal(read.sections.get('watch')?.summary.length, SUMMARY.length + 1);
});

// ---- plan round 9: a task that never ran ----

test('plan round 9: an empty or "never" last run, an empty last result and Task Scheduler\'s "not run yet" all read as never run — never undefined', () => {
  const cases: readonly (readonly [string, Record<string, string>])[] = [
    ['never', { lastRunUtc: 'never', lastResult: '267011' }],
    ['empty last run', { lastRunUtc: '', lastResult: '0' }],
    ['empty last result', { lastResult: '' }],
    ['0x41303 with a date', { lastResult: '267011' }],
  ];
  for (const [name, fields] of cases) {
    const task = taskOf(readOf(schedulerStdout({ tasks: [{ name: 'watch', summary: SUMMARY, ...fields }] })), 'watch');
    assert.equal(task.kind, 'present', name);
    assert.deepEqual(task.kind === 'present' ? task.lastRun : undefined, { kind: 'never' }, name);
  }
});

test('a task that ran carries the run\'s instant and the result as the unsigned 32-bit code Task Scheduler means', () => {
  const task = taskOf(readOf(schedulerStdout({ tasks: [{ name: 'watch', summary: SUMMARY, lastRunUtc: '2026-10-16T08:00:00.0000000Z', lastResult: '-2147216609' }] })), 'watch');
  assert.deepEqual(task.kind === 'present' ? task.lastRun : undefined, { kind: 'ran', utc: '2026-10-16T08:00:00.0000000Z', result: 0x8004131f });
});

test('a running task is one Task Scheduler says is running — by its State (4) or by 0x41301', () => {
  const byState = taskOf(readOf(schedulerStdout({ tasks: [{ name: 'watch', summary: SUMMARY, state: '4' }] })), 'watch');
  const byResult = taskOf(readOf(schedulerStdout({ tasks: [{ name: 'watch', summary: SUMMARY, lastResult: '267009' }] })), 'watch');
  const idle = taskOf(readOf(schedulerStdout({ tasks: [{ name: 'watch', summary: SUMMARY }] })), 'watch');
  assert.deepEqual([byState, byResult, idle].map((t) => t.kind === 'present' && t.running), [true, true, false]);
});

// ---- plan round 6: access denied and every other Task Scheduler error is visible ----

test('plan round 6: a folder Task Scheduler will not let this account read is unreadable with the HRESULT, for every task', () => {
  const read = readOf(schedulerStdout({ folder: { unreadable: '0x80070005' } }));
  assert.deepEqual(read.folder, { kind: 'unreadable', hresult: '0x80070005' });
  assert.deepEqual(taskOf(read, 'watch'), { kind: 'unreadable', reason: 'Task Scheduler does not let this account read it (0x80070005) — installed or not cannot be told', hresult: '0x80070005' });
});

test('plan round 6, 8: a query that did not answer is unknown, with its reason — a timeout says it timed out', () => {
  const timedOut = schedulerReadOf({ kind: 'timedOut', timeoutMs: 30_000, treeEnded: false, stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) });
  assert.deepEqual(timedOut, { kind: 'unknown', cause: 'timedOut', timeoutMs: 30_000, treeEnded: false, reason: 'the Task Scheduler query did not answer within 30 s (wslCare.timeouts.windowsTimeGuardQuerySeconds)' });
  assert.deepEqual(schedulerReadOf({ kind: 'failedToStart', reason: 'ENOENT' }), { kind: 'unknown', cause: 'failed', reason: 'the Task Scheduler query could not start: ENOENT' });
  assert.deepEqual(schedulerReadOf(exited('', 1)), { kind: 'unknown', cause: 'failed', reason: 'the Task Scheduler query exited 1' });
  assert.deepEqual(schedulerReadOf({ kind: 'signalled', signal: 'SIGTERM', stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) }), { kind: 'unknown', cause: 'failed', reason: 'the Task Scheduler query was stopped (SIGTERM)' });
  assert.equal(schedulerReadOf(exited(schedulerStdout())).kind, 'read');
});

test('an exit 0 that printed no folder answer is unknown — never read as "nothing installed"', () => {
  assert.deepEqual(schedulerReadOf(exited('')), { kind: 'unknown', cause: 'failed', reason: 'the Task Scheduler query printed no answer' });
  assert.equal(parseSchedulerAnswer('channel=enabled\r\nstamp=absent\r\n').kind, 'unknown');
});

test('a task name is untrusted text: a line break in it cannot add a line, and the name is read as printed', () => {
  const read = readOf(schedulerStdout({ tasks: [{ name: 'a=b', summary: SUMMARY }] }));
  assert.deepEqual([...read.sections.keys()], ['a=b']);
});
