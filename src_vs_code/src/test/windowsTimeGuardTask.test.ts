import assert from 'node:assert/strict';
import * as childProcess from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { test } from 'node:test';

import type { ProcessRequest } from '../process/runner';
import { commandLineLength, ELEVATED_COMMAND_LINE_MAX, HERE_END, installRequest, installScript, NOT_FOUND_HRESULT, queryRequest, QUERY_SCRIPT, REMOVE_SCRIPT, removeRequest } from '../windowsTime/guardScripts';
import { commandAfterArgv, GUARD_SDDL, guardArguments, guardScript, guardSummary, guardTaskXml, START_ALLOWED_FUNCTION, STAMP_KEY, STOP_QUERY, SUMMARY_FUNCTION, TASK_POWERSHELL, type GuardOptions } from '../windowsTime/guardTask';
import { BODY, SET_AUTOMATIC } from '../windowsTime/windowsTimeFix';
import { isElevatedPowerShell, isMachineChange, MUTATING } from './support/noRealWsl';
import { at, parseXml, type XmlElement } from './support/xmlTree';

/**
 * The Windows Time guard's task and scripts (PLAN_windows_time_task.md D1–D3, D8): the definition is read back PARSED —
 * by a strict XML reader on every OS, and by Task Scheduler's own parser in memory on the Windows leg (nothing is ever
 * registered); the scripts are read by PowerShell's own parser (never executed — they would change this machine); and the
 * rate limit's one decision is extracted from the script's syntax tree and run ALONE.
 */

const DEFAULTS: GuardOptions = { setAutomaticStart: true, everyHours: 4, minMinutesBetweenStarts: 10, delaySeconds: 60, timeLimitMinutes: 5 };
const OTHER: GuardOptions = { setAutomaticStart: false, everyHours: 7, minMinutesBetweenStarts: 3, delaySeconds: 0, timeLimitMinutes: 9 };
const LARGEST: GuardOptions = { setAutomaticStart: true, everyHours: 168, minMinutesBetweenStarts: 1440, delaySeconds: 3600, timeLimitMinutes: 60 };
const ENV = { SystemRoot: 'C:\\Windows' } as const;
const POWERSHELL = 'C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe';

function task(options: GuardOptions): XmlElement {
  return parseXml(guardTaskXml(options));
}

function encodedInner(request: ProcessRequest): string {
  const b64 = /-EncodedCommand ([A-Za-z0-9+/=]+)'/.exec(request.args[3] ?? '')?.[1] ?? '';
  return Buffer.from(b64, 'base64').toString('utf16le');
}

// ---- D2: the action ----

