import type { Modal } from '../cleanup/modalText';
import type { NoticeLevel } from '../cleanup/resultText';
import type { ArchiveUi } from './archiveFlow';

/**
 * The Test-mode surfaces of the archive flows (E10.S1; `cleanRecorder.ts`' pattern) — no `vscode`, so the node tests and the
 * extension-host scenarios share them. The folder dialog answers `picked` (dismissed when `undefined`), a modal `answer`
 * (declined unless a test says otherwise); every modal and notification is recorded.
 */

export interface ArchiveRecorder {
  readonly modals: Modal[];
  readonly notices: { readonly level: NoticeLevel; readonly sentence: string }[];
  /** How many times the folder dialog was opened. */
  picks: number;
  picked: string | undefined;
  answer: boolean;
}

export function newArchiveRecorder(): ArchiveRecorder {
  return { modals: [], notices: [], picks: 0, picked: undefined, answer: false };
}

export function recordingArchiveUi(recorder: ArchiveRecorder): ArchiveUi {
  return {
    pickFolder: () => {
      recorder.picks += 1;
      return Promise.resolve(recorder.picked);
    },
    confirm: (modal) => {
      recorder.modals.push(modal);
      return Promise.resolve(recorder.answer);
    },
    notify: (level, sentence) => {
      recorder.notices.push({ level, sentence });
      return Promise.resolve();
    },
  };
}
