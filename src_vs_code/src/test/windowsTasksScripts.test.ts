import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import { test } from 'node:test';

import { UNELEVATED_SWITCHES, unelevatedRequest } from '../powershell/unelevatedLauncher';
import { COMMAND_LINE_MAX, commandLineLength } from '../powershell/commandLine';
import { NO_ENGINE } from '../powershell/elevatedEngine';
import { HERE_END, registerTaskScript, removeTasksScript as peerRemove, terminatesHereString } from '../scheduler/registerScript';
import { installTaskScript, removeTasksScript } from '../windowsTasks/tasksScripts';
import { REMOVE_SCRIPT } from '../windowsTime/guardScripts';
import { fromExtensionRoot, shippedSources } from './support/paths';
import { PS51_ENGINE, STORE_ENGINE } from './support/engines';
import { importsOf, stringLiteralsOf } from './support/sourceScan';
import { decoded } from './support/tasksWorld';
import { renderAnswer } from './support/windowsTasks';

/**
 * The scripts and the request of the Windows tasks' install and remove (WR-E3, PLAN_windows_runner.md §3.4, §3.8): the XML travels verbatim in a
 * single-quoted here-string, a foreign name cannot reach a script, the removal can never touch the guard, and the request is one program with
 * a fixed argv and a payload that is exactly the script shown. Nothing here starts a PowerShell.
 */

const XML = String(renderAnswer('watch from the staged copy').xml);

test('the script carries the XML verbatim in a single-quoted here-string, creates the folder, then registers create-or-replace', () => {
  const lines = installTaskScript('watch', XML).split('\n');
  const open = lines.indexOf("$xml = @'");
  const close = lines.lastIndexOf(HERE_END);
  assert.ok(open > 0 && close > open);
  assert.equal(lines.slice(open + 1, close).join('\n'), XML, 'the XML in the script is not the render\'s XML');
  const text = lines.join('\n');
  assert.match(text, /GetFolder\('\\wsl-care'\).*CreateFolder\('wsl-care'\)/);
  assert.match(text, /Register-ScheduledTask -TaskPath '\\wsl-care\\' -TaskName 'watch' -Xml \$xml -Force -ErrorAction Stop/);
  assert.ok(text.indexOf('CreateFolder') < text.indexOf('Register-ScheduledTask -TaskPath'), 'the folder exists before the registration');
  assert.ok(lines[0]?.startsWith('$env:PSModulePath'), 'the module path is pinned first');
  assert.doesNotMatch(text, /\$\{|windows-time-guard|HKLM|-Service|w32tm/);
  assert.ok(!XML.includes("'@"), 'the golden carries no terminator');
});

test('a foreign task name or folder throws RangeError; a terminator line throws RangeError; the closed list is checked at runtime', () => {
  assert.throws(() => installTaskScript('other' as never, XML), RangeError);
  assert.throws(() => installTaskScript('windows-time-guard' as never, XML), RangeError, 'the guard\'s name is not one of the two tasks');
  assert.throws(() => registerTaskScript('wsl-care', "x'; Remove-Item -Recurse /", XML), RangeError);
  assert.throws(() => registerTaskScript("wsl-care'; evil", 'watch', XML), RangeError);
  assert.throws(() => registerTaskScript('wsl-care', 'watch', `${XML}\n'@\nRemove-Item x`), RangeError);
  assert.equal(terminatesHereString(`${XML}\r\n'@`), true);
  assert.equal(terminatesHereString("a '@ b"), false);
});

test('the removal deletes the two tasks, never names the guard, HKLM or a service, and deletes the folder only when nothing else is in it', () => {
  const script = removeTasksScript();
  assert.match(script, /\$folder\.DeleteTask\('watch', 0\)/);
  assert.match(script, /\$folder\.DeleteTask\('archive', 0\)/);
  assert.doesNotMatch(script, /windows-time-guard|HKLM|-Service|w32tm|Remove-Item|Test-Path/);
  assert.match(script, /GetTasks\(1\)\)\.Count -eq 0 -and @\(\$folder\.GetFolders\(0\)\)\.Count -eq 0\) \{ \$service\.GetFolder\('\\'\)\.DeleteFolder\('wsl-care', 0\)/, 'the remove deleted the folder with a task in it');
  assert.equal(script.split('DeleteFolder(').length, 2, 'ONE folder delete');
  assert.ok(script.indexOf("DeleteTask('archive'") < script.indexOf('DeleteFolder('), 'the folder goes after the tasks');
  assert.match(script, /exit 31/);
  assert.match(script, /exit 32/);
  assert.equal(script, peerRemove('wsl-care', ['watch', 'archive']));
});

test('the guard\'s removal is the peer\'s lines plus its own stamp: the two tasks\' removal shares its text', () => {
  const peerLines = removeTasksScript().split('\n').slice(1, -1);
  const guard = REMOVE_SCRIPT.split('\n');
  assert.ok(peerLines.slice(0, 3).every((line) => guard.includes(line)), 'the connect and the folder read are the same lines');
});

