import assert from 'node:assert/strict';
import { test } from 'node:test';

import { ArchiveController } from '../../archivePage/archiveController';
import type { ArchivePageView } from '../../archivePage/archiveView';
import type { ReadOutcome, VerbOutcome } from '../../client/outcome';
import type { RunRead } from '../../client/verbs';
import { bodyOf, stringsAt } from '../support/body';
import { answered, headBody } from '../support/outcomes';
import { recordingRestore, type RecordingRestore } from '../support/archiveRestore';
import { goldenFile } from '../support/paths';

/**
 * Plan §15s, E10.S2a: the Archive page's host side — vscode-free. It reads `archive list --json` (and nothing else) on the page's
 * first `ready` and on *Refresh*, only of a daemon that advertises `archive.list`; an answer to an older read is dropped; the
 * open months are kept by agent and month across a refresh; a month index past the held list is told, never acted on; the
 * paths toggle is the host's.
 */

type Entry = Record<string, unknown>;

/** The golden list's one entry — every fixture entry below is this shape with some fields edited. */
function goldenEntry(): Entry {
  const entries = goldenFile('archive-list.json').entries;
  assert.ok(Array.isArray(entries), 'archive-list.json carries entries');
  return bodyOf(entries[0], 'archive-list.json entries[0]');
}

const ENTRY: Entry = goldenEntry();

/** The golden list answer with `entries` in place of its one — each entry the golden's shape, its fields edited. */
function listWith(entries: readonly Entry[]): Record<string, unknown> {
  return { ...goldenFile('archive-list.json'), entries: entries.map((e) => ({ ...ENTRY, ...e })) };
}

const TWO_MONTH_ENTRIES: readonly Entry[] = [
  { entryId: '00000000000000a1', agent: 'claude-code', month: '2000/01', key: 'projects/p/a.jsonl' },
  { entryId: '00000000000000a2', agent: 'claude-code', month: '2000/02', key: 'projects/p/b.jsonl' },
  { entryId: '00000000000000b1', agent: 'codex', month: '2000/02', key: 'sessions/2000/02/c.jsonl' },
];

const TWO_MONTHS = listWith(TWO_MONTH_ENTRIES);

function read(body: Record<string, unknown>): ReadOutcome {
  return { kind: 'read', read: 'archiveList', distro: 'Ubuntu', body };
}

interface Harness {
  readonly controller: ArchiveController;
  readonly asked: RunRead[];
  readonly views: ArchivePageView[];
  /** What the next read answers — a test replaces it. */
  readonly script: { answer: (request: RunRead) => Promise<ReadOutcome>; status: VerbOutcome | undefined };
  readonly restore: RecordingRestore;
}

function harness(status: VerbOutcome | undefined = answered('status', headBody('status'))): Harness {
  const asked: RunRead[] = [];
  const views: ArchivePageView[] = [];
  const script = { answer: (_request: RunRead): Promise<ReadOutcome> => Promise.resolve(read(goldenFile('archive-list.json'))), status };
  const restore = recordingRestore();
  const controller = new ArchiveController({ read: (request) => { asked.push(request); return script.answer(request); }, status: () => script.status, post: (view) => { views.push(view); }, restore });

  return { controller, asked, views, script, restore };
}

function last(h: Harness): ArchivePageView {
  const view = h.views.at(-1);
  assert.ok(view !== undefined, 'a view was posted');
  return view;
}

function monthLabels(view: ArchivePageView): string[] {
  return view.agents.flatMap((agent) => agent.months.map((m) => `${agent.agent} ${m.month}${m.expanded ? ' (open)' : ''}`));
}

test('the first ready reads `archive list` ONCE, and a returning tab is shown what is held, never read again', async () => {
  const h = harness();
  await h.controller.receive({ type: 'ready' });
  assert.deepEqual(h.asked, [{ read: 'archiveList' }]);
  assert.equal(last(h).state, 'answered');
  await h.controller.receive({ type: 'ready' });
  assert.equal(h.asked.length, 1, 'the second ready posts the held answer');
  await h.controller.receive({ type: 'refresh' });
  assert.equal(h.asked.length, 2, 'Refresh reads again');
});

