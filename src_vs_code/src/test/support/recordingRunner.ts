import type { ProcessRequest, ProcessResult, Runner } from '../../process/runner';

/**
 * A runner that starts NOTHING: it records every request and answers from a script keyed by the exact argv. An
 * unscripted argv answers `failedToStart` naming it, so a test that forgot a step fails on the step, not later.
 */
export interface RecordingRunner {
  readonly runner: Runner;
  readonly requests: readonly ProcessRequest[];
  /** Every request's argv, joined with spaces — what most assertions read. */
  argvs(): string[];
}

export type Scripted = ProcessResult | ((request: ProcessRequest) => ProcessResult | Promise<ProcessResult>);

export function recordingRunner(script: Readonly<Record<string, Scripted>>): RecordingRunner {
  const requests: ProcessRequest[] = [];
  const runner: Runner = async (request) => {
    requests.push(request);
    const answer = script[request.args.join(' ')];
    if (answer === undefined) {
      return { kind: 'failedToStart', reason: `recordingRunner: unscripted argv: ${request.args.join(' ')}` };
    }

    return typeof answer === 'function' ? answer(request) : answer;
  };

  return { runner, requests, argvs: () => requests.map((r) => r.args.join(' ')) };
}

/** An exit with UTF-8 stdout / stderr text. */
export function exited(code: number, stdout = '', stderr = ''): ProcessResult {
  return { kind: 'exited', code, stdout: Buffer.from(stdout, 'utf8'), stderr: Buffer.from(stderr, 'utf8') };
}

/** An exit whose stdout is UTF-16LE, as `wsl.exe`'s own output is (measured 2026-10-03). */
export function exitedUtf16(code: number, stdout: string): ProcessResult {
  return { kind: 'exited', code, stdout: Buffer.from(stdout, 'utf16le'), stderr: Buffer.alloc(0) };
}

/** The measured `wsl.exe -l -v` table (2026-10-03), with the `*` on `defaultName`. */
export function verboseTable(rows: readonly { name: string; running: boolean }[], defaultName: string | undefined): string {
  const head = `  ${'NAME'.padEnd(18)}${'STATE'.padEnd(16)}VERSION\r\n`;
  const body = rows.map((r) => `${r.name === defaultName ? '*' : ' '} ${r.name.padEnd(18)}${(r.running ? 'Running' : 'Stopped').padEnd(16)}2\r\n`);

  return head + body.join('');
}

/** The `wsl.exe` argv the client sends for its three WSL questions. */
export const LIST_QUIET = '--list --quiet';
export const LIST_VERBOSE = '-l -v';
export const LIST_RUNNING = '--list --running --quiet';

/** The argv of a daemon call in `distro`. */
export function daemonArgv(distro: string, tail: readonly string[]): string {
  return ['-d', distro, '--cd', '/', '--exec', '/opt/wsl-care/bin/wsl-care', ...tail].join(' ');
}
