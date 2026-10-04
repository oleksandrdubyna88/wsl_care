import assert from 'node:assert/strict';
import { test } from 'node:test';

import { FIELD_MAP, isArriving, SECTIONS, type ReadRow } from '../../panel/fieldMap';
import { MAX_TEXT } from '../../panel/format';
import { at } from '../../panel/jsonPath';
import type { PanelView, ViewRow } from '../../panel/view';
import { buildPanelView } from '../../panel/viewModel';
import type { Snapshot } from '../../state/outcomeStore';
import { goldenSets } from '../support/paths';
import { answered, failed, goldenOutcomes, headBody, removeAt, setAt } from '../support/outcomes';

/**
 * The panel's view model, built from the field map over the golden answers (plan §7.2, §15g B2): every section, every
 * row, and the four ways a row can be other than a figure — "arrives in E#", "unavailable — <reason>", "update the
 * daemon to see this", and the verb's own failure — never blank and never a made-up 0.
 */

function snapshot(parts: Partial<Snapshot> = {}): Snapshot {
  return { status: undefined, preview: undefined, doctor: undefined, checking: false, ...parts };
}

function rowOf(view: PanelView, id: string): ViewRow {
  const row = view.sections.flatMap((s) => s.rows).find((r) => r.id === id);
  assert.ok(row !== undefined, `no row ${id}`);
  return row;
}

function allRows(view: PanelView): ViewRow[] {
  return view.sections.flatMap((s) => s.rows);
}

for (const set of goldenSets()) {
  test(`over the ${set} goldens: every section in order, every field-map row in its section, none blank`, () => {
    const view = buildPanelView(snapshot(goldenOutcomes(set)));
    assert.deepEqual(view.sections.map((s) => s.id), SECTIONS.map((s) => s.id));
    for (const section of view.sections) {
      assert.deepEqual(section.rows.map((r) => r.id), FIELD_MAP.filter((r) => r.section === section.id).map((r) => r.id), section.id);
    }
    for (const row of allRows(view)) {
      assert.ok(row.value.trim().length > 0, `${row.id} is blank`);
      assert.notEqual(row.state, 'blocked', `${row.id}: ${row.value}`);
      assert.notEqual(row.state, 'checking', row.id);
    }
  });
}

test('every row the field map READS exists at its path in the head goldens — the table names real fields', () => {
  const bodies = { status: headBody('status'), preview: headBody('preview'), doctor: headBody('doctor') };
  const missing = FIELD_MAP.filter((r): r is ReadRow => !isArriving(r)).filter((r) => at(bodies[r.verb], r.path) === undefined).map((r) => `${r.verb} ${r.path}`);
  assert.deepEqual(missing, []);
});

test('the rows the E5 verbs cannot fill say "arrives in E#" with why — never blank, never 0', () => {
  const view = buildPanelView(snapshot(goldenOutcomes()));
  for (const row of FIELD_MAP.filter(isArriving)) {
    const shown = rowOf(view, row.id);
    assert.equal(shown.state, 'arrives');
    assert.equal(shown.value, `arrives in ${row.arrives} — ${row.why}`);
  }
  assert.deepEqual(FIELD_MAP.filter(isArriving).map((r) => r.arrives).sort(), ['E11', 'E6', 'E6', 'E6', 'E6', 'E7', 'E7', 'E7', 'E7']);
});

test('the head goldens\' own available:false figures read "unavailable — <reason>": the npm folder, A8\'s cleanup row', () => {
  const view = buildPanelView(snapshot(goldenOutcomes()));
  const folders = rowOf(view, 'folders.sizes');
  assert.ok(folders.items.some((cells) => cells.includes('unavailable — /golden-root/home/user/.npm does not exist')), JSON.stringify(folders.items));
  const cleanup = rowOf(view, 'cleanup.rows');
  const a8 = cleanup.items.find((cells) => cells[0] === 'A8');
  assert.ok(a8 !== undefined && a8.some((c) => c.startsWith('unavailable — the full run')), JSON.stringify(a8));
});

test('a whole figure answered available:false reads "unavailable — <reason>"', () => {
  const status = answered('status', setAt(headBody('status'), 'vm.disk', { available: false, reason: 'statfs / failed' }));
  const row = rowOf(buildPanelView(snapshot({ ...goldenOutcomes(), status })), 'disk.distro');
  assert.equal(row.state, 'unavailable');
  assert.equal(row.value, 'unavailable — statfs / failed');
});

test('a field an older daemon does not send reads "update the daemon to see this" (plan §6), never 0', () => {
  const body = removeAt(removeAt(headBody('status'), 'vm.memory.fragmentation'), 'productVersion');
  const view = buildPanelView(snapshot({ ...goldenOutcomes(), status: answered('status', body) }));
  for (const id of ['memory.fragmentation', 'health.version']) {
    assert.equal(rowOf(view, id).state, 'missing', id);
    assert.equal(rowOf(view, id).value, 'update the daemon to see this', id);
  }
});

test('an unknown preview schema blanks ONLY the preview rows, with the verb\'s state; the memory rows stand', () => {
  const preview = failed('preview', { kind: 'needsNewerExtension', schemaVersion: 2 });
  const view = buildPanelView(snapshot({ ...goldenOutcomes(), preview }));
  assert.equal(rowOf(view, 'cleanup.rows').state, 'blocked');
  assert.match(rowOf(view, 'cleanup.rows').value, /needs a newer extension/);
  assert.equal(rowOf(view, 'containers.totals').state, 'blocked');
  assert.equal(rowOf(view, 'memory.total').state, 'value');
  assert.equal(rowOf(view, 'health.checks').state, 'value');
});

