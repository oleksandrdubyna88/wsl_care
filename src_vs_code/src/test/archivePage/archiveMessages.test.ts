import assert from 'node:assert/strict';
import { test } from 'node:test';

import { ARCHIVE_MESSAGE_TYPES, parseArchiveMessage } from '../../archivePage/archiveMessages';

/**
 * Plan §15s, E10.S2a: the CLOSED set of messages the Archive page may send the host — exact keys, exact value shapes, nothing
 * else. A month is an INDEX into the list the host holds; an entry id, a session path, an agent or a month name never comes from
 * the page at all.
 */

test('the known instances parse to themselves — the companions that keep every refusal below from being vacuous', () => {
  const good: readonly unknown[] = [
    { type: 'ready' },
    { type: 'refresh' },
    { type: 'showPaths' },
    { type: 'hidePaths' },
    { type: 'expandMonth', index: 0 },
    { type: 'collapseMonth', index: 41 },
    { type: 'rendered', blocks: 3 },
  ];
  for (const message of good) {
    assert.deepEqual(parseArchiveMessage(message), message, JSON.stringify(message));
  }
  assert.deepEqual([...ARCHIVE_MESSAGE_TYPES].sort(), ['collapseMonth', 'expandMonth', 'hidePaths', 'ready', 'refresh', 'rendered', 'showPaths']);
});

test('an id, a path, a month name or an extra field from the page is no message — dropped before the host does anything', () => {
  const bad: readonly unknown[] = [
    { type: 'expandMonth', index: 0, month: '2000/01' },
    { type: 'expandMonth', month: '2000/01' },
    { type: 'expandMonth', agent: 'claude-code', index: 0 },
    { type: 'expandMonth', index: 0, entryId: '0123456789abcdef' },
    { type: 'showPaths', key: 'projects/p/s1.jsonl' },
    { type: 'refresh', argv: ['archive', 'list', '--restorable'] },
    { type: 'ready', extra: true },
    // indexes that cannot name a month of a list
    { type: 'expandMonth', index: -1 },
    { type: 'expandMonth', index: 1.5 },
    { type: 'expandMonth', index: '0' },
    { type: 'expandMonth', index: Number.NaN },
    { type: 'collapseMonth', index: Number.POSITIVE_INFINITY },
    { type: 'expandMonth' },
    { type: 'rendered', blocks: -1 },
    // types outside the set — the restore of S2b is not one of them yet — and prototype names
    { type: 'restore' },
    { type: 'toggleEntry', index: 0 },
    { type: 'today' },
    { type: 'constructor' },
    { type: '__proto__' },
    { type: 'toString' },
    { type: 'hasOwnProperty', index: 0 },
    'refresh',
    null,
    undefined,
    [],
    [{ type: 'refresh' }],
  ];
  for (const message of bad) {
    assert.equal(parseArchiveMessage(message), undefined, JSON.stringify(message));
  }
});
