import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

import { ARCHIVE_SCOPE, buildArchiveView, monthKey, type ArchiveHostState } from '../../archivePage/archiveViewModel';
import type { ArchivePageView } from '../../archivePage/archiveView';
import { localMinuteOf, sizeText } from '../../text/format';
import { bodyOf, type Body } from '../support/body';
import { Element, runPageScript, type Page } from '../support/pageHarness';
import { EXTENSION_ROOT, goldenFile } from '../support/paths';
import { withZone } from '../support/zone';

/**
 * Plan §15s, E10.S2a: the Archive page's script RUN in the strict harness (`common.generated-code-tests` §1) — `media/archive.js`,
 * the file the webview loads, is executed; the view built by the REAL view model over the daemon's `archive-list.json` golden is
 * delivered; and what the page shows is compared with the golden's fields, read HERE. The harness throws on every HTML sink, so a
 * page that reached for `innerHTML` fails by name. A session path is shown short until the person asks for it whole.
 */

const SCRIPT = path.join(EXTENSION_ROOT, 'media', 'archive.js');
const ZONE = 'Europe/Kyiv';

function goldenEntry(): Body {
  const entries = goldenFile('archive-list.json').entries;
  assert.ok(Array.isArray(entries));
  return bodyOf(entries[0], 'archive-list.json entries[0]');
}

function listWith(edits: readonly Body[]): Body {
  return { ...goldenFile('archive-list.json'), entries: edits.map((e) => ({ ...goldenEntry(), ...e })) };
}

function state(body: Body, parts: Partial<ArchiveHostState> = {}): ArchiveHostState {
  return { list: { kind: 'read', distro: 'Ubuntu', body }, expanded: new Set(), pathsShown: false, notice: '', noticeLevel: 'none', ...parts };
}

function viewOf(s: ArchiveHostState): ArchivePageView {
  return withZone(ZONE, () => buildArchiveView(s));
}

function show(view: ArchivePageView): { page: Page; root: Element } {
  const root = new Element('MAIN');
  const page = runPageScript(fs.readFileSync(SCRIPT, 'utf8'), { archive: root });
  page.message({ type: 'view', view: structuredClone(view) });
  return { page, root };
}

function entryCells(root: Element): string[][] {
  return root.all('tr[data-entry]').map((row) => row.all('[data-cell]').map((cell) => cell.textContent));
}

test('the page shows the golden entry as the daemon listed it — path SHORT by default, files, size, status, verified, the local time', () => {
  const entry = goldenEntry();
  const open = new Set([monthKey(String(entry.agent), String(entry.month))]);
  const { root, page } = show(viewOf(state(goldenFile('archive-list.json'), { expanded: open })));
  assert.deepEqual(entryCells(root), [[
    '…/s1.jsonl',
    String(entry.files),
    sizeText(Number(entry.bytes)),
    'archived — still at its source',
    'yes',
    withZone(ZONE, () => localMinuteOf(String(entry.archivedAtUtc))),
  ]]);
  assert.ok(!root.textContent.includes(String(entry.key)), 'the whole session path is not on the page');
  assert.equal(root.one('[data-scope]').textContent, ARCHIVE_SCOPE, 'D9: this distribution only');
  assert.match(root.textContent, /\/golden-root\/mnt\/v\/ai-archive/, 'the base folder');
  assert.deepEqual(page.posted.at(-1), { type: 'rendered', blocks: 1 }, 'one agent block drawn');
});

test('Show full session paths: the toggle posts its bare message, and a view with paths shown shows the path whole', () => {
  const entry = goldenEntry();
  const open = new Set([monthKey(String(entry.agent), String(entry.month))]);
  const { root, page } = show(viewOf(state(goldenFile('archive-list.json'), { expanded: open })));
  const toggle = root.one('button[data-action="paths"]');
  assert.equal(toggle.attributes['aria-pressed'], 'false');
  page.click(toggle);
  assert.deepEqual(page.posted.at(-1), { type: 'showPaths' });
  const shown = show(viewOf(state(goldenFile('archive-list.json'), { expanded: open, pathsShown: true })));
  assert.equal(entryCells(shown.root)[0]?.[0], String(entry.key));
  const pressed = shown.root.one('button[data-action="paths"]');
  assert.equal(pressed.attributes['aria-pressed'], 'true');
  shown.page.click(pressed);
  assert.deepEqual(shown.page.posted.at(-1), { type: 'hidePaths' });
});

