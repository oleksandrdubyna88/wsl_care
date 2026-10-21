import assert from 'node:assert/strict';
import { test } from 'node:test';

import { Listeners } from '../shared/listeners';
import { SingleFlight } from '../shared/singleFlight';

/**
 * The two small helpers the Windows tasks' hosts share (WR-E2, the Opus own review m2/m3 — reuse-first: ONE copy of the "one in
 * flight, one more when asked meanwhile" rule and ONE of the listener list that survives a listener's own fault).
 */

function gate(): { readonly opened: Promise<void>; readonly open: () => void } {
  let open: () => void = () => undefined;
  const opened = new Promise<void>((resolve) => { open = resolve; });

  return { opened, open };
}

test('one at a time: three requests at once run the work twice — the flight and ONE more, which the later two share', async () => {
  let runs = 0;
  const flight = new SingleFlight(() => { runs += 1; return Promise.resolve(); });
  await Promise.all([flight.request(), flight.request(), flight.request()]);
  assert.equal(runs, 2);
  assert.equal(flight.running, false);
});

test('a request made after a flight ended starts a new one; a request during a flight is answered by one run that STARTS after it', async () => {
  const first = gate();
  const starts: number[] = [];
  const ends: number[] = [];
  let n = 0;
  const flight = new SingleFlight(async () => { n += 1; starts.push(n); if (n === 1) { await first.opened; } ends.push(n); });
  const a = flight.request();
  assert.equal(flight.running, true);
  const b = flight.request();
  assert.deepEqual(starts, [1], 'the second request did not start a parallel run');
  first.open();
  await Promise.all([a, b]);
  assert.deepEqual([starts, ends], [[1, 2], [1, 2]], 'the second run started after the first ended');
  await flight.request();
  assert.equal(n, 3);
});

test('the hooks: started once when a flight begins and ended once when its last run is over — running is already true / false in them', async () => {
  const seen: string[] = [];
  const flight: SingleFlight = new SingleFlight(() => Promise.resolve(), {
    started: () => { seen.push(`started:${String(flight.running)}`); },
    ended: () => { seen.push(`ended:${String(flight.running)}`); },
  });
  await Promise.all([flight.request(), flight.request()]);
  assert.deepEqual(seen, ['started:true', 'ended:false']);
});

test('work that rejects rejects the request, and the next request still runs — a failure never wedges the flight', async () => {
  let fail = true;
  const flight = new SingleFlight(() => (fail ? Promise.reject(new Error('boom')) : Promise.resolve()));
  await assert.rejects(flight.request(), /boom/);
  assert.equal(flight.running, false);
  fail = false;
  await flight.request();
});

test('the listener list: every listener hears it; one that throws neither stops the others nor throws at the caller; unsubscribing works', () => {
  const listeners = new Listeners();
  const heard: string[] = [];
  listeners.add(() => { throw new Error('a listener\'s own fault'); });
  const off = listeners.add(() => { heard.push('b'); });
  listeners.add(() => { heard.push('c'); });
  listeners.tell();
  assert.deepEqual(heard, ['b', 'c']);
  off();
  listeners.tell();
  assert.deepEqual(heard, ['b', 'c', 'c']);
});

test('the coai code round, finding 4: a run that rejects does not drop a request queued behind it — the queued run still happens, and the last error is then surfaced', async () => {
  const first = gate();
  let n = 0;
  const flight = new SingleFlight(async () => { n += 1; if (n === 1) { await first.opened; throw new Error('run 1 failed'); } });
  const a = flight.request();
  void flight.request();
  first.open();
  await assert.rejects(a, /run 1 failed/, 'no failure is hidden');
  assert.equal(n, 2, 'the queued run was not dropped');
  assert.equal(flight.running, false);
});

test('finding 4: when the queued run fails too, the LAST error is the one surfaced', async () => {
  const first = gate();
  let n = 0;
  const flight = new SingleFlight(async () => { n += 1; if (n === 1) { await first.opened; } throw new Error(`run ${n} failed`); });
  const a = flight.request();
  void flight.request();
  first.open();
  await assert.rejects(a, /run 2 failed/);
  assert.equal(n, 2);
});