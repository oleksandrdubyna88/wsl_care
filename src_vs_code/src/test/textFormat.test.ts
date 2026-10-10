import assert from 'node:assert/strict';
import { test } from 'node:test';

import { gib, localMinuteOf, metricText, percent, sizeText } from '../text/format';
import { withZone } from './support/zone';

/**
 * The ONE shared format module (`src/text/format.ts`, E6.S4): sizes and times every view spells the same way. The Logs page
 * shows the times of LOCAL days, so its instants are rendered in the machine's zone — at the presentation edge only
 * (`common.utc-timestamps` rule 4) — with the offset written beside them, so a time on either side of a summer-time change
 * says which side it is on.
 */

test('an instant to the minute in the local zone, with its offset — truncated, never rounded up into the next minute', () => {
  withZone('Europe/Kyiv', () => {
    assert.equal(localMinuteOf('2026-10-01T23:59:59+00:00'), '2026-10-02 02:59 (UTC+03:00)');
    assert.equal(localMinuteOf('2026-12-01T10:00:00Z'), '2026-12-01 12:00 (UTC+02:00)', 'winter time');
  });
  withZone('Europe/Berlin', () => {
    assert.equal(localMinuteOf('2026-03-29T00:30:00Z'), '2026-03-29 01:30 (UTC+01:00)', 'before the spring change');
    assert.equal(localMinuteOf('2026-03-29T01:30:00Z'), '2026-03-29 03:30 (UTC+02:00)', 'after it: 02:30 never happened');
  });
  withZone('America/New_York', () => {
    assert.equal(localMinuteOf('2026-10-05T03:30:00Z'), '2026-10-04 23:30 (UTC-04:00)');
  });
  withZone('Asia/Kolkata', () => {
    assert.equal(localMinuteOf('2026-10-01T00:00:00Z'), '2026-10-01 05:30 (UTC+05:30)', 'a half-hour offset');
  });
});

test('a text that is not an instant is the fallback, never "NaN" or "Invalid Date"', () => {
  assert.equal(localMinuteOf('yesterday'), 'an unknown time');
  assert.equal(localMinuteOf('', '—'), '—');
});

test('a metric in its own unit: % as a percentage, memory bytes in GiB, Docker bytes in GB, starts as a count', () => {
  assert.equal(metricText('memAvailablePercent', '%', 28.5), percent(28.5));
  assert.equal(metricText('memAvailableBytes', 'bytes', 13400000000), gib(13400000000));
  assert.equal(metricText('swapUsedBytes', 'bytes', 600000000), gib(600000000));
  assert.equal(metricText('pageCacheBytes', 'bytes', 9500000000), gib(9500000000));
  assert.equal(metricText('dockerReclaimableBytes', 'bytes', 21000000000), '21.0 GB');
  assert.equal(metricText('containerStarts24h', 'starts', 950), '950 starts');
  assert.equal(metricText('somethingNew', 'widgets', 3), '3 widgets', 'a unit this build does not know: the number and the unit as answered');
});

test('sizeText (E10.S1b code round #2): a byte count in the largest decimal unit it reaches — small amounts never read 0.0 GB', () => {
  assert.equal(sizeText(0), '0 B');
  assert.equal(sizeText(200), '200 B');
  assert.equal(sizeText(1_500), '1.5 kB');
  assert.equal(sizeText(2_400_000), '2.4 MB');
  assert.equal(sizeText(59_600_000_000), '59.6 GB');
});
