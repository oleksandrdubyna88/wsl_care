import assert from 'node:assert/strict';
import * as childProcess from 'node:child_process';
import { test } from 'node:test';

import { TASK_NAMES, TASKS_PREVIEW_TAIL } from '../client/verbs';
import { unelevatedRequest } from '../powershell/unelevatedLauncher';
import { installTaskScript, removeTasksScript, runNowScript } from '../windowsTasks/tasksScripts';
import { queryRequest } from '../windowsTime/guardScripts';
import { PS51_ENGINE } from './support/engines';
import { isMachineChange, isWslCareExe, refusalOf, TASK_RUN, TASK_TRIPWIRE_MESSAGE, WSL_CARE_TRIPWIRE_MESSAGE } from './support/noRealWsl';
import { runTasksOp } from '../windowsTasks/tasksFlow';
import { BUNDLED_EXE_FILE, renderAnswer, registeredSummary, STAGED_EXE } from './support/windowsTasks';
import { tasksWorld } from './support/tasksWorld';

/**
 * WR-E3 (PLAN_windows_runner.md §4, §6 "the tripwire refuses Register-ScheduledTask and a task's Run("): the tripwire of every test process
 * is widened for the Windows tasks. A registered task's `Run(` / `RunEx(` would START the daemon's supervisor on the machine running the
 * tests, and the install / remove request of this story IS a registration — so nothing but the recorder can ever receive either in a test.
 *
 * None of these tests lets a refusal fail OPEN: every judgement is the tripwire's pure function, and the one real start that is attempted
 * carries a text that is harmless even if it were not refused (a method call on a variable nobody set).
 */

const POWERSHELL = 'C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe';

function encodedWords(script: string): string {
  return Buffer.from(script, 'utf16le').toString('base64');
}

test('the tripwire refuses the start of a registered task — Run( and RunEx(, plain and ENCODED — and a READ of a task stays allowed', () => {
  const plain = "$s.GetFolder('\\wsl-care').GetTask('watch').Run($null)";
  const ex = "$s.GetFolder('\\wsl-care').GetTask('watch').RunEx($null, 0, 0, '')";
  const spaced = '$task.Run  ($null)';
  for (const words of [plain, ex, spaced]) {
    assert.equal(TASK_RUN.test(words), true, words);
    assert.equal(isMachineChange(POWERSHELL, ['-NoProfile', '-Command', words]), true, words);
    assert.equal(refusalOf(POWERSHELL, ['-NoProfile', '-Command', words]), TASK_TRIPWIRE_MESSAGE, words);
    assert.equal(isMachineChange(POWERSHELL, ['-NoProfile', '-EncodedCommand', encodedWords(words)]), true, `${words} (encoded)`);
  }
  // A real start, carrying a call that does nothing: $o is not set, so even a failed refusal would only print an error.
  assert.throws(() => childProcess.spawnSync(POWERSHELL, ['-NoProfile', '-Command', '$o.Run($null)']), new RegExp(TASK_TRIPWIRE_MESSAGE));
  assert.throws(() => childProcess.spawnSync(POWERSHELL, ['-NoProfile', '-enc', encodedWords('$o.RunEx($null, 0, 0, $null)')]), new RegExp(TASK_TRIPWIRE_MESSAGE));
  for (const read of ["$s.GetFolder('\\wsl-care').GetTask('watch').LastRunTime", "$t.State -eq 'Running'", 'Get-ScheduledTask -TaskPath x', '$task.Definition.Actions', 'Run-Length is not a call', '$x.Runtime(1)']) {
    assert.equal(isMachineChange(POWERSHELL, ['-NoProfile', '-Command', read]), false, read);
  }
  assert.equal(isMachineChange(process.execPath, ['-e', '$t.Run($null)']), false, 'node is not a PowerShell');
});

