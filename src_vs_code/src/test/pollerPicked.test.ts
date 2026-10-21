import assert from 'node:assert/strict';
import { test } from 'node:test';

import type { Failure, VerbOutcome } from '../client/outcome';
import type { Verb } from '../client/verbs';
import { WslCareClient, type RunOptions } from '../client/WslCareClient';
import { Poller } from '../poll/poller';
import { OutcomeStore, type PanelVerb } from '../state/outcomeStore';
import { DEFAULT_TARGET, type DistroList, type DistroTarget } from '../wsl/distroTarget';
import { distroListOf } from '../wsl/distroList';
import { listed } from './support/distroNames';
import { TEST_ENV } from './support/fakeWorld';
import { ManualClock } from './support/manualClock';
import { answered, failed, headBody } from './support/outcomes';
import { golden } from './support/paths';
import { daemonArgv, exited, exitedUtf16, LIST_QUIET, LIST_RUNNING, LIST_VERBOSE, recordingRunner, verboseTable } from './support/recordingRunner';

/**
 * E18.W1a-2 (todo/PLAN_disk_health_E18.md §3.4, RED 13-17b and the join): the poller reads the DEFAULT distribution on its schedule as
 * ever and a PICKED one ONLY when asked (`readPicked`) — no timer ever fires for it, no generation obsoletes it, and every outcome is
 * stored under the name the read was started FOR, so two distributions' answers cannot land in one slot.
 */

const DEBIAN = listed('Debian');

interface Call {
  readonly target: string;
  readonly verb: Verb;
  readonly options: RunOptions;
}

interface Deferred<T> {
  readonly promise: Promise<T>;
  resolve(value: T): void;
}

function deferred<T>(): Deferred<T> {
  let resolve: (value: T) => void = () => undefined;
  const promise = new Promise<T>((r) => { resolve = r; });

  return { promise, resolve };
}

function panelVerb(verb: Verb): PanelVerb {
  if (verb === 'version') {
    throw new Error('the poller never asks --version');
  }

  return verb;
}

function label(target: DistroTarget): string {
  return target.kind === 'picked' ? target.distro : 'default';
}

function distroOf(outcome: VerbOutcome | undefined): string {
  return outcome !== undefined && outcome.kind === 'answered' ? outcome.distro : `not answered: ${JSON.stringify(outcome)}`;
}

/** What `listDistros()` answers when WSL reports `names` (Ubuntu the default), all running. */
function listOf(...names: string[]): DistroList {
  return distroListOf(verboseTable(names.map((name) => ({ name, running: true })), names[0]), names);
}

interface World {
  readonly poller: Poller;
  readonly store: OutcomeStore;
  readonly clock: ManualClock;
  readonly calls: Call[];
  setting: string;
  /** What the next `listDistros()` answers. */
  list: DistroList | Failure;
  /** Overrides the answer of one call: `<target> <verb>`. */
  readonly answers: Map<string, () => Promise<VerbOutcome>>;
}

/** A poller over a `run` that answers as the distribution its TARGET names (the default being Ubuntu), recording every call. */
function world(): World {
  const clock = new ManualClock();
  const store = new OutcomeStore();
  const calls: Call[] = [];
  const answers = new Map<string, () => Promise<VerbOutcome>>();
  const w = { setting: '', list: listOf('Ubuntu', 'Debian') as DistroList | Failure };
  const run = (verb: Verb, target: DistroTarget, options: RunOptions = {}): Promise<VerbOutcome> => {
    calls.push({ target: label(target), verb, options });
    const scripted = answers.get(`${label(target)} ${verb}`);

    return scripted !== undefined ? scripted() : Promise.resolve(answered(panelVerb(verb), headBody(panelVerb(verb)), target.kind === 'picked' ? target.distro : 'Ubuntu'));
  };
  const poller = new Poller({ run, listDistros: () => Promise.resolve(w.list), store, focused: () => true, refreshSeconds: () => undefined, timers: clock, target: () => w.setting });

  return Object.assign(w, { poller, store, clock, calls, answers });
}

