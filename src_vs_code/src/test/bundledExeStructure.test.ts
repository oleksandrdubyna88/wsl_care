import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import { test } from 'node:test';

import { RUN_READ_VERBS, VERBS } from '../client/verbs';
import { fromExtensionRoot, shippedSources } from './support/paths';
import { importsOf, stringLiteralsOf } from './support/sourceScan';

/**
 * The bundled exe's structural guarantees (E7.S5a, research/PLAN_bundle_windows_binary.md §3.7, §4), over the SHIPPED sources:
 *
 * 1. ONE module names the exe — `src/client/WindowsCareClient.ts` — as a path segment (`'wsl-care.exe'`, or a path ending in
 *    it); a sentence that merely mentions it (a field-map note) is no path;
 * 2. the exe never reaches the elevated or root modules: nothing under `src/windowsTime/`, `src/root/` or `src/powershell/`
 *    imports the client or the bundled row — it is never run elevated, from a scheduled task or as SYSTEM;
 * 3. the client starts through the runner it is GIVEN — it imports no process API, no runner selection.
 *
 * Each scan has a planted companion that proves it finds a violation.
 */

const CLIENT = 'src/client/WindowsCareClient.ts';
const NEVER_REACHED_FROM = ['src/windowsTime/', 'src/root/', 'src/powershell/'];

interface Source {
  readonly file: string;
  readonly text: string;
}

function sources(): Source[] {
  return shippedSources().map((file) => ({ file: fromExtensionRoot(file), text: fs.readFileSync(file, 'utf8') }));
}

/** Whether a string literal names the exe as a path segment. */
function namesExe(literal: string): boolean {
  return literal === 'wsl-care.exe' || /[\\/]wsl-care\.exe$/i.test(literal);
}

function exeNamers(all: readonly Source[]): string[] {
  return all.filter((s) => stringLiteralsOf(s.text).some(namesExe)).map((s) => s.file);
}

test('only WindowsCareClient names the bundled exe as a path — and it does (the scan is alive)', () => {
  assert.deepEqual(exeNamers(sources()), [CLIENT]);
});

test('the exe-name scan finds a planted path, and ignores a sentence that mentions the exe', () => {
  assert.deepEqual(exeNamers([{ file: 'planted.ts', text: "const exe = path.join(root, 'bin', 'wsl-care.exe');" }]), ['planted.ts']);
  assert.deepEqual(exeNamers([{ file: 'planted.ts', text: "const exe = 'C:\\\\tools\\\\wsl-care.exe';" }]), ['planted.ts']);
  assert.deepEqual(exeNamers([{ file: 'prose.ts', text: "const note = 'read by wsl-care.exe on the Windows side (E7.S3)';" }]), []);
});

// ---- WR-E1 (PLAN_windows_runner.md §6 E1): the STAGED exe is named only by WindowsCareClient, too ----

test('the staged exe is named only by WindowsCareClient: no module but the client builds a path ending in wsl-care.exe — the answers module and the verbs included', () => {
  const namers = exeNamers(sources());
  assert.deepEqual(namers, [CLIENT]);
  for (const module of ['src/client/windowsTasks.ts', 'src/client/verbs.ts', 'src/client/runReadChecks.ts']) {
    assert.ok(sources().some((s) => s.file === module), `the scan reads ${module}`);
    assert.equal(namers.includes(module), false, module);
  }
});

test('the exe-name scan finds a planted STAGED path — joined from a digest, a template over the staging folder, a concatenation — and ignores a folder name', () => {
  const planted: Source[] = [
    { file: 'src/windowsTasks/flow.ts', text: "const exe = path.win32.join(stagingRoot, digest, 'wsl-care.exe');" },
    { file: 'src/windowsTasks/other.ts', text: 'const exe = `${stagingRoot}\\\\${digest}\\\\wsl-care.exe`;' },
    { file: 'src/windowsTasks/third.ts', text: "const exe = stagingRoot + '/' + digest + '/wsl-care.exe';" },
    { file: 'src/windowsTasks/folder.ts', text: 'const folder = path.win32.join(stagingRoot, digest);' },
  ];
  assert.deepEqual(exeNamers(planted), ['src/windowsTasks/flow.ts', 'src/windowsTasks/other.ts', 'src/windowsTasks/third.ts']);
});

