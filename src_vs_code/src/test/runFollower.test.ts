import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

import { CleanupJournal, type JournalEntry, type NewEntry } from '../cleanup/journal';
import { FOLLOW_POLL, RunFollower, type RunResult } from '../cleanup/runFollower';
import { NOT_A_FULL_CHECK_PREFIXES } from '../cleanup/runMatching';
import type { ReadOutcome, VerbOutcome } from '../client/outcome';
import type { RunRead } from '../client/verbs';
import { runningOf } from '../root/rootAnswers';
import { runIdOf, type RunId } from '../root/rootIds';
import type { RunningBlock } from '../root/rootOutcome';
import { ManualTimers, MapStore } from './support/memento';
import { answered, failed, headBody } from './support/outcomes';
import { GOLDEN_ROOT, REPOSITORY_ROOT } from './support/paths';

/**
 * The durable poll (E6.S3, plan §15j M6, §15k #3 / #4, the coai E6.S2 plan round #3 contract; the E6.S3 review round's B1–B5,
 * C4, C5, C8, C13, C14, C16): against a journal over a `globalState`-shaped store, a scripted `status` (the head golden with
 * the running block a test sets), scripted run reads, a manual one-shot timer and a wall clock the test moves. Every read the
 * follower makes is recorded, so "status only, and ONE runs show at the terminal state" is a count, and "it stops itself" is
 * the absence of an armed timer.
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

function done(): ReadOutcome {
  return { kind: 'read', read: 'runsShow', distro: 'Ubuntu', body: golden('runs-show-done.json') };
}

function listing(runs: unknown[]): ReadOutcome {
  return { kind: 'read', read: 'runs', distro: 'Ubuntu', body: { schemaVersion: 1, count: runs.length, runs } };
}

const NOT_READ: ReadOutcome = { kind: 'timedOut', timeoutMs: 20_000, read: 'runsShow' };

/** One test's world, built by its constructor — no cast (the TypeScript doctrine §3). */
class World {
  readonly timers = new ManualTimers();
  readonly journal: CleanupJournal;
  readonly reads: RunRead[] = [];
  readonly shown: RunResult[] = [];
  readonly faults: unknown[] = [];
  readonly clock = { now: T0 };
  readonly follower: RunFollower;
  statusCalls = 0;
  afterTerminal = 0;
  status: VerbOutcome = statusWith(undefined);
  /** What the store's newest status says is running — the follower's `running()` (review C4). */
  storeRunning: RunningBlock | undefined = undefined;
  answer: (request: RunRead) => ReadOutcome | Promise<ReadOutcome> = () => show('unknown');
  focused = true;

  constructor(readonly store = new MapStore()) {
    this.journal = new CleanupJournal(store, () => this.clock.now);
    this.follower = new RunFollower({
      journal: this.journal,
      status: () => { this.statusCalls += 1; return Promise.resolve(this.status); },
      read: (request) => { this.reads.push(request); return Promise.resolve(this.answer(request)); },
      show: (result) => { this.shown.push(result); },
      afterTerminal: () => { this.afterTerminal += 1; return Promise.resolve(); },
      focused: () => this.focused,
      running: () => this.storeRunning,
      wallNow: () => this.clock.now,
      timers: this.timers,
      fault: (error) => { this.faults.push(error); },
    });
  }
}

async function addedTo(journal: CleanupJournal, entry: NewEntry): Promise<JournalEntry> {
  const added = await journal.add(entry);
  assert.ok(added !== undefined, 'the journal took it');
  return added;
}

const RUN_ENTRY: NewEntry = { kind: 'run', op: 'clean', distro: 'Ubuntu', actions: ['A4'], since: new Date(T0).toISOString(), runId: runId(RUN) };
const UNRESOLVED: NewEntry = { kind: 'unresolved', op: 'clean', distro: 'Ubuntu', actions: ['A4'], since: new Date(T0).toISOString() };
const FULL_CHECK: NewEntry = { ...UNRESOLVED, op: 'fullCheck', actions: ['collect'] };

/** Fires the armed poll and waits for its tick to finish. */
async function poll(w: World): Promise<void> {
  assert.ok(w.timers.fire(), 'a poll was armed');
  await settle();
}

async function settle(): Promise<void> {
  for (let i = 0; i < 30; i += 1) {
    await new Promise((resolve) => setImmediate(resolve));
  }
}

