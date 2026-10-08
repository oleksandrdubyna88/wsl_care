import * as vscode from 'vscode';

import type { ProcessRequest, ProcessResult, Runner } from '../process/runner';
import type { FixDeps, FixPrompt } from './windowsTimeFix';

/**
 * The surfaces *Start Windows Time* uses — the modal, the runner and the notification — chosen the way *Install daemon*'s
 * are (`install/installUi.ts`): by whether the extension host runs the extension in Test mode, and by nothing else.
 *
 * - **not Test mode** — `showWarningMessage({ modal: true })`, the real runner under a progress notification
 *   (*Starting the Windows Time service…*, the in-flight state a person sees while the UAC prompt and the fix run), and a
 *   notification with the outcome.
 * - **Test mode** — a RECORDER: the prompt and every request are recorded and answered from it; no PowerShell is ever
 *   started (it would ask for elevation on the machine running the tests), and the tripwire refuses one that tried.
 */

export type WindowsTimeUi = Pick<FixDeps, 'confirm' | 'run' | 'report'>;

export interface WindowsTimeRecorder {
  readonly prompts: FixPrompt[];
  readonly requests: ProcessRequest[];
  readonly reports: { readonly message: string; readonly failed: boolean }[];
  /** How the recorded modal is answered. */
  answer: boolean;
  /** What a recorded run answers. */
  result: ProcessResult;
}

export function newWindowsTimeRecorder(): WindowsTimeRecorder {
  return { prompts: [], requests: [], reports: [], answer: false, result: { kind: 'exited', code: 0, stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) } };
}

function realUi(runner: Runner): WindowsTimeUi {
  return {
    confirm: async (prompt) => (await vscode.window.showWarningMessage(prompt.message, { modal: true, detail: prompt.detail }, prompt.confirm)) === prompt.confirm,
    run: (request) => Promise.resolve(vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: 'Starting the Windows Time service…' }, () => runner(request))),
    report: (message, failed) => { void (failed ? vscode.window.showErrorMessage(message) : vscode.window.showInformationMessage(message)); },
  };
}

function recordingUi(recorder: WindowsTimeRecorder): WindowsTimeUi {
  return {
    confirm: (prompt) => {
      recorder.prompts.push(prompt);
      return Promise.resolve(recorder.answer);
    },
    run: (request) => {
      recorder.requests.push(request);
      return Promise.resolve(recorder.result);
    },
    report: (message, failed) => { recorder.reports.push({ message, failed }); },
  };
}

export function windowsTimeUiFor(isTestMode: boolean, recorder: WindowsTimeRecorder, realRunner: Runner): WindowsTimeUi {
  return isTestMode ? recordingUi(recorder) : realUi(realRunner);
}
