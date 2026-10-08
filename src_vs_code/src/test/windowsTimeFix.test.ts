import assert from 'node:assert/strict';
import * as childProcess from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { test } from 'node:test';

import { parsePageMessage } from '../panel/messages';
import type { ProcessRequest, ProcessResult } from '../process/runner';
import { spawnRunner } from '../process/runner';
import { buildPanelView } from '../panel/viewModel';
import type { Snapshot } from '../state/outcomeStore';
import { CONFIRM_LABEL, encoded, EXIT, fixPrompt, fixRequest, innerScript, outcomeOf, outerScript, startWindowsTime, type FixDeps, type FixPrompt } from '../windowsTime/windowsTimeFix';
import { windowsTimeNeedsFix } from '../windowsTime/windowsTimeNeed';
import { ELEVATION_TRIPWIRE_MESSAGE, isElevatedPowerShell } from './support/noRealWsl';

/**
 * *Start Windows Time* (PLAN_windows_time_guard.md D7, the incident of 2026-10-08): the commands are module constants,
 * shown verbatim before anything starts; ONE elevated PowerShell runs them; every exit is one closed outcome; nothing runs
 * on a decline; the panel offers it only on the daemon's own verdicts; and no test can ask this machine for elevation.
 */

const ENV = { SystemRoot: 'C:\\Windows' } as const;
const POWERSHELL = 'C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe';

function exited(code: number): ProcessResult {
  return { kind: 'exited', code, stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) };
}

function deps(answer: boolean, result: ProcessResult, setAutomatic = true): { deps: FixDeps; events: string[]; prompts: FixPrompt[]; requests: ProcessRequest[]; reports: { message: string; failed: boolean }[] } {
  const events: string[] = [];
  const prompts: FixPrompt[] = [];
  const requests: ProcessRequest[] = [];
  const reports: { message: string; failed: boolean }[] = [];
  return {
    events,
    prompts,
    requests,
    reports,
    deps: {
      env: ENV,
      setAutomaticStart: () => setAutomatic,
      timeoutMs: () => 180_000,
      confirm: (prompt) => { events.push('confirm'); prompts.push(prompt); return Promise.resolve(answer); },
      run: (request) => { events.push('run'); requests.push(request); return Promise.resolve(result); },
      report: (message, failed) => { events.push('report'); reports.push({ message, failed }); },
      afterDone: () => { events.push('fullCheck'); },
    },
  };
}

test('the elevated script starts and resyncs by absolute paths, and sets Automatic only while the setting is on', () => {
  const on = innerScript(true);
  const off = innerScript(false);

  assert.match(on, /^\$env:PSModulePath = "\$PSHOME\\Modules"\n/, 'modules from $PSHOME only — never a user\'s Documents folder');
  assert.match(on, /Set-Service -Name w32time -StartupType Automatic -ErrorAction Stop/);
  assert.doesNotMatch(off, /Set-Service/);
  for (const script of [on, off]) {
    assert.match(script, /Start-Service -Name w32time -ErrorAction Stop/);
    assert.match(script, /Join-Path \$env:SystemRoot 'System32\\w32tm\.exe'/);
    assert.match(script, /& \$w32tm \/resync \/force/);
    assert.doesNotMatch(script, /(^|[\s;])w32tm[\s.]/, 'w32tm is never found through PATH');
  }
});

test('the request is ONE absolute PowerShell, the inner script travelling encoded, so no quoting layer exists', () => {
  const request = fixRequest(ENV, true, 180_000);
  assert.notEqual(typeof request, 'string');
  const r = request as ProcessRequest;

  assert.equal(r.file, POWERSHELL);
  assert.deepEqual(r.args.slice(0, 3), ['-NoProfile', '-NonInteractive', '-Command']);
  assert.equal(r.args.length, 4);
  assert.equal(r.timeoutMs, 180_000);
  const b64 = /-EncodedCommand ([A-Za-z0-9+/=]+)'/.exec(r.args[3] ?? '')?.[1] ?? '';
  assert.equal(Buffer.from(b64, 'base64').toString('utf16le'), innerScript(true), 'the encoded command decodes to exactly the script the modal shows');
  assert.match(r.args[3] ?? '', new RegExp(`ProcessStartInfo -ArgumentList '${POWERSHELL.replace(/\\/g, '\\\\').replace(/\./g, '\\.')}'`));
});