test('a daemon that does not advertise archive.list is not asked, and the page says to update it', async () => {
  const status = headBody('status');
  const h = harness(answered('status', { ...status, capabilities: stringsAt(status, 'capabilities').filter((c) => c !== 'archive.list') }));
  await h.controller.receive({ type: 'ready' });
  assert.deepEqual(h.asked, []);
  assert.equal(last(h).state, 'unavailable');
  assert.match(last(h).notes.join(' '), /does not list its archive \(archive\.list\) — update the daemon/);
  assert.equal(last(h).notice, '', 'said once, as the note of the page — not twice (own review #7)');
});

test('a read that fails is said in the client\'s own words, never as an empty archive', async () => {
  const h = harness();
  h.script.answer = () => Promise.resolve({ kind: 'timedOut', read: 'archiveList', timeoutMs: 630_000 });
  await h.controller.receive({ type: 'ready' });
  assert.equal(last(h).state, 'failed');
  assert.deepEqual(last(h).agents, []);
  assert.match(last(h).notes.join(' '), /did not answer within 630 s/, 'the client sentence for a timeout');
});

test('an answer to an OLDER read is dropped — the newest read wins (a page closed and reopened while the first read ran)', async () => {
  const h = harness();
  let release: (value: ReadOutcome) => void = () => undefined;
  h.script.answer = () => new Promise((resolve) => { release = resolve; });
  const first = h.controller.receive({ type: 'refresh' });
  const releaseFirst = release;
  h.controller.pageClosed();
  h.script.answer = () => Promise.resolve(read(TWO_MONTHS));
  await h.controller.receive({ type: 'ready' });
  releaseFirst(read(goldenFile('archive-list.json')));
  await first;
  assert.equal(h.asked.length, 2);
  assert.equal(last(h).agents.length, 2, 'the newer answer (two agents) stands; the older one (one agent) was dropped');
});
test('a month is opened and closed by its INDEX into the list the host holds — and stays open across a refresh, by agent and month', async () => {
  const h = harness();
  h.script.answer = () => Promise.resolve(read(TWO_MONTHS));
  await h.controller.receive({ type: 'ready' });
  assert.deepEqual(monthLabels(last(h)), ['claude-code 2000/01', 'claude-code 2000/02', 'codex 2000/02']);
  await h.controller.receive({ type: 'expandMonth', index: 1, generation: last(h).generation });
  assert.deepEqual(monthLabels(last(h)), ['claude-code 2000/01', 'claude-code 2000/02 (open)', 'codex 2000/02']);
  assert.equal(last(h).agents[0]?.months[1]?.rows.length, 1, 'an open month lists its entries');
  assert.equal(last(h).agents[0]?.months[0]?.rows.length, 0, 'a closed month builds no rows');

  // The list moves under the open month: a month before it appears, so its index changes — the open month is still the same one.
  h.script.answer = () => Promise.resolve(read(listWith([{ entryId: '00000000000000a0', agent: 'claude-code', month: '1999/12', key: 'projects/p/z.jsonl' }, ...TWO_MONTH_ENTRIES])));
  await h.controller.receive({ type: 'refresh' });
  assert.deepEqual(monthLabels(last(h)), ['claude-code 1999/12', 'claude-code 2000/01', 'claude-code 2000/02 (open)', 'codex 2000/02']);
  await h.controller.receive({ type: 'collapseMonth', index: 2, generation: last(h).generation });
  assert.deepEqual(monthLabels(last(h)), ['claude-code 1999/12', 'claude-code 2000/01', 'claude-code 2000/02', 'codex 2000/02']);
});

