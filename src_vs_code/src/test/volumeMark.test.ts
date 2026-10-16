import assert from 'node:assert/strict';
import { test } from 'node:test';

import type { StatusAnswer } from '../client/outcome';
import { barView, NO_VOLUME_MARK, volumeMarkOf, volumeMarkOfOutcome, withVolumeMark, type BarView } from '../statusBar/statusBarModel';
import { answered, headBody, setAt, type Body } from './support/outcomes';

/**
 * E19.S1 (PLAN_disk_hygiene_windows.md §3.2): the status bar's VOLUME mark — a critical `disk.volumeFree` (the exact id) adds
 * ` · D: low space` and the volume's free space and the reading's age to the tooltip, and never lowers the bar. A warn adds
 * nothing; no other `disk.*` verdict colours the bar (statusBar.test.ts holds that for the E15.S2 trio).
 */

interface Lettered {
  readonly letter: string;
  readonly freeBytes?: number;
  readonly freePercent?: number;
  readonly level: string;
  readonly judgedBy: string;
}

const GIB = 1024 ** 3;
const D_CRITICAL: Lettered = { letter: 'D', freeBytes: Math.round(4.1 * GIB), freePercent: 2.1, level: 'critical', judgedBy: 'gib-or-percent' };
const C_OK: Lettered = { letter: 'C', freeBytes: 988 * GIB, freePercent: 41.2, level: 'ok', judgedBy: 'gib-or-percent' };

/** The head golden's status body with `disk.volumeFree` set to `level`, read `ageSeconds` ago by the full run, and `disk.lettered`. */
function volumeBody(level: string, ageSeconds: number, lettered: readonly Lettered[], id = 'disk.volumeFree'): Body {
  const body = headBody('status');
  const verdicts = (body.verdicts as Record<string, unknown>[]).filter((v) => v.id !== id);
  verdicts.push({ id, level, value: 'D: 4.1 GiB free (2.1 %) — critical', limit: 'x', reason: 'y', basis: { source: 'fullRun', runId: 'r', ageSeconds } });
  body.verdicts = verdicts;
  setAt(body, 'disk.lettered', lettered);

  return body;
}

function statusAnswer(body: Body): StatusAnswer {
  const outcome = answered('status', body);
  assert.ok(outcome.kind === 'answered' && outcome.answer.verb === 'status');

  return outcome.answer;
}

function composed(body: Body): BarView {
  return withVolumeMark(barView(answered('status', body)), volumeMarkOf(statusAnswer(body)));
}

test('a critical disk.volumeFree marks the bar with the volume and its age', () => {
  const body = volumeBody('critical', 3 * 3600 + 12 * 60, [C_OK, D_CRITICAL]);
  const plain = barView(answered('status', body));

  const view = composed(body);

  assert.equal(view.text, `${plain.text} · D: low space`);
  assert.equal(view.level, 'critical');
  assert.equal(view.tooltip.split('\n')[0], 'D: 4.1 GiB free (2.1 %), read 3 h 12 min ago by the full run');
  assert.ok(view.tooltip.endsWith(plain.tooltip), 'the bar\'s own tooltip follows the mark\'s line');
});

test('the mark names the WORST critical volume, the lowest free share', () => {
  const body = volumeBody('critical', 600, [D_CRITICAL, { letter: 'F', freeBytes: 20 * GIB, freePercent: 1.4, level: 'critical', judgedBy: 'gib-or-percent' }, C_OK]);

  const view = composed(body);

  assert.match(view.text, / · F: low space$/);
  assert.equal(view.tooltip.split('\n')[0], 'F: 20.0 GiB free (1.4 %), read 10 min ago by the full run');
});

test('the age reads in minutes under an hour, "under a minute" below one, days from two', () => {
  assert.match(composed(volumeBody('critical', 45, [D_CRITICAL])).tooltip.split('\n')[0] ?? '', /, read under a minute ago by the full run$/);
  assert.match(composed(volumeBody('critical', 59 * 60, [D_CRITICAL])).tooltip.split('\n')[0] ?? '', /, read 59 min ago by the full run$/);
  assert.match(composed(volumeBody('critical', 2 * 24 * 3600 + 5 * 3600, [D_CRITICAL])).tooltip.split('\n')[0] ?? '', /, read 2 d 5 h ago by the full run$/);
});

