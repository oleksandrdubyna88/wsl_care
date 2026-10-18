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
  const view = buildPanelView(snapshot(goldenOutcomes()), undefined, { line: hostile, level: 'warn', buttons: [{ id: 'installWindowsTimeGuard', label: 'Install the Windows Time guard', enabled: true }, { id: 'removeWindowsTimeGuard', label: 'Remove the Windows Time guard', enabled: true }], engine: '' });
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
  const view = buildPanelView(snapshot(goldenOutcomes()), undefined, { line: 'Windows Time guard: Waiting for the elevated PowerShell that installs it…', level: 'none', buttons: [{ id: 'installWindowsTimeGuard', label: 'Install the Windows Time guard', enabled: false }, { id: 'removeWindowsTimeGuard', label: 'Remove the Windows Time guard', enabled: false }], engine: '' });
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

// ---- PLAN_gpu_health.md D6: the GPU alerts, RUN in the strict harness ----

const GPU_ALERT = {
  level: 'critical' as const,
  line: 'GPU AMD Radeon AI PRO R9700 is not working (code 22: it is disabled)',
  fix: 'It is disabled. Enable it — no restart is needed.',
  consequence: 'Programs that use this GPU — local models such as Ollama — run on the CPU or do not start.',
  button: { index: 1, label: 'Enable AMD Radeon AI PRO R9700', enabled: true, readAt: 1_760_097_600_000 },
};

test('a GPU alert is the FIRST element of the panel, above the heading, in a role="alert" box — its words as TEXT, its level in words and a sign', () => {
  const { root } = show(buildPanelView(snapshot(goldenOutcomes()), undefined, undefined, undefined, { alerts: [GPU_ALERT], line: '' }));
  const first = root.children[0];
  assert.equal(first?.dataset.gpuAlerts, '', 'the alerts container comes before the header');
  assert.equal(first?.attributes.role, 'alert');
  const alert = first?.one('div[data-gpu-alert]');
  assert.equal(alert?.dataset.level, 'critical');
  assert.equal(alert?.one('p[data-gpu-line]').textContent, `⚠ ${GPU_ALERT.line}`, 'the level is a sign and words, not a colour only');
  assert.equal(alert?.one('p[data-gpu-fix]').textContent, GPU_ALERT.fix);
  assert.equal(alert?.one('p[data-gpu-consequence]').textContent, GPU_ALERT.consequence);
});

test('the alert\'s Enable button posts ONLY { type: "enableGpu", index, readAt } — the host holds the device id, and the READ the button came from is named so a press after a newer read is refused; a disabled one posts nothing', () => {
  const { page, root } = show(buildPanelView(snapshot(goldenOutcomes()), undefined, undefined, undefined, { alerts: [GPU_ALERT], line: '' }));
  const button = root.one('button[data-gpu-action="enableGpu"]');
  assert.equal(button.textContent, 'Enable AMD Radeon AI PRO R9700');
  assert.equal(button.attributes.type, 'button');
  page.click(button);
  assert.deepEqual(page.posted.at(-1), { type: 'enableGpu', index: 1, readAt: 1_760_097_600_000 });

  const waiting = show(buildPanelView(snapshot(goldenOutcomes()), undefined, undefined, undefined, { alerts: [{ ...GPU_ALERT, button: { ...GPU_ALERT.button, enabled: false } }], line: '' }));
  const before = waiting.page.posted.length;
  waiting.page.click(waiting.root.one('button[data-gpu-action="enableGpu"]'));
  assert.equal(waiting.page.posted.length, before);
});

test('the alerts box is the SAME element across renders and is rebuilt only when its words change — announced once, not on every render', () => {
  const view = buildPanelView(snapshot(goldenOutcomes()), undefined, undefined, undefined, { alerts: [GPU_ALERT], line: '' });
  const { page, root } = show(view);
  const box = root.children[0];
  const alert = box?.one('div[data-gpu-alert]');
  page.message({ type: 'view', view: structuredClone(view) });
  assert.equal(root.children[0], box, 'the same container');
  assert.equal(root.children[0]?.one('div[data-gpu-alert]'), alert, 'unchanged words: the same alert element, nothing re-announced');
  page.message({ type: 'view', view: buildPanelView(snapshot(goldenOutcomes())) });
  assert.equal(root.children[0], box);
  assert.equal(box?.children.length, 0, 'no alert: the container stays, empty');
});

