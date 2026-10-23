import assert from 'node:assert/strict';
import { test } from 'node:test';

import { BUNDLED_DAEMON, CLEANUP_TASK_SINCE, versionAtLeast } from '../client/handshake';
import type { ProcessRequest, ProcessResult } from '../process/runner';
import { SchedulerHost } from '../scheduler/schedulerHost';
import { CLEANUP_UNAVAILABLE, cleanupGate } from '../windowsTasks/cleanupGate';
import { runTasksOp, type TasksOp } from '../windowsTasks/tasksFlow';
import { TasksHost } from '../windowsTasks/tasksHost';
import { QUERY_SCRIPT } from '../windowsTime/guardScripts';
import { exited } from './support/recordingRunner';
import { schedulerStdout, type SchedulerFixture } from './support/schedulerAnswers';
import { quietTasks } from './support/tasksDeps';
import { tasksWorld } from './support/tasksWorld';

/**
 * The cleanup task needs a daemon that has it (WR-T2, #172), and the daemon the .vsix bundles may be older: a daemon without `tasks render cleanup` /
 * `tasks preview cleanup` answers exit 2 (the wrong side), which would put a false-reason cleanup row on every refresh and a dead button in the panel.
 * So the three cleanup buttons, the probe's third render and the flows of that task are gated on ONE comparison of the bundled version with
 * `CLEANUP_TASK_SINCE`, made with the existing `versionAtLeast`; nothing is started while the gate is closed.
 */

const ENV = { SystemRoot: 'C:\\Windows' } as const;
const CLEANUP_IDS = ['installWindowsCleanupTask', 'previewWindowsTempCleanup', 'runWindowsCleanupTask'];

function hostWith(pin: string, since?: string, read: SchedulerFixture = { tasks: [] }) {
  const world = tasksWorld();
  const queries: ProcessRequest[] = [];
  const query = (request: ProcessRequest): Promise<ProcessResult> => { queries.push(request); return Promise.resolve(exited(0, schedulerStdout(read))); };
  const scheduler = new SchedulerHost({ env: ENV, queryTimeoutMs: () => 30_000, script: QUERY_SCRIPT, query });
  const quiet = quietTasks();
  quiet.recorder.answer = true;
  const host = new TasksHost({ scheduler, client: world.client, pin, formatInstant: (iso) => iso, ...quiet.deps, ...(since === undefined ? {} : { cleanupSince: since }) });

  return { host, scheduler, world, quiet };
}

async function settle(): Promise<void> {
  for (let i = 0; i < 8; i += 1) {
    await new Promise<void>((resolve) => { setImmediate(resolve); });
  }
}

test('the gate is closed for a bundled daemon below the first release that has the cleanup task, open at it and above', () => {
  assert.equal(cleanupGate('0.6.0', '0.7.0'), CLEANUP_UNAVAILABLE);
  assert.equal(cleanupGate('0.6.9', '0.7.0'), CLEANUP_UNAVAILABLE);
  assert.equal(cleanupGate('0.7.0', '0.7.0'), '');
  assert.equal(cleanupGate('0.7.1', '0.7.0'), '');
  assert.equal(cleanupGate('1.0.0', '0.7.0'), '');
  assert.equal(CLEANUP_UNAVAILABLE, 'the bundled daemon is older than the cleanup task; the next daemon release brings it');
});

test('the gate follows the real pin: closed exactly while BUNDLED_DAEMON is below CLEANUP_TASK_SINCE (the day the pin is bumped it opens by itself)', () => {
  assert.equal(cleanupGate(BUNDLED_DAEMON), versionAtLeast(BUNDLED_DAEMON, CLEANUP_TASK_SINCE) ? '' : CLEANUP_UNAVAILABLE);
});

