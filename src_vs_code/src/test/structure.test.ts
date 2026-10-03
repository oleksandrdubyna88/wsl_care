import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import { test } from 'node:test';

import { VERB_NAMES, VERBS } from '../client/verbs';
import { fromExtensionRoot, shippedSources } from './support/paths';
import { importsOf, isChildProcess, stringLiteralsOf } from './support/sourceScan';

/**
 * The structural guarantees of plan §15g m1, over the SHIPPED sources (everything under `src/` but `src/test/`), read
 * by the TypeScript parser: ONE module starts processes, ONE module builds `wsl.exe` argv, ONE module holds the verbs.
 * Each scan has a known-instance companion (it still finds the sanctioned site) and a planted-instance companion (it
 * finds a violation in a fixture) — `common.testing`: a structural test that matches nothing passes forever.
 */

const RUNNER = 'src/process/runner.ts';
const CLIENT = 'src/client/WslCareClient.ts';
const VERB_MODULE = 'src/client/verbs.ts';

/** The words of a `wsl.exe` call and the daemon's path: only the client may spell them. */
const WSL_ARGV_WORDS = ['-d', '--cd', '--exec', '--list', '--quiet', '--running', '-l', '-v', '/opt/wsl-care/bin/wsl-care'];

/** The option words of the daemon verbs: only the verb module may spell them. */
const VERB_WORDS = ['--json', '--all', '--version'];

interface Source {
  readonly file: string;
  readonly text: string;
}

function sources(): Source[] {
  return shippedSources().map((file) => ({ file: fromExtensionRoot(file), text: fs.readFileSync(file, 'utf8') }));
}

function importersOfChildProcess(all: readonly Source[]): string[] {
  return all.filter((s) => importsOf(s.text).some(isChildProcess)).map((s) => s.file);
}

function spellers(all: readonly Source[], words: readonly string[]): Record<string, string[]> {
  const out: Record<string, string[]> = {};
  for (const source of all) {
    const hits = stringLiteralsOf(source.text).filter((literal) => words.includes(literal));
    if (hits.length > 0) {
      out[source.file] = [...new Set(hits)].sort();
    }
  }

  return out;
}

test('the scan reads the shipped sources and nothing under src/test', () => {
  const files = sources().map((s) => s.file);
  assert.ok(files.includes(RUNNER) && files.includes(CLIENT) && files.includes(VERB_MODULE), files.join(', '));
  assert.ok(files.every((f) => !f.startsWith('src/test/')));
});

test('only the runner imports child_process — in any form', () => {
  assert.deepEqual(importersOfChildProcess(sources()), [RUNNER]);
});

test('the import scan finds every spelling of a child_process import in a planted fixture, and ignores a mention', () => {
  const planted: readonly string[] = [
    "import { spawn } from 'node:child_process';",
    'import * as cp from "child_process";',
    "import cp from 'node:child_process';",
    "import cp = require('child_process');",
    "const cp = require('node:child_process');",
    "const later = import('node:child_process');",
    "export { spawn } from 'child_process';",
    "const cp = require(\n  'child_process'\n);",
  ];
  for (const text of planted) {
    assert.ok(importsOf(text).some(isChildProcess), text);
  }
  assert.equal(importsOf("// we never import 'node:child_process' here\nconst s = 'node:child_process';").some(isChildProcess), false);
});

test('only the client spells wsl.exe argv words or the daemon path', () => {
  assert.deepEqual(Object.keys(spellers(sources(), WSL_ARGV_WORDS)), [CLIENT]);
});

test('the client still spells each of them — the argv scan is alive', () => {
  assert.deepEqual(spellers(sources(), WSL_ARGV_WORDS)[CLIENT], [...WSL_ARGV_WORDS].sort());
});

test('only the verb module spells the daemon verbs\' option words, and it spells all three', () => {
  assert.deepEqual(spellers(sources(), VERB_WORDS), { [VERB_MODULE]: [...VERB_WORDS].sort() });
});

test('the literal scan finds a planted argv word outside the client, in a string, a template and over lines', () => {
  const planted: Source[] = [
    { file: 'src/panel/elsewhere.ts', text: "const args = ['-d', distro, '--exec', path];" },
    { file: 'src/other.ts', text: 'const a = [\n  "--cd",\n  `/opt/wsl-care/bin/wsl-care`,\n];' },
    { file: 'src/verbsAgain.ts', text: "const extra = ['status', '--json'];" },
  ];
  assert.deepEqual(spellers(planted, WSL_ARGV_WORDS), { 'src/panel/elsewhere.ts': ['--exec', '-d'], 'src/other.ts': ['--cd', '/opt/wsl-care/bin/wsl-care'] });
  assert.deepEqual(spellers(planted, VERB_WORDS), { 'src/verbsAgain.ts': ['--json'] });
  assert.deepEqual(spellers([{ file: 'src/x.ts', text: '// pass --exec and -d here\nconst s = 1;' }], WSL_ARGV_WORDS), {}, 'a comment is not an argv');
});

test('the verb union is exactly the four read-only verbs of plan §15f #5, each with its one tail', () => {
  assert.deepEqual([...VERB_NAMES], ['status', 'preview', 'doctor', 'version']);
  assert.deepEqual(Object.keys(VERBS), [...VERB_NAMES]);
  assert.deepEqual(VERBS, { status: ['status', '--json'], preview: ['preview', '--all', '--json'], doctor: ['doctor', '--json'], version: ['--version'] });
});
