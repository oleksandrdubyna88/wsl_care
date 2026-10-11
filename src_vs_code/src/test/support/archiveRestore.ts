import type { ArchiveRestore } from '../../archivePage/archiveController';
import type { RestoreFacts } from '../../archivePage/archiveViewModel';

/** The cleanup host's restore facts when nothing runs and nothing blocks — what a browse-only test hands the Archive page. */
export const IDLE_RESTORE: RestoreFacts = { inFlight: false, state: '', blocker: '' };

/** A restore seam that records what it was handed and answers the facts a test sets (E10.S2b). */
export interface RecordingRestore extends ArchiveRestore {
  readonly started: (readonly string[])[];
  facts: () => RestoreFacts;
}

export function recordingRestore(facts: RestoreFacts = IDLE_RESTORE): RecordingRestore {
  const started: (readonly string[])[] = [];
  const seam: RecordingRestore = { started, facts: () => facts, start: (ids) => { started.push([...ids]); return Promise.resolve(); } };

  return seam;
}