test('own review 2 F4: a new READ with the same words does not rebuild (re-announce) the alert — and the button still posts the NEWEST readAt', () => {
  const { page, root } = show(buildPanelView(snapshot(goldenOutcomes()), undefined, undefined, undefined, { alerts: [GPU_ALERT], line: '' }));
  const alert = root.children[0]?.one('div[data-gpu-alert]');
  const later = { ...GPU_ALERT, button: { ...GPU_ALERT.button, readAt: GPU_ALERT.button.readAt + 900_000 } };
  page.message({ type: 'view', view: buildPanelView(snapshot(goldenOutcomes()), undefined, undefined, undefined, { alerts: [later], line: '' }) });
  assert.equal(root.children[0]?.one('div[data-gpu-alert]'), alert, 'the same words from a newer read: the same element, nothing re-announced');
  page.click(root.one('button[data-gpu-action="enableGpu"]'));
  assert.deepEqual(page.posted.at(-1), { type: 'enableGpu', index: 1, readAt: GPU_ALERT.button.readAt + 900_000 }, 'the click names the read the host holds NOW, not the one the element was built from');
});

test('code round 4: the live region is mounted ONCE and never detached — a render replaces the rest of the panel beside it, so an unrelated update cannot re-announce the alert', () => {
  const { page, root } = show(buildPanelView(snapshot(goldenOutcomes()), undefined, undefined, undefined, { alerts: [GPU_ALERT], line: '' }));
  const box = root.children[0];
  const rest = root.children[1];
  assert.ok(box !== undefined && rest !== undefined && root.children.length === 2, 'two children: the live region and the rest of the panel');
  assert.equal(rest.one('header[data-part="header"]').dataset.part, 'header', 'the heading, the notice and the sections live in the second child');
  let detached = 0;
  let parent = box.parent;
  Object.defineProperty(box, 'parent', { configurable: true, get: () => parent, set: (value: Element | undefined) => { if (value === undefined) { detached++; } parent = value; } });
  page.message({ type: 'view', view: buildPanelView(snapshot(goldenOutcomes()), undefined, undefined, undefined, { alerts: [GPU_ALERT], line: '' }) });
  page.message({ type: 'view', view: buildPanelView(snapshot(goldenOutcomes())) });
  assert.equal(detached, 0, 'the live region was never taken out of the document');
  assert.equal(root.children[0], box);
  assert.equal(root.children[1], rest, 'the rest of the panel is the same container, its children replaced');
});

test('hostile adapter text renders as inert TEXT; an unknown check is an alert without a button; all working is one muted line in Health', () => {
  const hostile = 'GPU </script><img src=x onerror=alert(1)> is not working (code 22: it is disabled)';
  const { root } = show(buildPanelView(snapshot(goldenOutcomes()), undefined, undefined, undefined, { alerts: [{ ...GPU_ALERT, line: hostile }, { level: 'unknown', line: 'GPU state could not be checked: x', fix: 'Refresh to try again.', consequence: '', button: undefined }], line: '' }));
  assert.ok(![root, ...root.descendants()].some((e) => e.tagName === 'IMG' || e.tagName === 'SCRIPT'));
  const alerts = root.all('div[data-gpu-alert]');
  assert.deepEqual(alerts.map((a) => a.dataset.level), ['critical', 'unknown']);
  assert.equal(alerts[1]?.all('button[data-gpu-action]').length, 0);
  assert.equal(alerts[1]?.all('p[data-gpu-consequence]').length, 0, 'an empty consequence draws no line');

  const ok = show(buildPanelView(snapshot(goldenOutcomes()), undefined, undefined, undefined, { alerts: [], line: 'GPU: AMD Radeon(TM) 890M Graphics — working (checked 14:31)' }));
  assert.equal(ok.root.one('section[data-section="health"]').one('p[data-gpu-ok]').textContent, 'GPU: AMD Radeon(TM) 890M Graphics — working (checked 14:31)');
  assert.equal(ok.root.all('div[data-gpu-alert]').length, 0);
});

