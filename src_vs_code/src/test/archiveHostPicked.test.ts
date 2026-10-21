import assert from 'node:assert/strict';
import { test } from 'node:test';

import { ArchiveHost } from '../archive/archiveHost';
import { newArchiveRecorder, recordingArchiveUi } from '../archive/archiveRecorder';
import type { ReadOutcome } from '../client/outcome';
import { OutcomeStore } from '../state/outcomeStore';
import { DEFAULT_TARGET, type DistroTarget } from '../wsl/distroTarget';
import { listed, pick } from './support/distroNames';
import { goldenOutcomes } from './support/outcomes';
import { goldenFile } from './support/paths';
import { exited } from './support/recordingRunner';

/**
 * E18.W1a-3 (todo/PLAN_disk_health_E18.md §3.5, RED 19): the archive's two flows and *Archive now* for a PICKED distribution. The flow's
 * judged-folder read, its resolved target and the config write all name the distribution the caller picked; the host's VIEW (its
 * refresh) stays the default's — a per-distro archive card is W1b's.
 */

const FILES: Readonly<Record<string, string>> = { archiveStatus: 'archive-status.json', archivePreview: 'archive-preview.json', archiveCheckBase: 'archive-check-base.json' };
const WSL = 'C:\\Windows\\System32\\wsl.exe';

function label(target: DistroTarget): string {
  return target.kind === 'picked' ? target.distro : 'default';
}

function nameOf(target: DistroTarget): string {
  return target.kind === 'picked' ? target.distro : 'Ubuntu';
}

function host() {
  const store = new OutcomeStore();
  const status = goldenOutcomes().status;
  store.set('status', status);
  store.dropUnlisted([listed('Ubuntu'), listed('Debian')]);
  store.setPicked(listed('Debian'), 'status', status);
  const recorder = newArchiveRecorder();
  const reads: string[] = [];
  const targets: string[] = [];
  const writes: string[] = [];
  const archivedFor: string[] = [];
  const archive = new ArchiveHost({
    read: (request, target): Promise<ReadOutcome> => {
      reads.push(`${label(target)} ${request.read}`);
      return Promise.resolve({ kind: 'read', read: request.read, distro: nameOf(target), body: goldenFile(FILES[request.read] ?? '') });
    },
    target: (target) => { targets.push(label(target)); return Promise.resolve({ wsl: WSL, distro: nameOf(target) }); },
    runner: (request) => { writes.push(request.args.join(' ')); return Promise.resolve(exited(0, 'archive.baseFolder = x (user)\n')); },
    outcomes: store, ui: recordingArchiveUi(recorder), log: () => undefined, cleanupFree: () => true,
    archiveNow: (target) => { archivedFor.push(label(target)); return Promise.resolve(undefined); },
  });

  return { archive, recorder, reads, targets, writes, archivedFor };
}

const DEBIAN = pick('Debian');

test('Choose archive folder for Debian: the judged-folder read, the resolved target and the config write all name Debian — and the default\'s view is not re-read', async () => {
  const h = host();
  h.recorder.picked = 'V:\\ai-archive';
  h.recorder.answer = true;
  assert.equal(await h.archive.choose(DEBIAN), 'written');
  assert.deepEqual(h.reads, ['Debian archiveCheckBase'], "the host's view is the default's: a write for a picked distro does not re-read it");
  assert.deepEqual(h.targets, ['Debian']);
  assert.equal(h.writes.length, 1);
  assert.ok(h.writes[0]?.startsWith('-d Debian --cd / --exec /opt/wsl-care/bin/wsl-care config set archive.baseFolder '), h.writes[0]);
});

test('Stop archiving for Debian writes the empty value to Debian', async () => {
  const h = host();
  h.recorder.answer = true;
  assert.equal(await h.archive.stop(DEBIAN), 'written');
  assert.deepEqual(h.targets, ['Debian']);
  assert.equal(h.writes[0], '-d Debian --cd / --exec /opt/wsl-care/bin/wsl-care config set archive.baseFolder ');
});

test('with DEFAULT_TARGET the flows are today\'s: the default is read, resolved and written', async () => {
  const h = host();
  h.recorder.picked = 'V:\\ai-archive';
  h.recorder.answer = true;
  await h.archive.choose(DEFAULT_TARGET);
  assert.deepEqual(h.reads.slice(0, 1), ['default archiveCheckBase']);
  assert.deepEqual(h.targets, ['default']);
  assert.ok(h.writes[0]?.startsWith('-d Ubuntu '), h.writes[0]);
});

test('Archive now hands the cleanup host the target it was asked for', async () => {
  const h = host();
  await h.archive.refresh();
  await h.archive.archiveNow(DEBIAN);
  await h.archive.archiveNow(DEFAULT_TARGET);
  assert.deepEqual(h.archivedFor, ['Debian', 'default']);
});
