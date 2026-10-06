import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

import { firstModal, missingFromPreview, secondModal } from '../cleanup/modalText';
import { ROW_IDS } from '../cleanup/rowIds';
import { parsePreview } from '../root/rootAnswers';
import { ACTION_IDS, type ActionIds } from '../root/rootIds';
import type { HeldPreview } from '../root/rootOutcome';
import { golden, GOLDEN_ROOT } from './support/paths';

/**
 * The confirmation modals' words (E6.S3, plan §7.3, §15j M8, m8): built from a preview read by the REAL `parsePreview`
 * over the daemon's golden (or an edited copy), so what the modal says is what the daemon answered.
 */

function previewBody(edit: (actions: Record<string, unknown>[]) => void = () => undefined): Record<string, unknown> {
  const body = JSON.parse(fs.readFileSync(path.join(GOLDEN_ROOT, 'head', 'act-a4-preview.json'), 'utf8')) as Record<string, unknown>;
  edit(body.actions as Record<string, unknown>[]);
  return body;
}

function held(ids: ActionIds, body = previewBody()): HeldPreview {
  const preview = parsePreview(JSON.stringify(body), { distro: 'Ubuntu', ids, takenAtMs: 0 });
  assert.ok(!('kind' in preview), JSON.stringify(preview).slice(0, 300));
  return preview;
}

function withActions(...extra: Record<string, unknown>[]): Record<string, unknown> {
  return previewBody((actions) => actions.push(...extra));
}

function action(id: string, what: string, count: number, bytes: number, names: string[] = []): Record<string, unknown> {
  return { id, summary: '', status: 'previewed', reason: '', preview: { what, available: true, count, bytes, items: names.map((name) => ({ kind: 'container', name, bytes: 1 })) } };
}

test('the preview\'s actions are read with what they remove: the text, the count, the bytes and the listed names', () => {
  const a4 = held(['A4']).actions[0];
  assert.equal(a4?.what, 'anonymous volumes no container uses, first seen unattached at least 0 days ago');
  assert.equal(a4?.count, 387);
  assert.equal(a4?.bytes, 59_598_000_000);
  assert.equal(a4?.available, true);
  assert.ok((a4?.items.length ?? 0) > 0 && (a4?.items.length ?? 99) <= 20, 'the daemon lists at most 20 items');
});

test('A4\'s modal: the distribution, the count and the size, the first names, how many more — and that it is BOUND to the list shown (m8)', () => {
  const modal = firstModal(held(['A4']), false);
  assert.equal(modal.message, 'Clean A4 in "Ubuntu"?');
  assert.equal(modal.confirm, 'Clean');
  assert.match(modal.detail, /A4 — anonymous volumes no container uses, first seen unattached at least 0 days ago: 387 objects, 59\.6 GB/);
  assert.match(modal.detail, /Removes exactly the 387 volumes this preview listed/);
  assert.doesNotMatch(modal.detail, /re-checked at run time/);
  const names = held(['A4']).actions[0]?.items ?? [];
  assert.ok(modal.detail.includes(`• ${names[0] ?? 'none'}`));
  assert.match(modal.detail, new RegExp(`… and ${387 - names.length} more`));
});

test('A4 past the cap: the modal says only the shown names are removed, and how many were selected (§15k #11)', () => {
  const body = previewBody((actions) => {
    const a4 = actions[0] as { shown: string[]; shownTruncated?: boolean; preview: { count: number } };
    a4.shown = Array.from({ length: 10_000 }, (_, i) => i.toString(16).padStart(64, '0'));
    a4.shownTruncated = true;
    a4.preview.count = 10_001;
  });
  assert.match(firstModal(held(['A4'], body), false).detail, /Only the 10000 volumes shown are removed; 10001 were selected/);
});

test('A5, A5Testcontainers, A6, A6Unused and A7 say "re-checked at run time" (m8); A8 and A9 do not', () => {
  const body = withActions(action('A5', 'stopped containers', 3, 3e9), action('A5Testcontainers', 'tc', 1, 1), action('A6', 'dangling', 1, 1), action('A6Unused', 'unused', 1, 1), action('A7', 'cache', 1, 1), action('A8', 'npm cache', 1, 1), action('A9', 'apt', 1, 1));
  const detail = firstModal(held(['A4', 'A5', 'A5Testcontainers', 'A6', 'A6Unused', 'A7', 'A8', 'A9'], body), true).detail;
  const blockOf = (id: string): string => detail.split('\n\n').find((b) => b.startsWith(`${id} — `)) ?? '';
  for (const id of ['A5', 'A5Testcontainers', 'A6', 'A6Unused', 'A7']) {
    assert.match(blockOf(id), /re-checked at run time/, id);
  }
  for (const id of ['A4', 'A8', 'A9']) {
    assert.doesNotMatch(blockOf(id), /re-checked at run time/, id);
  }
});

