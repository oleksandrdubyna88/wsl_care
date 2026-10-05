import assert from 'node:assert/strict';
import { test } from 'node:test';

import { CleanupJournal, JOURNAL_KEY, MAX_ACTIONS_PER_ENTRY, MAX_ENTRIES, type NewEntry } from '../cleanup/journal';
import { ACTION_IDS, runIdOf, type RunId } from '../root/rootIds';
import { DEFAULT_NUMBERS } from '../settings/numbers';
import { MapStore } from './support/memento';

/**
 * The cleanup journal (E6.S3, plan §15k #4, `common.durable-status` rules 1 and 4): the started runs and the unresolved
 * confirms, persisted in a `globalState`-shaped store — what a reload reads back. The store here is a map whose `update`
 * resolves on a LATER turn, so "persisted before it resolves" is something the test can observe; two journals over ONE
 * store are two windows (E6.S3 review C6). The clock is the test's (`common.testing`: a test that reads the clock expires).
 */

const NOW = Date.parse('2026-10-05T10:05:00.000Z');

function journalOver(store: MapStore, now = NOW): CleanupJournal {
  return new CleanupJournal(store, () => now);
}

function run(id: string): RunId {
  const runId = runIdOf(id);
  assert.ok(runId !== undefined, id);
  return runId;
}

const UNRESOLVED: NewEntry = { kind: 'unresolved', op: 'clean', distro: 'Ubuntu', actions: ['A4'], since: '2026-10-05T10:00:00.000Z' };
const RUN: NewEntry = { kind: 'run', op: 'clean', distro: 'Ubuntu', actions: ['A4'], since: '2026-10-05T10:00:00.000Z', runId: run('20261005T100000Z-77') };

async function added(journal: CleanupJournal, entry: NewEntry): Promise<NonNullable<Awaited<ReturnType<CleanupJournal['add']>>>> {
  const result = await journal.add(entry);
  assert.ok(result !== undefined, 'the journal took it');
  return result;
}

test('an entry is in the STORE when add resolves, and a new journal over the same store (a reload) reads it back', async () => {
  const store = new MapStore();
  const entry = await added(journalOver(store), UNRESOLVED);
  assert.ok(store.writes >= 1, 'the write completed before add resolved');
  const reloaded = journalOver(store).entries();
  assert.deepEqual(reloaded, [entry]);
  assert.equal(reloaded[0]?.kind, 'unresolved');
  assert.match(reloaded[0]?.id ?? '', /^[0-9a-f-]{36}$/);
});

test('replace turns an unresolved confirm into its run (same id); remove takes it out; each change is persisted and announced', async () => {
  const store = new MapStore();
  const journal = journalOver(store);
  let changes = 0;
  journal.onChange(() => { changes += 1; });
  const entry = await added(journal, UNRESOLVED);
  await journal.replace(entry.id, RUN);
  assert.deepEqual(journalOver(store).entries(), [{ ...RUN, id: entry.id }]);
  await journal.remove(entry.id);
  assert.deepEqual(journalOver(store).entries(), []);
  assert.equal(changes, 3);
  assert.equal(journalOver(store).has(entry.id), false);
});

test('a store another build (or a corruption) wrote is read entry by entry: the valid ones kept, every other dropped — never a crash', () => {
  const store = new MapStore();
  store.values.set(JOURNAL_KEY, {
    entries: [
      { ...RUN, id: 'a' },
      { ...RUN, id: 'b', runId: '20261005T100000Z-077' },
      { ...RUN, id: 'c', kind: 'paused' },
      { ...RUN, id: 'd', actions: ['A4', 'rm -rf /'] },
      { ...RUN, id: 'e', since: 'yesterday' },
      { ...RUN, id: 'f', distro: '' },
      { ...RUN, id: 'g', op: 'shutdown' },
      { ...UNRESOLVED, id: 'h', actions: ['collect'] },
      { ...UNRESOLVED, id: 'i', actions: [] },
      7,
      null,
    ],
    removed: [],
  });
  assert.deepEqual(journalOver(store).entries().map((e) => e.id), ['a', 'h']);
  for (const junk of [undefined, 'x', 42, [{ ...RUN, id: 'v1' }], { entries: 'x' }]) {
    store.values.set(JOURNAL_KEY, junk);
    assert.deepEqual(journalOver(store).entries(), [], JSON.stringify(junk));
  }
});

