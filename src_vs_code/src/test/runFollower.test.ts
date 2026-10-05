import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

import { CleanupJournal, type JournalEntry, type NewEntry } from '../cleanup/journal';
import { FOLLOW_POLL, RunFollower, type RunResult } from '../cleanup/runFollower';
import type { ReadOutcome, VerbOutcome } from '../client/outcome';
import type { RunRead } from '../client/verbs';
import { runIdOf, type RunId } from '../root/rootIds';
import { ManualTimers, MapStore } from './support/memento';
import { answered, failed, headBody } from './support/outcomes';
import { GOLDEN_ROOT } from './support/paths';

/**
 * The durable poll (E6.S3, plan §15j M6, §15k #3 / #4, the coai E6.S2 plan round #3 contract): against a journal over a
 * `globalState`-shaped store, a scripted `status` (the head golden with the running block a test sets), scripted run reads,
 * a manual one-shot timer and a wall clock the test moves. Every read the follower makes is recorded, so "status only,
 * and ONE runs show at the terminal state" is a count, and "it stops itself" is the absence of an armed timer.
 */

const T0 = Date.parse('2026-10-05T10:00:00.000Z');
const RUN = '20261005T100000Z-77';

function runId(text: string): RunId {
  const id = runIdOf(text);
  assert.ok(id !== undefined);
  return id;
}

function golden(name: string): Record<string, unknown> {
  return JSON.parse(fs.readFileSync(path.join(GOLDEN_ROOT, 'head', name), 'utf8')) as Record<string, unknown>;
}

function running(state: string, extra: Record<string, unknown> = {}): Record<string, unknown> {
  return { state, reason: `run ${RUN} is ${state}`, runId: RUN, actions: ['A4'], current: 'A4', trigger: 'manual', ...extra };
}

function statusWith(block: Record<string, unknown> | undefined): VerbOutcome {
  const body = headBody('status');
  body.running = block ?? { state: 'none' };
  return answered('status', body);
}

function show(state: string, extra: Record<string, unknown> = {}): ReadOutcome {
  return { kind: 'read', read: 'runsShow', distro: 'Ubuntu', body: { schemaVersion: 1, runId: RUN, state, ...extra } };
}

interface World {
  readonly follower: RunFollower;
  readonly journal: CleanupJournal;
  readonly store: MapStore;
  readonly timers: ManualTimers;
  readonly reads: RunRead[];
  readonly shown: RunResult[];
  readonly faults: unknown[];
  readonly clock: { now: number };
  statusCalls: number;
  afterTerminal: number;
  status: VerbOutcome;
  answer: (request: RunRead) => ReadOutcome;
  focused: boolean;
}

function world(store = new MapStore()): World {
  const timers = new ManualTimers();
  const journal = new CleanupJournal(store, () => w.clock.now);
  const w: World = {
    journal, store, timers, reads: [], shown: [], faults: [], clock: { now: T0 }, statusCalls: 0, afterTerminal: 0,
    status: statusWith(undefined), answer: () => show('unknown'), focused: true,
    follower: undefined as unknown as RunFollower,
  };
  (w as { follower: RunFollower }).follower = new RunFollower({
    journal,
    status: () => { w.statusCalls += 1; return Promise.resolve(w.status); },
    read: (request) => { w.reads.push(request); return Promise.resolve(w.answer(request)); },
    show: (result) => { w.shown.push(result); },
    afterTerminal: () => { w.afterTerminal += 1; return Promise.resolve(); },
    focused: () => w.focused,
    wallNow: () => w.clock.now,
    timers,
    fault: (error) => { w.faults.push(error); },
  });
  return w;
}

async function addedTo(journal: CleanupJournal, entry: NewEntry): Promise<JournalEntry> {
  const added = await journal.add(entry);
  assert.ok(added !== undefined, 'the journal took it');
  return added;
}

const RUN_ENTRY: NewEntry = { kind: 'run', op: 'clean', distro: 'Ubuntu', actions: ['A4'], since: new Date(T0).toISOString(), runId: runId(RUN) };
const UNRESOLVED: NewEntry = { kind: 'unresolved', op: 'clean', distro: 'Ubuntu', actions: ['A4'], since: new Date(T0).toISOString() };

/** Fires the armed poll and waits for its tick to finish. */
async function poll(w: World): Promise<void> {
  assert.ok(w.timers.fire(), 'a poll was armed');
  await settle();
}

