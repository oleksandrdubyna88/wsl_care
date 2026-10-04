import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';
import * as ts from 'typescript';

import { INSTALL_COMMAND, INSTALL_PREREQUISITES } from '../install/installCommand';
import { BUNDLE, EXTENSION_ROOT } from './support/paths';
import { stringLiteralsAt, stringLiteralsOf } from './support/sourceScan';

/**
 * What SHIPS (`dist/extension.js`, the one file the extension runs) spells root argv words in ONE place (plan §15f #2,
 * §15j M1, §15k #10). esbuild, unminified, heads every module's code with `// src/<path>.ts`; the bundle is PARTITIONED by
 * those headers, and:
 *
 * - the ROOT REGION (`// src/root/rootCall.ts`) must exist — a missing marker, or a minified bundle with none, is a finding
 *   — and its string literals must EQUAL an exact expected set, written out here (an extra word in the root module is a
 *   finding, as a missing one is);
 * - every OTHER region keeps the full forbidden set: the multi-character flags of a root or mutating call (`-u`, `--user`,
 *   `--timer`, `--confirm`, `--manual`, `--preview`, `--detach`, `--only`, `--stop`, `--request`), `config` as an argv
 *   word, and the word `root` — prose about root only as an exact literal of `root/rootFailureText.ts`, as `sudo` is allowed
 *   only in the install command;
 * - `--timer`, `--user` and `config` are forbidden EVERYWHERE, the root region included.
 *
 * Bare words (`-`, `act`, `collect`) are deliberately NOT matched outside the root region (§15k #10: they occur in
 * ordinary code and prose): they are held by the import graph (`structure.test.ts`: only the cleanup controller imports
 * the root module) and by the root region's exact-literal equality. Every finding has a planted-instance companion.
 */

interface Forbidden {
  readonly name: string;
  readonly matches: (literal: string) => boolean;
  /** How the word would be planted: as an argv element, or inside a longer string. Each is planted on its own. */
  readonly plants: readonly string[];
}

/** A backtick, spelled without one — the planted template is built as text (TypeScript doctrine §2). */
const BACKTICK = String.fromCharCode(96);

const ROOT_MARKER = 'src/root/rootCall.ts';

function flag(word: string): (literal: string) => boolean {
  const escaped = word.replace(/[-]/g, '\\-');
  return (literal) => new RegExp(`(^|\\s)${escaped}(\\s|=|$)`).test(literal);
}

/** Forbidden in EVERY region, the root one included. */
const EVERYWHERE: readonly Forbidden[] = [
  { name: '--user', matches: flag('--user'), plants: ['--user', '--user=x'] },
  { name: '--timer', matches: flag('--timer'), plants: ['--timer', 'collect --timer'] },
  { name: 'config (an argv word; config set / config reset)', matches: (s) => /(^|\s)config(\s|$)/.test(s), plants: ['config', 'config set dryRun false', 'config reset dryRun'] },
];

/** Forbidden outside the root region. `root` is the one prose word, allowed as an exact literal of the failure-text module. */
const OUTSIDE_ROOT: readonly Forbidden[] = [
  ...EVERYWHERE,
  { name: '-u (another user)', matches: flag('-u'), plants: ['-u', '-u someone'] },
  { name: 'root', matches: (s) => /\broot\b/i.test(s) && !rootProse().includes(s), plants: ['root', 'run as Root'] },
  ...['--confirm', '--manual', '--preview', '--detach', '--only', '--stop', '--request'].map((word) => ({ name: word, matches: flag(word), plants: [word, `act A4 ${word} x`] })),
];

/**
 * The root region's literals, EXACTLY — the oracle of `rootCall.ts`, written out independently: the argv words of the
 * five ops, the comma that joins the ids, A4 (the one id bound to a shown list), `confirm` (the op that pipes it), the
 * empty string and the newline that build the stdin lines.
 */
