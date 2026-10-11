import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import { test } from 'node:test';
import * as ts from 'typescript';

import { fromExtensionRoot, shippedSources } from './support/paths';
import { importsOf, stringLiteralsOf } from './support/sourceScan';

/**
 * ONE resolver of the elevated PowerShell (PLAN_pwsh_engine_windows_time.md D1): the probe that resolves and verifies
 * PowerShell 7 and the functions that choose the engine live in `src/powershell/elevatedEngine.ts` alone. *Enable <GPU>* and
 * the Windows Time launcher import them; a second copy would drift (reuse-first). Each scan has a companion that proves it
 * still finds the sanctioned site.
 */

const ENGINE_MODULE = 'src/powershell/elevatedEngine.ts';

/** The probe's words: who spells them builds a probe of their own. */
const PROBE_WORDS = ['Get-AppxPackage', 'Get-AuthenticodeSignature', 'CN=Microsoft Corporation'];

/** The resolver's functions: who declares them chooses an engine of their own. */
const RESOLVER_FUNCTIONS = ['storePwsh', 'msiPwsh', 'engineOf', 'elevatedPowerShellFor', 'pwshFactsFromTags'];

interface Source {
  readonly file: string;
  readonly text: string;
}

function sources(): Source[] {
  return shippedSources().map((file) => ({ file: fromExtensionRoot(file), text: fs.readFileSync(file, 'utf8') }));
}

function probeSpellers(all: readonly Source[]): string[] {
  return all.filter((s) => stringLiteralsOf(s.text).some((literal) => PROBE_WORDS.some((word) => literal.includes(word)))).map((s) => s.file);
}

function declaredFunctions(text: string): string[] {
  const file = ts.createSourceFile('x.ts', text, ts.ScriptTarget.Latest, true, ts.ScriptKind.TS);
  const names: string[] = [];
  const visit = (node: ts.Node): void => {
    if (ts.isFunctionDeclaration(node) && node.name !== undefined) {
      names.push(node.name.text);
    }
    ts.forEachChild(node, visit);
  };
  visit(file);

  return names;
}

function resolverDeclarers(all: readonly Source[]): Record<string, string[]> {
  return Object.fromEntries(RESOLVER_FUNCTIONS.map((name) => [name, all.filter((s) => declaredFunctions(s.text).includes(name)).map((s) => s.file)]));
}

test('only the engine module spells the PowerShell 7 probe — and it spells every word of it (the scan is alive)', () => {
  const all = sources();
  assert.deepEqual(probeSpellers(all), [ENGINE_MODULE]);
  const engine = all.find((s) => s.file === ENGINE_MODULE)?.text ?? '';
  assert.ok(PROBE_WORDS.every((word) => stringLiteralsOf(engine).some((literal) => literal.includes(word))));
});

test('only the engine module declares the resolver\'s functions', () => {
  assert.deepEqual(resolverDeclarers(sources()), Object.fromEntries(RESOLVER_FUNCTIONS.map((name) => [name, [ENGINE_MODULE]])));
});

test('the declaration scan finds a planted copy of a resolver function, and ignores a mention in a comment', () => {
  assert.deepEqual(declaredFunctions('// function storePwsh() {}\nexport function storePwsh(): void {}'), ['storePwsh']);
  assert.deepEqual(declaredFunctions('const storePwsh = 1; // function engineOf'), []);
});

test('R2: the shared PowerShell modules import nothing from the stories that use them — launcher <- engine <- {gpu, windowsTime}, no cycle', () => {
  const shared = sources().filter((s) => s.file.startsWith('src/powershell/'));
  assert.ok(shared.some((s) => s.file === 'src/powershell/elevatedLauncher.ts') && shared.some((s) => s.file === ENGINE_MODULE));
  const offending = shared.flatMap((s) => importsOf(s.text).filter((spec) => /\/(windowsTime|gpu)\//.test(spec)).map((spec) => `${s.file} -> ${spec}`));
  assert.deepEqual(offending, []);
  const launcher = shared.find((s) => s.file === 'src/powershell/elevatedLauncher.ts')?.text ?? '';
  assert.deepEqual(importsOf(launcher).filter((spec) => spec.includes('elevatedEngine')), [], 'the launcher does not import the engine');
});
