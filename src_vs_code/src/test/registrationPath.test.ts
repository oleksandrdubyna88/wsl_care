import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import { test } from 'node:test';

import { fromExtensionRoot, shippedSources } from './support/paths';
import { TASK_RUN } from './support/noRealWsl';
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

/** The shipped sources whose string literals start a task — the tripwire's own pattern (`TASK_RUN`, `.Run(` and `.RunEx(`, any case), one pattern in two places. */
function starters(all: readonly Source[]): string[] {
  return all.filter((source) => stringLiteralsOf(source.text).some((literal) => TASK_RUN.test(literal))).map((source) => source.file).sort();
}

test('the start of a task has one home — scheduler/runScript.ts — found by the tripwire\'s own TASK_RUN', () => {
  assert.deepEqual(starters(sources()), ['src/scheduler/runScript.ts'], '.Run( spelt outside scheduler/runScript.ts');
});

test('the start scan finds a planted second spelling — plain, mixed case, over lines — and ignores a comment', () => {
  const planted: Source[] = [
    { file: 'src/windowsTasks/x.ts', text: "const s = '$t.Run($null)';" },
    { file: 'src/windowsTasks/y.ts', text: 'const t = `$task.rUnEx(${a}, 0)`;' },
    { file: 'src/windowsTasks/z.ts', text: "const u = [\n  'a.run',\n  '(1)',\n];\nconst v = \"$x.run ($null)\";" },
    { file: 'src/windowsTasks/w.ts', text: '// the task\'s $t.Run($null) belongs to the peer\nconst w = 1;' },
  ];
  assert.deepEqual(starters(planted), ['src/windowsTasks/x.ts', 'src/windowsTasks/y.ts', 'src/windowsTasks/z.ts']);
});