test('the action is ONE line that survives the command line byte for byte: no double quote, tab, newline, double space or %', () => {
  for (const options of [DEFAULTS, OTHER, LARGEST]) {
    const script = guardScript(options);
    assert.doesNotMatch(script, /["\t\r\n%]| {2}/, 'CommandLineToArgvW would change it, or Task Scheduler would expand it');
    assert.equal(commandAfterArgv(guardArguments(options)), script);
  }
  assert.equal(commandAfterArgv('-NoProfile -Command a  b'), 'a b', 'the round trip does collapse a double space — which is why the script may hold none');
});

test('the action does only what the owner listed, in the order that makes it safe', () => {
  const script = guardScript(DEFAULTS);
  const order = [
    "$env:PSModulePath = Join-Path $PSHOME 'Modules'",
    'if ($null -eq $service) { exit 22 }',
    SET_AUTOMATIC,
    "if ($service.Status -ne 'Running') {",
    'Set-ItemProperty -Path $key -Name lastStartUtcTicks',
    BODY.join('; '),
  ].map((part) => script.indexOf(part));
  assert.ok(order.every((i) => i >= 0), `every part present: ${JSON.stringify(order)}`);
  assert.deepEqual([...order].sort((a, b) => a - b), order, 'modules → the service exists → start type FIRST (a Disabled service is set Automatic even when rate-limited) → the rate limit → the stamp written BEFORE the start → story 1\'s start and resync');
  assert.ok(script.endsWith(BODY.join('; ')), 'story 1\'s lines verbatim, last');
  assert.ok(script.startsWith("$env:PSModulePath = Join-Path $PSHOME 'Modules'"));
  assert.ok(script.includes(`$key = '${STAMP_KEY}'`) && STAMP_KEY.startsWith('HKLM:\\SOFTWARE\\'), 'the stamp under HKLM\\SOFTWARE — only administrators and SYSTEM write there');
});

test('the start-type line is there only while wslCare.windowsTime.setAutomaticStart is on, and the window is the setting in ticks', () => {
  assert.ok(guardScript(DEFAULTS).includes(SET_AUTOMATIC));
  assert.ok(!guardScript(OTHER).includes('Set-Service'));
  assert.ok(guardScript(DEFAULTS).includes('Test-WslCareStartAllowed $now $last 6000000000)'), '10 min = 6 000 000 000 ticks');
  assert.ok(guardScript(OTHER).includes('Test-WslCareStartAllowed $now $last 1800000000)'), '3 min');
  assert.ok(guardScript(DEFAULTS).includes(START_ALLOWED_FUNCTION));
});

// ---- D1: the definition, read back parsed ----

test('the definition: SYSTEM, its descriptor, four triggers, one at a time, and the fixed action — read back PARSED', () => {
  const root = task(DEFAULTS);
  assert.equal(root.name, 'Task');
  assert.equal(root.attributes.xmlns, 'http://schemas.microsoft.com/windows/2004/02/mit/task');
  assert.equal(at(root, 'Principals', 'Principal', 'UserId').text, 'S-1-5-18');
  assert.equal(at(root, 'Principals', 'Principal', 'RunLevel').text, 'HighestAvailable');
  assert.equal(at(root, 'RegistrationInfo', 'SecurityDescriptor').text, GUARD_SDDL);
  assert.deepEqual(at(root, 'Triggers').children.map((t) => t.name), ['BootTrigger', 'LogonTrigger', 'TimeTrigger', 'EventTrigger']);
  for (const name of ['BootTrigger', 'LogonTrigger', 'EventTrigger']) {
    assert.equal(at(root, 'Triggers', name, 'Delay').text, 'PT60S', name);
    assert.equal(at(root, 'Triggers', name, 'Enabled').text, 'true', name);
  }
  assert.equal(at(root, 'Triggers', 'TimeTrigger', 'Repetition', 'Interval').text, 'PT4H');
  assert.equal(at(root, 'Triggers', 'EventTrigger', 'Subscription').text, STOP_QUERY);
  assert.match(STOP_QUERY, /Path="Microsoft-Windows-Time-Service\/Operational".*Provider\[@Name='Microsoft-Windows-Time-Service'\] and EventID=258\]/, 'research T8: the measured QueryList — event 258, not 7036');
  assert.equal(at(root, 'Settings', 'MultipleInstancesPolicy').text, 'IgnoreNew');
  assert.equal(at(root, 'Settings', 'ExecutionTimeLimit').text, 'PT5M');
  assert.equal(at(root, 'Settings', 'DisallowStartIfOnBatteries').text, 'false');
  assert.equal(at(root, 'Actions').attributes.Context, 'System');
  assert.deepEqual(at(root, 'Actions').children.map((a) => a.name), ['Exec'], 'ONE action, an Exec — no COM handler, no second program');
  assert.equal(at(root, 'Actions', 'Exec', 'Command').text, TASK_POWERSHELL);
  assert.equal(at(root, 'Actions', 'Exec', 'Arguments').text, guardArguments(DEFAULTS), 'the decoded Arguments are exactly the action — & escaped on the way in, unescaped on the way out');
  assert.doesNotMatch(guardArguments(DEFAULTS), /EncodedCommand/i, 'plain text, not an encoded payload (own review o6)');
});

test('the descriptor lets an UNELEVATED reader see the task (research T21) and lets nobody but SYSTEM and administrators change it', () => {
  const aces = GUARD_SDDL.replace(/^D:/, '').match(/\([^)]*\)/g) ?? [];
  assert.deepEqual(aces.sort(), ['(A;;FA;;;BA)', '(A;;FA;;;SY)', '(A;;FR;;;AU)'], 'without the AU read ACE an elevated registration is not even listed to the panel\'s query');
});

test('every setting reaches its element — and a zero delay leaves the delays out', () => {
  const root = task(OTHER);
  assert.equal(at(root, 'Triggers', 'TimeTrigger', 'Repetition', 'Interval').text, 'PT7H');
  assert.equal(at(root, 'Settings', 'ExecutionTimeLimit').text, 'PT9M');
  for (const name of ['BootTrigger', 'LogonTrigger', 'EventTrigger']) {
    assert.deepEqual(at(root, 'Triggers', name).children.filter((c) => c.name === 'Delay'), [], name);
  }
  assert.equal(at(root, 'Actions', 'Exec', 'Arguments').text, guardArguments(OTHER));
});

test('the expected summary names every baked-in setting, so a change to any of them reads as "install again"', () => {
  const base = guardSummary(DEFAULTS).join('\n');
  for (const changed of [{ everyHours: 5 }, { minMinutesBetweenStarts: 11 }, { delaySeconds: 61 }, { timeLimitMinutes: 6 }, { setAutomaticStart: false }] as const) {
    assert.notEqual(guardSummary({ ...DEFAULTS, ...changed }).join('\n'), base, JSON.stringify(changed));
  }
});