test('a month index past the held list is told on the page, and nothing is read or opened', async () => {
  const h = harness();
  await h.controller.receive({ type: 'ready' });
  await h.controller.receive({ type: 'expandMonth', index: 7, generation: last(h).generation });
  assert.equal(h.asked.length, 1);
  assert.match(last(h).notice, /no longer lists that month/);
  assert.ok(last(h).agents.every((a) => a.months.every((m) => !m.expanded)));
});

test('a message outside the closed set is dropped: nothing is read, nothing is posted', async () => {
  const h = harness();
  for (const raw of [{ type: 'restore', entryIds: ['0123456789abcdef'] }, { type: 'expandMonth', index: 0 }, { type: 'expandMonth', month: '2000/01' }, { type: 'refresh', argv: ['--restorable'] }, null]) {
    await h.controller.receive(raw);
  }
  assert.deepEqual(h.asked, []);
  assert.deepEqual(h.views, []);
});

test('the full session paths are the host\'s toggle — off at first, on after showPaths, off again after hidePaths', async () => {
  const h = harness();
  await h.controller.receive({ type: 'ready' });
  await h.controller.receive({ type: 'expandMonth', index: 0, generation: last(h).generation });
  assert.equal(last(h).pathsShown, false);
  assert.equal(last(h).agents[0]?.months[0]?.rows[0]?.cells[0], '…/s1.jsonl');
  await h.controller.receive({ type: 'showPaths' });
  assert.equal(last(h).pathsShown, true);
  assert.equal(last(h).agents[0]?.months[0]?.rows[0]?.cells[0], 'projects/p/s1.jsonl');
  await h.controller.receive({ type: 'hidePaths' });
  assert.equal(last(h).agents[0]?.months[0]?.rows[0]?.cells[0], '…/s1.jsonl');
  assert.equal(h.asked.length, 1, 'the toggle reads nothing');
});

test('rendered is recorded for the extension-host scenarios', async () => {
  const h = harness();
  await h.controller.receive({ type: 'ready' });
  await h.controller.receive({ type: 'rendered', blocks: 2 });
  assert.equal(h.controller.lastRendered(), 2);
});

/** The golden status, with archive.list advertised or not. */
function statusListing(lists: boolean): VerbOutcome {
  const status = headBody('status');
  return answered('status', { ...status, capabilities: stringsAt(status, 'capabilities').filter((c) => lists || c !== 'archive.list') });
}

test('own review #3: a status that newly advertises archive.list reads the list; one that drops it ends the held list; an unchanged one posts nothing', async () => {
  const h = harness(statusListing(false));
  await h.controller.receive({ type: 'ready' });
  assert.equal(last(h).state, 'unavailable');
  h.script.status = statusListing(true);
  await h.controller.statusChanged();
  assert.deepEqual(h.asked, [{ read: 'archiveList' }], 'the gate opened: the list is read');
  assert.equal(last(h).state, 'answered');
  const posts = h.views.length;
  await h.controller.statusChanged();
  assert.equal(h.views.length, posts, 'nothing changed for the page: no rebuild');
  h.script.status = statusListing(false);
  await h.controller.statusChanged();
  assert.equal(last(h).state, 'unavailable', 'the gate closed: the held list is not shown as current');
});

test('own review #5: Refresh while a read is in flight starts no second read — and the view greys Refresh meanwhile', async () => {
  const h = harness();
  let release: (value: ReadOutcome) => void = () => undefined;
  h.script.answer = () => new Promise((resolve) => { release = resolve; });
  const first = h.controller.receive({ type: 'refresh' });
  assert.equal(last(h).refreshEnabled, false, 'reading: Refresh greyed');
  // The second Refresh SHARES the read in flight — awaiting it here would wait for that read, so it is awaited after the release.
  const second = h.controller.receive({ type: 'refresh' });
  await h.controller.receive({ type: 'ready' });
  assert.equal(h.asked.length, 1, 'one wsl.exe for the read in flight, however often Refresh is pressed');
  release(read(goldenFile('archive-list.json')));
  await Promise.all([first, second]);
  assert.equal(last(h).refreshEnabled, true);
});

