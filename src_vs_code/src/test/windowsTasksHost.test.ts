import assert from 'node:assert/strict';
import { test } from 'node:test';

import type { RenderAnswer } from '../client/windowsTasks';
import { parsePageMessage } from '../panel/messages';
import type { ProcessRequest, ProcessResult } from '../process/runner';
import { schedulerReadOf } from '../scheduler/schedulerAnswer';
import { SchedulerHost } from '../scheduler/schedulerHost';
import { PENDING_KEY, readPending, sweepPending } from '../windowsTasks/tasksPending';
import { TasksHost, type TasksHostDeps } from '../windowsTasks/tasksHost';
import { finishedBy, windowsTasksState, type CopyState, type TaskRow } from '../windowsTasks/tasksState';
import { windowsTasksView } from '../windowsTasks/tasksView';
import { QUERY_SCRIPT } from '../windowsTime/guardScripts';
import { exited } from './support/recordingRunner';
import { schedulerStdout, type SchedulerFixture, type TaskFixture } from './support/schedulerAnswers';
import { quietTasks } from './support/tasksDeps';
import { tasksWorld, type WorldOptions } from './support/tasksWorld';
import { renderAnswer, STAGED_DIGEST } from './support/windowsTasks';
import { MapStore } from './support/memento';

/**
 * The Windows tasks' host after WR-E3 (PLAN_windows_runner.md §3.8): ONE flow at a time in this window, every button held by a persisted pending
 * run in EVERY window and across a reload, the part re-read and re-probed after a flow answers, a pending run that Task Scheduler already shows
 * finished settled on the next read, and a stale record swept. The surfaces are the product's own Test-mode recorder; no PowerShell starts.
 */

const ENV = { SystemRoot: 'C:\\Windows' } as const;
const NOW = 1_000_000;

/** What Task Scheduler reads back of a registered task: the render's summary, the delay as Task Scheduler spells it (`PT60S` → `PT1M`). */
function registered(task: 'watch' | 'archive'): readonly string[] {
  return (renderAnswer(`${task} from the staged copy`).summary as string[]).map((line) => line.replaceAll('delay=PT60S', 'delay=PT1M'));
}

interface Options {
  readonly read?: SchedulerFixture;
  readonly store?: MapStore;
  readonly now?: () => number;
  readonly world?: WorldOptions;
  readonly over?: Partial<TasksHostDeps>;
}

function hostOf(options: Options = {}) {
  const world = tasksWorld(options.world);
  const queries: ProcessRequest[] = [];
  const answer = (): ProcessResult => exited(0, schedulerStdout(options.read ?? { tasks: [] }));
  const scheduler = new SchedulerHost({ env: ENV, queryTimeoutMs: () => 30_000, script: QUERY_SCRIPT, query: (request) => { queries.push(request); return Promise.resolve(answer()); } });
  const quiet = quietTasks(undefined, options.now ?? (() => NOW));
  quiet.recorder.answer = true;
  const store = options.store ?? quiet.store;
  const host = new TasksHost({ scheduler, client: world.client, pin: '0.4.0', formatInstant: (iso) => iso, ...quiet.deps, durable: store, ...options.over });

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
  assert.deepEqual(view.buttons.map((b) => b.id), ['installWindowsWatchTask', 'installWindowsArchiveTask', 'removeWindowsTasks']);
  assert.deepEqual(ENABLED(reloaded), [false, false, false], 'expected false, got true');
  assert.equal(view.copy.line, 'Waiting for the PowerShell that installs the Windows archive task…');
  assert.equal(await reloaded.host.run({ kind: 'remove' }).then((o) => o?.kind), 'busy');
  assert.deepEqual(reloaded.world.ps(), []);
  assert.equal(reloaded.quiet.recorder.requests.length, 0, 'no request was made');

  let now = NOW;
  const later = hostOf({ store, now: () => now });
  await ready(later);
  assert.deepEqual(ENABLED(later), [false, false, false]);
  now = NOW + 120_000;
  assert.deepEqual(ENABLED(later), [true, true, true], 'past the deadline the record holds nothing');
  const removal = new MapStore();
  await removal.update(PENDING_KEY, { op: 'remove', startedAtUtcMs: NOW - 10, deadlineUtcMs: NOW + 1 });
  const removing = hostOf({ store: removal });
  await ready(removing);
  assert.equal(removing.host.view().copy.line, 'Waiting for the PowerShell that removes the Windows tasks…');
});