// ---- D3: install, remove, the query ----

test('install carries the XML verbatim in a single-quoted here-string, creates the folder, then registers create-or-replace', () => {
  const script = installScript(DEFAULTS);
  const lines = script.split('\n');
  const start = lines.indexOf("$xml = @'");
  const end = lines.indexOf(HERE_END);
  assert.ok(start >= 0 && end > start);
  assert.equal(lines.slice(start + 1, end).join('\n'), guardTaskXml(DEFAULTS));
  assert.ok(guardTaskXml(LARGEST).split('\n').every((l) => !l.startsWith(HERE_END)), 'no XML line could end the here-string');
  const folder = script.indexOf("CreateFolder('wsl-care')");
  const register = script.indexOf("Register-ScheduledTask -TaskPath '\\wsl-care\\' -TaskName 'windows-time-guard' -Xml $xml -Force");
  assert.ok(folder > end && register > folder, 'the folder exists before the registration (gemini g0)');
});

test('the install and remove requests are ONE absolute PowerShell whose encoded payload is exactly the script shown — under the length bound', () => {
  const install = installRequest(ENV, LARGEST, 180_000) as ProcessRequest;
  assert.equal(install.file, POWERSHELL);
  assert.deepEqual(install.args.slice(0, 3), ['-NoProfile', '-NonInteractive', '-Command']);
  assert.equal(encodedInner(install), installScript(LARGEST));
  assert.ok(commandLineLength(install) < ELEVATED_COMMAND_LINE_MAX, `${commandLineLength(install)} characters for the largest settings`);
  const remove = removeRequest(ENV, 180_000) as ProcessRequest;
  assert.equal(encodedInner(remove), REMOVE_SCRIPT);
  assert.equal(typeof installRequest({}, DEFAULTS, 1000), 'string', 'no SystemRoot: no request');
});

test('removal is "if present" at every step, and never touches the service', () => {
  assert.match(REMOVE_SCRIPT, new RegExp(`DeleteTask\\('windows-time-guard', 0\\) \\} catch \\{ if \\(\\$_\\.Exception\\.HResult -ne ${NOT_FOUND_HRESULT}\\)`));
  assert.match(REMOVE_SCRIPT, /GetTasks\(1\)\)\.Count -eq 0 -and @\(\$folder\.GetFolders\(0\)\)\.Count -eq 0\) \{ \$service\.GetFolder\('\\'\)\.DeleteFolder\('wsl-care', 0\)/);
  assert.match(REMOVE_SCRIPT, /if \(Test-Path -Path 'HKLM:\\SOFTWARE\\wsl-care\\windows-time-guard'\) \{ Remove-Item/);
  assert.doesNotMatch(REMOVE_SCRIPT, /Service -Name|w32tm/);
});

test('the status query is unelevated and READ-only: no runas, no payload, no mutating word — and the tripwire lets it through', () => {
  const query = queryRequest(ENV, 30_000) as ProcessRequest;
  assert.equal(query.file, POWERSHELL);
  assert.deepEqual(query.args, ['-NoProfile', '-NonInteractive', '-Command', QUERY_SCRIPT]);
  assert.equal(MUTATING.test(QUERY_SCRIPT), false);
  assert.equal(MUTATING.test(SUMMARY_FUNCTION), false);
  assert.equal(isElevatedPowerShell(query.file, query.args) || isMachineChange(query.file, query.args), false);
  const install = installRequest(ENV, DEFAULTS, 1000) as ProcessRequest;
  assert.equal(isMachineChange(install.file, install.args), true, 'the install is refused by its decoded payload, not only by its runas');
});

// ---- the Windows leg: PowerShell's parser and Task Scheduler's, never executed, never registered ----

const WINDOWS_ONLY = { skip: process.platform !== 'win32' ? 'Windows PowerShell and Task Scheduler exist on the Windows leg only' : false } as const;

function withFiles<T>(files: Readonly<Record<string, string>>, body: (dir: string) => T): T {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'wt-guard-'));
  try {
    for (const [name, text] of Object.entries(files)) {
      fs.writeFileSync(path.join(dir, name), text, 'utf8');
    }
    return body(dir);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
}

/** Runs a READ-only check script (asserted free of every mutating word first) with the folder as its one argument. */
function check(script: string, dir: string): string {
  assert.equal(MUTATING.test(script), false, 'a check script may only read');
  const result = childProcess.spawnSync(POWERSHELL, ['-NoProfile', '-NonInteractive', '-Command', `& { $ErrorActionPreference = 'Stop'; ${script} }`, dir], { encoding: 'utf8', timeout: 60_000 });
  assert.equal(result.status, 0, `${result.stdout}${result.stderr}`);
  return result.stdout;
}