test('own review #7: a read in flight when the gate closes does not overwrite the "update the daemon" state', async () => {
  const h = harness();
  let release: (value: ReadOutcome) => void = () => undefined;
  h.script.answer = () => new Promise((resolve) => { release = resolve; });
  const first = h.controller.receive({ type: 'refresh' });
  h.script.status = statusListing(false);
  await h.controller.statusChanged();
  release(read(goldenFile('archive-list.json')));
  await first;
  assert.equal(last(h).state, 'unavailable');
});

test('own review #2: a page closed and opened again starts afresh — one new read, no month open, the session paths SHORT again', async () => {
  const h = harness();
  await h.controller.receive({ type: 'ready' });
  await h.controller.receive({ type: 'expandMonth', index: 0, generation: last(h).generation });
  await h.controller.receive({ type: 'showPaths' });
  assert.equal(last(h).pathsShown, true);
  h.controller.pageClosed();
  await h.controller.receive({ type: 'ready' });
  assert.equal(h.asked.length, 2, 'the reopened page reads the list again');
  assert.equal(last(h).pathsShown, false, 'the full paths are off on every open');
  assert.ok(last(h).agents.every((a) => a.months.every((m) => !m.expanded)));
});

// ---- E10.S2b: the restore selection and its button ----

/** A verified entry removed at its source — selectable — in `month`, archived at `at`. */
function restorable(id: string, month = '2000/01', at = '2000-01-01T00:00:00+00:00'): Entry {
  return { entryId: id, agent: 'claude-code', month, key: `projects/p/${id}.jsonl`, status: 'sourceRemoved', verified: true, archivedAtUtc: at };
}

const R1 = '00000000000000a1';
const R2 = '00000000000000a2';
const R3 = '00000000000000a3';

/** The session cells of the rows the view shows selected (open months only). */
function selectedRows(view: ArchivePageView): string[] {
  return view.agents.flatMap((a) => a.months.flatMap((m) => m.rows.filter((r) => r.selected).map((r) => r.cells[0] ?? '')));
}

async function openAll(h: Harness): Promise<void> {
  const months = last(h).agents.flatMap((a) => a.months);
  for (const month of months) {
    await h.controller.receive({ type: 'expandMonth', index: month.index, generation: last(h).generation });
  }
}

function statusWithShown(maxShownNames: number): VerbOutcome {
  const status = headBody('status');
  return answered('status', { ...status, limits: { ...bodyOf(status.limits, 'limits'), maxShownNames } });
}

test('an index from an OLDER view is refused and told — never applied to a list it was not chosen from', async () => {
  const h = harness();
  h.script.answer = () => Promise.resolve(read(listWith([restorable(R1), restorable(R2, '2000/02')])));
  await h.controller.receive({ type: 'ready' });
  const old = last(h).generation;
  await h.controller.receive({ type: 'refresh' });
  assert.notEqual(last(h).generation, old, 'a new read is a new generation');
  for (const message of [{ type: 'expandMonth', index: 0, generation: old }, { type: 'toggleEntry', index: 0, generation: old }, { type: 'selectMonth', index: 0, generation: old }]) {
    await h.controller.receive(message);
    assert.match(last(h).notice, /list changed since the page was drawn/, JSON.stringify(message));
  }
  assert.ok(last(h).agents.every((a) => a.months.every((m) => !m.expanded)));
  assert.equal(last(h).selection.count, 0);
});

