import { daemonArgv } from '../client/WslCareClient';
import { VERB_TIMEOUT_MS, VERBS } from '../client/verbs';
import type { ProcessRequest, ProcessResult, Runner } from '../process/runner';
import type { ActionIds, RunId, VolumeName } from './rootIds';

/**
 * THE root boundary (E6.S2, plan §15f #2, §15j M1) — the ONE module of the extension that spells a root argv word: `-u`,
 * `root`, `act`, `collect`, `--preview`, `--confirm`, `--manual`, `--detach`, `--only`, `-` and `--stop`. Every root call
 * the extension can make is one op of the closed `ROOT_OPS` union below, built from typed values only (`rootIds.ts`'s
 * validators are the one road in), and started only through the runner seam it is handed — this module imports no
 * process API (`structure.test.ts`). It is a confused-deputy boundary, not a malware one: any process of this Windows user
 * can already run `wsl -u root`; what must never happen is a webview, a workspace setting or a daemon answer steering WHICH
 * root call is made.
 *
 * <p>Never `--timer` (the timer's mark is the systemd timer's alone), never `--user`, never `config`: no op spells them,
 * and the bundle scan holds this module's region to an exact set of literals (`bundleScan.test.ts`). Prose belongs to
 * `rootFailureText.ts`, not here.</p>
 *
 * <p><b>The host's ceilings (plan §15k #19).</b> Every op is a SHORT call — the long work runs in the daemon's own unit:</p>
 * <ul>
 *   <li>`preview` — `act <ids> --preview --json` runs, for the ids asked, the same per-action previews `preview --all` runs
 *       (Docker's `system df -v`, the volume listing, the container inspection batches), so `preview --all`'s 330 s holds.</li>
 *   <li>`confirm` / `fullCheck` (`collect --detach`, the *Run full check now* button, §15j M9) — a DETACH: the daemon reads the shown list (its own 10 s stdin ceiling), sweeps the request
 *       folder under the run lock (one `systemctl show`, 15 s — one root operation at a time leaves at most a stale request
 *       or two), writes the request, starts the unit with `systemctl start --no-block` (30 s) and, when that start timed
 *       out, asks the unit once more (15 s): 70 s of daemon ceilings + the relay → 90 s. A detach that outruns it is
 *       OUTCOME UNKNOWN, never a failure: the caller follows `status.running` (plan §15k #3). The real time on the owner's
 *       machine is measured at the E6 live gate (`wsl.exe -u root` is not run outside it).</li>
 *   <li>`stop` — `act --stop` runs `systemctl stop <unit>` with its 120 s ceiling (the units carry `TimeoutStopSec=90`) →
 *       150 s.</li>
 *   <li>`rootCheck` — `--version` answers before the machine is read, as the read-only `--version` (20 s).</li>
 * </ul>
 */

export const ROOT_OPS = ['preview', 'confirm', 'stop', 'fullCheck', 'rootCheck'] as const;

export type RootOpName = (typeof ROOT_OPS)[number];

/**
 * The closed set of root calls. A confirm is ALWAYS detached (`--manual --detach`: the long run lives in the daemon's
 * template unit and survives a reload, §15j B2) and A4 is ALWAYS bound to the list its preview showed (`--only -`, the
 * names on stdin — never `--volume` argv: 387 names would near Windows' 32 767-character command line, §15f #7; a full
 * 10 000-name list, 650 000 bytes, relays through `wsl.exe` byte for byte — research/2026-10-03_wsl_exe_facts.md row 20).
 */
export type RootOp =
  | { readonly op: 'preview'; readonly ids: ActionIds }
  | { readonly op: 'confirm'; readonly ids: ActionIds; readonly shown: readonly VolumeName[] | undefined }
  | { readonly op: 'stop'; readonly runId: RunId }
  | { readonly op: 'fullCheck' }
  | { readonly op: 'rootCheck' };

/** The launcher and the validated, listed, running distribution — `WslCareClient.rootTarget()`'s answer. */
export interface RootTarget {
  readonly wsl: string;
  readonly distro: string;
}

/** The detach's ceiling — see the header. */
export const DETACH_TIMEOUT_MS = 90_000;

/** The stop's ceiling — see the header. */
export const STOP_TIMEOUT_MS = 150_000;

export const ROOT_TIMEOUT_MS: { readonly [K in RootOpName]: number } = {
  preview: VERB_TIMEOUT_MS.preview,
  confirm: DETACH_TIMEOUT_MS,
  stop: STOP_TIMEOUT_MS,
  fullCheck: DETACH_TIMEOUT_MS,
  rootCheck: VERB_TIMEOUT_MS.version,
};

/** What a full check's run holds as its actions in `status.running` (`["collect"]`) — what a follow with no run id may adopt. */
export const FULL_CHECK_ACTIONS: readonly string[] = ['collect'];

const AS_ROOT = ['-u', 'root'];

/** Taken out of every root call's environment (review S2): `wsl.exe` would carry the Windows variables it names into root's. */
const NOT_FOR_ROOT = ['WSLENV'];

type Tails = { readonly [K in RootOpName]: (op: Extract<RootOp, { op: K }>) => readonly string[] | undefined };

/** The daemon tail of each op — typed by the op, so an op added to `ROOT_OPS` without its tail does not compile. */
const TAILS: Tails = {
  preview: (op) => ['act', op.ids.join(','), '--preview', '--json'],
  confirm: (op) => confirmTail(op.ids, op.shown),
  stop: (op) => ['act', '--stop', op.runId, '--json'],
  fullCheck: () => ['collect', '--detach', '--json'],
  rootCheck: () => VERBS.version,
};

/** The daemon tail of `op`, or `undefined` when the op cannot be built (a shown list that does not match its ids). */
function tailOf(op: RootOp): readonly string[] | undefined {
  const tail = TAILS[op.op] as (op: RootOp) => readonly string[] | undefined;

  return tail(op);
}

/** A shown list exists exactly when A4 is confirmed: without one A4 would re-select live, and a list belongs to A4 alone. */
function confirmTail(ids: ActionIds, shown: readonly VolumeName[] | undefined): readonly string[] | undefined {
  if (ids.includes('A4') !== (shown !== undefined)) {
    return undefined;
  }
  const only = shown === undefined ? [] : ['--only', '-'];

  return ['act', ids.join(','), '--confirm', '--manual', '--detach', ...only, '--json'];
}

/** A4's names as the daemon reads an `--only` list: one per line, every line ended. */
function stdinOf(op: RootOp): { readonly stdin?: Buffer } {
  return op.op === 'confirm' && op.shown !== undefined ? { stdin: Buffer.from(op.shown.map((name) => `${name}\n`).join('')) } : {};
}

/** The one request `op` makes in `target`, or `undefined` when the op cannot be built. Pure. */
export function rootRequest(target: RootTarget, op: RootOp): ProcessRequest | undefined {
  const tail = tailOf(op);

  return tail === undefined ? undefined : { file: target.wsl, args: daemonArgv(target.distro, tail, AS_ROOT), timeoutMs: ROOT_TIMEOUT_MS[op.op], withoutEnv: NOT_FOR_ROOT, ...stdinOf(op) };
}

/** Start `op` through `runner` — or nothing, when it cannot be built. */
export async function callRoot(runner: Runner, target: RootTarget, op: RootOp): Promise<ProcessResult | undefined> {
  const request = rootRequest(target, op);

  return request === undefined ? undefined : runner(request);
}
