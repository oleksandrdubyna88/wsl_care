import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

import { ArchiveHost } from '../archive/archiveHost';
import { newArchiveRecorder, recordingArchiveUi } from '../archive/archiveRecorder';
import type { ReadOutcome } from '../client/outcome';
import type { RunRead } from '../client/verbs';
import { parsePageMessage } from '../panel/messages';
import { OutcomeStore } from '../state/outcomeStore';
import { goldenOutcomes } from './support/outcomes';
import { GOLDEN_ROOT } from './support/paths';
import { exited } from './support/recordingRunner';

/**
 * Plan §15s, E10.S1: the archive's host — the two reads only on a refresh and only of a daemon that advertises them, ONE flow at
 * a time, the reads asked again after a write so the panel shows the daemon's reading, and a fault told rather than thrown.
 */

function golden(name: string): Record<string, unknown> {
  return JSON.parse(fs.readFileSync(path.join(GOLDEN_ROOT, 'head', name), 'utf8')) as Record<string, unknown>;
}

const FILES: Readonly<Record<string, string>> = { archiveStatus: 'archive-status.json', archivePreview: 'archive-preview.json', archiveCheckBase: 'archive-check-base.json' };

function host(options: { capabilities?: readonly string[]; readHolds?: Promise<void> } = {}) {
  const store = new OutcomeStore();
  const outcomes = goldenOutcomes();
  const status = outcomes.status;
  assert.ok(status.kind === 'answered' && status.answer.verb === 'status');
  store.set('status', options.capabilities === undefined ? status : { ...status, answer: { ...status.answer, body: { ...status.answer.body, capabilities: options.capabilities } } });
  const reads: RunRead[] = [];
  const recorder = newArchiveRecorder();
  const writes: string[][] = [];
  const logged: string[] = [];
  const archive = new ArchiveHost({
    read: async (request): Promise<ReadOutcome> => {
      reads.push(request);
      await options.readHolds;
      return { kind: 'read', read: request.read, distro: 'Ubuntu', body: golden(FILES[request.read] ?? '') };
    },
    target: () => Promise.resolve({ wsl: 'C:\\Windows\\System32\\wsl.exe', distro: 'Ubuntu' }),
    runner: (request) => { writes.push([...request.args]); return Promise.resolve(exited(0, 'archive.baseFolder = x (user)\n')); },
    outcomes: store, ui: recordingArchiveUi(recorder), log: (line) => logged.push(line),
  });

  return { archive, reads, recorder, writes, logged };
}

test('a refresh asks archive status and archive preview of a daemon that advertises them — and the view shows them', async () => {
  const h = host();
  assert.equal(h.archive.view().line, 'Archive: checking…');
  await h.archive.refresh();
  assert.deepEqual(h.reads.map((r) => r.read), ['archiveStatus', 'archivePreview']);
  assert.equal(h.archive.view().line, 'Archive folder: /mnt/v/ai-archive');
});

test('a daemon that advertises neither is not asked at all', async () => {
  const h = host({ capabilities: ['act.detach'] });
  await h.archive.refresh();
  assert.deepEqual(h.reads, []);
  assert.equal(h.archive.view().line, 'Archive: this daemon has no AI-session archive — update the daemon');
});

test('two refreshes at once share one pair of reads', async () => {
  let release: () => void = () => undefined;
  const holds = new Promise<void>((resolve) => { release = resolve; });
  const h = host({ readHolds: holds });
  const first = h.archive.refresh();
  const second = h.archive.refresh();
  release();
  await Promise.all([first, second]);
  assert.equal(h.reads.length, 2);
});

test('a written folder is followed by the reads again — the panel shows the daemon\'s reading, not the flow\'s belief', async () => {
  const h = host();
  h.recorder.picked = 'V:\\ai-archive';
  h.recorder.answer = true;
  assert.equal(await h.archive.choose(), 'written');
  assert.deepEqual(h.reads.map((r) => r.read), ['archiveCheckBase', 'archiveStatus', 'archivePreview']);
  assert.equal(h.writes.length, 1);
});

test('one flow at a time: a second press while one runs is told, and starts nothing', async () => {
  let pick: (value: string | undefined) => void = () => undefined;
  const h = host();
  const ui = recordingArchiveUi(h.recorder);
  const held = new ArchiveHost({
    read: () => Promise.resolve({ kind: 'read', read: 'archiveCheckBase', distro: 'Ubuntu', body: golden('archive-check-base.json') }),
    target: () => Promise.resolve({ wsl: 'w', distro: 'Ubuntu' }), runner: () => Promise.resolve(exited(0, '')), outcomes: new OutcomeStore(),
    ui: { ...ui, pickFolder: () => new Promise((resolve) => { pick = resolve; }) }, log: () => undefined,
  });
  const first = held.choose();
  assert.equal(await held.stop(), undefined);
  assert.match(h.recorder.notices.at(-1)?.sentence ?? '', /already being changed/);
  pick(undefined);
  assert.equal(await first, 'cancelled');
});

test('a fault inside a flow is told and logged, never thrown', async () => {
  const h = host();
  const broken = new ArchiveHost({
    read: () => Promise.reject(new Error('boom')), target: () => Promise.resolve({ wsl: 'w', distro: 'Ubuntu' }), runner: () => Promise.resolve(exited(0, '')),
    outcomes: new OutcomeStore(), ui: { ...recordingArchiveUi(h.recorder), pickFolder: () => Promise.resolve('V:\\a') }, log: (line) => h.logged.push(line),
  });
  assert.equal(await broken.choose(), undefined);
  assert.match(h.logged[0] ?? '', /^archive: choosing the archive folder failed: boom/);
  assert.equal(h.recorder.notices.at(-1)?.level, 'error');
  assert.match(broken.view().line, /checking/, 'the busy text is gone after the fault');
});

test('the page may ask for the two flows only as BARE messages — a path or any payload is dropped', () => {
  assert.deepEqual(parsePageMessage({ type: 'chooseArchiveFolder' }), { type: 'chooseArchiveFolder' });
  assert.deepEqual(parsePageMessage({ type: 'stopArchiving' }), { type: 'stopArchiving' });
  assert.equal(parsePageMessage({ type: 'chooseArchiveFolder', path: 'V:\\x' }), undefined);
  assert.equal(parsePageMessage({ type: 'stopArchiving', value: '' }), undefined);
});
