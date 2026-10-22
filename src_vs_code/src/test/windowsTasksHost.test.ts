import assert from 'node:assert/strict';
import { test } from 'node:test';

import type { TaskName } from '../client/verbs';
import { parsePageMessage } from '../panel/messages';
import type { ProcessRequest, ProcessResult } from '../process/runner';
import { schedulerReadOf } from '../scheduler/schedulerAnswer';
import { SchedulerHost } from '../scheduler/schedulerHost';
import { PENDING_KEY, readPending, sweepPending } from '../windowsTasks/tasksPending';
import { TasksHost, type TasksHostDeps } from '../windowsTasks/tasksHost';
import { finishedBy, windowsTasksState, type CopyState, type TaskRow, type WindowsTasksState } from '../windowsTasks/tasksState';
import { windowsTasksView } from '../windowsTasks/tasksView';
import { QUERY_SCRIPT } from '../windowsTime/guardScripts';
import { exited } from './support/recordingRunner';
import { schedulerStdout, type SchedulerFixture, type TaskFixture } from './support/schedulerAnswers';
import { quietTasks } from './support/tasksDeps';
import { tasksWorld, type WorldOptions } from './support/tasksWorld';
import { BUNDLED_EXE_FILE, renderAnswerOf, STAGED_DIGEST } from './support/windowsTasks';
import { MapStore } from './support/memento';

/**
 * The Windows tasks' host after WR-E3 (PLAN_windows_runner.md §3.8): ONE flow at a time in this window, every button held by a persisted pending
 * run in EVERY window and across a reload, the part re-read and re-probed after a flow answers, a pending run that Task Scheduler already shows
 * finished settled on the next read, and a stale record swept. The surfaces are the product's own Test-mode recorder; no PowerShell starts.
 */

const ENV = { SystemRoot: 'C:\\Windows' } as const;
const NOW = 1_000_000;

/** What Task Scheduler reads back of a registered task: the render's summary, the delay as Task Scheduler spells it (`PT60S` → `PT1M`). */
function registered(task: TaskName): readonly string[] {
  return renderAnswerOf(task + ' from the staged copy', task).summary.map((line) => line.replaceAll('delay=PT60S', 'delay=PT1M'));
}

interface Options {
  readonly read?: SchedulerFixture | (() => SchedulerFixture);
  readonly store?: MapStore;
  readonly now?: () => number;
  readonly world?: WorldOptions;
  readonly over?: Partial<TasksHostDeps>;
}

function hostOf(options: Options = {}) {
  const world = tasksWorld(options.world);
  const queries: ProcessRequest[] = [];
  const readNow = (): SchedulerFixture => (typeof options.read === 'function' ? options.read() : options.read ?? { tasks: [] });
  const answer = (): ProcessResult => exited(0, schedulerStdout(readNow()));
  const scheduler = new SchedulerHost({ env: ENV, queryTimeoutMs: () => 30_000, script: QUERY_SCRIPT, query: (request) => { queries.push(request); return Promise.resolve(answer()); } });
  const quiet = quietTasks(undefined, options.now ?? (() => NOW));
  quiet.recorder.answer = true;
  const store = options.store ?? quiet.store;
  const host = new TasksHost({ scheduler, client: world.client, pin: '0.4.0', cleanupSince: '0.1.0', formatInstant: (iso) => iso, ...quiet.deps, durable: store, ...options.over });

  return { host, scheduler, queries, world, quiet, store };
}

/** Lets every detached continuation (a store write is a later turn) finish. */
async function settle(): Promise<void> {
  for (let i = 0; i < 8; i += 1) {
    await new Promise<void>((resolve) => { setImmediate(resolve); });
  }
}

async function ready(h: ReturnType<typeof hostOf>): Promise<void> {
  await Promise.all([h.scheduler.refresh(), h.host.refresh()]);
  await settle();
}

const ENABLED = (h: ReturnType<typeof hostOf>): boolean[] => h.host.view().buttons.map((b) => b.enabled);

