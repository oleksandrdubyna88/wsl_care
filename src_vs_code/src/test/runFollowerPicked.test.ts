import assert from 'node:assert/strict';
import { test } from 'node:test';

import { DEFAULT_NUMBERS } from '../settings/numbers';
import { FALLBACK_LIMITS } from '../shared/daemonLimits';

import { CleanupJournal, type JournalEntry, type NewEntry } from '../cleanup/journal';
import { RunFollower, type RunResult } from '../cleanup/runFollower';
import type { ReadOutcome, VerbOutcome } from '../client/outcome';
import type { RunRead } from '../client/verbs';
import { runIdOf, type RunId } from '../root/rootIds';
import type { DistroTarget } from '../wsl/distroTarget';
import { DEFAULT_TARGET } from '../wsl/distroTarget';
import { ManualTimers, MapStore } from './support/memento';
import { answered, failed, headBody } from './support/outcomes';
import { goldenFile } from './support/paths';

/**
 * E18.W1a-3 (todo/PLAN_disk_health_E18.md §3.5, RED 19): the durable poll of a run started for a PICKED distribution. A journal entry
 * carries `targetKind: 'picked'` beside its `distro`; after a reload the follower builds the entry's `DistroTarget` from the two
 * fields and asks `status` — and reads `runs show` — with THAT target. An entry without `targetKind` (every one written before W1a)
 * is the default's, asked exactly as before.
 */

const T0 = Date.parse('2026-10-05T10:00:00.000Z');
const RUN = '20261005T100000Z-77';

function runId(text: string): RunId {
  const id = runIdOf(text);
  assert.ok(id !== undefined);
  return id;
}

function label(target: DistroTarget): string {
  return target.kind === 'picked' ? target.distro : 'default';
}

function statusFor(distro: string, block: Record<string, unknown> | undefined): VerbOutcome {
  const body = headBody('status');
  body.running = block ?? { state: 'none' };

  return answered('status', body, distro);
}

function doneFor(distro: string): ReadOutcome {
  return { kind: 'read', read: 'runsShow', distro, body: goldenFile('runs-show-done.json') };
}

class World {
  readonly timers = new ManualTimers();
  readonly journal: CleanupJournal;
  readonly clock = { now: T0 };
  readonly follower: RunFollower;
  readonly statuses: string[] = [];
  readonly reads: string[] = [];
  readonly shown: RunResult[] = [];
  /** What `status` answers per target label; absent means the default Ubuntu with nothing running. */
  answers = new Map<string, VerbOutcome>();

  constructor(readonly store = new MapStore()) {
    this.journal = new CleanupJournal(store, () => this.clock.now);
    this.follower = new RunFollower({
      journal: this.journal,
      status: (target) => { this.statuses.push(label(target)); return Promise.resolve(this.answers.get(label(target)) ?? failed('status', { kind: 'stopped', distro: label(target) })); },
      read: (request: RunRead, target) => { this.reads.push(`${label(target)} ${request.read}`); return Promise.resolve(doneFor(label(target) === 'default' ? 'Ubuntu' : label(target))); },
      show: (result) => { this.shown.push(result); },
      afterTerminal: () => Promise.resolve(),
      focused: () => true,
      running: () => undefined,
      wallNow: () => this.clock.now,
      timers: this.timers,
      fault: (error) => { throw error instanceof Error ? error : new Error(String(error)); },
      numbers: () => DEFAULT_NUMBERS,
      limits: () => FALLBACK_LIMITS,
    });
  }
}

async function settle(): Promise<void> {
  for (let i = 0; i < 30; i += 1) {
    await new Promise((resolve) => setImmediate(resolve));
  }
}

async function added(journal: CleanupJournal, entry: NewEntry): Promise<JournalEntry> {
  const result = await journal.add(entry);
  assert.ok(result !== undefined, 'the journal took it');

  return result;
}

