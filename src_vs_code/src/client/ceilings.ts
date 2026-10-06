import type { Numbers } from '../settings/numbers';
import { FALLBACK_LIMITS, type DaemonLimits } from '../shared/daemonLimits';
import { MARGIN_S, otherRowShareS, previewWorstCaseS, worstCasesOf, type WorstCases } from './worstCases';

/**
 * Every host ceiling of the extension — each read call, each root op — in ONE place (plan §15q N-1–N-3): the setting the
 * person chose (`settings/numbers.ts`, whose minimums are the derived worst cases plus a margin), and for a cleanup's
 * preview the sum its rows need. `ceilings.test.ts` holds each strictly above its daemon worst case
 * (`client/worstCases.ts`). A call that outruns its ceiling reads "timed out" — and a DETACH that does is "outcome
 * unknown", followed, never a failure (`cleanupController.ts`).
 *
 * Since daemon #17 the drain grace and `systemctl stop`'s ceiling are the daemon's PUBLISHED values (`status.limits`): a
 * ceiling is the setting, raised when needed to stay above the worst case under the limits the daemon answered — a daemon
 * whose drain is 10 s never meets a ceiling sized for 2 s.
 */

export type HostCall =
  | { readonly call: 'status' | 'version' | 'rootCheck' | 'doctor' | 'preview' | 'runRead' | 'detach' | 'stop' }
  | { readonly call: 'rootPreview'; readonly ids: readonly string[] };

type FixedCall = Exclude<HostCall, { call: 'rootPreview' }>['call'];

const SETTING_OF: { readonly [K in FixedCall]: keyof Numbers } = {
  status: 'statusSeconds',
  version: 'versionSeconds',
  rootCheck: 'versionSeconds',
  doctor: 'doctorSeconds',
  preview: 'previewSeconds',
  runRead: 'runReadSeconds',
  detach: 'detachSeconds',
  stop: 'stopSeconds',
};

/** One id's share of a cleanup's preview: a Docker row (or an id outside the rows) its own snapshot's setting; A8 / A9 their cost. */
function shareS(numbers: Numbers, id: string): number {
  return otherRowShareS(id) ?? numbers.previewPerDockerRowSeconds;
}

/**
 * `act <ids> --preview` (N-2): one Docker snapshot per Docker row, A9's snap listing, and the base of a call that runs no
 * command (the `status` ceiling) — so "Clean selected" over six Docker rows waits for six snapshots, not one.
 */
function rootPreviewS(numbers: Numbers, ids: readonly string[], limits: DaemonLimits): number {
  return Math.max(ids.reduce((sum, id) => sum + shareS(numbers, id), numbers.statusSeconds), previewWorstCaseS(ids, limits) + MARGIN_S);
}

/** The worst case of a fixed call under `worst` — the root check is `--version`'s. */
function fixedWorstS(call: FixedCall, worst: WorstCases): number {
  return worst[call === 'rootCheck' ? 'version' : call];
}

/** A fixed call: its setting, raised to stay above its worst case under the published limits. */
function fixedS(numbers: Numbers, call: FixedCall, limits: DaemonLimits): number {
  return Math.max(numbers[SETTING_OF[call]], fixedWorstS(call, worstCasesOf(limits)) + MARGIN_S);
}

/** Which host call a root op is — `root/rootCall.ts` asks here, so its region keeps only its argv literals (`bundleScan.test.ts`). */
const ROOT_OP_CALLS: Readonly<Record<string, FixedCall>> = { confirm: 'detach', fullCheck: 'detach', stop: 'stop', rootCheck: 'rootCheck' };

export function rootOpCall(op: { readonly op: string; readonly ids?: readonly string[] }): HostCall {
  const fixed = Object.hasOwn(ROOT_OP_CALLS, op.op) ? ROOT_OP_CALLS[op.op] : undefined;

  return fixed === undefined ? { call: 'rootPreview', ids: op.ids ?? [] } : { call: fixed };
}

export function ceilingMs(numbers: Numbers, call: HostCall, limits: DaemonLimits = FALLBACK_LIMITS): number {
  return 1000 * (call.call === 'rootPreview' ? rootPreviewS(numbers, call.ids, limits) : fixedS(numbers, call.call, limits));
}
