import type { Numbers } from '../settings/numbers';
import { DOCKER_ROW_IDS, OTHER_ROW_PREVIEW_S } from './worstCases';

/**
 * Every host ceiling of the extension — each read call, each root op — in ONE place (plan §15q N-1–N-3): the setting the
 * person chose (`settings/numbers.ts`, whose minimums are the derived worst cases plus a margin), and for a cleanup's
 * preview the sum its rows need. `ceilings.test.ts` holds each strictly above its daemon worst case
 * (`client/worstCases.ts`). A call that outruns its ceiling reads "timed out" — and a DETACH that does is "outcome
 * unknown", followed, never a failure (`cleanupController.ts`).
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
  const other = Object.hasOwn(OTHER_ROW_PREVIEW_S, id) && !DOCKER_ROW_IDS.has(id) ? OTHER_ROW_PREVIEW_S[id] : undefined;

  return other ?? numbers.previewPerDockerRowSeconds;
}

/**
 * `act <ids> --preview` (N-2): one Docker snapshot per Docker row, A9's snap listing, and the base of a call that runs no
 * command (the `status` ceiling) — so "Clean selected" over six Docker rows waits for six snapshots, not one.
 */
function rootPreviewS(numbers: Numbers, ids: readonly string[]): number {
  return ids.reduce((sum, id) => sum + shareS(numbers, id), numbers.statusSeconds);
}

/** Which host call a root op is — `root/rootCall.ts` asks here, so its region keeps only its argv literals (`bundleScan.test.ts`). */
const ROOT_OP_CALLS: Readonly<Record<string, FixedCall>> = { confirm: 'detach', fullCheck: 'detach', stop: 'stop', rootCheck: 'rootCheck' };

export function rootOpCall(op: { readonly op: string; readonly ids?: readonly string[] }): HostCall {
  const fixed = Object.hasOwn(ROOT_OP_CALLS, op.op) ? ROOT_OP_CALLS[op.op] : undefined;

  return fixed === undefined ? { call: 'rootPreview', ids: op.ids ?? [] } : { call: fixed };
}

export function ceilingMs(numbers: Numbers, call: HostCall): number {
  return 1000 * (call.call === 'rootPreview' ? rootPreviewS(numbers, call.ids) : numbers[SETTING_OF[call.call]]);
}
