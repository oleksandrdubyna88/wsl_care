import assert from 'node:assert/strict';
import { test } from 'node:test';

import { runReadTail } from '../../client/verbs';
import { dayOf, periodOf, readsOf, retainedDays, windowOf, type Period } from '../../logsPage/period';
import { withZone } from '../support/zone';

/**
 * The Logs page's periods (plan §7.4, §15j M7 / M8): the HOST builds every argv from a period it validated itself — real
 * calendar `yyyy-MM-dd` parts, a run id of the daemon's one spelling — and a local day reaches the daemon as the instant
 * range of its two local midnights, so a day of 23 or 25 hours is the day the person meant. Every zone is set for the
 * test's own duration (`support/zone.ts`): the code under test reads the machine's zone, as it will on the user's.
 */

const RUN = '20261005T080000Z-4242';

/** 2026-10-05 12:00 in Kyiv (UTC+03:00, summer time). */
const NOON_KYIV = Date.parse('2026-10-05T09:00:00Z');

function tails(period: Period, now: number): string[] {
  return readsOf(period, now).map((read) => runReadTail(read).join(' '));
}

test('each period → its exact argv: today, yesterday, a date and a range as the local-midnight instants; This run as runs show', () => {
  withZone('Europe/Kyiv', () => {
    assert.deepEqual(tails({ kind: 'today' }, NOON_KYIV), [
      'logs --from 2026-10-04T21:00:00Z --to 2026-10-05T21:00:00Z --json',
      'runs --from 2026-10-04T21:00:00Z --to 2026-10-05T21:00:00Z --json',
    ]);
    assert.deepEqual(tails({ kind: 'yesterday' }, NOON_KYIV), [
      'logs --from 2026-10-03T21:00:00Z --to 2026-10-04T21:00:00Z --json',
      'runs --from 2026-10-03T21:00:00Z --to 2026-10-04T21:00:00Z --json',
    ]);
    assert.deepEqual(tails({ kind: 'day', day: '2026-10-01' }, NOON_KYIV), [
      'logs --from 2026-09-30T21:00:00Z --to 2026-10-01T21:00:00Z --json',
      'runs --from 2026-09-30T21:00:00Z --to 2026-10-01T21:00:00Z --json',
    ]);
    assert.deepEqual(tails({ kind: 'range', from: '2026-09-30', to: '2026-10-02' }, NOON_KYIV), [
      'logs --from 2026-09-29T21:00:00Z --to 2026-10-02T21:00:00Z --json',
      'runs --from 2026-09-29T21:00:00Z --to 2026-10-02T21:00:00Z --json',
    ]);
    assert.deepEqual(tails({ kind: 'thisRun', runId: RUN }, NOON_KYIV), [`runs show ${RUN} --json`]);
  });
});

test('"today" is the LOCAL day: just after local midnight it is already the new day, though UTC is still on the old one', () => {
  withZone('Europe/Kyiv', () => {
    // 2026-10-05 00:30 in Kyiv is 2026-10-04 21:30 UTC.
    assert.deepEqual(tails({ kind: 'today' }, Date.parse('2026-10-04T21:30:00Z'))[0], 'logs --from 2026-10-04T21:00:00Z --to 2026-10-05T21:00:00Z --json');
  });
  withZone('America/New_York', () => {
    // 2026-10-04 23:30 in New York (UTC-04:00) is 2026-10-05 03:30 UTC — still the 4th there.
    assert.deepEqual(tails({ kind: 'today' }, Date.parse('2026-10-05T03:30:00Z'))[0], 'logs --from 2026-10-04T04:00:00Z --to 2026-10-05T04:00:00Z --json');
  });
});

test('DST: the spring day is 23 hours, the autumn day 25 — each bounded by its own two midnights (Europe/Berlin)', () => {
  withZone('Europe/Berlin', () => {
    const spring = windowOf({ kind: 'day', day: '2026-03-29' }, Date.parse('2026-04-01T10:00:00Z'));
    assert.deepEqual([spring.from, spring.to], ['2026-03-28T23:00:00Z', '2026-03-29T22:00:00Z']);
    assert.equal(Date.parse(spring.to) - Date.parse(spring.from), 23 * 3_600_000);
    const autumn = windowOf({ kind: 'day', day: '2026-10-25' }, Date.parse('2026-10-26T10:00:00Z'));
    assert.deepEqual([autumn.from, autumn.to], ['2026-10-24T22:00:00Z', '2026-10-25T23:00:00Z']);
    assert.equal(Date.parse(autumn.to) - Date.parse(autumn.from), 25 * 3_600_000);
  });
});

