import assert from 'node:assert/strict';
import { test } from 'node:test';

import type { VerbOutcome } from '../client/outcome';
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
  const throwing = new Poller({ run: () => Promise.reject(new Error('boom')), store: w.store, focused: () => true, refreshSeconds: () => 30, timers: w.clock });
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
