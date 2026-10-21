import assert from 'node:assert/strict';
import { test } from 'node:test';

import { ceilingMs, type HostCall } from '../client/ceilings';
import { DOCKER_SNAPSHOT_S, MARGIN_S, previewWorstCaseS, WORST_CASE_S, worstCasesOf } from '../client/worstCases';
import { FALLBACK_LIMITS, LIMIT_FIELDS, type DaemonLimits } from '../shared/daemonLimits';
import { MAX_SHOWN_VOLUMES, shownCap } from '../root/rootIds';
import { ROW_IDS } from '../cleanup/rowIds';
import { DEFAULT_NUMBERS, NUMBER_NAMES, NUMBER_SETTINGS, type Numbers } from '../settings/numbers';

/**
 * Plan §15q N-1–N-3 (the E7 numbers inventory): every host ceiling is STRICTLY above the daemon's own worst case for its
 * call — derived from the daemon's per-command ceilings and what the call can do (`client/worstCases.ts`) — with the
 * defaults AND with every setting at its minimum, and for a cleanup's preview for EVERY selection of rows (each Docker row
 * takes its own Docker snapshot).
 */

const MINIMUMS = Object.fromEntries(NUMBER_NAMES.map((name) => [name, NUMBER_SETTINGS[name].minimum])) as Numbers;

/** Every non-empty selection of the cleanup rows — and A13 alone, *Archive now*'s preview (E10.S1b: no row carries A13). */
function selections(): string[][] {
  const all: string[][] = [];
  for (let mask = 1; mask < 1 << ROW_IDS.length; mask += 1) {
    all.push(ROW_IDS.filter((_id, i) => (mask & (1 << i)) !== 0));
  }
  all.push(['A13']);
  return all;
}

const FIXED: readonly [HostCall, number][] = [
  [{ call: 'status' }, WORST_CASE_S.status],
  [{ call: 'windowsStatus' }, WORST_CASE_S.windowsStatus],
  [{ call: 'version' }, WORST_CASE_S.version],
  [{ call: 'rootCheck' }, WORST_CASE_S.version],
  [{ call: 'doctor' }, WORST_CASE_S.doctor],
  [{ call: 'preview' }, WORST_CASE_S.preview],
  [{ call: 'runRead' }, WORST_CASE_S.runRead],
  [{ call: 'windowsTempPreview' }, WORST_CASE_S.windowsTempPreview],
  [{ call: 'detach' }, WORST_CASE_S.detach],
  [{ call: 'stop' }, WORST_CASE_S.stop],
];

test('the derived worst cases — the inventory\'s own numbers: a snapshot 330 s, doctor 109 s, a detach with two stale requests 101 s, with 32 671 s', () => {
  assert.equal(DOCKER_SNAPSHOT_S, 330, '310 s of ceilings + 20 s of drain');
  assert.equal(WORST_CASE_S.doctor, 109);
  assert.equal(10 + 2 * 19 + 34 + 19, 101, 'N-3 as the inventory counted it');
  assert.equal(WORST_CASE_S.detach, 671);
  assert.equal(WORST_CASE_S.stop, 124);
  assert.equal(previewWorstCaseS(['A4', 'A5', 'A5Testcontainers', 'A6', 'A6Unused', 'A7']), 1980, 'N-2: six Docker rows, six snapshots');
  assert.equal(previewWorstCaseS([...ROW_IDS]), 2014, 'and A9\'s snap listing');
});

for (const [name, numbers] of [['the defaults', DEFAULT_NUMBERS], ['every setting at its minimum', MINIMUMS]] as const) {
  test(`N-1 / N-3: every fixed call's host ceiling is strictly above its worst case — ${name}`, () => {
    for (const [call, worstS] of FIXED) {
      assert.ok(ceilingMs(numbers, call) > worstS * 1000, `${call.call}: ${ceilingMs(numbers, call)} ms against a worst case of ${worstS} s`);
    }
  });

  test(`N-2: a cleanup's preview ceiling is strictly above its worst case for every selection of rows — ${name}`, () => {
    for (const ids of selections()) {
      assert.ok(ceilingMs(numbers, { call: 'rootPreview', ids }) > previewWorstCaseS(ids) * 1000, `${ids.join(',')}: ${ceilingMs(numbers, { call: 'rootPreview', ids })} ms`);
    }
  });
}