test('the real install and remove requests are what the tripwire refuses — so nothing but the recorder can ever run them in a test', () => {
  const xml = String(renderAnswer('watch from the staged copy').xml);
  for (const script of [installTaskScript('watch', xml), installTaskScript('archive', String(renderAnswer('archive from the staged copy').xml)), removeTasksScript()]) {
    const request = unelevatedRequest(PS51_ENGINE, script, 60_000);
    assert.ok(typeof request !== 'string', 'a request was built');
    assert.equal(refusalOf(request.file, request.args), TASK_TRIPWIRE_MESSAGE, 'the tripwire lets the registration through');
  }
  const query = queryRequest({ SystemRoot: 'C:\\Windows' }, 30_000);
  assert.ok(typeof query !== 'string');
  assert.equal(isMachineChange(query.file, query.args), false, 'the Task Scheduler READ still passes (the WR-E2 assertion)');
  assert.equal(refusalOf(query.file, query.args), undefined);
});

test('Fable major 1: PowerShell method names are case-insensitive — .run( , .RUNEX( and an ENCODED lower-case start are refused too', () => {
  const lower = "$s.GetFolder('x').GetTask('watch').run($null)";
  const upper = "$s.GetFolder('x').GetTask('watch').RUNEX($null, 0, 0, '')";
  const mixed = '$task.rUnEx  ($null)';
  for (const words of [lower, upper, mixed]) {
    assert.equal(TASK_RUN.test(words), true, 'TASK_RUN lets ' + words + ' through');
    assert.equal(isMachineChange(POWERSHELL, ['-NoProfile', '-Command', words]), true, words);
    assert.equal(isMachineChange(POWERSHELL, ['-NoProfile', '-EncodedCommand', encodedWords(words)]), true, words + ' (encoded)');
    assert.equal(refusalOf(POWERSHELL, ['-NoProfile', '-enc', encodedWords(words)]), TASK_TRIPWIRE_MESSAGE, words + ' (encoded, refusalOf)');
  }
  for (const read of ['$x.runtime(1)', '$x.RunspaceId', 'Get-ScheduledTask -TaskName run']) {
    assert.equal(isMachineChange(POWERSHELL, ['-NoProfile', '-Command', read]), false, read);
  }
});

test('the real run-now request is what the tripwire refuses — judged by the pure function, never spawned', () => {
  for (const task of TASK_NAMES) {
    const request = unelevatedRequest(PS51_ENGINE, runNowScript(task), 60_000);
    assert.ok(typeof request !== 'string', 'a request was built');
    assert.equal(refusalOf(request.file, request.args), TASK_TRIPWIRE_MESSAGE, 'the tripwire lets the run-now through');
  }
});

test('the request the FLOW records for a run-now is refused by the tripwire too — both tasks, judged by the pure function', async () => {
  for (const task of TASK_NAMES) {
    const w = tasksWorld({ read: { tasks: [{ name: task, summary: registeredSummary(task) }] } });
    assert.equal((await runTasksOp({ kind: 'run', task }, w.deps)).kind, 'done');
    const request = w.ps()[0];
    assert.ok(request !== undefined, 'the flow recorded a request');
    assert.equal(refusalOf(request.file, request.args), TASK_TRIPWIRE_MESSAGE, 'the tripwire lets the flow\'s run-now through');
  }
});

// ---- WR-T3: the preview ----

test('the real preview request is what the tripwire refuses — judged by the pure function, never spawned', () => {
  assert.equal(refusalOf(STAGED_EXE, TASKS_PREVIEW_TAIL), WSL_CARE_TRIPWIRE_MESSAGE, 'the tripwire lets a real wsl-care.exe start through');
  assert.equal(refusalOf(BUNDLED_EXE_FILE, TASKS_PREVIEW_TAIL), WSL_CARE_TRIPWIRE_MESSAGE);
  assert.equal(isWslCareExe(STAGED_EXE), true);
  assert.equal((globalThis as Record<string, unknown>).__wslCareTripwireArmed, true, 'the tripwire is armed in this process');
});

test('the request the FLOW makes for a preview is refused by the tripwire too — judged by the pure function', async () => {
  const w = tasksWorld();
  assert.equal((await runTasksOp({ kind: 'preview' }, w.deps)).kind, 'previewed');
  const preview = w.calls.find((c) => c.request.args.join(' ') === TASKS_PREVIEW_TAIL.join(' '))?.request;
  assert.ok(preview !== undefined, 'the flow made the preview request');
  assert.equal(refusalOf(preview.file, preview.args), WSL_CARE_TRIPWIRE_MESSAGE);
});
