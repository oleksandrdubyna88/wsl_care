import * as vscode from 'vscode';

import type { NoticeLevel } from '../cleanup/resultText';
import type { ArchiveUi } from './archiveFlow';
import { recordingArchiveUi, type ArchiveRecorder } from './archiveRecorder';

/**
 * The surfaces of the archive flows (E10.S1), chosen as the cleanup's are (`cleanup/cleanUi.ts`): by Test mode and nothing else.
 *
 * - **not Test mode** — `showOpenDialog({ canSelectFolders: true, canSelectMany: false })` for the folder, a modal
 *   `showWarningMessage(message, { modal: true, detail }, confirm)` for a confirmation, a notification for everything else;
 * - **Test mode** — the RECORDER of `archiveRecorder.ts`.
 */

const SHOW: { readonly [L in NoticeLevel]: (message: string) => Thenable<unknown> } = {
  info: (message) => vscode.window.showInformationMessage(message),
  warn: (message) => vscode.window.showWarningMessage(message),
  error: (message) => vscode.window.showErrorMessage(message),
};

function realUi(): ArchiveUi {
  return {
    pickFolder: async () => {
      const picked = await vscode.window.showOpenDialog({ canSelectFolders: true, canSelectFiles: false, canSelectMany: false, openLabel: 'Check this folder', title: 'The folder the AI-session archive is written to' });
      return picked?.[0]?.fsPath;
    },
    confirm: async (modal) => (await vscode.window.showWarningMessage(modal.message, { modal: true, detail: modal.detail }, modal.confirm)) === modal.confirm,
    // A notification resolves only when it is dismissed: it is shown, never waited for.
    notify: (level, sentence) => { void SHOW[level](sentence); return Promise.resolve(); },
  };
}

export function archiveUiFor(isTestMode: boolean, recorder: ArchiveRecorder): ArchiveUi {
  return isTestMode ? recordingArchiveUi(recorder) : realUi();
}
