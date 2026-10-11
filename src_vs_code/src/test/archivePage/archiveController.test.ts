import assert from 'node:assert/strict';
import { test } from 'node:test';

import { ArchiveController } from '../../archivePage/archiveController';
import type { ArchivePageView } from '../../archivePage/archiveView';
import type { ReadOutcome, VerbOutcome } from '../../client/outcome';
import type { RunRead } from '../../client/verbs';
import { bodyOf, stringsAt } from '../support/body';
import { answered, headBody } from '../support/outcomes';
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
}

function harness(status: VerbOutcome | undefined = answered('status', headBody('status'))): Harness {
  const asked: RunRead[] = [];
  const views: ArchivePageView[] = [];
  const script = { answer: (_request: RunRead): Promise<ReadOutcome> => Promise.resolve(read(goldenFile('archive-list.json'))), status };
  const controller = new ArchiveController({ read: (request) => { asked.push(request); return script.answer(request); }, status: () => script.status, post: (view) => { views.push(view); } });

  return { controller, asked, views, script };
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
  await h.controller.receive({ type: 'expandMonth', index: 1 });
  assert.deepEqual(monthLabels(last(h)), ['claude-code 2000/01', 'claude-code 2000/02 (open)', 'codex 2000/02']);
  assert.equal(last(h).agents[0]?.months[1]?.rows.length, 1, 'an open month lists its entries');
  assert.equal(last(h).agents[0]?.months[0]?.rows.length, 0, 'a closed month builds no rows');

  // The list moves under the open month: a month before it appears, so its index changes — the open month is still the same one.
  h.script.answer = () => Promise.resolve(read(listWith([{ entryId: '00000000000000a0', agent: 'claude-code', month: '1999/12', key: 'projects/p/z.jsonl' }, ...TWO_MONTH_ENTRIES])));
  await h.controller.receive({ type: 'refresh' });
  assert.deepEqual(monthLabels(last(h)), ['claude-code 1999/12', 'claude-code 2000/01', 'claude-code 2000/02 (open)', 'codex 2000/02']);
  await h.controller.receive({ type: 'collapseMonth', index: 2 });
  assert.deepEqual(monthLabels(last(h)), ['claude-code 1999/12', 'claude-code 2000/01', 'claude-code 2000/02', 'codex 2000/02']);
});

test('a month index past the held list is told on the page, and nothing is read or opened', async () => {
  const h = harness();
  await h.controller.receive({ type: 'ready' });
  await h.controller.receive({ type: 'expandMonth', index: 7 });
  assert.equal(h.asked.length, 1);
  assert.match(last(h).notice, /no longer lists that month/);
  assert.ok(last(h).agents.every((a) => a.months.every((m) => !m.expanded)));
});

test('a message outside the closed set is dropped: nothing is read, nothing is posted', async () => {
  const h = harness();
  for (const raw of [{ type: 'restore' }, { type: 'expandMonth', month: '2000/01' }, { type: 'refresh', argv: ['--restorable'] }, null]) {
    await h.controller.receive(raw);
  }
  assert.deepEqual(h.asked, []);
  assert.deepEqual(h.views, []);
});

test('the full session paths are the host\'s toggle — off at first, on after showPaths, off again after hidePaths', async () => {
  const h = harness();
  await h.controller.receive({ type: 'ready' });
  await h.controller.receive({ type: 'expandMonth', index: 0 });
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
  await h.controller.receive({ type: 'expandMonth', index: 0 });
  await h.controller.receive({ type: 'showPaths' });
  assert.equal(last(h).pathsShown, true);
  h.controller.pageClosed();
  await h.controller.receive({ type: 'ready' });
  assert.equal(h.asked.length, 2, 'the reopened page reads the list again');
  assert.equal(last(h).pathsShown, false, 'the full paths are off on every open');
  assert.ok(last(h).agents.every((a) => a.months.every((m) => !m.expanded)));
});
