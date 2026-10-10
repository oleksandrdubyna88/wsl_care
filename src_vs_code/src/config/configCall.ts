import type { JudgedFolder } from '../archive/judgedFolder';
import { ceilingMs } from '../client/ceilings';
import { DAEMON_EXIT } from '../client/exitCodes';
import { classifyExit, launchFailure } from '../client/failures';
import type { Failure } from '../client/outcome';
import { DAEMON_PATH, daemonArgv } from '../client/WslCareClient';
import type { ProcessRequest, ProcessResult, Runner } from '../process/runner';
import { DEFAULT_NUMBERS, type Numbers } from '../settings/numbers';
import { FALLBACK_LIMITS, type DaemonLimits } from '../shared/daemonLimits';

/**
 * THE user-layer writer (E10.S1, plan §15s): the ONE module of the extension that spells the daemon's settings verb. It writes
 * exactly one key, `archive.baseFolder`, in exactly two ways — a folder the daemon's `archive check-base` accepted
 * (`JudgedFolder`: nothing else type-checks) or the empty value that stops the archive — and nothing else: no other key, no
 * `config reset`, never `-u` (the daemon refuses the key as root, 81, and the user layer is the person's own). Its argv is built
 * by the client's `daemonArgv`, as `rootCall.ts`'s is, and started only through the runner seam it is handed — this module
 * imports no process API (`structure.test.ts`), and the bundle scan holds its region to an exact set of literals
 * (`bundleScan.test.ts`).
 *
 * <p>`config set` answers in TEXT (the key's effective value, one line), not JSON: its exit is read on its own. 0 is written; 2
 * (refused: the daemon's own judgment of the folder failed, or the value broke the key's rule), 81 (the distribution's default
 * user is root) and 70 (a defect) are the client's failures with the daemon's own `wsl-care:` lines. `config set` never answers
 * 78: an observe-only configuration still writes the user layer.</p>
 *
 * <p>Its ceiling is the run-read ceiling (`wslCare.timeouts.runReadSeconds`): the daemon judges the folder as `check-base` does
 * (one mount read, no child) and writes one file.</p>
 */

export type ConfigOp = { readonly op: 'setBaseFolder'; readonly folder: JudgedFolder } | { readonly op: 'clearBaseFolder' };

/** Where a config call goes: the launcher and a validated, listed, running distribution (`WslCareClient.rootTarget()`'s answer). */
export interface ConfigTarget {
  readonly wsl: string;
  readonly distro: string;
}

/** What one config call ends in: written (with the daemon's one line), or the client's reading of why not. */
export type ConfigOutcome = { readonly kind: 'written'; readonly line: string } | Failure;

const BASE_FOLDER_KEY = 'archive.baseFolder';

type Values = { readonly [K in ConfigOp['op']]: (op: Extract<ConfigOp, { op: K }>) => string };

/** The value each op writes — typed by the op, so an op added to `ConfigOp` without its value does not compile. */
const VALUES: Values = {
  setBaseFolder: (op) => op.folder,
  clearBaseFolder: () => '',
};

/** The daemon tail of `op`. Pure. */
export function configTail(op: ConfigOp): readonly string[] {
  const value = VALUES[op.op] as (op: ConfigOp) => string;

  return ['config', 'set', BASE_FOLDER_KEY, value(op)];
}

/** The one request `op` makes in `target`. Pure. */
export function configRequest(target: ConfigTarget, op: ConfigOp, numbers: Numbers = DEFAULT_NUMBERS, limits: DaemonLimits = FALLBACK_LIMITS): ProcessRequest {
  return { file: target.wsl, args: daemonArgv(target.distro, configTail(op)), timeoutMs: ceilingMs(numbers, { call: 'runRead' }, limits) };
}

/** Start `op` through `runner`, and read how it ended. */
export async function callConfig(runner: Runner, target: ConfigTarget, op: ConfigOp, numbers: Numbers = DEFAULT_NUMBERS, limits: DaemonLimits = FALLBACK_LIMITS): Promise<ConfigOutcome> {
  return configOutcomeOf(await runner(configRequest(target, op, numbers, limits)), target.distro);
}

/** A config call's ending. Pure. */
export function configOutcomeOf(result: ProcessResult, distro: string): ConfigOutcome {
  if (result.kind !== 'exited') {
    return launchFailure(result, 'daemonCall');
  }

  return result.code === DAEMON_EXIT.ok ? { kind: 'written', line: result.stdout.toString('utf8').trim() } : classifyExit(result.code, result.stdout, result.stderr, distro, DAEMON_PATH);
}