/** The poller has been told what WSL lists, as every pick follows a `listDistros()`. */
async function listedWorld(): Promise<World> {
  const w = world();
  await w.poller.listDistros();

  return w;
}

function pickedCalls(w: World): string[] {
  return w.calls.filter((c) => c.target !== 'default').map((c) => `${c.target} ${c.verb}`);
}

// ---- RED 13 ----

test('readPicked(Debian) asks status, then preview and doctor, each stored under Debian — and NOTHING lands in the default\'s slot', async () => {
  const w = await listedWorld();
  await w.poller.readPicked(DEBIAN);
  assert.deepEqual(w.calls.map((c) => `${c.target} ${c.verb}`), ['Debian status', 'Debian preview', 'Debian doctor']);
  const debian = w.store.snapshotOf(DEBIAN);
  assert.deepEqual([distroOf(debian.status), distroOf(debian.preview), distroOf(debian.doctor), debian.checking], ['Debian', 'Debian', 'Debian', false]);
  const own = w.store.snapshot();
  assert.deepEqual([own.status, own.preview, own.doctor, own.checking], [undefined, undefined, undefined, false], 'a picked read never writes through set()');
});

test('a tick between the picked read\'s legs stores Ubuntu\'s status in the default\'s slot and never under Debian', async () => {
  const w = await listedWorld();
  const held = deferred<VerbOutcome>();
  w.answers.set('Debian preview', () => held.promise);
  const picked = w.poller.readPicked(DEBIAN);
  await w.poller.tick();
  held.resolve(answered('preview', headBody('preview'), 'Debian'));
  await picked;
  assert.equal(distroOf(w.store.snapshot().status), 'Ubuntu');
  assert.equal(distroOf(w.store.snapshotOf(DEBIAN).status), 'Debian', 'the picked status is its own, not the tick\'s');
});

test('Ubuntu\'s preview resolving AFTER Debian\'s lands in Ubuntu\'s slot — the two never mix', async () => {
  const w = await listedWorld();
  const ubuntuPreview = deferred<VerbOutcome>();
  w.answers.set('default preview', () => ubuntuPreview.promise);
  const panel = w.poller.refreshPanel();
  await w.poller.readPicked(DEBIAN);
  assert.equal(distroOf(w.store.snapshotOf(DEBIAN).preview), 'Debian');
  ubuntuPreview.resolve(answered('preview', headBody('preview'), 'Ubuntu'));
  await panel;
  assert.equal(distroOf(w.store.snapshot().preview), 'Ubuntu');
  assert.equal(distroOf(w.store.snapshotOf(DEBIAN).preview), 'Debian', 'Ubuntu\'s late answer did not reach Debian\'s slot');
});

// ---- RED 14 ----

test('over a simulated day, one readPicked is exactly one status + preview + doctor for Debian — every tick of the schedule is the DEFAULT target', async () => {
  const w = await listedWorld();
  w.poller.start();
  w.clock.advance(24 * 3_600_000);
  await w.poller.readPicked(DEBIAN);
  w.clock.advance(24 * 3_600_000);
  await w.poller.settled();
  assert.deepEqual(pickedCalls(w), ['Debian status', 'Debian preview', 'Debian doctor'], 'no timer ever asked a picked distro');
  const ticks = w.calls.filter((c) => c.target === 'default');
  assert.ok(ticks.length > 1000 && ticks.every((c) => c.verb === 'status'), `${ticks.length} default calls, all status`);
});

// ---- RED 15 ----

test('a setting change clears the default\'s snapshot and NOT Debian\'s', async () => {
  const w = await listedWorld();
  await w.poller.readPicked(DEBIAN);
  await w.poller.refreshPanel();
  assert.equal(distroOf(w.store.snapshot().preview), 'Ubuntu');
  w.setting = 'Debian';
  await w.poller.tick();
  const own = w.store.snapshot();
  assert.equal(own.preview, undefined, 'the setting changed: the default\'s preview is cleared (the tick asked only status)');
  assert.equal(own.doctor, undefined);
  const debian = w.store.snapshotOf(DEBIAN);
  assert.deepEqual([distroOf(debian.status), distroOf(debian.preview), distroOf(debian.doctor)], ['Debian', 'Debian', 'Debian'], 'a pick is not about the setting');
});

