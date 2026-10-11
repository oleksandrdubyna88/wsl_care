import type { ProcessRequest, ProcessResult, Runner } from '../process/runner';

/**
 * The engine read in Test mode (PLAN_pwsh_engine_windows_time.md §11 R3): every query is RECORDED and answered from here — no
 * PowerShell is ever started by the extension-host tests, as the guard's and Windows Time's recorders do. The default answer
 * is a machine with no PowerShell 7 (Windows PowerShell 5.1 runs), so a scenario sees the launcher's own program unless it
 * sets another answer.
 */
export interface EngineRecorder {
  readonly queries: ProcessRequest[];
  /** What a recorded read answers. */
  result: ProcessResult;
}

/**
 * The probe's two verdicts on a machine without PowerShell 7. No path rides along: the shipped bundle holds no drive path
 * (`vsixCheck`), and an `absent` verdict needs none.
 */
export const NO_PWSH_ANSWER = ['pwsh.store=absent', 'pwsh.path=', 'pwsh.msi=absent', 'pwsh.msiPath=', ''].join('\r\n');

export function newEngineRecorder(): EngineRecorder {
  return { queries: [], result: { kind: 'exited', code: 0, stdout: Buffer.from(NO_PWSH_ANSWER), stderr: Buffer.alloc(0) } };
}

/** The engine read's runner: the recorder in Test mode, else the real runner (never the fake wsl.exe). */
export function engineQueryFor(isTestMode: boolean, recorder: EngineRecorder, realRunner: Runner): Runner {
  return isTestMode ? (request) => { recorder.queries.push(request); return Promise.resolve(recorder.result); } : realRunner;
}
