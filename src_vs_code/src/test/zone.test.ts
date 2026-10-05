import assert from 'node:assert/strict';
import { test } from 'node:test';

import { localMinuteOf } from '../text/format';
import { withZone } from './support/zone';

/**
 * The zone helper the local-day tests lean on (review K1) — in a file of its OWN, so it starts in the machine's zone: Node
 * caches the zone, and deleting `TZ` does not reset that cache (measured 2026-10-05: after `delete process.env.TZ` the
 * clock still read the deleted zone), so a helper that only deletes it leaks its zone into every later test of the file.
 */

test('review K1: withZone holds the zone across an ASYNC body\'s awaits and restores it once the promise settles — resolved or rejected', async () => {
  const before = process.env.TZ;
  const zone = Intl.DateTimeFormat().resolvedOptions().timeZone === 'Asia/Kolkata' ? 'Asia/Tokyo' : 'Asia/Kolkata';
  const outside = localMinuteOf('2026-10-01T00:00:00Z');
  const inside = await withZone(zone, async () => {
    await new Promise((resolve) => { setImmediate(resolve); });
    return localMinuteOf('2026-10-01T00:00:00Z');
  });
  assert.equal(inside, zone === 'Asia/Kolkata' ? '2026-10-01 05:30 (UTC+05:30)' : '2026-10-01 09:00 (UTC+09:00)', 'still the zone after an await');
  assert.equal(process.env.TZ, before, 'restored after it resolved');
  assert.equal(localMinuteOf('2026-10-01T00:00:00Z'), outside, 'and the clock reads the machine\'s zone again — not a cached one');
  await assert.rejects(withZone(zone, async () => { await Promise.resolve(); throw new Error('boom'); }), /boom/);
  assert.equal(process.env.TZ, before, 'restored after it rejected');
  assert.equal(localMinuteOf('2026-10-01T00:00:00Z'), outside);
});
