import assert from 'node:assert/strict';
import { test } from 'node:test';

import { CleanFlow, type CleanUi } from '../cleanup/cleanFlow';
import { CleanupJournal } from '../cleanup/journal';
import type { Modal } from '../cleanup/modalText';
import { CleanupController, type CleanupClient } from '../root/cleanupController';
import { runIdOf } from '../root/rootIds';
import { DEFAULT_TARGET, type DistroTarget } from '../wsl/distroTarget';
import { pick } from './support/distroNames';
import { MapStore } from './support/memento';
import { answered, headBody } from './support/outcomes';
import { goldenFile } from './support/paths';
import { exited, recordingRunner } from './support/recordingRunner';

/**
 * E18.W1a-3 (todo/PLAN_disk_health_E18.md §3.5, RED 19): the cleanup flow for a PICKED distribution. The journal entry written BEFORE the
 * call names the distribution the run is for and says it was picked (`targetKind`), so a reload follows it with the right target; a
 * default run writes an entry with no such key; a picked full check or stop does not need the DEFAULT's status.
 */

const RUN = '20000101T000000Z-1';
const NOW = Date.parse('2026-10-05T10:00:00.000Z');

function rootFor(distro: string): string {
  return `-d ${distro} -u root --cd / --exec /opt/wsl-care/bin/wsl-care`;
}

function handOff(result = 'accepted'): string {
  return JSON.stringify({ schemaVersion: 1, result, kind: 'act', runId: RUN, unit: `wsl-care-act@${RUN}.service`, productVersion: '0.1.0' });
}

function nameOf(target: DistroTarget): string {
  return target.kind === 'picked' ? target.distro : 'Ubuntu';
}

class World {
  readonly store = new MapStore();
  readonly journal: CleanupJournal;
  readonly flow: CleanFlow;
  readonly modals: Modal[] = [];
  readonly runner: ReturnType<typeof recordingRunner>;
  /** The journal as the confirm call saw it — "persisted BEFORE the call". */
  seenAtCall: readonly unknown[] = [];

  constructor(script: Record<string, ReturnType<typeof exited>>, defaultStatusDistro: string | undefined) {
    const scripted: Record<string, ReturnType<typeof exited> | (() => ReturnType<typeof exited>)> = {};
    for (const distro of ['Ubuntu', 'Debian']) {
      scripted[`${rootFor(distro)} --version`] = exited(0, '0.1.0\n');
    }
    for (const [argv, answer] of Object.entries(script)) {
      scripted[argv] = () => { this.seenAtCall = new CleanupJournal(this.store, () => NOW).entries(); return answer; };
    }
    this.runner = recordingRunner(scripted);
    const client: CleanupClient = {
      rootTarget: (target) => Promise.resolve({ wsl: 'C:\\Windows\\System32\\wsl.exe', distro: nameOf(target) }),
      run: (_verb, target) => Promise.resolve(answered('status', headBody('status'), nameOf(target))),
    };
    let mono = 1_000_000;
    const controller = new CleanupController({ client, runner: this.runner.runner, now: () => mono, sleep: (ms) => { mono += ms; return Promise.resolve(); } });
    this.journal = new CleanupJournal(this.store, () => NOW);
    const ui: CleanUi = { confirm: (modal) => { this.modals.push(modal); return Promise.resolve(true); }, notify: () => Promise.resolve(undefined) };
    this.flow = new CleanFlow({
      controller, journal: this.journal, ui, follower: { started: () => undefined, kick: () => undefined },
      now: () => mono, wallNow: () => NOW, distro: () => defaultStatusDistro, changed: () => undefined,
    });
  }
}

const DEBIAN = pick('Debian');
const PREVIEW_A4_JSON = JSON.stringify(goldenFile('act-a4-preview.json'));