test('each ceiling setting\'s minimum is its worst case plus the stated margin — no setting can reach the worst case', () => {
  const ceilings: readonly [keyof Numbers, number][] = [
    ['statusSeconds', WORST_CASE_S.status], ['windowsStatusSeconds', WORST_CASE_S.windowsStatus], ['versionSeconds', WORST_CASE_S.version], ['doctorSeconds', WORST_CASE_S.doctor],
    ['previewSeconds', WORST_CASE_S.preview], ['previewPerDockerRowSeconds', DOCKER_SNAPSHOT_S], ['runReadSeconds', WORST_CASE_S.runRead],
    ['detachSeconds', WORST_CASE_S.detach], ['stopSeconds', WORST_CASE_S.stop], ['windowsTempPreviewSeconds', WORST_CASE_S.windowsTempPreview],
  ];
  for (const [name, worstS] of ceilings) {
    assert.equal(NUMBER_SETTINGS[name].minimum, worstS + MARGIN_S, name);
    assert.ok(NUMBER_SETTINGS[name].default >= NUMBER_SETTINGS[name].minimum, name);
  }
});

test('a ceiling follows its setting — a raised detach or per-row preview setting is what the call gets', () => {
  const raised: Numbers = { ...DEFAULT_NUMBERS, detachSeconds: 900, previewPerDockerRowSeconds: 400 };
  assert.equal(ceilingMs(raised, { call: 'detach' }), 900_000);
  assert.equal(ceilingMs(raised, { call: 'rootPreview', ids: ['A4', 'A5', 'A9'] }), (2 * 400 + 34 + raised.statusSeconds) * 1000, 'two snapshots, the snap listing, and the base of a call that runs no command');
  assert.equal(ceilingMs(raised, { call: 'rootPreview', ids: ['A8'] }), raised.statusSeconds * 1000, 'A8 reads a folder: the base alone');
});

// ---- #17: the daemon publishes its drain grace and its unit stop timeout (status.limits) ----

/** Every limit at its contract maximum — the dearest daemon a setting must still sit above. */
/** Every published limit at its CONTRACT maximum (contracts/status-limits.json, mirrored by LIMIT_FIELDS). */
const MAX_LIMITS: DaemonLimits = LIMIT_FIELDS.reduce<DaemonLimits>((limits, field) => ({ ...limits, [field.into]: field.max * field.scale }), FALLBACK_LIMITS);

test('#17: the contract maximum is what the ceilings are held to — and A4\'s shown cap there is the compiled bound, never above it', () => {
  assert.deepEqual([MAX_LIMITS.drainGraceMs, MAX_LIMITS.unitStopSeconds, MAX_LIMITS.maxShownNames], [10_000, 600, 10_000]);
  assert.equal(shownCap(MAX_LIMITS.maxShownNames), MAX_SHOWN_VOLUMES);
});

test('#17: the worst cases follow the published drain grace and unit stop timeout — today\'s values reproduce the inventory\'s numbers', () => {
  assert.deepEqual(worstCasesOf(FALLBACK_LIMITS), { ...WORST_CASE_S, snapshot: DOCKER_SNAPSHOT_S, snapList: 34, relayScan: 30 }, 'the fallback is today');
  const dear = worstCasesOf(MAX_LIMITS);
  assert.equal(dear.stop, 600 + 20, 'systemctl stop at 600 s, then two 10 s drains');
  assert.equal(dear.doctor, 5 * (15 + 20) + (10 + 20));
  assert.equal(dear.snapshot, 310 + 5 * 20);
});