async function settle(): Promise<void> {
  for (let i = 0; i < 20; i += 1) {
    await new Promise((resolve) => setImmediate(resolve));
  }
}

test('M6: nothing in flight — no journal entry, status.running none — arms nothing and asks nothing', async () => {
  const w = world();
  w.follower.kick();
  await settle();
  assert.equal(w.timers.pending(), 0);
  assert.equal(w.statusCalls, 0);
});

test('a run this window follows: status every 4 s while it is in flight, then ONE runs show at the terminal state, shown, removed, and the poll stops', async () => {
  const w = world();
  const entry = await addedTo(w.journal, RUN_ENTRY);
  w.follower.started(entry.id);
  w.status = statusWith(running('live'));
  w.follower.kick();
  assert.equal(w.timers.armed[0]?.ms, FOLLOW_POLL.intervalMs);
  assert.ok(FOLLOW_POLL.intervalMs >= 3_000 && FOLLOW_POLL.intervalMs <= 5_000, 'M6: every 3–5 s');
  await poll(w);
  await poll(w);
  assert.deepEqual(w.reads, [], 'status only, while the run is in flight');
  w.answer = () => ({ kind: 'read', read: 'runsShow', distro: 'Ubuntu', body: golden('runs-show-done.json') });
  w.status = statusWith(undefined);
  await poll(w);
  assert.deepEqual(w.reads, [{ read: 'runsShow', runId: RUN }]);
  assert.equal(w.shown.length, 1);
  assert.equal(w.shown[0]?.kind, 'run');
  assert.deepEqual(new CleanupJournal(w.store).entries(), [], 'the entry left with its terminal answer');
  assert.equal(w.afterTerminal, 1, 'the panel re-read once (Docker after)');
  assert.equal(w.timers.pending(), 0, 'it stopped itself');
  assert.equal(w.statusCalls, 3);
});

test('queued, live and wedged naming the run all count as in flight; a runs show that still says running or queued keeps the poll', async () => {
  const w = world();
  w.follower.started((await addedTo(w.journal, RUN_ENTRY)).id);
  for (const state of ['queued', 'live', 'wedged']) {
    w.status = statusWith(running(state));
    w.follower.kick();
    await poll(w);
  }
  assert.deepEqual(w.reads, []);
  w.status = statusWith(running('live', { runId: '20261005T100500Z-78' }));
  w.answer = () => show('running');
  await poll(w);
  w.answer = () => show('queued');
  await poll(w);
  assert.equal(w.reads.length, 2, 'another run in flight is not this one: runs show asked, and it is not over');
  assert.equal(w.shown.length, 0);
  assert.equal(w.timers.pending(), 1, 'still polling');
});

test('§15k #4: runs show answering unknown is terminal — shown, removed, the poll stops; a dead run reads interrupted', async () => {
  for (const [answer, state] of [[show('unknown', { reason: 'never existed here' }), 'unknown'], [{ kind: 'read', read: 'runsShow', distro: 'Ubuntu', body: golden('runs-show-interrupted.json') } as ReadOutcome, 'interrupted']] as const) {
    const w = world();
    w.follower.started((await addedTo(w.journal, RUN_ENTRY)).id);
    w.status = statusWith(running('dead'));
    w.answer = () => answer;
    w.follower.kick();
    await poll(w);
    const result = w.shown[0];
    assert.ok(result?.kind === 'run', JSON.stringify(result));
    assert.deepEqual(result.show.state, { kind: 'known', value: state });
    assert.equal(w.timers.pending(), 0);
    assert.deepEqual(w.journal.entries(), []);
  }
});

test('§15k #4: the hard ceiling — past 30 minutes an entry ends as "state unknown" WITH its run id, nothing more is asked of it', async () => {
  const w = world();
  w.follower.started((await addedTo(w.journal, RUN_ENTRY)).id);
  w.status = statusWith(running('wedged'));
  w.follower.kick();
  await poll(w);
  w.clock.now = T0 + FOLLOW_POLL.ceilingMs + 1;
  await poll(w);
  assert.equal(w.shown[0]?.kind, 'ceiling');
  assert.equal(w.shown[0]?.entry.kind === 'run' ? w.shown[0].entry.runId : '', RUN);
  assert.deepEqual(w.reads, []);
  assert.deepEqual(w.journal.entries(), []);
  assert.equal(w.timers.pending(), 0);
});

