import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import { test } from 'node:test';

import * as path from 'node:path';

import { deriveCleanup, type CleanupState } from '../cleanup/cleanupView';
import { FIELD_MAP, isArriving, SECTIONS } from '../panel/fieldMap';
import { MAX_TEXT } from '../panel/format';
import type { PanelView } from '../panel/view';
import { buildPanelView } from '../panel/viewModel';
import type { Snapshot } from '../state/outcomeStore';
import { Element, runPageScript, type Page } from './support/pageHarness';
import { FORBIDDEN_NAMES, namesIn } from './support/pageSource';
import { answered, failed, goldenOutcomes, headBody } from './support/outcomes';
import { GOLDEN_ROOT, goldenSets, PAGE_SCRIPT } from './support/paths';

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
  assert.ok(lines.some((cells) => cells.includes('unavailable — /golden-root/home/user/.npm does not exist')));
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
    assert.ok(['MAIN', 'HEADER', 'H1', 'H2', 'P', 'DIV', 'BUTTON', 'SECTION', 'TABLE', 'THEAD', 'TBODY', 'TR', 'TH', 'TD', 'UL', 'LI', 'SPAN'].includes(tag), `the page made a ${tag}`);
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

test('the notice is the ONE live region, and the same element across renders — only its text changes, so a screen reader announces the change and not the whole panel', () => {
  const stop = { kind: 'stopped' as const, distro: 'Ubuntu' };
  const { page, root } = show(buildPanelView(snapshot({ status: failed('status', stop), preview: failed('preview', stop), doctor: failed('doctor', stop) })));
  const notice = root.one('p[data-notice]');
  assert.equal(notice.attributes['aria-live'], 'polite');
  assert.match(notice.textContent, /not running/);
  assert.deepEqual([root, ...root.descendants()].filter((e) => e.attributes['aria-live'] !== undefined), [notice], 'nothing else is live');

  const missing = { kind: 'notInstalled' as const, distro: 'Ubuntu' };
  page.message({ type: 'view', view: buildPanelView(snapshot({ status: failed('status', missing) })) });
  assert.equal(root.one('p[data-notice]'), notice, 'the same element, re-placed — not a new live region each render');
  assert.match(notice.textContent, /not installed/);

  page.message({ type: 'view', view: buildPanelView(snapshot(goldenOutcomes())) });
  assert.equal(root.one('p[data-notice]'), notice);
  assert.equal(notice.textContent, '', 'no notice: the live region stays, empty');
});

// ---- E6.S3: the cleanup controls, RUN in the strict harness over the goldens (plan §16 E6.S3 acceptance) ----

const IDLE_CLEANUP: CleanupState = { entries: [], results: [], flowBusy: false };

function cleanupView(status: Record<string, unknown> = headBody('status'), state: CleanupState = IDLE_CLEANUP): PanelView {
  const snap = snapshot({ ...goldenOutcomes(), status: answered('status', status) });
  return buildPanelView(snap, deriveCleanup(snap, state).controls);
}

function goldenBody(name: string): Record<string, unknown> {
  return JSON.parse(fs.readFileSync(path.join(GOLDEN_ROOT, 'head', name), 'utf8')) as Record<string, unknown>;
}

for (const set of goldenSets()) {
  test(`E6.S3 over the ${set} goldens: a Clean and a Select per cleanup row, inside the Cleanup section; a row's Clean posts ONLY its row id`, () => {
    const outcomes = goldenOutcomes(set);
    const snap = snapshot(outcomes);
    const view = buildPanelView(snap, deriveCleanup(snap, IDLE_CLEANUP).controls);
    const { page, root } = show(view);
    const box = root.one('section[data-section="cleanup"]').one('div[data-cleanup]');
    assert.deepEqual(box.all('div[data-clean-row]').map((d) => d.dataset.cleanRow), view.cleanup.rows.map((r) => r.rowId));
    for (const row of view.cleanup.rows) {
      const button = box.one(`button[data-clean="${row.rowId}"]`);
      assert.equal(button.textContent, row.label);
      assert.equal(button.disabled, !row.enabled, row.rowId);
      assert.equal(button.attributes.type, 'button');
    }
    const enabled = view.cleanup.rows.find((r) => r.enabled);
    assert.ok(enabled !== undefined, 'the golden has a row to clean');
    page.click(box.one(`button[data-clean="${enabled.rowId}"]`));
    assert.deepEqual(page.posted.at(-1), { type: 'clean', rowIds: [enabled.rowId] });
  });
}

