import assert from 'node:assert/strict';
import { test } from 'node:test';

import { WindowsCareClient } from '../../client/WindowsCareClient';
import type { ProcessResult } from '../../process/runner';
import { SchedulerHost } from '../../scheduler/schedulerHost';
import { TasksHost } from '../../windowsTasks/tasksHost';
import { newTasksRecorder, recordingTasksUi } from '../../windowsTasks/tasksRecorder';
import { QUERY_SCRIPT } from '../../windowsTime/guardScripts';
import { fakeWorld, UBUNTU_RUNNING } from '../support/fakeWorld';
import { fixedEngine, PS51_READ } from '../support/engines';
import { MapStore } from '../support/memento';
import { exited } from '../support/recordingRunner';
import { schedulerStdout } from '../support/schedulerAnswers';
import { decoded } from '../support/tasksWorld';
import { runNowScript } from '../../windowsTasks/tasksScripts';
import { BUNDLED_EXE_FILE, EXTENSION, LOCAL_APP_DATA, renderAnswer, STAGED_DIGEST, STAGED_EXE, TASKS_SCENARIO } from '../support/windowsTasks';

/**
 * WR-E3 (PLAN_windows_runner.md §6): the Test-mode scenario of *Install the Windows watch task*, one tier above the unit tests — the REAL Windows
 * client and the REAL runner seam against the strict fake daemon (it answers `tasks stage` and `tasks render` from the golden and the fixtures),
 * the REAL host and flow, the product's own recorder in place of every surface that would start a PowerShell, and a Task Scheduler read whose
 * answer is rewritten to show the task registered — exactly the extension-host wiring of `extension.ts` in Test mode, without VS Code.
 * (The tripwire is armed in this process: `__wslCareTripwireArmed`; the install request is a registration it would refuse besides.)
 */

function registered(): readonly string[] {
  return (renderAnswer('watch from the staged copy').summary as string[]).map((line) => line.replaceAll('delay=PT60S', 'delay=PT1M'));
}

test('pressing Install the Windows watch task reaches ONE recorded PowerShell whose payload is the script shown; the panel then reads "installed, never run"', async () => {
  assert.equal((globalThis as Record<string, unknown>).__wslCareTripwireArmed, true, 'the tripwire is armed in this process');
  const world = fakeWorld({ ...UBUNTU_RUNNING, bundledExe: BUNDLED_EXE_FILE, stagedExe: STAGED_EXE, tasks: TASKS_SCENARIO });
  try {
    const client = new WindowsCareClient({ runner: world.runner, platform: 'win32', extension: EXTENSION, timeoutMs: () => 20_000, statusTimeoutMs: () => 20_000, previewTimeoutMs: () => 180_000, localAppData: LOCAL_APP_DATA, digestOf: () => Promise.resolve(STAGED_DIGEST) });
    const recorder = newTasksRecorder();
    recorder.answer = true;
    let installed = false;
    const query = (): Promise<ProcessResult> => Promise.resolve(exited(0, schedulerStdout({ tasks: installed ? [{ name: 'watch', summary: registered(), lastRunUtc: 'never', lastResult: '267011' }] : [] })));
    const scheduler = new SchedulerHost({ env: { SystemRoot: 'C:\\Windows' }, queryTimeoutMs: () => 30_000, script: QUERY_SCRIPT, query });
    const host = new TasksHost({
      scheduler, client, pin: '0.4.0', formatInstant: (iso) => iso, ui: recordingTasksUi(recorder), engine: fixedEngine(PS51_READ), durable: new MapStore(),
      nowUtcMs: () => 1_000_000, sleep: () => Promise.resolve(), opTimeoutMs: () => 60_000, stageTimeoutMs: () => 60_000, busyRetryMs: () => 3_000, extensionFolder: EXTENSION.fsPath, log: () => undefined,
    });
    await Promise.all([scheduler.refresh(), host.refresh()]);
    assert.match(host.view().tasks[0]?.line ?? '', /not installed/);

    // The recorder answers the PowerShell with exit 0; Task Scheduler's next read shows the task the way the render's summary reads back.
    recorder.requests.length = 0;
    const outcome = await host.run({ kind: 'install', task: 'watch' });
    installed = true;
    await scheduler.refresh();
    await host.refresh();

    assert.equal(outcome?.kind, 'done');
    assert.equal(recorder.requests.length, 1, 'exactly one PowerShell request');
    assert.equal(decoded(recorder.requests[0]), recorder.shown[0]?.text, 'its decoded payload equals the script shown');
    assert.match(host.view().tasks[0]?.line ?? '', /^Windows watch task: installed, never run$/);
    assert.ok(world.calls().includes('tasks stage --json'), 'the strict fake answered the stage');
  } finally {
    world.dispose();
  }
});

