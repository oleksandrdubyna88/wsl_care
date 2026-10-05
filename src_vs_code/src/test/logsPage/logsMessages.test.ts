import assert from 'node:assert/strict';
import { test } from 'node:test';

import { LOGS_MESSAGE_TYPES, MAX_RUN_INDEX, parseLogsMessage } from '../../logsPage/logsMessages';

/**
 * The CLOSED set of messages the Logs page may send the host (plan §15j M8, the panel's m10 rule extended): exact keys,
 * exact value shapes, nothing else — a period is NAMED (or given as real calendar `yyyy-MM-dd` parts), a run is an INDEX
 * into the list the host read itself, and a run id never comes from the page at all.
 */

test('the known instances parse to themselves — the companions that keep every refusal below from being vacuous', () => {
  const good: readonly unknown[] = [
    { type: 'ready' },
    { type: 'refresh' },
    { type: 'today' },
    { type: 'yesterday' },
    { type: 'thisRun' },
    { type: 'day', day: '2026-10-01' },
    { type: 'range', from: '2026-09-30', to: '2026-10-02' },
    { type: 'range', from: '2026-10-02', to: '2026-09-30' },
    { type: 'expand', index: 0 },
    { type: 'collapse', index: MAX_RUN_INDEX },
    { type: 'rendered', blocks: 4 },
  ];
  for (const message of good) {
    assert.deepEqual(parseLogsMessage(message), message, JSON.stringify(message));
  }
  assert.deepEqual([...LOGS_MESSAGE_TYPES].sort(), ['collapse', 'day', 'expand', 'range', 'ready', 'refresh', 'rendered', 'thisRun', 'today', 'yesterday']);
});

test('a malicious period, run id or extra field is no message — dropped before the host does anything', () => {
  const bad: readonly unknown[] = [
    // a run id or an argv word from the page: never accepted, in any field
    { type: 'thisRun', runId: '20261005T080000Z-4242' },
    { type: 'expand', index: 0, runId: '20261005T080000Z-4242' },
    { type: 'expand', runId: '20261005T080000Z-4242' },
    { type: 'today', from: '2026-10-01T00:00:00Z' },
    { type: 'day', day: '2026-10-01', detail: true },
    // periods that are not real calendar days
    { type: 'day', day: '2026-02-30' },
    { type: 'day', day: '2026-10-01 --detail' },
    { type: 'day', day: '2026-10-01T00:00:00Z' },
    { type: 'day', day: 20261001 },
    { type: 'day', day: '' },
    { type: 'day' },
    { type: 'range', from: '2026-10-01' },
    { type: 'range', from: '2026-10-01', to: '2026-13-01' },
    { type: 'range', from: ['2026-10-01'], to: '2026-10-02' },
    // indexes that are not one of the host's list
    { type: 'expand', index: -1 },
    { type: 'expand', index: 1.5 },
    { type: 'expand', index: '0' },
    { type: 'expand', index: MAX_RUN_INDEX + 1 },
    { type: 'expand', index: Number.NaN },
    { type: 'rendered', blocks: -1 },
    // types outside the set, and prototype names
    { type: 'week' },
    { type: 'clean', rowIds: ['A4'] },
    { type: 'constructor' },
    { type: '__proto__' },
    { type: 'toString' },
    { type: 'hasOwnProperty', index: 0 },
    'today',
    null,
    undefined,
    [],
    [{ type: 'today' }],
  ];
  for (const message of bad) {
    assert.equal(parseLogsMessage(message), undefined, JSON.stringify(message));
  }
});
