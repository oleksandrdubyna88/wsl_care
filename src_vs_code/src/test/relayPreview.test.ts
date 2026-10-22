import assert from 'node:assert/strict';
import { test } from 'node:test';

import { parsePreview } from '../root/rootAnswers';
import { MAX_PROCESS_ARGS } from '../root/rootIds';
import { DEFAULT_TARGET } from '../wsl/distroTarget';
import { goldenFile } from './support/paths';
import { relayKeys, relayPreviewBody } from './support/relayBodies';

/**
 * S7b.2b: A21's preview held by the REAL `parsePreview` — its `shown` processes validated one by one under the daemon's
 * invariant (`shown.length == min(count, cap)`, `shownTruncated` exactly when the cap cut it), A4's ONE selection code
 * generalised; a list longer than the command line takes is cut to `MAX_PROCESS_ARGS`, said as truncated.
 */

const CONTEXT = { distro: 'Ubuntu', target: DEFAULT_TARGET, ids: ['A21'], takenAtMs: 0 } as const;

test('A21\'s shown processes are held, in order, with the daemon\'s count', () => {
  const preview = parsePreview(JSON.stringify(relayPreviewBody(relayKeys(3))), CONTEXT);
  assert.ok(!('kind' in preview), JSON.stringify(preview).slice(0, 300));
  assert.deepEqual(preview.processes, { names: relayKeys(3), count: 3, truncated: false, cap: 10_000 });
  assert.equal(preview.a4, undefined, 'A4 was not asked');
});

test('an A21 entry that is not a canonical pid:start refuses the preview — nothing can be confirmed', () => {
  for (const bad of ['1:5', '5:1 --timer', 'x', 5]) {
    const outcome = parsePreview(JSON.stringify(relayPreviewBody([...relayKeys(1), bad as string])), CONTEXT);
    assert.equal('kind' in outcome && outcome.kind, 'shownListInvalid', JSON.stringify(bad));
    assert.match(JSON.stringify(outcome), /entry 2 of its shown list is not a process as pid:start/);
  }
});

test('a count that does not match the shown list refuses it, as A4\'s does', () => {
  const outcome = parsePreview(JSON.stringify(relayPreviewBody(relayKeys(2), { count: 5 })), CONTEXT);
  assert.equal('kind' in outcome && outcome.kind, 'shownListInvalid');
});

test('an unavailable A21 preview holds no process list — the flow tells its reason', () => {
  const preview = parsePreview(JSON.stringify(relayPreviewBody([], { available: false, reason: 'the boot id cannot be read' })), CONTEXT);
  assert.ok(!('kind' in preview));
  assert.equal(preview.processes, undefined);
  assert.equal(preview.actions[0]?.available, false);
});

test('a list longer than the command line takes is cut to MAX_PROCESS_ARGS and said as truncated, the daemon\'s count kept', () => {
  const shown = relayKeys(MAX_PROCESS_ARGS + 7);
  const preview = parsePreview(JSON.stringify(relayPreviewBody(shown)), CONTEXT);
  assert.ok(!('kind' in preview), JSON.stringify(preview).slice(0, 300));
  assert.equal(preview.processes?.names.length, MAX_PROCESS_ARGS);
  assert.deepEqual(preview.processes?.names, shown.slice(0, MAX_PROCESS_ARGS));
  assert.equal(preview.processes?.count, MAX_PROCESS_ARGS + 7);
  assert.equal(preview.processes?.truncated, true);
  assert.equal(preview.processes?.cap, MAX_PROCESS_ARGS);
});

test('the DAEMON-WRITTEN A21 line (contracts/golden/head/act-a21-preview-action.json) is held as it is: its two shown pairs, its count, its basis', () => {
  const line = goldenFile('act-a21-preview-action.json');
  const preview = parsePreview(JSON.stringify({ ...relayPreviewBody([]), actions: [line] }), CONTEXT);
  assert.ok(!('kind' in preview), JSON.stringify(preview).slice(0, 300));
  assert.deepEqual(preview.processes, { names: ['12062:48563', '12063:48564'], count: 2, truncated: false, cap: 10_000 });
  assert.match(preview.actions[0]?.basis ?? '', /^interop relays read from \/proc/);
});
