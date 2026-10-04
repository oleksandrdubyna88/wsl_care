import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import { test } from 'node:test';
import * as ts from 'typescript';

import { FIELD_MAP, isArriving, SECTIONS } from '../panel/fieldMap';
import { MAX_TEXT } from '../panel/format';
import type { PanelView } from '../panel/view';
import { buildPanelView } from '../panel/viewModel';
import type { Snapshot } from '../state/outcomeStore';
import { Element, runPageScript, type Page } from './support/pageHarness';
import { answered, failed, goldenOutcomes, headBody } from './support/outcomes';
import { goldenSets, PAGE_SCRIPT } from './support/paths';

/**
 * The panel's page script RUN in the strict harness (plan §16 E5.S2 acceptance; `common.generated-code-tests` §1):
 * `media/panel.js` — exactly the file the webview loads — is executed, the host's view message (built by the REAL view
 * model over the goldens) is delivered, and the DOM it built is asserted. The harness throws on every HTML sink and
 * every member it does not model, so "renders as inert text" is a property of the run, not of a regex.
 */

function snapshot(parts: Partial<Snapshot>): Snapshot {
  return { status: undefined, preview: undefined, doctor: undefined, checking: false, ...parts };
}

function load(): { page: Page; root: Element } {
  const root = new Element('MAIN');
  return { page: runPageScript(fs.readFileSync(PAGE_SCRIPT, 'utf8'), { panel: root }), root };
}

function show(view: PanelView): { page: Page; root: Element } {
  const loaded = load();
  loaded.page.message({ type: 'view', view: structuredClone(view) });
  return loaded;
}

function valueCell(root: Element, rowId: string): Element {
  return root.one(`tr[data-row="${rowId}"]`).one('td[data-value]');
}

test('the page announces itself once loaded, and renders nothing before the host speaks', () => {
  const { page, root } = load();
  assert.deepEqual(page.posted, [{ type: 'ready' }]);
  assert.equal(root.children.length, 0);
});

for (const set of goldenSets()) {
  test(`over the ${set} goldens: every section and every row is rendered, label and value as text, and the page says how many`, () => {
    const view = buildPanelView(snapshot(goldenOutcomes(set)));
    const { page, root } = show(view);
    assert.deepEqual(root.all('section[data-section]').map((s) => s.dataset.section), SECTIONS.map((s) => s.id));
    for (const section of view.sections) {
      const shown = root.one(`section[data-section="${section.id}"]`);
      assert.equal(shown.one('h2[data-title]').textContent, section.title);
      for (const row of section.rows) {
        const tr = shown.one(`tr[data-row="${row.id}"]`);
        assert.equal(tr.one('th[data-label]').textContent, row.label, row.id);
        assert.equal(tr.one('td[data-value]').textContent, row.value, row.id);
        assert.equal(tr.dataset.state, row.state, row.id);
      }
    }
    assert.deepEqual(page.posted.at(-1), { type: 'rendered', rows: FIELD_MAP.length });
  });
}

test('the "arrives in E#" rows appear as such, with their epic and reason', () => {
  const { root } = show(buildPanelView(snapshot(goldenOutcomes())));
  for (const row of FIELD_MAP.filter(isArriving)) {
    assert.equal(root.one(`tr[data-row="${row.id}"]`).dataset.state, 'arrives');
    assert.equal(valueCell(root, row.id).textContent, `arrives in ${row.arrives} — ${row.why}`);
  }
});

test('lists under a row render as a sub-table: headers, then one line per item — the "unavailable — reason" cells included', () => {
  const view = buildPanelView(snapshot(goldenOutcomes()));
  const { root } = show(view);
  const folders = view.sections.flatMap((s) => s.rows).find((r) => r.id === 'folders.sizes');
  assert.ok(folders !== undefined);
  const list = root.one('tr[data-items-for="folders.sizes"]');
  assert.deepEqual(list.all('th[data-header]').map((h) => h.textContent), folders.headers);
  const lines = list.all('tr[data-item]').map((tr) => tr.all('td[data-cell]').map((td) => td.textContent));
  assert.deepEqual(lines, folders.items);
  assert.ok(lines.some((cells) => cells.includes('unavailable — /golden-root/home/me/.npm does not exist')));
});

test('a row\'s level reaches the page as data (clock jumps warn in the head golden)', () => {
  const { root } = show(buildPanelView(snapshot(goldenOutcomes())));
  assert.equal(root.one('tr[data-row="health.clockJumps"]').dataset.level, 'warn');
});