async function ours(w: World, entry: NewEntry): Promise<JournalEntry> {
  const added = await addedTo(w.journal, entry);
  w.follower.started(added.id);
  return added;
}

test('M6: nothing in flight — no journal entry, status.running none — arms nothing and asks nothing', async () => {
  const w = new World();
  w.follower.kick();
  await settle();
  assert.equal(w.timers.pending(), 0);
  assert.equal(w.statusCalls, 0);
});

test('a run this window follows: status every 4 s while it is in flight, then ONE runs show at the terminal state, shown, removed, and the poll stops', async () => {
  const w = new World();
  await ours(w, RUN_ENTRY);
  w.status = statusWith(running('live'));
  w.follower.kick();
  assert.equal(w.timers.armed[0]?.ms, FOLLOW_POLL.intervalMs);
  assert.ok(FOLLOW_POLL.intervalMs >= 3_000 && FOLLOW_POLL.intervalMs <= 5_000, 'M6: every 3–5 s');
  await poll(w);
  await poll(w);
  assert.deepEqual(w.reads, [], 'status only, while the run is in flight');
  w.answer = done;
  w.status = statusWith(undefined);
  await poll(w);
  assert.deepEqual(w.reads, [{ read: 'runsShow', runId: RUN }]);
  assert.equal(w.shown.length, 1);
  assert.equal(w.shown[0]?.kind, 'run');
  assert.deepEqual(new CleanupJournal(w.store, () => w.clock.now).entries(), [], 'the entry left with its terminal answer');
  assert.equal(w.afterTerminal, 1, 'the panel re-read once (Docker after)');
  assert.equal(w.timers.pending(), 0, 'it stopped itself');
  assert.equal(w.statusCalls, 3);
});

test('queued, live and wedged naming the run all count as in flight; C16: a runs show that says running is NOT asked again until status names the run once more', async () => {
  const w = new World();
  await ours(w, RUN_ENTRY);
  for (const state of ['queued', 'live', 'wedged']) {
    w.status = statusWith(running(state));
    w.follower.kick();
    await poll(w);
  }
  assert.deepEqual(w.reads, []);
  w.status = statusWith(running('live', { runId: '20261005T100500Z-78' }));
  w.answer = () => show('running');
  await poll(w);
  await poll(w);
  const ofRun = (): number => w.reads.filter((r) => r.read === 'runsShow' && r.runId === RUN).length;
  assert.equal(ofRun(), 1, 'one runs show after it left flight — not one every tick while status and history disagree');
  w.status = statusWith(running('live'));
  await poll(w);
  w.status = statusWith(undefined);
  w.answer = () => show('queued');
  await poll(w);
  assert.equal(ofRun(), 2, 'status named it again, it left again: one more (the other run in flight is observed on its own, review C4)');
  assert.equal(w.shown.length, 0);
  assert.equal(w.timers.pending(), 1, 'still polling');
});