test('a readPicked in flight across a setting change still stores under Debian — no generation applies to it', async () => {
  const w = await listedWorld();
  const held = deferred<VerbOutcome>();
  w.answers.set('Debian doctor', () => held.promise);
  const inFlight = w.poller.readPicked(DEBIAN);
  await w.poller.tick();
  w.setting = 'Debian';
  await w.poller.tick();
  held.resolve(answered('doctor', headBody('doctor'), 'Debian'));
  await inFlight;
  const debian = w.store.snapshotOf(DEBIAN);
  assert.deepEqual([distroOf(debian.status), distroOf(debian.preview), distroOf(debian.doctor), debian.checking], ['Debian', 'Debian', 'Debian', false]);
});

// ---- RED 16 ----

test('a picked distro whose status answers stopped stores stopped under Debian for all three verbs and asks no preview / doctor', async () => {
  const w = await listedWorld();
  w.answers.set('Debian status', () => Promise.resolve(failed('status', { kind: 'stopped', distro: 'Debian' })));
  await w.poller.readPicked(DEBIAN);
  assert.deepEqual(pickedCalls(w), ['Debian status']);
  const debian = w.store.snapshotOf(DEBIAN);
  assert.deepEqual([debian.status, debian.preview, debian.doctor], [
    { kind: 'stopped', distro: 'Debian', verb: 'status' }, { kind: 'stopped', distro: 'Debian', verb: 'preview' }, { kind: 'stopped', distro: 'Debian', verb: 'doctor' },
  ]);
  assert.equal(debian.checking, false);
});

test('a WSL 1 distro likewise: wsl1 mirrored into preview and doctor, nothing more asked', async () => {
  const w = await listedWorld();
  w.answers.set('Debian status', () => Promise.resolve(failed('status', { kind: 'wsl1', distro: 'Debian' })));
  await w.poller.readPicked(DEBIAN);
  assert.deepEqual(pickedCalls(w), ['Debian status']);
  assert.deepEqual(w.store.snapshotOf(DEBIAN).preview, { kind: 'wsl1', distro: 'Debian', verb: 'preview' });
  assert.deepEqual(w.store.snapshotOf(DEBIAN).doctor, { kind: 'wsl1', distro: 'Debian', verb: 'doctor' });
});

test('readPicked never passes startIfStopped unless given it — and then to status ONLY', async () => {
  const w = await listedWorld();
  await w.poller.readPicked(DEBIAN);
  assert.ok(w.calls.every((c) => c.options.startIfStopped !== true), 'a read never starts a distribution');
  w.calls.length = 0;
  await w.poller.readPicked(DEBIAN, { startIfStopped: true });
  assert.deepEqual(w.calls.filter((c) => c.options.startIfStopped === true).map((c) => `${c.target} ${c.verb}`), ['Debian status']);
});

test('a run that throws is stored as an unknown failure under the picked name — the read goes on to end', async () => {
  const w = await listedWorld();
  w.answers.set('Debian status', () => Promise.reject(new Error('boom')));
  await w.poller.readPicked(DEBIAN);
  assert.deepEqual(w.store.snapshotOf(DEBIAN).status, { kind: 'unknownFailure', code: undefined, messages: ['boom'], verb: 'status' });
  assert.equal(w.store.snapshotOf(DEBIAN).checking, false);
});

// ---- RED 17 and 17b ----

test('the poller\'s listDistros() hands the store the answered names: an unregistered Debian loses its snapshot; a failed list drops nothing', async () => {
  const w = await listedWorld();
  await w.poller.readPicked(DEBIAN);
  w.list = { kind: 'wslFailed', message: 'wsl.exe did not answer' };
  assert.ok('kind' in (await w.poller.listDistros()), 'the Failure is handed on');
  assert.equal(distroOf(w.store.snapshotOf(DEBIAN).status), 'Debian', 'a failed list is not evidence that Debian is gone');

  w.list = listOf('Ubuntu');
  const answered = await w.poller.listDistros();
  assert.ok('distros' in answered);
  assert.equal(w.store.snapshotOf(DEBIAN).status, undefined);
  assert.deepEqual(w.store.pickedNames(), []);
});