test('with the real pin (0.6.0) the cleanup row says why, the three buttons are off, the probe renders only the first two tasks and nothing spawns for the third', async () => {
  const h = hostWith('0.6.0');
  await Promise.all([h.scheduler.refresh(), h.host.refresh()]);
  await settle();
  const view = h.host.view();
  assert.equal(view.tasks[2]?.line, `Windows cleanup task: unavailable — ${CLEANUP_UNAVAILABLE}`);
  assert.equal(view.tasks[2]?.level, 'none');
  const enabled = Object.fromEntries(view.buttons.map((b) => [b.id, b.enabled]));
  for (const id of CLEANUP_IDS) {
    assert.equal(enabled[id], false, `${id} must be off`);
  }
  assert.equal(enabled['installWindowsWatchTask'], true);
  assert.equal(enabled['installWindowsArchiveTask'], true);
  assert.equal(enabled['removeWindowsTasks'], true);
  const renders = h.world.daemon().filter((a) => a.startsWith('tasks render'));
  assert.deepEqual(renders, ['tasks render watch --json', 'tasks render archive --json'], 'a render of the cleanup task was run');
  assert.deepEqual(h.world.daemon().filter((a) => a.includes('cleanup')), [], 'nothing spawned for the cleanup task');
  assert.equal(view.cleanup.line, '', 'and no last-cleanup line');
});

test('with the real pin, a cleanup task that somehow ran (registered by another build) is not read about: no run-history request, no line', async () => {
  const ran = { name: 'cleanup', summary: ['principal'], lastRunUtc: '2026-10-01T21:00:00.0000000Z', lastResult: '0' };
  const h = hostWith('0.6.0', undefined, { tasks: [ran] });
  await Promise.all([h.scheduler.refresh(), h.host.refresh()]);
  await settle();
  assert.deepEqual(h.world.calls.filter((c) => c.request.args[0] === 'runs').map((c) => c.request.args.join(' ')), [], 'a run-history read was made while the gate is closed');
  assert.equal(h.host.view().cleanup.line, '');
  const open = hostWith('0.4.0', '0.1.0', { tasks: [ran] });
  await Promise.all([open.scheduler.refresh(), open.host.refresh()]);
  await settle();
  assert.ok(open.world.calls.some((c) => c.request.args[0] === 'runs'), 'and with the gate open the history IS read');
});

test('with the real pin, a flow of the cleanup task asked anyway (the palette) starts nothing and says the sentence', async () => {
  const h = hostWith('0.6.0');
  await Promise.all([h.scheduler.refresh(), h.host.refresh()]);
  await settle();
  const before = h.world.calls.length;
  const ops: readonly TasksOp[] = [{ kind: 'preview' }, { kind: 'install', task: 'cleanup' }, { kind: 'run', task: 'cleanup' }];
  for (const op of ops) {
    const outcome = await h.host.run(op);
    assert.equal(outcome?.kind, 'notStarted', JSON.stringify(op));
    assert.equal(outcome?.kind === 'notStarted' ? outcome.reason : '', CLEANUP_UNAVAILABLE);
  }
  assert.equal(h.world.calls.length, before, 'nothing was spawned');
  assert.deepEqual(h.quiet.recorder.requests, [], 'no PowerShell either');
  assert.equal(h.quiet.recorder.reports.length, 3);
  assert.ok(h.quiet.recorder.reports.every((r) => r.failed && r.message.includes(CLEANUP_UNAVAILABLE)));
});

test('the other two tasks are not gated: an install of the watch still runs with the gate closed', async () => {
  const w = tasksWorld({ cleanupGate: CLEANUP_UNAVAILABLE });
  assert.equal((await runTasksOp({ kind: 'install', task: 'watch' }, w.deps)).kind, 'done');
  const gated = await runTasksOp({ kind: 'run', task: 'cleanup' }, w.deps);
  assert.equal(gated.kind === 'notStarted' ? gated.reason : gated.kind, CLEANUP_UNAVAILABLE);
});

test('with an injected pin at or above the first release (today\'s behaviour): three renders, the three buttons on', async () => {
  const h = hostWith('0.4.0', '0.1.0');
  await Promise.all([h.scheduler.refresh(), h.host.refresh()]);
  await settle();
  const view = h.host.view();
  assert.deepEqual(h.world.daemon().filter((a) => a.startsWith('tasks render')), ['tasks render watch --json', 'tasks render archive --json', 'tasks render cleanup --json']);
  assert.equal(view.tasks[2]?.line, 'Windows cleanup task: not installed');
  assert.deepEqual(view.buttons.filter((b) => CLEANUP_IDS.includes(b.id)).map((b) => b.enabled), [true, true, true]);
  const preview = await h.host.run({ kind: 'preview' });
  assert.equal(preview?.kind, 'previewed');
});
