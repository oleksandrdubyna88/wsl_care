import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

import { FIELD_MAP, isArriving, SECTIONS } from '../../panel/fieldMap';
import { BEGIN, END, fieldMapMarkdown } from '../support/fieldMapDoc';
import { REPOSITORY_ROOT } from '../support/paths';

/**
 * ONE field map, two readers (plan §15g B2): the renderer reads `FIELD_MAP`, and research/architecture.md carries the
 * same table generated from it. This holds them equal — a row changed in one place only is a red build.
 */

const ARCHITECTURE = path.join(REPOSITORY_ROOT, 'research', 'architecture.md');

function documentBlock(markdown: string): string {
  const lines = markdown.split(/\r?\n/);
  const start = lines.indexOf(BEGIN);
  const end = lines.indexOf(END);
  assert.ok(start >= 0 && end > start, `research/architecture.md lacks the field-map markers (${BEGIN} … ${END})`);

  return lines.slice(start, end + 1).join('\n');
}

test('research/architecture.md carries exactly the field map the panel renders from (npm run fieldmap:doc rewrites it)', () => {
  assert.equal(documentBlock(fs.readFileSync(ARCHITECTURE, 'utf8')), fieldMapMarkdown());
});

test('the comparison bites: a row re-pointed in the code only no longer equals the document', () => {
  const doc = fieldMapMarkdown();
  const planted = doc.replace('vm.memory.swapUsed', 'vm.memory.swapTotal');
  assert.notEqual(planted, doc);
  assert.notEqual(documentBlock(planted), fieldMapMarkdown());
});

test('the field map is well-formed: unique ids, known sections, every section has a row, each arriving row names its epic and why', () => {
  const ids = FIELD_MAP.map((r) => r.id);
  assert.equal(new Set(ids).size, ids.length, 'ids are unique');
  for (const row of FIELD_MAP) {
    assert.ok(SECTIONS.some((s) => s.id === row.section), row.id);
  }
  for (const section of SECTIONS) {
    assert.ok(FIELD_MAP.some((r) => r.section === section.id), `${section.id} has no row`);
  }
  for (const row of FIELD_MAP.filter(isArriving)) {
    assert.match(row.arrives, /^E\d+$/, row.id);
    assert.ok(row.why.length > 0, row.id);
  }
});