test('a pending run holds every button across a reload — and past its deadline they are enabled again', async () => {
  const store = new MapStore();
  await store.update(PENDING_KEY, { op: 'install', task: 'archive', startedAtUtcMs: NOW - 10, deadlineUtcMs: NOW + 120_000 });
  const reloaded = hostOf({ store });
  await ready(reloaded);
  const view = reloaded.host.view();
  assert.deepEqual(view.buttons.map((b) => b.id), ['installWindowsWatchTask', 'installWindowsArchiveTask', 'removeWindowsTasks', 'runWindowsWatchTask', 'runWindowsArchiveTask', 'installWindowsCleanupTask', 'previewWindowsTempCleanup', 'runWindowsCleanupTask']);
  assert.deepEqual(ENABLED(reloaded), [false, false, false, false, false, false, false, false], 'expected false, got true');
  assert.equal(view.copy.line, 'Waiting for the PowerShell that installs the Windows archive task…');
  assert.equal(await reloaded.host.run({ kind: 'remove' }).then((o) => o?.kind), 'busy');
  assert.deepEqual(reloaded.world.ps(), []);
  assert.equal(reloaded.quiet.recorder.requests.length, 0, 'no request was made');

  let now = NOW;
  const later = hostOf({ store, now: () => now });
  await ready(later);
  assert.deepEqual(ENABLED(later), [false, false, false, false, false, false, false, false]);
  now = NOW + 120_000;
  assert.deepEqual(ENABLED(later), [true, true, true, true, true, true, true, true], 'past the deadline the record holds nothing');
  const removal = new MapStore();
  await removal.update(PENDING_KEY, { op: 'remove', startedAtUtcMs: NOW - 10, deadlineUtcMs: NOW + 1 });
  const removing = hostOf({ store: removal });
  await ready(removing);
  assert.equal(removing.host.view().copy.line, 'Waiting for the PowerShell that removes the Windows tasks…');
});

test('the buttons by state: install needs a known copy, remove needs something to remove', async () => {
  const staged: CopyState = { kind: 'staged', digest: STAGED_DIGEST, renders: {} };
  const none: Record<TaskName, TaskRow> = { watch: { kind: 'notInstalled' }, archive: { kind: 'notInstalled' }, cleanup: { kind: 'notInstalled' } };
  const installed: TaskRow = { kind: 'installed', lastRun: { kind: 'never' } };
  const table: readonly (readonly [string, CopyState, Record<TaskName, TaskRow>, boolean[]])[] = [
    ['off Windows: no part at all', { kind: 'unavailable' }, none, []],
    ['probing the copy', { kind: 'checking' }, none, [false, false, false, true, true, false, false, true]],
    ['the digest is unknown', { kind: 'digestUnreadable', reason: 'x' }, none, [false, false, false, true, true, false, false, true]],
    ['nothing staged, nothing registered', { kind: 'notStaged', digest: STAGED_DIGEST }, none, [true, true, false, true, true, true, true, true]],
    ['a staged copy, nothing registered: its removal deletes the copy', staged, none, [true, true, true, true, true, true, true, true]],
    ['an unreadable copy may be there to remove', { kind: 'unreadable', digest: STAGED_DIGEST, reason: 'x' }, none, [true, true, true, true, true, true, true, true]],
    ['a registered task', { kind: 'notStaged', digest: STAGED_DIGEST }, { watch: installed, archive: { kind: 'notInstalled' }, cleanup: { kind: 'notInstalled' } }, [true, true, true, true, true, true, true, true]],
    ['a registered task whose read is unknown does not count', { kind: 'notStaged', digest: STAGED_DIGEST }, { watch: { kind: 'unknown', reason: 'x' }, archive: { kind: 'checking' }, cleanup: { kind: 'notInstalled' } }, [true, true, false, false, false, true, true, true]],
  ];
  for (const [name, copy, tasks, expected] of table) {
    const view = windowsTasksView({ copy, tasks, dry: '' }, '0.4.0', (iso) => iso);
    assert.deepEqual(view.buttons.map((b) => b.enabled), expected, name);
  }
  const held = windowsTasksView({ copy: staged, tasks: none, dry: '' }, '0.4.0', (iso) => iso, { busy: true });
  assert.deepEqual(held.buttons.map((b) => b.enabled), [false, false, false, false, false, false, false, false], 'a flow running in this window holds them too');
});

test('the host runs one op at a time, greys the buttons while it runs, and re-reads AND re-probes afterwards', async () => {
  let release: (yes: boolean) => void = () => undefined;
  const gate = new Promise<boolean>((resolve) => { release = resolve; });
  const quiet = quietTasks();
  const h = hostOf({ over: { ui: { ...quiet.deps.ui, confirm: async () => gate } } });
  await ready(h);
  assert.deepEqual(ENABLED(h), [true, true, true, true, true, true, true, true]);
  const first = h.host.run({ kind: 'install', task: 'watch' });
  await settle();
  assert.deepEqual(ENABLED(h), [false, false, false, false, false, false, false, false], 'greyed while the modal is open');
  assert.equal(await h.host.run({ kind: 'install', task: 'archive' }), undefined, 'a second press while one runs starts nothing');
  release(false);
  assert.equal((await first)?.kind, 'declined');
  await settle();
  assert.deepEqual(ENABLED(h), [true, true, true, true, true, true, true, true]);

  const done = hostOf();
  await ready(done);
  const reads = done.queries.length;
  const renders = done.world.daemon().filter((x) => x.includes('render')).length;
  assert.equal((await done.host.run({ kind: 'install', task: 'watch' }))?.kind, 'done');
  await settle();
  assert.equal(done.queries.length, reads + 1, 'one Task Scheduler read after the answer');
  assert.equal(done.world.daemon().filter((x) => x.includes('render')).length, renders + 4, "the flow render, then the probe's three (watch, archive, cleanup)");
});

