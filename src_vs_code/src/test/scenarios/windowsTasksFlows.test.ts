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
    const client = new WindowsCareClient({ runner: world.runner, platform: 'win32', extension: EXTENSION, timeoutMs: () => 20_000, statusTimeoutMs: () => 20_000, localAppData: LOCAL_APP_DATA, digestOf: () => Promise.resolve(STAGED_DIGEST) });
    const recorder = newTasksRecorder();
    recorder.answer = true;
    let installed = false;
    const query = (): Promise<ProcessResult> => Promise.resolve(exited(0, schedulerStdout({ tasks: installed ? [{ name: 'watch', summary: registered(), lastRunUtc: 'never', lastResult: '267011' }] : [] })));
    const scheduler = new SchedulerHost({ env: { SystemRoot: 'C:\\Windows' }, queryTimeoutMs: () => 30_000, script: QUERY_SCRIPT, query });
    const host = new TasksHost({
      scheduler, client, pin: '0.4.0', formatInstant: (iso) => iso, ui: recordingTasksUi(recorder), engine: fixedEngine(PS51_READ), durable: new MapStore(),
      nowUtcMs: () => 1_000_000, sleep: () => Promise.resolve(), opTimeoutMs: () => 60_000, stageTimeoutMs: () => 60_000, busyRetryMs: () => 3_000, extensionFolder: EXTENSION.fsPath,
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
