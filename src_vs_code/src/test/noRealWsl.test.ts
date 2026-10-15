import assert from 'node:assert/strict';
import * as childProcess from 'node:child_process';
import { test } from 'node:test';

import { spawnRunner } from '../process/runner';
import { wslExecutable } from '../wsl/wslExecutable';
import { commandWords, isMachineChange, isWslCareExe, isWslLauncher, MUTATING, refusalOf, revealed, TASK_TRIPWIRE_MESSAGE, TRIPWIRE_MESSAGE, WSL_CARE_TRIPWIRE_MESSAGE } from './support/noRealWsl';

/**
 * Plan §16 E5.S1 acceptance: "no test can reach the real wsl.exe (asserted)". The tripwire is loaded by the runner
 * script into every test process; these tests run in such a process and prove it is ARMED there — if
 * `scripts/run-tests.mjs` stopped passing `--require`, the first test is red, never silently unprotected.
 */

test('the tripwire is armed in this test process', () => {
  assert.equal((globalThis as Record<string, unknown>).__wslCareTripwireArmed, true, 'run-tests.mjs must start every test process with --require out/test/support/noRealWsl.js');
});

test('starting the real System32 wsl.exe from a test throws before anything starts — through every launcher', () => {
  const real = 'C:\\Windows\\System32\\wsl.exe';
  assert.throws(() => childProcess.spawn(real, ['--list']), new RegExp(TRIPWIRE_MESSAGE));
  assert.throws(() => childProcess.spawnSync(real, ['--list']), new RegExp(TRIPWIRE_MESSAGE));
  assert.throws(() => childProcess.execFile(real, ['--list']), new RegExp(TRIPWIRE_MESSAGE));
  assert.throws(() => childProcess.execFileSync(real, ['--list']), new RegExp(TRIPWIRE_MESSAGE));
  assert.throws(() => childProcess.execSync('wsl --list'), new RegExp(TRIPWIRE_MESSAGE));
  assert.throws(() => childProcess.exec('wsl.exe --list'), new RegExp(TRIPWIRE_MESSAGE));
});

test('the product runner, handed the real environment, cannot start the real wsl.exe from a test', async () => {
  const real = wslExecutable({ SystemRoot: 'C:\\Windows' });
  assert.equal(real, 'C:\\Windows\\System32\\wsl.exe');
  const result = await spawnRunner({ file: real ?? '', args: ['--list', '--quiet'], timeoutMs: 5_000 });
  assert.equal(result.kind, 'failedToStart');
  assert.match(result.kind === 'failedToStart' ? result.reason : '', new RegExp(TRIPWIRE_MESSAGE));
});

test('the tripwire names a WSL launcher by its base name on either path style and leaves other programs alone', () => {
  for (const name of ['wsl', 'wsl.exe', 'WSL.EXE', 'C:\\Windows\\System32\\wsl.exe', '/mnt/c/Windows/System32/wsl.exe', 'D:\\x\\WSL']) {
    assert.equal(isWslLauncher(name), true, name);
  }
  for (const name of [process.execPath, 'wslpath', 'C:\\Windows\\System32\\wslhost.exe', 'node', 'wsl.exe.bak']) {
    assert.equal(isWslLauncher(name), false, name);
  }
});

// ---- PLAN_windows_time_task.md D7, D8 o4: the Windows Time guard's machine changes are refused too — by the decoded text ----

const POWERSHELL = 'C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe';

function encodedWords(script: string): string {
  return Buffer.from(script, 'utf16le').toString('base64');
}