test('DST: a day whose midnight does not exist starts at its first instant (America/Santiago, 2026-09-06 begins at 01:00)', () => {
  withZone('America/Santiago', () => {
    const day = windowOf({ kind: 'day', day: '2026-09-06' }, Date.parse('2026-09-10T12:00:00Z'));
    assert.deepEqual([day.from, day.to], ['2026-09-06T04:00:00Z', '2026-09-07T03:00:00Z']);
  });
});

test('far zones: UTC itself, and UTC+14 where the local day starts on the UTC day before', () => {
  withZone('UTC', () => {
    assert.deepEqual(tails({ kind: 'day', day: '2026-10-01' }, NOON_KYIV)[0], 'logs --from 2026-10-01T00:00:00Z --to 2026-10-02T00:00:00Z --json');
  });
  withZone('Pacific/Kiritimati', () => {
    assert.deepEqual(tails({ kind: 'day', day: '2026-10-01' }, NOON_KYIV)[0], 'logs --from 2026-09-30T10:00:00Z --to 2026-10-01T10:00:00Z --json');
  });
});

test('a real calendar day, and nothing else: no 30 February, no month 13, no single digits, no instant, no smuggled word', () => {
  assert.deepEqual(dayOf('2024-02-29'), { year: 2024, month: 2, day: 29 });
  assert.deepEqual(dayOf('2026-12-31'), { year: 2026, month: 12, day: 31 });
  const never: readonly unknown[] = ['2026-02-29', '2026-02-30', '2026-04-31', '2026-13-01', '2026-00-10', '2026-10-00', '2026-1-01', ' 2026-01-01', '2026-01-01 ', '2026-01-01T00:00:00Z', '2026-01-01 --detail', '20260101', 20260101, null, undefined, ['2026-01-01'], { day: '2026-01-01' }, '２０２６-01-01'];
  for (const value of never) {
    assert.equal(dayOf(value), undefined, JSON.stringify(value));
  }
});

test('the retention clamp: the 90 days the daemon keeps, today included — an older day is the oldest kept, a later one today', () => {
  withZone('Europe/Kyiv', () => {
    assert.deepEqual(retainedDays(NOON_KYIV), { oldest: '2026-07-08', newest: '2026-10-05' });
    const old = windowOf({ kind: 'day', day: '2026-01-01' }, NOON_KYIV);
    assert.deepEqual([old.firstDay, old.lastDay, old.clamped], ['2026-07-08', '2026-07-08', true]);
    const future = windowOf({ kind: 'range', from: '2026-10-04', to: '2027-01-01' }, NOON_KYIV);
    assert.deepEqual([future.firstDay, future.lastDay, future.clamped, future.to], ['2026-10-04', '2026-10-05', true, '2026-10-05T21:00:00Z']);
    const inside = windowOf({ kind: 'range', from: '2026-07-08', to: '2026-10-05' }, NOON_KYIV);
    assert.deepEqual([inside.firstDay, inside.lastDay, inside.clamped], ['2026-07-08', '2026-10-05', false]);
  });
});

test('a period read back (from the memento or a message) is the closed shape exactly — anything else is no period', () => {
  const good: readonly Period[] = [{ kind: 'today' }, { kind: 'yesterday' }, { kind: 'day', day: '2026-10-01' }, { kind: 'range', from: '2026-10-01', to: '2026-10-01' }, { kind: 'thisRun', runId: RUN }];
  for (const period of good) {
    assert.deepEqual(periodOf(JSON.parse(JSON.stringify(period))), period);
  }
  const bad: readonly unknown[] = [
    { kind: 'today', extra: 1 },
    { kind: 'day', day: '2026-02-30' },
    { kind: 'day' },
    { kind: 'range', from: '2026-10-02', to: '2026-10-01' },
    { kind: 'range', from: '2026-10-01' },
    { kind: 'thisRun', runId: '20261005T080000Z-04242' },
    { kind: 'thisRun', runId: `${RUN} --json` },
    { kind: 'thisRun' },
    { kind: 'week' },
    { kind: 'constructor' },
    { kind: '__proto__' },
    'today',
    null,
    [],
  ];
  for (const value of bad) {
    assert.equal(periodOf(value), undefined, JSON.stringify(value));
  }
});