test('#38: status waits the MCP CPU window and the run-log listing — its worst case follows both published limits', () => {
  // daemon #38 (E7.S2d): `status` samples each running MCP server's CPU across `mcpServers.cpuWindowMilliseconds` and lists
  // its run logs within `mcpServers.logListMilliseconds` (research/module_mcp_servers.md: "status takes … plus the CPU window").
  assert.equal(worstCasesOf(FALLBACK_LIMITS).status, (1000 + 1000) / 1000, 'today: a 1 s window and a 1 s listing');
  assert.equal(worstCasesOf(MAX_LIMITS).status, (5000 + 5000) / 1000, 'both at their contract maximum, 5 s each');
});

for (const [name, numbers] of [['the defaults', DEFAULT_NUMBERS], ['every setting at its minimum', MINIMUMS]] as const) {
  test(`#17: with every published limit at its MAXIMUM, each ceiling is still strictly above the daemon's worst case — ${name}`, () => {
    const dear = worstCasesOf(MAX_LIMITS);
    const fixed: readonly [HostCall, number][] = [
      [{ call: 'status' }, dear.status], [{ call: 'windowsStatus' }, dear.windowsStatus], [{ call: 'version' }, dear.version], [{ call: 'rootCheck' }, dear.version], [{ call: 'doctor' }, dear.doctor],
      [{ call: 'preview' }, dear.preview], [{ call: 'runRead' }, dear.runRead], [{ call: 'detach' }, dear.detach], [{ call: 'stop' }, dear.stop],
    ];
    for (const [call, worstS] of fixed) {
      assert.ok(ceilingMs(numbers, call, MAX_LIMITS) > worstS * 1000, `${call.call}: ${ceilingMs(numbers, call, MAX_LIMITS)} ms against ${worstS} s`);
    }
    for (const ids of selections()) {
      assert.ok(ceilingMs(numbers, { call: 'rootPreview', ids }, MAX_LIMITS) > previewWorstCaseS(ids, MAX_LIMITS) * 1000, ids.join(','));
    }
  });
}

test('E19.S2b: the bundled exe\'s status waits one MCP CPU window and starts no child — its worst case is the distro status\'s expression, and its setting a ceiling of its own', () => {
  // The Windows binary's status reads the Windows process table and samples each MCP server across ONE mcpServers.cpuWindowMilliseconds
  // (WindowsMcpCollector, waiting only for an instance its ledger has no point of), reads no run logs and starts no process. The bound
  // kept is the distro status's — window plus listing — so the figure holds should the Windows side ever list logs too.
  assert.equal(worstCasesOf(FALLBACK_LIMITS).windowsStatus, worstCasesOf(FALLBACK_LIMITS).status);
  assert.equal(worstCasesOf(MAX_LIMITS).windowsStatus, (5000 + 5000) / 1000);
  assert.equal(NUMBER_SETTINGS.windowsStatusSeconds.key, 'timeouts.windowsStatusSeconds');
  assert.equal(ceilingMs({ ...DEFAULT_NUMBERS, windowsStatusSeconds: 33, statusSeconds: 20 }, { call: 'windowsStatus' }), 33_000, 'it follows its own setting, never the distro\'s');
  assert.equal(ceilingMs({ ...DEFAULT_NUMBERS, windowsStatusSeconds: 33, statusSeconds: 44 }, { call: 'status' }), 44_000, 'and the distro\'s call never follows the Windows setting');
});

test('WR-T3: the ceiling of the Windows TEMP preview covers ONE allowance of temp.scanMaxSeconds at its default (120 s) and the margin, and reaches 3 700 s', () => {
  assert.equal(WORST_CASE_S.windowsTempPreview, 120);
  const setting = NUMBER_SETTINGS.windowsTempPreviewSeconds;
  assert.deepEqual([setting.default, setting.minimum, setting.maximum], [180, 130, 3_700]);
  assert.match(setting.description, /temp.scanMaxSeconds/);
  assert.equal(ceilingMs(DEFAULT_NUMBERS, { call: 'windowsTempPreview' }), 180_000);
  assert.equal(ceilingMs({ ...DEFAULT_NUMBERS, windowsTempPreviewSeconds: 3_700 }, { call: 'windowsTempPreview' }), 3_700_000, 'a person who raised temp.scanMaxSeconds to its maximum can cover it');
});