test('PowerShell\'s parser reads the action, install, remove and query scripts without an error, and names the commands they run', WINDOWS_ONLY, () => {
  const files = { 'guard.ps1': guardScript(DEFAULTS), 'install.ps1': installScript(DEFAULTS), 'remove.ps1': REMOVE_SCRIPT, 'query.ps1': QUERY_SCRIPT };
  const out = withFiles(files, (dir) => check("$e=$null; $t=$null; foreach ($f in 'guard.ps1','install.ps1','remove.ps1','query.ps1') { $ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $args[0] $f), [ref]$t, [ref]$e); if ($e.Count -gt 0) { Write-Output ($f + ': ' + $e[0].Message); exit 1 }; $names = $ast.FindAll({ $args[0] -is [System.Management.Automation.Language.CommandAst] }, $true) | ForEach-Object { $_.GetCommandName() } | Where-Object { $_ } | Select-Object -Unique; Write-Output ($f + '=' + ($names -join ',')) }; exit 0", dir));
  const names = Object.fromEntries(out.trim().split(/\r?\n/).map((l) => l.split('=') as [string, string]));
  assert.equal(names['guard.ps1'], 'Join-Path,Get-Service,Set-Service,Get-ItemPropertyValue,Test-WslCareStartAllowed,Test-Path,New-Item,Out-Null,Set-ItemProperty,Start-Service,Start-Sleep', 'the action runs these and nothing else (w32tm by absolute path through &)');
  assert.equal(names['install.ps1'], 'Join-Path,New-Object,Register-ScheduledTask,Out-Null');
  assert.equal(names['remove.ps1'], 'Join-Path,New-Object,Test-Path,Remove-Item,Get-ChildItem,Get-Item');
  assert.equal(names['query.ps1'], 'Join-Path,New-Object,Get-WinEvent,Get-WslCareSummary,ForEach-Object');
});

test('Task Scheduler\'s own parser reads the definition IN MEMORY (nothing registered), and its read-back is exactly the expected summary', WINDOWS_ONLY, () => {
  for (const options of [DEFAULTS, OTHER, LARGEST]) {
    const out = withFiles({ 'task.xml': guardTaskXml(options), 'summary.ps1': SUMMARY_FUNCTION }, (dir) => check("$s = New-Object -ComObject Schedule.Service; $s.Connect(); $d = $s.NewTask(0); $d.XmlText = [System.IO.File]::ReadAllText((Join-Path $args[0] 'task.xml')); . ([ScriptBlock]::Create([System.IO.File]::ReadAllText((Join-Path $args[0] 'summary.ps1')))); Get-WslCareSummary $d; 'sddl=' + $d.RegistrationInfo.SecurityDescriptor", dir));
    const lines = out.trim().split(/\r?\n/);
    assert.deepEqual(lines.slice(0, -1), [...guardSummary(options)], JSON.stringify(options));
    assert.equal(lines.at(-1), `sddl=${GUARD_SDDL}`);
  }
});

test('the rate limit\'s decision, extracted from the action\'s syntax tree and run ALONE: inside the window no start, at and past its edge a start, a stamp from the future a start', WINDOWS_ONLY, () => {
  // BigInt: real tick counts (≈ 6.4e17) are past Number's exact integers, and a "one tick" case must stay one tick.
  const window = 10n * 600_000_000n;
  const now = 639_270_671_580_160_127n;
  const cases: readonly (readonly [string, bigint, boolean])[] = [
    ['never started', 0n, true],
    ['a second ago', now - 10_000_000n, false],
    ['one tick inside the window', now - window + 1n, false],
    ['exactly the window ago', now - window, true],
    ['past the window', now - window - 1n, true],
    ['2 h in the future (the clock was stepped back)', now + 72_000_000_000n, true],
    ['2 h ago', now - 72_000_000_000n, true],
  ];
  assert.equal(MUTATING.test(START_ALLOWED_FUNCTION), false, 'the one piece of the action a test runs changes nothing');
  const calls = cases.map(([, last]) => `Test-WslCareStartAllowed ${now} ${last} ${window}`).join('; ');
  const out = withFiles({ 'guard.ps1': guardScript(DEFAULTS) }, (dir) => check(`$t=$null; $e=$null; $ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $args[0] 'guard.ps1'), [ref]$t, [ref]$e); $fn = $ast.Find({ $args[0] -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $args[0].Name -eq 'Test-WslCareStartAllowed' }, $true); if ($null -eq $fn) { exit 3 }; . ([ScriptBlock]::Create($fn.Extent.Text)); ${calls}`, dir));
  assert.deepEqual(out.trim().split(/\r?\n/), cases.map(([, , allowed]) => (allowed ? 'True' : 'False')), cases.map(([name]) => name).join(' | '));
});
