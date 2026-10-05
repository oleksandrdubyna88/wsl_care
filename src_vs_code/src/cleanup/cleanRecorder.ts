import type { CleanUi } from './cleanFlow';
import type { Modal } from './modalText';
import type { NoticeLevel } from './resultText';

/**
 * The Test-mode surfaces of the cleanup flow (E6.S3) — kept apart from `cleanUi.ts` because they need no `vscode`: the
 * extension-host scenarios reach them through the test API, the node scenarios use them directly. Every modal and
 * notification is recorded; a modal is answered from `answer` (declined unless a scenario says otherwise), a
 * notification's button from `noticeAnswer`.
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

export function recordingCleanUi(recorder: CleanRecorder): CleanUi {
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