test('finding 4: an install settles only on ITS task\'s summary — the archive install is not settled by the watch\'s', () => {
  const answers = { watch: renderAnswerOf('watch from the staged copy', 'watch'), archive: renderAnswerOf('archive from the staged copy', 'archive') };
  const copy: CopyState = { kind: 'staged', digest: STAGED_DIGEST, renders: { watch: { kind: 'registrable', answer: answers.watch }, archive: { kind: 'registrable', answer: answers.archive } } };
  const stateOf = (tasks: TaskFixture[], c: CopyState = copy) => windowsTasksState(schedulerReadOf(exited(0, schedulerStdout({ tasks }))), c);
  const pending = { op: 'install', task: 'archive' } as const;
  assert.equal(finishedBy(stateOf([{ name: 'archive', summary: registered('watch') }]), pending), false, 'the archive install settled on the watch\'s summary');
  assert.equal(finishedBy(stateOf([{ name: 'archive', summary: registered('archive') }]), pending), true);
  assert.equal(finishedBy(stateOf([{ name: 'watch', summary: registered('watch') }]), pending), false, 'the other task being present settles nothing');
  assert.equal(finishedBy(stateOf([{ name: 'watch', summary: registered('watch') }]), { op: 'install', task: 'watch' }), true);
  assert.equal(finishedBy(stateOf([{ name: 'archive', summary: registered('archive'), state: '4' }]), pending), false, 'a running task is not compared');
  assert.equal(finishedBy(stateOf([{ name: 'archive', summary: registered('archive') }], { kind: 'notStaged', digest: STAGED_DIGEST }), pending), false, 'without a staged copy there is nothing to compare with');
});

test('a pending removal is settled only when no task is left and no copy is staged', () => {
  const none = (copy: CopyState, tasks: TaskFixture[] = []) => windowsTasksState(schedulerReadOf(exited(0, schedulerStdout({ tasks }))), copy);
  const gone: CopyState = { kind: 'notStaged', digest: STAGED_DIGEST };
  assert.equal(finishedBy(none(gone), { op: 'remove' }), true);
  assert.equal(finishedBy(none(gone, [{ name: 'watch', summary: [] }]), { op: 'remove' }), false, 'a task still present');
  assert.equal(finishedBy(none({ kind: 'staged', digest: STAGED_DIGEST, renders: {} }), { op: 'remove' }), false, 'a copy still staged');
  assert.equal(finishedBy(windowsTasksState({ kind: 'unknown', cause: 'failed', reason: 'x' }, gone), { op: 'remove' }), false, 'an unanswered read settles nothing');
});

test('a pending install Task Scheduler already shows finished is settled on the next read; a pending removal with a task present is not', async () => {
  const store = new MapStore();
  await store.update(PENDING_KEY, { op: 'install', task: 'watch', startedAtUtcMs: NOW - 70_000, deadlineUtcMs: NOW + 50_000 });
  const finished = hostOf({ store, read: { tasks: [{ name: 'watch', summary: registered('watch') }] } });
  await ready(finished);
  assert.equal(store.get(PENDING_KEY), undefined, 'the record Task Scheduler shows finished was waited out');
  assert.deepEqual(ENABLED(finished), [true, true, true, true, true, true, true, true]);

  const removal = new MapStore();
  await removal.update(PENDING_KEY, { op: 'remove', startedAtUtcMs: NOW - 10, deadlineUtcMs: NOW + 120_000 });
  const stuck = hostOf({ store: removal, read: { tasks: [{ name: 'watch', summary: registered('watch') }] } });
  await ready(stuck);
  assert.equal(readPending(removal, NOW)?.op, 'remove');
});

test('activation sweeps a stale record — past its deadline, or malformed — and leaves a standing one', async () => {
  const stale = new MapStore();
  await stale.update(PENDING_KEY, { op: 'remove', startedAtUtcMs: NOW - 300_000, deadlineUtcMs: NOW - 1 });
  await sweepPending(stale, NOW);
  assert.equal(stale.get(PENDING_KEY), undefined);
  for (const bad of [{ op: 'install', task: 'guard', startedAtUtcMs: 1, deadlineUtcMs: NOW + 5 }, { op: 'zap', startedAtUtcMs: 1, deadlineUtcMs: NOW + 5 }, 'text', { op: 'remove' }]) {
    const store = new MapStore();
    await store.update(PENDING_KEY, bad);
    await sweepPending(store, NOW);
    assert.equal(store.get(PENDING_KEY), undefined, JSON.stringify(bad));
  }
  const standing = new MapStore();
  const record = { op: 'install', task: 'watch', startedAtUtcMs: NOW - 1, deadlineUtcMs: NOW + 5 };
  await standing.update(PENDING_KEY, record);
  await sweepPending(standing, NOW);
  assert.deepEqual(standing.get(PENDING_KEY), record);
});