test('E6.S3: ticking rows enables "Clean selected (n)", which posts the ticked row ids in row order as ONE message; none ticked, it is disabled', () => {
  const { page, root } = show(cleanupView());
  const selectedButton = root.one('button[data-clean-selected]');
  assert.equal(selectedButton.disabled, true);
  assert.equal(selectedButton.textContent, 'Clean selected (0)');
  page.click(root.one('button[data-select="A5"]'));
  page.click(root.one('button[data-select="A4"]'));
  assert.equal(root.one('button[data-select="A4"]').attributes['aria-pressed'], 'true');
  assert.equal(selectedButton.textContent, 'Clean selected (2)');
  assert.equal(selectedButton.disabled, false);
  page.click(selectedButton);
  assert.deepEqual(page.posted.at(-1), { type: 'cleanSelected', rowIds: ['A4', 'A5'] });
  page.click(root.one('button[data-select="A5"]'));
  assert.equal(root.one('button[data-select="A5"]').attributes['aria-pressed'], 'false');
  assert.equal(selectedButton.textContent, 'Clean selected (1)');
});

test('E6.S3: a ticked row stays ticked across a re-render — and is dropped once the host says the row cannot be cleaned', () => {
  const { page, root } = show(cleanupView());
  page.click(root.one('button[data-select="A4"]'));
  page.message({ type: 'view', view: structuredClone(cleanupView()) });
  assert.equal(root.one('button[data-select="A4"]').attributes['aria-pressed'], 'true');
  page.message({ type: 'view', view: structuredClone(cleanupView(goldenBody('status-running-live.json'))) });
  assert.equal(root.one('button[data-select="A4"]').attributes['aria-pressed'], 'false');
});

test('E6.S3 the reload case on the page: status.running live reads "Cleaning… A4"; every Clean, Select and the full check disabled — and a press posts nothing', () => {
  const { page, root } = show(cleanupView(goldenBody('status-running-live.json')));
  assert.equal(root.one('p[data-cleanup-state]').textContent, 'Cleaning… A4');
  const buttons = root.one('div[data-cleanup]').all('button[data-clean]');
  assert.ok(buttons.length > 0 && buttons.every((b) => b.disabled));
  assert.equal(root.one('button[data-full-check]').disabled, true);
  const before = page.posted.length;
  page.click(buttons[0] as Element);
  page.click(root.one('button[data-full-check]'));
  assert.equal(page.posted.length, before, 'a disabled button posts nothing');
});

test('E6.S3: "Run full check now" posts the bare message when enabled', () => {
  const { page, root } = show(cleanupView());
  page.click(root.one('button[data-full-check]'));
  assert.deepEqual(page.posted.at(-1), { type: 'runFullCheck' });
});

test('E6.S3 §15j M4: a wedged run of the daemon\'s units shows Stop, which posts the INDEX the host gave; one outside them, the text with its pid', () => {
  const wedged = goldenBody('status-running-wedged.json');
  const { page, root } = show(cleanupView(wedged));
  page.click(root.one('button[data-stop]'));
  assert.deepEqual(page.posted.at(-1), { type: 'stop', index: 0 });
  const outside = show(cleanupView({ ...wedged, running: { ...(wedged.running as Record<string, unknown>), trigger: 'cli' } })).root;
  assert.deepEqual(outside.all('button[data-stop]'), []);
  assert.match(outside.one('p[data-stop-text]').textContent, /pid 4242/);
});

