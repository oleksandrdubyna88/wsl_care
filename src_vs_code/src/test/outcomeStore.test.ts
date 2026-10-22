import assert from 'node:assert/strict';
import { test } from 'node:test';

import type { VerbOutcome } from '../client/outcome';
import { EMPTY, OutcomeStore, type Snapshot } from '../state/outcomeStore';
import { listed } from './support/distroNames';
import { answered, failed, headBody } from './support/outcomes';

/**
 * E18.W1a-2 (todo/PLAN_disk_health_E18.md §3.3, RED 12 and 17b): the store holds the DEFAULT distribution's snapshot exactly as it
 * always did and, beside it, one snapshot per PICKED distribution. A SETTING change clears the default's; a PICK clears nothing; a
 * distribution that leaves WSL's list loses its entry at the next `dropUnlisted`, and a write for a name the last list did not hold is
 * ignored — a read that resolves after its distribution was unregistered re-inserts nothing.
 */

const DEBIAN = listed('Debian');
const UBUNTU_AND_DEBIAN = [listed('Ubuntu'), DEBIAN];

function status(distro: string): VerbOutcome {
  return answered('status', headBody('status'), distro);
}

function distroOf(outcome: VerbOutcome | undefined): string {
  return outcome !== undefined && outcome.kind === 'answered' ? outcome.distro : `not answered: ${JSON.stringify(outcome)}`;
}

/** A store whose last `dropUnlisted` held Ubuntu and Debian — as after `listDistros()` on the owner's machine. */
function listedStore(): OutcomeStore {
  const store = new OutcomeStore();
  store.dropUnlisted(UBUNTU_AND_DEBIAN);

  return store;
}

test('setPicked leaves the default\'s snapshot untouched; snapshotOf reads what was written; a distro never written is EMPTY', () => {
  const store = listedStore();
  store.set('status', status('Ubuntu'));
  const before = store.snapshot();
  store.setPicked(DEBIAN, 'status', status('Debian'));
  assert.equal(store.snapshot(), before, 'the very same default snapshot: nothing was replaced');
  assert.equal(distroOf(store.snapshotOf(DEBIAN).status), 'Debian');
  assert.equal(store.snapshotOf(DEBIAN).preview, undefined);
  assert.equal(store.snapshotOf(listed('Alpine', ['Alpine'])), EMPTY, 'the same EMPTY the default starts from, so a reader needs no new case');
  assert.deepEqual(store.pickedNames(), [DEBIAN]);
});

test('setPicked replaces verb by verb: the next read\'s status does not touch the preview already held', () => {
  const store = listedStore();
  store.setPicked(DEBIAN, 'preview', answered('preview', headBody('preview'), 'Debian'));
  store.setPicked(DEBIAN, 'status', status('Debian'));
  assert.equal(distroOf(store.snapshotOf(DEBIAN).preview), 'Debian');
  assert.equal(distroOf(store.snapshotOf(DEBIAN).status), 'Debian');
});

test('clear() empties the default\'s three outcomes and its checking, keeps windowsStatus AND every picked snapshot', () => {
  const store = listedStore();
  store.set('status', status('Ubuntu'));
  store.set('preview', answered('preview', headBody('preview'), 'Ubuntu'));
  store.setChecking(true);
  store.setWindowsStatus(status('Windows'));
  store.setPicked(DEBIAN, 'status', status('Debian'));
  store.setPickedChecking(DEBIAN, true);
  store.clear();
  const snapshot = store.snapshot();
  assert.deepEqual([snapshot.status, snapshot.preview, snapshot.doctor, snapshot.checking], [undefined, undefined, undefined, false]);
  assert.equal(distroOf(snapshot.windowsStatus), 'Windows');
  assert.equal(distroOf(store.snapshotOf(DEBIAN).status), 'Debian', 'a setting change is about the default; a pick is not about the setting');
  assert.equal(store.snapshotOf(DEBIAN).checking, true);
});

test('dropUnlisted removes the snapshots of distributions the list no longer holds and keeps the default\'s', () => {
  const store = listedStore();
  store.set('status', status('Ubuntu'));
  store.setPicked(DEBIAN, 'status', status('Debian'));
  store.dropUnlisted([listed('Ubuntu')]);
  assert.equal(store.snapshotOf(DEBIAN), EMPTY);
  assert.deepEqual(store.pickedNames(), []);
  assert.equal(distroOf(store.snapshot().status), 'Ubuntu', 'the default\'s snapshot is not about a name');
});

test('onChange fires on a picked write, with the DEFAULT snapshot as it is', () => {
  const store = listedStore();
  store.set('status', status('Ubuntu'));
  const seen: Snapshot[] = [];
  store.onChange((snapshot) => { seen.push(snapshot); });
  store.setPicked(DEBIAN, 'status', status('Debian'));
  store.setPickedChecking(DEBIAN, true);
  assert.equal(seen.length, 2);
  assert.ok(seen.every((snapshot) => distroOf(snapshot.status) === 'Ubuntu'), 'the bar and the panel re-render what they already show');
});

// ---- RED 17b: no write for a distro the list dropped (the gate's finding 4) ----

test('a write for a name the last list did not hold is ignored — a fresh store has been told no list, so it holds no picked snapshot at all', () => {
  const store = new OutcomeStore();
  let changes = 0;
  store.onChange(() => { changes += 1; });
  store.setPicked(DEBIAN, 'status', status('Debian'));
  store.setPickedChecking(DEBIAN, true);
  assert.equal(store.snapshotOf(DEBIAN), EMPTY);
  assert.deepEqual(store.pickedNames(), []);
  assert.equal(changes, 0, 'an ignored write is not a change');
});

test('a read that resolves after its distribution was dropped re-inserts nothing; once listed again the write is taken, from EMPTY', () => {
  const store = listedStore();
  store.setPicked(DEBIAN, 'status', status('Debian'));
  store.dropUnlisted([listed('Ubuntu')]);
  store.setPicked(DEBIAN, 'preview', answered('preview', headBody('preview'), 'Debian'));
  store.setPickedChecking(DEBIAN, false);
  assert.equal(store.snapshotOf(DEBIAN), EMPTY);

  store.dropUnlisted(UBUNTU_AND_DEBIAN);
  store.setPicked(DEBIAN, 'preview', answered('preview', headBody('preview'), 'Debian'));
  assert.equal(store.snapshotOf(DEBIAN).status, undefined, 'the dropped snapshot did not come back');
  assert.equal(distroOf(store.snapshotOf(DEBIAN).preview), 'Debian');
});

test('a stopped outcome is stored like any other under the picked name', () => {
  const store = listedStore();
  store.setPicked(DEBIAN, 'status', failed('status', { kind: 'stopped', distro: 'Debian' }));
  assert.deepEqual(store.snapshotOf(DEBIAN).status, { kind: 'stopped', distro: 'Debian', verb: 'status' });
});