test('the page sends the three buttons as BARE ids: a value beside the type is dropped', () => {
  for (const type of ['installWindowsWatchTask', 'installWindowsArchiveTask', 'removeWindowsTasks']) {
    assert.deepEqual(parsePageMessage({ type }), { type });
    assert.equal(parsePageMessage({ type, xml: '<Task/>' }), undefined);
    assert.equal(parsePageMessage({ type, task: 'watch' }), undefined);
  }
});

test('the ceiling is read at every call: a changed setting gives the next op another timeout', async () => {
  let seconds = 60;
  const h = hostOf({ over: { opTimeoutMs: () => seconds * 1000 } });
  await ready(h);
  await h.host.run({ kind: 'install', task: 'watch' });
  seconds = 90;
  await h.host.run({ kind: 'install', task: 'watch' });
  assert.deepEqual(h.quiet.recorder.requests.map((r) => r.timeoutMs), [60_000, 90_000]);
});

test('Fable major 2: a record another window\'s live PowerShell holds is NOT settled early from the read — only once its ceiling has passed', async () => {
  const store = new MapStore();
  // Window A confirmed an install of an ALREADY installed task: its PowerShell runs (ceiling 60 s, record deadline 120 s) while the read still shows the old task.
  await store.update(PENDING_KEY, { op: 'install', task: 'watch', startedAtUtcMs: NOW, deadlineUtcMs: NOW + 120_000 });
  let now = NOW + 1_000;
  const b = hostOf({ store, now: () => now, read: { tasks: [{ name: 'watch', summary: registered('watch') }] } });
  await ready(b);
  assert.equal(readPending(store, now)?.op, 'install', 'window B settled window A\'s live record');
  assert.deepEqual(ENABLED(b), [false, false, false, false, false, false, false, false], 'window B\'s buttons are enabled while A\'s PowerShell may still run');
  assert.equal(await b.host.run({ kind: 'remove' }).then((o) => o?.kind), 'busy', 'B\'s Remove could run beside A\'s Register');
  now = NOW + 59_999;
  await b.scheduler.refresh();
  await settle();
  assert.equal(readPending(store, now)?.op, 'install', 'one millisecond before the ceiling it still holds');
  now = NOW + 60_000;
  await b.scheduler.refresh();
  await settle();
  assert.equal(store.get(PENDING_KEY), undefined, 'past the ceiling the read settles it');
  assert.deepEqual(ENABLED(b), [true, true, true, true, true, true, true, true]);
});

// ---- WR-E4: the running state, the held buttons and the settle of a run-now ----

const RUN_IDS = ['runWindowsWatchTask', 'runWindowsArchiveTask', 'runWindowsCleanupTask'];

test('a running task reads running in every window — the truth is the read, not a flag — and the run buttons say "already running" on a press', async () => {
  const running: SchedulerFixture = { tasks: [{ name: 'watch', summary: registered('watch'), state: '4', lastResult: '267009' }] };
  let current: SchedulerFixture = running;
  const hosts = [hostOf({ read: () => current }), hostOf({ read: () => current }), hostOf({ read: () => current, store: new MapStore() })];
  for (const h of hosts) {
    await ready(h);
    assert.equal(h.host.view().tasks[0]?.line, 'Windows watch task: installed, running (0x41301)');
    assert.deepEqual(h.host.view().buttons.filter((b) => RUN_IDS.includes(b.id)).map((b) => b.enabled), [true, true, true]);
  }
  const pressed = await hosts[0]?.host.run({ kind: 'run', task: 'watch' });
  assert.equal(pressed?.kind, 'alreadyRunning');
  assert.equal(hosts[0]?.quiet.recorder.requests.length, 0, 'no request');
  current = { tasks: [{ name: 'watch', summary: registered('watch'), state: '3', lastResult: '0', lastRunUtc: '2026-10-18T09:00:00.0000000Z' }] };
  for (const h of hosts) {
    await h.scheduler.refresh();
    await settle();
    assert.match(h.host.view().tasks[0]?.line ?? '', /^Windows watch task: installed, last run .*: succeeded$/);
  }
});

