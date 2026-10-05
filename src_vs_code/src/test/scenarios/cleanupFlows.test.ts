import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

import { CleanupHost } from '../../cleanup/cleanupHost';
import { newCleanRecorder, recordingCleanUi, type CleanRecorder } from '../../cleanup/cleanRecorder';
import { WslCareClient } from '../../client/WslCareClient';
import { CleanupController } from '../../root/cleanupController';
import { OutcomeStore } from '../../state/outcomeStore';
import { fakeWorld, TEST_ENV, UBUNTU_RUNNING, type FakeWorld } from '../support/fakeWorld';
import { ManualTimers, MapStore } from '../support/memento';
import { GOLDEN_ROOT } from '../support/paths';

/**
 * The extension's scenario harness, CLEANUP tier (E6.S3; research/module_tests.md § The extension): the REAL cleanup host —
 * journal, follower, flow, view derivation — over the REAL controller, the REAL `WslCareClient` and the REAL runner seam
 * against the strict fake `wsl.exe`, with a `globalState`-shaped store that outlives the host. A "reload" is a second host,
 * built from scratch over the SAME store and the same machine: nothing of the first survives but what was persisted.
 * Only the clocks are the test's, and the modals answer from a recorder.
 */

const ROOT = '-d Ubuntu -u root --cd / --exec /opt/wsl-care/bin/wsl-care';
const RUN = '20000101T000000Z-1';

/** An answers folder: the head goldens, with `status.json` (and any other file) replaced by the named golden or body. */
function answers(world: FakeWorld, name: string, files: Readonly<Record<string, string | Record<string, unknown>>>): string {
  const folder = path.join(world.folder, name);
  fs.mkdirSync(folder, { recursive: true });
  for (const file of fs.readdirSync(path.join(GOLDEN_ROOT, 'head'))) {
    fs.copyFileSync(path.join(GOLDEN_ROOT, 'head', file), path.join(folder, file));
  }
  for (const [file, from] of Object.entries(files)) {
    const text = typeof from === 'string' ? fs.readFileSync(path.join(GOLDEN_ROOT, 'head', from), 'utf8') : JSON.stringify(from);
    fs.writeFileSync(path.join(folder, file), text);
  }
  return folder;
}

interface Window {
  readonly host: CleanupHost;
  readonly outcomes: OutcomeStore;
  readonly recorder: CleanRecorder;
}

/** One VS Code window: a client, a controller, a store of outcomes, a cleanup host — all fresh, over `durable`. */
function openWindow(world: FakeWorld, durable: MapStore): Window {
  const client = new WslCareClient({ runner: world.runner, platform: 'win32', env: TEST_ENV, distroSetting: () => '' });
  let mono = 0;
  const controller = new CleanupController({ client, runner: world.runner, now: () => mono, sleep: (ms) => { mono += ms; return Promise.resolve(); } });
  const outcomes = new OutcomeStore();
  const recorder = newCleanRecorder();
  recorder.answer = true;
  const askStatus = async () => {
    const outcome = await client.run('status');
    outcomes.set('status', outcome);
    return outcome;
  };
  const host = new CleanupHost({
    durable, controller, read: (request) => client.read(request), outcomes, askStatus,
    refreshPanel: async () => { await askStatus(); outcomes.set('preview', await client.run('preview')); },
    focused: () => true, ui: recordingCleanUi(recorder), timers: new ManualTimers(), now: () => mono, wallNow: () => Date.parse('2026-10-05T10:00:00.000Z'),
  });
  return { host, outcomes, recorder };
}

async function within(body: (world: FakeWorld) => Promise<void>): Promise<void> {
  const world = fakeWorld({ ...UBUNTU_RUNNING, version: '0.1.0' });
  try {
    await body(world);
  } finally {
    world.dispose();
  }
}