test('a readPicked whose preview resolves AFTER a list without Debian leaves no Debian snapshot (the gate\'s finding 4)', async () => {
  const w = await listedWorld();
  const held = deferred<VerbOutcome>();
  w.answers.set('Debian preview', () => held.promise);
  const picked = w.poller.readPicked(DEBIAN);
  await w.poller.tick();
  w.list = listOf('Ubuntu');
  await w.poller.listDistros();
  held.resolve(answered('preview', headBody('preview'), 'Debian'));
  await picked;
  assert.equal(w.store.snapshotOf(DEBIAN).preview, undefined);
  assert.deepEqual(w.store.pickedNames(), [], 'nothing was re-inserted');
});

// ---- askStatus(target) ----

test('askStatus(DEFAULT_TARGET) is today\'s: one status into the default\'s slot; askStatus(picked) is the picked read\'s status leg only', async () => {
  const w = await listedWorld();
  assert.equal(distroOf(await w.poller.askStatus(DEFAULT_TARGET)), 'Ubuntu');
  assert.equal(distroOf(w.store.snapshot().status), 'Ubuntu');
  assert.equal(w.store.snapshotOf(DEBIAN).status, undefined);

  const picked = await w.poller.askStatus({ kind: 'picked', distro: DEBIAN });
  assert.equal(distroOf(picked), 'Debian');
  assert.equal(distroOf(w.store.snapshotOf(DEBIAN).status), 'Debian');
  assert.deepEqual(w.calls.map((c) => `${c.target} ${c.verb}`), ['default status', 'Debian status'], 'the status leg alone: no preview, no doctor');
  assert.equal(w.store.snapshotOf(DEBIAN).checking, false);
});

// ---- the join, with the REAL client ----

test('two readPicked of one distro at once are ONE start of the client: one -d status, one write (the client\'s in-flight sharing)', async () => {
  const names = ['Ubuntu', 'Debian'];
  const status = JSON.stringify({ ...golden('head', 'status'), productVersion: '0.1.0' });
  const gate = deferred<void>();
  const rec = recordingRunner({
    [LIST_QUIET]: exitedUtf16(0, 'Ubuntu\r\nDebian\r\n'),
    [LIST_VERBOSE]: exitedUtf16(0, verboseTable(names.map((name) => ({ name, running: true })), 'Ubuntu')),
    [LIST_RUNNING]: exitedUtf16(0, 'Ubuntu\r\nDebian\r\n'),
    [daemonArgv('Debian', ['status', '--json'])]: async () => { await gate.promise; return exited(0, status); },
    [daemonArgv('Debian', ['preview', '--all', '--json'])]: exited(0, JSON.stringify(golden('head', 'preview'))),
    [daemonArgv('Debian', ['doctor', '--json'])]: exited(0, JSON.stringify(golden('head', 'doctor'))),
  });
  const client = new WslCareClient({ runner: rec.runner, platform: 'win32', env: TEST_ENV, distroSetting: () => '' });
  const store = new OutcomeStore();
  const poller = new Poller({
    run: (verb, target, options) => client.run(verb, target, options), listDistros: () => client.listDistros(), store,
    focused: () => true, refreshSeconds: () => undefined, timers: new ManualClock(), target: () => '',
  });
  await poller.listDistros();
  const first = poller.readPicked(DEBIAN);
  const second = poller.readPicked(DEBIAN);
  for (let i = 0; i < 50; i += 1) {
    await new Promise((resolve) => setImmediate(resolve));
  }
  gate.resolve();
  await Promise.all([first, second]);
  assert.equal(rec.argvs().filter((a) => a === daemonArgv('Debian', ['status', '--json'])).length, 1);
  assert.equal(distroOf(store.snapshotOf(DEBIAN).status), 'Debian');
  assert.equal(distroOf(store.snapshotOf(DEBIAN).doctor), 'Debian');
});

