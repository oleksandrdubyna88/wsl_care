import { decodeWslText, stripAnsi, textLines } from '../wsl/wslText';
import type { ProcessResult } from '../process/runner';
import { DAEMON_EXIT, WSL_EXE_FAILED } from './exitCodes';
import type { Failure } from './outcome';

/**
 * How a non-zero ending of `wsl.exe -d … --exec /opt/wsl-care/bin/wsl-care …` is read (plan §15g M6). Measured
 * 2026-10-03 (research/2026-10-03_wsl_exe_facts.md):
 *
 * - a missing binary exits **1** — the daemon's own `RunFailed` — with ONE UTF-8 line on stderr from the WSL relay,
 *   `<3>WSL (<pid> - Relay) ERROR: CreateProcessCommon:818: execvpe(<path>) failed: No such file or directory`. So
 *   "not installed" is read ONLY from that signature, for exactly our path; any other 1 is an unknown failure.
 * - `wsl.exe` refusing on its own account exits -1 with its UTF-16LE sentence on STDOUT.
 * - the daemon's stderr carries its console log lines (coloured) before its message; only lines starting `wsl-care:`
 *   are its message (E1.S3 found that a refusal is one `wsl-care:` message, not one stderr line).
 */

/** The dynamic loader's refusal of a binary built against a newer glibc than the distribution has (the Ubuntu ≥ 24.04 /
 * glibc 2.39 floor, plan §15f #5). Its documented shape — NOT measured here: no distribution on this machine is old
 * enough. */
export const GLIBC_MISSING = /version [`'"]?GLIBC_[0-9.]+['"]? not found/;

function escapeRegExp(text: string): string {
  return text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

/** The measured `execvpe` refusal of a missing `daemonPath`, as a whole line. */
export function notInstalledSignature(daemonPath: string): RegExp {
  return new RegExp(`^<3>WSL \\(\\d+ - Relay\\) ERROR: CreateProcessCommon:\\d+: execvpe\\(${escapeRegExp(daemonPath)}\\) failed: No such file or directory$`);
}

/** The daemon's own message lines: colour stripped, only the lines that START with `wsl-care:`. */
export function daemonMessages(stderr: string): string[] {
  return textLines(stripAnsi(stderr)).filter((line) => line.startsWith('wsl-care:'));
}

/** `wsl.exe` refusing on its own account: its sentence, which it writes to STDOUT (UTF-16LE), plus anything on stderr. */
export function wslRefusal(stdout: Buffer, stderr: Buffer): Failure {
  return { kind: 'wslFailed', message: [...textLines(decodeWslText(stdout)), ...textLines(decodeWslText(stderr))].join(' ') };
}

/** The readings that depend on the stderr text alone. */
function fromSignature(code: number, lines: readonly string[], distro: string, daemonPath: string): Failure | undefined {
  const glibc = lines.find((line) => GLIBC_MISSING.test(line));
  if (glibc !== undefined) {
    return { kind: 'unsupportedDistro', distro, detail: glibc };
  }
  const missing = notInstalledSignature(daemonPath);

  return code === DAEMON_EXIT.runFailed && lines.some((line) => missing.test(line)) ? { kind: 'notInstalled', distro } : undefined;
}

function byCode(code: number, messages: readonly string[]): Failure {
  switch (code) {
    case DAEMON_EXIT.usage:
      return { kind: 'refused', messages };
    case DAEMON_EXIT.internal:
      return { kind: 'internalDefect', messages };
    case DAEMON_EXIT.interrupted:
      return { kind: 'interrupted' };
    default:
      return { kind: 'unknownFailure', code, messages };
  }
}

/** What a non-zero exit of a daemon call means. */
export function classifyExit(code: number, stdout: Buffer, stderr: Buffer, distro: string, daemonPath: string): Failure {
  if (code === WSL_EXE_FAILED) {
    return wslRefusal(stdout, stderr);
  }
  const text = stripAnsi(decodeWslText(stderr));

  return fromSignature(code, textLines(text), distro, daemonPath) ?? byCode(code, daemonMessages(text));
}

type NotExited = Exclude<ProcessResult, { kind: 'exited' }>;

/** How a timeout of the launcher reads: a WSL question that did not answer is WSL failing; a daemon call is its own timeout. */
export type TimeoutReading = 'wslQuestion' | 'daemonCall';

/**
 * THE reading of a launcher ending that is not an exit (E6.S2 review L1: one reader, where the client and the root paths
 * each had their own and read a signal differently). A timeout reads by `timeout`; everything else the same everywhere.
 */
export function launchFailure(result: NotExited, timeout: TimeoutReading): Failure {
  const read = ENDINGS[result.kind] as (r: NotExited, t: TimeoutReading) => Failure;

  return read(result, timeout);
}

type Endings = { readonly [K in NotExited['kind']]: (result: Extract<NotExited, { kind: K }>, timeout: TimeoutReading) => Failure };

const ENDINGS: Endings = {
  failedToStart: (r) => ({ kind: 'wslFailed', message: `wsl.exe could not be started: ${r.reason}` }),
  tooMuchOutput: (r) => ({ kind: 'unparseable', detail: `the answer exceeded ${r.limitBytes} bytes on ${r.stream}` }),
  timedOut: (r, timeout) => (timeout === 'daemonCall' ? { kind: 'timedOut', timeoutMs: r.timeoutMs } : { kind: 'wslFailed', message: `wsl.exe did not answer within ${r.timeoutMs} ms` }),
  signalled: (r) => ({ kind: 'unknownFailure', code: undefined, messages: [`ended by ${r.signal}`] }),
};
