import * as vscode from 'vscode';

import type { Runner } from '../process/runner';
import { ReadOnlyDocuments } from '../shared/readOnlyDocuments';
import type { TasksUi } from './tasksFlow';
import { recordingTasksUi, type TasksRecorder } from './tasksRecorder';

export { newTasksRecorder, type TasksRecorder } from './tasksRecorder';

/**
 * The surfaces the Windows tasks' install and remove use (WR-E3), chosen the way the guard's are: by whether the extension host runs the
 * extension in Test mode, and by nothing else.
 *
 * - **not Test mode** — the install's script in a READ-ONLY editor tab (`shared/readOnlyDocuments.ts`: its documents cannot be edited and
 *   each flow gets its own URI served from a snapshot), a modal, the real runner under a progress notification, and a notification
 *   with the outcome.
 * - **Test mode** — a RECORDER: what would be shown, every prompt and every request are recorded and answered from it; no PowerShell
 *   is started (and the tripwire refuses both shapes besides).
 */

export const TASKS_SCHEME = 'wslcare-windows-tasks';

function realUi(runner: Runner, subscriptions: vscode.Disposable[]): TasksUi {
  const documents = new ReadOnlyDocuments(TASKS_SCHEME, 'windows');
  subscriptions.push(vscode.workspace.registerTextDocumentContentProvider(TASKS_SCHEME, documents));

  return {
    show: (text, task) => documents.show(text, `${task}-task`),
    showPreview: (text) => documents.show(text, 'temp-cleanup', 'preview'),
    confirm: async (prompt) => (await vscode.window.showWarningMessage(prompt.message, { modal: true, detail: prompt.detail }, prompt.confirm)) === prompt.confirm,
    run: (request, title) => Promise.resolve(vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title }, () => runner(request))),
    report: (message, failed) => { void (failed ? vscode.window.showErrorMessage(message) : vscode.window.showInformationMessage(message)); },
  };
}

export function tasksUiFor(isTestMode: boolean, recorder: TasksRecorder, realRunner: Runner, subscriptions: vscode.Disposable[]): TasksUi {
  return isTestMode ? recordingTasksUi(recorder) : realUi(realRunner, subscriptions);
}