test('the RELOAD scenario: start A4, reload, the new window shows "Cleaning… A4" from status.running, then the result from runs show', async () => {
  await within(async (world) => {
    const durable = new MapStore();
    const first = openWindow(world, durable);
    const outcome = await first.host.clean(['A4'], false);
    assert.equal(outcome.kind, 'handedOff', JSON.stringify(outcome).slice(0, 300));
    assert.equal(first.host.journal.entries()[0]?.kind, 'run', 'the started run is persisted');
    first.host.dispose();

    world.rewrite({ answers: answers(world, 'live', { 'status.json': 'status-running-live.json' }) });
    const second = openWindow(world, durable);
    await second.host.follower.tick();
    assert.equal(second.host.controls().state, 'Cleaning… A4', 'from the daemon\'s running block, in a window that started nothing');
    assert.equal(second.host.controls().enabled, false);
    assert.equal(world.calls().filter((c) => c.includes('runs show')).length, 0, 'in flight: status only');

    world.rewrite({ answers: answers(world, 'done', { 'status.json': 'status.json' }), runsShow: 'runs-show-done.json' });
    await second.host.follower.tick();
    assert.equal(world.calls().filter((c) => c.endsWith(`runs show ${RUN} --json`)).length, 1, 'ONE runs show at the terminal state');
    assert.deepEqual(second.host.journal.entries(), [], 'the terminal answer was shown, so the entry is gone');
    assert.match(second.host.controls().results[0]?.sentence ?? '', /^Run 20000101T000000Z-1 \(A4\) is done/);
    assert.match(second.recorder.notices.at(-1)?.sentence ?? '', /is done/);
    assert.equal(second.host.controls().enabled, true);
  });
});

test('a DEAD run after a reload ends as interrupted — the panel never sticks on "Cleaning…"', async () => {
  await within(async (world) => {
    const durable = new MapStore();
    await openWindow(world, durable).host.clean(['A4'], false);
    world.rewrite({ answers: answers(world, 'dead', { 'status.json': 'status-running-dead.json' }), runsShow: 'runs-show-interrupted.json' });
    const window = openWindow(world, durable);
    await window.host.follower.tick();
    assert.match(window.host.controls().results[0]?.sentence ?? '', /was interrupted: swept: pid 4242 is gone/);
    assert.equal(window.host.controls().enabled, true, 'a dead run greys nothing');
    assert.deepEqual(window.host.journal.entries(), []);
  });
});

test('a REFUSED request after a reload shows its reason', async () => {
  await within(async (world) => {
    const durable = new MapStore();
    await openWindow(world, durable).host.clean(['A4'], false);
    const refused = { schemaVersion: 1, runId: RUN, state: 'refused', reason: 'busy: run 20000101T000000Z-9 holds the lock', run: { runId: RUN, trigger: 'manual', startedAt: '2026-10-05T10:00:01+00:00', outcome: 'refused', actions: [{ id: 'A4', status: 'refused', count: 0, freedBytes: 0 }] } };
    world.rewrite({ answers: answers(world, 'refused', { 'runs-show-refused.json': refused }), runsShow: 'runs-show-refused.json' });
    const window = openWindow(world, durable);
    await window.host.follower.tick();
    assert.match(window.host.controls().results[0]?.sentence ?? '', /was refused: busy: run 20000101T000000Z-9 holds the lock$/);
  });
});

test('a 387-volume A4 pipes all 387 names; Clean selected is ONE act call through the real client, runner and fake', async () => {
  await within(async (world) => {
    const window = openWindow(world, new MapStore());
    await window.host.clean(['A4'], false);
    assert.equal(world.stdins()[0]?.split('\n').filter((l) => l.length > 0).length, 387);
    await window.host.follower.tick();
    const confirms = () => world.calls().filter((c) => c.startsWith(`${ROOT} act`) && c.includes('--confirm'));
    const before = confirms().length;
    world.rewrite({ runsShow: 'runs-show-done.json' });
    await window.host.follower.tick();
    const outcome = await window.host.clean(['A5', 'A4'], true);
    assert.equal(outcome.kind, 'handedOff', JSON.stringify(outcome).slice(0, 300));
    assert.deepEqual(confirms().slice(before), [`${ROOT} act A4,A5 --confirm --manual --detach --only - --json`]);
  });
});
