import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

import type { JsonObject } from '../../client/outcome';
import { buildLogsView } from '../../logsPage/logsViewModel';
import type { LogsState, LogsView, ReadState } from '../../logsPage/logsView';
import { retainedDays, windowOf, type Period } from '../../logsPage/period';
import { gb, gib, localMinuteOf, metricText, percent } from '../../text/format';
import { Element, runPageScript, type Page } from '../support/pageHarness';
import { FORBIDDEN_NAMES, namesIn } from '../support/pageSource';
import { EXTENSION_ROOT, GOLDEN_ROOT } from '../support/paths';
import { withZone } from '../support/zone';

/**
 * The Logs page's script RUN in the strict harness over the daemon's goldens (plan §16 E6.S4 acceptance,
 * `common.generated-code-tests` §1): `media/logs.js` — the file the webview loads — is executed, the view built by the REAL
 * view model is delivered, and every block's text is compared with the JSON field it shows, spelt by the shared format
 * module. The expected values are read from the golden HERE, not from the view model, and a planted answer whose figures
 * do not add up is shown exactly as answered: the page (and the view model) computes nothing.
 */

const LOGS_SCRIPT = path.join(EXTENSION_ROOT, 'media', 'logs.js');
const NOW = Date.parse('2026-10-02T09:00:00Z');
const ZONE = 'Europe/Kyiv';

type Json = Record<string, unknown>;

function golden(name: string): Json {
  return JSON.parse(fs.readFileSync(path.join(GOLDEN_ROOT, 'head', name), 'utf8')) as Json;
}

function answered(body: Json): ReadState {
  return { kind: 'answered', body: body as JsonObject };
}

function state(parts: Partial<LogsState> = {}, period: Period = { kind: 'today' }): LogsState {
  return {
    period,
    window: period.kind === 'thisRun' ? undefined : windowOf(period, NOW),
    retained: retainedDays(NOW),
    logs: answered(golden('logs-local-day.json')),
    runs: answered(golden('runs-local-day.json')),
    show: { kind: 'idle' },
    details: new Map(),
    thisRunReason: '',
    notice: '',
    noticeLevel: 'none',
    ...parts,
  };
}

function load(): { page: Page; root: Element } {
  const root = new Element('MAIN');
  return { page: runPageScript(fs.readFileSync(LOGS_SCRIPT, 'utf8'), { logs: root }), root };
}

function show(view: LogsView): { page: Page; root: Element } {
  const loaded = load();
  loaded.page.message({ type: 'view', view: structuredClone(view) });
  return loaded;
}

function viewOf(parts: Partial<LogsState> = {}, period?: Period): LogsView {
  return withZone(ZONE, () => buildLogsView(state(parts, period)));
}

function block(root: Element, id: string): Element {
  return root.one(`section[data-block="${id}"]`);
}

function lineValue(holder: Element, id: string): string {
  return holder.one(`[data-line="${id}"]`).one('[data-value]').textContent;
}

function cells(holder: Element, table: string): string[][] {
  return holder.one(`table[data-table="${table}"]`).all('tr[data-row]').map((row) => row.all('[data-cell]').map((cell) => cell.textContent));
}

const gbOf = (value: unknown): string => gb(value as number);
const at = (value: unknown): string => withZone(ZONE, () => localMinuteOf(value as string));

// ---- every block equals the JSON ----

test('Totals equal the logs answer: freed, objects removed, and every perAction row as the daemon answered it', () => {
  const logs = golden('logs-local-day.json');
  const { root } = show(viewOf());
  const totals = block(root, 'totals');
  assert.equal(lineValue(totals, 'freed'), gbOf(logs.freedBytes));
  assert.equal(lineValue(totals, 'objects'), String(logs.objectsRemoved));
  const perAction = logs.perAction as Json[];
  assert.deepEqual(cells(totals, 'perAction'), perAction.map((a) => [String(a.id), String(a.runs), String(a.count), gbOf(a.freedBytes), String(a.dryRuns), gbOf(a.wouldFreeBytes), String(a.failed)]));
});