// ---- S7b.2b: the client-gone relays' line and button in AI agents ----

function relaysView(line: string, enabled: boolean, why = ''): PanelView {
  return buildPanelView(snapshot(goldenOutcomes()), undefined, undefined, undefined, undefined, { line, level: 'none', why, button: { id: 'stopRelays', label: 'Stop client-gone relays…', enabled } });
}

test('the relays line sits in the AI agents section as TEXT, and its button posts only its bare id', () => {
  const hostile = 'Windows MCP relays: 2 </script><img src=x onerror=alert(1)>';
  const { page, root } = show(relaysView(hostile, true));
  const box = root.one('section[data-section="aiAgents"]').one('div[data-relays]');
  assert.equal(box.one('p[data-relays-line]').textContent, hostile);
  page.click(box.one('button[data-relays-action="stopRelays"]'));
  assert.deepEqual(page.posted.at(-1), { type: 'stopRelays' });
  assert.ok(![root, ...root.descendants()].some((e) => e.tagName === 'IMG' || e.tagName === 'SCRIPT'));
});

test('a greyed relays button shows why and posts nothing when pressed', () => {
  const { page, root } = show(relaysView('Windows MCP relays: 0 (0 re-parented)', false, 'a run is in flight'));
  const box = root.one('div[data-relays]');
  assert.equal(box.one('p[data-relays-why]').textContent, 'a run is in flight');
  const button = box.one('button[data-relays-action="stopRelays"]');
  assert.ok(button.disabled);
  const before = page.posted.length;
  page.click(button);
  assert.equal(page.posted.length, before);
});

test('an older daemon (no interopRelays) renders no relays part at all', () => {
  const { root } = show(buildPanelView(snapshot(goldenOutcomes())));
  assert.equal(root.all('div[data-relays]').length, 0);
});

// ---- PLAN_pwsh_engine_windows_time.md D6: each part's engine, as TEXT ----

test('pwsh D6: the guard part shows its engine line as TEXT under its line — and no paragraph when the host gives none', () => {
  const hostile = 'Elevated runs here: PowerShell 7 (</script><img src=x onerror=alert(1)>).';
  const guard = { line: 'Windows Time guard: not installed', level: 'none' as const, buttons: [], engine: hostile };
  const { root } = show(buildPanelView(snapshot(goldenOutcomes()), undefined, guard));
  assert.equal(root.one('div[data-guard]').one('p[data-guard-engine]').textContent, hostile);
  assert.ok(![root, ...root.descendants()].some((e) => e.tagName === 'IMG' || e.tagName === 'SCRIPT'));
  const { root: plain } = show(buildPanelView(snapshot(goldenOutcomes()), undefined, { ...guard, engine: '' }));
  assert.equal(plain.one('div[data-guard]').all('p[data-guard-engine]').length, 0);
});

test('pwsh D6: a GPU alert shows the engine Enable elevates as TEXT, only when the host set it', () => {
  const button = { index: 0, label: 'Enable AMD Radeon AI PRO R9700', enabled: true, readAt: 1 };
  const alert = { level: 'critical' as const, line: 'GPU AMD Radeon AI PRO R9700 is not working (code 22: it is disabled)', fix: 'Enable it.', consequence: '', button };
  const named = show(buildPanelView(snapshot(goldenOutcomes()), undefined, undefined, undefined, { alerts: [{ ...alert, engine: 'Enable runs elevated in: PowerShell 7 (P).' }], line: '' }));
  assert.equal(named.root.one('div[data-gpu-alert]').one('p[data-gpu-engine]').textContent, 'Enable runs elevated in: PowerShell 7 (P).');
  const bare = show(buildPanelView(snapshot(goldenOutcomes()), undefined, undefined, undefined, { alerts: [alert], line: '' }));
  assert.equal(bare.root.one('div[data-gpu-alert]').all('p[data-gpu-engine]').length, 0);
});

// ---- E7.S5a (research/PLAN_bundle_windows_binary.md §3.7): the bundled Windows daemon's row in Health, as TEXT ----