test('the buttons by state: install needs a known copy, remove needs something to remove', async () => {
  const staged: CopyState = { kind: 'staged', digest: STAGED_DIGEST, renders: {} };
  const none: Record<'watch' | 'archive', TaskRow> = { watch: { kind: 'notInstalled' }, archive: { kind: 'notInstalled' } };
  const installed: TaskRow = { kind: 'installed', lastRun: { kind: 'never' } };
  const table: readonly (readonly [string, CopyState, Record<'watch' | 'archive', TaskRow>, boolean[]])[] = [
    ['off Windows: no part at all', { kind: 'unavailable' }, none, []],
    ['probing the copy', { kind: 'checking' }, none, [false, false, false]],
    ['the digest is unknown', { kind: 'digestUnreadable', reason: 'x' }, none, [false, false, false]],
    ['nothing staged, nothing registered', { kind: 'notStaged', digest: STAGED_DIGEST }, none, [true, true, false]],
    ['a staged copy, nothing registered: its removal deletes the copy', staged, none, [true, true, true]],
    ['an unreadable copy may be there to remove', { kind: 'unreadable', digest: STAGED_DIGEST, reason: 'x' }, none, [true, true, true]],
    ['a registered task', { kind: 'notStaged', digest: STAGED_DIGEST }, { watch: installed, archive: { kind: 'notInstalled' } }, [true, true, true]],
    ['a registered task whose read is unknown does not count', { kind: 'notStaged', digest: STAGED_DIGEST }, { watch: { kind: 'unknown', reason: 'x' }, archive: { kind: 'checking' } }, [true, true, false]],
  ];
  for (const [name, copy, tasks, expected] of table) {
    const view = windowsTasksView({ copy, tasks, dry: '' }, '0.4.0', (iso) => iso);
    assert.deepEqual(view.buttons.map((b) => b.enabled), expected, name);
  }
  const held = windowsTasksView({ copy: staged, tasks: none, dry: '' }, '0.4.0', (iso) => iso, { busy: true });
  assert.deepEqual(held.buttons.map((b) => b.enabled), [false, false, false], 'a flow running in this window holds them too');
});

test('the host runs one op at a time, greys the buttons while it runs, and re-reads AND re-probes afterwards', async () => {
  let release: (yes: boolean) => void = () => undefined;
  const gate = new Promise<boolean>((resolve) => { release = resolve; });
  const quiet = quietTasks();
  const h = hostOf({ over: { ui: { ...quiet.deps.ui, confirm: async () => gate } } });
  await ready(h);
  assert.deepEqual(ENABLED(h), [true, true, true]);
  const first = h.host.run({ kind: 'install', task: 'watch' });
  await settle();
  assert.deepEqual(ENABLED(h), [false, false, false], 'greyed while the modal is open');
  assert.equal(await h.host.run({ kind: 'install', task: 'archive' }), undefined, 'a second press while one runs starts nothing');
  release(false);
  assert.equal((await first)?.kind, 'declined');
  await settle();
  assert.deepEqual(ENABLED(h), [true, true, true]);

  const done = hostOf();
  await ready(done);
  const reads = done.queries.length;
  const renders = done.world.daemon().filter((x) => x.includes('render')).length;
  assert.equal((await done.host.run({ kind: 'install', task: 'watch' }))?.kind, 'done');
  await settle();
  assert.equal(done.queries.length, reads + 1, 'one Task Scheduler read after the answer');
  assert.equal(done.world.daemon().filter((x) => x.includes('render')).length, renders + 3, 'the flow render, then the probe two (watch, archive)');
});

test('finding 4: an install settles only on ITS task\'s summary — the archive install is not settled by the watch\'s', () => {
  const answers = { watch: { ...renderAnswer('watch from the staged copy'), task: 'watch' } as unknown as RenderAnswer, archive: { ...renderAnswer('archive from the staged copy'), task: 'archive' } as unknown as RenderAnswer };
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
  assert.deepEqual(ENABLED(finished), [true, true, true]);

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
  assert.deepEqual(ENABLED(b), [false, false, false], 'window B\'s buttons are enabled while A\'s PowerShell may still run');
  assert.equal(await b.host.run({ kind: 'remove' }).then((o) => o?.kind), 'busy', 'B\'s Remove could run beside A\'s Register');
  now = NOW + 59_999;
  await b.scheduler.refresh();
  await settle();
  assert.equal(readPending(store, now)?.op, 'install', 'one millisecond before the ceiling it still holds');
  now = NOW + 60_000;
  await b.scheduler.refresh();
  await settle();
  assert.equal(store.get(PENDING_KEY), undefined, 'past the ceiling the read settles it');
  assert.deepEqual(ENABLED(b), [true, true, true]);
});
