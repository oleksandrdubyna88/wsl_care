import * as vscode from 'vscode';

import type { CleanUi } from './cleanFlow';
import type { Modal } from './modalText';
import type { NoticeLevel } from './resultText';

/**
 * The surfaces of the cleanup flow — chosen the way the runner and *Install daemon*'s are (`install/installUi.ts`): by
 * whether the extension host runs the extension in Test mode, and by nothing else.
 *
 * - **not Test mode** — `showWarningMessage(message, { modal: true, detail }, confirm)` for a confirmation, and an
 *   information / warning / error notification (with its buttons) for everything else;
 * - **Test mode** — a RECORDER: every modal and notification recorded, the modal answered from `answer` (declined unless
 *   a scenario says otherwise), a notification's button from `noticeAnswer`.
 */

export interface CleanRecorder {
  readonly modals: Modal[];
  readonly notices: { readonly level: NoticeLevel; readonly sentence: string; readonly actions: readonly string[] }[];
  /** How a recorded modal is answered. */
  answer: boolean;
  /** Which button a recorded notification answers (none by default). */
  noticeAnswer: string | undefined;
}

export function newCleanRecorder(): CleanRecorder {
  return { modals: [], notices: [], answer: false, noticeAnswer: undefined };
}

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

function recordingUi(recorder: CleanRecorder): CleanUi {
  return {
    confirm: (modal) => {
      recorder.modals.push(modal);
      return Promise.resolve(recorder.answer);
    },
    notify: (level, sentence, actions = []) => {
      recorder.notices.push({ level, sentence, actions });
      return Promise.resolve(actions.length > 0 ? recorder.noticeAnswer : undefined);
    },
  };
}

export function cleanUiFor(isTestMode: boolean, recorder: CleanRecorder): CleanUi {
  return isTestMode ? recordingUi(recorder) : realUi();
}