const ROOT_LITERALS = ['-u', 'root', 'act', 'collect', '--preview', '--confirm', '--manual', '--detach', '--only', '-', '--stop', '--json', ',', 'A4', 'confirm', '', '\n'];

/** The exact literals of the failure-text module that spell "root" — the only root prose any other region may carry. */
function rootProse(): string[] {
  const source = fs.readFileSync(path.join(EXTENSION_ROOT, 'src', 'root', 'rootFailureText.ts'), 'utf8');
  return stringLiteralsOf(source).filter((literal) => /\broot\b/i.test(literal));
}

function bundleText(): string {
  assert.ok(fs.existsSync(BUNDLE), `${BUNDLE} does not exist — npm test bundles before it tests`);
  return fs.readFileSync(BUNDLE, 'utf8');
}

interface Region {
  readonly name: string;
  readonly start: number;
}

/** The bundle cut at every column-0 `// …` header esbuild writes; what comes before the first is the preamble. */
function regions(text: string): Region[] {
  const found: Region[] = [{ name: '(preamble)', start: 0 }];
  for (const match of text.matchAll(/^\/\/ (\S[^\r\n]*)$/gm)) {
    found.push({ name: match[1] ?? '', start: match.index ?? 0 });
  }

  return found;
}

function regionAt(all: readonly Region[], offset: number): string {
  return [...all].reverse().find((r) => r.start <= offset)?.name ?? '(preamble)';
}

/** Every finding of the scan, as `region: what`. */
function findings(text: string): string[] {
  const all = regions(text);
  const out: string[] = [];
  if (!all.some((r) => r.name === ROOT_MARKER)) {
    out.push(`${ROOT_MARKER}: the root region's marker is missing (a minified bundle, or the module gone)`);
  }
  const rootLiterals = new Set<string>();
  for (const literal of stringLiteralsAt(text, ts.ScriptKind.JS)) {
    const region = regionAt(all, literal.start);
    const set = region === ROOT_MARKER ? EVERYWHERE : OUTSIDE_ROOT;
    for (const word of set.filter((f) => f.matches(literal.text))) {
      out.push(`${region}: ${word.name}`);
    }
    if (region === ROOT_MARKER) {
      rootLiterals.add(literal.text);
    }
  }

  return [...new Set([...out, ...rootSetFindings(rootLiterals, all)])];
}

function rootSetFindings(literals: ReadonlySet<string>, all: readonly Region[]): string[] {
  if (!all.some((r) => r.name === ROOT_MARKER)) {
    return [];
  }
  const extra = [...literals].filter((l) => !ROOT_LITERALS.includes(l));
  const missing = ROOT_LITERALS.filter((l) => !literals.has(l));

  return [...extra.map((l) => `${ROOT_MARKER}: an unexpected literal ${JSON.stringify(l)}`), ...missing.map((l) => `${ROOT_MARKER}: the expected literal ${JSON.stringify(l)} is missing`)];
}

/** A copy of `text` with `line` inserted right after the header of `region`. */
function plantedIn(text: string, region: string, line: string): string {
  const header = `// ${region}\n`;
  const at = text.indexOf(header);
  assert.ok(at >= 0, `the bundle has no region ${region}`);
  return text.slice(0, at + header.length) + line + text.slice(at + header.length);
}

test('the shipped bundle: the root region holds exactly its literals, and no other region spells a root word', () => {
  assert.deepEqual(findings(bundleText()), []);
});

test('the scan reads the real bundle: the regions are there, the root region and the cleanup controller among them', () => {
  const names = regions(bundleText()).map((r) => r.name);
  for (const module of ['src/extension.ts', 'src/client/WslCareClient.ts', ROOT_MARKER, 'src/root/cleanupController.ts']) {
    assert.ok(names.includes(module), `the bundle has no region ${module}`);
  }
  const literals = stringLiteralsOf(bundleText(), ts.ScriptKind.JS);
  for (const word of ['--exec', '/opt/wsl-care/bin/wsl-care', 'status', 'preview', 'doctor', '--version', '--list', '--running', '-u', '--detach', 'collect']) {
    assert.ok(literals.includes(word), `the bundle should spell ${word}`);
  }
});