test('Clean selected names every row in one modal; an action the daemon could not read says so with its reason', () => {
  const body = withActions({ id: 'A8', status: 'previewed', reason: '', preview: { what: 'the npm cache', available: false, reason: 'no target user' } });
  const modal = firstModal(held(['A4', 'A8'], body), true);
  assert.equal(modal.message, 'Clean the 2 selected rows in "Ubuntu"?');
  assert.match(modal.detail, /A8 — the npm cache: not read — no target user/);
});

test('hostile text reaches the modal printable and short: control and bidi characters as U+FFFD, a long name cut', () => {
  const evil = `pay\u202Etxt.exe${'x'.repeat(300)}`;
  const body = withActions(action('A5', 'stopped\u0007 containers\u001b[2J', 1, 1, [evil]));
  const detail = firstModal(held(['A5'], body), false).detail;
  // eslint-disable-next-line no-control-regex -- matching control characters is the point of this assertion
  assert.ok(!/[\u0000-\u001f\u202A-\u202E]/.test(detail.replace(/\n/g, '')), 'no control or bidi character survives');
  assert.ok(detail.includes('pay\uFFFDtxt.exe'));
  assert.ok(!detail.includes('x'.repeat(100)), 'the name is cut short');
  assert.ok(detail.includes('stopped\uFFFD containers\uFFFD[2J'));
});

test('the second confirmation (§7.3): A5, A6Unused, A8, A11, A12 — each named with the setting it used; none for the others', () => {
  const second = secondModal(['A4', 'A5', 'A6Unused', 'A8', 'A11', 'A12']);
  assert.ok(second !== undefined);
  assert.equal(second.confirm, 'Clean anyway');
  for (const setting of ['containers.stoppedOlderThanDays', 'images.unusedOlderThanDays', 'auto.A8', 'processes.idleOlderThanHours', 'auto.A12']) {
    assert.ok(second.detail.includes(setting), setting);
  }
  assert.doesNotMatch(second.detail, /^A4 /m);
  assert.equal(secondModal(['A4', 'A5Testcontainers', 'A6', 'A7', 'A9']), undefined);
  for (const id of ACTION_IDS.filter((a) => !['A5', 'A6Unused', 'A8', 'A11', 'A12'].includes(a))) {
    assert.equal(secondModal([id]), undefined, id);
  }
});

test('the row ids are the rows preview --all reports, and each is an action of the compiled registry', () => {
  const rows = (golden('head', 'preview').rows as { id: string }[]).map((r) => r.id);
  assert.deepEqual([...ROW_IDS], rows);
  for (const id of ROW_IDS) {
    assert.ok((ACTION_IDS as readonly string[]).includes(id), id);
  }
});

test('E6.S3 review A3: the modal has ONE block per id the confirm acts on — in that order — whatever the daemon answered', () => {
  const body = withActions(action('A9', 'apt', 1, 1), action('A8', 'npm', 1, 1), action('A7', 'not asked', 9, 9));
  const modal = firstModal(held(['A4', 'A8', 'A9'], body), true);
  const heads = modal.detail.split('\n\n').filter((b) => /^A\d/.test(b)).map((b) => b.split(' ')[0]);
  assert.deepEqual(heads, ['A4', 'A8', 'A9'], 'the confirmed ids, not the answer\'s entries');
  assert.doesNotMatch(modal.detail, /not asked/);
});

test('E6.S3 review A3: missingFromPreview names the confirmed ids the daemon\'s preview did not describe', () => {
  assert.deepEqual(missingFromPreview(held(['A4', 'A8'])), ['A8']);
  assert.deepEqual(missingFromPreview(held(['A4'])), []);
});

test('#17: the truncated line names the cap in force, not the compiled bound', () => {
  const body = previewBody((actions) => {
    const a4 = actions.find((a) => a.id === 'A4') as { shown: string[]; shownTruncated?: boolean };
    a4.shown = a4.shown.slice(0, 300);
    a4.shownTruncated = true;
  });
  const preview = parsePreview(JSON.stringify(body), { distro: 'Ubuntu', ids: ['A4'], takenAtMs: 0, maxShownNames: 300 });
  assert.ok(!('kind' in preview), JSON.stringify(preview).slice(0, 300));
  assert.match(firstModal(preview, false).detail, /Only the 300 volumes shown are removed; 387 were selected\./);
});
