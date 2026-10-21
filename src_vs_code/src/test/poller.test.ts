import assert from 'node:assert/strict';
import { test } from 'node:test';

import type { VerbOutcome } from '../client/outcome';
import { WINDOWS_SIDE } from '../client/windowsStatus';
import type { Verb } from '../client/verbs';
import type { RunOptions } from '../client/WslCareClient';
import { DEFAULT_REFRESH_SECONDS, effectiveSeconds, MIN_REFRESH_SECONDS, Poller, type Timers } from '../poll/poller';
import { OutcomeStore } from '../state/outcomeStore';
import { answered, failed, headBody } from './support/outcomes';

/**
 * The polling policy (plan §15f #8, §15g M1, m3), driven by a MANUAL clock: only the focused window polls, once on
 * focus and then every `wslCare.refreshSeconds` (default 120, never below 30); only `status` is polled; `preview` and
 * `doctor` only when the panel opens or Refresh is pressed; a stopped distribution leaves them unasked. The run-log
 * churn the owner decides on (M1) is MEASURED here by running the real policy over a simulated day.
 */

/** A clock the test advances by hand; `every` registers a repeating timer like `setInterval`. */
class ManualClock implements Timers {
  now = 0;
  readonly nowMs = (): number => this.now;
  private timers: { at: number; ms: number; run: () => void; live: boolean }[] = [];

  every(ms: number, run: () => void): () => void {
    const timer = { at: this.now + ms, ms, run, live: true };
    this.timers.push(timer);
    return () => { timer.live = false; };
  }

  armed(): number[] {
    return this.timers.filter((t) => t.live).map((t) => t.ms);
  }

  /** Move time forward, firing every due timer in order. */
  advance(ms: number): void {
    const end = this.now + ms;
    for (;;) {
      const due = this.timers.filter((t) => t.live && t.at <= end).sort((a, b) => a.at - b.at)[0];
      if (due === undefined) {
        break;
      }
      this.now = due.at;
      due.at += due.ms;
      due.run();
    }
    this.now = end;
  }
}

interface World {
  readonly poller: Poller;
  readonly store: OutcomeStore;
  readonly clock: ManualClock;
  readonly calls: { verb: Verb; options: RunOptions }[];
  focused: boolean;
  seconds: unknown;
}

function world(answer: (verb: Verb) => VerbOutcome = okAnswer): World {
  const clock = new ManualClock();
  const store = new OutcomeStore();
  const calls: { verb: Verb; options: RunOptions }[] = [];
  const w: { focused: boolean; seconds: unknown } = { focused: true, seconds: undefined };
  const poller = new Poller({
    run: (verb, options = {}) => { calls.push({ verb, options }); return Promise.resolve(answer(verb)); },
    store,
    focused: () => w.focused,
    refreshSeconds: () => w.seconds,
    timers: clock,
    target: () => '',
  });
  return Object.assign(w, { poller, store, clock, calls });
}

function okAnswer(verb: Verb): VerbOutcome {
  return verb === 'version' ? failed(verb, { kind: 'interrupted' }) : answered(verb, headBody(verb));
}

function verbs(w: World): Verb[] {
  return w.calls.map((c) => c.verb);
}

test('the focused window asks status once at start and arms the default interval', async () => {
  const w = world();
  w.poller.start();
  await w.poller.settled();
  assert.deepEqual(verbs(w), ['status']);
  assert.deepEqual(w.clock.armed(), [DEFAULT_REFRESH_SECONDS * 1000]);
  assert.equal(w.store.snapshot().status?.kind, 'answered');
});

test('an unfocused window asks nothing and arms nothing', async () => {
  const w = world();
  w.focused = false;
  w.poller.start();
  w.clock.advance(3_600_000);
  await w.poller.settled();
  assert.deepEqual(w.calls, []);
  assert.deepEqual(w.clock.armed(), []);
});

test('while focused, ONLY status is asked, once per interval', async () => {
  const w = world();
  w.poller.start();
  w.clock.advance(DEFAULT_REFRESH_SECONDS * 1000 * 3);
  await w.poller.settled();
  assert.deepEqual(verbs(w), ['status', 'status', 'status', 'status']);
  assert.ok(w.calls.every((c) => c.options.startIfStopped !== true), 'a poll never starts the VM');
});