test('a pending run-now holds every button across a reload and shows the waiting line', async () => {
  const store = new MapStore();
  await store.update(PENDING_KEY, { op: 'run', task: 'watch', startedAtUtcMs: NOW - 10, deadlineUtcMs: NOW + 120_000 });
  let now = NOW;
  const reloaded = hostOf({ store, now: () => now, read: { tasks: [{ name: 'watch', summary: registered('watch') }] } });
  await ready(reloaded);
  assert.deepEqual(ENABLED(reloaded), [false, false, false, false, false, false, false, false], 'expected false, got true');
  assert.equal(reloaded.host.view().copy.line, 'Waiting for the PowerShell that starts the Windows watch task…');
  assert.equal(await reloaded.host.run({ kind: 'install', task: 'archive' }).then((o) => o?.kind), 'busy');
  assert.equal(await reloaded.host.run({ kind: 'run', task: 'archive' }).then((o) => o?.kind), 'busy');
  now = NOW + 120_000;
  assert.deepEqual(ENABLED(reloaded), [true, true, true, true, true, true, true, true], 'past the deadline they are enabled again');
});

test('a run record is settled from the read only past its ceiling, and only when the task is running or ran after the record started', async () => {
  const store = new MapStore();
  await store.update(PENDING_KEY, { op: 'run', task: 'watch', startedAtUtcMs: NOW, deadlineUtcMs: NOW + 120_000 });
  let now = NOW + 1_000;
  const b = hostOf({ store, now: () => now, read: { tasks: [{ name: 'watch', summary: registered('watch'), state: '4', lastResult: '267009' }] } });
  await ready(b);
  assert.equal(readPending(store, now)?.op, 'run', 'window B settled window A\'s live run');
  assert.deepEqual(ENABLED(b), [false, false, false, false, false, false, false, false]);
  now = NOW + 60_000;
  await b.scheduler.refresh();
  await settle();
  assert.equal(store.get(PENDING_KEY), undefined, 'past the ceiling the read settles it');
});

test('finishedBy for a run record: running, or ran since the record started — nothing else, and never the other task\'s row', () => {
  const never: TaskRow = { kind: 'installed', lastRun: { kind: 'never' } };
  const ranAt = (ms: number): TaskRow => ({ kind: 'installed', lastRun: { kind: 'ran', utc: new Date(ms).toISOString(), result: 0 } });
  const rows: readonly (readonly [string, TaskRow, boolean])[] = [
    ['running', { kind: 'running', byCode: true }, true],
    ['installed, ran after the start', ranAt(NOW + 5_000), true],
    ['installed, ran within the second before (truncation)', ranAt(NOW - 3_000), true],
    ['installed, ran 30 s before', ranAt(NOW - 30_000), false],
    ['installed, ran long before', ranAt(NOW - 120_000), false],
    ['installed, never run', never, false],
    ['not installed', { kind: 'notInstalled' }, false],
    ['disabled', { kind: 'disabled' }, false],
    ['unknown', { kind: 'unknown', reason: 'x' }, false],
    ['checking', { kind: 'checking' }, false],
    ['update needed', { kind: 'updateNeeded', why: 'differs', detail: '' }, false],
    ['unreadable', { kind: 'unreadable', reason: 'x' }, false],
  ];
  const copy: CopyState = { kind: 'notStaged', digest: STAGED_DIGEST };
  for (const [name, row, expected] of rows) {
    assert.equal(finishedBy({ copy, tasks: { watch: row, archive: { kind: 'notInstalled' }, cleanup: { kind: 'notInstalled' } }, dry: '' }, { op: 'run', task: 'watch', startedAtUtcMs: NOW, deadlineUtcMs: NOW + 120_000 }), expected, name);
  }
  assert.equal(finishedBy({ copy, tasks: { watch: { kind: 'notInstalled' }, archive: { kind: 'running', byCode: false }, cleanup: { kind: 'notInstalled' } }, dry: '' }, { op: 'run', task: 'watch', startedAtUtcMs: NOW, deadlineUtcMs: NOW + 120_000 }), false, 'the other task\'s row settles nothing');
});

test('activation sweeps a stale or malformed run record and leaves a standing one', async () => {
  for (const bad of [{ op: 'run', task: 'guard', startedAtUtcMs: 1, deadlineUtcMs: NOW + 5 }, { op: 'run', task: 'watch', startedAtUtcMs: 1, deadlineUtcMs: NOW - 1 }, { op: 'run', startedAtUtcMs: 1, deadlineUtcMs: NOW + 5 }]) {
    const store = new MapStore();
    await store.update(PENDING_KEY, bad);
    await sweepPending(store, NOW);
    assert.equal(store.get(PENDING_KEY), undefined, JSON.stringify(bad));
  }
  const standing = new MapStore();
  const record = { op: 'run', task: 'archive', startedAtUtcMs: NOW - 1, deadlineUtcMs: NOW + 5 };
  await standing.update(PENDING_KEY, record);
  await sweepPending(standing, NOW);
  assert.deepEqual(standing.get(PENDING_KEY), record);
});

