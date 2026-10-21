import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildSync } from 'esbuild';

import { esbuildOptionsOf, webviewEntries } from './support/bundleEntries';
import { Element, runPageScript, type Page } from './support/pageHarness';
import { EXTENSION_ROOT } from './support/paths';

/**
 * W0 of `todo/PLAN_disk_health_E18.md`, RED item 8 (goal 5): the kit's three components — `Tabs`, `DataTable` (search, sort,
 * the reveal, `stack`) and `RecommendationCard` — RUN in the strict page harness, with wsl_care-shaped data, and are driven
 * like a person drives them. The page is `src/test/webview/kitPageEntry.ts`, bundled here by esbuild for the browser with the
 * options of the real full-view bundle (the webview entry of `scripts/bundle-entries.json`, read here, not copied) and imports the kit exactly as W1b will:
 * `…/components` and `import '…/components.css'`.
 *
 * It proves two things at once: the kit does what its contract says in a DOM that models ONLY the kit's port
 * (`dew_flow_vscode_kit`'s `research/PLAN_kit_components_storybook.md` §4.3), and that harness accepts the port and refuses a
 * step beyond it. It does NOT prove a layout, a keyboard or the stylesheet's honouring of the CSP nonce — a real browser's
 * (`research/module_tests.md`, W0's "what it does not prove").
 */

const SECRET = 'ollama serve --keep-alive 5m';

interface Bundled {
  readonly script: string;
  readonly styles: string;
}

let bundled: Bundled | undefined;

/** The test page, bundled once: an IIFE for the browser, CSS emitted beside it as the real bundle's is. */
function bundle(): Bundled {
  if (bundled === undefined) {
    // The options that SHIP: the webview entry that writes dist/webview/full.js (scripts/bundle-entries.json), with this test's entry and no write.
    const full = webviewEntries().find((entry) => entry.outfile === 'dist/webview/full.js');
    assert.ok(full !== undefined, 'scripts/bundle-entries.json has the webview entry for dist/webview/full.js');
    const built = buildSync({ ...esbuildOptionsOf(full), absWorkingDir: EXTENSION_ROOT, entryPoints: ['src/test/webview/kitPageEntry.ts'], outfile: 'kitPage.js', write: false, logLevel: 'silent' });
    const text = (extension: string): string => built.outputFiles.find((file) => file.path.endsWith(extension))?.text ?? '';
    bundled = { script: text('.js'), styles: text('.css') };
  }

  return bundled;
}

function open(): { page: Page; root: Element } {
  const root = new Element('MAIN');
  return { page: runPageScript(bundle().script, { root }), root };
}

const tab = (root: Element, id: string): Element => root.one(`button[data-kit-tab="${id}"]`);
const panel = (root: Element, id: string): Element => root.one(`div[data-kit-panel="${id}"]`);
const rowOrder = (root: Element): string[] => root.all('[data-kit-row]').map((row) => row.dataset.kitRow ?? '');
const shownRows = (root: Element): string[] => root.all('[data-kit-row]').filter((row) => !row.hidden).map((row) => row.dataset.kitRow ?? '');
const revealButton = (root: Element, index: number): Element => root.one(`button[data-kit-reveal="${index}"]`);
const countLine = (root: Element): string => root.descendants().find((e) => e.className === 'kit-table__count')?.textContent ?? '';
const requestOf = (page: Page): string => String((page.posted.at(-1) as { request?: unknown }).request);

test('the page imports the stylesheet the kit ships: the bundle emits it, with the token block', () => {
  assert.ok(bundle().styles.includes('--kit-fg'), 'components.css reached the bundle as a stylesheet');
  assert.ok(bundle().script.length > 0);
});

test('the kit builds in the strict harness: the tablist\'s tabs, their roles, the roving tabindex, the panels\' hidden state', () => {
  const { root } = open();
  assert.equal(root.all('[data-kit-tab]').length, 2);
  assert.deepEqual([tab(root, 'wsl').attributes.role, tab(root, 'wsl').attributes['aria-selected'], tab(root, 'wsl').attributes.tabindex, tab(root, 'wsl').attributes.id], ['tab', 'true', '0', 'full-tab-wsl']);
  assert.deepEqual([tab(root, 'windows').attributes['aria-selected'], tab(root, 'windows').attributes.tabindex], ['false', '-1']);
  assert.deepEqual([panel(root, 'wsl').hidden, panel(root, 'windows').hidden], [false, true]);
  assert.equal(panel(root, 'windows').attributes['aria-labelledby'], 'full-tab-windows');
});

