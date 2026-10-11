import type { ProcessRequest, ProcessResult, Runner } from '../process/runner';
import type { FixPrompt } from '../windowsTime/windowsTimeFix';

/** *Enable <GPU>*'s surfaces and its Test-mode recorder (`gpuUi.ts` chooses): everything recorded, every answer scripted,
 * nothing started — and no `vscode` import, so the unit tests load it. */

export interface GpuUi {
  readonly confirm: (prompt: FixPrompt) => Promise<boolean>;
  /** The ELEVATED enable. */
  readonly run: Runner;
  /** The unelevated adapter query. */
  readonly query: Runner;
  readonly report: (message: string, failed: boolean) => void;
}

export interface GpuRecorder {
  readonly prompts: FixPrompt[];
  readonly requests: ProcessRequest[];
  readonly queries: ProcessRequest[];
  readonly reports: { readonly message: string; readonly failed: boolean }[];
  /** How the recorded modal is answered. */
  answer: boolean;
  /** What a recorded elevated run answers. */
  result: ProcessResult;
  /** What a recorded query answers. */
  queryResult: ProcessResult;
}

const NOTHING = Buffer.alloc(0);

export function newGpuRecorder(): GpuRecorder {
  return {
    prompts: [], requests: [], queries: [], reports: [], answer: false,
    result: { kind: 'exited', code: 0, stdout: NOTHING, stderr: NOTHING },
    queryResult: { kind: 'exited', code: 0, stdout: Buffer.from('adapters=0\r\n'), stderr: NOTHING },
  };
}
