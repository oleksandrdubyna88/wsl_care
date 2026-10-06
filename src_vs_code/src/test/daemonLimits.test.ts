import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

import { CleanupJournal, JOURNAL_KEY, type JournalEntry } from '../cleanup/journal';
import { windowOf as runsWindowOf } from '../cleanup/runMatching';
import { retainedDays, windowOf } from '../logsPage/period';
import { daemonLimitsOf, FALLBACK_LIMITS, LIMIT_FIELDS, type DaemonLimits } from '../shared/daemonLimits';
import { REPOSITORY_ROOT } from './support/paths';
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

interface ContractField {
  readonly name: string;
  readonly unit: string;
  readonly min: number;
  readonly max: number;
  readonly default: number;
}

/** The daemon's generated contract of `status.limits` (daemon #17, `StatusLimits.Fields`) — the field names come from HERE. */
function contract(): readonly ContractField[] {
  const file = JSON.parse(fs.readFileSync(path.join(REPOSITORY_ROOT, 'contracts', 'status-limits.json'), 'utf8')) as { object: string; fields: ContractField[] };
  assert.equal(file.object, 'limits');
  return file.fields;
}

test('the reader is held EQUAL to contracts/status-limits.json: every field the daemon publishes, its range and its default', () => {
  const fields = contract();
  assert.deepEqual(fields.map((f) => f.name), ['historyRetentionDays', 'requestFutureSkewSeconds', 'requestGraceSeconds', 'maxShownNames', 'unitStopSeconds', 'drainGraceMilliseconds'], 'the known instances');
  assert.deepEqual(LIMIT_FIELDS.map((f) => ({ name: f.name, min: f.min, max: f.max, default: f.default })), fields.map((f) => ({ name: f.name, min: f.min, max: f.max, default: f.default })));
});

test('no `limits` in the answer (every daemon before E7.S2c): every field its contract default', () => {
  const defaults = Object.fromEntries(contract().map((f) => [f.name, f.default]));
  assert.deepEqual(FALLBACK_LIMITS, {
    historyRetentionDays: defaults.historyRetentionDays,
    futureSkewMs: (defaults.requestFutureSkewSeconds as number) * 1000,
    requestGraceMs: (defaults.requestGraceSeconds as number) * 1000,
    maxShownNames: defaults.maxShownNames,
    unitStopSeconds: defaults.unitStopSeconds,
    drainGraceMs: defaults.drainGraceMilliseconds,
  });
  for (const body of [undefined, {}, { limits: null }, { limits: 'x' }, { limits: [] }]) {
    assert.deepEqual(daemonLimitsOf(body), FALLBACK_LIMITS, JSON.stringify(body));
  }
});

test('each field is the daemon\'s when it is a whole number inside the contract\'s range — its min and max accepted, anything else that field\'s default', () => {
  for (const field of contract()) {
    const read = (value: unknown): DaemonLimits => daemonLimitsOf({ limits: { [field.name]: value } });
    const fallback = daemonLimitsOf({});
    for (const good of [field.min, field.max]) {
      assert.notDeepEqual(good === field.default ? undefined : read(good), fallback, `${field.name} ${good}`);
    }
    for (const bad of [field.min - 1, field.max + 1, field.min + 0.5, String(field.min), null, Number.NaN]) {
      assert.deepEqual(read(bad), fallback, `${field.name} ${String(bad)}`);
    }
  }
  assert.deepEqual(daemonLimitsOf({ limits: { historyRetentionDays: 30, requestFutureSkewSeconds: 120, unitStopSeconds: 600, drainGraceMilliseconds: 10_000, requestGraceSeconds: 300, maxShownNames: 50 } }),
    { historyRetentionDays: 30, futureSkewMs: 120_000, unitStopSeconds: 600, drainGraceMs: 10_000, requestGraceMs: 300_000, maxShownNames: 50 });
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
  assert.deepEqual(runsWindowOf(entry, NOW, { ...FALLBACK_LIMITS, historyRetentionDays: 30, futureSkewMs: 60_000 }), { from: '2026-09-05T09:00:00Z', to: '2026-10-05T09:01:01Z' });
  const recent: JournalEntry = { ...entry, since: '2026-10-05T08:00:00.000Z' };
  assert.equal(runsWindowOf(recent, NOW, { ...FALLBACK_LIMITS, historyRetentionDays: 30, futureSkewMs: 60_000 }).from, '2026-10-05T07:59:00Z');
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