test('E7.S5a: the Windows row sits in Health as TEXT with its level — and renders nothing when the host says nothing', () => {
  const hostile = 'Windows daemon (bundled): 0.3.0+</script><img src=x onerror=alert(1)> — not 0.4.0, the build this extension was packaged with: reinstall the extension';
  const { root } = show(buildPanelView(snapshot(goldenOutcomes()), undefined, undefined, undefined, undefined, undefined, { line: hostile, level: 'warn' }));
  const row = root.one('section[data-section="health"]').one('p[data-windows-daemon]');
  assert.equal(row.textContent, hostile);
  assert.equal(row.dataset.level, 'warn');
  assert.ok(![root, ...root.descendants()].some((e) => e.tagName === 'IMG' || e.tagName === 'SCRIPT'));
  const { root: quiet } = show(buildPanelView(snapshot(goldenOutcomes())));
  assert.equal(quiet.all('p[data-windows-daemon]').length, 0, 'off Windows (or not yet asked by a caller) no row');
});

// ---- WR-E2 (PLAN_windows_runner.md §3.8): the Windows tasks' lines in Health, as TEXT ----

const WINDOWS_TASKS_VIEW = {
  copy: { line: 'Windows daemon copy: 0.4.0 (0123456789ab) staged', level: 'ok' as const },
  tasks: [
    { id: 'watch' as const, line: 'Windows watch task: installed, last run <2026-10-16T08:00:00Z>: succeeded', level: 'ok' as const },
    { id: 'archive' as const, line: 'Windows archive task: not installed', level: 'none' as const },
  ],
  dry: 'Windows tasks, dry run: the setting dryRun is on',
};

test('WR-E2: the Windows tasks\' lines sit in Health as TEXT with their levels — the copy line, one per task, the dry line', () => {
  const { root } = show(buildPanelView(snapshot(goldenOutcomes()), undefined, undefined, undefined, undefined, undefined, undefined, WINDOWS_TASKS_VIEW));
  const box = root.one('section[data-section="health"]').one('div[data-windows-tasks]');
  assert.equal(box.one('p[data-windows-tasks-copy]').textContent, WINDOWS_TASKS_VIEW.copy.line);
  assert.equal(box.one('p[data-windows-tasks-copy]').dataset.level, 'ok');
  assert.deepEqual(box.all('p[data-windows-task]').map((p) => [p.dataset['windowsTask'], p.textContent, p.dataset.level]), WINDOWS_TASKS_VIEW.tasks.map((t) => [t.id, t.line, t.level]));
  assert.equal(box.one('p[data-windows-tasks-dry]').textContent, WINDOWS_TASKS_VIEW.dry);
});

test('WR-E2: a hostile word in a task\'s line is shown as characters, never as markup', () => {
  const hostile = 'Windows watch task: unreadable — </script><img src=x onerror=alert(1)>';
  const view = { ...WINDOWS_TASKS_VIEW, tasks: [{ id: 'watch' as const, line: hostile, level: 'unknown' as const }] };
  const { root } = show(buildPanelView(snapshot(goldenOutcomes()), undefined, undefined, undefined, undefined, undefined, undefined, view));
  assert.equal(root.one('p[data-windows-task="watch"]').textContent, hostile);
  assert.ok(![root, ...root.descendants()].some((e) => e.tagName === 'IMG' || e.tagName === 'SCRIPT'));
});

test('WR-E2: when the host says nothing (off Windows, or no caller derived it) no Windows tasks part is drawn — and the dry line only when there is one', () => {
  assert.equal(show(buildPanelView(snapshot(goldenOutcomes()))).root.all('div[data-windows-tasks]').length, 0);
  const { root } = show(buildPanelView(snapshot(goldenOutcomes()), undefined, undefined, undefined, undefined, undefined, undefined, { ...WINDOWS_TASKS_VIEW, dry: '' }));
  assert.equal(root.all('p[data-windows-tasks-dry]').length, 0);
  assert.equal(root.all('div[data-windows-tasks]').length, 1);
  assert.equal(buildPanelView(snapshot(goldenOutcomes())).windowsTasks.copy.line, '');
});