const PICKED_RUN = { kind: 'run', op: 'clean', distro: 'Debian', targetKind: 'picked', actions: ['A4'], since: new Date(T0).toISOString(), runId: runId(RUN) } as const satisfies NewEntry;
const DEFAULT_RUN: NewEntry = { kind: 'run', op: 'clean', distro: 'Ubuntu', actions: ['A4'], since: new Date(T0).toISOString(), runId: runId(RUN) };

test('a picked entry after a reload: the follower asks status with the PICKED target, reads runs show from Debian, and resolves the entry from Debian\'s answer', async () => {
  const store = new MapStore();
  await added(new CleanupJournal(store, () => T0), PICKED_RUN);
  const w = new World(store);
  w.answers.set('default', statusFor('Ubuntu', undefined));
  w.answers.set('Debian', statusFor('Debian', undefined));
  w.follower.kick();
  assert.ok(w.timers.fire(), 'a poll was armed for the entry');
  await settle();
  assert.ok(w.statuses.includes('Debian'), `status was asked for ${JSON.stringify(w.statuses)}`);
  assert.deepEqual(w.reads, ['Debian runsShow'], 'the run record is read from the distribution that ran it');
  assert.equal(w.shown.length, 1);
  assert.equal(w.shown[0]?.entry.distro, 'Debian');
  assert.deepEqual(w.journal.entries(), [], 'its terminal answer was shown');
});

test('an entry WITHOUT targetKind (written before W1a) is the default\'s: only the default is asked, as today', async () => {
  const store = new MapStore();
  await added(new CleanupJournal(store, () => T0), DEFAULT_RUN);
  const w = new World(store);
  w.answers.set('default', statusFor('Ubuntu', undefined));
  w.follower.kick();
  assert.ok(w.timers.fire());
  await settle();
  assert.deepEqual(w.statuses, ['default']);
  assert.deepEqual(w.reads, ['default runsShow']);
  assert.equal(w.shown[0]?.entry.distro, 'Ubuntu');
});

test('a picked distribution that answers stopped is no evidence: the entry stays, nothing is read from it', async () => {
  const store = new MapStore();
  await added(new CleanupJournal(store, () => T0), PICKED_RUN);
  const w = new World(store);
  w.answers.set('default', statusFor('Ubuntu', undefined));
  w.follower.kick();
  assert.ok(w.timers.fire());
  await settle();
  assert.ok(w.statuses.includes('Debian'));
  assert.deepEqual(w.reads, []);
  assert.equal(w.journal.entries().length, 1);
  assert.equal(w.shown.length, 0);
});

test('two picked entries of one distribution and one default entry cost ONE status per distribution per poll', async () => {
  const store = new MapStore();
  const journal = new CleanupJournal(store, () => T0);
  await added(journal, PICKED_RUN);
  await added(journal, { ...PICKED_RUN, runId: runId('20261005T100000Z-78') });
  await added(journal, DEFAULT_RUN);
  const w = new World(store);
  w.answers.set('default', statusFor('Ubuntu', { state: 'queued', reason: 'q', runId: RUN, actions: ['A4'], trigger: 'manual' }));
  w.answers.set('Debian', statusFor('Debian', { state: 'queued', reason: 'q', runId: RUN, actions: ['A4'], trigger: 'manual' }));
  w.follower.kick();
  assert.ok(w.timers.fire());
  await settle();
  assert.deepEqual([...w.statuses].sort(), ['Debian', 'default']);
});


test('a picked entry alone asks ONLY the picked distribution — the default is asked only when a default entry (or the daemon) is in flight', async () => {
  const store = new MapStore();
  await added(new CleanupJournal(store, () => T0), PICKED_RUN);
  const w = new World(store);
  w.answers.set('Debian', statusFor('Debian', undefined));
  w.follower.kick();
  assert.ok(w.timers.fire());
  await settle();
  assert.deepEqual(w.statuses, ['Debian']);
  void DEFAULT_TARGET;
});
