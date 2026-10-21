import assert from 'node:assert/strict';
import { test } from 'node:test';

import type { ProcessRequest, ProcessResult } from '../process/runner';
import { UNELEVATED_SWITCHES } from '../powershell/unelevatedLauncher';
import { RUN_EXIT } from '../scheduler/runScript';
import type { SchedulerFixture, TaskFixture } from './support/schedulerAnswers';
import { runTasksOp, RUN_CONFIRM, type TasksFlowDeps, type TasksOp, type TasksOutcome } from '../windowsTasks/tasksFlow';
import { PENDING_KEY, readPending } from '../windowsTasks/tasksPending';
import { runNowScript } from '../windowsTasks/tasksScripts';
import { elevatedProgramOf, PS51, PS51_READ, STORE_READ } from './support/engines';
import { isElevatedPowerShell, revealed } from './support/noRealWsl';
import { exited } from './support/recordingRunner';
import { decoded, tasksWorld, type World, type WorldOptions } from './support/tasksWorld';
import { registeredSummary } from './support/windowsTasks';

/**
 * *Run the Windows watch now* / *Archive Windows sessions now* (WR-E4 of PLAN_windows_runner.md §3.8; the story's plan round 1c023671: R1 — the pending
 * record is cleared on ANY answer but a timeout; R2 — an overlap is never silent): the install flow with the stage, the render and the guards taken
 * out and ONE read put in. The REAL flow over the recorder — no daemon call at all, no real PowerShell, no real task.
 */

const WATCH: TasksOp = { kind: 'run', task: 'watch' };
const ARCHIVE: TasksOp = { kind: 'run', task: 'archive' };
const NOW = 1_000_000;

/** A read with the task installed as the golden would install it: present, enabled, idle. */
function installed(task: 'watch' | 'archive', over: Partial<TaskFixture> = {}): SchedulerFixture {
  return { tasks: [{ name: task, summary: registeredSummary(task), ...over }] };
}

async function run(op: TasksOp, options: WorldOptions = {}): Promise<{ readonly w: World; readonly outcome: TasksOutcome }> {
  const w = tasksWorld({ read: installed(op.kind === 'run' ? op.task : 'watch'), ...options });

  return { w, outcome: await runTasksOp(op, w.deps) };
}

function message(w: World, at = 0): string {
  return w.recorder.reports[at]?.message ?? '';
}

function timedOut(treeEnded: boolean): ProcessResult {
  return { kind: 'timedOut', timeoutMs: 60_000, treeEnded, stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) };
}

const SHOWN = (outcome: TasksOutcome): string => `${outcome.kind}: ${JSON.stringify(outcome)}`;

test('run-now starts the person\'s own task and nothing else', async () => {
  for (const op of [WATCH, ARCHIVE]) {
    const task = op.kind === 'run' ? op.task : 'watch';
    const { w, outcome } = await run(op);
    assert.equal(outcome.kind, 'done', SHOWN(outcome));
    assert.equal(outcome.kind === 'done' && outcome.done.op === 'run' ? outcome.done.task : '', task);
    assert.deepEqual(w.events, ['engine', 'confirm', 'reread', `run(pending=run:${task})`, 'query', 'report']);
    assert.equal(w.ps().length, 1, 'a request was made: ' + w.ps().length);
    assert.equal(decoded(w.ps()[0]), runNowScript(task));
    assert.deepEqual(w.daemon(), [], 'a daemon call was made: ' + w.daemon().join(', '));
    assert.equal(w.store.get(PENDING_KEY), undefined);
    assert.match(message(w), new RegExp('The Windows ' + task + ' task was started: Task Scheduler runs .* now, as you, not elevated\\. The panel shows it running until it ends\\. Program: Windows PowerShell 5\\.1'));
    assert.equal(w.recorder.reports[0]?.failed, false);
  }
});

