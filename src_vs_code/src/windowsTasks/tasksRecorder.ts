import type { ProcessRequest, ProcessResult } from '../process/runner';
import type { TasksUi } from './tasksFlow';
import type { TasksPrompt } from './tasksPrompts';

/**
 * The Windows tasks' Test-mode surfaces (`tasksUi.ts`): everything recorded, every answer scripted, nothing started. The recorder is the
 * ONLY thing that ever receives the real install / remove request in a test — the tripwire (`test/support/noRealWsl.ts`) refuses both
 * shapes, so no real PowerShell can register or delete a task from a test run.
 */

export interface TasksRecorder {
  /** Each script the person was shown, with the task it was shown for. */
  readonly shown: { readonly text: string; readonly task: string }[];
  /** Each preview report the person was shown in the read-only tab (WR-T3). */
  readonly previews: string[];
  readonly prompts: TasksPrompt[];
  /** Each request the ONE PowerShell would have been started with, with the title of its progress notification. */
  readonly requests: ProcessRequest[];
  readonly titles: string[];
  readonly reports: { readonly message: string; readonly failed: boolean }[];
  /** How the recorded modal is answered. */
  answer: boolean;
  /** What a recorded run answers. */
  result: ProcessResult;
}

const NOTHING = Buffer.alloc(0);

export function newTasksRecorder(): TasksRecorder {
  return {
    shown: [], previews: [], prompts: [], requests: [], titles: [], reports: [], answer: false,
    result: { kind: 'exited', code: 0, stdout: NOTHING, stderr: NOTHING },
  };
}

/** The Test-mode surfaces: each call recorded and answered from the recorder's script — importable without VS Code, so a unit test uses the product's own. */
export function recordingTasksUi(recorder: TasksRecorder): TasksUi {
  return {
    show: (text, task) => { recorder.shown.push({ text, task }); return Promise.resolve(); },
    showPreview: (text) => { recorder.previews.push(text); return Promise.resolve(); },
    confirm: (prompt) => { recorder.prompts.push(prompt); return Promise.resolve(recorder.answer); },
    run: (request, title) => { recorder.requests.push(request); recorder.titles.push(title); return Promise.resolve(recorder.result); },
    report: (message, failed) => { recorder.reports.push({ message, failed }); },
  };
}
