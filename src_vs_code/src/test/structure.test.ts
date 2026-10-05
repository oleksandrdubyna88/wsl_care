import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';
import * as ts from 'typescript';

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

test('only the verb module spells the daemon verbs\' option words (it spells all three) — and the root module its --json', () => {
  assert.deepEqual(spellers(sources(), VERB_WORDS), { [VERB_MODULE]: [...VERB_WORDS].sort(), 'src/root/rootCall.ts': ['--json'] });
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

// ---- E5.S3: Install daemon — ONE module builds the command, and nothing that builds it can start a process ----

const INSTALL_COMMAND_MODULE = 'src/install/installCommand.ts';
const INSTALL_FLOW_MODULES = ['src/install/installCommand.ts', 'src/install/installDaemon.ts', 'src/install/installUi.ts'];
const INSTALL_UI_MODULE = 'src/install/installUi.ts';

/** Fragments of the install command: only its module may spell a string holding one. */
const INSTALLER_FRAGMENTS = ['install.sh', 'raw.githubusercontent.com', 'sudo'];

function fragmentSpellers(all: readonly Source[]): string[] {
  const holds = (literal: string): boolean => INSTALLER_FRAGMENTS.some((f) => new RegExp(`(^|[^A-Za-z0-9])${f.split('.').join('[.]')}([^A-Za-z0-9]|$)`).test(literal));

  return all.filter((s) => stringLiteralsOf(s.text).some(holds)).map((s) => s.file);
}

/** Files that touch `.<member>` (a property access or a call through one), read with the parser. */
function accessorsOf(all: readonly Source[], member: string): string[] {
  return all.filter((s) => {
    let found = false;
    const visit = (node: ts.Node): void => {
      found ||= ts.isPropertyAccessExpression(node) && node.name.text === member;
      ts.forEachChild(node, visit);
    };
    visit(ts.createSourceFile('scan.ts', s.text, ts.ScriptTarget.ES2022, true, ts.ScriptKind.TS));
    return found;
  }).map((s) => s.file);
}

test('only the install-command module spells the installer, its URL or sudo — and it does (the scan is alive)', () => {
  assert.deepEqual(fragmentSpellers(sources()), [INSTALL_COMMAND_MODULE]);
});

test('the fragment scan finds a planted installer string elsewhere, in a template and over lines, and ignores a comment', () => {
  const planted: Source[] = [
    { file: 'src/panel/x.ts', text: "const c = 'curl https://raw.githubusercontent.com/x/y/main/install.sh';" },
    { file: 'src/y.ts', text: 'const c = [\n  `sudo sh -s -- ${v}`,\n];' },
    { file: 'src/z.ts', text: '// sudo install.sh is typed by installCommand\nconst s = 1;' },
    { file: 'src/w.ts', text: "const pseudo = 'pseudonym';" },
  ];
  assert.deepEqual(fragmentSpellers(planted), ['src/panel/x.ts', 'src/y.ts']);
});

test('the install flow imports no child_process and not the runner — it can type a command, never run one', () => {
  for (const file of INSTALL_FLOW_MODULES) {
    const text = sources().find((s) => s.file === file)?.text;
    assert.ok(text !== undefined, file);
    const imports = importsOf(text);
    assert.equal(imports.some(isChildProcess), false, file);
    assert.equal(imports.some((m) => m.includes('process/runner')), false, file);
  }
  assert.deepEqual(importsOf(sources().find((s) => s.file === INSTALL_COMMAND_MODULE)?.text ?? ''), ['../client/handshake'], 'the command module reads the version constant and nothing else');
});

test('only the install UI module opens a terminal (createTerminal), and it does', () => {
  assert.deepEqual(accessorsOf(sources(), 'createTerminal'), [INSTALL_UI_MODULE]);
  assert.deepEqual(accessorsOf([{ file: 'src/planted.ts', text: 'void vscode.window\n  .createTerminal({ shellPath: p });' }], 'createTerminal'), ['src/planted.ts'], 'the access scan finds a planted one over lines');
});

// ---- E6.S2: the root boundary (plan §15j M1, §15k #10) ----
//
// ONE module spells a root argv word, ONE module imports it, and nothing a webview runs or renders is anywhere near it.
// Matched as EXACT string literals (an argv element is one), read by the parser: a comment that names `--confirm` is no
// finding, a call spread over lines still is. The bare `-` (the stdin marker) is not scanned at source level — the client
// rightly spells it in `startsWith('-')` — and is held instead by the import graph below and by the bundle scan's exact
// literal set for the root region (§15k #10, deliberate).

const ROOT_CALL = 'src/root/rootCall.ts';
const CLEANUP_CONTROLLER = 'src/root/cleanupController.ts';

/** The root argv words (§15j M1) — only `rootCall.ts` may spell them. */
const ROOT_WORDS = ['-u', 'root', 'act', 'collect', '--preview', '--confirm', '--manual', '--detach', '--only', '--stop'];

/** Words NO module may spell, the root module included: the timer's mark, another user, the daemon's settings verb. */
const NEVER_WORDS = ['--timer', '--user', 'config'];

/** Every module a source imports by a RELATIVE specifier, resolved to `src/...ts` the way the structural tests name files. */
function resolvedImports(source: Source): string[] {
  const dir = path.posix.dirname(source.file);

  return importsOf(source.text).filter((m) => m.startsWith('.')).map((m) => `${path.posix.normalize(path.posix.join(dir, m))}.ts`);
}

function importersOf(all: readonly Source[], module: string): string[] {
  return all.filter((s) => resolvedImports(s).includes(module)).map((s) => s.file).sort();
}

/** Modules whose code a webview runs or whose output it renders, plus the poller and the install flow: none may touch root/. */
const FAR_FROM_ROOT = ['src/panel/', 'src/statusBar/', 'src/poll/', 'src/install/', 'src/state/', 'src/logsPage/'];

test('only rootCall.ts spells a root argv word — and it spells every one of them (the scan is alive)', () => {
  assert.deepEqual(spellers(sources(), ROOT_WORDS), { [ROOT_CALL]: [...ROOT_WORDS].sort() });
});

test('no module spells --timer, --user or config — the root module included', () => {
  assert.deepEqual(spellers(sources(), NEVER_WORDS), {});
});

test('the root-word scan finds a planted word outside the root module, in an array, a template and over lines, and ignores a comment', () => {
  const planted: Source[] = [
    { file: 'src/panel/x.ts', text: "const args = ['act', ids, '--confirm'];" },
    { file: 'src/poll/y.ts', text: 'const a = [\n  `-u`,\n  "root",\n];' },
    { file: 'src/root/rootCall.ts', text: "const t = ['collect', '--timer'];" },
    { file: 'src/z.ts', text: '// we never pass --confirm or -u root here\nconst s = "rooted";' },
  ];
  assert.deepEqual(spellers(planted, ROOT_WORDS), { 'src/panel/x.ts': ['--confirm', 'act'], 'src/poll/y.ts': ['-u', 'root'], 'src/root/rootCall.ts': ['collect'] });
  assert.deepEqual(spellers(planted, NEVER_WORDS), { 'src/root/rootCall.ts': ['--timer'] });
});

test('only the host-side cleanup controller imports rootCall.ts', () => {
  assert.deepEqual(importersOf(sources(), ROOT_CALL), [CLEANUP_CONTROLLER]);
});

test('no panel, status-bar, poller, install, store or Logs-page module imports anything under src/root/', () => {
  const offenders = sources().filter((s) => FAR_FROM_ROOT.some((dir) => s.file.startsWith(dir)) && resolvedImports(s).some((m) => m.startsWith('src/root/')));
  assert.deepEqual(offenders.map((s) => s.file), []);
  assert.ok(sources().some((s) => s.file.startsWith('src/panel/')), 'the scan sees the panel modules');
});

test('rootCall.ts imports only the client\'s argv builder, the verbs, the runner\'s types and the typed ids — no process API', () => {
  const source = sources().find((s) => s.file === ROOT_CALL);
  assert.ok(source !== undefined);
  assert.deepEqual(resolvedImports(source).sort(), ['src/client/WslCareClient.ts', 'src/client/verbs.ts', 'src/process/runner.ts', 'src/root/rootIds.ts']);
  assert.deepEqual(importsOf(source.text).filter((m) => !m.startsWith('.')), []);
});

test('the import-graph scan resolves relative specifiers and finds a planted import of the root module', () => {
  const planted: Source[] = [
    { file: 'src/panel/viewModel.ts', text: "import { callRoot } from '../root/rootCall';" },
    { file: 'src/root/cleanupController.ts', text: "import { rootRequest } from './rootCall';" },
    { file: 'src/other.ts', text: "const s = '../root/rootCall';" },
  ];
  assert.deepEqual(importersOf(planted, ROOT_CALL), ['src/panel/viewModel.ts', 'src/root/cleanupController.ts']);
});