test('a run-now request is never elevated, one program, the fixed switches, the constant script, the tree kill and the ceiling', async () => {
  for (const op of [WATCH, ARCHIVE]) {
    const task = op.kind === 'run' ? op.task : 'watch';
    const { w } = await run(op);
    const request = w.ps()[0] as ProcessRequest;
    assert.equal(isElevatedPowerShell(request.file, request.args), false, 'the request asks for elevation');
    assert.doesNotMatch(revealed(request.args), /runas|-Verb|Start-Process|ProcessStartInfo/i);
    assert.equal(elevatedProgramOf(request), '');
    assert.equal(request.file, PS51);
    assert.deepEqual(request.args.slice(0, 3), [...UNELEVATED_SWITCHES]);
    assert.equal(request.args.length, 4);
    assert.equal(request.killTree, true, 'the request asks for no tree kill');
    assert.equal(request.timeoutMs, 60_000);
    assert.equal(decoded(request), runNowScript(task), 'decoded payload differs from runNowScript');
    assert.doesNotMatch(revealed(request.args), /wsl-care\.exe|--timer|\btasks\b/);
  }
});

test('a run-now makes no daemon call: no stage, no render, no tasks run', async () => {
  for (const op of [WATCH, ARCHIVE]) {
    const { w } = await run(op);
    assert.deepEqual(w.daemon(), [], 'a daemon call was made: ' + w.daemon().join(', '));
  }
});

test('not installed → install first: nothing started, no modal, told once as an error', async () => {
  for (const read of [{ tasks: [] }, { folder: 'absent' as const }]) {
    const { w, outcome } = await run(WATCH, { read });
    assert.equal(outcome.kind, 'notStarted', SHOWN(outcome));
    assert.match(message(w), /install the Windows watch task first/);
    assert.equal(w.ps().length, 0, 'a request was made');
    assert.deepEqual(w.recorder.prompts, []);
    assert.equal(w.store.writes, 0);
    assert.equal(w.recorder.reports.length, 1);
    assert.equal(w.recorder.reports[0]?.failed, true);
  }
});

test('a read that did not answer, an unreadable task and a disabled one start nothing and say why', async () => {
  const unknown = await run(WATCH, { read: { kind: 'unknown', cause: 'failed', reason: 'no answer' } });
  assert.equal(unknown.outcome.kind, 'notStarted');
  assert.match(message(unknown.w), /has not answered what is registered/);
  const unreadable = await run(WATCH, { read: { tasks: [{ name: 'watch', unreadable: '0x80070005' }] } });
  assert.equal(unreadable.outcome.kind, 'notStarted');
  const disabled = await run(WATCH, { read: installed('watch', { enabled: 'False', state: '1' }) });
  assert.equal(disabled.outcome.kind, 'notStarted');
  assert.match(message(disabled.w), /disabled in Task Scheduler — install it again to enable it/);
  for (const { w } of [unknown, unreadable, disabled]) {
    assert.deepEqual([w.ps().length, w.recorder.prompts.length, w.store.writes], [0, 0, 0]);
  }
});

test('R2: a task already running starts nothing and says so — from the read, and from the script at run time', async () => {
  for (const read of [installed('watch', { state: '4' }), installed('watch', { state: '3', lastResult: '267009' })]) {
    const { w, outcome } = await run(WATCH, { read });
    assert.equal(outcome.kind, 'alreadyRunning', SHOWN(outcome));
    assert.equal(w.ps().length, 0, 'a request was made while the task runs');
    assert.equal(w.store.writes, 0);
    assert.equal(w.recorder.reports.length, 1);
    assert.equal(w.recorder.reports[0]?.failed, false, 'an overlap is information, not an error');
    assert.match(message(w), /already running/);
  }
  const late = await run(WATCH, { run: () => Promise.resolve(exited(RUN_EXIT.alreadyRunning, 'state=4\r\n')) });
  assert.equal(late.outcome.kind, 'alreadyRunning', SHOWN(late.outcome));
  assert.equal(late.w.store.get(PENDING_KEY), undefined, 'the record is cleared');
  assert.equal(late.w.recorder.reports[0]?.failed, false);
  assert.match(message(late.w), /already running/);
});