test('arrow keys move the tab and focus it (wrapping), Home and End go to the ends, any other key does nothing — each key goes to the FOCUSED tab', () => {
  const { page, root } = open();
  const selected = (): string[] => ['wsl', 'windows'].filter((id) => tab(root, id).attributes['aria-selected'] === 'true');
  const focused = (): Element => page.activeElement ?? tab(root, 'wsl');

  const right = page.key(focused(), 'ArrowRight');
  assert.deepEqual([selected(), right.defaultPrevented, page.activeElement === tab(root, 'windows')], [['windows'], true, true]);
  assert.deepEqual([panel(root, 'windows').hidden, panel(root, 'wsl').hidden, tab(root, 'windows').attributes.tabindex, tab(root, 'wsl').attributes.tabindex], [false, true, '0', '-1']);
  assert.deepEqual(page.posted, [{ type: 'selected', id: 'windows' }], 'onSelect heard the person\'s selection');

  page.key(focused(), 'ArrowRight');
  assert.deepEqual([selected(), page.activeElement === tab(root, 'wsl')], [['wsl'], true], 'wrapped to the first tab, which now holds focus');
  page.key(focused(), 'End');
  assert.deepEqual([selected(), page.activeElement === tab(root, 'windows')], [['windows'], true]);
  page.key(focused(), 'Home');
  assert.deepEqual([selected(), page.activeElement === tab(root, 'wsl')], [['wsl'], true]);
  page.key(focused(), 'ArrowLeft');
  assert.deepEqual([selected(), page.activeElement === tab(root, 'windows')], [['windows'], true], 'ArrowLeft from the first tab wraps to the last');

  const before = page.posted.length;
  const other = page.key(focused(), 'a');
  assert.deepEqual([other.defaultPrevented, selected(), page.posted.length], [false, ['windows'], before]);
});

test('a click on a tab selects it, and the status the tab was given is drawn with its word', () => {
  const { page, root } = open();
  page.click(tab(root, 'windows'));
  assert.deepEqual([tab(root, 'windows').attributes['aria-selected'], panel(root, 'wsl').hidden], ['true', true]);
  assert.match(tab(root, 'wsl').textContent, /2 warnings/, 'the word, never the colour alone');
});

test('a search hides the rows whose shown text lacks the query, says how many remain, and a query with no match shows the empty row', () => {
  const { page, root } = open();
  const box = root.one('input[data-kit-search]');
  assert.equal(countLine(root), 'Showing 4 of 4 rows.');

  page.type(box, 'NODE');
  assert.deepEqual(shownRows(root), ['1']);
  assert.equal(countLine(root), 'Showing 1 of 4 rows.');
  page.type(box, '6.0 GiB');
  assert.deepEqual(shownRows(root), ['0'], 'the SHOWN text is searched: the formatted size');
  page.type(box, 'zzz');
  assert.deepEqual(shownRows(root), []);
  assert.equal(root.one('[data-kit-empty]').hidden, false);
  assert.match(root.one('[data-kit-empty]').textContent, /No process matches\./);
  page.type(box, '');
  assert.deepEqual(shownRows(root), ['0', '1', '2', '3']);
  assert.equal(root.one('[data-kit-empty]').hidden, true);
});

test('a header click sorts ascending, a second click descending; absent sorts last in both and shows a dash; aria-sort follows', () => {
  const { page, root } = open();
  const held = root.one('button[data-kit-sort="held"]');
  const head = held.parent as Element;
  assert.equal(head.attributes['aria-sort'], 'none');

  page.click(held);
  assert.deepEqual([rowOrder(root), head.attributes['aria-sort']], [['3', '1', '0', '2'], 'ascending']);
  page.click(held);
  assert.deepEqual([rowOrder(root), head.attributes['aria-sort']], [['0', '1', '3', '2'], 'descending']);
  page.click(root.one('button[data-kit-sort="program"]'));
  assert.deepEqual([rowOrder(root), head.attributes['aria-sort']], [['3', '1', '0', '2'], 'none'], 'by program, a to z: dotnet, node, ollama, vmmem');
  assert.match(root.one('tr[data-kit-row="2"]').textContent, /—/, 'the absent value is a dash, not a zero');
});