test('losing focus stops the interval; regaining it asks once at once and re-arms', async () => {
  const w = world();
  w.poller.start();
  await w.poller.settled();
  w.focused = false;
  w.poller.focusChanged(false);
  assert.deepEqual(w.clock.armed(), []);
  w.clock.advance(3_600_000);
  await w.poller.settled();
  assert.deepEqual(verbs(w), ['status']);
  w.focused = true;
  w.poller.focusChanged(true);
  await w.poller.settled();
  assert.deepEqual(verbs(w), ['status', 'status']);
  assert.deepEqual(w.clock.armed(), [DEFAULT_REFRESH_SECONDS * 1000]);
});

test('a timer that fires after focus was lost (the race) asks nothing', async () => {
  const w = world();
  w.poller.start();
  await w.poller.settled();
  w.focused = false;
  await w.poller.tick();
  assert.deepEqual(verbs(w), ['status']);
});

test('the interval setting: default 120, never below 30, whole seconds; a changed value re-arms', async () => {
  assert.equal(effectiveSeconds(undefined), DEFAULT_REFRESH_SECONDS);
  assert.equal(effectiveSeconds('60'), DEFAULT_REFRESH_SECONDS);
  assert.equal(effectiveSeconds(Number.NaN), DEFAULT_REFRESH_SECONDS);
  assert.equal(effectiveSeconds(10), MIN_REFRESH_SECONDS);
  assert.equal(effectiveSeconds(45), 45);
  assert.equal(effectiveSeconds(45.7), 45);
  const w = world();
  w.poller.start();
  w.seconds = 45;
  w.poller.settingsChanged();
  assert.deepEqual(w.clock.armed(), [45_000]);
});

test('a huge interval is clamped to one day — above 2^31-1 ms setInterval overflows and fires every millisecond', () => {
  const overflowing = Math.floor((2 ** 31) / 1000) + 1;
  assert.equal(effectiveSeconds(overflowing), 86_400);
  assert.equal(effectiveSeconds(1e12), 86_400);
  assert.equal(effectiveSeconds(86_400), 86_400);
  assert.equal(effectiveSeconds(Number.POSITIVE_INFINITY), DEFAULT_REFRESH_SECONDS, 'not a finite number: the default');
  const w = world();
  w.seconds = overflowing;
  w.poller.start();
  assert.deepEqual(w.clock.armed(), [86_400_000]);
  assert.ok((w.clock.armed()[0] ?? 0) <= 2 ** 31 - 1, 'what setInterval is handed stays within its 32-bit delay');
});

test('opening the panel asks status, then preview and doctor — and puts all three in the store', async () => {
  const w = world();
  await w.poller.refreshPanel();
  assert.deepEqual(verbs(w).sort(), ['doctor', 'preview', 'status']);
  assert.equal(w.calls[0]?.verb, 'status', 'status first: it decides whether the others are asked');
  const snapshot = w.store.snapshot();
  assert.equal(snapshot.preview?.kind, 'answered');
  assert.equal(snapshot.doctor?.kind, 'answered');
  assert.equal(snapshot.checking, false);
});

test('a stopped distribution: the panel asks status only — preview and doctor are not asked, and show the same stop', async () => {
  const w = world((verb) => failed(verb, { kind: 'stopped', distro: 'Ubuntu' }));
  await w.poller.refreshPanel();
  assert.deepEqual(verbs(w), ['status']);
  assert.deepEqual(w.store.snapshot().preview, { kind: 'stopped', distro: 'Ubuntu', verb: 'preview' });
  assert.deepEqual(w.store.snapshot().doctor, { kind: 'stopped', distro: 'Ubuntu', verb: 'doctor' });
});

test('"Start WSL and check" passes startIfStopped to status ONLY', async () => {
  const w = world();
  await w.poller.refreshPanel({ startIfStopped: true });
  assert.deepEqual(w.calls.filter((c) => c.options.startIfStopped === true).map((c) => c.verb), ['status']);
});

test('a status refused for its schema does not blank preview and doctor (refusal is per verb)', async () => {
  const w = world((verb) => (verb === 'status' ? failed(verb, { kind: 'needsNewerExtension', schemaVersion: 2 }) : okAnswer(verb)));
  await w.poller.refreshPanel();
  assert.deepEqual(verbs(w).sort(), ['doctor', 'preview', 'status']);
});

