import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import { test } from 'node:test';
import * as ts from 'typescript';

import { BUNDLE } from './support/paths';
import { stringLiteralsOf } from './support/sourceScan';

/**
 * What SHIPS carries no root call path (plan §15f #5, §15g m1): `dist/extension.js` — the bundle esbuild writes, the
 * one file the extension runs — is parsed and every string it spells is checked for the words of a privileged or
 * mutating daemon call. Over the bundle rather than the sources, because the bundle is what a user installs: a word that
 * arrived through a dependency, a generated file or a module nobody scanned is in it.
 *
 * Each word has planted-instance companions — the word put into a COPY of the real bundle is found, as an argv element
 * and inside a template — so none of the checks passes vacuously; and a known-instance companion proves the scan reads
 * the real bundle (it finds the verbs the extension does send).
 */

interface Forbidden {
  readonly name: string;
  readonly matches: (literal: string) => boolean;
  /** How the word would be planted: as an argv element, or inside a longer string. Each is planted on its own. */
  readonly plants: readonly string[];
}

/** A backtick, spelled without one — the planted template is built as text (TypeScript doctrine §2). */
const BACKTICK = String.fromCharCode(96);

const FORBIDDEN: readonly Forbidden[] = [
  { name: '-u (another user)', matches: (s) => /(^|\s)-u(\s|$)/.test(s), plants: ['-u', '-u someone'] },
  { name: '--user', matches: (s) => /(^|\s)--user(\s|=|$)/.test(s), plants: ['--user', '--user=x'] },
  { name: 'root', matches: (s) => /\broot\b/i.test(s), plants: ['root', 'run as Root'] },
  { name: '--timer', matches: (s) => s.includes('--timer'), plants: ['--timer', 'collect --timer'] },
  { name: '--confirm', matches: (s) => s.includes('--confirm'), plants: ['--confirm', 'act A4 --confirm'] },
  { name: '--manual', matches: (s) => s.includes('--manual'), plants: ['--manual'] },
  { name: 'config (an argv word; config set / config reset)', matches: (s) => /(^|\s)config(\s|$)/.test(s), plants: ['config', 'config set dryRun false', 'config reset dryRun'] },
];

function bundleText(): string {
  assert.ok(fs.existsSync(BUNDLE), `${BUNDLE} does not exist — npm test bundles before it tests`);
  return fs.readFileSync(BUNDLE, 'utf8');
}

function findings(text: string): string[] {
  const literals = stringLiteralsOf(text, ts.ScriptKind.JS);
  return FORBIDDEN.filter((f) => literals.some((literal) => f.matches(literal))).map((f) => f.name);
}

test('the shipped bundle spells no root, timer, confirm, manual or config word', () => {
  assert.deepEqual(findings(bundleText()), []);
});

test('the scan reads the real bundle: it finds the words the extension does send', () => {
  const literals = stringLiteralsOf(bundleText(), ts.ScriptKind.JS);
  for (const word of ['--exec', '/opt/wsl-care/bin/wsl-care', 'status', 'preview', 'doctor', '--version', '--list', '--running']) {
    assert.ok(literals.includes(word), `the bundle should spell ${word}`);
  }
});

for (const word of FORBIDDEN) {
  for (const plant of word.plants) {
    test(`a planted ${JSON.stringify(plant)} in a copy of the real bundle is found as ${word.name}, as an argv element and inside a template`, () => {
      const text = bundleText();
      const argv = '\nvar plantedArgv = [' + JSON.stringify(plant) + '];\n';
      assert.deepEqual(findings(text + argv), [word.name], 'as a string literal');
      const template = '\nvar plantedTemplate = ' + BACKTICK + 'wsl-care ' + plant + ' $' + '{x}' + BACKTICK + ';\n';
      assert.deepEqual(findings(text + template), [word.name], 'inside a template');
    });
  }
}

test('the near misses are not findings: words that merely contain the letters', () => {
  assert.deepEqual(findings('var a = ["--users-guide", "rooted", "configure", "configuration", "-ux", "manually", "timeout"];'), []);
});

test('no source map ships: no sourceMappingURL comment and no sourcesContent', () => {
  const text = bundleText();
  assert.equal(/sourceMappingURL=/.test(text), false);
  assert.equal(text.includes('sourcesContent'), false);
  assert.equal(fs.existsSync(`${BUNDLE}.map`), false);
});
