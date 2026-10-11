import assert from 'node:assert/strict';
import { test } from 'node:test';

import { ARCHIVE_MESSAGE_TYPES, parseArchiveMessage } from '../../archivePage/archiveMessages';

/**
 * Plan §15s, E10.S2a: the CLOSED set of messages the Archive page may send the host — exact keys, exact value shapes, nothing
 * else. A month is an INDEX into the list the host holds; an entry id, a session path, an agent or a month name never comes from
 * the page at all. E10.S2b: an entry is an index too, every index message carries the view's generation, and *Restore* and *Clear
 * selection* are bare — the ids a restore takes are the host's selection.
 */

test('the known instances parse to themselves — the companions that keep every refusal below from being vacuous', () => {
  const good: readonly unknown[] = [
    { type: 'ready' },
    { type: 'refresh' },
    { type: 'showPaths' },
    { type: 'hidePaths' },
    { type: 'expandMonth', index: 0, generation: 1 },
    { type: 'collapseMonth', index: 41, generation: 7 },
    { type: 'selectMonth', index: 2, generation: 0 },
    { type: 'toggleEntry', index: 4999, generation: 3 },
    { type: 'clearSelection' },
    { type: 'restore' },
    { type: 'rendered', blocks: 3 },
  ];
  for (const message of good) {
    assert.deepEqual(parseArchiveMessage(message), message, JSON.stringify(message));
  }
  assert.deepEqual([...ARCHIVE_MESSAGE_TYPES].sort(), ['clearSelection', 'collapseMonth', 'expandMonth', 'hidePaths', 'ready', 'refresh', 'rendered', 'restore', 'selectMonth', 'showPaths', 'toggleEntry']);
});

test('an id, a path, a month name or an extra field from the page is no message — dropped before the host does anything', () => {
  const bad: readonly unknown[] = [
    { type: 'expandMonth', index: 0, generation: 1, month: '2000/01' },
    { type: 'expandMonth', month: '2000/01', generation: 1 },
    { type: 'expandMonth', agent: 'claude-code', index: 0, generation: 1 },
    { type: 'expandMonth', index: 0, generation: 1, entryId: '0123456789abcdef' },
    // E10.S2b: an id never comes from the page — not to select, not to restore
    { type: 'toggleEntry', entryId: '0123456789abcdef', generation: 1 },
    { type: 'toggleEntry', index: 0, generation: 1, entryId: '0123456789abcdef' },
    { type: 'restore', entryIds: ['0123456789abcdef'] },
    { type: 'restore', ids: '0123456789abcdef\n' },
    { type: 'clearSelection', index: 0 },
    { type: 'selectMonth', index: 0, generation: 1, month: '2000/01' },
    // an index message without the generation of the view it came from (the S2a own review's #8)
    { type: 'expandMonth', index: 0 },
    { type: 'toggleEntry', index: 0 },
    { type: 'selectMonth', index: 0 },
    { type: 'toggleEntry', index: 0, generation: -1 },
    { type: 'toggleEntry', index: 0, generation: '1' },
    { type: 'toggleEntry', index: 0, generation: 1.5 },
    { type: 'showPaths', key: 'projects/p/s1.jsonl' },
    { type: 'refresh', argv: ['archive', 'list', '--restorable'] },
    { type: 'ready', extra: true },
    // indexes that cannot name a month of a list
    { type: 'expandMonth', index: -1, generation: 1 },
    { type: 'expandMonth', index: 1.5, generation: 1 },
    { type: 'expandMonth', index: '0', generation: 1 },
    { type: 'expandMonth', index: Number.NaN, generation: 1 },
    { type: 'collapseMonth', index: Number.POSITIVE_INFINITY, generation: 1 },
    { type: 'toggleEntry', index: -1, generation: 1 },
    { type: 'expandMonth' },
    { type: 'rendered', blocks: -1 },
    // types outside the set, and prototype names
    { type: 'restoreAll' },
    { type: 'selectAll' },
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
