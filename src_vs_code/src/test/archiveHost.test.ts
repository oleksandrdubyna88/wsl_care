import assert from 'node:assert/strict';
import { test } from 'node:test';

import { ArchiveHost } from '../archive/archiveHost';
import { newArchiveRecorder, recordingArchiveUi } from '../archive/archiveRecorder';
import type { ReadOutcome } from '../client/outcome';
import type { RunRead } from '../client/verbs';
import { parsePageMessage } from '../panel/messages';
import { OutcomeStore } from '../state/outcomeStore';
import { stringsAt } from './support/body';
import { goldenOutcomes } from './support/outcomes';
import { goldenFile } from './support/paths';
import { exited } from './support/recordingRunner';

/**
 * Plan §15s, E10.S1: the archive's host — the two reads only on a refresh and only of a daemon that advertises them, ONE flow at
 * a time, the reads asked again after a write so the panel shows the daemon's reading, and a fault told rather than thrown.
 */

const FILES: Readonly<Record<string, string>> = { archiveStatus: 'archive-status.json', archivePreview: 'archive-preview.json', archiveCheckBase: 'archive-check-base.json' };

interface HostOptions {
  readonly capabilities?: readonly string[];
  readHolds?: Promise<void>;
  /** The base folder `archive status` answers — the golden's unless given. */
  readonly baseFolder?: string;
  /** `status.actions` without A13 — a daemon that does not offer it. */
  readonly a13Offered?: boolean;
  readonly cleanupFree?: () => boolean;
  readonly onCleanupChange?: (listener: () => void) => () => void;
}

/** The panel's `status` over the head golden, with the capabilities and the actions `options` ask for. */
function panelStatus(options: HostOptions): Record<string, unknown> {
  const body = goldenOutcomes().status;
  assert.ok(body.kind === 'answered' && body.answer.verb === 'status');
  const head = body.answer.body;
  const capabilities = options.capabilities === undefined ? {} : { capabilities: options.capabilities };
  const actions = options.a13Offered === false ? { actions: stringsAt(head, 'actions').filter((id) => id !== 'A13') } : {};

  return { ...head, ...capabilities, ...actions };
}

function host(options: HostOptions = {}) {
  const store = new OutcomeStore();
  const status = goldenOutcomes().status;
  assert.ok(status.kind === 'answered' && status.answer.verb === 'status');
  store.set('status', { ...status, answer: { ...status.answer, body: panelStatus(options) } });
  const reads: RunRead[] = [];
  const recorder = newArchiveRecorder();
  const writes: string[][] = [];
  const logged: string[] = [];
  /** How many times the cleanup host's *Archive now* was asked. */
  const asked: number[] = [];
  const archive = new ArchiveHost({
    read: async (request): Promise<ReadOutcome> => {
      reads.push(request);
      await options.readHolds;
      const body = goldenFile(FILES[request.read] ?? '');
      return { kind: 'read', read: request.read, distro: 'Ubuntu', body: request.read === 'archiveStatus' && options.baseFolder !== undefined ? { ...body, baseFolder: options.baseFolder } : body };
    },
    target: () => Promise.resolve({ wsl: 'C:\\Windows\\System32\\wsl.exe', distro: 'Ubuntu' }),
    runner: (request) => { writes.push([...request.args]); return Promise.resolve(exited(0, 'archive.baseFolder = x (user)\n')); },
    outcomes: store, ui: recordingArchiveUi(recorder), log: (line) => logged.push(line),
    ...(options.cleanupFree === undefined ? {} : { cleanupFree: options.cleanupFree }),
    ...(options.onCleanupChange === undefined ? {} : { onCleanupChange: options.onCleanupChange }),
    archiveNow: () => { asked.push(1); return Promise.resolve(undefined); },
  });

  return { archive, reads, recorder, writes, logged, asked };
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
    read: () => Promise.resolve({ kind: 'read', read: 'archiveCheckBase', distro: 'Ubuntu', body: goldenFile('archive-check-base.json') }),
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

// ---- the code round (coai 237ecc90): findings 4 and 5 ----

test('code round #4: after a write whose ending is unknown or failed, the daemon is asked again', async () => {
  for (const ending of [{ kind: 'timedOut' as const, timeoutMs: 20_000, stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) }, exited(70, '', 'wsl-care: internal error: x\n')]) {
    const reads: string[] = [];
    const recorder = newArchiveRecorder();
    recorder.answer = true;
    const store = new OutcomeStore();
    store.set('status', goldenOutcomes().status);
    const h = new ArchiveHost({
      read: (request) => { reads.push(request.read); return Promise.resolve({ kind: 'read', read: request.read, distro: 'Ubuntu', body: goldenFile(FILES[request.read] ?? '') }); },
      target: () => Promise.resolve({ wsl: 'w', distro: 'Ubuntu' }), runner: () => Promise.resolve(ending), outcomes: store, ui: recordingArchiveUi(recorder), log: () => undefined,
    });
    await h.stop();
    assert.deepEqual(reads, ['archiveStatus', 'archivePreview'], ending.kind);
  }
});

