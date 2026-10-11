import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

import { ArchiveController } from '../../archivePage/archiveController';
import type { ArchivePageView } from '../../archivePage/archiveView';
import { CleanupHost } from '../../cleanup/cleanupHost';
import { newCleanRecorder, recordingCleanUi, type CleanRecorder } from '../../cleanup/cleanRecorder';
import { WslCareClient } from '../../client/WslCareClient';
import { CleanupController } from '../../root/cleanupController';
import { OutcomeStore } from '../../state/outcomeStore';
import { bodyAt, bodyOf } from '../support/body';
import { fakeWorld, TEST_ENV, UBUNTU_RUNNING, type FakeWorld } from '../support/fakeWorld';
import { ManualTimers, MapStore } from '../support/memento';
import { GOLDEN_ROOT, goldenFile } from '../support/paths';

/**
 * Plan §15s, E10.S2b: *Restore* end to end — the REAL Archive page host (`ArchiveController`) wired to the REAL cleanup host as
 * `extension.ts` wires them (the page hands entry ids as text, reads the host's facts), over the REAL controller, client and runner
 * against the strict fake `wsl.exe`, with a `globalState`-shaped store that outlives a window. A "reload" is a second window over
 * the same store and machine. The fake's stdin log is the witness of what reached the root daemon.
 */

const ROOT = '-d Ubuntu -u root --cd / --exec /opt/wsl-care/bin/wsl-care';
const PREVIEW = `${ROOT} act A20 --preview --entry - --json`;
const CONFIRM = `${ROOT} act A20 --confirm --manual --detach --entry - --json`;
const VERIFIED = '00000000000000a1';
const UNVERIFIED = '00000000000000a2';
const OTHER = '00000000000000a3';

type Json = Record<string, unknown>;

function listEntry(id: string, edit: Json = {}): Json {
  const entries = goldenFile('archive-list.json').entries;
  assert.ok(Array.isArray(entries));
  return { ...bodyOf(entries[0], 'archive-list.json entries[0]'), entryId: id, key: `projects/p/${id}.jsonl`, status: 'sourceRemoved', verified: true, ...edit };
}

/** The scenario's archive: one verified entry removed at its source, one unverified, one still at its source. */
const LIST: Json = { ...goldenFile('archive-list.json'), entries: [listEntry(VERIFIED), listEntry(UNVERIFIED, { verified: false }), listEntry(OTHER, { status: 'archived' })] };

/** An answers folder: the head goldens, with the named files replaced. */
function answers(world: FakeWorld, name: string, files: Readonly<Record<string, string | Json>>): string {
  const folder = path.join(world.folder, name);
  fs.mkdirSync(folder, { recursive: true });
  for (const file of fs.readdirSync(path.join(GOLDEN_ROOT, 'head'))) {
    fs.copyFileSync(path.join(GOLDEN_ROOT, 'head', file), path.join(folder, file));
  }
  for (const [file, from] of Object.entries({ 'archive-list.json': LIST, ...files })) {
    fs.writeFileSync(path.join(folder, file), typeof from === 'string' ? fs.readFileSync(path.join(GOLDEN_ROOT, 'head', from), 'utf8') : JSON.stringify(from));
  }
  return folder;
}

function restoring(state: 'live' | 'dead'): Json {
  const golden = goldenFile(`status-running-${state}.json`);
  return { ...golden, running: { ...bodyAt(golden, 'running'), actions: ['A20'], current: '' } };
}

interface Window {
  readonly host: CleanupHost;
  readonly page: ArchiveController;
  readonly views: ArchivePageView[];
  readonly recorder: CleanRecorder;
  /** Waits for every page update the host's changes started — none may outlive the test's machine. */
  readonly settled: () => Promise<void>;
}

/** One VS Code window: the cleanup host and the Archive page host, wired as `extension.ts` wires them, over `durable`. */
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
    durable, controller, read: (request) => client.read(request), outcomes, askStatus, refreshPanel: async () => { await askStatus(); },
    focused: () => true, ui: recordingCleanUi(recorder), timers: new ManualTimers(), now: () => mono, wallNow: () => Date.parse('2026-10-11T10:00:00.000Z'), log: () => undefined,
  });
  const views: ArchivePageView[] = [];
  const page = new ArchiveController({
    read: (request) => client.read(request), status: () => outcomes.snapshot().status, post: (view) => { views.push(view); },
    restore: { facts: () => ({ inFlight: host.restoreInFlight(), state: host.controls().state, blocker: host.restoreBlocker() }), start: (ids) => host.restore(ids) },
  });
  const pending: Promise<void>[] = [];
  host.onChange(() => { pending.push(page.restoreChanged()); });

  return { host, page, views, recorder, settled: async () => { await Promise.all(pending); } };
}

function last(window: Window): ArchivePageView {
  const view = window.views.at(-1);
  assert.ok(view !== undefined);
  return view;
}

async function within(body: (world: FakeWorld) => Promise<void>): Promise<void> {
  const world = fakeWorld({ ...UBUNTU_RUNNING, version: '0.1.0' });
  try {
    await body(world);
  } finally {
    world.dispose();
  }
}

function listReads(world: FakeWorld): number {
  return world.calls().filter((c) => c.includes(' archive list ')).length;
}

