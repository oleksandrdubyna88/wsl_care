import assert from 'node:assert/strict';
import { test } from 'node:test';

import type { Failure, VerbOutcome } from '../client/outcome';
import { CleanupController, type CleanupClient } from '../root/cleanupController';
import { DEFAULT_TARGET, type DistroTarget } from '../wsl/distroTarget';
import { listed, pick } from './support/distroNames';
import { answered, headBody } from './support/outcomes';
import { goldenFile } from './support/paths';
import { exited, recordingRunner, type Scripted } from './support/recordingRunner';

/**
 * E18.W1a-3 (todo/PLAN_disk_health_E18.md §3.5, RED 18): every cleanup-controller road names its distribution. With the PICKED target
 * `Debian` — the DEFAULT being `Ubuntu` — the target, the gate's fresh `status`, the root check and the call all go to Debian, and the
 * confirm of a held preview goes to the distribution that preview was taken for, whatever the caller holds by then.
 */

const WSL = 'C:\\Windows\\System32\\wsl.exe';
const ROOT_FOR = (distro: string): string => `-d ${distro} -u root --cd / --exec /opt/wsl-care/bin/wsl-care`;
const RUN = '20000101T000000Z-1';
const ENTRY = '0123456789abcdef';

function label(target: DistroTarget): string {
  return target.kind === 'picked' ? target.distro : 'default';
}

function nameOf(target: DistroTarget): string {
  return target.kind === 'picked' ? target.distro : 'Ubuntu';
}

function handOff(result = 'accepted'): string {
  return JSON.stringify({ schemaVersion: 1, result, kind: 'act', runId: RUN, unit: `wsl-care-act@${RUN}.service`, productVersion: '0.1.0' });
}

/** The A4 volumes the head golden's preview shows — the confirm pipes them, so its script need not know them. */
function previewA20(): string {
  return JSON.stringify({ ...goldenFile('act-a4-preview.json'), actions: [goldenFile('act-a20-preview-action.json')] });
}

interface World {
  readonly controller: CleanupController;
  readonly runner: ReturnType<typeof recordingRunner>;
  readonly client: string[];
}

interface WorldInput {
  readonly script?: Readonly<Record<string, Scripted>>;
  /** What `run('status', target)` answers for: the distribution NAMED by default, or another one. */
  readonly statusFor?: (target: DistroTarget) => VerbOutcome;
  readonly refuse?: Failure;
}

function world(input: WorldInput = {}): World {
  const client: string[] = [];
  const script: Record<string, Scripted> = {};
  for (const distro of ['Ubuntu', 'Debian']) {
    script[`${ROOT_FOR(distro)} --version`] = exited(0, '0.1.0\n');
  }
  const runner = recordingRunner({ ...script, ...input.script });
  const fake: CleanupClient = {
    rootTarget: (target) => {
      client.push(`rootTarget ${label(target)}`);
      return Promise.resolve(input.refuse ?? { wsl: WSL, distro: nameOf(target) });
    },
    run: (_verb, target) => {
      client.push(`run status ${label(target)}`);
      return Promise.resolve(input.statusFor?.(target) ?? answered('status', headBody('status'), nameOf(target)));
    },
  };
  let now = 1_000_000;
  const controller = new CleanupController({ client: fake, runner: runner.runner, now: () => now, sleep: (ms) => { now += ms; return Promise.resolve(); } });

  return { controller, runner, client };
}

const DEBIAN = pick('Debian');

test('preview(A4, picked Debian): the target, the fresh status, the root check and the preview all go to Debian — the held preview carries the target', async () => {
  const preview = `${ROOT_FOR('Debian')} act A4 --preview --json`;
  const w = world({ script: { [preview]: exited(0, JSON.stringify(goldenFile('act-a4-preview.json'))) } });
  const outcome = await w.controller.preview(['A4'], DEBIAN);
  assert.ok(outcome.kind === 'previewed', JSON.stringify(outcome).slice(0, 300));
  assert.deepEqual(w.client, ['rootTarget Debian', 'run status Debian']);
  assert.deepEqual(w.runner.argvs(), [`${ROOT_FOR('Debian')} --version`, preview]);
  assert.equal(outcome.preview.distro, 'Debian');
  assert.deepEqual(outcome.preview.target, DEBIAN);
});

