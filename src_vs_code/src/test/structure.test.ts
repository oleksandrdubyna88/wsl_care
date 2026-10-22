import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import { test } from 'node:test';
import * as ts from 'typescript';

import { VERB_NAMES, VERBS } from '../client/verbs';
import { fromExtensionRoot, shippedSources } from './support/paths';
import { importsOf, isChildProcess, resolveRelative, stringLiteralsOf } from './support/sourceScan';

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

// ---- WR-E1 (PLAN_windows_runner.md §3.8): the Windows runner's `tasks` verbs are spelt in the verb module alone ----

/** The words of `tasks render` / `tasks stage` / `tasks stage --remove`: only `verbs.ts` may spell them. */
const TASKS_WORDS = ['tasks', 'render', 'stage', '--remove'];

const WINDOWS_CLIENT_MODULES = ['src/client/WindowsCareClient.ts', 'src/client/windowsTasks.ts'];

test('only the verb module spells the words of the tasks verbs — and it spells all of them (the scan is alive)', () => {
  assert.deepEqual(spellers(sources(), TASKS_WORDS), { [VERB_MODULE]: [...TASKS_WORDS].sort() });
});

test('the tasks-word scan finds a planted argv outside the verb module, in an array, a template and over lines — the Windows client included — and ignores a comment', () => {
  const planted: Source[] = [
    { file: 'src/client/WindowsCareClient.ts', text: "const args = ['tasks', 'stage', '--json'];" },
    { file: 'src/panel/x.ts', text: 'const a = [\n  `tasks`,\n  "render",\n];' },
    { file: 'src/windowsTasks/flow.ts', text: "const remove = ['stage', '--remove'];" },
    { file: 'src/z.ts', text: '// tasks render and tasks stage --remove belong to verbs.ts\nconst s = "staged";' },
  ];
  assert.deepEqual(spellers(planted, TASKS_WORDS), {
    'src/client/WindowsCareClient.ts': ['stage', 'tasks'],
    'src/panel/x.ts': ['render', 'tasks'],
    'src/windowsTasks/flow.ts': ['--remove', 'stage'],
  });
});

