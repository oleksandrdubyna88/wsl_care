import assert from 'node:assert/strict';
import { test } from 'node:test';

import { CleanupJournal, JOURNAL_KEY, type JournalEntry } from '../cleanup/journal';
import { windowOf as runsWindowOf } from '../cleanup/runMatching';
import { retainedDays, windowOf } from '../logsPage/period';
import { daemonLimitsOf, FALLBACK_LIMITS } from '../shared/daemonLimits';
import { DEFAULT_NUMBERS } from '../settings/numbers';
import { MapStore } from './support/memento';
import { withZone } from './support/zone';

/**
 * Values that MIRROR the daemon — the history it keeps and the clock skew it allows a request — are never a second, separate
 * setting that can drift (the owner, 2026-10-05): the extension reads them from the daemon's own `status` answer
 * (`status.limits`, the shape this extension expects from E7.S2c — plan §15p) and falls back to today's values when the
 * answer does not carry them, field by field.
 */

const NOW = Date.parse('2026-10-05T09:00:00Z');

test('no `limits` in the answer (every daemon before E7.S2c): today\'s values, 90 days and 5 minutes', () => {
  assert.deepEqual(FALLBACK_LIMITS, { historyRetentionDays: 90, futureSkewMs: 300_000 });
  for (const body of [undefined, {}, { limits: null }, { limits: 'x' }, { limits: [] }]) {
    assert.deepEqual(daemonLimitsOf(body), FALLBACK_LIMITS, JSON.stringify(body));
  }
});

test('the daemon\'s values when it answers them; each field that is not a whole number in range falls back on its own', () => {
  assert.deepEqual(daemonLimitsOf({ limits: { historyRetentionDays: 30, requestFutureSkewSeconds: 120 } }), { historyRetentionDays: 30, futureSkewMs: 120_000 });
  assert.deepEqual(daemonLimitsOf({ limits: { historyRetentionDays: 400 } }), { historyRetentionDays: 400, futureSkewMs: 300_000 });
  for (const bad of [0, -1, 1.5, '30', 3651, Number.NaN, null]) {
    assert.equal(daemonLimitsOf({ limits: { historyRetentionDays: bad, requestFutureSkewSeconds: 60 } }).historyRetentionDays, 90, String(bad));
  }
  for (const bad of [-1, 0.5, '60', 3601]) {
    assert.equal(daemonLimitsOf({ limits: { requestFutureSkewSeconds: bad } }).futureSkewMs, 300_000, String(bad));
  }
  assert.equal(daemonLimitsOf({ limits: { requestFutureSkewSeconds: 0 } }).futureSkewMs, 0, 'no skew allowed is a value, not a fallback');
});

test('the Logs page keeps the history the DAEMON keeps: 30 days answered → the picker and the clamp stop at 30', () => {
  withZone('Europe/Kyiv', () => {
    assert.deepEqual(retainedDays(NOW, 30), { oldest: '2026-09-06', newest: '2026-10-05' });
    assert.deepEqual(retainedDays(NOW), { oldest: '2026-07-08', newest: '2026-10-05' }, 'the fallback, 90');
    const old = windowOf({ kind: 'day', day: '2026-08-01' }, NOW, 30);
    assert.deepEqual([old.firstDay, old.clamped], ['2026-09-06', true]);
  });
});

test('the durable poll\'s runs window uses the daemon\'s retention and skew: no further back than it keeps, widened by its skew', () => {
  const entry: JournalEntry = { id: 'x', kind: 'unresolved', op: 'clean', distro: 'Ubuntu', actions: ['A4'], since: '2026-01-01T00:00:00.000Z' };
  assert.deepEqual(runsWindowOf(entry, NOW, { historyRetentionDays: 30, futureSkewMs: 60_000 }), { from: '2026-09-05T09:00:00Z', to: '2026-10-05T09:01:01Z' });
  const recent: JournalEntry = { ...entry, since: '2026-10-05T08:00:00.000Z' };
  assert.equal(runsWindowOf(recent, NOW, { historyRetentionDays: 30, futureSkewMs: 60_000 }).from, '2026-10-05T07:59:00Z');
  assert.equal(runsWindowOf(recent, NOW).from, '2026-10-05T07:55:00Z', 'the fallback skew, 5 minutes');
});

test('the journal\'s clock-skew allowance is the daemon\'s too: an entry 2 minutes ahead is kept with 5 minutes allowed, dropped with 1', async () => {
  const now = Date.parse('2026-10-05T10:00:00Z');
  const entry = { id: 'e', kind: 'unresolved', op: 'clean', distro: 'Ubuntu', actions: ['A4'], since: '2026-10-05T10:02:00.000Z' };
  for (const [skewMs, kept] of [[300_000, 1], [60_000, 0]] as const) {
    const store = new MapStore();
    await store.update(JOURNAL_KEY, { entries: [entry], removed: [] });
    assert.equal(new CleanupJournal(store, () => now, () => DEFAULT_NUMBERS, () => skewMs).entries().length, kept, String(skewMs));
  }
});