test('the request is ONE program with the fixed switches, the encoded script as its payload, the tree kill and the ceiling', () => {
  const script = installTaskScript('archive', String(renderAnswer('archive from the staged copy').xml));
  const request = unelevatedRequest(PS51_ENGINE, script, 45_000);
  assert.ok(typeof request !== 'string');
  assert.equal(request.file, PS51_ENGINE.file);
  assert.deepEqual(request.args.slice(0, 3), [...UNELEVATED_SWITCHES]);
  assert.equal(request.args.length, 4);
  assert.equal(decoded(request), script);
  assert.deepEqual([request.timeoutMs, request.killTree], [45_000, true]);
  const store = unelevatedRequest(STORE_ENGINE, script, 1_000);
  assert.ok(typeof store !== 'string' && store.file === STORE_ENGINE.file, 'the Store package\'s path is quotable: it is started directly');
});

test('no request is built without a program, for a path PowerShell cannot quote, or for a command line past the bound', () => {
  assert.match(String(unelevatedRequest(NO_ENGINE, 'x', 1)), /no PowerShell can be named/);
  assert.match(String(unelevatedRequest({ ...PS51_ENGINE, file: "C:\\it's\\powershell.exe" }, 'x', 1)), /cannot be quoted safely/);
  assert.match(String(unelevatedRequest({ ...PS51_ENGINE, file: 'powershell.exe' }, 'x', 1)), /cannot be quoted safely/);
  const long = unelevatedRequest(PS51_ENGINE, 'x'.repeat(COMMAND_LINE_MAX), 1);
  assert.match(String(long), /too long for one command line \(\d+ characters\)/);
  const fits = unelevatedRequest(PS51_ENGINE, installTaskScript('watch', XML), 1);
  assert.ok(typeof fits !== 'string' && commandLineLength(fits) < COMMAND_LINE_MAX);
});

// ---- structure: where the words live ----

interface Source {
  readonly file: string;
  readonly text: string;
}

const TASK_FILES = ['src/scheduler/registerScript.ts'];

function sources(): Source[] {
  return shippedSources().map((file) => ({ file: fromExtensionRoot(file), text: fs.readFileSync(file, 'utf8') }));
}

function tasksModules(all: readonly Source[]): Source[] {
  return all.filter((s) => s.file.startsWith('src/windowsTasks/') || TASK_FILES.includes(s.file));
}

/** The modules that author task XML: a literal holding an element only a task definition has. */
function xmlAuthors(all: readonly Source[]): string[] {
  const shape = /<Task[ >]|<Principals>|<Triggers>/;

  return all.filter((s) => stringLiteralsOf(s.text).some((literal) => shape.test(literal))).map((s) => s.file).sort();
}

test('the extension authors no task XML for the Windows tasks: the Windows tasks\' modules and the registration module spell no definition', () => {
  const all = sources();
  assert.ok(tasksModules(all).length >= 8, 'the scan reads the Windows tasks\' modules');
  assert.deepEqual(xmlAuthors(tasksModules(all)), []);
  assert.deepEqual(xmlAuthors(all), ['src/windowsTime/guardTask.ts'], 'the guard\'s own task is the one definition this extension writes (the scan is alive)');
});

test('the XML scan finds a planted definition in a literal, a template and over lines — and ignores a comment', () => {
  const planted: Source[] = [
    { file: 'src/windowsTasks/x.ts', text: "const a = '<Task version=\"1.4\">';" },
    { file: 'src/windowsTasks/y.ts', text: 'const b = `<Triggers>${x}</Triggers>`;' },
    { file: 'src/windowsTasks/z.ts', text: "const c = [\n  '<Principals>',\n];" },
    { file: 'src/windowsTasks/w.ts', text: '// <Task version="1.4"> and <Principals> belong to the daemon\nconst d = 1;' },
  ];
  assert.deepEqual(xmlAuthors(planted), ['src/windowsTasks/x.ts', 'src/windowsTasks/y.ts', 'src/windowsTasks/z.ts']);
});

test('no module under windowsTasks/ imports src/config/, child_process or the elevated launcher; nothing in it names elevation', () => {
  const offenders = sources()
    .filter((s) => s.file.startsWith('src/windowsTasks/') || TASK_FILES.includes(s.file))
    .filter((s) => importsOf(s.text).some((m) => /(^|\/)config\//.test(m) || m === 'child_process' || m === 'node:child_process' || /\/elevatedLauncher$/.test(m)))
    .map((s) => s.file);
  assert.deepEqual(offenders, []);
  // The render's guards NAME the word  — to refuse an XML that carries it — so the guards' own module is the one place it is spelt.
  const asking = tasksModules(sources()).filter((s) => s.file !== 'src/windowsTasks/tasksGuards.ts').filter((s) => stringLiteralsOf(s.text).some((l) => /runas|Start-Process|-Verb/i.test(l))).map((s) => s.file);
  assert.deepEqual(asking, [], 'no literal of the Windows tasks\' modules asks for elevation');
});

test('coai finding 4: the registration module\'s refusal of a terminator line names the terminator', () => {
  assert.throws(() => registerTaskScript('wsl-care', 'watch', `${XML}\n'@\nx`), /'@/);
});
