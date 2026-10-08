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
    queryResult: { kind: 'exited', code: 0, stdout: Buffer.from('guard=absent\r\nchannel=enabled\r\n'), stderr: NOTHING },
  };
}