test('code round #5: the view says "reading" while the two reads are in flight, and not after', async () => {
  let release: () => void = () => undefined;
  const holds = new Promise<void>((resolve) => { release = resolve; });
  const h = host({ readHolds: holds });
  const refreshing = h.archive.refresh();
  assert.notEqual(h.archive.view().reading, '');
  release();
  await refreshing;
  assert.equal(h.archive.view().reading, '');
});

// ---- E10.S1b: Archive now through the host (the own review on Fable, #1 and #6) ----

function archiveNowEnabled(archive: ArchiveHost): boolean | undefined {
  return archive.view().buttons.find((b) => b.id === 'archiveNow')?.enabled;
}

test('E10.S1b own review #1: Archive now is refused here, and told, when the archive\'s side of the button would grey it — the cleanup host is never asked', async () => {
  const noFolder = host({ baseFolder: '', cleanupFree: () => true });
  await noFolder.archive.refresh();
  await noFolder.archive.archiveNow();
  assert.deepEqual(noFolder.asked, [], 'no folder set: nothing started');
  assert.equal(noFolder.recorder.notices.at(-1)?.sentence, 'Archive now is not available: no archive folder is set.');

  const notOffered = host({ a13Offered: false, cleanupFree: () => true });
  await notOffered.archive.refresh();
  await notOffered.archive.archiveNow();
  assert.deepEqual(notOffered.asked, [], 'A13 not offered: nothing started');
  assert.equal(notOffered.recorder.notices.at(-1)?.sentence, 'Archive now is not available: the daemon does not offer A13.');

  const old = host({ capabilities: ['archive.checkBase', 'archive.preview', 'act.detach'], cleanupFree: () => true });
  await old.archive.refresh();
  await old.archive.archiveNow();
  assert.deepEqual(old.asked, [], 'archive.run not advertised: nothing started');
  assert.match(old.recorder.notices.at(-1)?.sentence ?? '', /^Archive now is not available: the daemon does not advertise .*archive\.run/);

  let pick: (value: string | undefined) => void = () => undefined;
  const busy = host({ cleanupFree: () => true });
  await busy.archive.refresh();
  const ui = recordingArchiveUi(busy.recorder);
  const held = new ArchiveHost({
    read: () => Promise.resolve({ kind: 'read', read: 'archiveCheckBase', distro: 'Ubuntu', body: goldenFile('archive-check-base.json') }),
    target: () => Promise.resolve({ wsl: 'w', distro: 'Ubuntu' }), runner: () => Promise.resolve(exited(0, '')), outcomes: new OutcomeStore(),
    ui: { ...ui, pickFolder: () => new Promise((resolve) => { pick = resolve; }) }, log: () => undefined, archiveNow: () => { busy.asked.push(1); return Promise.resolve(undefined); },
  });
  const choosing = held.choose();
  await held.archiveNow();
  assert.deepEqual(busy.asked, [], 'a flow busy: nothing started');
  assert.match(busy.recorder.notices.at(-1)?.sentence ?? '', /^Archive now is not available: /);
  pick(undefined);
  await choosing;
});

test('E10.S1b: with every archive condition holding, Archive now is handed to the cleanup host — which refuses its own side', async () => {
  const h = host({ cleanupFree: () => true });
  await h.archive.refresh();
  assert.equal(archiveNowEnabled(h.archive), true);
  await h.archive.archiveNow();
  assert.deepEqual(h.asked, [1]);
});

test('E10.S1b own review #6: a cleanup change re-renders the archive and greys Archive now through the host\'s view — and dispose stops listening', async () => {
  let free = true;
  const cleanupListeners = new Set<() => void>();
  const h = host({ cleanupFree: () => free, onCleanupChange: (listener) => { cleanupListeners.add(listener); return () => cleanupListeners.delete(listener); } });
  await h.archive.refresh();
  let renders = 0;
  h.archive.onChange(() => { renders += 1; });
  assert.equal(archiveNowEnabled(h.archive), true);
  free = false;
  cleanupListeners.forEach((listener) => listener());
  assert.equal(renders, 1, 'the cleanup\'s change re-rendered the archive');
  assert.equal(archiveNowEnabled(h.archive), false, 'a cleanup in flight greys the button');
  h.archive.dispose();
  assert.equal(cleanupListeners.size, 0, 'dispose unsubscribed from the cleanup');
});

test('own review #1: with the panel\'s status failed, the archive line says why after a refresh', async () => {
  const store = new OutcomeStore();
  store.set('status', { kind: 'stopped', verb: 'status', distro: 'Ubuntu' });
  const h = new ArchiveHost({
    read: () => Promise.reject(new Error('never asked')), target: () => Promise.resolve({ wsl: 'w', distro: 'Ubuntu' }), runner: () => Promise.resolve(exited(0, '')),
    outcomes: store, ui: recordingArchiveUi(newArchiveRecorder()), log: () => undefined,
  });
  await h.refresh();
  assert.equal(h.view().line, 'Archive: unavailable — WSL stopped');
});