test('stack: every body cell carries its column\'s header as data-kit-label, and the table carries its stack class', () => {
  const { root } = open();
  const grid = root.descendants().find((e) => e.tagName === 'TABLE');
  assert.ok(grid !== undefined);
  assert.match(grid.className, /kit-table--stack-narrow/);
  assert.equal(grid.attributes['aria-label'], 'Processes');
  const labels = root.one('tr[data-kit-row="0"]').children.map((cell) => cell.dataset.kitLabel);
  assert.deepEqual(labels, ['Program', 'PID', 'Held', 'Command line']);
});

const revealCell = (root: Element, index: number): Element => root.one(`tr[data-kit-row="${index}"]`).children.at(-1) as Element;

test('the reveal: a click hands the host an index and an opaque request — and until the host replies the reveal cell holds only its button and the row only its shown text', () => {
  const { page, root } = open();
  const button = revealButton(root, 0);
  assert.equal(button.attributes['aria-label'], 'Show the command line of ollama', 'the label names WHAT, never the value');
  const row = root.one('tr[data-kit-row="0"]');
  assert.deepEqual(revealCell(root, 0).children, [button], 'the cell holds nothing but the button');
  assert.deepEqual([button.textContent, row.textContent], ['Show', 'ollama41206.0 GiBShow']);

  page.click(button);
  const asked = page.posted.at(-1) as { type: string; index: number; request: string };
  assert.deepEqual([asked.type, asked.index], ['reveal', 0]);
  assert.match(asked.request, /^\d+:\d+$/, 'an opaque id, carrying no row data');
  assert.deepEqual(Object.keys(asked).sort(), ['index', 'request', 'type'], 'the post holds an index and a request, no program, no pid');
  assert.deepEqual(revealCell(root, 0).children, [button], 'still only the button while it waits');
  assert.deepEqual([button.textContent, row.textContent], ['Waiting…', 'ollama41206.0 GiBWaiting…'], 'the label says it waits; nothing hidden is in the row');

  page.click(button);
  assert.equal(page.posted.length, 1, 'a click on a waiting button tells nobody again');
});

test('the host\'s answer lands once: reveal(index, text, request) is true for the current request and false when repeated', () => {
  const { page, root } = open();
  page.click(revealButton(root, 0));
  const request = requestOf(page);

  page.message({ type: 'answer', index: 0, text: SECRET, request });
  assert.deepEqual(page.posted.at(-1), { type: 'answered', index: 0, ok: true });
  assert.equal(root.one('tr[data-kit-row="0"]').textContent.includes(SECRET), true);
  page.message({ type: 'answer', index: 0, text: 'again', request });
  assert.deepEqual(page.posted.at(-1), { type: 'answered', index: 0, ok: false }, 'a spent request answers false');
  assert.equal(root.textContent.includes('again'), false);
});

test('a stale or unknown request answers false and changes nothing, never throws — a late reply cannot land on a new row', () => {
  const { page, root } = open();
  page.message({ type: 'answer', index: 1, text: SECRET, request: '1:99' });
  assert.deepEqual(page.posted.at(-1), { type: 'answered', index: 1, ok: false }, 'a request nobody made');

  page.click(revealButton(root, 1));
  const old = requestOf(page);
  page.message({ type: 'rows', rows: [{ program: 'node', pid: 1, heldBytes: 1 }, { program: 'bash', pid: 2, heldBytes: 2 }] });
  page.message({ type: 'answer', index: 1, text: SECRET, request: old });
  assert.deepEqual(page.posted.at(-1), { type: 'answered', index: 1, ok: false }, 'the old request died with setRows');
  assert.equal(root.textContent.includes(SECRET), false);

  page.click(revealButton(root, 1));
  const fresh = requestOf(page);
  assert.notEqual(fresh, old);
  page.message({ type: 'answer', index: 1, text: SECRET, request: old });
  assert.deepEqual(page.posted.at(-1), { type: 'answered', index: 1, ok: false }, 'even with the same row waiting again, the OLD request is refused');
  page.message({ type: 'answer', index: 1, text: 'bash --login', request: fresh });
  assert.deepEqual(page.posted.at(-1), { type: 'answered', index: 1, ok: true });
});