test('the extension never spells --timer: the scan reads the Windows client and its answers too, and finds none', () => {
  const files = sources().map((s) => s.file);
  for (const module of WINDOWS_CLIENT_MODULES) {
    assert.ok(files.includes(module), `the scan reads ${module}`);
  }
  assert.deepEqual(spellers(sources(), NEVER_WORDS), {});
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
const ROOT_WORDS = ['-u', 'root', 'act', 'collect', '--preview', '--confirm', '--manual', '--detach', '--only', '--process', '--entry', '--stop'];

/** Words NO module may spell, the root module included: the timer's mark, another user. The daemon's settings verb is the writer's alone (E10.S1, below). */
const NEVER_WORDS = ['--timer', '--user'];

/** Every module a source imports by a RELATIVE specifier, resolved to `src/...ts` the way the structural tests name files. */
function resolvedImports(source: Source): string[] {
  return importsOf(source.text).filter((m) => m.startsWith('.')).map((m) => `${resolveRelative(source.file, m)}.ts`);
}

function importersOf(all: readonly Source[], module: string): string[] {
  return all.filter((s) => resolvedImports(s).includes(module)).map((s) => s.file).sort();
}

/** Modules whose code a webview runs or whose output it renders, plus the poller and the install flow: none may touch root/ (E10.S2a: nor the Archive page). */
const FAR_FROM_ROOT = ['src/panel/', 'src/statusBar/', 'src/poll/', 'src/install/', 'src/state/', 'src/logsPage/', 'src/archivePage/'];

test('only rootCall.ts spells a root argv word — and it spells every one of them (the scan is alive)', () => {
  assert.deepEqual(spellers(sources(), ROOT_WORDS), { [ROOT_CALL]: [...ROOT_WORDS].sort() });
});

test('no module spells --timer or --user — the root module included', () => {
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

test('no panel, status-bar, poller, install, store, Logs-page or Archive-page module imports anything under src/root/', () => {
  const offenders = sources().filter((s) => FAR_FROM_ROOT.some((dir) => s.file.startsWith(dir)) && resolvedImports(s).some((m) => m.startsWith('src/root/')));
  assert.deepEqual(offenders.map((s) => s.file), []);
  assert.ok(sources().some((s) => s.file.startsWith('src/panel/')), 'the scan sees the panel modules');
  assert.ok(sources().some((s) => s.file.startsWith('src/archivePage/')), 'the scan sees the Archive page modules (E10.S2a)');
});

/** E10.S2c: the Logs page takes its run words from `cleanup/runWords.ts` — so that table must stay as far from root as the page. */
test('the words table the Logs page imports (cleanup/runWords.ts) imports nothing under src/root/ or src/config/', () => {
  const words = sources().find((s) => s.file === 'src/cleanup/runWords.ts');
  assert.ok(words !== undefined, 'the scan sees the words table');
  assert.deepEqual(resolvedImports(words).filter((m) => m.startsWith('src/root/') || m.startsWith('src/config/')), []);
  assert.ok(sources().some((s) => s.file === 'src/logsPage/runDetail.ts' && resolvedImports(s).includes('src/cleanup/runWords.ts')), 'the Logs page does import it');
});

test('rootCall.ts imports only the client\'s argv builder, the verbs, the runner\'s types and the typed ids — no process API', () => {
  const source = sources().find((s) => s.file === ROOT_CALL);
  assert.ok(source !== undefined);
  assert.deepEqual(resolvedImports(source).sort(), ['src/client/WslCareClient.ts', 'src/client/ceilings.ts', 'src/client/verbs.ts', 'src/process/runner.ts', 'src/root/rootIds.ts', 'src/settings/numbers.ts', 'src/shared/daemonLimits.ts'], '§15q: the ceilings, the number settings and the published daemon limits, all pure');
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

// ---- E10.S1: THE user-layer writer (plan §15s) ----
//
// ONE module spells the daemon's settings verb and the one key it writes, ONE module imports it (the archive flow), and it
// imports no process API — the root boundary's shape, for the one unprivileged write the extension makes.

const CONFIG_CALL = 'src/config/configCall.ts';
const ARCHIVE_FLOW = 'src/archive/archiveFlow.ts';

/** The writer's words — only `configCall.ts` may spell them. */
const CONFIG_WORDS = ['config', 'archive.baseFolder'];

test('only configCall.ts spells config or archive.baseFolder — and it spells both (the scan is alive)', () => {
  assert.deepEqual(spellers(sources(), CONFIG_WORDS), { [CONFIG_CALL]: [...CONFIG_WORDS].sort() });
});

test('the config-word scan finds a planted word elsewhere — the root module included — and ignores a comment', () => {
  const planted: Source[] = [
    { file: 'src/root/rootCall.ts', text: "const t = ['config', 'set', 'dryRun', 'false'];" },
    { file: 'src/panel/x.ts', text: 'const k = [\n  `archive.baseFolder`,\n];' },
    { file: 'src/z.ts', text: '// config set archive.baseFolder is the writer\'s\nconst s = "configure";' },
  ];
  assert.deepEqual(spellers(planted, CONFIG_WORDS), { 'src/root/rootCall.ts': ['config'], 'src/panel/x.ts': ['archive.baseFolder'] });
});

test('only the archive flow imports configCall.ts', () => {
  assert.deepEqual(importersOf(sources(), CONFIG_CALL), [ARCHIVE_FLOW]);
});

test('no panel, status-bar, poller, install, store, Logs-page or Archive-page module imports anything under src/config/', () => {
  const offenders = sources().filter((s) => FAR_FROM_ROOT.some((dir) => s.file.startsWith(dir)) && resolvedImports(s).some((m) => m.startsWith('src/config/')));
  assert.deepEqual(offenders.map((s) => s.file), []);
});

test('configCall.ts imports only the client\'s argv builder and failure readers, the runner\'s types, the ceilings and the judged folder — no process API', () => {
  const source = sources().find((s) => s.file === CONFIG_CALL);
  assert.ok(source !== undefined);
  assert.deepEqual(resolvedImports(source).sort(), [
    'src/archive/judgedFolder.ts', 'src/client/WslCareClient.ts', 'src/client/ceilings.ts', 'src/client/exitCodes.ts', 'src/client/failures.ts', 'src/client/outcome.ts',
    'src/process/runner.ts', 'src/settings/numbers.ts', 'src/shared/daemonLimits.ts',
  ]);
  assert.deepEqual(importsOf(source.text).filter((m) => !m.startsWith('.')), []);
});

test('the writer\'s import scan finds a planted import of configCall.ts outside the archive flow', () => {
  const planted: Source[] = [
    { file: 'src/panel/viewModel.ts', text: "import { callConfig } from '../config/configCall';" },
    { file: ARCHIVE_FLOW, text: "import { callConfig } from '../config/configCall';" },
  ];
  assert.deepEqual(importersOf(planted, CONFIG_CALL), [ARCHIVE_FLOW, 'src/panel/viewModel.ts']);
});

// ---- E10.S1 own review on Fable, finding 4: the JudgedFolder brand is held by more than the types ----

/** Files that assert a value INTO `JudgedFolder` (`x as JudgedFolder`, `<JudgedFolder>x`), read with the parser — casts are erased
 * from the bundle, so this is the one place a forged folder could be seen. */
function judgedFolderCasters(all: readonly Source[]): string[] {
  return castersInto(all, 'JudgedFolder');
}

/** Files that assert a value INTO the branded type `brand` (`x as Brand`, `<Brand>x`) — the one scan every brand shares. */
function castersInto(all: readonly Source[], brand: string): string[] {
  const named = new RegExp(`\\b${brand}\\b`);

  return all.filter((s) => {
    let found = false;
    const visit = (node: ts.Node): void => {
      found ||= (ts.isAsExpression(node) || ts.isTypeAssertionExpression(node)) && named.test(node.type.getText());
      ts.forEachChild(node, visit);
    };
    visit(ts.createSourceFile('scan.ts', s.text, ts.ScriptTarget.ES2022, true, ts.ScriptKind.TS));
    return found;
  }).map((s) => s.file);
}

test('only judgedFolder.ts makes a JudgedFolder — no shipped module casts a value into one', () => {
  assert.deepEqual(judgedFolderCasters(sources()), ['src/archive/judgedFolder.ts']);
});

test('the brand scan finds a planted cast, in both spellings, and ignores a mention in a comment or a type annotation', () => {
  const planted: Source[] = [
    { file: 'src/archive/archiveFlow.ts', text: "const f = picked as JudgedFolder;" },
    { file: 'src/panel/x.ts', text: 'const f = <JudgedFolder>(picked);' },
    { file: 'src/z.ts', text: '// never `x as JudgedFolder` here\nlet f: JudgedFolder | undefined;' },
  ];
  assert.deepEqual(judgedFolderCasters(planted), ['src/archive/archiveFlow.ts', 'src/panel/x.ts']);
});

// ---- WR-T3 (I4): the preview button never spells the act verb ----
//
// The root-word scan above reads a literal that IS `act`. A cleanup preview, install or run-now has no business near the verb at all, and an argv built from
// one string — `'act A23 --preview'`, a template `act ${id} --confirm` — is not an exact `act`. So: no shipped module but the root module has a string literal
// that is `act` or BEGINS with it as a word (an argv written as one string). Prose that names the verb inside a sentence (the settings' descriptions say
// "`act <ids> --preview`" to a person) is not an argv, and neither are "the task acts" or "action".

/** The modules with a literal that spells the act verb: the whole word, or a string that begins with it as a word. */
function actSpellers(all: readonly Source[]): string[] {
  const spelt = (literal: string): boolean => /^act(\s|$)/.test(literal);

  return all.filter((s) => s.file !== ROOT_CALL && stringLiteralsOf(s.text).some(spelt)).map((s) => s.file);
}

test('no shipped module outside the root module spells the act verb — the Windows tasks\' modules included', () => {
  assert.deepEqual(actSpellers(sources()), []);
  assert.ok(sources().some((s) => s.file === 'src/windowsTasks/tasksFlow.ts'), 'the scan reads the Windows tasks\' modules');
});

test('the act scan finds a planted argv in an array, a one-string argv and a template — and ignores prose and comments', () => {
  const planted: Source[] = [
    { file: 'src/windowsTasks/x.ts', text: "const argv = ['act', 'A23'];" },
    { file: 'src/windowsTasks/y.ts', text: "const argv = 'act A23 --preview';" },
    { file: 'src/windowsTasks/z.ts', text: 'const argv = `act ${id} --confirm`;' },
    { file: 'src/windowsTasks/ok.ts', text: "// act A23 is the root's\nconst s = 'when the dry line says it acts, the action of the cleanup act is A23 only';" },
  ];
  assert.deepEqual(actSpellers(planted), ['src/windowsTasks/x.ts', 'src/windowsTasks/y.ts', 'src/windowsTasks/z.ts']);
  assert.deepEqual(actSpellers([{ file: ROOT_CALL, text: "const t = ['act'];" }]), [], 'the root module is the one that may');
});

// ---- E18.W1a-1: the typed distro (todo/PLAN_disk_health_E18.md, W1a §3.1, RED 11) ----
//
// `src/wsl/distroTarget.ts` is the home of the one distro token every client road takes. It sits under `wsl/` so that `state/`, `poll/`,
// `panel/`, `install/` and the pages may import it without touching `root/` or `config/`; it must therefore import none of the three
// modules that would pull them in, and no module but it may mint a `DistroName` by a cast.

const DISTRO_TARGET = 'src/wsl/distroTarget.ts';

test('distroTarget.ts imports nothing under src/root/, src/config/ or src/client/, and no package', () => {
  const source = sources().find((s) => s.file === DISTRO_TARGET);
  assert.ok(source !== undefined, 'the scan sees the typed distro module');
  assert.deepEqual(resolvedImports(source).filter((m) => m.startsWith('src/root/') || m.startsWith('src/config/') || m.startsWith('src/client/')), []);
  assert.deepEqual(importsOf(source.text).filter((m) => !m.startsWith('.')), []);
});

test('only distroTarget.ts makes a DistroName — no other shipped module casts a value into one (listDistros mints through listedNameOf)', () => {
  assert.deepEqual(castersInto(sources(), 'DistroName'), [DISTRO_TARGET]);
  const planted: Source[] = [
    { file: 'src/client/WslCareClient.ts', text: 'const d = row.name as DistroName;' },
    { file: 'src/poll/poller.ts', text: 'const d = <DistroName>(name);' },
    { file: 'src/z.ts', text: '// never `x as DistroName` here\nlet d: DistroName | undefined;' },
  ];
  assert.deepEqual(castersInto(planted, 'DistroName'), ['src/client/WslCareClient.ts', 'src/poll/poller.ts']);
});