test('preview(A4, DEFAULT_TARGET) is today\'s: the default road, and the held preview carries DEFAULT_TARGET', async () => {
  const preview = `${ROOT_FOR('Ubuntu')} act A4 --preview --json`;
  const w = world({ script: { [preview]: exited(0, JSON.stringify(goldenFile('act-a4-preview.json'))) } });
  const outcome = await w.controller.preview(['A4'], DEFAULT_TARGET);
  assert.ok(outcome.kind === 'previewed');
  assert.deepEqual(w.client, ['rootTarget default', 'run status default']);
  assert.deepEqual(outcome.preview.target, DEFAULT_TARGET);
});

test('the confirm of a held Debian preview goes to Debian — even though the caller\'s DEFAULT is Ubuntu', async () => {
  const preview = `${ROOT_FOR('Debian')} act A4 --preview --json`;
  const confirm = `${ROOT_FOR('Debian')} act A4 --confirm --manual --detach --only - --json`;
  const w = world({ script: { [preview]: exited(0, JSON.stringify(goldenFile('act-a4-preview.json'))), [confirm]: exited(0, handOff()) } });
  const outcome = await w.controller.preview(['A4'], DEBIAN);
  assert.ok(outcome.kind === 'previewed');
  w.client.length = 0;
  const handed = await w.controller.confirm(outcome.preview);
  assert.equal(handed.kind, 'accepted', JSON.stringify(handed));
  assert.deepEqual(w.client, ['rootTarget Debian', 'run status Debian'], 'the confirm resolved Debian again, never the default');
  assert.ok(w.runner.argvs().includes(confirm), w.runner.argvs().join(' | '));
  assert.ok(w.runner.argvs().every((a) => !a.includes('-d Ubuntu')), 'nothing was sent to Ubuntu');
});

test('rootCheck, stop and runFullCheck with the picked target each carry -d Debian', async () => {
  const stop = `${ROOT_FOR('Debian')} act --stop ${RUN} --json`;
  const collect = `${ROOT_FOR('Debian')} collect --detach --json`;
  const w = world({ script: { [stop]: exited(0, JSON.stringify({ schemaVersion: 1, result: 'stopping', kind: 'act', runId: RUN, unit: `wsl-care-act@${RUN}.service`, productVersion: '0.1.0' })), [collect]: exited(0, handOff()) } });
  const check = await w.controller.rootCheck(DEBIAN);
  assert.equal(check.kind, 'rootOk', JSON.stringify(check));
  assert.equal((await w.controller.stop(RUN, DEBIAN)).kind, 'stopping');
  assert.equal((await w.controller.runFullCheck(DEBIAN)).kind, 'accepted');
  assert.deepEqual(w.runner.argvs(), [`${ROOT_FOR('Debian')} --version`, stop, collect]);
  assert.ok(w.client.every((c) => c.endsWith('Debian')), w.client.join(' | '));
});

test('previewRestore with the picked target carries -d Debian (the entry ids on stdin, A20 only)', async () => {
  const preview = `${ROOT_FOR('Debian')} act A20 --preview --entry - --json`;
  const w = world({ script: { [preview]: exited(0, previewA20()) } });
  const outcome = await w.controller.previewRestore([ENTRY], DEBIAN);
  assert.ok(outcome.kind === 'previewed', JSON.stringify(outcome).slice(0, 300));
  assert.deepEqual(w.runner.argvs(), [`${ROOT_FOR('Debian')} --version`, preview]);
  assert.deepEqual(outcome.preview.target, DEBIAN);
  assert.equal(outcome.preview.distro, 'Debian');
});