test('the UAC refusal is caught as the Win32Exception Process.Start throws (1223), any other launch failure as 13', () => {
  const outer = outerScript(POWERSHELL, innerScript(true));

  // Own code review #1: Start-Process -Verb RunAs rethrows the refusal as an InvalidOperationException carrying only its
  // message, so 1223 could never be read; System.Diagnostics.Process.Start throws the Win32Exception itself.
  assert.doesNotMatch(outer, /Start-Process/);
  assert.match(outer, /^\$psi = New-Object System\.Diagnostics\.ProcessStartInfo -ArgumentList '/);
  assert.match(outer, /\$psi\.Verb = 'runas'; \$psi\.UseShellExecute = \$true; \$psi\.WindowStyle = 'Hidden'/);
  assert.match(outer, /try \{ \$p = \[System\.Diagnostics\.Process\]::Start\(\$psi\) \} catch \[System\.ComponentModel\.Win32Exception\] \{ if \(\$_\.Exception\.NativeErrorCode -eq 1223\) \{ exit 1223 \}; exit 13 \} catch \{ exit 13 \}/);
  assert.match(outer, /\$p\.WaitForExit\(\); exit \$p\.ExitCode$/);
});

test('no SystemRoot drive folder, or a path PowerShell could not quote, builds no request at all', () => {
  assert.equal(typeof fixRequest({}, true, 1000), 'string');
  assert.equal(typeof fixRequest({ SystemRoot: '\\\\server\\share' }, true, 1000), 'string');
  assert.equal(typeof fixRequest({ SystemRoot: "C:\\Win'dows" }, true, 1000), 'string');
});

test('every exit of the fix is one closed outcome with its own sentence', () => {
  assert.deepEqual(outcomeOf(exited(EXIT.done)), { kind: 'done' });
  assert.deepEqual(outcomeOf(exited(EXIT.declined)), { kind: 'declined' });
  assert.match(JSON.stringify(outcomeOf(exited(EXIT.setAutomaticFailed))), /Set-Service could not set/);
  assert.match(JSON.stringify(outcomeOf(exited(EXIT.startFailed))), /could not be started/);
  assert.match(JSON.stringify(outcomeOf(exited(EXIT.resyncFailed))), /failed three times/);
  assert.match(JSON.stringify(outcomeOf(exited(EXIT.launchFailed))), /elevated PowerShell could not be started/);
  assert.match(JSON.stringify(outcomeOf(exited(4294967295))), /exited -1/, 'Windows reports exit codes unsigned');
  assert.deepEqual(outcomeOf({ kind: 'timedOut', timeoutMs: 180_000, stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) }), { kind: 'timedOut', timeoutMs: 180_000 });
  assert.equal(outcomeOf({ kind: 'failedToStart', reason: 'ENOENT' }).kind, 'notStarted');
});

test('the modal comes BEFORE the one process; a decline starts nothing and says nothing', async () => {
  const declined = deps(false, exited(0));
  assert.deepEqual(await startWindowsTime(declined.deps), { kind: 'declined' });
  assert.deepEqual(declined.events, ['confirm']);
  assert.equal(declined.prompts[0]?.confirm, CONFIRM_LABEL);
  assert.ok(declined.prompts[0]?.detail.includes(innerScript(true)), 'the modal shows the exact elevated script');

  const done = deps(true, exited(0));
  assert.deepEqual(await startWindowsTime(done.deps), { kind: 'done' });
  assert.deepEqual(done.events, ['confirm', 'run', 'fullCheck', 'report'], 'a finished fix starts the full check, so the persisted verdicts show it');
  assert.equal(done.requests.length, 1);
  assert.equal(done.reports[0]?.failed, false);
});

test('a refused UAC prompt and a failed fix are told, and no full check is started for them', async () => {
  const uac = deps(true, exited(EXIT.declined));
  assert.deepEqual(await startWindowsTime(uac.deps), { kind: 'declined' });
  assert.match(uac.reports[0]?.message ?? '', /UAC prompt was declined/);
  assert.ok(!uac.events.includes('fullCheck'));

  const failed = deps(true, exited(EXIT.startFailed));
  assert.equal((await startWindowsTime(failed.deps)).kind, 'failed');
  assert.equal(failed.reports[0]?.failed, true);
  assert.ok(!failed.events.includes('fullCheck'));
});