test('R1: the record is cleared on ANY answer but a timeout, and kept to its deadline after one (run)', async () => {
  const endings: readonly (readonly [string, (request: ProcessRequest) => Promise<ProcessResult>])[] = [
    ['exit 0', () => Promise.resolve(exited(0))],
    ['exit 34', () => Promise.resolve(exited(RUN_EXIT.notRegistered))],
    ['exit 35', () => Promise.resolve(exited(RUN_EXIT.alreadyRunning))],
    ['exit 36', () => Promise.resolve(exited(RUN_EXIT.disabled))],
    ['exit 37', () => Promise.resolve(exited(RUN_EXIT.runFailed, 'hresult=0x80041326\r\n', 'boom'))],
    ['exit 1', () => Promise.resolve(exited(1, '', 'boom'))],
    ['exit -1', () => Promise.resolve(exited(4294967295))],
    ['failedToStart', () => Promise.resolve<ProcessResult>({ kind: 'failedToStart', reason: 'ENOENT' })],
    ['a runner that throws', () => Promise.reject(new Error('the runner broke'))],
    ['signalled', () => Promise.resolve<ProcessResult>({ kind: 'signalled', signal: 'SIGKILL', stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) })],
    ['tooMuchOutput', () => Promise.resolve<ProcessResult>({ kind: 'tooMuchOutput', stream: 'stdout', limitBytes: 1 })],
  ];
  for (const [name, answer] of endings) {
    const { w } = await run(WATCH, { run: answer });
    assert.equal(w.store.get(PENDING_KEY), undefined, name + ': an answer left the pending record');
    assert.equal(w.recorder.reports.length, 1, name + ': not told exactly once');
    assert.ok(w.events.includes('query'), name + ': the status was not read again');
  }
  const first = await run(WATCH, { run: () => Promise.resolve(timedOut(true)) });
  assert.equal(first.outcome.kind, 'timedOut');
  assert.deepEqual(readPending(first.w.store, NOW), { op: 'run', task: 'watch', startedAtUtcMs: NOW, deadlineUtcMs: NOW + 2 * 60_000 }, 'a timed-out run did not keep its record');
  const early = tasksWorld({ store: first.w.store, now: NOW + 1, read: installed('watch') });
  assert.equal((await runTasksOp(WATCH, early.deps)).kind, 'busy');
  const atDeadline = tasksWorld({ store: first.w.store, now: NOW + 2 * 60_000, read: installed('watch') });
  assert.equal((await runTasksOp(WATCH, atDeadline.deps)).kind, 'done', 'past its deadline it holds nothing');
});

test('R1: a runner that throws and a store that refuses the clear are still told', async () => {
  const thrown = await run(WATCH, { run: () => Promise.reject(new Error('the runner broke')) });
  assert.equal(thrown.outcome.kind, 'failed');
  assert.match(message(thrown.w), /the runner broke/);
  assert.equal(thrown.w.recorder.reports.length, 1, 'not told exactly once');
  const w = tasksWorld({ read: installed('watch') });
  const failing = { get: (key: string) => w.store.get(key), update: (key: string, value: unknown) => (value === undefined ? Promise.reject(new Error('the store refused')) : w.store.update(key, value)) };
  const deps: TasksFlowDeps = { ...w.deps, durable: failing };
  assert.equal((await runTasksOp(WATCH, deps)).kind, 'done');
  assert.equal(w.recorder.reports.length, 1);
  assert.match(w.logs.join('\n'), /pending record could not be cleared/);
});

test('the pending record is written BEFORE the spawn', async () => {
  const { w } = await run(ARCHIVE);
  assert.ok(w.events.includes('run(pending=run:archive)'), 'order: ' + w.events.join(' → '));
});

