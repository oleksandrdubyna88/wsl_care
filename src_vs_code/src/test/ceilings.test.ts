import assert from 'node:assert/strict';
import { test } from 'node:test';

import { ceilingMs, type HostCall } from '../client/ceilings';
import { DOCKER_SNAPSHOT_S, MARGIN_S, previewWorstCaseS, WORST_CASE_S } from '../client/worstCases';
import { ROW_IDS } from '../cleanup/rowIds';
import { DEFAULT_NUMBERS, NUMBER_NAMES, NUMBER_SETTINGS, type Numbers } from '../settings/numbers';

/**
 * Plan §15q N-1–N-3 (the E7 numbers inventory): every host ceiling is STRICTLY above the daemon's own worst case for its
 * call — derived from the daemon's per-command ceilings and what the call can do (`client/worstCases.ts`) — with the
 * defaults AND with every setting at its minimum, and for a cleanup's preview for EVERY selection of rows (each Docker row
 * takes its own Docker snapshot).
 */

const MINIMUMS = Object.fromEntries(NUMBER_NAMES.map((name) => [name, NUMBER_SETTINGS[name].minimum])) as Numbers;

/** Every non-empty selection of the cleanup rows. */
function selections(): string[][] {
  const all: string[][] = [];
  for (let mask = 1; mask < 1 << ROW_IDS.length; mask += 1) {
    all.push(ROW_IDS.filter((_id, i) => (mask & (1 << i)) !== 0));
  }
  return all;
}

const FIXED: readonly [HostCall, number][] = [
  [{ call: 'status' }, WORST_CASE_S.status],
  [{ call: 'version' }, WORST_CASE_S.version],
  [{ call: 'rootCheck' }, WORST_CASE_S.version],
  [{ call: 'doctor' }, WORST_CASE_S.doctor],
  [{ call: 'preview' }, WORST_CASE_S.preview],
  [{ call: 'runRead' }, WORST_CASE_S.runRead],
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
    ['statusSeconds', WORST_CASE_S.status], ['versionSeconds', WORST_CASE_S.version], ['doctorSeconds', WORST_CASE_S.doctor],
    ['previewSeconds', WORST_CASE_S.preview], ['previewPerDockerRowSeconds', DOCKER_SNAPSHOT_S], ['runReadSeconds', WORST_CASE_S.runRead],
    ['detachSeconds', WORST_CASE_S.detach], ['stopSeconds', WORST_CASE_S.stop],
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