test('Runs equal the logs answer\'s counts: with / without a cleanup, dry runs and what they would have freed, timer / button / CLI, failed, interrupted', () => {
  const runs = golden('logs-local-day.json').runs as Json;
  const { root } = show(viewOf());
  const counts = block(root, 'runs');
  for (const key of ['total', 'withCleanup', 'withoutCleanup', 'dryRun', 'timer', 'manual', 'cli', 'failed', 'interrupted']) {
    assert.equal(lineValue(counts, key), String(runs[key]), key);
  }
  assert.equal(lineValue(counts, 'wouldFreeBytes'), gbOf(runs.wouldFreeBytes));
  assert.equal(lineValue(counts, 'unparseableLines'), String(golden('logs-local-day.json').unparseableLines));
});

test('Max / min equal the logs answer: the runs that freed the most and the least, every metric\'s max and min with its time — and vmmemWSL arrives in E7.S3 / E11', () => {
  const logs = golden('logs-local-day.json');
  const { root } = show(viewOf());
  const extremes = block(root, 'extremes');
  for (const key of ['mostFreed', 'leastFreed']) {
    const run = logs[key] as Json;
    assert.equal(lineValue(extremes, key), `${gbOf(run.freedBytes)} — run ${String(run.runId)}, ${at(run.startedAt)}`, key);
  }
  const metrics = (logs.metrics as Json[]).map((m) => {
    const max = m.max as Json;
    const min = m.min as Json;
    return [metricText(String(m.name), String(m.unit), max.value as number), at(max.at), metricText(String(m.name), String(m.unit), min.value as number), at(min.at), String(m.samples)];
  });
  const shown = cells(extremes, 'metrics');
  assert.deepEqual(shown.slice(0, metrics.length).map((row) => row.slice(1)), metrics);
  assert.deepEqual(shown.at(-1), ['vmmemWSL (Windows)', 'arrives in E7.S3 / E11', '—', 'arrives in E7.S3 / E11', '—', '—']);
});

test('the trend (the MemAvailable sparkline\'s data) equals the runs answer\'s RunLine.metrics, run by run; a run without metrics is not a point', () => {
  const runs = (golden('runs-local-day.json').runs as Json[]).filter((run) => typeof run.metrics === 'object');
  const { root } = show(viewOf());
  assert.deepEqual(cells(block(root, 'trend'), 'trend'), runs.map((run) => {
    const metrics = run.metrics as Json;
    return [at(run.startedAt), percent(metrics.memAvailablePercent as number), gib(metrics.swapUsedBytes as number)];
  }));
});

test('the run list equals the runs answer: time, trigger, dry run, actions, freed, outcome — and its count is the answer\'s count', () => {
  const answer = golden('runs-local-day.json');
  const { root } = show(viewOf());
  const list = block(root, 'runList');
  const rows = list.all('tr[data-run]').map((row) => row.all('[data-cell]').map((cell) => cell.textContent));
  assert.deepEqual(rows, (answer.runs as Json[]).map((run) => [
    at(run.startedAt),
    String(run.trigger),
    run.dryRun === true ? 'yes' : 'no',
    (run.actions as Json[]).map((a) => `${String(a.id)} ${String(a.status)} (${String(a.count)})`).join(', '),
    run.dryRun === true ? `${gbOf(run.freedBytes)} (would free ${gbOf(run.wouldFreeBytes)})` : gbOf(run.freedBytes),
    String(run.outcome),
  ]));
  assert.match(list.one('[data-notes]').textContent, new RegExp(`^${String(answer.count)} runs`));
});

test('detailsNotRead is SHOWN: the runs whose objects were not read with the period, and how to read them', () => {
  const logs = { ...golden('logs-local-day.json'), detailsRead: 0, detailsNotRead: 2 };
  const { root } = show(viewOf({ logs: answered(logs) }));
  assert.match(block(root, 'runList').one('[data-notes]').textContent, /objects not read for 2 run\(s\) with a cleanup — expand a run to list them/);
  const none = show(viewOf()).root;
  assert.match(block(none, 'runList').one('[data-notes]').textContent, /objects not read for 0 run\(s\)/, 'shown when 0 too');
});