test('an entry is selected BY ID: a refreshed list whose indexes moved keeps it selected; one that no longer holds it drops it and says so', async () => {
  const h = harness();
  h.script.answer = () => Promise.resolve(read(listWith([restorable(R1), restorable(R2)])));
  await h.controller.receive({ type: 'ready' });
  await h.controller.receive({ type: 'toggleEntry', index: 1, generation: last(h).generation });
  assert.equal(last(h).selection.count, 1);
  h.script.answer = () => Promise.resolve(read(listWith([restorable('00000000000000a0'), restorable(R1), restorable(R2)])));
  await h.controller.receive({ type: 'refresh' });
  await openAll(h);
  assert.deepEqual(selectedRows(last(h)), [`…/${R2}.jsonl`], 'the same session, at its new index');
  h.script.answer = () => Promise.resolve(read(listWith([restorable(R1), { ...restorable(R2), status: 'restored' }])));
  await h.controller.receive({ type: 'refresh' });
  assert.equal(last(h).selection.count, 0);
  assert.match(last(h).notice, /^1 selected session can no longer be restored and was taken out of the selection\.$/);
});

test('an unverified entry, one still at its source, or an id not of the daemon shape is never selectable — refused and told', async () => {
  const h = harness();
  h.script.answer = () => Promise.resolve(read(listWith([
    { ...restorable(R1), verified: false },
    { ...restorable(R2), status: 'archived' },
    { ...restorable(R3), entryId: 'NOT-AN-ENTRY-ID!' },
  ])));
  await h.controller.receive({ type: 'ready' });
  await openAll(h);
  assert.ok(last(h).agents[0]?.months[0]?.rows.every((r) => !r.selectable));
  for (const index of [0, 1, 2]) {
    await h.controller.receive({ type: 'toggleEntry', index, generation: last(h).generation });
    assert.match(last(h).notice, /cannot be restored from here/);
  }
  assert.equal(last(h).selection.count, 0);
  assert.equal(last(h).agents[0]?.months[0]?.selectEnabled, false, 'Select month has nothing to take');
});

test('the ceiling is min(restoreCeiling, status.limits.maxShownNames): past it a toggle is refused, a lowered cap greys Restore, a list without restoreCeiling asks for a newer daemon', async () => {
  const h = harness(statusWithShown(2));
  h.script.answer = () => Promise.resolve(read(listWith([restorable(R1), restorable(R2), restorable(R3)])));
  await h.controller.receive({ type: 'ready' });
  for (const index of [0, 1]) {
    await h.controller.receive({ type: 'toggleEntry', index, generation: last(h).generation });
  }
  assert.equal(last(h).selection.summary, '2 selected — one restore takes at most 2.');
  assert.equal(last(h).selection.restoreEnabled, true);
  await h.controller.receive({ type: 'toggleEntry', index: 2, generation: last(h).generation });
  assert.match(last(h).notice, /^One restore takes at most 2 /);
  assert.equal(last(h).selection.count, 2);
  h.script.status = statusWithShown(1);
  await h.controller.statusChanged();
  assert.equal(last(h).selection.summary, '2 selected — one restore takes at most 1; select fewer.');
  assert.equal(last(h).selection.restoreEnabled, false);
  await h.controller.receive({ type: 'restore' });
  assert.match(last(h).notice, /at most 1 — select fewer/);
  assert.deepEqual(h.restore.started, [], 'nothing handed to the cleanup host');
  const old = harness();
  old.script.answer = () => Promise.resolve(read({ ...listWith([restorable(R1)]), restoreCeiling: undefined }));
  await old.controller.receive({ type: 'ready' });
  assert.match(last(old).selection.summary, /does not say how many sessions one restore takes — update the daemon/);
  await old.controller.receive({ type: 'toggleEntry', index: 0, generation: last(old).generation });
  assert.equal(last(old).selection.count, 0);
});

