import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

import { FAKE_SHAPES } from './fake/fakeWsl';
import { RUN_ID_SHAPE, UTC_INSTANT_SHAPE } from '../shared/shapes';
import { fromExtensionRoot, shippedSources, SOURCE_ROOT } from './support/paths';

/**
 * The E6.S3 review round's "one decision, one place" (coai #0 / #5, #8 / #12 / #14, #18): the daemon's run-id spelling and
 * the client's instant shape live in ONE module the client, the root ids and the strict fake import; `gb` and `minuteOf`
 * are defined once; the result words take the ceiling as a value instead of importing the follower. Each scan names
 * what it found, and each has a known instance it must still find.
 */

const RUN_ID_FRAGMENT = '[0-9]{8}T[0-9]{6}Z';

function scanned(): { file: string; text: string }[] {
  const fake = path.join(SOURCE_ROOT, 'test', 'fake', 'fakeWsl.ts');
  return [...shippedSources(), fake].map((file) => ({ file: fromExtensionRoot(file), text: fs.readFileSync(file, 'utf8') }));
}

test('the run-id shape is spelt in ONE module — the shared one — and the client, the root ids and the fake use it', () => {
  const spellers = scanned().filter((s) => s.text.includes(RUN_ID_FRAGMENT)).map((s) => s.file);
  assert.deepEqual(spellers, ['src/shared/shapes.ts']);
  assert.equal(FAKE_SHAPES.runId, RUN_ID_SHAPE, 'the fake holds the very same object');
  assert.equal(FAKE_SHAPES.instant, UTC_INSTANT_SHAPE);
  assert.ok(RUN_ID_SHAPE.test('20261005T100000Z-77') && !RUN_ID_SHAPE.test('20261005T100000Z-077'));
  assert.ok(UTC_INSTANT_SHAPE.test('2026-10-05T10:00:00Z') && !UTC_INSTANT_SHAPE.test('2026-10-05T10:00:00+02:00'));
});

test('gb and minuteOf are DEFINED once (src/text/format.ts); everyone else imports them', () => {
  for (const name of ['gb', 'minuteOf']) {
    const definers = scanned().filter((s) => new RegExp(`function ${name}\\(`).test(s.text)).map((s) => s.file);
    assert.deepEqual(definers, ['src/text/format.ts'], name);
  }
});

test('the result words do not import the follower: the ceiling is handed in', () => {
  const text = scanned().find((s) => s.file === 'src/cleanup/resultText.ts')?.text ?? '';
  assert.ok(text.length > 0);
  assert.doesNotMatch(text, /import {[^}]*FOLLOW_POLL/, 'a type import of RunResult is fine; the ceiling VALUE is handed in');
});