test('no test can register or delete a task, change a service, write the machine registry or resync — through every launcher', () => {
  for (const tool of ['schtasks', 'C:\\Windows\\System32\\schtasks.exe', 'sc.exe']) {
    assert.throws(() => childProcess.spawnSync(tool, ['/Query']), new RegExp(TASK_TRIPWIRE_MESSAGE), tool);
  }
  assert.throws(() => childProcess.execSync('schtasks /Delete /TN x /F'), new RegExp(TASK_TRIPWIRE_MESSAGE));
  for (const words of ['Register-ScheduledTask -Xml $x -TaskName t', 'Start-Service w32time', '$f.DeleteTask(\'t\', 0)', 'Remove-Item HKLM:\\SOFTWARE\\x', '& w32tm /resync']) {
    assert.throws(() => childProcess.spawn(POWERSHELL, ['-NoProfile', '-Command', words]), new RegExp(TASK_TRIPWIRE_MESSAGE), words);
  }
});

test('an ENCODED payload is decoded before it is judged — as its own argument under every prefix of -EncodedCommand, and nested in a -Command text', () => {
  const payload = encodedWords("Unregister-ScheduledTask -TaskName 'windows-time-guard' -Confirm:$false");
  for (const flag of ['-EncodedCommand', '-enc', '-ec', '-e', '-ENCODEDCOMMAND']) {
    assert.equal(isMachineChange(POWERSHELL, ['-NoProfile', flag, payload]), true, flag);
  }
  const nested = encodedWords(`$p = '-NoProfile -EncodedCommand ${payload}'`);
  assert.equal(isMachineChange(POWERSHELL, ['-NoProfile', '-EncodedCommand', nested]), true, 'a payload carrying a payload');
  assert.equal(isMachineChange(POWERSHELL, ['-Command', `$psi.Arguments = '-NoProfile -NonInteractive -EncodedCommand ${payload}'`]), true, 'story 1\'s launcher shape');
  assert.throws(() => childProcess.spawnSync(POWERSHELL, ['-NoProfile', '-enc', payload]), new RegExp(TASK_TRIPWIRE_MESSAGE));
  assert.ok(revealed(['-e', payload]).includes('Unregister-ScheduledTask'));
});

test('a READ stays allowed: Task Scheduler\'s in-memory parse, a task read, PowerShell\'s parser — and other programs', () => {
  assert.equal(isMachineChange(POWERSHELL, ['-Command', "$s = New-Object -ComObject Schedule.Service; $s.Connect(); $d = $s.NewTask(0); $d.XmlText = 'x'"]), false);
  assert.equal(isMachineChange(POWERSHELL, ['-Command', "$s.GetFolder('\\wsl-care').GetTask('windows-time-guard')"]), false);
  assert.equal(isMachineChange(POWERSHELL, ['-Command', '[System.Management.Automation.Language.Parser]::ParseFile']), false);
  assert.equal(isMachineChange(process.execPath, ['-e', 'Start-Service']), false, 'node is not a PowerShell');
  assert.equal(MUTATING.test('Get-Service w32time'), false);
  assert.equal(MUTATING.test('NewItemProperty'), false, 'matched as words, not substrings');
});

test('own code review k6: a quoted program, cmd /c and shell: true are judged by the program they really run', () => {
  assert.deepEqual(commandWords('"C:\\Program Files\\x\\powershell.exe" -Command a'), ['C:\\Program Files\\x\\powershell.exe', '-Command', 'a']);
  assert.throws(() => childProcess.execSync(`"${POWERSHELL}" -Command Start-Service w32time`), new RegExp(TASK_TRIPWIRE_MESSAGE));
  assert.throws(() => childProcess.spawnSync('cmd.exe', ['/c', 'schtasks', '/Delete', '/TN', 'x']), new RegExp(TASK_TRIPWIRE_MESSAGE));
  assert.throws(() => childProcess.spawnSync('cmd.exe', ['/c', 'wsl.exe', '--list']), new RegExp(TRIPWIRE_MESSAGE));
  assert.throws(() => childProcess.spawnSync('schtasks /Query', { shell: true }), new RegExp(TASK_TRIPWIRE_MESSAGE));
  assert.throws(() => childProcess.spawnSync('wsl.exe', ['--list'], { shell: true }), new RegExp(TRIPWIRE_MESSAGE));
  assert.equal(refusalOf('cmd.exe', ['/c', 'echo', 'hello']), undefined, 'cmd running something harmless is not refused');
  for (const words of ['New-ItemProperty -Path HKLM:\\x -Name a', 'Remove-ItemProperty -Path HKLM:\\x -Name a', 'sc.exe stop w32time', 'net start w32time']) {
    assert.equal(MUTATING.test(words), true, words);
  }
});

