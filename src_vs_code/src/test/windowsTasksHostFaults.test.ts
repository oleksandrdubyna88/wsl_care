import assert from 'node:assert/strict';
import { test } from 'node:test';

import type { WindowsCareClient } from '../client/WindowsCareClient';
import type { ProcessRequest, ProcessResult } from '../process/runner';
import { SchedulerHost } from '../scheduler/schedulerHost';
import { PENDING_KEY } from '../windowsTasks/tasksPending';
import { TasksHost, type TasksClient } from '../windowsTasks/tasksHost';
import { QUERY_SCRIPT } from '../windowsTime/guardScripts';
import { MapStore } from './support/memento';
import { exited } from './support/recordingRunner';
import { schedulerStdout, type SchedulerFixture } from './support/schedulerAnswers';
import { quietTasks } from './support/tasksDeps';
import { tasksWorld } from './support/tasksWorld';
import { registeredSummary } from './support/windowsTasks';

/**
 * A fault at one of the host's detached edges (a record read, the settling of a pending run, the read after a flow) must not stop anything — and must not
 * vanish either: it goes to the extension's log, the way a pending record that cannot be cleared already does (`clearedOrLogged` in `tasksFlow.ts`).
 */

const NOW = 1_000_000;

function hostWith(read: SchedulerFixture, over: { readonly store?: MapStore; readonly client?: (base: WindowsCareClient) => TasksClient } = {}) {
  const world = tasksWorld();
  const logs: string[] = [];
  const query = (_request: ProcessRequest): Promise<ProcessResult> => Promise.resolve(exited(0, schedulerStdout(read)));
  const scheduler = new SchedulerHost({ env: { SystemRoot: 'C:\\Windows' }, queryTimeoutMs: () => 30_000, script: QUERY_SCRIPT, query });
  const quiet = quietTasks(undefined, () => NOW);
  const client = over.client === undefined ? world.client : over.client(world.client);
  const host = new TasksHost({ scheduler, client, pin: '0.4.0', cleanupSince: '0.1.0', formatInstant: (iso) => iso, ...quiet.deps, ...(over.store === undefined ? {} : { durable: over.store }), log: (line) => { logs.push(line); } });

  return { host, scheduler, logs };
}

async function settle(): Promise<void> {
  for (let i = 0; i < 8; i += 1) {
    await new Promise<void>((resolve) => { setImmediate(resolve); });
  }
}

const RAN = { name: 'cleanup', summary: registeredSummary('cleanup'), lastRunUtc: '2026-10-01T21:00:00.0000000Z', lastResult: '0' };

function clientThat(base: WindowsCareClient, read: TasksClient['read']): TasksClient {
  return {
    bundledDigest: () => base.bundledDigest(),
    tasksRender: (task, digest) => base.tasksRender(task, digest),
    tasksPreview: (digest) => base.tasksPreview(digest),
    tasksStage: () => base.tasksStage(),
    tasksStageRemove: () => base.tasksStageRemove(),
    exe: base.exe.bind(base),
    read,
  };
}

test('a record read that throws is logged, with its reason, and the panel goes on', async () => {
  const h = hostWith({ tasks: [RAN] }, { client: (base) => clientThat(base, () => Promise.reject(new Error('the history boom'))) });
  await Promise.all([h.scheduler.refresh(), h.host.refresh()]);
  await settle();
  assert.equal(h.logs.length, 1, `the fault vanished: ${JSON.stringify(h.logs)}`);
  assert.match(h.logs[0] ?? '', /^Windows tasks: the record read of the cleanup task failed: the history boom$/);
  assert.match(h.host.view().tasks[2]?.line ?? '', /^Windows cleanup task: installed, last run /, 'the task line is untouched');
});

test('a store that refuses to clear a settled record is logged, not swallowed', async () => {
  const store = new MapStore();
  await store.update(PENDING_KEY, { op: 'install', task: 'watch', startedAtUtcMs: NOW - 70_000, deadlineUtcMs: NOW + 50_000 });
  const refusing = Object.assign(Object.create(store) as MapStore, { update: (): Promise<void> => Promise.reject(new Error('the store boom')) });
  const h = hostWith({ tasks: [{ name: 'watch', summary: registeredSummary('watch') }] }, { store: refusing });
  await Promise.all([h.scheduler.refresh(), h.host.refresh()]);
  await settle();
  assert.ok(h.logs.some((line) => /^Windows tasks: settling a pending run failed: the store boom$/.test(line)), `the fault vanished: ${JSON.stringify(h.logs)}`);
});
