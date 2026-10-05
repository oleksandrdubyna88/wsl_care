import * as vscode from 'vscode';

import type { CleanUi } from './cleanFlow';
import { recordingCleanUi, type CleanRecorder } from './cleanRecorder';
import type { NoticeLevel } from './resultText';

/**
 * The surfaces of the cleanup flow — chosen the way the runner and *Install daemon*'s are (`install/installUi.ts`): by
 * whether the extension host runs the extension in Test mode, and by nothing else.
 *
 * - **not Test mode** — `showWarningMessage(message, { modal: true, detail }, confirm)` for a confirmation, and an
 *   information / warning / error notification (with its buttons) for everything else;
 * - **Test mode** — the RECORDER of `cleanRecorder.ts`.
 */

const SHOW: { readonly [L in NoticeLevel]: (message: string, ...items: string[]) => Thenable<string | undefined> } = {
  info: (message, ...items) => vscode.window.showInformationMessage(message, ...items),
  warn: (message, ...items) => vscode.window.showWarningMessage(message, ...items),
  error: (message, ...items) => vscode.window.showErrorMessage(message, ...items),
};

function realUi(): CleanUi {
  return {
    confirm: async (modal) => (await vscode.window.showWarningMessage(modal.message, { modal: true, detail: modal.detail }, modal.confirm)) === modal.confirm,
    notify: async (level, sentence, actions = []) => SHOW[level](sentence, ...actions),
  };
}

export function cleanUiFor(isTestMode: boolean, recorder: CleanRecorder): CleanUi {
  return isTestMode ? recordingCleanUi(recorder) : realUi();
}