test('Select month takes the selectable sessions of the month — the NEWEST when they do not fit the room left — and says so', async () => {
  const h = harness(statusWithShown(2));
  h.script.answer = () => Promise.resolve(read(listWith([
    restorable(R1, '2000/01', '2000-01-03T00:00:00+00:00'),
    restorable(R2, '2000/01', '2000-01-09T00:00:00+00:00'),
    restorable(R3, '2000/01', '2000-01-05T00:00:00+00:00'),
    { ...restorable('00000000000000a4'), verified: false },
  ])));
  await h.controller.receive({ type: 'ready' });
  await h.controller.receive({ type: 'selectMonth', index: 0, generation: last(h).generation });
  const notice = last(h).notice;
  await openAll(h);
  assert.deepEqual(selectedRows(last(h)).sort(), [`…/${R2}.jsonl`, `…/${R3}.jsonl`], 'the two newest of three; the unverified one never');
  assert.match(notice, /holds 3 sessions to restore; the newest 2 were selected — one restore takes at most 2/);
  assert.equal(last(h).agents[0]?.months[0]?.selectEnabled, false, 'no room left');
  await h.controller.receive({ type: 'clearSelection' });
  assert.equal(last(h).selection.count, 0);
  assert.equal(last(h).selection.clearEnabled, false);
});

test('Restore hands the HOST selection, in the order of the list, as text to the cleanup host — refused with nothing selected, while a restore runs, or while the host blocks it', async () => {
  const h = harness();
  h.script.answer = () => Promise.resolve(read(listWith([restorable(R1), restorable(R2), restorable(R3)])));
  await h.controller.receive({ type: 'ready' });
  await h.controller.receive({ type: 'restore' });
  assert.match(last(h).notice, /Select at least one session/);
  for (const index of [2, 0]) {
    await h.controller.receive({ type: 'toggleEntry', index, generation: last(h).generation });
  }
  h.restore.facts = () => ({ inFlight: false, state: '', blocker: 'a run is in flight' });
  assert.equal(h.controller.view().selection.restoreEnabled, false);
  assert.equal(h.controller.view().selection.line, 'Restore is not available: a run is in flight.');
  await h.controller.receive({ type: 'restore' });
  assert.match(last(h).notice, /Restore is not available: a run is in flight/);
  h.restore.facts = () => ({ inFlight: true, state: 'Restoring… archived sessions', blocker: 'a run is in flight' });
  assert.equal(h.controller.view().selection.line, 'Restoring… archived sessions');
  await h.controller.receive({ type: 'restore' });
  assert.match(last(h).notice, /already running/);
  assert.deepEqual(h.restore.started, []);
  h.restore.facts = () => ({ inFlight: false, state: '', blocker: '' });
  await h.controller.receive({ type: 'restore' });
  assert.deepEqual(h.restore.started, [[R1, R3]], 'the order of the list, the ids as text');
});

test('when the restore stops being in flight the list is read again and the selection cleared; a fact that changes nothing posts nothing', async () => {
  const h = harness();
  h.script.answer = () => Promise.resolve(read(listWith([restorable(R1)])));
  await h.controller.receive({ type: 'ready' });
  await h.controller.receive({ type: 'toggleEntry', index: 0, generation: last(h).generation });
  h.restore.facts = () => ({ inFlight: true, state: 'Restoring… archived sessions', blocker: 'a run is in flight' });
  await h.controller.restoreChanged();
  assert.equal(last(h).selection.line, 'Restoring… archived sessions', 'the line follows the run');
  const posts = h.views.length;
  await h.controller.restoreChanged();
  assert.equal(h.views.length, posts, 'nothing changed: no rebuild');
  h.restore.facts = () => ({ inFlight: false, state: '', blocker: '' });
  h.script.answer = () => Promise.resolve(read(listWith([{ ...restorable(R1), status: 'restored' }])));
  await h.controller.restoreChanged();
  assert.equal(h.asked.length, 2, 'read again at its end');
  assert.equal(last(h).selection.count, 0);
  assert.equal(last(h).notice, '', 'a cleared selection drops nothing to report');
});

// ---- E10.S2b code round ----

