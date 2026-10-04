#!/usr/bin/env node
/**
 * Rewrites the panel's field-map table in research/architecture.md from the ONE table the renderer reads
 * (src/panel/fieldMap.ts, rendered as Markdown by src/test/support/fieldMapDoc.ts) — between the two markers, and
 * nothing else. `fieldMap.test.ts` fails while the document and the code differ; this is how they are made equal.
 *
 *     npm run fieldmap:doc     (compiles first; reads out/)
 */
import { readFileSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const require = createRequire(import.meta.url);
const { BEGIN, END, fieldMapMarkdown } = require(join(ROOT, 'out', 'test', 'support', 'fieldMapDoc.js'));
const doc = join(ROOT, '..', 'research', 'architecture.md');

const text = readFileSync(doc, 'utf8');
const start = text.indexOf(BEGIN);
const end = text.indexOf(END);
if (start < 0 || end < start) {
  console.error(`field-map-doc: ${doc} has no ${BEGIN} … ${END} block — add the two markers where the table belongs`);
  process.exit(1);
}
writeFileSync(doc, text.slice(0, start) + fieldMapMarkdown() + text.slice(end + END.length));
console.log('field-map-doc: research/architecture.md updated');