test('E6.S3: Last cleanup shows the results this window showed and "Docker after" with its time — hostile text as inert TEXT', () => {
  const hostile = '</script><img src=x onerror=alert(1)>';
  const { root } = show(cleanupView(headBody('status'), { ...IDLE_CLEANUP, results: [{ level: 'warn', sentence: hostile }, { level: 'info', sentence: 'Run Y is done' }] }));
  const box = root.one('section[data-section="lastCleanup"]').one('div[data-last-cleanup]');
  assert.deepEqual(box.all('li[data-result]').map((li) => [li.textContent, li.dataset.level]), [[hostile, 'warn'], ['Run Y is done', 'ok']]);
  assert.match(box.one('p[data-docker-after]').textContent, /reclaimable \(docker system df, read at /);
  assert.ok(![root, ...root.descendants()].some((e) => e.tagName === 'IMG' || e.tagName === 'SCRIPT'));
});

test('E6.S4: Last cleanup carries a Logs button that posts the bare openRunLogs — the host opens THAT run (its id is the host\'s)', () => {
  const { page, root } = show(cleanupView());
  const box = root.one('section[data-section="lastCleanup"]').one('div[data-last-cleanup]');
  page.click(box.one('button[data-run-logs]'));
  assert.equal(box.one('button[data-run-logs]').textContent, 'Logs');
  assert.deepEqual(page.posted.at(-1), { type: 'openRunLogs' });
});

test('E6.S3 review C15: a Select press toggles the tick and posts NOTHING', () => {
  const { page, root } = show(cleanupView());
  const before = page.posted.length;
  page.click(root.one('button[data-select="A4"]'));
  assert.equal(page.posted.length, before);
  assert.equal(root.one('button[data-select="A4"]').attributes['aria-pressed'], 'true');
});

// ---- PLAN_windows_time_task.md D5, D8 o5: the Windows Time guard's line, RUN in the strict harness ----

test('the Windows Time guard line sits in the Health section as TEXT, and its buttons post only their bare ids', () => {
  const hostile = 'Windows Time guard: </script><img src=x onerror=alert(1)>';
  const view = buildPanelView(snapshot(goldenOutcomes()), undefined, { line: hostile, level: 'warn', buttons: [{ id: 'installWindowsTimeGuard', label: 'Install the Windows Time guard', enabled: true }, { id: 'removeWindowsTimeGuard', label: 'Remove the Windows Time guard', enabled: true }] });
  const { page, root } = show(view);
  const box = root.one('section[data-section="health"]').one('div[data-guard]');
  assert.equal(box.one('p[data-guard-line]').textContent, hostile);
  assert.equal(box.one('p[data-guard-line]').dataset.level, 'warn');
  page.click(box.one('button[data-guard-action="installWindowsTimeGuard"]'));
  assert.deepEqual(page.posted.at(-1), { type: 'installWindowsTimeGuard' });
  page.click(box.one('button[data-guard-action="removeWindowsTimeGuard"]'));
  assert.deepEqual(page.posted.at(-1), { type: 'removeWindowsTimeGuard' });
  assert.ok(![root, ...root.descendants()].some((e) => e.tagName === 'IMG' || e.tagName === 'SCRIPT'));
});

test('while the guard elevated run is pending its buttons are disabled, and a press posts nothing', () => {
  const view = buildPanelView(snapshot(goldenOutcomes()), undefined, { line: 'Windows Time guard: Waiting for the elevated PowerShell that installs it…', level: 'none', buttons: [{ id: 'installWindowsTimeGuard', label: 'Install the Windows Time guard', enabled: false }, { id: 'removeWindowsTimeGuard', label: 'Remove the Windows Time guard', enabled: false }] });
  const { page, root } = show(view);
  const buttons = root.one('div[data-guard]').all('button[data-guard-action]');
  assert.equal(buttons.length, 2);
  assert.ok(buttons.every((b) => b.disabled));
  const before = page.posted.length;
  buttons.forEach((b) => page.click(b));
  assert.equal(page.posted.length, before);
});

test('only the Health section carries the guard line', () => {
  const { root } = show(buildPanelView(snapshot(goldenOutcomes())));
  assert.deepEqual(root.all('div[data-guard]').length, 1);
  assert.match(root.one('section[data-section="health"]').one('p[data-guard-line]').textContent, /checking…/);
});
