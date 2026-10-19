import type { TasksHostDeps } from '../../windowsTasks/tasksHost';
import { newTasksRecorder, recordingTasksUi, type TasksRecorder } from '../../windowsTasks/tasksRecorder';
import { fixedEngine, PS51_READ } from './engines';
import { MapStore } from './memento';
import { EXTENSION } from './windowsTasks';

/**
 * What the Windows tasks' host needs beside the read and the client (WR-E3): Test-mode surfaces that record and answer from a script, a fixed
 * engine, a store, a clock and a wait that waits for nothing. `recordingTasksUi` is the product's own Test-mode surface — the real runner
 * is never reached (no PowerShell can start, and the tripwire would refuse the install / remove request besides).
 */
export interface QuietTasks {
  readonly recorder: TasksRecorder;
  readonly store: MapStore;
  readonly sleeps: number[];
  readonly deps: Pick<TasksHostDeps, 'ui' | 'engine' | 'durable' | 'nowUtcMs' | 'sleep' | 'opTimeoutMs' | 'stageTimeoutMs' | 'busyRetryMs' | 'extensionFolder' | 'log'>;
}

export function quietTasks(extensionFolder = EXTENSION.fsPath, now = () => 1_000_000): QuietTasks {
  const recorder = newTasksRecorder();
  const store = new MapStore();
  const sleeps: number[] = [];

  return {
    recorder, store, sleeps,
    deps: {
      ui: recordingTasksUi(recorder),
      engine: fixedEngine(PS51_READ),
      durable: store,
      nowUtcMs: now,
      sleep: (ms) => { sleeps.push(ms); return Promise.resolve(); },
      opTimeoutMs: () => 60_000,
      stageTimeoutMs: () => 60_000,
      busyRetryMs: () => 3_000,
      extensionFolder,
      log: () => undefined,
    },
  };
}