test('NO ARITHMETIC: an answer whose figures do not add up is shown exactly as answered — nothing summed, nothing counted', () => {
  const logs = golden('logs-local-day.json');
  const planted = { ...logs, freedBytes: 7_000_000_000, objectsRemoved: 41, runs: { ...(logs.runs as Json), total: 99, withCleanup: 2, withoutCleanup: 1 } };
  const runs = { ...golden('runs-local-day.json'), count: 7 };
  const { root } = show(viewOf({ logs: answered(planted), runs: answered(runs) }));
  assert.equal(lineValue(block(root, 'totals'), 'freed'), '7.0 GB', 'not the sum of perAction (0.3 GB)');
  assert.equal(lineValue(block(root, 'totals'), 'objects'), '41', 'not the sum of the counts (5)');
  assert.equal(lineValue(block(root, 'runs'), 'total'), '99', 'not withCleanup + withoutCleanup (3)');
  assert.match(block(root, 'runList').one('[data-notes]').textContent, /^7 runs/, 'the answer\'s count, not the list\'s length (3)');
});

// ---- the lazy object lists, through runs show ----

test('an expanded run shows what runs show answered: every removed object, every command with its exit — equal to the JSON', () => {
  const done = golden('runs-show-done.json');
  const detail = done.detail as Json;
  const action = (detail.actions as Json[])[0] as Json;
  const run = action.run as Json;
  const withObjects = { ...done, detail: { ...detail, actions: [{ ...action, run: { ...run, removed: [{ kind: 'volume', name: '60f2c0fbf6c2', bytes: 154000000, note: 'unattached, 31 days' }], notRemoved: [{ kind: 'volume', name: 'in-use-vol', bytes: null, note: 'in use since the preview' }] } }] } };
  const { root } = show(viewOf({ details: new Map([[1, answered(withObjects)]]) }));
  const holder = root.one('[data-detail-for="1"]');
  assert.deepEqual(cells(holder, 'removed-0'), [['volume', '60f2c0fbf6c2', gbOf(154000000), 'unattached, 31 days']]);
  assert.deepEqual(cells(holder, 'notRemoved-0'), [['volume', 'in-use-vol', '—', 'in use since the preview']]);
  assert.deepEqual(cells(holder, 'commands-0'), (run.commands as Json[]).map((c) => [String(c.display), String(c.outcome), String(c.exit), String(c.detail)]));
  assert.equal(root.all('[data-detail-for]').length, 1, 'only the expanded run carries a detail');
  assert.equal(root.one('button[data-expand="1"]').textContent, 'Hide objects');
  assert.equal(root.one('button[data-expand="0"]').textContent, 'Show objects');
});

test('a detail being read says so; one that failed says why; one the daemon has no file for says its detailState', () => {
  const unknown = golden('runs-show-interrupted.json');
  const { root } = show(viewOf({ details: new Map<number, ReadState>([[0, { kind: 'reading' }], [1, { kind: 'failed', sentence: 'The daemon did not answer within 20 s; wsl.exe was stopped.' }], [2, answered(unknown)]]) }));
  assert.match(root.one('[data-detail-for="0"]').textContent, /Reading/);
  assert.match(root.one('[data-detail-for="1"]').textContent, /unavailable — The daemon did not answer within 20 s/);
  assert.match(root.one('[data-detail-for="2"]').textContent, /interrupted/);
  assert.match(root.one('[data-detail-for="2"]').textContent, /no detail: none/);
});

// ---- This run ----

