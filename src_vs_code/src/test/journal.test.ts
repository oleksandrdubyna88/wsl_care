import assert from 'node:assert/strict';
import { test } from 'node:test';

import { CleanupJournal, JOURNAL_KEY, MAX_ENTRIES, type NewEntry } from '../cleanup/journal';
import { runIdOf, type RunId } from '../root/rootIds';
import { MapStore } from './support/memento';

/**
 * The cleanup journal (E6.S3, plan §15k #4, `common.durable-status` rules 1 and 4): the started runs and the unresolved
 * confirms, persisted in a `globalState`-shaped store — what a reload reads back. The store here is a map whose `update`
 * resolves on a LATER turn, so "persisted before it resolves" is something the test can observe.
 */

function run(id: string): RunId {
  const runId = runIdOf(id);
  assert.ok(runId !== undefined, id);
  return runId;
}

const UNRESOLVED: NewEntry = { kind: 'unresolved', op: 'clean', distro: 'Ubuntu', actions: ['A4'], since: '2026-10-05T10:00:00.000Z' };
const RUN: NewEntry = { kind: 'run', op: 'clean', distro: 'Ubuntu', actions: ['A4'], since: '2026-10-05T10:00:00.000Z', runId: run('20261005T100000Z-77') };

test('an entry is in the STORE when add resolves, and a new journal over the same store (a reload) reads it back', async () => {
  const store = new MapStore();
  const added = await new CleanupJournal(store).add(UNRESOLVED);
  assert.equal(store.writes, 1, 'the write completed before add resolved');
  const reloaded = new CleanupJournal(store).entries();
  assert.deepEqual(reloaded, [added]);
  assert.equal(reloaded[0]?.kind, 'unresolved');
  assert.match(reloaded[0]?.id ?? '', /^[0-9a-f-]{36}$/);
});

test('replace turns an unresolved confirm into its run (same id); remove takes it out; each change is persisted and announced', async () => {
  const store = new MapStore();
  const journal = new CleanupJournal(store);
  let changes = 0;
  journal.onChange(() => { changes += 1; });
  const added = await journal.add(UNRESOLVED);
  await journal.replace(added.id, RUN);
  assert.deepEqual(new CleanupJournal(store).entries(), [{ ...RUN, id: added.id }]);
  await journal.remove(added.id);
  assert.deepEqual(new CleanupJournal(store).entries(), []);
  assert.equal(changes, 3);
});

test('a store another build (or a corruption) wrote is read entry by entry: the valid ones kept, every other dropped — never a crash', () => {
  const store = new MapStore();
  const valid = { ...RUN, id: 'a' };
  store.values.set(JOURNAL_KEY, [
    valid,
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
  ]);
  assert.deepEqual(new CleanupJournal(store).entries().map((e) => e.id), ['a', 'h']);
  for (const junk of [undefined, 'x', 42, { entries: [] }]) {
    store.values.set(JOURNAL_KEY, junk);
    assert.deepEqual(new CleanupJournal(store).entries(), [], JSON.stringify(junk));
  }
});

test('growth is bounded: past MAX_ENTRIES the OLDEST entry goes', async () => {
  const store = new MapStore();
  const journal = new CleanupJournal(store);
  const first = await journal.add(UNRESOLVED);
  for (let i = 1; i <= MAX_ENTRIES; i += 1) {
    await journal.add({ ...UNRESOLVED, since: `2026-10-05T10:${String(i % 60).padStart(2, '0')}:00.000Z` });
  }
  const entries = new CleanupJournal(store).entries();
  assert.equal(entries.length, MAX_ENTRIES);
  assert.ok(!entries.some((e) => e.id === first.id), 'the oldest was dropped');
});

test('replace and remove of an id that is gone change nothing (another window already ended it)', async () => {
  const store = new MapStore();
  const journal = new CleanupJournal(store);
  const added = await journal.add(UNRESOLVED);
  await journal.replace('gone', RUN);
  await journal.remove('gone');
  assert.deepEqual(journal.entries(), [added]);
});
