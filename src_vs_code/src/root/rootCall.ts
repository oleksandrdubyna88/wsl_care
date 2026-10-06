import { daemonArgv } from '../client/WslCareClient';
import { ceilingMs, rootOpCall } from '../client/ceilings';
import { VERBS } from '../client/verbs';
import type { ProcessRequest, ProcessResult, Runner } from '../process/runner';
import { DEFAULT_NUMBERS, type Numbers } from '../settings/numbers';
import { FALLBACK_LIMITS, type DaemonLimits } from '../shared/daemonLimits';
import { shownCap, type ActionIds, type RunId, type VolumeName } from './rootIds';

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
 * <p><b>The host's ceilings (plan §15k #19, §15q N-2 / N-3)</b> come from `client/ceilings.ts` — the number settings, each
 * held strictly above the daemon's worst case for its call (`client/worstCases.ts`, `ceilings.test.ts`). Every op is a
 * SHORT call — the long work runs in the daemon's own unit:</p>
 * <ul>
 *   <li>`preview` — `act <ids> --preview --json` runs each action's own preview, and EACH Docker row takes its own Docker
 *       snapshot (`DockerLook.TakeAsync`): the ceiling is the per-Docker-row setting × the Docker rows asked, plus A9's
 *       snap listing and a base — not `preview --all`'s one snapshot (N-2).</li>
 *   <li>`confirm` / `fullCheck` (`collect --detach`, the *Run full check now* button, §15j M9) — a DETACH: the daemon reads
 *       the shown list (10 s), sweeps the request folder under the run lock — ONE `systemctl show` per queued request, up
 *       to 32 (N-3: two already outran the old 90 s) — writes the request, starts the unit (30 s) and, when that start
 *       timed out, asks the unit once more (15 s). A detach that outruns its ceiling is OUTCOME UNKNOWN, never a failure:
 *       the caller follows `status.running` (plan §15k #3).</li>
 *   <li>`stop` — `act --stop` runs `systemctl stop <unit>` with its 120 s ceiling (the units carry `TimeoutStopSec=90`).</li>
 *   <li>`rootCheck` — `--version` answers before the machine is read, as the read-only `--version`.</li>
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

/** An op's ceiling under `numbers` — see the header. */
export function rootTimeoutMs(op: RootOp, numbers: Numbers = DEFAULT_NUMBERS, limits: DaemonLimits = FALLBACK_LIMITS): number {
  return ceilingMs(numbers, rootOpCall(op), limits);
}

/** The detach's DEFAULT ceiling (`wslCare.timeouts.detachSeconds`). */
export const DETACH_TIMEOUT_MS = DEFAULT_NUMBERS.detachSeconds * 1000;

/** The stop's DEFAULT ceiling (`wslCare.timeouts.stopSeconds`). */
export const STOP_TIMEOUT_MS = DEFAULT_NUMBERS.stopSeconds * 1000;

/** What a full check's run holds as its actions in `status.running` (`["collect"]`) — what a follow with no run id may adopt. */
export const FULL_CHECK_ACTIONS: readonly string[] = ['collect'];

/**
 * What a history line says the run WAS (plan §15o, the additive `kind`): a full check or an `act`. Spelt here because the
 * two values are the root verbs' own words, which only this module spells (`structure.test.ts`).
 */
export const RUN_KINDS = { fullCheck: 'collect', act: 'act' } as const;

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

/** The daemon tail of `op`, or `undefined` when the op cannot be built (a shown list that does not match its ids, or longer than the cap in force). */
function tailOf(op: RootOp, limits: DaemonLimits): readonly string[] | undefined {
  const tail = TAILS[op.op] as (op: RootOp) => readonly string[] | undefined;

  return fitsCap(op, limits) ? tail(op) : undefined;
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

/** A confirm's shown list fits the cap in force (daemon #17): the stdin never carries more names than the daemon takes. */
function fitsCap(op: RootOp, limits: DaemonLimits): boolean {
  return op.op !== 'confirm' || op.shown === undefined || op.shown.length <= shownCap(limits.maxShownNames);
}

/** The one request `op` makes in `target`, or `undefined` when the op cannot be built. Pure. */
export function rootRequest(target: RootTarget, op: RootOp, numbers: Numbers = DEFAULT_NUMBERS, limits: DaemonLimits = FALLBACK_LIMITS): ProcessRequest | undefined {
  const tail = tailOf(op, limits);

  return tail === undefined ? undefined : { file: target.wsl, args: daemonArgv(target.distro, tail, AS_ROOT), timeoutMs: rootTimeoutMs(op, numbers, limits), withoutEnv: NOT_FOR_ROOT, ...stdinOf(op) };
}

/** Start `op` through `runner` — or nothing, when it cannot be built. */
export async function callRoot(runner: Runner, target: RootTarget, op: RootOp, numbers: Numbers = DEFAULT_NUMBERS, limits: DaemonLimits = FALLBACK_LIMITS): Promise<ProcessResult | undefined> {
  const request = rootRequest(target, op, numbers, limits);

  return request === undefined ? undefined : runner(request);
}