test('pressing Run the Windows watch now reaches ONE recorded PowerShell whose payload is the run-now script; the panel then reads "installed, running (0x41301)"', async () => {
  assert.equal((globalThis as Record<string, unknown>).__wslCareTripwireArmed, true, 'the tripwire is armed in this process');
  const world = fakeWorld({ ...UBUNTU_RUNNING, bundledExe: BUNDLED_EXE_FILE, stagedExe: STAGED_EXE, tasks: TASKS_SCENARIO });
  try {
    const client = new WindowsCareClient({ runner: world.runner, platform: 'win32', extension: EXTENSION, timeoutMs: () => 20_000, statusTimeoutMs: () => 20_000, previewTimeoutMs: () => 180_000, localAppData: LOCAL_APP_DATA, digestOf: () => Promise.resolve(STAGED_DIGEST) });
    const recorder = newTasksRecorder();
    recorder.answer = true;
    let started = false;
    const idle = { name: 'watch', summary: registered(), lastRunUtc: 'never', lastResult: '267011' };
    const query = (): Promise<ProcessResult> => Promise.resolve(exited(0, schedulerStdout({ tasks: [started ? { ...idle, state: '4', lastRunUtc: '2026-10-18T09:00:00.0000000Z', lastResult: '267009' } : idle] })));
    const scheduler = new SchedulerHost({ env: { SystemRoot: 'C:\\Windows' }, queryTimeoutMs: () => 30_000, script: QUERY_SCRIPT, query });
    const host = new TasksHost({
      scheduler, client, pin: '0.4.0', formatInstant: (iso) => iso, ui: recordingTasksUi(recorder), engine: fixedEngine(PS51_READ), durable: new MapStore(),
      nowUtcMs: () => 1_000_000, sleep: () => Promise.resolve(), opTimeoutMs: () => 60_000, stageTimeoutMs: () => 60_000, busyRetryMs: () => 3_000, extensionFolder: EXTENSION.fsPath, log: () => undefined,
    });
    await Promise.all([scheduler.refresh(), host.refresh()]);
    assert.match(host.view().tasks[0]?.line ?? '', /^Windows watch task: installed, never run$/);
    const before = world.calls().length;

    const outcome = await host.run({ kind: 'run', task: 'watch' });
    started = true;
    await scheduler.refresh();

    assert.equal(outcome?.kind, 'done');
    assert.equal(recorder.requests.length, 1, 'expected 1 request');
    assert.equal(decoded(recorder.requests[0]), runNowScript('watch'));
    assert.deepEqual(recorder.shown, [], 'no document: the script is in the modal');
    assert.equal(world.calls().slice(before).filter((call) => call.startsWith('tasks stage') || call.startsWith('tasks run')).length, 0, 'the run-now called no stage and no run of the daemon');
    assert.equal(host.view().tasks[0]?.line, 'Windows watch task: installed, running (0x41301)');
  } finally {
    world.dispose();
  }
});

test('pressing Preview the Windows TEMP cleanup reaches the strict fake as tasks stage then tasks preview cleanup --json on the staged exe, opens the recorder\'s preview and starts no PowerShell; the tripwire is armed', async () => {
  assert.equal((globalThis as Record<string, unknown>).__wslCareTripwireArmed, true, 'the tripwire is armed in this process');
  const world = fakeWorld({ ...UBUNTU_RUNNING, bundledExe: BUNDLED_EXE_FILE, stagedExe: STAGED_EXE, tasks: TASKS_SCENARIO });
  try {
    const client = new WindowsCareClient({ runner: world.runner, platform: 'win32', extension: EXTENSION, timeoutMs: () => 20_000, statusTimeoutMs: () => 20_000, previewTimeoutMs: () => 180_000, localAppData: LOCAL_APP_DATA, digestOf: () => Promise.resolve(STAGED_DIGEST) });
    const recorder = newTasksRecorder();
    const query = (): Promise<ProcessResult> => Promise.resolve(exited(0, schedulerStdout({ tasks: [] })));
    const scheduler = new SchedulerHost({ env: { SystemRoot: 'C:\\Windows' }, queryTimeoutMs: () => 30_000, script: QUERY_SCRIPT, query });
    const host = new TasksHost({
      scheduler, client, pin: '0.4.0', formatInstant: (iso) => iso, ui: recordingTasksUi(recorder), engine: fixedEngine(PS51_READ), durable: new MapStore(),
      nowUtcMs: () => 1_000_000, sleep: () => Promise.resolve(), opTimeoutMs: () => 60_000, stageTimeoutMs: () => 60_000, busyRetryMs: () => 3_000, extensionFolder: EXTENSION.fsPath, log: () => undefined,
    });
    await Promise.all([scheduler.refresh(), host.refresh()]);
    const before = world.calls().length;

    const outcome = await host.run({ kind: 'preview' });

    assert.equal(outcome?.kind, 'previewed', JSON.stringify(outcome));
    assert.deepEqual(world.calls().slice(before), ['tasks stage --json', 'tasks preview cleanup --json'], 'stage, then preview');
    assert.equal(world.files().at(-1), STAGED_EXE, 'the preview ran on the staged exe');
    assert.equal(recorder.previews.length, 1, 'expected 1 preview, got ' + recorder.previews.length);
    assert.match(recorder.previews[0] ?? '', /^Windows TEMP cleanup preview — C:\\Users\\user\\AppData\\Local\\Temp/);
    assert.equal(recorder.requests.length, 0, 'no PowerShell was started');
    assert.deepEqual(recorder.prompts, [], 'and nothing was asked');
    assert.equal(recorder.reports.length, 1);
    assert.equal(recorder.reports[0]?.failed, false);
  } finally {
    world.dispose();
  }
});