test('a reload: a NEW follower over the same store resumes the run on its first kick, and a status that does not answer keeps the entry', async () => {
  const store = new MapStore();
  await addedTo(world(store).journal, RUN_ENTRY);
  const w = world(store);
  w.status = failed('status', { kind: 'timedOut', timeoutMs: 20_000 });
  w.follower.kick();
  await poll(w);
  assert.equal(w.journal.entries().length, 1, 'no answer is no evidence: kept');
  w.status = statusWith(undefined);
  w.answer = () => show('refused', { reason: 'refused: another run holds the lock', run: { runId: RUN, trigger: 'manual', outcome: 'refused', actions: [{ id: 'A4', status: 'refused' }] } });
  await poll(w);
  const result = w.shown[0];
  assert.ok(result?.kind === 'run');
  assert.equal(result.show.reason, 'refused: another run holds the lock');
});

test('focus: an entry ANOTHER window started is followed only while this window is focused; one it started, always', async () => {
  const store = new MapStore();
  const w = world(store);
  const entry = await addedTo(w.journal, RUN_ENTRY);
  w.focused = false;
  w.status = statusWith(running('live'));
  w.follower.kick();
  assert.equal(w.timers.pending(), 0, 'not this window\'s run, not focused: no poll');
  w.follower.started(entry.id);
  w.follower.kick();
  assert.equal(w.timers.pending(), 1, 'this window started it: polled unfocused');
});

test('a run in flight that is NOT the journal\'s (the timer\'s): polled while focused, never while unfocused, and it ends with the run', async () => {
  const w = world();
  w.status = statusWith(running('live', { trigger: 'timer' }));
  w.focused = false;
  w.follower.kick();
  assert.equal(w.timers.pending(), 0);
  w.focused = true;
  await w.follower.tick();
  w.follower.kick();
  assert.equal(w.timers.pending(), 1);
  w.status = statusWith(undefined);
  await poll(w);
  assert.equal(w.timers.pending(), 0, 'nothing in flight any more');
  assert.deepEqual(w.reads, [], 'not ours: no runs show');
});

// ---- the unresolved confirm (the controller's outcomeUnknown with no run id) ----

test('unresolved: a queued / live run of trigger manual holding EXACTLY the confirmed actions is adopted, then followed as any run', async () => {
  const w = world();
  w.follower.started((await addedTo(w.journal, UNRESOLVED)).id);
  w.status = statusWith(running('queued'));
  w.follower.kick();
  await poll(w);
  const entries = w.journal.entries();
  assert.equal(entries.length, 1);
  assert.ok(entries[0]?.kind === 'run' && entries[0].runId === RUN, JSON.stringify(entries));
  assert.deepEqual(w.reads, [], 'adopted from status alone');
});

test('unresolved: the timer\'s run, or a run of other actions, is NOT adopted; before the grace nothing is listed', async () => {
  const w = world();
  w.follower.started((await addedTo(w.journal, UNRESOLVED)).id);
  w.status = statusWith(running('live', { trigger: 'timer' }));
  w.follower.kick();
  await poll(w);
  w.status = statusWith(running('live', { actions: ['A4', 'A5'] }));
  await poll(w);
  assert.equal(w.journal.entries()[0]?.kind, 'unresolved');
  assert.deepEqual(w.reads, []);
});