test('the run buttons by the task\'s row: enabled for every row that is known, held by a record or a running flow', () => {
  const staged: CopyState = { kind: 'staged', digest: STAGED_DIGEST, renders: {} };
  const rows: readonly (readonly [string, TaskRow, boolean])[] = [
    ['not installed', { kind: 'notInstalled' }, true],
    ['disabled', { kind: 'disabled' }, true],
    ['running', { kind: 'running', byCode: true }, true],
    ['update needed', { kind: 'updateNeeded', why: 'differs', detail: '' }, true],
    ['installed', { kind: 'installed', lastRun: { kind: 'never' } }, true],
    ['unreadable', { kind: 'unreadable', reason: 'x' }, true],
    ['checking', { kind: 'checking' }, false],
    ['unknown', { kind: 'unknown', reason: 'x' }, false],
  ];
  for (const [name, row, enabled] of rows) {
    const view = windowsTasksView({ copy: staged, tasks: { watch: row, archive: row, cleanup: row }, dry: '' }, '0.4.0', (iso) => iso);
    assert.deepEqual(view.buttons.filter((b) => RUN_IDS.includes(b.id)).map((b) => b.enabled), [enabled, enabled, enabled], name);
    assert.deepEqual(view.buttons.filter((b) => RUN_IDS.includes(b.id)).map((b) => b.label), ['Run the Windows watch now', 'Archive Windows sessions now', 'Clean Windows TEMP now']);
  }
  const free: WindowsTasksState = { copy: staged, tasks: { watch: { kind: 'notInstalled' }, archive: { kind: 'notInstalled' }, cleanup: { kind: 'notInstalled' } }, dry: '' };
  assert.deepEqual(windowsTasksView(free, '0.4.0', (iso) => iso, { busy: true }).buttons.map((b) => b.enabled), [false, false, false, false, false, false, false, false]);
  assert.deepEqual(windowsTasksView(free, '0.4.0', (iso) => iso, { pending: { op: 'run', task: 'archive' } }).buttons.map((b) => b.enabled), [false, false, false, false, false, false, false, false]);
  assert.equal(windowsTasksView({ copy: { kind: 'unavailable' }, tasks: free.tasks, dry: '' }, '0.4.0', (iso) => iso).buttons.length, 0, 'off Windows: no buttons');
});

test('the host re-reads AND re-probes after a run-now: one read, the probe\'s three renders — no render of its own', async () => {
  const h = hostOf({ read: { tasks: [{ name: 'watch', summary: registered('watch') }] } });
  await ready(h);
  const reads = h.queries.length;
  const renders = h.world.daemon().filter((x) => x.includes('render')).length;
  assert.equal((await h.host.run({ kind: 'run', task: 'watch' }))?.kind, 'done');
  await settle();
  assert.equal(h.queries.length, reads + 1, 'expected reads + 1');
  assert.equal(h.world.daemon().filter((x) => x.includes('render')).length, renders + 3, 'the probe\'s three renders (watch, archive, cleanup); the run-now makes none');
});

test('the page sends the two run buttons as BARE ids: a value beside the type is dropped', () => {
  for (const type of ['runWindowsWatchTask', 'runWindowsArchiveTask', 'runWindowsCleanupTask', 'installWindowsCleanupTask', 'previewWindowsTempCleanup']) {
    assert.deepEqual(parsePageMessage({ type }), { type });
    assert.equal(parsePageMessage({ type, task: 'archive' }), undefined);
    assert.equal(parsePageMessage({ type, script: 'x' }), undefined);
  }
});

// ---- WR-T3: the cleanup task — the last-cleanup line's record read, the preview's held buttons, and the record rules for the third name ----

const RAN_AT = '2026-10-01T21:00:00.0000000Z';
const cleanupRow = (over: Partial<TaskFixture> = {}): TaskFixture => ({ name: 'cleanup', summary: registered('cleanup'), lastRunUtc: RAN_AT, lastResult: '0', ...over });

/** The daemon calls a host's world made for the run reads: [events, files, argvs]. */
function runReads(h: ReturnType<typeof hostOf>): { readonly argv: string[]; readonly files: string[] } {
  const calls = h.world.calls.filter((c) => c.who === 'daemon' && c.request.args[0] === 'runs');

  return { argv: calls.map((c) => c.request.args.join(' ')), files: calls.map((c) => c.request.file) };
}