test('E6.S3 review A5: an instant that does not exist (month 13, 30 February) or lies in the future past the skew is dropped — it would never age', () => {
  const store = new MapStore();
  store.values.set(JOURNAL_KEY, {
    entries: [
      { ...RUN, id: 'month13', since: '2026-13-01T10:00:00.000Z' },
      { ...RUN, id: 'feb30', since: '2026-02-30T10:00:00.000Z' },
      { ...RUN, id: 'future', since: '2026-10-05T11:00:00.000Z' },
      { ...RUN, id: 'skewed', since: '2026-10-05T10:09:00.000Z' },
      { ...RUN, id: 'fine', since: '2026-10-05T10:00:00.000Z' },
    ],
    removed: [],
  });
  assert.deepEqual(journalOver(store).entries().map((e) => e.id), ['skewed', 'fine'], 'within the 5-minute skew is kept');
});

test('E6.S3 review C9: an entry holds at most MAX_ACTIONS_PER_ENTRY actions — the registry\'s size, not the entry cap', () => {
  assert.equal(MAX_ACTIONS_PER_ENTRY, ACTION_IDS.length);
  const store = new MapStore();
  store.values.set(JOURNAL_KEY, { entries: [{ ...RUN, id: 'many', actions: Array.from({ length: ACTION_IDS.length + 1 }, () => 'A4') }, { ...RUN, id: 'all', actions: [...ACTION_IDS] }], removed: [] });
  assert.deepEqual(journalOver(store).entries().map((e) => e.id), ['all']);
});

test('E6.S3 review C12: past MAX_ENTRIES a new entry is REFUSED (undefined) — no unshown entry is ever evicted', async () => {
  const store = new MapStore();
  const journal = journalOver(store);
  const first = await added(journal, UNRESOLVED);
  for (let i = 1; i < MAX_ENTRIES; i += 1) {
    await added(journal, UNRESOLVED);
  }
  assert.equal(await journal.add(UNRESOLVED), undefined);
  const entries = journalOver(store).entries();
  assert.equal(entries.length, MAX_ENTRIES);
  assert.ok(entries.some((e) => e.id === first.id), 'the oldest is still there');
});

test('replace and remove of an id that is gone change nothing (another window already ended it)', async () => {
  const store = new MapStore();
  const journal = journalOver(store);
  const entry = await added(journal, UNRESOLVED);
  await journal.replace('gone', RUN);
  await journal.remove('gone');
  assert.deepEqual(journal.entries(), [entry]);
});

// ---- E6.S3 review C6: two windows over ONE globalState ----

test('C6: two windows adding at the same moment both keep their entry (each write re-read and merged)', async () => {
  const store = new MapStore();
  const [x, y] = await Promise.all([journalOver(store).add(UNRESOLVED), journalOver(store).add(RUN)]);
  assert.ok(x !== undefined && y !== undefined);
  assert.deepEqual(journalOver(store).entries().map((e) => e.id).sort(), [x.id, y.id].sort());
});

test('C6: a removal and another window\'s replace at the same moment — the removal wins; the entry is never resurrected', async () => {
  const store = new MapStore();
  const a = journalOver(store);
  const entry = await added(a, UNRESOLVED);
  await Promise.all([a.remove(entry.id), journalOver(store).replace(entry.id, RUN)]);
  assert.deepEqual(journalOver(store).entries(), []);
  assert.equal(journalOver(store).has(entry.id), false);
});

test('C6: an entry whose id is tombstoned is never read — and a later write does not carry it back', async () => {
  const store = new MapStore();
  const entry = { ...RUN, id: 'x' };
  store.values.set(JOURNAL_KEY, { entries: [entry], removed: [{ id: 'x', at: NOW }] });
  const journal = journalOver(store);
  assert.deepEqual(journal.entries(), []);
  await added(journal, UNRESOLVED);
  const value = store.values.get(JOURNAL_KEY) as { entries: { id: string }[] };
  assert.ok(!value.entries.some((e) => e.id === 'x'), JSON.stringify(value));
});

test('§15p: a tombstone lives `wslCare.cleanup.tombstoneMinutes` — 3 minutes old, it is pruned at 2 and kept at 10', async () => {
  for (const [minutes, kept] of [[2, false], [10, true]] as const) {
    const store = new MapStore();
    store.values.set(JOURNAL_KEY, { entries: [], removed: [{ id: 'gone', at: NOW - 3 * 60_000 }] });
    const journal = new CleanupJournal(store, () => NOW, () => ({ ...DEFAULT_NUMBERS, tombstoneMinutes: minutes }));
    await added(journal, UNRESOLVED);
    const value = store.values.get(JOURNAL_KEY) as { removed: { id: string }[] };
    assert.equal(value.removed.some((t) => t.id === 'gone'), kept, String(minutes));
  }
});
