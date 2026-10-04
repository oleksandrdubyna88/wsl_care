import assert from 'node:assert/strict';
import { test } from 'node:test';

import { age, gb, gib, MAX_TEXT, percent, safeText } from '../../panel/format';
import { at } from '../../panel/jsonPath';

/**
 * The view-model's small pure pieces: units, ages, and the one function every daemon string passes before it reaches
 * the page (`safeText`). Command lines, working folders and container names are written by ANY process in the
 * distribution (plan §15g M7), so the page receives them clipped and with every control and bidirectional-override
 * character made visible — and renders them with `textContent` only (`panelPage.test.ts`).
 */

test('memory is shown in binary GiB, Docker and disk in decimal GB — each as the daemon\'s own figures spell them', () => {
  assert.equal(gib(10509950976), '9.8 GiB');
  assert.equal(gib(0), '0.0 GiB');
  assert.equal(gb(20300000000), '20.3 GB');
  assert.equal(gb(641400000), '0.6 GB');
});

test('a percentage keeps at most one decimal and no float noise', () => {
  assert.equal(percent(66.8), '66.8 %');
  assert.equal(percent(13), '13 %');
  assert.equal(percent(33.20000000000001), '33.2 %');
});

test('an age reads as a person would say it', () => {
  assert.equal(age(0), 'just now');
  assert.equal(age(59), 'just now');
  assert.equal(age(60), '1 min ago');
  assert.equal(age(3599), '59 min ago');
  assert.equal(age(3600), '1.0 h ago');
  assert.equal(age(4 * 3600 + 1800), '4.5 h ago');
  assert.equal(age(3 * 86400), '3 d ago');
});

test('safeText makes control and bidi-override characters visible and keeps the rest verbatim', () => {
  assert.equal(safeText('node server.js --port=3000'), 'node server.js --port=3000');
  assert.equal(safeText('a\u0000b\u0007c\u001bd\u007fe\u009bf'), 'a\uFFFDb\uFFFDc\uFFFDd\uFFFDe\uFFFDf');
  assert.equal(safeText('evil\u202Egnp.exe'), 'evil\uFFFDgnp.exe');
  assert.equal(safeText('x\u2066y\u2069z'), 'x\uFFFDy\uFFFDz');
  assert.equal(safeText('tab\there\nnewline'), 'tab\uFFFDhere\uFFFDnewline');
  assert.equal(safeText('</script><img src=x onerror=alert(1)>'), '</script><img src=x onerror=alert(1)>', 'markup stays TEXT — the page never parses it');
});

test('safeText clips a long string to MAX_TEXT characters and says it did', () => {
  const long = 'A'.repeat(10_000);
  const shown = safeText(long);
  assert.equal(shown.length, MAX_TEXT + 1);
  assert.ok(shown.endsWith('…'));
  assert.equal(safeText('B'.repeat(MAX_TEXT)), 'B'.repeat(MAX_TEXT), 'exactly MAX_TEXT is not clipped');
});

test('at() reads a dotted path, an [id=…] member of an array (ids may hold dots), and answers undefined for a miss', () => {
  const body = { vm: { memory: { total: { bytes: 5 } } }, verdicts: [{ id: 'memory.swap', level: 'ok' }, { id: 'clock.jumps', level: 'warn' }] };
  assert.deepEqual(at(body, 'vm.memory.total'), { bytes: 5 });
  assert.equal(at(body, 'vm.memory.total.bytes'), 5);
  assert.deepEqual(at(body, 'verdicts[id=clock.jumps]'), { id: 'clock.jumps', level: 'warn' });
  assert.equal(at(body, 'verdicts[id=clock.jumps].level'), 'warn');
  assert.equal(at(body, 'vm.swap'), undefined);
  assert.equal(at(body, 'verdicts[id=nope]'), undefined);
  assert.equal(at(body, 'vm.memory.total.bytes.deeper'), undefined);
  assert.equal(at([1, 2], 'length'), undefined, 'only plain objects are walked');
});

test('at() refuses a path it cannot read rather than guessing', () => {
  assert.throws(() => at({}, 'vm..memory'), /not a field path/);
  assert.throws(() => at({}, 'verdicts[name=x]'), /not a field path/);
});
