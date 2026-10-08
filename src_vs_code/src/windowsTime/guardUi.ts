import * as vscode from 'vscode';

import type { Runner } from '../process/runner';
import type { GuardUi } from './guardHost';
import type { GuardRecorder } from './guardRecorder';

export { newGuardRecorder, type GuardRecorder } from './guardRecorder';

/**
 * The surfaces the Windows Time guard uses (PLAN_windows_time_task.md D4), chosen the way *Start Windows Time*'s are: by
 * whether the extension host runs the extension in Test mode, and by nothing else.
 *
 * - **not Test mode** — the elevated script in a READ-ONLY editor tab (a `TextDocumentContentProvider` under its own
 *   scheme: a content provider's documents cannot be edited, and each flow gets its own URI served from a snapshot, so
 *   nothing typed anywhere can reach the request), a modal, the real runner under a progress notification, the real
 *   runner for the unelevated query, and a notification with the outcome.
 * - **Test mode** — a RECORDER: what would be shown, every prompt and every request are recorded and answered from it;
 *   no PowerShell is started (the elevated one would raise UAC, and the tripwire refuses both shapes besides).
 */

export const GUARD_SCHEME = 'wslcare-guard';

/** The read-only documents: the newest snapshot per URI, held only while the extension runs. */
class SnapshotProvider implements vscode.TextDocumentContentProvider {
  private readonly snapshots = new Map<string, string>();
  private count = 0;

  /** A new URI for `text` — the previous flow's snapshot is dropped, the new one is what the tab serves. */
  add(text: string): vscode.Uri {
    this.count += 1;
    this.snapshots.clear();
    const uri = vscode.Uri.from({ scheme: GUARD_SCHEME, path: `/windows-time-guard-install-${this.count}.ps1` });
    this.snapshots.set(uri.toString(), text);
    return uri;
  }

  provideTextDocumentContent(uri: vscode.Uri): string {
    return this.snapshots.get(uri.toString()) ?? '';
  }
}

function realUi(runner: Runner, subscriptions: vscode.Disposable[]): GuardUi {
  const provider = new SnapshotProvider();
  subscriptions.push(vscode.workspace.registerTextDocumentContentProvider(GUARD_SCHEME, provider));

  return {
    show: async (text) => {
      const document = await vscode.workspace.openTextDocument(provider.add(text));
      await vscode.window.showTextDocument(document, { preview: true, viewColumn: vscode.ViewColumn.Active });
    },
    confirm: async (prompt) => (await vscode.window.showWarningMessage(prompt.message, { modal: true, detail: prompt.detail }, prompt.confirm)) === prompt.confirm,
    run: (request) => Promise.resolve(vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: 'Waiting for the elevated PowerShell (Windows Time guard)…' }, () => runner(request))),
    query: runner,
    report: (message, failed) => { void (failed ? vscode.window.showErrorMessage(message) : vscode.window.showInformationMessage(message)); },
  };
}

function recordingUi(recorder: GuardRecorder): GuardUi {
  return {
    show: (text) => { recorder.shown.push(text); return Promise.resolve(); },
    confirm: (prompt) => { recorder.prompts.push(prompt); return Promise.resolve(recorder.answer); },
    run: (request) => { recorder.requests.push(request); return Promise.resolve(recorder.result); },
    query: (request) => { recorder.queries.push(request); return Promise.resolve(recorder.queryResult); },
    report: (message, failed) => { recorder.reports.push({ message, failed }); },
  };
}

export function guardUiFor(isTestMode: boolean, recorder: GuardRecorder, realRunner: Runner, subscriptions: vscode.Disposable[]): GuardUi {
  return isTestMode ? recordingUi(recorder) : realUi(realRunner, subscriptions);
}