for (const word of OUTSIDE_ROOT) {
  for (const plant of word.plants) {
    test(`a planted ${JSON.stringify(plant)} outside the root region is found as ${word.name}, as an argv element and inside a template`, () => {
      const text = bundleText();
      const argv = '\nvar plantedArgv = [' + JSON.stringify(plant) + '];\n';
      assert.deepEqual(findings(text + argv), [`Annotate the CommonJS export names for ESM import in node:: ${word.name}`], 'as a string literal');
      const template = '\nvar plantedTemplate = ' + BACKTICK + 'wsl-care ' + plant + ' $' + '{x}' + BACKTICK + ';\n';
      assert.deepEqual(findings(text + template), [`Annotate the CommonJS export names for ESM import in node:: ${word.name}`], 'inside a template');
    });
  }
}

test('a planted -u in ANOTHER module\'s region is found there — the cleanup controller may not spell it either', () => {
  const planted = plantedIn(bundleText(), 'src/root/cleanupController.ts', 'var stray = ["-u", "root"];\n');
  assert.deepEqual(findings(planted), ['src/root/cleanupController.ts: -u (another user)', 'src/root/cleanupController.ts: root']);
});

test('a planted --timer IN the root region is found — forbidden everywhere, the root module included', () => {
  const planted = plantedIn(bundleText(), ROOT_MARKER, 'var timer = ["collect", "--timer"];\n');
  assert.deepEqual(findings(planted), [`${ROOT_MARKER}: --timer`, `${ROOT_MARKER}: an unexpected literal "--timer"`]);
});

test('an extra literal in the root region is found — its set is exact (a --volume would put names on the command line)', () => {
  const planted = plantedIn(bundleText(), ROOT_MARKER, 'var extra = ["--volume"];\n');
  assert.deepEqual(findings(planted), [`${ROOT_MARKER}: an unexpected literal "--volume"`]);
});

test('a stripped root marker is found — and its words then fall into the region before it, where they are findings too', () => {
  const stripped = bundleText().replace(`// ${ROOT_MARKER}\n`, '');
  const found = findings(stripped);
  assert.ok(found.includes(`${ROOT_MARKER}: the root region's marker is missing (a minified bundle, or the module gone)`), found.join('\n'));
  assert.ok(found.some((f) => f.endsWith(': -u (another user)')), found.join('\n'));
});

test('a bundle with no headers at all (minified) is a finding: the root marker is missing', () => {
  const minified = bundleText().replace(/^\/\/ [^\r\n]*$/gm, '');
  assert.ok(findings(minified).includes(`${ROOT_MARKER}: the root region's marker is missing (a minified bundle, or the module gone)`));
});

test('root prose outside the root region is allowed only as an exact literal of rootFailureText.ts', () => {
  const prose = rootProse();
  assert.ok(prose.length >= 2, `the failure-text module spells root prose (${prose.length} literals)`);
  const text = bundleText();
  assert.deepEqual(findings(text + '\nvar allowed = ' + JSON.stringify(prose[0]) + ';\n'), [], 'an exact literal of the table');
  assert.deepEqual(findings(text + '\nvar edited = ' + JSON.stringify(`${prose[0] ?? ''} now`) + ';\n'), ['Annotate the CommonJS export names for ESM import in node:: root'], 'an edited copy is not');
});

test('the near misses are not findings: words that merely contain the letters, and the bare words outside the root region', () => {
  assert.deepEqual(findings(bundleText() + '\nvar a = ["--users-guide", "rooted", "configure", "configuration", "-ux", "manually", "timeout", "act", "collect", "-", "--onlyx", "preview"];\n'), []);
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