test('a warn disk.volumeFree adds no mark — it stays in the panel', () => {
  const body = volumeBody('warn', 600, [{ ...D_CRITICAL, level: 'warn' }]);

  assert.deepEqual(volumeMarkOf(statusAnswer(body)), NO_VOLUME_MARK);
  assert.deepEqual(composed(body), barView(answered('status', body)));
});

test('ok and unknown add no mark either', () => {
  for (const level of ['ok', 'unknown']) {
    assert.deepEqual(volumeMarkOf(statusAnswer(volumeBody(level, 600, [D_CRITICAL]))), NO_VOLUME_MARK, level);
  }
});

test('the mark never lowers a critical bar, and a critical mark raises a bar that was uncoloured or only warning', () => {
  const critical: BarView = { text: 'WSL RAM 99% · swap 0.0G · 3 containers', tooltip: 'AI OS Care — Ubuntu', level: 'critical' };
  const mark = volumeMarkOf(statusAnswer(volumeBody('critical', 600, [D_CRITICAL])));

  assert.equal(withVolumeMark(critical, mark).level, 'critical');
  assert.equal(withVolumeMark(critical, { text: ' · x', level: 'warn', lines: [] }).level, 'critical', 'a lower mark never lowers the level');
  assert.equal(withVolumeMark({ ...critical, level: 'none' }, mark).level, 'critical');
  assert.equal(withVolumeMark({ ...critical, level: 'warn' }, mark).level, 'critical');
});

test('no other disk.* verdict colours the bar or marks it — only the exact id disk.volumeFree', () => {
  for (const id of ['disk.vhdxGap', 'disk.vhdxGrowth', 'disk.hostFree', 'disk.root', 'disk.discard', 'disk.volumeFreeX', 'disk.volume']) {
    const body = volumeBody('critical', 600, [D_CRITICAL], id);
    // The real id is absent (volumeBody dropped any previous one only for `id`): remove the golden's own so the test stands on `id` alone.
    body.verdicts = (body.verdicts as Record<string, unknown>[]).filter((v) => v.id !== 'disk.volumeFree');
    const plain = barView(answered('status', body));

    // The head golden's own level (host.compression warns in it since E15.S5): a critical disk.* verdict of another id changes nothing.
    assert.equal(plain.level, barView(answered('status', headBody('status'))).level, id);
    assert.ok(!plain.tooltip.includes(id), id);
    assert.deepEqual(composed(body), plain, id);
  }
});

test('a critical verdict with no critical volume listed still marks the bar, without inventing a letter', () => {
  const view = composed(volumeBody('critical', 600, []));

  assert.match(view.text, / · low space$/);
  assert.equal(view.level, 'critical');
  assert.match(view.tooltip.split('\n')[0] ?? '', /^disk\.volumeFree: critical/);
});

test('a daemon without disk.lettered or disk.volumeFree adds no mark (an older daemon)', () => {
  const body = headBody('status');
  body.verdicts = (body.verdicts as Record<string, unknown>[]).filter((v) => v.id !== 'disk.volumeFree');

  assert.deepEqual(volumeMarkOf(statusAnswer(body)), NO_VOLUME_MARK);
});

test('a volume that was not judged never names the mark, even if it were listed critical', () => {
  const body = volumeBody('critical', 600, [{ letter: 'E', freeBytes: 0, freePercent: 0, level: 'critical', judgedBy: 'not judged: smaller than 8 GiB (disk.volumeFreeMinSizeGib)' }, D_CRITICAL]);

  assert.match(composed(body).text, / · D: low space$/);
});

test('the bar takes the mark from the status outcome, and none while there is no answer', () => {
  assert.deepEqual(volumeMarkOfOutcome(undefined), NO_VOLUME_MARK);
  assert.match(withVolumeMark(barView(answered('status', volumeBody('critical', 600, [D_CRITICAL]))), volumeMarkOfOutcome(answered('status', volumeBody('critical', 600, [D_CRITICAL])))).text, / · D: low space$/);
});

test('an untrusted letter or number from the daemon cannot break the tooltip line', () => {
  const view = composed(volumeBody('critical', 600, [{ letter: 'D\nfake: line', freeBytes: Math.round(4.1 * GIB), freePercent: 2.1, level: 'critical', judgedBy: 'gib-or-percent' }]));

  assert.equal(view.text.includes('\n'), false);
  assert.equal(view.tooltip.split('\n')[0]?.startsWith('fake'), false);
});
