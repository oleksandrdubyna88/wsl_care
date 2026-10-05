import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

import { CleanupJournal } from '../cleanup/journal';
import { followPollOf, FOLLOW_POLL } from '../cleanup/runFollower';
import { parseLogsMessage } from '../logsPage/logsMessages';
import { DEFAULT_NUMBERS, NUMBER_NAMES, NUMBER_SETTINGS, numberOf, readNumbers } from '../settings/numbers';
import { MapStore } from './support/memento';
import { EXTENSION_ROOT } from './support/paths';

/**
 * "Every number is configurable" (the owner's standing rule, 2026-10-05): the E6 numbers are `wslCare.*` settings of
 * application scope, with ranges and defaults — ONE table (`settings/numbers.ts`), which `package.json` must equal, read
 * at each use, and a value outside its range never reaches the code.
 */

interface Property {
  readonly type: string;
  readonly default: number;
  readonly minimum: number;
  readonly maximum: number;
  readonly scope: string;
  readonly markdownDescription: string;
}

function properties(): Readonly<Record<string, Property>> {
  const manifest = JSON.parse(fs.readFileSync(path.join(EXTENSION_ROOT, 'package.json'), 'utf8')) as { contributes: { configuration: { properties: Record<string, Property> } } };
  return manifest.contributes.configuration.properties;
}

test('package.json contributes EXACTLY the table: every number an application-scoped integer with its default, minimum and maximum', () => {
  const contributed = properties();
  for (const name of NUMBER_NAMES) {
    const setting = NUMBER_SETTINGS[name];
    const property = contributed[`wslCare.${setting.key}`];
    assert.ok(property !== undefined, `wslCare.${setting.key} is not contributed`);
    assert.deepEqual(
      { type: property.type, default: property.default, minimum: property.minimum, maximum: property.maximum, scope: property.scope, markdownDescription: property.markdownDescription },
      { type: 'integer', default: setting.default, minimum: setting.minimum, maximum: setting.maximum, scope: 'application', markdownDescription: setting.description },
      setting.key,
    );
  }
  const numeric = Object.keys(contributed).filter((key) => contributed[key]?.type === 'integer' && key !== 'wslCare.refreshSeconds');
  assert.deepEqual(numeric.sort(), NUMBER_NAMES.map((name) => `wslCare.${NUMBER_SETTINGS[name].key}`).sort(), 'no number contributed outside the table');
});

test('every default lies in its range, and the known instances are the numbers this branch used', () => {
  for (const name of NUMBER_NAMES) {
    const s = NUMBER_SETTINGS[name];
    assert.ok(s.minimum <= s.default && s.default <= s.maximum, name);
  }
  assert.deepEqual([DEFAULT_NUMBERS.followPollSeconds, DEFAULT_NUMBERS.followCeilingMinutes, DEFAULT_NUMBERS.requestGraceSeconds, DEFAULT_NUMBERS.previewExpiryMinutes, DEFAULT_NUMBERS.journalEntries, DEFAULT_NUMBERS.maxRunIndex], [4, 30, 90, 5, 32, 9_999]);
  assert.ok(NUMBER_SETTINGS.requestGraceSeconds.minimum > 60, 'above the daemon\'s own 60 s request grace (RequestSweep.Grace)');
});

test('a value read back is an integer inside its range: out of range clamped, anything else the default', () => {
  const s = NUMBER_SETTINGS.detachSeconds;
  assert.equal(numberOf(s, 900), 900);
  assert.equal(numberOf(s, 5), s.minimum, 'a hand-edited value below the worst case is clamped up to the minimum');
  assert.equal(numberOf(s, 10 ** 9), s.maximum);
  for (const bad of [undefined, null, '900', 900.5, Number.NaN, Number.POSITIVE_INFINITY, [900], { value: 900 }]) {
    assert.equal(numberOf(s, bad), s.default, JSON.stringify(bad));
  }
  const read = readNumbers((key) => (key === 'cleanup.followPollSeconds' ? 10 : undefined));
  assert.deepEqual(read, { ...DEFAULT_NUMBERS, followPollSeconds: 10 });
});

test('a configured number reaches its use: the poll, the journal\'s budget, the Logs page\'s index bound', async () => {
  assert.deepEqual(followPollOf({ ...DEFAULT_NUMBERS, followPollSeconds: 10, followCeilingMinutes: 60, requestGraceSeconds: 120 }), { intervalMs: 10_000, ceilingMs: 3_600_000, graceMs: 120_000 });
  assert.deepEqual(FOLLOW_POLL, { intervalMs: 4_000, ceilingMs: 1_800_000, graceMs: 90_000 }, 'the defaults');
  const journal = new CleanupJournal(new MapStore(), () => Date.parse('2026-10-05T10:00:00Z'), () => 2);
  const entry = { kind: 'unresolved', op: 'clean', distro: 'Ubuntu', actions: ['A4'], since: '2026-10-05T10:00:00.000Z' } as const;
  assert.ok(await journal.add(entry));
  assert.ok(await journal.add(entry));
  assert.equal(await journal.add(entry), undefined, 'a budget of 2 refuses the third');
  assert.deepEqual(parseLogsMessage({ type: 'expand', index: 150 }, 100), undefined);
  assert.deepEqual(parseLogsMessage({ type: 'expand', index: 150 }, 200), { type: 'expand', index: 150 });
});