test('setRows([]) then dispose() leave no revealed text, and a late reply after dispose is false', () => {
  const { page, root } = open();
  page.click(revealButton(root, 0));
  const request = requestOf(page);
  page.message({ type: 'answer', index: 0, text: SECRET, request });
  assert.equal(root.textContent.includes(SECRET), true);
  page.click(revealButton(root, 2));
  const waiting = requestOf(page);

  page.message({ type: 'dismantle' });
  assert.deepEqual(page.posted.at(-1), { type: 'dismantled' });
  assert.equal(root.textContent.includes(SECRET), false, 'the revealed text went with the rows');
  assert.equal(root.all('[data-kit-reveal]').length, 0);
  page.message({ type: 'answer', index: 2, text: SECRET, request: waiting });
  assert.deepEqual(page.posted.at(-1), { type: 'answered', index: 2, ok: false });
  assert.equal(root.textContent.includes(SECRET), false);
});

test('dispose() ALONE: the waiting request is dropped (a late answer is false), the table stops reacting, and what was already revealed stays - the kit\'s contract; setRows([]) first is how a consumer clears it', () => {
  const { page, root } = open();
  page.click(revealButton(root, 0));
  page.message({ type: 'answer', index: 0, text: SECRET, request: requestOf(page) });
  page.click(revealButton(root, 2));
  const waiting = requestOf(page);
  const orderBefore = rowOrder(root);
  const sortHead = root.one('button[data-kit-sort="held"]').parent as Element;
  assert.equal(root.one('tr[data-kit-row="0"]').textContent.includes(SECRET), true, 'revealed before the dispose');

  page.message({ type: 'dispose' });
  assert.deepEqual(page.posted.at(-1), { type: 'disposed' });
  page.message({ type: 'answer', index: 2, text: 'late', request: waiting });
  assert.deepEqual(page.posted.at(-1), { type: 'answered', index: 2, ok: false }, 'a pending request does not survive dispose()');
  assert.equal(root.textContent.includes('late'), false);
  page.click(root.one('button[data-kit-sort="held"]'));
  assert.deepEqual([rowOrder(root), sortHead.attributes['aria-sort']], [orderBefore, 'none'], 'a header click after dispose() sorts nothing');
  const posts = page.posted.length;
  page.click(revealButton(root, 3));
  assert.equal(page.posted.length, posts, 'a reveal click after dispose() tells nobody');
  assert.equal(root.one('tr[data-kit-row="0"]').textContent.includes(SECRET), true, 'dispose() alone does not clear what is revealed');
});

test('an onReveal that throws leaves the row clickable again: the kit clears the request and rethrows to the click', () => {
  const { page, root } = open();
  const button = revealButton(root, 3);
  page.message({ type: 'failReveals', on: true });

  assert.throws(() => page.click(button), /post failed/);
  assert.equal(button.textContent, 'Show', 'back to its normal state, not stuck on Waiting…');
  assert.equal(button.className, 'kit-table__reveal-button');
  assert.equal(page.posted.length, 0, 'nothing was posted');

  page.message({ type: 'failReveals', on: false });
  page.click(button);
  assert.deepEqual(page.posted.map((m) => (m as { type: string }).type), ['reveal'], 'the same row asks again');
});

test('the recommendation card: the tone and its word, the sentence, the consumer\'s own button in the action slot, the evidence — and the button is the consumer\'s', () => {
  const { page, root } = open();
  const card = panel(root, 'windows');
  const text = card.textContent;
  for (const part of ['Warning', 'Memory is low: 6 % available. Drop caches to free about 2.1 GiB.', 'Evidence', 'WSL RAM available 6 %', 'Swap 92 % used', 'Drop caches']) {
    assert.ok(text.includes(part), part);
  }
  const action = card.descendants().find((e) => e.tagName === 'BUTTON' && e.textContent === 'Drop caches');
  assert.ok(action !== undefined);
  page.click(tab(root, 'windows'));
  page.click(action);
  assert.deepEqual(page.posted.at(-1), { type: 'dropCaches' });
});

test('the harness still refuses one attribute beyond the kit\'s port, and one event', () => {
  const root = new Element('MAIN');
  assert.throws(() => runPageScript(`${bundle().script}\ndocument.getElementById('root').setAttribute('draggable', 'true');`, { root }), /setAttribute\("draggable"\) is not modelled/);
  assert.throws(() => runPageScript(`${bundle().script}\ndocument.getElementById('root').addEventListener('mouseover', () => 1);`, { root: new Element('MAIN') }), /listening for mouseover on an element is not modelled/);
});
