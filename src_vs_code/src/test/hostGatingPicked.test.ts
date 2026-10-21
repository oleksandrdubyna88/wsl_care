import assert from 'node:assert/strict';
import { test } from 'node:test';

import { ArchiveHost } from '../archive/archiveHost';
import { newArchiveRecorder, recordingArchiveUi } from '../archive/archiveRecorder';
import { CleanupHost } from '../cleanup/cleanupHost';
import { newCleanRecorder, recordingCleanUi } from '../cleanup/cleanRecorder';
import type { ReadOutcome } from '../client/outcome';
import type { PreviewOutcome } from '../root/rootOutcome';
import { OutcomeStore } from '../state/outcomeStore';
import { DEFAULT_TARGET, type DistroTarget } from '../wsl/distroTarget';
import { stringsAt } from './support/body';
import { pick, listed } from './support/distroNames';
import { ManualTimers, MapStore } from './support/memento';
import { answered, headBody, type Body } from './support/outcomes';
import { goldenFile } from './support/paths';

/**
 * Review round (todo/PLAN_disk_health_E18.md W1a §10a, B): a host flow for a PICKED distribution is allowed or refused by THAT
 * distribution's `status`, never by the default's. Each test gives the two distributions opposite facts — Ubuntu (the default) lacks
 * what Debian has, and the reverse — and watches whether the controller is reached.
 */

const DEBIAN = pick('Debian');
const NAMES = [listed('Ubuntu'), listed('Debian')];

function without(body: Body, key: 'actions' | 'capabilities', value: string): Body {
  return { ...body, [key]: stringsAt(body, key).filter((x) => x !== value) };
}

function label(target: DistroTarget): string {
  return target.kind === 'picked' ? target.distro : 'default';
}

interface Facts {
  /** Ubuntu's (the default's) `status` body, and Debian's. */
  readonly ubuntu: Body;
  readonly debian: Body;
}

function cleanupWorld(facts: Facts) {
  const calls: string[] = [];
  const store = new OutcomeStore();
  store.dropUnlisted(NAMES);
  store.set('status', answered('status', facts.ubuntu, 'Ubuntu'));
  store.setPicked(NAMES[1] as (typeof NAMES)[1], 'status', answered('status', facts.debian, 'Debian'));
  store.set('preview', answered('preview', headBody('preview'), 'Ubuntu'));
  const refusal: PreviewOutcome = { kind: 'rootBusy', distro: 'x' };
  const recorder = newCleanRecorder();
  recorder.answer = true;
  const never = (): Promise<never> => Promise.reject(new Error('not asked in this test'));
  const host = new CleanupHost({
    durable: new MapStore(),
    controller: {
      preview: (ids, target) => { calls.push(`preview ${ids.join(',')} ${label(target)}`); return Promise.resolve(refusal); },
      previewRestore: (_texts, target) => { calls.push(`previewRestore ${label(target)}`); return Promise.resolve(refusal); },
      confirm: never, stop: never,
      runFullCheck: (target) => { calls.push(`runFullCheck ${label(target)}`); return Promise.resolve({ kind: 'rootBusy', distro: 'x' }); },
    },
    read: never, outcomes: store, askStatus: never, refreshPanel: () => Promise.resolve(),
    focused: () => true, ui: recordingCleanUi(recorder), timers: new ManualTimers(), now: () => 0, wallNow: () => Date.parse('2026-10-05T10:00:00.000Z'), log: () => undefined,
  });

  return { host, calls, recorder };
}

const HEAD = (): Body => headBody('status');

test('Stop client-gone relays for Debian: allowed by DEBIAN\'s status when the default lacks A21; refused when Debian lacks it and the default has it', async () => {
  const allowed = cleanupWorld({ ubuntu: without(HEAD(), 'actions', 'A21'), debian: HEAD() });
  await allowed.host.stopRelays(DEBIAN);
  assert.deepEqual(allowed.calls, ['preview A21 Debian']);

  const refused = cleanupWorld({ ubuntu: HEAD(), debian: without(HEAD(), 'actions', 'A21') });
  await refused.host.stopRelays(DEBIAN);
  assert.deepEqual(refused.calls, []);
  assert.match(refused.recorder.notices.at(-1)?.sentence ?? '', /does not offer A21/);
});

test('Run full check for Debian: gated by Debian\'s capabilities, not the default\'s', async () => {
  const allowed = cleanupWorld({ ubuntu: without(HEAD(), 'capabilities', 'act.detach'), debian: HEAD() });
  await allowed.host.runFullCheck(DEBIAN);
  assert.deepEqual(allowed.calls, ['runFullCheck Debian']);

  const refused = cleanupWorld({ ubuntu: HEAD(), debian: without(HEAD(), 'capabilities', 'act.detach') });
  await refused.host.runFullCheck(DEBIAN);
  assert.deepEqual(refused.calls, []);
  assert.match(refused.recorder.notices.at(-1)?.sentence ?? '', /Run full check now is not available/);
});