test('the record is read ONCE per last-run instant, on the bundled exe, with checked instants, only when the cleanup task ran', async () => {
  const h = hostOf({ read: { tasks: [cleanupRow()] } });
  await ready(h);
  const first = runReads(h);
  assert.deepEqual(first.argv, ['runs --from 2026-10-01T20:59:55Z --to 2026-10-01T21:00:00Z --json', 'runs show 20261001T210000Z-4242 --json'], `2 runs requests for one last run? ${first.argv.join(' | ')}`);
  assert.deepEqual(first.files, [BUNDLED_EXE_FILE, BUNDLED_EXE_FILE], 'the state folder is the profile\'s, whichever exe reads it');
  assert.equal(h.host.view().cleanup.line, 'last cleanup: 2026-10-01T21:00:00+00:00, 12 deleted, 3 entries kept in use, 4096 logical bytes');
  assert.equal(h.host.view().cleanup.level, 'ok');
  await h.scheduler.refresh();
  await h.host.refresh();
  await settle();
  assert.deepEqual(runReads(h).argv, first.argv, 'a later refresh with the same last run reads nothing');
  assert.equal(h.host.view().cleanup.line.startsWith('last cleanup: 2026-10-01T21:00:00+00:00, 12 deleted'), true);
});

test('a NEW last run is read again, and its record replaces the old one', async () => {
  let current: SchedulerFixture = { tasks: [cleanupRow()] };
  const h = hostOf({ read: () => current });
  await ready(h);
  current = { tasks: [cleanupRow({ lastRunUtc: '2026-10-02T21:00:00.0000000Z' })] };
  await h.scheduler.refresh();
  await settle();
  assert.equal(runReads(h).argv.filter((a) => a.startsWith('runs --from')).length, 2, 'a runs request for the second run');
  assert.equal(runReads(h).argv.at(-2), 'runs --from 2026-10-02T20:59:55Z --to 2026-10-02T21:00:00Z --json');
});

test('no record read for a task that is absent, unreadable, never ran, or whose Task Scheduler read did not answer', async () => {
  const none: readonly (readonly [string, SchedulerFixture])[] = [
    ['absent', { tasks: [] }],
    ['unreadable', { tasks: [{ name: 'cleanup', unreadable: '0x80070005' }] }],
    ['never ran', { tasks: [cleanupRow({ lastRunUtc: 'never', lastResult: '267011' })] }],
  ];
  for (const [what, read] of none) {
    const h = hostOf({ read });
    await ready(h);
    assert.deepEqual(runReads(h).argv, [], `a runs request for a task that ${what}`);
    assert.equal(h.host.view().cleanup.line === '' || h.host.view().cleanup.line.startsWith('last cleanup: never'), true, what);
  }
  const unanswered = hostOf({});
  await unanswered.host.refresh();
  assert.deepEqual(runReads(unanswered).argv, [], 'before any Task Scheduler answer');
});

test('a history that cannot be read says so, leaves the task line alone, and is tried again at the next read', async () => {
  let failing = true;
  const h = hostOf({ read: { tasks: [cleanupRow()] }, world: { ending: (argv) => (failing && argv.startsWith('runs --from') ? { kind: 'failedToStart', reason: 'spawn EACCES' } : undefined) } });
  await ready(h);
  const view = h.host.view();
  assert.match(view.cleanup.line, /the run history could not be read \(it could not start: spawn EACCES\)/);
  assert.equal(view.cleanup.level, 'unknown');
  assert.match(view.tasks[2]?.line ?? '', /^Windows cleanup task: installed, last run .*: succeeded$/, 'the task line is untouched');
  failing = false;
  await h.scheduler.refresh();
  await settle();
  assert.match(h.host.view().cleanup.line, /12 deleted/, 'the next read tried again');
});

test('while the record is being read the line says only what Task Scheduler said', async () => {
  let release: () => void = () => undefined;
  const gate = new Promise<void>((resolve) => { release = resolve; });
  const base = tasksWorld();
  const client = {
    bundledDigest: () => base.client.bundledDigest(),
    tasksRender: (task: TaskName, digest: string) => base.client.tasksRender(task, digest),
    tasksPreview: (digest: string) => base.client.tasksPreview(digest),
    tasksStage: () => base.client.tasksStage(),
    tasksStageRemove: () => base.client.tasksStageRemove(),
    exe: base.client.exe.bind(base.client),
    read: async (request: Parameters<typeof base.client.read>[0]) => { await gate; return base.client.read(request); },
  };
  const slow = hostOf({ read: { tasks: [cleanupRow()] }, over: { client } });
  const asked = Promise.all([slow.scheduler.refresh(), slow.host.refresh()]);
  await settle();
  assert.equal(slow.host.view().cleanup.line, 'last cleanup: 2026-10-01T21:00:00.0000000Z', 'only the instant, no claim about a record');
  release();
  await asked;
  await settle();
  assert.match(slow.host.view().cleanup.line, /12 deleted/);
});