test('the RESTORE scenario: select on the page, Restore — preview, modal, detached confirm of the SHOWN id on stdin; reload → "Restoring…"; the result; the list read again', async () => {
  await within(async (world) => {
    world.rewrite({ answers: answers(world, 'restorable', {}) });
    const durable = new MapStore();
    const first = openWindow(world, durable);
    await first.host.follower.tick();
    await first.page.receive({ type: 'ready' });
    await first.page.receive({ type: 'toggleEntry', index: 1, generation: last(first).generation });
    assert.match(last(first).notice, /cannot be restored from here/, 'the unverified entry is never selectable');
    await first.page.receive({ type: 'toggleEntry', index: 0, generation: last(first).generation });
    assert.equal(last(first).selection.restoreEnabled, true);
    await first.page.receive({ type: 'restore' });
    assert.ok(world.calls().includes(PREVIEW) && world.calls().includes(CONFIRM), JSON.stringify(world.calls().filter((c) => c.includes(' act '))));
    assert.equal(first.recorder.modals[0]?.confirm, 'Restore');
    assert.equal(first.recorder.modals[0]?.message, 'Restore 1 archived session in "Ubuntu"?');
    assert.deepEqual(world.stdins(), [`${VERIFIED}\n`, `${VERIFIED}\n`], 'the preview and the confirm each piped exactly the one id');
    assert.ok(world.calls().every((c) => !c.includes(VERIFIED) && !c.includes(UNVERIFIED)), 'no id travels as argv');
    assert.deepEqual(first.host.journal.entries().map((e) => e.actions), [['A20']], 'the restore run is persisted');
    await first.settled();
    first.host.dispose();

    world.rewrite({ answers: answers(world, 'running', { 'status.json': restoring('live') }) });
    const second = openWindow(world, durable);
    await second.host.follower.tick();
    assert.equal(second.host.controls().state, 'Restoring… archived sessions', 'from the daemon running block, in a window that started nothing');
    await second.page.receive({ type: 'ready' });
    assert.equal(last(second).selection.line, 'Restoring… archived sessions');
    assert.equal(last(second).selection.restoreEnabled, false);
    await second.page.restoreChanged();
    const reads = listReads(world);

    const restored: Json = { ...LIST, entries: [listEntry(VERIFIED, { status: 'restored' }), listEntry(UNVERIFIED, { verified: false }), listEntry(OTHER, { status: 'archived' })] };
    world.rewrite({ answers: answers(world, 'restored', { 'status.json': 'status.json', 'archive-list.json': restored }), runsShow: 'runs-show-done.json' });
    await second.host.follower.tick();
    assert.match(second.host.controls().results[0]?.sentence ?? '', /^Run 20000101T000000Z-1 \(the restore run\) is done: \d+ archived sessions? restored\.$/);
    assert.deepEqual(second.host.journal.entries(), []);
    await second.page.restoreChanged();
    assert.equal(listReads(world), reads + 1, 'the page read its list again when the restore ended');
    assert.equal(last(second).selection.count, 0);
    assert.equal(last(second).agents[0]?.months[0]?.selectEnabled, false, 'the restored entry is no longer selectable');
    await second.settled();
  });
});

test('a DEAD restore run after a reload ends as interrupted — never stuck on "Restoring…"', async () => {
  await within(async (world) => {
    world.rewrite({ answers: answers(world, 'restorable', {}) });
    const durable = new MapStore();
    const started = openWindow(world, durable);
    await started.host.follower.tick();
    assert.equal((await started.host.restore([VERIFIED]))?.kind, 'handedOff');
    world.rewrite({ answers: answers(world, 'dead', { 'status.json': restoring('dead') }), runsShow: 'runs-show-interrupted.json' });
    const window = openWindow(world, durable);
    await window.host.follower.tick();
    assert.match(window.host.controls().results[0]?.sentence ?? '', /\(the restore run\) was interrupted/);
    assert.equal(window.host.restoreInFlight(), false);
    assert.equal(window.host.restoreBlocker(), '');
    await Promise.all([started.settled(), window.settled()]);
  });
});

test('a selection past a LOWERED act.maxShownNames (still under restoreCeiling) cannot be restored — the page refuses the second id, and the controller refuses the list before any root call', async () => {
  await within(async (world) => {
    const status = goldenFile('status.json');
    const lowered = { ...status, limits: { ...bodyOf(status.limits, 'limits'), maxShownNames: 1 } };
    const two: Json = { ...LIST, entries: [listEntry(VERIFIED), listEntry(OTHER)] };
    world.rewrite({ answers: answers(world, 'lowered', { 'status.json': lowered, 'archive-list.json': two }) });
    const window = openWindow(world, new MapStore());
    await window.host.follower.tick();
    await window.page.receive({ type: 'ready' });
    for (const index of [0, 1]) {
      await window.page.receive({ type: 'toggleEntry', index, generation: last(window).generation });
    }
    assert.equal(last(window).selection.count, 1);
    assert.match(last(window).notice, /^One restore takes at most 1 /);
    const outcome = await window.host.restore([VERIFIED, OTHER]);
    assert.ok(outcome?.kind === 'refused' && outcome.failure.kind === 'entriesRefused', JSON.stringify(outcome).slice(0, 300));
    assert.ok(world.calls().every((c) => !c.includes(' act A20 ')), 'nothing reached the root daemon');
    await window.settled();
  });
});
