import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';
import * as ts from 'typescript';

import { INSTALL_COMMAND, INSTALL_PREREQUISITES } from '../install/installCommand';
import { BUNDLE, EXTENSION_ROOT } from './support/paths';
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

// ---- E5.S3: the one privileged WORD the bundle carries is typed for the person, never run ----
//
// *Install daemon* shows and types `… | sudo sh -s -- --version <MIN>` (install/installCommand.ts) and lists "sudo
// rights" among the prerequisites. That is the only `sudo` the bundle may spell, and none of the words above (-u,
// --user, root, --timer, --confirm, --manual, config) is part of it — so the forbidden set stays as it was, with no
// exception. What keeps the command from ever being RUN is structural: the install modules import neither the runner
// nor child_process, and the command reaches a terminal only through sendText(command, false) (structure.test.ts,
// installDaemon.test.ts).

/** The sudo-carrying literals the install-command module itself spells: its command template part and one prerequisite. */
function installerSudoLiterals(): string[] {
  const source = fs.readFileSync(path.join(EXTENSION_ROOT, 'src', 'install', 'installCommand.ts'), 'utf8');
  return stringLiteralsOf(source).filter((literal) => /\bsudo\b/.test(literal));
}

function sudoOutsideTheInstaller(text: string): string[] {
  const allowed = installerSudoLiterals();
  return stringLiteralsOf(text, ts.ScriptKind.JS).filter((literal) => /\bsudo\b/.test(literal) && !allowed.includes(literal));
}

test('sudo appears in the bundle only inside the install command the extension types and its prerequisite line', () => {
  assert.deepEqual(sudoOutsideTheInstaller(bundleText()), []);
});

test('the sudo scan is alive: the real bundle does carry the install command\'s sudo, and a planted one elsewhere is found', () => {
  const literals = stringLiteralsOf(bundleText(), ts.ScriptKind.JS);
  assert.ok(literals.some((l) => /\bsudo\b/.test(l) && INSTALL_COMMAND.includes(l)), 'the install command ships');
  assert.ok(INSTALL_PREREQUISITES.some((p) => /\bsudo\b/.test(p) && literals.includes(p)), 'its prerequisite line ships');
  assert.equal(installerSudoLiterals().length, 2, 'the module spells sudo in exactly its command template and one prerequisite');
  assert.deepEqual(sudoOutsideTheInstaller(bundleText() + '\nvar plantedArgv = ["sudo", "/opt/wsl-care/bin/wsl-care", "status"];\n'), ['sudo']);
});

test('the bundle carries the build stamp of the version it was built for (scripts/bundle.mjs)', () => {
  const version = (JSON.parse(fs.readFileSync(path.join(EXTENSION_ROOT, 'package.json'), 'utf8')) as { version: string }).version;
  assert.deepEqual(stringLiteralsOf(bundleText(), ts.ScriptKind.JS).filter((l) => l.startsWith('wsl-care-build ')), [`wsl-care-build ${version}`]);
});