test('a run that throws is recorded as an unknown failure and polling goes on', async () => {
  const w = world();
  const throwing = new Poller({ run: () => Promise.reject(new Error('boom')), store: w.store, focused: () => true, refreshSeconds: () => 30, timers: w.clock, target: () => '' });
  throwing.start();
  await throwing.settled();
  assert.equal(w.store.snapshot().status?.kind, 'unknownFailure');
  assert.deepEqual(w.clock.armed(), [30_000]);
});

test('dispose disarms the interval', async () => {
  const w = world();
  w.poller.start();
  w.poller.dispose();
  assert.deepEqual(w.clock.armed(), []);
});

/**
 * M1, measured: the number of `status` runs — each one run-log file in `$XDG_STATE_HOME/wsl-care/logs/` (every verb but
 * `--help` / `--version` opens one) — that the real policy makes over a simulated day. Recorded in
 * research/2026-10-04_extension_poll_churn.md for the owner's decision.
 */
function statusRunsOver(hoursFocused: number, seconds: number): number {
  const w = world();
  w.seconds = seconds;
  w.poller.start();
  w.clock.advance(hoursFocused * 3_600_000);
  w.focused = false;
  w.poller.focusChanged(false);
  w.clock.advance((24 - hoursFocused) * 3_600_000);

  return w.calls.filter((c) => c.verb === 'status').length;
}

test('M1 measured: status runs per window-day at the default 120 s and at the 30 s floor', () => {
  assert.equal(statusRunsOver(24, 120), 721, 'a window focused all day: 720 interval polls + the one at focus');
  assert.equal(statusRunsOver(8, 120), 241, 'an 8-hour focused working day');
  assert.equal(statusRunsOver(24, 30), 2881, 'the floor, focused all day');
  assert.equal(statusRunsOver(0, 120), 1, 'focused for an instant: the one refresh on focus');
});

// ---- E19.S2b (todo/PLAN_disk_hygiene_windows_E19.md decision 12): the Windows `status` slot, read on the same tick ----

const WINDOWS_DISTRO = WINDOWS_SIDE;

/** The Windows reading as the bundled client hands it: the distro's golden with the side named, the way the fake answers it. */
function windowsAnswer(): VerbOutcome {
  return answered('status', { ...headBody('status'), side: 'windows' }, WINDOWS_DISTRO);
}

interface WindowsWorld extends World {
  /** Every ask in the order it was made: `status` (the distro's) and `windowsStatus`. */
  readonly order: string[];
  usable: boolean;
}

function windowsWorld(distro: (verb: Verb) => VerbOutcome = okAnswer, windows: () => Promise<VerbOutcome> = () => Promise.resolve(windowsAnswer())): WindowsWorld {
  const clock = new ManualClock();
  const store = new OutcomeStore();
  const calls: { verb: Verb; options: RunOptions }[] = [];
  const order: string[] = [];
  const w: { focused: boolean; seconds: unknown; usable: boolean } = { focused: true, seconds: undefined, usable: true };
  const poller = new Poller({
    run: (verb, options = {}) => { calls.push({ verb, options }); order.push(verb); return Promise.resolve(distro(verb)); },
    store,
    focused: () => w.focused,
    refreshSeconds: () => w.seconds,
    timers: clock,
    target: () => '',
    windows: { usable: () => w.usable, status: () => { order.push('windowsStatus'); return windows(); } },
  });
  return Object.assign(w, { poller, store, clock, calls, order });
}

/**
 * Fires `count` interval ticks `everyMs` apart, letting each read finish before the next tick — as real timers do: the Windows read
 * is shared while it runs, so ticks fired back to back in one synchronous step would join one read.
 */
async function advanceEach(w: World, everyMs: number, count: number): Promise<void> {
  for (let i = 0; i < count; i += 1) {
    w.clock.advance(everyMs);
    await w.poller.settled();
  }
}

