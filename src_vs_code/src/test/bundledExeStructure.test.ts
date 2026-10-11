import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import { test } from 'node:test';

import { fromExtensionRoot, shippedSources } from './support/paths';
import { importsOf, stringLiteralsOf } from './support/sourceScan';

/**
 * The bundled exe's structural guarantees (E7.S5a, todo/PLAN_bundle_windows_binary.md §3.7, §4), over the SHIPPED sources:
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