test('§15k #4: runs show answering unknown is terminal — shown, removed, the poll stops; a dead run reads interrupted', async () => {
  const cases: readonly [ReadOutcome, string][] = [[show('unknown', { reason: 'never existed here' }), 'unknown'], [{ kind: 'read', read: 'runsShow', distro: 'Ubuntu', body: golden('runs-show-interrupted.json') }, 'interrupted']];
  for (const [answer, state] of cases) {
    const w = new World();
    await ours(w, RUN_ENTRY);
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

test('§15k #4 + review B1: past 30 minutes a run in flight gets ONE record read; only then does it end as "state unknown" WITH its run id', async () => {
  const w = new World();
  await ours(w, RUN_ENTRY);
  w.status = statusWith(running('wedged'));
  w.answer = () => show('running');
  w.follower.kick();
  await poll(w);
  assert.deepEqual(w.reads, []);
  w.clock.now = T0 + FOLLOW_POLL.ceilingMs + 1;
  await poll(w);
  assert.deepEqual(w.reads, [{ read: 'runsShow', runId: RUN }], 'the one record read before the ceiling may end it');
  assert.equal(w.shown[0]?.kind, 'ceiling');
  assert.equal(w.shown[0]?.entry.kind === 'run' ? w.shown[0].entry.runId : '', RUN);
  assert.deepEqual(w.journal.entries(), []);
  assert.equal(w.timers.pending(), 0);
});

test('review B1: an entry past the ceiling is KEPT while no status has answered for its distribution (VS Code open before WSL) — nothing shown, nothing read', async () => {
  const w = new World();
  await ours(w, RUN_ENTRY);
  w.clock.now = T0 + FOLLOW_POLL.ceilingMs + 1;
  w.status = failed('status', { kind: 'timedOut', timeoutMs: 20_000 });
  w.follower.kick();
  await poll(w);
  await poll(w);
  assert.equal(w.shown.length, 0);
  assert.equal(w.reads.length, 0);
  assert.equal(w.journal.entries().length, 1);
  w.status = statusWith(undefined);
  w.answer = done;
  await poll(w);
  assert.equal(w.shown[0]?.kind, 'run', 'the long-closed window still gets its real answer');
});

test('review B1: an unresolved confirm older than the ceiling is resolved by ONE runs window — clamped to the 90-day retention', async () => {
  const w = new World();
  await ours(w, UNRESOLVED);
  w.clock.now = T0 + 100 * 86_400_000;
  w.answer = () => listing([]);
  w.follower.kick();
  await poll(w);
  assert.equal(w.reads.length, 1);
  const read = w.reads[0];
  assert.ok(read?.read === 'runs');
  assert.equal(read.from, new Date(w.clock.now - 90 * 86_400_000).toISOString().replace('.000Z', 'Z'));
  assert.equal(w.shown[0]?.kind, 'neverRan');
});

test('a reload: a NEW follower over the same store resumes the run on its first kick, and a status that does not answer keeps the entry', async () => {
  const store = new MapStore();
  await addedTo(new World(store).journal, RUN_ENTRY);
  const w = new World(store);
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
  const w = new World();
  const entry = await addedTo(w.journal, RUN_ENTRY);
  w.focused = false;
  w.status = statusWith(running('live'));
  w.follower.kick();
  assert.equal(w.timers.pending(), 0, 'not this window\'s run, not focused: no poll');
  w.follower.started(entry.id);
  w.follower.kick();
  assert.equal(w.timers.pending(), 1, 'this window started it: polled unfocused');
});

test('review C5: an unfocused window does not SETTLE another window\'s entry — not even on a tick it makes for its own', async () => {
  const w = new World();
  await addedTo(w.journal, RUN_ENTRY);
  await ours(w, { ...RUN_ENTRY, runId: runId('20261005T100500Z-78') });
  w.focused = false;
  w.answer = () => show('done');
  await w.follower.tick();
  assert.deepEqual(w.reads.map((r) => (r.read === 'runsShow' ? r.runId : '')), ['20261005T100500Z-78'], 'only its own entry was asked about');
  assert.equal(w.journal.entries().length, 1, 'the other window\'s entry is left for it');
});

test('review C4: a run the daemon reports in flight starts the poll FROM IDLE (the store\'s status, no tick yet) — followed, and its result shown once', async () => {
  const w = new World();
  w.storeRunning = runningOf({ running: running('live', { trigger: 'timer' }) });
  w.status = statusWith(running('live', { trigger: 'timer' }));
  w.follower.kick();
  assert.equal(w.timers.pending(), 1, 'armed from the store\'s answer alone');
  await poll(w);
  w.status = statusWith(undefined);
  w.storeRunning = undefined;
  w.answer = done;
  await poll(w);
  await w.follower.tick();
  assert.equal(w.shown.length, 1, 'its result, once');
  assert.equal(w.reads.length, 1);
  assert.equal(w.timers.pending(), 0);
  const unfocused = new World();
  unfocused.focused = false;
  unfocused.storeRunning = runningOf({ running: running('live', { trigger: 'timer' }) });
  unfocused.follower.kick();
  assert.equal(unfocused.timers.pending(), 0, 'never while unfocused');
});

// ---- the unresolved confirm (the controller's outcomeUnknown with no run id) ----

test('unresolved: a queued / live run of trigger manual holding EXACTLY the confirmed actions is adopted, then followed as any run', async () => {
  const w = new World();
  await ours(w, UNRESOLVED);
  w.status = statusWith(running('queued'));
  w.follower.kick();
  await poll(w);
  const entries = w.journal.entries();
  assert.equal(entries.length, 1);
  assert.ok(entries[0]?.kind === 'run' && entries[0].runId === RUN, JSON.stringify(entries));
  assert.deepEqual(w.reads, [], 'adopted from status alone');
});

test('unresolved: the timer\'s run, or a run of other actions, is NOT adopted; before the grace nothing is listed', async () => {
  const w = new World();
  await ours(w, UNRESOLVED);
  w.status = statusWith(running('live', { trigger: 'timer' }));
  w.follower.kick();
  await poll(w);
  w.status = statusWith(running('live', { actions: ['A4', 'A5'] }));
  await poll(w);
  assert.equal(w.journal.entries()[0]?.kind, 'unresolved');
  assert.equal(w.reads.filter((r) => r.read === 'runs').length, 0);
});

test('review B4: at the grace, a matching run that is WEDGED is adopted, and one whose block is unknown / unreadable is waited on — never "never ran"', async () => {
  const cases: readonly [Record<string, unknown>, string][] = [
    [running('wedged'), 'run'],
    [{ state: 'unknown', reason: 'the pid cannot be inspected' }, 'unresolved'],
    [{ state: 'unreadable', reason: 'running.json does not parse' }, 'unresolved'],
    [running('live', { runId: undefined }), 'unresolved'],
  ];
  for (const [block, expected] of cases) {
    const w = new World();
    await ours(w, UNRESOLVED);
    w.clock.now = T0 + FOLLOW_POLL.graceMs + 1;
    w.status = statusWith(block);
    w.answer = () => listing([]);
    w.follower.kick();
    await poll(w);
    assert.equal(w.journal.entries()[0]?.kind, expected, JSON.stringify(block));
    assert.deepEqual(w.shown, [], 'nothing decided while a matching run may be in flight');
  }
});

test('unresolved, after the grace: runs over the window, widened by the clock skew (review B3); ONE match is the run, NONE "never ran", SEVERAL candidates', async () => {
  const lines = (n: number) => Array.from({ length: n }, (_, i) => ({ runId: `20261005T10000${i}Z-${80 + i}`, trigger: 'manual', startedAt: `2026-10-05T10:00:0${i}+00:00`, outcome: 'completed', actions: [{ id: 'A4', status: 'ran', count: 1, freedBytes: 5 }] }));
  const earlier = { ...lines(1)[0], startedAt: '2026-10-05T09:59:50+00:00' };
  const cases: readonly [unknown[], string][] = [[lines(1), 'adopted'], [[], 'neverRan'], [lines(2), 'ambiguous'], [[{ ...lines(1)[0], trigger: 'timer' }], 'neverRan'], [[earlier], 'adopted']];
  for (const [runs, expected] of cases) {
    const w = new World();
    await ours(w, UNRESOLVED);
    w.answer = (request) => (request.read === 'runs' ? listing(runs) : show('done'));
    w.follower.kick();
    await poll(w);
    assert.deepEqual(w.reads, [], 'not before the grace');
    w.clock.now = T0 + FOLLOW_POLL.graceMs + 1;
    await poll(w);
    assert.deepEqual(w.reads[0], { read: 'runs', from: '2026-10-05T09:55:00Z', to: '2026-10-05T10:06:32Z' }, 'both ends widened by 5 minutes');
    const outcome = w.journal.entries()[0]?.kind === 'run' ? 'adopted' : w.shown[0]?.kind;
    assert.equal(outcome, expected, JSON.stringify(runs));
    if (expected === 'ambiguous') {
      assert.ok(w.shown[0]?.kind === 'ambiguous');
      assert.deepEqual(w.shown[0].candidates, ['20261005T100000Z-80', '20261005T100001Z-81']);
    }
  }
});

test('review B2: a full check matches its history line as the daemon writes it — [] when it completed, ["collect"] when refused, cut off or swept — and never a refused unusable request or a reconciled orphan', async () => {
  const line = (actions: unknown[], extra: Record<string, unknown> = {}) => ({ runId: RUN, trigger: 'manual', startedAt: '2026-10-05T10:00:01+00:00', outcome: 'completed', actions, ...extra });
  const cases: readonly [Record<string, unknown>, boolean][] = [
    [line([]), true],
    [line([{ id: 'collect', status: 'refused', count: 0, freedBytes: 0 }], { outcome: 'refused', reason: 'busy: run X holds the lock' }), true],
    [line([{ id: 'collect', status: 'interrupted', count: 0, freedBytes: 0 }], { outcome: 'interrupted', reason: 'swept: the detached run never recorded itself' }), true],
    [line([], { outcome: 'interrupted', reason: 'interrupted by SIGTERM during the measurement: nothing was recorded but this line' }), true],
    [line([], { outcome: 'refused', reason: 'refused: its request could not be used (schema 9); nothing was run' }), false],
    [line([], { outcome: 'interrupted', reason: 'the run wrote its detail and ended before its history line (found by the next run\'s reconcile)' }), false],
    [line([], { outcome: 'interrupted', reason: 'the run left a detail that cannot be read; its start is the second its id names' }), false],
  ];
  for (const [candidate, adopted] of cases) {
    const w = new World();
    await ours(w, FULL_CHECK);
    w.answer = () => listing([candidate]);
    w.clock.now = T0 + FOLLOW_POLL.graceMs + 1;
    w.follower.kick();
    await poll(w);
    assert.equal(w.journal.entries()[0]?.kind === 'run', adopted, JSON.stringify(candidate));
  }
  const viaStatus = new World();
  await ours(viaStatus, FULL_CHECK);
  viaStatus.status = statusWith(running('live', { actions: ['collect'], current: 'collect' }));
  viaStatus.follower.kick();
  await poll(viaStatus);
  assert.ok(viaStatus.journal.entries()[0]?.kind === 'run', 'adopted from status.running');
});

test('§15o: kind FIRST — kind "collect" is the full check whatever its actions and reason; kind "act" never is; no kind or an unknown one keeps the old rule', async () => {
  const line = (actions: unknown[], extra: Record<string, unknown> = {}) => ({ runId: RUN, trigger: 'manual', startedAt: '2026-10-05T10:00:01+00:00', outcome: 'refused', actions, ...extra });
  const unusable = 'refused: its request could not be used (schema 9); nothing was run';
  const cases: readonly [Record<string, unknown>, boolean][] = [
    // kind decides: a refused full check written the §15o way, [] actions — even with a reason the old rule excludes.
    [line([], { kind: 'collect', reason: 'busy: run X holds the lock' }), true],
    [line([], { kind: 'collect', reason: unusable }), true],
    [line([{ id: 'A4', status: 'interrupted', count: 0, freedBytes: 0 }], { kind: 'collect', outcome: 'interrupted', reason: 'swept' }), true],
    // an act is never the full check, whatever shape its actions have.
    [line([], { kind: 'act', reason: 'busy: run X holds the lock' }), false],
    [line([{ id: 'collect', status: 'refused', count: 0, freedBytes: 0 }], { kind: 'act' }), false],
    // no kind (a daemon older than §15o): today's rule exactly.
    [line([], { reason: 'busy: run X holds the lock' }), true],
    [line([], { reason: unusable }), false],
    // an unknown kind reads as absent: the old rule, never a crash.
    [line([], { kind: 'sweep', reason: 'busy: run X holds the lock' }), true],
    [line([], { kind: 'sweep', reason: unusable }), false],
    [line([{ id: 'A4', status: 'ran', count: 1, freedBytes: 5 }], { kind: 'Collect' }), false],
    [line([], { kind: 7 }), true],
  ];
  for (const [candidate, adopted] of cases) {
    const w = new World();
    await ours(w, FULL_CHECK);
    w.answer = () => listing([candidate]);
    w.clock.now = T0 + FOLLOW_POLL.graceMs + 1;
    w.follower.kick();
    await poll(w);
    assert.equal(w.journal.entries()[0]?.kind === 'run', adopted, JSON.stringify(candidate));
  }
});

test('review C7 (§15o): an act entry is never resolved by a line of kind "collect" carrying its ids; kind "act", no kind or an unknown kind keep matching by ids', async () => {
  const line = (kind: unknown) => ({ runId: RUN, trigger: 'manual', startedAt: '2026-10-05T10:00:01+00:00', outcome: 'completed', ...(kind === undefined ? {} : { kind }), actions: [{ id: 'A4', status: 'ran', count: 1, freedBytes: 5 }] });
  const cases: readonly [unknown, boolean][] = [['collect', false], ['act', true], [undefined, true], ['sweep', true]];
  for (const [kind, adopted] of cases) {
    const w = new World();
    await ours(w, UNRESOLVED);
    w.answer = () => listing([line(kind)]);
    w.clock.now = T0 + FOLLOW_POLL.graceMs + 1;
    w.follower.kick();
    await poll(w);
    assert.equal(w.journal.entries()[0]?.kind === 'run', adopted, String(kind));
  }
});

test('§15o: an act entry still matches by its actions — a kind on the line changes nothing for it', async () => {
  const w = new World();
  await ours(w, UNRESOLVED);
  w.answer = () => listing([{ runId: RUN, trigger: 'manual', startedAt: '2026-10-05T10:00:01+00:00', outcome: 'completed', kind: 'act', actions: [{ id: 'A4', status: 'ran', count: 1, freedBytes: 5 }] }]);
  w.clock.now = T0 + FOLLOW_POLL.graceMs + 1;
  w.follower.kick();
  await poll(w);
  assert.ok(w.journal.entries()[0]?.kind === 'run');
});

// ---- bounded reads, dispose, per-entry faults, concurrency, one panel round, two windows ----

test('review C8: a record read that keeps failing is tried 3 times with backoff — then ends as "state unknown — the record could not be read"', async () => {
  const w = new World();
  await ours(w, RUN_ENTRY);
  w.answer = () => NOT_READ;
  w.follower.kick();
  for (let i = 1; i <= 40; i += 1) {
    w.clock.now = T0 + i * FOLLOW_POLL.intervalMs;
    if (w.timers.pending() > 0) {
      await poll(w);
    }
  }
  assert.equal(w.reads.length, 3, 'three tries, not one every 4 s');
  const result = w.shown[0];
  assert.ok(result?.kind === 'unreadable', JSON.stringify(result));
  assert.match(result.reason, /timedOut|did not answer/);
  assert.deepEqual(w.journal.entries(), []);
});

test('review B5: a follower disposed while its read is out shows nothing, removes nothing and re-reads no panel', async () => {
  const w = new World();
  await ours(w, RUN_ENTRY);
  let release: (value: ReadOutcome) => void = () => undefined;
  w.answer = () => new Promise<ReadOutcome>((resolve) => { release = resolve; });
  const tick = w.follower.tick();
  await settle();
  w.follower.dispose();
  release(show('done'));
  await tick;
  await settle();
  assert.deepEqual(w.shown, []);
  assert.equal(w.journal.entries().length, 1);
  assert.equal(w.afterTerminal, 0);
});

test('review A5: one entry whose settling throws is reported, and the others are still settled (per entry, never the whole loop)', async () => {
  const w = new World();
  await ours(w, RUN_ENTRY);
  await ours(w, { ...RUN_ENTRY, runId: runId('20261005T100500Z-78') });
  w.answer = (request) => {
    if (request.read === 'runsShow' && request.runId === RUN) {
      throw new Error('a defect in one entry');
    }
    return done();
  };
  await w.follower.tick();
  assert.equal(w.faults.length, 1);
  assert.equal(w.shown.length, 1, 'the other entry still ended');
});

test('review C13 / C14: entries are settled concurrently, at most 4 at a time — and a tick that ends several re-reads the panel ONCE', async () => {
  const w = new World();
  for (let i = 0; i < 6; i += 1) {
    await ours(w, { ...RUN_ENTRY, runId: runId(`20261005T10000${i}Z-${90 + i}`) });
  }
  let open = 0;
  let most = 0;
  w.answer = async () => {
    open += 1;
    most = Math.max(most, open);
    await settle();
    open -= 1;
    return done();
  };
  await w.follower.tick();
  assert.ok(most > 1 && most <= 4, `at most 4 at once, more than 1 (${most})`);
  assert.equal(w.shown.length, 6);
  assert.equal(w.afterTerminal, 1, 'one panel round for the tick');
});

test('review C6: two windows ending the same entry at the same moment show its result ONCE between them', async () => {
  const store = new MapStore();
  const a = new World(store);
  const b = new World(store);
  const entry = await ours(a, RUN_ENTRY);
  b.follower.started(entry.id);
  a.answer = done;
  b.answer = done;
  await Promise.all([a.follower.tick(), b.follower.tick()]);
  await settle();
  assert.equal(a.shown.length + b.shown.length, 1);
});

test('M6: a poll armed while a run was in flight asks NOTHING when it fires after that run ended (found by the extension-host tier)', async () => {
  const w = new World();
  await ours(w, RUN_ENTRY);
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

// ---- M6's churn, measured for the owner (research/2026-10-04_extension_poll_churn.md § E6.S3) ----

test('M6 churn, measured: a run followed for D minutes costs D × 15 status runs + 1 runs show; the ceiling bounds a wedged run at 451 (+ its one record read)', async () => {
  const measure = async (minutes: number, endState: 'done' | 'wedged'): Promise<{ status: number; reads: number }> => {
    const w = new World();
    await ours(w, RUN_ENTRY);
    w.status = statusWith(running(endState === 'done' ? 'live' : 'wedged'));
    w.answer = () => show(endState === 'done' ? 'done' : 'running');
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
  assert.deepEqual(await measure(40, 'wedged'), { status: 451, reads: 1 }, 'the 30-minute ceiling ends it, after one record read');
});

test('review B1: a status that answers for ANOTHER distribution is no evidence either — an old entry is kept', async () => {
  const w = new World();
  await ours(w, RUN_ENTRY);
  w.clock.now = T0 + FOLLOW_POLL.ceilingMs + 1;
  w.status = answered('status', headBody('status'), 'Debian');
  w.follower.kick();
  await poll(w);
  assert.equal(w.shown.length, 0);
  assert.equal(w.reads.length, 0);
  assert.equal(w.journal.entries().length, 1);
});

test('review B1 + B4: an unresolved confirm past the ceiling whose block cannot be read gets ONE runs window, then ends "state unknown" — never "never ran", never stuck', async () => {
  const w = new World();
  await ours(w, UNRESOLVED);
  w.clock.now = T0 + FOLLOW_POLL.ceilingMs + 1;
  w.status = statusWith({ state: 'unreadable', reason: 'running.json does not parse' });
  w.answer = () => listing([]);
  w.follower.kick();
  await poll(w);
  assert.equal(w.reads.filter((r) => r.read === 'runs').length, 1);
  assert.equal(w.shown[0]?.kind, 'ceiling');
});

test('§15o (daemon #16): a reconciled full-check orphan whose detail was readable carries kind "collect" AND the reconcile\'s prefix — kind wins, it is the full check', async () => {
  const contract = JSON.parse(fs.readFileSync(path.join(REPOSITORY_ROOT, 'contracts', 'history-reasons.json'), 'utf8')) as { notAFullCheckWithoutKind: { writer: string; prefix: string }[] };
  const reconciled = contract.notAFullCheckWithoutKind.filter((r) => r.writer === 'RunReconcile.InterruptedLine').map((r) => r.prefix);
  assert.equal(reconciled.length, 2, 'the known instances: both reconcile prefixes');
  for (const [prefix, kind, adopted] of [[reconciled[0], 'collect', true], [reconciled[0], undefined, false], [reconciled[1], 'collect', true], [reconciled[1], undefined, false]] as const) {
    const w = new World();
    await ours(w, FULL_CHECK);
    w.answer = () => listing([{ runId: RUN, trigger: 'manual', startedAt: '2026-10-05T10:00:01+00:00', outcome: 'interrupted', actions: [], reason: `${prefix ?? ''}.`, ...(kind === undefined ? {} : { kind }) }]);
    w.clock.now = T0 + FOLLOW_POLL.graceMs + 1;
    w.follower.kick();
    await poll(w);
    assert.equal(w.journal.entries()[0]?.kind === 'run', adopted, `${String(kind)} ${prefix ?? ''}`);
  }
});

test('§15o (daemon #16): the fallback reason prefixes for a line WITHOUT a kind are exactly the daemon\'s contract file\'s', () => {
  const contract = JSON.parse(fs.readFileSync(path.join(REPOSITORY_ROOT, 'contracts', 'history-reasons.json'), 'utf8')) as { notAFullCheckWithoutKind: { prefix: string }[] };
  assert.deepEqual([...NOT_A_FULL_CHECK_PREFIXES], contract.notAFullCheckWithoutKind.map((r) => r.prefix));
  assert.equal(NOT_A_FULL_CHECK_PREFIXES.length, 3, 'the known instances');
});
