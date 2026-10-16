import type { ProcessRequest, ProcessResult } from '../process/runner';
import type { FixPrompt } from './windowsTimeFix';

/** The Windows Time guard's Test-mode surfaces (`guardUi.ts`): everything recorded, every answer scripted, nothing started. */

export interface GuardRecorder {
  readonly shown: string[];
  readonly prompts: FixPrompt[];
  readonly requests: ProcessRequest[];
  readonly queries: ProcessRequest[];
  readonly reports: { readonly message: string; readonly failed: boolean }[];
  /** How the recorded modal is answered. */
  answer: boolean;
  /** What a recorded elevated run answers. */
  result: ProcessResult;
  /** What a recorded status query answers. */
  queryResult: ProcessResult;
}

const NOTHING = Buffer.alloc(0);

export function newGuardRecorder(): GuardRecorder {
  return {
    shown: [], prompts: [], requests: [], queries: [], reports: [], answer: false,
    result: { kind: 'exited', code: 0, stdout: NOTHING, stderr: NOTHING },
    // The ONE Task Scheduler read (WR-E2): a machine where the `\wsl-care` folder does not exist — nothing installed.
    queryResult: { kind: 'exited', code: 0, stdout: Buffer.from('channel=enabled\r\nstamp=absent\r\nuser=S-1-5-21-1000000000-2000000000-3000000000-1001\r\nfolder=absent\r\n'), stderr: NOTHING },
  };
}