test('the distroChanged guard still fires when the gate\'s status answers for another distribution than the target resolved', async () => {
  const w = world({ statusFor: () => answered('status', headBody('status'), 'Ubuntu') });
  const outcome = await w.controller.preview(['A4'], DEBIAN);
  assert.deepEqual(outcome, { kind: 'distroChanged', previewed: 'Debian', now: 'Ubuntu' });
  assert.deepEqual(w.runner.argvs(), [], 'nothing was sent as root');
});

test('a picked target the client refuses (a stopped or WSL 1 distribution) starts nothing: the Failure comes back and no root call is built', async () => {
  for (const refusal of [{ kind: 'stopped', distro: 'Debian' }, { kind: 'wsl1', distro: 'Debian' }] as const) {
    const w = world({ refuse: refusal });
    assert.deepEqual(await w.controller.preview(['A4'], DEBIAN), refusal);
    assert.deepEqual(await w.controller.rootCheck(DEBIAN), refusal);
    assert.deepEqual(w.runner.argvs(), []);
    assert.deepEqual(w.client, ['rootTarget Debian', 'rootTarget Debian'], 'not even the gate\'s status was asked');
  }
});

test('an unknown detach with no run id is followed through status of the SAME target — the picked one', async () => {
  const collect = `${ROOT_FOR('Debian')} collect --detach --json`;
  const w = world({ script: { [collect]: { kind: 'timedOut', timeoutMs: 1, treeEnded: true, stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) } } });
  const outcome = await w.controller.runFullCheck(DEBIAN);
  assert.equal(outcome.kind, 'outcomeUnknown', JSON.stringify(outcome));
  const polls = w.client.filter((c) => c.startsWith('run status')).slice(1);
  assert.ok(polls.length > 0 && polls.every((c) => c === 'run status Debian'), `the follow polled ${JSON.stringify(polls)}`);
});

test('the minted name is the only road: a picked target is built from listedNameOf, never a raw string', () => {
  assert.equal(listed('Debian'), 'Debian');
  assert.deepEqual(DEBIAN, { kind: 'picked', distro: 'Debian' });
});

test('forgetUnlisted drops a distribution\'s remembered root check: Debian is asked afresh after a list without it, Ubuntu is not', async () => {
  const w = world();
  assert.equal((await w.controller.rootCheck(DEBIAN)).kind, 'rootOk');
  assert.equal((await w.controller.rootCheck(DEFAULT_TARGET)).kind, 'rootOk');
  assert.equal((await w.controller.rootCheck(DEBIAN)).kind, 'rootOk', 'remembered');
  assert.equal(w.runner.argvs().length, 2, 'one --version each');
  w.controller.forgetUnlisted(['Ubuntu']);
  assert.equal((await w.controller.rootCheck(DEBIAN)).kind, 'rootOk');
  assert.equal((await w.controller.rootCheck(DEFAULT_TARGET)).kind, 'rootOk');
  assert.deepEqual(w.runner.argvs().slice(2), [`${ROOT_FOR('Debian')} --version`], 'Debian asked again, Ubuntu still remembered');
});

test('the held preview\'s target is frozen with the rest of the preview — a caller cannot re-aim a confirm by editing it', async () => {
  const preview = `${ROOT_FOR('Debian')} act A4 --preview --json`;
  const w = world({ script: { [preview]: exited(0, JSON.stringify(goldenFile('act-a4-preview.json'))) } });
  const outcome = await w.controller.preview(['A4'], DEBIAN);
  assert.ok(outcome.kind === 'previewed');
  assert.ok(Object.isFrozen(outcome.preview.target));
  assert.throws(() => { (outcome.preview.target as { distro: string }).distro = 'Ubuntu'; }, TypeError);
  assert.deepEqual(outcome.preview.target, DEBIAN);
});