test('This run: runs show\'s line and detail — state, trigger, times, outcome, freed, the actions, the commands', () => {
  const done = golden('runs-show-done.json');
  const line = done.run as Json;
  const view = viewOf({ logs: { kind: 'idle' }, runs: { kind: 'idle' }, show: answered(done) }, { kind: 'thisRun', runId: '20000101T000000Z-1' });
  const { root } = show(view);
  const run = block(root, 'run');
  assert.equal(lineValue(run, 'state'), String(done.state));
  assert.equal(lineValue(run, 'trigger'), String(line.trigger));
  assert.equal(lineValue(run, 'startedAt'), at(line.startedAt));
  assert.equal(lineValue(run, 'outcome'), String(line.outcome));
  assert.equal(lineValue(run, 'freed'), gbOf(line.freedBytes));
  assert.deepEqual(cells(run, 'actions'), (line.actions as Json[]).map((a) => [String(a.id), String(a.status), String(a.count), gbOf(a.freedBytes)]));
  assert.ok(root.one('section[data-block="detail"]').all('table[data-table="commands-0"]').length === 1);
  assert.equal(root.all('section[data-block="totals"]').length, 0, 'a period\'s totals are not shown for one run');
});

// ---- the controls post only the closed set ----

test('the period buttons post their bare names; the pressed one is marked; a greyed This run posts nothing and says why', () => {
  const { page, root } = show(viewOf({ thisRunReason: 'no cleanup is recorded yet' }));
  assert.equal(root.one('button[data-period="today"]').attributes['aria-pressed'], 'true');
  assert.equal(root.one('button[data-period="yesterday"]').attributes['aria-pressed'], 'false');
  page.click(root.one('button[data-period="yesterday"]'));
  page.click(root.one('button[data-period="today"]'));
  page.click(root.one('button[data-period="thisRun"]'));
  assert.deepEqual(page.posted.filter((m) => (m as { type: string }).type !== 'rendered' && (m as { type: string }).type !== 'ready'), [{ type: 'yesterday' }, { type: 'today' }]);
  assert.equal(root.one('button[data-period="thisRun"]').attributes.title, 'no cleanup is recorded yet');
});

test('the date picker: min / max are the retention\'s bounds; Show day posts the day, Show range the two days — as typed, for the host to check', () => {
  const { page, root } = show(viewOf());
  const day = root.one('[data-picker="day"]');
  assert.deepEqual([day.attributes.type, day.attributes.min, day.attributes.max], ['date', '2026-07-05', '2026-10-02']);
  day.value = '2026-10-01';
  page.click(root.one('button[data-show="day"]'));
  root.one('[data-picker="from"]').value = '2026-09-30';
  root.one('[data-picker="to"]').value = '2026-10-02';
  page.click(root.one('button[data-show="range"]'));
  const posted = page.posted.filter((m) => ['day', 'range'].includes((m as { type: string }).type));
  assert.deepEqual(posted, [{ type: 'day', day: '2026-10-01' }, { type: 'range', from: '2026-09-30', to: '2026-10-02' }]);
});

test('expanding and collapsing post the row\'s INDEX only — never its run id', () => {
  const { page, root } = show(viewOf({ details: new Map([[1, { kind: 'reading' }]]) }));
  page.click(root.one('button[data-expand="0"]'));
  page.click(root.one('button[data-expand="1"]'));
  const posted = page.posted.filter((m) => ['expand', 'collapse'].includes((m as { type: string }).type));
  assert.deepEqual(posted, [{ type: 'expand', index: 0 }, { type: 'collapse', index: 1 }]);
  assert.ok(page.posted.every((m) => !JSON.stringify(m).includes('20000101T000000Z-1')));
});

test('the page says ready, and after each render how many blocks it drew', () => {
  const { page } = show(viewOf());
  assert.deepEqual(page.posted[0], { type: 'ready' });
  assert.deepEqual(page.posted.at(-1), { type: 'rendered', blocks: 5 });
});

// ---- daemon text is inert and sanitised ----

test('a hostile object name or note is shown as its characters, its control and bidi characters made visible', () => {
  const done = golden('runs-show-done.json');
  const detail = done.detail as Json;
  const action = (detail.actions as Json[])[0] as Json;
  const evil = `</script><img src=x onerror=alert(1)>${String.fromCharCode(0x202e)}gpj.exe`;
  const hostile = { ...done, detail: { ...detail, actions: [{ ...action, run: { ...(action.run as Json), removed: [{ kind: 'container', name: evil, bytes: 1, note: `[x](command:workbench.action.terminal.new)${String.fromCharCode(0x2028)}` }] } }] } };
  const { root } = show(viewOf({ details: new Map([[0, answered(hostile)]]) }));
  const [row] = cells(root.one('[data-detail-for="0"]'), 'removed-0');
  assert.equal(row?.[1], `</script><img src=x onerror=alert(1)>${String.fromCharCode(0xfffd)}gpj.exe`);
  assert.equal(row?.[3], `[x](command:workbench.action.terminal.new)${String.fromCharCode(0xfffd)}`);
  assert.equal(root.all('[data-cell]').every((cell) => cell.children.length === 0), true, 'a cell holds text, never elements');
});