test('the modal names the task, what it does, the program and carries the script verbatim; a decline says nothing and runs nothing', async () => {
  const { w } = await run(WATCH);
  const prompt = w.recorder.prompts[0];
  const detail = prompt?.detail ?? '';
  assert.match(prompt?.message ?? '', /Start the Windows watch task now/);
  assert.equal(prompt?.confirm, RUN_CONFIRM);
  assert.equal(RUN_CONFIRM, 'Start it now (one PowerShell, not elevated)');
  assert.ok(detail.includes(runNowScript('watch')), 'the script is in the modal verbatim');
  assert.match(detail, /The program: Windows PowerShell 5\.1/);
  assert.match(detail, /runs AS YOU — not elevated, no UAC prompt — and asks Task Scheduler to start \\wsl-care\\watch exactly as its next trigger would/);
  assert.match(detail, /samples the Windows MCP|the Windows MCP watch/);
  assert.match(detail, /already running nothing new is started/);
  assert.deepEqual(w.recorder.shown, [], 'no document: the script is in the modal');
  const declined = await run(WATCH, { answer: false });
  assert.equal(declined.outcome.kind, 'declined');
  assert.deepEqual([declined.w.ps(), declined.w.recorder.reports, declined.w.store.writes], [[], [], 0]);
});

test('the engine is read before the modal and AGAIN before the launch; a change runs nothing', async () => {
  const { w, outcome } = await run(WATCH, { engine: [STORE_READ, PS51_READ] });
  assert.equal(outcome.kind, 'notRun');
  assert.deepEqual(w.events.filter((e) => e === 'engine' || e === 'reread'), ['engine', 'reread']);
  assert.deepEqual([w.ps().length, w.store.writes], [0, 0], 'a request was made');
});

test('a record another window wrote, and one it wrote while this modal was open, make this run busy — nothing runs', async () => {
  const standing = tasksWorld({ read: installed('watch') });
  await standing.store.update(PENDING_KEY, { op: 'install', task: 'archive', startedAtUtcMs: NOW - 1, deadlineUtcMs: NOW + 100_000 });
  assert.equal((await runTasksOp(WATCH, standing.deps)).kind, 'busy');
  assert.deepEqual(standing.events, ['report'], 'nothing read, shown, asked or run while any window\'s record stands');
  const w = tasksWorld({ read: installed('watch') });
  const deps: TasksFlowDeps = { ...w.deps, ui: { ...w.deps.ui, confirm: async (prompt) => { await w.store.update(PENDING_KEY, { op: 'remove', startedAtUtcMs: NOW - 1, deadlineUtcMs: NOW + 100_000 }); return w.deps.ui.confirm(prompt); } } };
  assert.equal((await runTasksOp(WATCH, deps)).kind, 'busy');
  assert.deepEqual(w.ps(), [], 'a request was made');
  assert.equal(readPending(w.store, NOW)?.op, 'remove', 'the other window\'s record is left as it was');
});

test('exit 37 names Task Scheduler\'s HRESULT and the first stderr line; exit 34 and 36 say what to do', async () => {
  const hard = await run(WATCH, { run: () => Promise.resolve(exited(RUN_EXIT.runFailed, 'state=3\r\nhresult=0x80041326\r\n', 'Exception calling "Run"\r\nsecond')) });
  assert.equal(hard.outcome.kind, 'failed');
  assert.match(message(hard.w), /Task Scheduler did not start the task \(0x80041326\): Exception calling "Run"/);
  const gone = await run(WATCH, { run: () => Promise.resolve(exited(RUN_EXIT.notRegistered, 'state=3\r\n')) });
  assert.match(message(gone.w), /install it first/);
  const off = await run(WATCH, { run: () => Promise.resolve(exited(RUN_EXIT.disabled)) });
  assert.match(message(off.w), /install it again to enable it/);
  for (const { w, outcome } of [hard, gone, off]) {
    assert.equal(outcome.kind, 'failed');
    assert.equal(w.store.get(PENDING_KEY), undefined);
    assert.equal(w.recorder.reports[0]?.failed, true);
  }
});

test('a timeout of the run is told with its ceiling and the tree verdict; a refused start carries the Store note', async () => {
  const slow = await run(WATCH, { run: () => Promise.resolve(timedOut(false)) });
  assert.match(message(slow.w), /The start of the Windows watch task did not finish within 60 s .*could not confirm/);
  const store = await run(WATCH, { engine: [STORE_READ, STORE_READ], run: () => Promise.resolve<ProcessResult>({ kind: 'failedToStart', reason: 'spawn ENOENT' }) });
  assert.match(message(store.w), /Store package could not be started by its package path/);
});
