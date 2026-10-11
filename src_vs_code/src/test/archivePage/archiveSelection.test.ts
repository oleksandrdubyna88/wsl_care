import assert from 'node:assert/strict';
import { test } from 'node:test';

import { isRestorableStatus, keptIn, monthPick, newestFirst, restoreCeiling, selectableId, selectableIds } from '../../archivePage/archiveSelection';
import { MAX_RESTORE_ENTRIES } from '../../shared/shapes';
import { bodyOf, type Body } from '../support/body';
import { headBody } from '../support/outcomes';
import { goldenFile } from '../support/paths';

/**
 * Plan §15s, E10.S2b story 3: the Archive page's restore selection, pure. Selectable is what A20 restores without a terminal
 * decision — verified, removed at its source (whole or in part), an id of the daemon's shape; the ceiling is the list's
 * `restoreCeiling` under the daemon's `act.maxShownNames`; a new list keeps the selection by id; *Select month* takes the newest.
 */

function entry(edit: Body = {}): Body {
  const entries = goldenFile('archive-list.json').entries;
  assert.ok(Array.isArray(entries));
  return { ...bodyOf(entries[0], 'archive-list.json entries[0]'), status: 'sourceRemoved', verified: true, ...edit };
}

test('selectable = verified, removed at its source or split, and an id of 16 lowercase hex — nothing else', () => {
  assert.equal(selectableId(entry()), '0123456789abcdef');
  assert.equal(selectableId(entry({ status: 'split' })), '0123456789abcdef');
  for (const edit of [{ verified: false }, { verified: 'true' }, { verified: undefined }, { status: 'archived' }, { status: 'restored' }, { status: 'damaged' }, { status: 'toString' }, { entryId: '0123456789ABCDEF' }, { entryId: '0123456789abcde' }, { entryId: 7 }]) {
    assert.equal(selectableId(entry(edit)), undefined, JSON.stringify(edit));
  }
  assert.equal(isRestorableStatus('sourceRemoved'), true);
  assert.equal(isRestorableStatus('archived'), false);
  assert.deepEqual(selectableIds([entry(), entry(), entry({ verified: false, entryId: '00000000000000aa' })]), ['0123456789abcdef'], 'each once');
});

test('the ceiling is min(restoreCeiling, maxShownNames) — and undefined when the list does not say or says nonsense', () => {
  const list = goldenFile('archive-list.json');
  const status = headBody('status');
  const lowered = { ...status, limits: { ...bodyOf(status.limits, 'limits'), maxShownNames: 7 } };
  assert.equal(restoreCeiling(list, status), 1000, 'the golden: restoreCeiling 1000 under the default 10000');
  assert.equal(restoreCeiling(list, lowered), 7);
  assert.equal(restoreCeiling(list, undefined), 1000, 'no status yet: the contract default for maxShownNames');
  assert.equal(restoreCeiling({ ...list, restoreCeiling: 999_999 }, status), MAX_RESTORE_ENTRIES, 'never past what one restore op takes');
  for (const bad of [undefined, 0, -1, 1.5, '1000', null]) {
    assert.equal(restoreCeiling({ ...list, restoreCeiling: bad }, status), undefined, String(bad));
  }
});

test('a new list keeps the selected ids it still holds as selectable, and counts the others', () => {
  const list = { entries: [entry({ entryId: '00000000000000a1' }), entry({ entryId: '00000000000000a2', status: 'restored' })] };
  const { kept, dropped } = keptIn(new Set(['00000000000000a1', '00000000000000a2', '00000000000000a3']), list);
  assert.deepEqual([...kept], ['00000000000000a1']);
  assert.equal(dropped, 2);
});

test('newest first by archivedAtUtc, ties and unreadable instants in the daemon order, unreadable last', () => {
  const a = entry({ entryId: 'a', archivedAtUtc: '2000-01-02T00:00:00+00:00' });
  const b = entry({ entryId: 'b', archivedAtUtc: 'not a time' });
  const c = entry({ entryId: 'c', archivedAtUtc: '2000-01-09T00:00:00+00:00' });
  const d = entry({ entryId: 'd', archivedAtUtc: '2000-01-02T00:00:00+00:00' });
  assert.deepEqual(newestFirst([a, b, c, d]).map((e) => e.entryId), ['c', 'a', 'd', 'b']);
});

test('Select month: every selectable one not yet selected when it fits — else the newest that fit, the offer counted', () => {
  const month = [
    entry({ entryId: '00000000000000a1', archivedAtUtc: '2000-01-01T00:00:00+00:00' }),
    entry({ entryId: '00000000000000a2', archivedAtUtc: '2000-01-03T00:00:00+00:00' }),
    entry({ entryId: '00000000000000a3', archivedAtUtc: '2000-01-02T00:00:00+00:00', verified: false }),
  ];
  assert.deepEqual(monthPick(month, new Set(), 10), { added: ['00000000000000a2', '00000000000000a1'], offered: 2 });
  assert.deepEqual(monthPick(month, new Set(), 1), { added: ['00000000000000a2'], offered: 2 });
  assert.deepEqual(monthPick(month, new Set(['00000000000000a2']), 5), { added: ['00000000000000a1'], offered: 1 });
  assert.deepEqual(monthPick(month, new Set(), 0), { added: [], offered: 2 });
  assert.deepEqual(monthPick(month, new Set(), -3), { added: [], offered: 2 });
});
