import type { ProcessResult } from '../process/runner';
import { signed32 } from '../process/runner';
import { classifyDirectExit } from './failures';
import { ownVersion, parseAnswer, versionRefusal } from './handshake';
import type { Answer, DaemonVersion, Failure, VerbOutcome } from './outcome';
import { endingReason } from './windowsTasks';

/**
 * The Windows `status` reading (E19.S2b, todo/PLAN_disk_hygiene_windows_E19.md decision 12): what the BUNDLED exe's `status --json`
 * ended in, read into the SAME `VerbOutcome` the distro's `status` is — `checkedBody` / `statusAnswer` through `parseAnswer`, the
 * same failure kinds, the same version judgement — so the store, the bar and the panel take the two readings through one shape.
 * Pure: `WindowsCareClient.status` starts the exe, this reads what came back (and why nothing did).
 */

/**
 * What an answered Windows outcome names where the distro's names a distribution: the side the reading is about. It is no
 * distribution — nothing may pass it to `wsl.exe -d`.
 */
export const WINDOWS_SIDE = 'Windows';

/** Why no status was read at all: the host is not Windows, or the extension is not on a local disk — nothing was started. */
export function windowsStatusUnavailable(platform: string, reason: string): VerbOutcome {
  return platform === 'win32' ? { kind: 'unknownFailure', code: undefined, messages: [reason], verb: 'status' } : { kind: 'notWindows', platform, verb: 'status' };
}

/**
 * A process that did not exit: the ceiling is the client's own `timedOut`; every other ending (a refused start, a signal, too much
 * output) is told in the sentence `windowsTasks.ts` gives it for every read of this exe — one wording, never a copy.
 */
function failureOf(result: Exclude<ProcessResult, { kind: 'exited' }>): Failure {
  return result.kind === 'timedOut' ? { kind: 'timedOut', timeoutMs: result.timeoutMs } : { kind: 'unknownFailure', code: undefined, messages: [endingReason(result)] };
}

/** The version the answer states, or why the reading carries none — the distro's rule (`ownVersion`). */
function versionOf(answer: Answer): DaemonVersion {
  return ownVersion(answer) ?? { kind: 'notRead', reason: 'the Windows status carries no productVersion' };
}

function answeredOutcome(answer: Answer): VerbOutcome {
  const daemonVersion = versionOf(answer);
  const refusal = versionRefusal(daemonVersion);

  return refusal === undefined ? { kind: 'answered', verb: 'status', distro: WINDOWS_SIDE, answer, daemonVersion } : { ...refusal, verb: 'status' };
}

function exitedOutcome(result: Extract<ProcessResult, { kind: 'exited' }>): VerbOutcome {
  const code = signed32(result.code);
  if (code !== 0) {
    return { ...classifyDirectExit(code, result.stderr), verb: 'status' };
  }
  const parsed = parseAnswer('status', result.stdout.toString('utf8'));

  return 'kind' in parsed ? { ...parsed, verb: 'status' } : answeredOutcome(parsed);
}

/** The runner's result of `status --json` on the bundled exe as one outcome — every ending is an outcome, none a throw. */
export function windowsStatusOutcomeOf(result: ProcessResult): VerbOutcome {
  return result.kind === 'exited' ? exitedOutcome(result) : { ...failureOf(result), verb: 'status' };
}