function reachesBundled(source: Source): boolean {
  return importsOf(source.text).some((spec) => spec.includes('WindowsCareClient') || spec.includes('/bundled/'));
}

test('the exe never reaches the elevated or root modules: nothing under windowsTime/, root/ or powershell/ imports the client or the row', () => {
  const offending = sources().filter((s) => NEVER_REACHED_FROM.some((prefix) => s.file.startsWith(prefix)) && reachesBundled(s)).map((s) => s.file);
  assert.deepEqual(offending, []);
  assert.equal(reachesBundled({ file: 'src/root/planted.ts', text: "import { WindowsCareClient } from '../client/WindowsCareClient';" }), true, 'the import scan finds a planted one');
});

test('the client starts through the runner it is given: no process API, no runner selection, no Windows-Time runner', () => {
  const client = sources().find((s) => s.file === CLIENT)?.text ?? '';
  const imports = importsOf(client);
  assert.ok(imports.length > 0, 'the client was read');
  assert.deepEqual(imports.filter((spec) => /child_process|runnerSelection|windowsTime|powershell/.test(spec)), []);
  const code = client.replace(/\/\*[\s\S]*?\*\//g, '').replace(/\/\/.*$/gm, '');
  assert.doesNotMatch(code, /runnerFor\(|spawnRunner|childProcess/, 'it never picks a runner of its own');
});

// ---- E19.S2b (todo/PLAN_disk_hygiene_windows_E19.md decision 12): the client may read `status` and `--version`, and nothing else ----

const ALLOWED_VERBS: readonly string[] = ['status', 'version'];

/**
 * Every daemon word the closed verb tables spell — the words the client must not write itself. `status` alone is left out: it is also
 * the outcome's own tag (`verb: 'status'`), a name and no argv; the argv it would be is `VERBS.status`, which the first scan holds.
 */
const DAEMON_WORDS: ReadonlySet<string> = new Set([...Object.values(VERBS), ...Object.values(RUN_READ_VERBS)].flat().filter((word) => word !== 'status'));

function withoutComments(text: string): string {
  return text.replace(/\/\*[\s\S]*?\*\//g, '').replace(/\/\/.*$/gm, '');
}

/** The names the source takes from the closed verb table: `VERBS.<name>`. */
function verbsTaken(text: string): string[] {
  return [...withoutComments(text).matchAll(/\bVERBS\.(\w+)/g)].map((m) => m[1] ?? '');
}

/** The daemon words the source spells as string literals of its own. */
function daemonWordsSpelled(text: string): string[] {
  return stringLiteralsOf(text).filter((literal) => DAEMON_WORDS.has(literal));
}

/** What the source takes from `./verbs` besides the table itself. */
function otherVerbImports(text: string): string[] {
  return [...text.matchAll(/import\s*\{([^}]*)\}\s*from\s*'\.\/verbs'/g)].flatMap((m) => (m[1] ?? '').split(',').map((name) => name.trim())).filter((name) => name !== '' && name !== 'VERBS');
}

test('the client names no other verb than status and --version', () => {
  const client = sources().find((s) => s.file === CLIENT)?.text ?? '';
  assert.ok(verbsTaken(client).length > 0, 'the scan is alive: the client takes its tails from VERBS');
  assert.deepEqual([...new Set(verbsTaken(client))].sort(), [...ALLOWED_VERBS].sort(), 'exactly status and version');
  assert.deepEqual(daemonWordsSpelled(client), [], 'it spells no daemon word itself — only verbs.ts does');
  assert.deepEqual(otherVerbImports(client), [], 'and takes no run-read table, no tail builder');
});

test('the verb scan finds a planted second verb, a planted literal and a planted run-read import', () => {
  assert.deepEqual(verbsTaken("const a = VERBS.status; const b = VERBS.preview; // VERBS.doctor"), ['status', 'preview']);
  assert.deepEqual(daemonWordsSpelled("const a = ['preview', '--all'];"), ['preview', '--all']);
  assert.deepEqual(otherVerbImports("import { VERBS, runReadTail } from './verbs';"), ['runReadTail']);
});