test('Archive now (cleanup side) for Debian: the cleanup controls of DEBIAN\'s status decide', async () => {
  const allowed = cleanupWorld({ ubuntu: without(HEAD(), 'capabilities', 'act.detach'), debian: HEAD() });
  await allowed.host.archiveNow(DEBIAN);
  assert.deepEqual(allowed.calls, ['preview A13 Debian']);

  const refused = cleanupWorld({ ubuntu: HEAD(), debian: without(HEAD(), 'capabilities', 'act.detach') });
  await refused.host.archiveNow(DEBIAN);
  assert.deepEqual(refused.calls, []);
  assert.match(refused.recorder.notices.at(-1)?.sentence ?? '', /Archive now is not available/);
});

test('Restore for Debian: A20 must be offered by Debian\'s status, whatever the default offers', async () => {
  const allowed = cleanupWorld({ ubuntu: without(HEAD(), 'actions', 'A20'), debian: HEAD() });
  await allowed.host.restore(['0123456789abcdef'], DEBIAN);
  assert.deepEqual(allowed.calls, ['previewRestore Debian']);

  const refused = cleanupWorld({ ubuntu: HEAD(), debian: without(HEAD(), 'actions', 'A20') });
  await refused.host.restore(['0123456789abcdef'], DEBIAN);
  assert.deepEqual(refused.calls, []);
  assert.match(refused.recorder.notices.at(-1)?.sentence ?? '', /Restore is not available: this daemon does not report A20/);
});

test('the default flows are unchanged: they read the default\'s status', async () => {
  const w = cleanupWorld({ ubuntu: without(HEAD(), 'actions', 'A21'), debian: HEAD() });
  await w.host.stopRelays(DEFAULT_TARGET);
  assert.deepEqual(w.calls, []);
  assert.equal(w.host.controls().enabled, true);
  assert.equal(w.host.controls(DEBIAN).enabled, true);
});

// ---- the archive side ----

function archiveWorld(facts: Facts) {
  const store = new OutcomeStore();
  store.dropUnlisted(NAMES);
  store.set('status', answered('status', facts.ubuntu, 'Ubuntu'));
  store.setPicked(NAMES[1] as (typeof NAMES)[1], 'status', answered('status', facts.debian, 'Debian'));
  const recorder = newArchiveRecorder();
  const reads: string[] = [];
  const archived: string[] = [];
  const archive = new ArchiveHost({
    read: (request, target): Promise<ReadOutcome> => {
      reads.push(`${label(target)} ${request.read}`);
      return Promise.resolve({ kind: 'read', read: request.read, distro: 'x', body: goldenFile('archive-status.json') });
    },
    target: () => Promise.reject(new Error('not asked')), runner: () => Promise.reject(new Error('not asked')),
    outcomes: store, ui: recordingArchiveUi(recorder), log: () => undefined,
    archiveNow: (target) => { archived.push(label(target)); return Promise.resolve(undefined); },
  });

  return { archive, recorder, reads, archived };
}

test('Archive now (archive side) for Debian: DEBIAN\'s capabilities and A13 decide, and the picked distro\'s own archive status is read for its folder', async () => {
  const allowed = archiveWorld({ ubuntu: without(HEAD(), 'capabilities', 'archive.run'), debian: HEAD() });
  await allowed.archive.archiveNow(DEBIAN);
  assert.deepEqual(allowed.archived, ['Debian']);
  assert.deepEqual(allowed.reads, ['Debian archiveStatus'], 'the default\'s view was not used for the folder');

  const missingCapability = archiveWorld({ ubuntu: HEAD(), debian: without(HEAD(), 'capabilities', 'archive.run') });
  await missingCapability.archive.archiveNow(DEBIAN);
  assert.deepEqual(missingCapability.archived, []);
  assert.match(missingCapability.recorder.notices.at(-1)?.sentence ?? '', /does not advertise archive\.run/);

  const noA13 = archiveWorld({ ubuntu: HEAD(), debian: without(HEAD(), 'actions', 'A13') });
  await noA13.archive.archiveNow(DEBIAN);
  assert.deepEqual(noA13.archived, []);
  assert.match(noA13.recorder.notices.at(-1)?.sentence ?? '', /does not offer A13/);
});

test('Archive now for the default is still judged by the default\'s status and view', async () => {
  const w = archiveWorld({ ubuntu: without(HEAD(), 'actions', 'A13'), debian: HEAD() });
  await w.archive.archiveNow(DEFAULT_TARGET);
  assert.deepEqual(w.archived, []);
  assert.deepEqual(w.reads, []);
});