test('Clean A4 for Debian: the entry is persisted BEFORE the call with distro Debian and targetKind picked, the confirm goes to Debian, the run entry keeps both', async () => {
  const w = new World({ [`${rootFor('Debian')} act A4 --preview --json`]: exited(0, PREVIEW_A4_JSON), [`${rootFor('Debian')} act A4 --confirm --manual --detach --only - --json`]: exited(0, handOff()) }, 'Ubuntu');
  const outcome = await w.flow.clean(['A4'], false, DEBIAN);
  assert.equal(outcome.kind, 'handedOff', JSON.stringify(outcome).slice(0, 300));
  assert.deepEqual(w.seenAtCall.map((e) => (e as { distro: string; targetKind?: string; kind: string }).kind), ['unresolved']);
  assert.equal((w.seenAtCall[0] as { distro: string }).distro, 'Debian');
  assert.equal((w.seenAtCall[0] as { targetKind?: string }).targetKind, 'picked');
  const after = w.journal.entries();
  assert.equal(after.length, 1);
  assert.deepEqual([after[0]?.kind, after[0]?.distro, after[0]?.targetKind], ['run', 'Debian', 'picked']);
  assert.equal(w.modals[0]?.message, 'Clean A4 in "Debian"?', 'the modal names the distribution the preview was taken for');
  assert.ok(w.runner.argvs().every((a) => !a.includes('-d Ubuntu')), 'nothing went to Ubuntu');
});

test('Clean A4 for the DEFAULT: the entry carries no targetKind key at all — what every pre-W1a journal reads as', async () => {
  const w = new World({ [`${rootFor('Ubuntu')} act A4 --preview --json`]: exited(0, PREVIEW_A4_JSON), [`${rootFor('Ubuntu')} act A4 --confirm --manual --detach --only - --json`]: exited(0, handOff()) }, 'Ubuntu');
  assert.equal((await w.flow.clean(['A4'], false, DEFAULT_TARGET)).kind, 'handedOff');
  const entry = w.journal.entries()[0];
  assert.ok(entry !== undefined);
  assert.equal('targetKind' in entry, false);
  assert.equal(entry.distro, 'Ubuntu');
});

test('a full check for Debian is written for Debian though the default\'s status named Ubuntu — and needs no default status at all', async () => {
  const collect = `${rootFor('Debian')} collect --detach --json`;
  const withDefault = new World({ [collect]: exited(0, handOff()) }, 'Ubuntu');
  assert.equal((await withDefault.flow.fullCheck(DEBIAN)).kind, 'handedOff');
  assert.deepEqual([withDefault.seenAtCall.length, (withDefault.seenAtCall[0] as { distro: string }).distro, (withDefault.seenAtCall[0] as { targetKind?: string }).targetKind], [1, 'Debian', 'picked']);

  const withoutDefault = new World({ [collect]: exited(0, handOff()) }, undefined);
  assert.equal((await withoutDefault.flow.fullCheck(DEBIAN)).kind, 'handedOff', 'a picked distribution does not wait for the default\'s status');
  assert.equal((await withoutDefault.flow.fullCheck(DEFAULT_TARGET)).kind, 'noStatus', 'the default still does');
});

test('a stop for a picked run is written for Debian and goes to Debian', async () => {
  const stop = `${rootFor('Debian')} act --stop ${RUN} --json`;
  const w = new World({ [stop]: exited(0, JSON.stringify({ schemaVersion: 1, result: 'stopping', kind: 'act', runId: RUN, unit: `wsl-care-act@${RUN}.service`, productVersion: '0.1.0' })) }, undefined);
  const runId = runIdOf(RUN);
  assert.ok(runId !== undefined);
  const outcome = await w.flow.stop({ runId, actions: ['A4'], trigger: 'manual' }, DEBIAN);
  assert.equal(outcome.kind, 'handedOff', JSON.stringify(outcome).slice(0, 300));
  const entry = w.journal.entries()[0];
  assert.deepEqual([entry?.op, entry?.distro, entry?.targetKind], ['stop', 'Debian', 'picked']);
  assert.ok(w.runner.argvs().includes(stop));
});