test('the windows slot is read on the same tick and only on windows', async () => {
  const w = windowsWorld();
  w.poller.start();
  await w.poller.settled();
  assert.deepEqual(w.order, ['status', 'windowsStatus'], 'one tick asks both, the distro\'s first');
  assert.equal(w.store.snapshot().status?.kind, 'answered');
  assert.equal(w.store.snapshot().windowsStatus?.kind, 'answered');
  await advanceEach(w, DEFAULT_REFRESH_SECONDS * 1000, 2);
  assert.deepEqual(w.order, ['status', 'windowsStatus', 'status', 'windowsStatus', 'status', 'windowsStatus'], 'every interval tick asks the pair');

  const notWindows = windowsWorld();
  notWindows.usable = false;
  notWindows.poller.start();
  notWindows.clock.advance(DEFAULT_REFRESH_SECONDS * 1000);
  await notWindows.poller.settled();
  assert.deepEqual(notWindows.order, ['status', 'status'], 'a host that cannot run the bundled exe asks the distro alone');
  assert.equal(notWindows.store.snapshot().windowsStatus, undefined, 'and the slot stays empty');

  const unfocused = windowsWorld();
  unfocused.focused = false;
  await unfocused.poller.tick();
  assert.deepEqual(unfocused.order, [], 'an unfocused window reads neither');
});

test('a windows status failure leaves the distro outcome untouched', async () => {
  const failure = failed('status', { kind: 'timedOut', timeoutMs: 12_000 });
  for (const windows of [() => Promise.resolve(failure), () => Promise.reject(new Error('the bundled client threw'))]) {
    const w = windowsWorld(okAnswer, windows);
    w.poller.start();
    await w.poller.settled();
    assert.equal(w.store.snapshot().status?.kind, 'answered', 'the distro\'s answer stands');
    assert.equal(w.store.snapshot().windowsStatus?.kind === 'answered', false, 'the Windows slot holds the failure');
    assert.equal(w.store.snapshot().windowsStatus?.verb, 'status');
  }
});

test('wsl stopped still reads the windows status', async () => {
  const w = windowsWorld((verb) => failed(verb, { kind: 'stopped', distro: 'Ubuntu' }));
  w.poller.start();
  await w.poller.settled();
  assert.equal(w.store.snapshot().status?.kind, 'stopped');
  assert.equal(w.store.snapshot().windowsStatus?.kind, 'answered', 'the Windows reading shows although the distribution is stopped');
  assert.deepEqual(w.order, ['status', 'windowsStatus']);
});

test('a panel refresh reads the windows status beside the distro\'s; the cleanup follower\'s status question does not', async () => {
  const w = windowsWorld();
  await w.poller.refreshPanel();
  await w.poller.settled();
  assert.deepEqual(w.order.filter((a) => a !== 'preview' && a !== 'doctor'), ['status', 'windowsStatus']);
  assert.equal(w.store.snapshot().windowsStatus?.kind, 'answered');
  const follower = windowsWorld();
  await follower.poller.askStatus();
  assert.deepEqual(follower.order, ['status'], 'a follower asks every few seconds; the Windows read is the poll\'s');
});

test('the windows reading is not about the distribution: a changed target keeps it while the distro\'s outcomes go', async () => {
  const store = new OutcomeStore();
  store.set('status', okAnswer('status'));
  store.setWindowsStatus(windowsAnswer());
  store.clear();
  assert.equal(store.snapshot().status, undefined);
  assert.equal(store.snapshot().windowsStatus?.kind, 'answered');
});

test('the windows slot is its own: a distro status never lands in it, and a windows read never lands in the distro\'s', () => {
  const store = new OutcomeStore();
  const seen: unknown[] = [];
  store.onChange((snapshot) => seen.push(snapshot.windowsStatus));
  store.set('status', okAnswer('status'));
  assert.equal(store.snapshot().windowsStatus, undefined);
  store.setWindowsStatus(windowsAnswer());
  const [distro, windows] = [store.snapshot().status, store.snapshot().windowsStatus];
  assert.equal(distro?.kind === 'answered' && distro.distro, 'Ubuntu');
  assert.equal(windows?.kind === 'answered' && windows.distro, WINDOWS_DISTRO);
  assert.equal(seen.length, 2, 'listeners hear the Windows slot change too');
});

// ---- E19.S2b review: the Windows read is shared while it runs, comes at once on focus, and is counted in the churn ----