test('code round #0: a cleanup-host change that leaves the selection bar as it is builds no archive view — 200 of them over 60 000 entries stay well under a second', async () => {
  const h = harness();
  const entries = Array.from({ length: 60_000 }, (_, i) => restorable(i.toString(16).padStart(16, '0')));
  h.script.answer = () => Promise.resolve(read(listWith(entries)));
  await h.controller.receive({ type: 'ready' });
  await h.controller.receive({ type: 'expandMonth', index: 0, generation: last(h).generation });
  const posts = h.views.length;
  const started = performance.now();
  for (let i = 0; i < 200; i += 1) {
    await h.controller.restoreChanged();
  }
  const took = performance.now() - started;
  assert.equal(h.views.length, posts, 'nothing changed: nothing posted');
  assert.ok(took < 1000, `200 unchanged host changes took ${Math.round(took)} ms`);
});

test('code round #1: while Restore waits for the daemon preview the page says so and greys Restore — then the line clears', async () => {
  const h = harness();
  h.script.answer = () => Promise.resolve(read(listWith([restorable(R1)])));
  await h.controller.receive({ type: 'ready' });
  await h.controller.receive({ type: 'toggleEntry', index: 0, generation: last(h).generation });
  let release: () => void = () => undefined;
  h.restore.start = () => new Promise<void>((resolve) => { release = resolve; });
  const pressed = h.controller.receive({ type: 'restore' });
  assert.equal(last(h).selection.line, 'Previewing the restore… the modal opens when the daemon answers.');
  assert.equal(last(h).selection.restoreEnabled, false);
  await h.controller.receive({ type: 'restore' });
  assert.match(last(h).notice, /already being previewed/);
  release();
  await pressed;
  assert.equal(last(h).selection.line, '');
  assert.equal(last(h).selection.restoreEnabled, true);
});

// ---- the own review on Fable ----

test('own review #4: a restore that ends while a list read is in flight reads AGAIN after it — never only the older answer', async () => {
  const h = harness();
  h.script.answer = () => Promise.resolve(read(listWith([restorable(R1)])));
  await h.controller.receive({ type: 'ready' });
  h.restore.facts = () => ({ inFlight: true, state: 'Restoring… archived sessions', blocker: 'a run is in flight' });
  await h.controller.restoreChanged();
  let release: (value: ReadOutcome) => void = () => undefined;
  h.script.answer = () => new Promise((resolve) => { release = resolve; });
  const older = h.controller.receive({ type: 'refresh' });
  h.restore.facts = () => ({ inFlight: false, state: '', blocker: '' });
  h.script.answer = () => Promise.resolve(read(listWith([{ ...restorable(R1), status: 'restored' }])));
  const ended = h.controller.restoreChanged();
  release(read(listWith([restorable(R1)])));
  await Promise.all([older, ended]);
  assert.equal(h.asked.length, 3, 'the first ready, the read in flight, and one more after it');
  await openAll(h);
  assert.equal(last(h).agents[0]?.months[0]?.rows[0]?.cells[3], 'restored');
});

test('own review #5: Select is refused while a restore runs — its end would clear the selection silently', async () => {
  const h = harness();
  h.script.answer = () => Promise.resolve(read(listWith([restorable(R1)])));
  await h.controller.receive({ type: 'ready' });
  h.restore.facts = () => ({ inFlight: true, state: 'Restoring… archived sessions', blocker: 'a run is in flight' });
  await h.controller.receive({ type: 'toggleEntry', index: 0, generation: last(h).generation });
  assert.equal(last(h).selection.count, 0);
  assert.match(last(h).notice, /A restore is running/);
});

test('own review #6: a list naming one session twice hands its id ONCE — never a duplicate the controller refuses whole', async () => {
  const h = harness();
  h.script.answer = () => Promise.resolve(read(listWith([restorable(R1), restorable(R1)])));
  await h.controller.receive({ type: 'ready' });
  await h.controller.receive({ type: 'toggleEntry', index: 0, generation: last(h).generation });
  await h.controller.receive({ type: 'restore' });
  assert.deepEqual(h.restore.started, [[R1]]);
});
