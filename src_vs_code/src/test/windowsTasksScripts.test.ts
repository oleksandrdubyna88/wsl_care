import assert from 'node:assert/strict';
import * as childProcess from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { test } from 'node:test';

import { TASK_NAMES } from '../client/verbs';
import { UNELEVATED_SWITCHES, unelevatedRequest } from '../powershell/unelevatedLauncher';
import { COMMAND_LINE_MAX, commandLineLength } from '../powershell/commandLine';
import { NO_ENGINE } from '../powershell/elevatedEngine';
import { HERE_END, registerTaskScript, removeTasksScript as peerRemove, terminatesHereString } from '../scheduler/registerScript';
import { installTaskScript, removeTasksScript, runNowScript } from '../windowsTasks/tasksScripts';
import { HRESULT_PRINT, runTaskScript } from '../scheduler/runScript';
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

test('the removal deletes the three tasks, never names the guard, HKLM or a service, and deletes the folder only when nothing else is in it', () => {
  const script = removeTasksScript();
  assert.match(script, /\$folder\.DeleteTask\('watch', 0\)/);
  assert.match(script, /\$folder\.DeleteTask\('archive', 0\)/);
  assert.match(script, /\$folder\.DeleteTask\('cleanup', 0\)/, "does not match /DeleteTask\\('cleanup', 0\\)/");
  assert.equal(script.split('DeleteTask(').length, TASK_NAMES.length + 1, 'one delete per task of the closed list');
  assert.doesNotMatch(script, /windows-time-guard|HKLM|-Service|w32tm|Remove-Item|Test-Path/);
  assert.match(script, /GetTasks\(1\)\)\.Count -eq 0 -and @\(\$folder\.GetFolders\(0\)\)\.Count -eq 0\) \{ \$service\.GetFolder\('\\'\)\.DeleteFolder\('wsl-care', 0\)/, 'the remove deleted the folder with a task in it');
  assert.equal(script.split('DeleteFolder(').length, 2, 'ONE folder delete');
  assert.ok(script.indexOf("DeleteTask('cleanup'") < script.indexOf('DeleteFolder('), 'the folder goes after the tasks');
  assert.match(script, /exit 31/);
  assert.match(script, /exit 32/);
  assert.equal(script, peerRemove('wsl-care', TASK_NAMES));
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

// ---- WR-E4: the run-now script ----

/** `needle` is in the script AND comes before the start — an ABSENT line (index -1) would pass a bare `<`, so presence is asserted first. */
function precedesRun(text: string, needle: string, why: string): void {
  const at = text.indexOf(needle);
  assert.notEqual(at, -1, 'the script has no ' + needle);
  assert.ok(at < text.indexOf('.Run('), why);
}

test('the run-now script names ONE folder and ONE task, reads the state before Run, and refuses a foreign name', () => {
  for (const task of TASK_NAMES) {
    const text = runNowScript(task);
    assert.equal(text.split("GetFolder('\\wsl-care')").length, 2, 'ONE folder');
    assert.equal(text.split("GetTask('" + task + "')").length, 2, 'ONE task');
    assert.equal(text.split('.Run(').length, 2, 'ONE start');
    assert.ok(text.startsWith('$env:PSModulePath'), 'the module path is pinned first');
    precedesRun(text, "'state='", 'the state is read AFTER Run(');
    precedesRun(text, '-eq 4 -or $task.LastTaskResult -eq 267009', 'the running test comes AFTER Run(');
    precedesRun(text, 'if (-not $enabled) { exit 36 }', 'the disabled test comes AFTER Run(');
    precedesRun(text, '$task.Definition.Principal', 'the principal is read AFTER Run(');
    precedesRun(text, 'WindowsIdentity]::GetCurrent().User.Value', 'the reader SID is read AFTER Run(');
    precedesRun(text, '[int]$p.LogonType -eq 3', 'the logon type is compared AFTER Run(');
    precedesRun(text, '[int]$p.RunLevel -eq 0', 'the run level is compared AFTER Run(');
    precedesRun(text, '$ok = $false', 'the verdict does not start refused AFTER Run(');
    precedesRun(text, 'if (-not $ok) { exit 38 }', 'the principal refusal comes AFTER Run(');
    precedesRun(text, '$running = $true', 'the running verdict does not start refused AFTER Run(');
    precedesRun(text, 'if ($running) { exit 35 }', 'the running refusal comes AFTER Run(');
    assert.ok(text.indexOf('$ok = $false') < text.indexOf('$ok = ($sid -eq'), 'the verdict starts refused BEFORE the compare');
    const guard = text.split(String.fromCharCode(10)).find((line) => line.startsWith('$ok = $false; try {')) ?? '';
    assert.ok(guard.includes('$ok = ($sid -eq') && guard.endsWith('catch { $ok = $false }'), 'the compare sits INSIDE a try whose catch refuses');
    const state = text.split(String.fromCharCode(10)).find((line) => line.startsWith('$running = $true; try {')) ?? '';
    assert.ok(state.includes('$running = (') && state.includes('catch {') && state.includes('exit 37'), 'a state that cannot be read is exit 37, never a fall through to Run');
    assert.match(text, /\.Run\(\$null\)/);
    for (const code of [34, 35, 36, 37, 38]) {
      assert.ok(text.includes('exit ' + code), 'exit ' + code);
    }
    assert.doesNotMatch(text, /windows-time-guard|HKLM|-Service|w32tm|Remove-Item|Register-ScheduledTask|DeleteTask|RunEx|schtasks|Import-Module/);
    assert.doesNotMatch(text, /--timer|\bact\b|wsl-care\.exe|\btasks\b/, 'no daemon word');
  }
  assert.throws(() => runNowScript('other' as never), RangeError);
  assert.throws(() => runNowScript('windows-time-guard' as never), RangeError, 'the guard\'s name is not one of the two tasks');
  assert.throws(() => runTaskScript("wsl-care'; evil", 'watch'), RangeError);
  assert.throws(() => runTaskScript('wsl-care', "x'; Remove-Item /"), RangeError);
});

test('Fable minor 1: the order helper refuses a script where a line is absent, not only one where it comes late', () => {
  const without = runNowScript('watch').replace("'state=' + [int]$task.State", '$null');
  assert.throws(() => precedesRun(without, "'state='", 'x'), /the script has no/);
  assert.throws(() => precedesRun("$task.Run($null)\n'state='", "'state='", 'x'), /x/);
  assert.doesNotThrow(() => precedesRun("'state='\n$task.Run($null)", "'state='", 'x'));
});

test('Fable minor 6: PowerShell\'s parser reads the run-now script without an error (parse only — nothing is run)', { skip: process.platform !== 'win32' ? 'Windows PowerShell exists on the Windows leg only' : false }, () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'testsToDelete-wsl-care-runnow-'));
  try {
    for (const task of TASK_NAMES) {
      fs.writeFileSync(path.join(dir, task + '.ps1'), runNowScript(task), 'utf8');
    }
    const files = TASK_NAMES.map((name) => `'${name}.ps1'`).join(',');
    const parse = `& { $e = $null; $t = $null; foreach ($f in ${files}) { [void][System.Management.Automation.Language.Parser]::ParseFile((Join-Path $args[0] $f), [ref]$t, [ref]$e); if ($e.Count -gt 0) { Write-Output ($f + ': ' + $e[0].Message); exit 1 } }; exit 0 }`;
    const result = childProcess.spawnSync(PS51_ENGINE.file, ['-NoProfile', '-NonInteractive', '-Command', parse, dir], { encoding: 'utf8', timeout: 60_000 });
    assert.equal(result.status, 0, result.stdout + result.stderr);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('coai finding 5: a foreign task name is "not a valid Windows task name" — for the install and for the run-now alike', () => {
  assert.throws(() => runNowScript('other' as never), /"other" is not a valid Windows task name/);
  assert.throws(() => installTaskScript('other' as never, XML), /"other" is not a valid Windows task name/);
});

// ---- coai findings 6 and 7: the script's catches unwrap the exception chain; an empty UserId is never translated ----

/** The catch blocks of the run script that print an HRESULT. */
function hresultCatches(text: string): string[] {
  return text.split('catch {').slice(1).filter((block) => block.includes("'hresult="));
}

test('coai finding 6: every catch of the run script that prints an HRESULT unwraps InnerException first', () => {
  for (const task of TASK_NAMES) {
    const catches = hresultCatches(runNowScript(task));
    assert.ok(catches.length >= 3, 'the script prints an HRESULT in at least three places: ' + catches.length);
    for (const block of catches) {
      const unwrap = block.indexOf('while ($ex.InnerException) { $ex = $ex.InnerException }');
      assert.notEqual(unwrap, -1, 'a catch prints an HRESULT without unwrapping: ' + block.slice(0, 80));
      assert.ok(unwrap < block.indexOf("'hresult="), 'the unwrap comes AFTER the print');
      assert.ok(block.includes('-f $ex.HResult'), 'the HRESULT is formatted from the innermost exception');
    }
  }
});

test('coai finding 6: the HRESULT line is formatted bit-exact for a NEGATIVE HRESULT, from the innermost exception (parse and format only)', { skip: process.platform !== 'win32' ? 'Windows PowerShell exists on the Windows leg only' : false }, () => {
  const script = "$inner = [System.Runtime.InteropServices.COMException]::new('inner', [int]-2147024894); $outer = New-Object System.InvalidOperationException('outer', $inner); try { throw $outer } catch { " + HRESULT_PRINT + " }";
  const result = childProcess.spawnSync(PS51_ENGINE.file, ['-NoProfile', '-NonInteractive', '-Command', script], { encoding: 'utf8', timeout: 60_000 });
  assert.equal(result.status, 0, result.stdout + result.stderr);
  assert.equal(result.stdout.trim(), 'hresult=0x80070002');
});

test('coai finding 7: the UserId is coerced to text first and translated only when it is not empty', () => {
  const text = runNowScript('watch');
  const coerced = text.indexOf('$id = [string]$p.UserId');
  assert.notEqual(coerced, -1, 'the script does not coerce the UserId');
  assert.ok(coerced < text.indexOf('NTAccount($id)'), 'the UserId is translated before it is coerced');
  assert.ok(text.includes("if ($id -ne '') {"), 'an empty UserId is translated');
  assert.ok(text.indexOf("if ($id -ne '') {") < text.indexOf('NTAccount($id)'), 'the empty test comes after the translation');
  assert.ok(!text.includes('NTAccount($p.UserId)'), 'the raw UserId reaches NTAccount');
});

// ---- WR-T3: the enabled check refuses by default, and the third name builds ----

test('the enabled check of the run script refuses by default: a read that throws is exit 37 with the HRESULT, never a fall through to the principal check and Run', () => {
  for (const task of TASK_NAMES) {
    const text = runNowScript(task);
    const lines = text.split(String.fromCharCode(10));
    const guard = lines.find((line) => line.startsWith('$enabled = $false; try {')) ?? '';
    assert.ok(guard !== '', 'the script has no $enabled = $false');
    assert.ok(text.indexOf('$enabled = $false') < text.indexOf('[bool]$task.Enabled'), 'the verdict starts refused BEFORE the read');
    assert.ok(guard.includes('$enabled = [bool]$task.Enabled') && guard.includes('catch {') && guard.includes('hresult=') && guard.endsWith('exit 37 }'), 'the read sits INSIDE a try whose catch prints the HRESULT and exits 37: ' + guard);
    const refusal = text.indexOf('if (-not $enabled) { exit 36 }');
    assert.notEqual(refusal, -1, 'the disabled refusal');
    assert.ok(text.indexOf(guard) < refusal && refusal < text.indexOf('$ok = $false'), 'enabled read, then the disabled refusal, then the principal check');
    assert.ok(refusal < text.indexOf('.Run('), 'and all of it before Run(');
    assert.doesNotMatch(text, /if \(-not \$task\.Enabled\)/, 'the bare read outside a try is gone');
  }
});

test('the cleanup builds: the run-now script and the install script name ONE folder and ONE task', () => {
  const run = runNowScript('cleanup');
  assert.equal(run.split("GetTask('cleanup')").length, 2);
  const install = installTaskScript('cleanup', XML);
  assert.match(install, /cleanup/);
  assert.equal(install.split("'wsl-care'").length >= 2, true);
  assert.throws(() => runNowScript('cleanups' as never), RangeError);
});