/** A Windows read the test finishes by hand — the exe that is still running. */
function heldWindows(): { windows: () => Promise<VerbOutcome>; starts: () => number; finish: (outcome: VerbOutcome) => void } {
  let starts = 0;
  let release: (outcome: VerbOutcome) => void = () => undefined;
  const held = new Promise<VerbOutcome>((resolve) => { release = resolve; });

  return { windows: () => { starts += 1; return held; }, starts: () => starts, finish: (outcome) => release(outcome) };
}

test('a second tick while the windows read runs shares it — one start, one store write', async () => {
  const held = heldWindows();
  const w = windowsWorld(okAnswer, held.windows);
  const writes: unknown[] = [];
  w.store.onChange((snapshot) => { if (snapshot.windowsStatus !== undefined && writes.at(-1) !== snapshot.windowsStatus) { writes.push(snapshot.windowsStatus); } });
  w.poller.start();
  await Promise.resolve();
  w.clock.advance(DEFAULT_REFRESH_SECONDS * 1000);
  void w.poller.refreshPanel();
  await Promise.resolve();
  assert.equal(held.starts(), 1, 'a tick, an interval tick and a Refresh during the running read join it');
  held.finish(windowsAnswer());
  await w.poller.settled();
  assert.equal(held.starts(), 1);
  assert.equal(writes.length, 1, 'ONE store write — an older answer can never land after a newer one');
  w.clock.advance(DEFAULT_REFRESH_SECONDS * 1000);
  await w.poller.settled();
  assert.equal(held.starts(), 2, 'once it has ended, the next tick starts a read of its own');
});

test('focus regained reads the windows status at once, without waiting for the interval', async () => {
  const w = windowsWorld();
  w.poller.start();
  await w.poller.settled();
  w.focused = false;
  w.poller.focusChanged(false);
  w.clock.advance(3_600_000);
  await w.poller.settled();
  assert.equal(w.order.filter((a) => a === 'windowsStatus').length, 1, 'an unfocused hour reads nothing');
  const at = w.clock.now;
  w.focused = true;
  w.poller.focusChanged(true);
  await w.poller.settled();
  assert.deepEqual(w.order, ['status', 'windowsStatus', 'status', 'windowsStatus'], 'the pair again, before any interval has passed');
  assert.equal(w.clock.now, at, 'no time passed since focus came back');
});

/** The bundled daemon's `status` runs over a simulated day — each one a run-log file of the Windows binary, beside the distribution's. */
async function windowsStatusRunsOver(hoursFocused: number, seconds: number): Promise<number> {
  const w = windowsWorld();
  w.seconds = seconds;
  w.poller.start();
  await w.poller.settled();
  await advanceEach(w, seconds * 1000, Math.floor((hoursFocused * 3600) / seconds));
  w.focused = false;
  w.poller.focusChanged(false);
  w.clock.advance((24 - hoursFocused) * 3_600_000);
  await w.poller.settled();

  return w.order.filter((a) => a === 'windowsStatus').length;
}

test('E19.S2b churn, measured: the Windows binary adds the same number of status runs (= run-log files) per window-day as the distro\'s', async () => {
  assert.equal(await windowsStatusRunsOver(24, 120), 721, 'a window focused all day: 720 interval polls + the one at focus');
  assert.equal(await windowsStatusRunsOver(8, 120), 241);
  assert.equal(await windowsStatusRunsOver(24, 30), 721, 'at the 30 s floor the Windows read is throttled to once per 120 s: not 2 881');
  assert.equal(await windowsStatusRunsOver(0, 120), 1);
});

// ---- E19.S2b coai code round: the throttle, the panel round's wait, a throwing usable() ----

test('at the 30 s floor the windows status is read once per 120 s — a Refresh and a regained focus always read', async () => {
  const w = windowsWorld();
  w.seconds = 30;
  w.poller.start();
  await w.poller.settled();
  await advanceEach(w, 30_000, 8);
  const reads = (): number => w.order.filter((a) => a === 'windowsStatus').length;
  assert.equal(w.order.filter((a) => a === 'status').length, 9, 'the distro is read at every 30 s tick');
  assert.equal(reads(), 3, 'the Windows binary at 0, 120 and 240 s only');
  await w.poller.refreshPanel();
  assert.equal(reads(), 4, 'a panel Refresh always reads');
  w.focused = false;
  w.poller.focusChanged(false);
  w.focused = true;
  w.poller.focusChanged(true);
  await w.poller.settled();
  assert.equal(reads(), 5, 'a regained focus always reads');
});