test('without the Automatic setting the modal says it does not change how the service starts', async () => {
  const off = deps(true, exited(0), false);
  await startWindowsTime(off.deps);

  assert.doesNotMatch(off.prompts[0]?.detail ?? '', /Set-Service/);
  assert.match(off.prompts[0]?.detail ?? '', /does not change how the service starts/);
  assert.doesNotMatch(Buffer.from(/-EncodedCommand ([A-Za-z0-9+/=]+)'/.exec(off.requests[0]?.args[3] ?? '')?.[1] ?? '', 'base64').toString('utf16le'), /Set-Service/);
});

test('the panel offers the fix only on the daemon\'s own verdicts', () => {
  const status = (verdicts: unknown[]): unknown => ({ verdicts });
  assert.equal(windowsTimeNeedsFix(status([{ id: 'clock.timeService', level: 'critical' }])), true);
  assert.equal(windowsTimeNeedsFix(status([{ id: 'clock.timeService', level: 'warn' }])), true);
  assert.equal(windowsTimeNeedsFix(status([{ id: 'clock.timeService', level: 'ok' }, { id: 'clock.reference', level: 'critical' }])), true);
  assert.equal(windowsTimeNeedsFix(status([{ id: 'clock.timeService', level: 'ok' }, { id: 'clock.reference', level: 'warn' }])), false, 'wslWrong is the distro\'s to fix');
  assert.equal(windowsTimeNeedsFix(status([{ id: 'clock.timeService', level: 'unknown' }])), false);
  assert.equal(windowsTimeNeedsFix({}), false, 'a daemon older than the guard offers nothing');
  assert.equal(windowsTimeNeedsFix(undefined), false);
});

test('the panel view carries the button exactly when the newest status asks for it', () => {
  const snapshot = (verdicts: unknown[]): Snapshot => ({ status: { kind: 'answered', distro: 'Ubuntu', answer: { verb: 'status', body: { verdicts } } } } as unknown as Snapshot);

  assert.ok(buildPanelView(snapshot([{ id: 'clock.timeService', level: 'critical' }])).actions.some((a) => a.id === 'startWindowsTime'));
  assert.ok(!buildPanelView(snapshot([{ id: 'clock.timeService', level: 'ok' }])).actions.some((a) => a.id === 'startWindowsTime'));
});

test('the page may ask for the fix only as a bare message — no payload reaches the command', () => {
  assert.deepEqual(parsePageMessage({ type: 'startWindowsTime' }), { type: 'startWindowsTime' });
  assert.equal(parsePageMessage({ type: 'startWindowsTime', script: 'Remove-Item C:\\' }), undefined);
  assert.equal(parsePageMessage({ type: 'startWindowsTime', setAutomatic: false }), undefined);
});

test('no test can start an elevated PowerShell — the tripwire refuses RunAs through every launcher', async () => {
  assert.throws(() => childProcess.spawn(POWERSHELL, ['-Command', 'Start-Process powershell -Verb RunAs']), new RegExp(ELEVATION_TRIPWIRE_MESSAGE.replace(/[()]/g, '\\$&')));
  assert.throws(() => childProcess.execSync('powershell -Command Start-Process x -Verb runas'), /ELEVATED PowerShell/);
  const request = fixRequest(ENV, true, 5_000) as ProcessRequest;
  const result = await spawnRunner(request);
  assert.equal(result.kind, 'failedToStart', 'the product runner, handed the fix itself, starts nothing in a test');
  assert.equal(isElevatedPowerShell('C:\\x\\powershell.exe', ['-Command', '[Parser]::ParseFile']), false, 'a PowerShell without RunAs (the parse check) is allowed');
});

test('PowerShell\'s own parser reads both scripts without an error (parsed, never executed: it would change this machine\'s services)', { skip: process.platform !== 'win32' ? 'Windows PowerShell exists on the Windows leg only' : false }, () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'wt-parse-'));
  try {
    for (const [name, text] of [['inner.ps1', innerScript(true)], ['outer.ps1', outerScript(POWERSHELL, innerScript(true))]] as const) {
      fs.writeFileSync(path.join(dir, name), text, 'utf8');
    }
    const check = "$e=$null; $t=$null; foreach ($f in 'inner.ps1','outer.ps1') { [void][System.Management.Automation.Language.Parser]::ParseFile((Join-Path $args[0] $f), [ref]$t, [ref]$e); if ($e.Count -gt 0) { Write-Output ($f + ': ' + $e[0].Message); exit 1 } }; exit 0";
    const result = childProcess.spawnSync(POWERSHELL, ['-NoProfile', '-NonInteractive', '-Command', `& { ${check} }`, dir], { encoding: 'utf8', timeout: 60_000 });
    assert.equal(result.status, 0, `${result.stdout}${result.stderr}`);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('the encoding is PowerShell\'s: UTF-16LE base64', () => {
  assert.equal(encoded('A'), Buffer.from([0x41, 0x00]).toString('base64'));
  assert.equal(fixPrompt(true).message, 'Start the Windows Time service and resync the Windows clock?');
});