test('PLAN_gpu_health.md D7: a device change is refused too — Enable-/Disable-PnpDevice in a PowerShell (decoded payloads included) and pnputil itself; a device READ stays allowed', () => {
  for (const words of ['Enable-PnpDevice -InstanceId x -Confirm:$false', 'disable-pnpdevice -InstanceId x']) {
    assert.equal(MUTATING.test(words), true, words);
    assert.equal(isMachineChange(POWERSHELL, ['-Command', words]), true, words);
    assert.throws(() => childProcess.spawnSync(POWERSHELL, ['-NoProfile', '-Command', words]), new RegExp(TASK_TRIPWIRE_MESSAGE), words);
  }
  const encoded = Buffer.from('Enable-PnpDevice -InstanceId x', 'utf16le').toString('base64');
  assert.equal(isMachineChange(POWERSHELL, ['-NoProfile', '-EncodedCommand', encoded]), true, 'an encoded payload is decoded and judged');
  assert.throws(() => childProcess.spawnSync('pnputil.exe', ['/enable-device', 'x']), new RegExp(TASK_TRIPWIRE_MESSAGE));
  assert.equal(isMachineChange(POWERSHELL, ['-Command', 'Get-PnpDevice -Class Display; Get-CimInstance -ClassName Win32_VideoController']), false, 'reading the devices is not a change');
});

// ---- WR-E1 own review m4: a real wsl-care.exe must never start from a test either ----

test('starting a real wsl-care.exe from a test throws before anything starts — bundled, staged, bare, through a shell', () => {
  for (const name of ['C:\\Users\\user\\.vscode\\extensions\\pub.ai-os-care-0.5.0-win32-x64\\bin\\wsl-care.exe', `C:\\Users\\user\\AppData\\Local\\wsl-care\\bin\\${'0123456789abcdef'.repeat(4)}\\wsl-care.exe`, 'wsl-care.exe', 'WSL-CARE.EXE', '/opt/wsl-care/bin/wsl-care']) {
    assert.equal(isWslCareExe(name), true, name);
    assert.throws(() => childProcess.spawnSync(name, ['--version']), new RegExp(WSL_CARE_TRIPWIRE_MESSAGE), name);
  }
  assert.throws(() => childProcess.execFileSync('wsl-care.exe', ['tasks', 'stage', '--json']), new RegExp(WSL_CARE_TRIPWIRE_MESSAGE));
  assert.throws(() => childProcess.execSync('wsl-care.exe --version'), new RegExp(WSL_CARE_TRIPWIRE_MESSAGE));
  assert.throws(() => childProcess.spawnSync('wsl-care.exe --version', { shell: true }), new RegExp(WSL_CARE_TRIPWIRE_MESSAGE));
  assert.throws(() => childProcess.spawnSync('cmd.exe', ['/c', 'wsl-care.exe', '--version']), new RegExp(WSL_CARE_TRIPWIRE_MESSAGE));
});

test('the wsl-care.exe tripwire names the program by its base name only: the fake\'s requested file (data), a folder of that name and other programs are left alone', () => {
  for (const name of [process.execPath, 'node', 'wsl-care.exe.bak', 'my-wsl-care.exe', 'C:\\wsl-care.exe\\other.exe', 'wsl-care-fake.exe', 'wsl.exe']) {
    assert.equal(isWslCareExe(name), false, name);
  }
  assert.equal(refusalOf('node', ['fakeWsl.js', 'tasks', 'stage', '--json']), undefined, 'the fake runner starts node and passes the exe only as data');
});
