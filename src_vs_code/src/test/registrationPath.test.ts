import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import { test } from 'node:test';

import { fromExtensionRoot, shippedSources } from './support/paths';
import { stringLiteralsOf } from './support/sourceScan';

/**
 * ONE sanctioned registration path (WR-E3, PLAN_windows_runner.md §3.4 invariant 7): `Register-ScheduledTask`, `DeleteTask(`, `CreateFolder(` and
 * `DeleteFolder(` are spelt in exactly one shipped module, `src/scheduler/registerScript.ts` — the text the guard (elevated) and the Windows tasks
 * (unelevated) both run. Every other shipped source is read by the TypeScript parser, so a comment that names them is no finding and a template
 * spread over lines still is. A second copy of a registration script is a second place for a principal, a folder rule or a quote to drift.
 */

const HOME = 'src/scheduler/registerScript.ts';

const WORDS = ['Register-ScheduledTask -', 'DeleteTask(', 'CreateFolder(', 'DeleteFolder('];

interface Source {
  readonly file: string;
  readonly text: string;
}

function sources(): Source[] {
  return shippedSources().map((file) => ({ file: fromExtensionRoot(file), text: fs.readFileSync(file, 'utf8') }));
}

/** Which of the words each source's string literals spell — only the sources that spell one. */
function spellers(all: readonly Source[]): Record<string, string[]> {
  const out: Record<string, string[]> = {};
  for (const source of all) {
    const literals = stringLiteralsOf(source.text);
    const hits = WORDS.filter((word) => literals.some((literal) => literal.includes(word)));
    if (hits.length > 0) {
      out[source.file] = hits;
    }
  }

  return out;
}

test('Register-ScheduledTask has one home — and the home spells all four (the scan is alive)', () => {
  const found = spellers(sources());
  assert.deepEqual(Object.keys(found), [HOME], `Register-ScheduledTask spelt outside scheduler/registerScript.ts: ${Object.keys(found).filter((f) => f !== HOME).join(', ')}`);
  assert.deepEqual(found[HOME], WORDS);
});

test('the scan finds a planted second registration — in a literal, a template and over lines — and ignores a comment', () => {
  const planted: Source[] = [
    { file: 'src/panel/x.ts', text: "const s = 'Register-ScheduledTask -Xml $x';" },
    { file: 'src/windowsTasks/y.ts', text: 'const t = `try { $folder.DeleteTask(\'${name}\', 0) }`;' },
    { file: 'src/windowsTasks/z.ts', text: "const u = [\n  '$service.GetFolder(',\n  \"').CreateFolder('\",\n];" },
    { file: 'src/windowsTasks/w.ts', text: '// Register-ScheduledTask and DeleteFolder( are the peer\'s\nconst v = 1;' },
  ];
  assert.deepEqual(spellers(planted), {
    'src/panel/x.ts': ['Register-ScheduledTask -'],
    'src/windowsTasks/y.ts': ['DeleteTask('],
    'src/windowsTasks/z.ts': ['CreateFolder('],
  });
});