test('reading and failed blocks: "Reading…" while the answer is out, "unavailable — <why>" when it did not come', () => {
  const { root } = show(viewOf({ logs: { kind: 'reading' }, runs: { kind: 'failed', sentence: 'The daemon refused the request. wsl-care: unknown option --from' } }));
  assert.equal(block(root, 'totals').dataset.state, 'reading');
  assert.match(block(root, 'totals').textContent, /Reading…/);
  assert.equal(block(root, 'runList').dataset.state, 'failed');
  assert.match(block(root, 'runList').textContent, /unavailable — The daemon refused the request/);
});

test('the page source names no HTML sink, no eval, no timer and no network — read by the parser, as the panel\'s is', () => {
  const names = namesIn(fs.readFileSync(LOGS_SCRIPT, 'utf8'));
  assert.ok(names.includes('textContent') && names.includes('createElement'), 'the scan reads the real page (its known instances)');
  assert.deepEqual(FORBIDDEN_NAMES.filter((n) => names.includes(n)), []);
});

// ---- the E6.S4 review round ----

test('review C4: a render keeps the date inputs — a typed day survives a new view, min / max follow it, an untouched input takes the new value', () => {
  const { page, root } = show(viewOf());
  const day = root.one('[data-picker="day"]');
  const from = root.one('[data-picker="from"]');
  day.value = '2026-09-20';
  const next = viewOf({}, { kind: 'yesterday' });
  page.message({ type: 'view', view: structuredClone({ ...next, picker: { ...next.picker, min: '2026-07-06' } }) });
  assert.equal(root.one('[data-picker="day"]'), day, 'the same element, not a new one');
  assert.equal(day.value, '2026-09-20', 'what the person typed is kept');
  assert.equal(day.attributes.min, '2026-07-06', 'the bounds follow the host');
  assert.equal(from.value, next.picker.from, 'an input nobody touched shows the new period');
  page.click(root.one('button[data-show="day"]'));
  assert.deepEqual(page.posted.filter((m) => (m as { type: string }).type === 'day').at(-1), { type: 'day', day: '2026-09-20' });
});

test('review: a figure the answer does not carry reads "not answered" or "not recorded" — never 0', () => {
  const logs = golden('logs-local-day.json');
  const { freedBytes: _freed, objectsRemoved: _objects, ...withoutTotals } = logs;
  const metrics = (logs.metrics as Json[]).map((metric, i) => (i === 0 ? { name: metric.name, unit: metric.unit, samples: metric.samples, min: metric.min } : metric));
  const { timer: _timer, ...counts } = logs.runs as Json;
  const runs = golden('runs-local-day.json');
  const lines = (runs.runs as Json[]).map((run, i) => (i === 1 ? Object.fromEntries(Object.entries(run).filter(([key]) => key !== 'freedBytes')) : run));
  const { root } = show(viewOf({ logs: answered({ ...withoutTotals, metrics, runs: counts }), runs: answered({ ...runs, runs: lines }) }));
  assert.equal(lineValue(block(root, 'totals'), 'freed'), 'not answered');
  assert.equal(lineValue(block(root, 'totals'), 'objects'), 'not answered');
  assert.equal(lineValue(block(root, 'runs'), 'timer'), 'not answered');
  assert.deepEqual(cells(block(root, 'extremes'), 'metrics')[0]?.slice(1, 3), ['not recorded', '—']);
  const freed = block(root, 'runList').all('tr[data-run]')[1]?.all('[data-cell]')[4]?.textContent;
  assert.equal(freed, 'not answered');
});