test('a panel refresh stays "checking" until the windows read ends, yet asks preview and doctor right after the distro status', async () => {
  const held = heldWindows();
  const w = windowsWorld(okAnswer, held.windows);
  const round = w.poller.refreshPanel();
  for (let i = 0; i < 10; i += 1) {
    await Promise.resolve();
  }
  assert.deepEqual(w.calls.map((c) => c.verb), ['status', 'preview', 'doctor'], 'preview and doctor do not wait for the Windows exe');
  assert.equal(w.store.snapshot().checking, true, 'still checking while the Windows read runs');
  held.finish(windowsAnswer());
  await round;
  assert.equal(w.store.snapshot().checking, false);
  assert.equal(w.store.snapshot().windowsStatus?.kind, 'answered');
});

test('a windows.usable() that throws is caught: the tick and the refresh resolve, nothing is asked, an unknownFailure is stored', async () => {
  const w = windowsWorld();
  const throwing = new Poller({
    run: (verb) => Promise.resolve(okAnswer(verb)), store: w.store, focused: () => true, refreshSeconds: () => undefined, timers: w.clock, target: () => '',
    windows: { usable: () => { throw new Error('the probe broke'); }, status: () => { w.order.push('windowsStatus'); return Promise.resolve(windowsAnswer()); } },
  });
  await throwing.tick();
  await throwing.refreshPanel();
  assert.deepEqual(w.order, [], 'the Windows status was never asked');
  const stored = w.store.snapshot().windowsStatus;
  assert.equal(stored?.kind, 'unknownFailure', 'a defect in the probe is visible in the slot, not silent');
  assert.match(JSON.stringify(stored), /the probe broke/);
  assert.equal(w.store.snapshot().status?.kind, 'answered', 'the distro outcome is untouched');
});

// ---- E19.S2b review: the throttle on a clock with jitter (real timers fire a hair off the nominal interval) ----

/**
 * A poller whose ticks and clock the test drives by hand: `tick(at)` sets the clock to `at` (in ms) and fires the interval's callback,
 * so a tick can land a millisecond short of — or past — the nominal interval, as real `setInterval` fires and the read's own start do.
 */
function jitterWorld(seconds: number): { start: () => Promise<void>; tick: (at: number) => Promise<void>; reads: () => number } {
  let now = 0;
  let fire: () => void = () => undefined;
  let reads = 0;
  const poller = new Poller({
    run: (verb) => Promise.resolve(okAnswer(verb)),
    store: new OutcomeStore(),
    focused: () => true,
    refreshSeconds: () => seconds,
    timers: { every: (_ms, run) => { fire = run; return () => undefined; }, nowMs: () => now },
    target: () => '',
    windows: { usable: () => true, status: () => { reads += 1; return Promise.resolve(windowsAnswer()); } },
  });

  return {
    start: async () => { poller.start(); await poller.settled(); },
    tick: async (at) => { now = at; fire(); await poller.settled(); },
    reads: () => reads,
  };
}

test('at the default interval every tick reads the windows status even when the clock runs a millisecond short', async () => {
  const w = jitterWorld(120);
  await w.start();
  await w.tick(1); // the read's start stamp lands a millisecond after its timer fired…
  await w.tick(120_000); // …so the next fire is 119 999 ms after it
  await w.tick(240_001);
  await w.tick(359_999);
  assert.equal(w.reads(), 5, 'start + four ticks: none skipped at the default');
});

test('at the 30 s floor the read lands every fourth tick with jitter', async () => {
  const w = jitterWorld(30);
  await w.start();
  const jitter = [1, -1, 1, -1, 0, 1, -1, 1, -1, 0, 1, -1];
  for (let i = 0; i < jitter.length; i += 1) {
    await w.tick((i + 1) * 30_000 + (jitter[i] ?? 0));
  }
  assert.equal(w.reads(), 1 + jitter.length / 4, 'the start read and one every fourth tick (120 s), not every eighth and not every one');
});