test('hostile process names, command lines and container names render as inert TEXT — no element is made from them', () => {
  const body = headBody('status');
  const top = (body.vm as { processes: { top: Record<string, unknown>[] } }).processes.top;
  const hostile = '</script><img src=x onerror=alert(1)>';
  top[0] = { ...top[0], name: hostile, commandLine: `<script>alert(2)</script>${'Y'.repeat(10_000)}` };
  top[1] = { ...top[1], name: 'nul\u0000bell\u0007esc\u001b[2Jrtl\u202Eexe' };
  const stats = (body.slow as { containerStats: { containers: Record<string, unknown>[] } }).containerStats.containers;
  stats[0] = { ...stats[0], name: '"><svg onload=alert(3)>' };
  const view = buildPanelView(snapshot({ ...goldenOutcomes(), status: answered('status', body) }));
  const { root } = show(view);
  const cells = root.one('tr[data-items-for="holders.processes"]').all('td[data-cell]').map((td) => td.textContent);
  assert.ok(cells.includes(hostile), 'the markup arrived as the exact text');
  assert.ok(cells.includes(`<script>alert(2)</script>${'Y'.repeat(MAX_TEXT - 25)}…`), 'the long command line is clipped');
  assert.ok(cells.includes('nul\uFFFDbell\uFFFDesc\uFFFD[2Jrtl\uFFFDexe'), 'control and bidi characters are visible, not obeyed');
  assert.ok(root.one('tr[data-items-for="holders.containers"]').all('td[data-cell]').some((td) => td.textContent === '"><svg onload=alert(3)>'));
  const tags = new Set([root, ...root.descendants()].map((e) => e.tagName));
  for (const tag of tags) {
    assert.ok(['MAIN', 'HEADER', 'H1', 'H2', 'P', 'DIV', 'BUTTON', 'SECTION', 'TABLE', 'THEAD', 'TBODY', 'TR', 'TH', 'TD'].includes(tag), `the page made a ${tag}`);
  }
});

test('the buttons post their own id and nothing else; "Start WSL and check" appears only when the distribution is stopped', () => {
  const { page, root } = show(buildPanelView(snapshot(goldenOutcomes())));
  assert.deepEqual(root.all('button[data-action]').map((b) => b.dataset.action), ['refresh', 'openSettings']);
  page.click(root.one('button[data-action="refresh"]'));
  page.click(root.one('button[data-action="openSettings"]'));
  assert.deepEqual(page.posted.slice(-2), [{ type: 'refresh' }, { type: 'openSettings' }]);
  for (const button of root.all('button[data-action]')) {
    assert.equal(button.attributes.type, 'button');
  }

  const stop = { kind: 'stopped' as const, distro: 'Ubuntu' };
  const stopped = show(buildPanelView(snapshot({ status: failed('status', stop), preview: failed('preview', stop), doctor: failed('doctor', stop) })));
  stopped.page.click(stopped.root.one('button[data-action="startWsl"]'));
  assert.deepEqual(stopped.page.posted.at(-1), { type: 'startWsl' });
  assert.match(stopped.root.one('p[data-notice]').textContent, /not running/);
});

test('a second view replaces the first — nothing is rendered twice', () => {
  const { page, root } = show(buildPanelView(snapshot(goldenOutcomes())));
  page.message({ type: 'view', view: buildPanelView(snapshot(goldenOutcomes())) });
  assert.equal(root.all('section[data-section="memory"]').length, 1);
});

test('a message that is not a view is ignored — the page renders only what the host built', () => {
  const { page, root } = load();
  for (const data of [{ type: 'other' }, { type: 'view' }, { type: 'view', view: { sections: 'x' } }, null, 'view']) {
    page.message(data);
  }
  assert.equal(root.children.length, 0);
  assert.deepEqual(page.posted, [{ type: 'ready' }]);
});

const FORBIDDEN_NAMES = ['innerHTML', 'outerHTML', 'insertAdjacentHTML', 'write', 'writeln', 'eval', 'Function', 'srcdoc', 'setTimeout', 'setInterval', 'fetch'];

function namesIn(text: string): string[] {
  const found: string[] = [];
  const visit = (node: ts.Node): void => {
    if (ts.isIdentifier(node) || ts.isPrivateIdentifier(node)) {
      found.push(node.text);
    }
    if (ts.isStringLiteralLike(node)) {
      found.push(node.text);
    }
    ts.forEachChild(node, visit);
  };
  visit(ts.createSourceFile('panel.js', text, ts.ScriptTarget.ES2022, true, ts.ScriptKind.JS));

  return found;
}

test('the page source names no HTML sink, no eval, no timer and no network — read by the parser, not as text', () => {
  const names = namesIn(fs.readFileSync(PAGE_SCRIPT, 'utf8'));
  assert.ok(names.includes('textContent') && names.includes('createElement'), 'the scan reads the real page (its known instances)');
  assert.deepEqual(FORBIDDEN_NAMES.filter((n) => names.includes(n)), []);
});

test('the page-source scan finds a planted sink in every spelling', () => {
  for (const planted of ['el.innerHTML = x;', "el['outerHTML'] = x;", 'el.insertAdjacentHTML("a", x);', 'new Function("x");', 'eval(x);']) {
    assert.ok(FORBIDDEN_NAMES.some((n) => namesIn(planted).includes(n)), planted);
  }
});