test('unresolved, after the grace: runs --from <the confirm> --to <now>; ONE match is the run, NONE means it never ran, SEVERAL are shown as candidates', async () => {
  const lines = (n: number, actions = [{ id: 'A4', status: 'ran', count: 1, freedBytes: 5 }]) => Array.from({ length: n }, (_, i) => ({ runId: `20261005T10000${i}Z-${80 + i}`, trigger: 'manual', startedAt: `2026-10-05T10:00:0${i}+00:00`, outcome: 'completed', actions }));
  const listing = (runs: unknown[]): ReadOutcome => ({ kind: 'read', read: 'runs', distro: 'Ubuntu', body: { schemaVersion: 1, count: runs.length, runs } });
  const cases: readonly [unknown[], string][] = [[lines(1), 'adopted'], [[], 'neverRan'], [lines(2), 'ambiguous'], [[{ ...lines(1)[0], trigger: 'timer' }], 'neverRan']];
  for (const [runs, expected] of cases) {
    const w = world();
    w.follower.started((await addedTo(w.journal, UNRESOLVED)).id);
    w.answer = (request) => (request.read === 'runs' ? listing(runs) : show('done'));
    w.follower.kick();
    await poll(w);
    assert.deepEqual(w.reads, [], 'not before the grace');
    w.clock.now = T0 + FOLLOW_POLL.graceMs + 1;
    await poll(w);
    assert.deepEqual(w.reads[0], { read: 'runs', from: '2026-10-05T10:00:00Z', to: '2026-10-05T10:01:32Z' });
    const outcome = w.journal.entries()[0]?.kind === 'run' ? 'adopted' : w.shown[0]?.kind;
    assert.equal(outcome, expected, JSON.stringify(runs));
    if (expected === 'ambiguous') {
      assert.ok(w.shown[0]?.kind === 'ambiguous');
      assert.deepEqual(w.shown[0].candidates, ['20261005T100000Z-80', '20261005T100001Z-81']);
    }
  }
});

test('unresolved full check: a manual run line with NO actions is a full check (its history line records none); status names it ["collect"]', async () => {
  const fullCheck: NewEntry = { ...UNRESOLVED, op: 'fullCheck', actions: ['collect'] };
  const w = world();
  w.follower.started((await addedTo(w.journal, fullCheck)).id);
  w.answer = () => ({ kind: 'read', read: 'runs', distro: 'Ubuntu', body: { schemaVersion: 1, runs: [{ runId: RUN, trigger: 'manual', startedAt: '2026-10-05T10:00:01+00:00', outcome: 'completed', actions: [] }] } });
  w.clock.now = T0 + FOLLOW_POLL.graceMs + 1;
  w.follower.kick();
  await poll(w);
  assert.ok(w.journal.entries()[0]?.kind === 'run');
  const viaStatus = world();
  viaStatus.follower.started((await addedTo(viaStatus.journal, fullCheck)).id);
  viaStatus.status = statusWith(running('live', { actions: ['collect'], current: 'collect' }));
  viaStatus.follower.kick();
  await poll(viaStatus);
  assert.ok(viaStatus.journal.entries()[0]?.kind === 'run', 'adopted from status.running');
});

// ---- M6's churn, measured for the owner (research/2026-10-04_extension_poll_churn.md § E6.S3) ----

test('M6 churn, measured: a run followed for D minutes costs D × 15 status runs + 1 runs show; the ceiling bounds a wedged run at 451', async () => {
  const measure = async (minutes: number, endState: 'done' | 'wedged'): Promise<{ status: number; reads: number }> => {
    const w = world();
    w.follower.started((await addedTo(w.journal, RUN_ENTRY)).id);
    w.status = statusWith(running(endState === 'done' ? 'live' : 'wedged'));
    w.answer = () => show('done');
    w.follower.kick();
    const ticks = Math.round((minutes * 60_000) / FOLLOW_POLL.intervalMs);
    for (let i = 1; i <= ticks && w.timers.pending() > 0; i += 1) {
      w.clock.now = T0 + i * FOLLOW_POLL.intervalMs;
      w.status = endState === 'done' && i === ticks ? statusWith(undefined) : w.status;
      await poll(w);
    }
    return { status: w.statusCalls, reads: w.reads.length };
  };
  assert.deepEqual(await measure(2, 'done'), { status: 30, reads: 1 });
  assert.deepEqual(await measure(10, 'done'), { status: 150, reads: 1 });
  assert.deepEqual(await measure(40, 'wedged'), { status: 451, reads: 0 }, 'the 30-minute ceiling ends it');
});

test('M6: a poll armed while a run was in flight asks NOTHING when it fires after that run ended (found by the extension-host tier)', async () => {
  const w = world();
  w.follower.started((await addedTo(w.journal, RUN_ENTRY)).id);
  w.follower.kick();
  assert.equal(w.timers.pending(), 1);
  w.answer = () => show('done');
  await w.follower.tick();
  assert.deepEqual(w.journal.entries(), [], 'ended by a tick of its own');
  const asked = w.statusCalls;
  assert.ok(w.timers.fire(), 'the poll armed before still fires');
  await settle();
  assert.equal(w.statusCalls, asked, 'nothing is in flight: no status');
  assert.equal(w.timers.pending(), 0);
});