test('the preview and install-cleanup buttons need a known copy; clean-now follows the run rule; every one of the eight is held by a record or a running flow', async () => {
  const h = hostOf();
  await ready(h);
  const ids = h.host.view().buttons.map((b) => b.id);
  assert.deepEqual(ids.slice(-3), ['installWindowsCleanupTask', 'previewWindowsTempCleanup', 'runWindowsCleanupTask']);
  assert.deepEqual(ENABLED(h), [true, true, true, true, true, true, true, true]);
  let release: () => void = () => undefined;
  const gate = new Promise<void>((resolve) => { release = resolve; });
  const slow = hostOf({ over: { ui: { ...quietTasks().deps.ui, showPreview: async () => gate } } });
  await ready(slow);
  const running = slow.host.run({ kind: 'preview' });
  await settle();
  assert.deepEqual(ENABLED(slow), [false, false, false, false, false, false, false, false], 'a preview in this window holds every button');
  assert.equal(slow.store.get(PENDING_KEY), undefined, 'and writes no record: store.get(PENDING_KEY) !== undefined');
  assert.equal(await slow.host.run({ kind: 'preview' }), undefined, 'a second press while one runs starts nothing');
  release();
  assert.equal((await running)?.kind, 'previewed');
  await settle();
  assert.deepEqual(ENABLED(slow), [true, true, true, true, true, true, true, true]);
  assert.equal(slow.store.get(PENDING_KEY), undefined);
  assert.equal(slow.store.writes, 0);
});

test('a timed-out cleanup install and clean-now keep their record to the deadline, hold all eight buttons across a reload, and release it like the watch\'s', async () => {
  const cases: readonly (readonly ['install' | 'run', string])[] = [['install', 'installs the Windows cleanup task'], ['run', 'starts the Windows cleanup task']];
  for (const [op, words] of cases) {
    const store = new MapStore();
    await store.update(PENDING_KEY, { op, task: 'cleanup', startedAtUtcMs: NOW - 10, deadlineUtcMs: NOW + 120_000 });
    let now = NOW;
    const reloaded = hostOf({ store, now: () => now, read: { tasks: [cleanupRow()] } });
    await ready(reloaded);
    assert.deepEqual(ENABLED(reloaded), [false, false, false, false, false, false, false, false], `${op}: expected false, got true`);
    assert.equal(reloaded.host.view().copy.line, `Waiting for the PowerShell that ${words}…`);
    assert.equal(await reloaded.host.run({ kind: 'preview' }).then((o) => o?.kind), 'busy', `${op}: a preview while a record stands`);
    assert.deepEqual(reloaded.world.daemon().filter((a) => a.startsWith('tasks stage')), [], 'and stages nothing');
    now = NOW + 120_000;
    assert.deepEqual(ENABLED(reloaded), [true, true, true, true, true, true, true, true], `${op}: free at the deadline`);
  }
  for (const op of ['install', 'run'] as const) {
    const stale = new MapStore();
    await stale.update(PENDING_KEY, { op, task: 'cleanup', startedAtUtcMs: NOW - 300_000, deadlineUtcMs: NOW - 1 });
    await sweepPending(stale, NOW);
    assert.equal(stale.get(PENDING_KEY), undefined, `the activation sweep releases a stale cleanup ${op}`);
  }
});

test('finishedBy for the cleanup: an install is finished by ITS row only, a run by running or ran since, a removal needs the cleanup row gone too', () => {
  const copy: CopyState = { kind: 'notStaged', digest: STAGED_DIGEST };
  const gone: Record<TaskName, TaskRow> = { watch: { kind: 'notInstalled' }, archive: { kind: 'notInstalled' }, cleanup: { kind: 'notInstalled' } };
  const installed: TaskRow = { kind: 'installed', lastRun: { kind: 'never' } };
  assert.equal(finishedBy({ copy, tasks: { ...gone, cleanup: installed }, dry: '' }, { op: 'install', task: 'cleanup' }), true);
  assert.equal(finishedBy({ copy, tasks: { ...gone, watch: installed }, dry: '' }, { op: 'install', task: 'cleanup' }), false, 'the watch row settles nothing for the cleanup');
  assert.equal(finishedBy({ copy, tasks: { ...gone, cleanup: { kind: 'running', byCode: true } }, dry: '' }, { op: 'run', task: 'cleanup', startedAtUtcMs: NOW }), true);
  assert.equal(finishedBy({ copy, tasks: gone, dry: '' }, { op: 'remove' }), true);
  assert.equal(finishedBy({ copy, tasks: { ...gone, cleanup: installed }, dry: '' }, { op: 'remove' }), false, 'a registered cleanup task is not removed');
});