test('a stopped distribution: every row says so, the notice says no call was made, and "Start WSL and check" is offered', () => {
  const stop = { kind: 'stopped' as const, distro: 'Ubuntu' };
  const view = buildPanelView(snapshot({ status: failed('status', stop), preview: failed('preview', stop), doctor: failed('doctor', stop) }));
  for (const row of allRows(view).filter((r) => r.state !== 'arrives')) {
    assert.equal(row.state, 'blocked', row.id);
    assert.equal(row.value, 'WSL stopped', row.id);
  }
  assert.match(view.notice, /not running/);
  assert.deepEqual(view.actions.map((a) => a.id), ['startWsl', 'refresh', 'openSettings']);
});

test('without a stop there is no start button — only Refresh and Settings', () => {
  assert.deepEqual(buildPanelView(snapshot(goldenOutcomes())).actions.map((a) => a.id), ['refresh', 'openSettings']);
});

test('before anything was asked every read row says "checking…"', () => {
  const view = buildPanelView(snapshot({ checking: true }));
  assert.ok(allRows(view).filter((r) => r.state !== 'arrives').every((r) => r.state === 'checking' && r.value === 'checking…'));
});

test('figures over the head golden read as the daemon measured them', () => {
  const view = buildPanelView(snapshot(goldenOutcomes()));
  assert.equal(rowOf(view, 'memory.total').value, '44.9 GiB');
  assert.equal(rowOf(view, 'memory.availablePercent').value, '66.8 %');
  assert.equal(rowOf(view, 'disk.distro').value, '13 % used — 13.0 GB of 100.0 GB, 87.0 GB free');
  assert.equal(rowOf(view, 'containers.now').value, '10 running — 4.2 GiB memory.current');
  assert.match(rowOf(view, 'holders.processes').value, /^30 processes/);
  assert.equal(rowOf(view, 'holders.processes').items.length, 30);
  assert.match(rowOf(view, 'starts.last24h').value, /partial/);
  assert.equal(rowOf(view, 'health.clockJumps').level, 'warn');
  assert.match(rowOf(view, 'health.clockJumps').value, /875 in 4\.0 h/);
  assert.equal(rowOf(view, 'health.configError').value, 'none');
  assert.equal(rowOf(view, 'cleanup.rows').items.length, 8);
});

test('slow parts carry their age: the container stats and the folder walk say which full run measured them', () => {
  const view = buildPanelView(snapshot(goldenOutcomes()));
  assert.match(rowOf(view, 'holders.containers').value, /from the full run 20000101T000000Z-1, just now/);
  assert.match(rowOf(view, 'folders.sizes').value, /from the full run 20000101T000000Z-1, just now/);
});

test('hostile process and container strings reach the view as clipped, visible TEXT', () => {
  const body = headBody('status');
  const top = (body.vm as { processes: { top: Record<string, unknown>[] } }).processes.top;
  top[0] = { ...top[0], name: '</script><img src=x onerror=alert(1)>', commandLine: 'X'.repeat(10_000) };
  top[1] = { ...top[1], name: 'bell\u0007esc\u001b[31mrtl\u202Eexe' };
  const stats = (body.slow as { containerStats: { containers: Record<string, unknown>[] } }).containerStats.containers;
  stats[0] = { ...stats[0], name: '<b onmouseover=alert(2)>c</b>' };
  const view = buildPanelView(snapshot({ ...goldenOutcomes(), status: answered('status', body) }));
  const cells = rowOf(view, 'holders.processes').items.flat();
  assert.ok(cells.includes('</script><img src=x onerror=alert(1)>'));
  assert.ok(cells.includes(`${'X'.repeat(MAX_TEXT)}…`));
  assert.ok(cells.includes('bell\uFFFDesc\uFFFD[31mrtl\uFFFDexe'));
  assert.ok(rowOf(view, 'holders.containers').items.flat().includes('<b onmouseover=alert(2)>c</b>'));
});

test('the view is plain data: it survives structuredClone unchanged (what postMessage does)', () => {
  const view = buildPanelView(snapshot(goldenOutcomes()));
  assert.deepEqual(structuredClone(view), view);
});

test('E5.S3: a daemon that is not installed offers "Install daemon" first — and only then', () => {
  const missing = { kind: 'notInstalled' as const, distro: 'Ubuntu' };
  const view = buildPanelView(snapshot({ status: failed('status', missing) }));
  assert.deepEqual(view.actions, [{ id: 'installDaemon', label: 'Install daemon' }, { id: 'refresh', label: 'Refresh' }, { id: 'openSettings', label: 'Settings' }]);
  assert.match(view.notice, /not installed/);
  const old = { kind: 'unsupportedDistro' as const, distro: 'Ubuntu', detail: 'GLIBC_2.38' };
  assert.deepEqual(buildPanelView(snapshot({ status: failed('status', old) })).actions.map((a) => a.id), ['refresh', 'openSettings'], 'an unsupported distribution gets no install button');
});

test('§15h #1: a crowded machine\'s preview timeout shows "too many containers for a quick preview (<n>)" on the cleanup rows', () => {
  const crowded = { kind: 'previewTooManyContainers' as const, containers: 140, timeoutMs: 330_000 };
  const view = buildPanelView(snapshot({ ...goldenOutcomes(), preview: failed('preview', crowded) }));
  const previewRows = allRows(view).filter((row) => FIELD_MAP.some((f) => f.id === row.id && !isArriving(f) && f.verb === 'preview'));
  assert.ok(previewRows.length > 0);
  for (const row of previewRows) {
    assert.equal(row.value, 'too many containers for a quick preview (140)', row.id);
  }
});