test('a month is expanded by its INDEX — the page posts the index the view gave it, never a month name', () => {
  const body = listWith([{ month: '2000/01' }, { month: '2000/02', entryId: '00000000000000a2', key: 'projects/p/s2.jsonl' }]);
  const { root, page } = show(viewOf(state(body)));
  const buttons = root.all('button[data-month]');
  assert.deepEqual(buttons.map((b) => b.dataset.month), ['0', '1']);
  const second = buttons[1];
  assert.ok(second !== undefined);
  page.click(second);
  assert.deepEqual(page.posted.at(-1), { type: 'expandMonth', index: 1 });
  assert.deepEqual(entryCells(root), [], 'a closed month shows no entries');
  const open = show(viewOf(state(body, { expanded: new Set([monthKey('claude-code', '2000/02')]) })));
  assert.equal(open.root.all('tr[data-entry]').length, 1);
  const close = open.root.all('button[data-month]')[1];
  assert.ok(close !== undefined);
  assert.equal(close.attributes['aria-expanded'], 'true');
  open.page.click(close);
  assert.deepEqual(open.page.posted.at(-1), { type: 'collapseMonth', index: 1 });
});

test('an UNVERIFIED entry removed at its source says how to restore it in a terminal; an unknown status reads "unknown (<value>)"', () => {
  const body = listWith([
    { status: 'sourceRemoved', verified: false },
    { status: 'sourceRemoved', verified: true, entryId: '00000000000000a2', key: 'projects/p/s2.jsonl' },
    { status: 'teleported', verified: true, entryId: '00000000000000a3', key: 'projects/p/s3.jsonl' },
  ]);
  const { root } = show(viewOf(state(body, { expanded: new Set([monthKey('claude-code', '2000/01')]) })));
  assert.deepEqual(entryCells(root).map((cells) => [cells[3], cells[4]]), [
    ['archived — removed at its source', 'no — restore it in a terminal with --accept-unverified'],
    ['archived — removed at its source', 'yes'],
    ['unknown (teleported)', 'yes'],
  ]);
});

test('own review #4: an entry whose verified flag is missing reads "unknown" — never "no", never the terminal instruction', () => {
  const body = listWith([{ status: 'sourceRemoved', verified: undefined }]);
  const { root } = show(viewOf(state(body, { expanded: new Set([monthKey('claude-code', '2000/01')]) })));
  assert.equal(entryCells(root)[0]?.[4], 'unknown');
});

test('own review #5: while the list is being read, Refresh is greyed (the host decides; the page only lays it out)', () => {
  const reading = show(viewOf({ ...state(goldenFile('archive-list.json')), list: { kind: 'reading' } }));
  assert.equal(reading.root.one('button[data-action="refresh"]').disabled, true);
  const answered = show(viewOf(state(goldenFile('archive-list.json'))));
  assert.equal(answered.root.one('button[data-action="refresh"]').disabled, false);
});

test('hostile text from the index is inert: it reaches the page as text, cut, with control characters shown as U+FFFD', () => {
  const body = listWith([{ key: 'projects/<img src=x onerror=alert(1)>\u0007.jsonl', agent: 'claude-code' }]);
  const { root } = show(viewOf(state(body, { expanded: new Set([monthKey('claude-code', '2000/01')]), pathsShown: true })));
  const cell = entryCells(root)[0]?.[0] ?? '';
  assert.ok(cell.includes('<img src=x onerror=alert(1)>'), 'shown as text');
  assert.ok(!cell.includes('\u0007'), 'the control character is replaced');
});

test('code round #3: a month or agent with an entry whose size is missing reads "size unknown", never a partial sum', () => {
  const body = listWith([{ bytes: 200 }, { entryId: '00000000000000a2', key: 'projects/p/s2.jsonl', bytes: undefined }]);
  const view = viewOf(state(body));
  assert.equal(view.agents[0]?.months[0]?.summary, '2 listed · 2 verified · size unknown');
  assert.match(view.agents[0]?.summary ?? '', / · size unknown$/);
  const known = viewOf(state(listWith([{ bytes: 200 }, { entryId: '00000000000000a2', key: 'projects/p/s2.jsonl', bytes: 300 }])));
  assert.equal(known.agents[0]?.months[0]?.summary, `2 listed · 2 verified · ${sizeText(500)}`);
});

test('code round #4: grouping is linear — 60 000 entries of ONE month are grouped and summarised well inside a second', () => {
  const entries = Array.from({ length: 60_000 }, (_, i) => ({ entryId: i.toString(16).padStart(16, '0'), key: `projects/p/${i}.jsonl` }));
  const body = listWith(entries);
  const started = performance.now();
  const view = viewOf(state(body));
  const took = performance.now() - started;
  assert.equal(view.agents[0]?.months[0]?.summary.startsWith('60000 listed'), true);
  assert.ok(took < 1000, `grouping 60 000 entries took ${Math.round(took)} ms`);
});

test('a failed read is said, with no agents; a daemon without archive.list says to update it; and nothing is drawn as an empty archive', () => {
  const failed = show(viewOf({ ...state(goldenFile('archive-list.json')), list: { kind: 'failed', sentence: 'The call timed out after 630 s.' } }));
  assert.equal(failed.root.all('section[data-agent]').length, 0);
  assert.match(failed.root.textContent, /timed out after 630 s/);
  assert.ok(!/Nothing is archived/.test(failed.root.textContent));
  const empty = show(viewOf(state(listWith([]))));
  assert.match(empty.root.textContent, /Nothing is archived here yet/);
});
