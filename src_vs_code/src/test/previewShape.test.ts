import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

import { previewResultOf } from '../client/windowsTasks';
import { REPOSITORY_ROOT } from './support/paths';
import { exited, stageFixture } from './support/windowsTasks';

/**
 * WR-T3 (PLAN_windows_runner.md §3.12, "the fixture is held to the daemon's record"): there is no golden of `tasks preview cleanup --json`, so the
 * fixtures under `test/fixtures/windows-tasks/` are the only thing the panel's tests read it from — and a fixture that lies about its shape would pass
 * every test while the daemon answered something else. The C# records are READ, parameter by parameter (the way `exitCodes.test.ts` reads
 * `ExitCode.cs`): every positional parameter, camel-cased, is a key of the fixture, the fixture has no key the records lack, and the typed reader
 * in `client/windowsTasks.ts` reads every one of them (a key it never reads is a key whose absence goes unnoticed).
 */

const WINDOWS_TEMP = path.join(REPOSITORY_ROOT, 'src_daemon', 'src', 'WslCare.Core', 'Actions', 'WindowsTemp', 'TempPreviewReport.cs');
const DRY_WINDOW = path.join(REPOSITORY_ROOT, 'src_daemon', 'src', 'WslCare.Core', 'Actions', 'Engine', 'DryRunWindow.cs');

/** The positional parameters of `public sealed record <name>(…)`, camel-cased, in order. Commas inside generics are not separators. */
function recordKeys(source: string, name: string): string[] {
  const start = source.indexOf(`record ${name}(`);
  assert.ok(start >= 0, `the record ${name} was not found`);
  let depth = 0;
  let current = '';
  const parameters: string[] = [];
  for (const char of source.slice(start + `record ${name}(`.length)) {
    if (char === ')' && depth === 0) {
      break;
    }
    depth += char === '<' || char === '(' ? 1 : char === '>' || char === ')' ? -1 : 0;
    if (char === ',' && depth === 0) {
      parameters.push(current);
      current = '';
    } else {
      current += char;
    }
  }
  parameters.push(current);

  return parameters.map((p) => p.trim().split(/\s+/).at(-1) ?? '').filter((p) => p !== '').map((p) => p.charAt(0).toLowerCase() + p.slice(1));
}

const SOURCE = fs.readFileSync(WINDOWS_TEMP, 'utf8');
const DRY_SOURCE = fs.readFileSync(DRY_WINDOW, 'utf8');

/** The object at each place of the answer, with the record that describes it. */
const SHAPES: readonly (readonly [string, string, string])[] = [
  ['', 'TempPreviewReport', SOURCE],
  ['dry', 'DryRunPeek', DRY_SOURCE],
  ['pass', 'TempPreviewPass', SOURCE],
  ['counts', 'TempPreviewCounts', SOURCE],
  ['excludedByReason[0]', 'TempExcludedReason', SOURCE],
  ['groups[0]', 'TempPreviewGroup', SOURCE],
  ['groupsNotShown', 'TempGroupsNotShown', SOURCE],
];

function objectAt(body: Record<string, unknown>, place: string): Record<string, unknown> | undefined {
  if (place === '') {
    return body;
  }
  const [key = '', index] = place.replace(/\]$/, '').split('[');
  const member = body[key];

  return (index === undefined ? member : (member as unknown[])[Number(index)]) as Record<string, unknown> | undefined;
}

test('the record reader finds the daemon\'s parameters (its known instances)', () => {
  assert.deepEqual(recordKeys(SOURCE, 'TempGroupsNotShown'), ['eligible', 'wouldBeEligible']);
  assert.ok(recordKeys(SOURCE, 'TempPreviewReport').includes('wouldBeEligibleLimited'));
  assert.deepEqual(recordKeys(DRY_SOURCE, 'DryRunPeek'), ['dryRun', 'setting', 'week', 'until', 'reason']);
  assert.deepEqual(recordKeys('public sealed record X(IReadOnlyList<A> One, int Two)', 'X'), ['one', 'two'], 'a generic is one parameter');
});

test('every fixture of the preview has EXACTLY the keys of the daemon\'s records (until is omitted by the daemon when null)', () => {
  for (const name of ['preview-cleanup.json', 'preview-cleanup-limited.json', 'preview-cleanup-refused.json']) {
    const body = stageFixture(name) as Record<string, unknown>;
    for (const [place, record, source] of SHAPES) {
      const object = objectAt(body, place);
      if (object === undefined) {
        continue; // a list the fixture leaves empty (the refused root has no groups) has no member to hold against its record
      }
      const optional = (key: string): boolean => record === 'DryRunPeek' && key === 'until';
      const wanted = recordKeys(source, record).filter((key) => !optional(key));
      const have = Object.keys(object).filter((key) => !optional(key));
      for (const key of wanted) {
        assert.ok(have.includes(key), `the fixture ${name} lacks the key ${key} of ${record}`);
      }
      for (const key of have) {
        assert.ok(wanted.includes(key), `the fixture ${name} has a key ${key} that ${record} does not have`);
      }
    }
  }
});

/** Every key path of the fixture's objects: [place, key]. */
function keyPaths(): (readonly [string, string])[] {
  const body = stageFixture('preview-cleanup.json') as Record<string, unknown>;

  return SHAPES.flatMap(([place]) => Object.keys(objectAt(body, place) ?? {}).map((key) => [place, key] as const));
}

test('the typed reader reads EVERY member of the report: dropping any one from the answer is a failure that names it', () => {
  const keys = keyPaths();
  assert.ok(keys.length >= 50, `only ${keys.length} members found`);
  for (const [place, key] of keys) {
    const body = structuredClone(stageFixture('preview-cleanup.json')) as Record<string, unknown>;
    delete (objectAt(body, place) ?? {})[key];
    const result = previewResultOf(exited(0, body as never));
    assert.equal(result.kind, 'failed', `${place === '' ? '' : `${place}.`}${key} was dropped and the reader did not notice: ${result.kind}`);
    assert.match(result.kind === 'failed' ? result.reason : '', new RegExp(key), `${place}.${key}`);
  }
});
