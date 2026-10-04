/**
 * The typed values a root call may carry (E6.S2, plan §15f #2, §15j B1 / M1): each arrives as untrusted text — from a
 * daemon answer, from host state, later from a webview message's index — and becomes argv or a stdin line ONLY through
 * one of the validators below. The types are branded, so the closed `ROOT_OPS` union of `rootCall.ts` cannot be handed a
 * raw string by mistake; the validators are the one road in (`common.security`: one road, not a check at each site).
 */

/**
 * The COMPILED action registry — the ids this extension version knows how to offer. Acting is limited to this list ∩
 * the ids the daemon reports in `status.actions` (plan §15f #2, §15j M5). `rootIds.test.ts` holds it EQUAL to
 * `contracts/actions.json`, which the daemon writes from `ActionId.All`: an id the daemon adds is not acted on until this
 * list knows it, and the test says so on the day the contract changes.
 */
export const ACTION_IDS = [
  'A1', 'A2', 'A3', 'A4', 'A5', 'A5Testcontainers', 'A6', 'A6Unused', 'A7', 'A8', 'A9', 'A10', 'A11', 'A12', 'A13', 'A14', 'A15', 'A16', 'A17',
] as const;

export type ActionId = (typeof ACTION_IDS)[number];

/** One or more registry ids — never an empty `act`. */
export type ActionIds = readonly [ActionId, ...ActionId[]];

/** A run id as the daemon spells it (`RunId.New`): `yyyyMMddTHHmmssZ-<pid>`. */
export type RunId = string & { readonly __brand: 'RunId' };

/** An anonymous Docker volume's full name — what A4's shown list holds. */
export type VolumeName = string & { readonly __brand: 'VolumeName' };

/** The most names A4's preview shows and a confirm may pipe back — the daemon's `ShownList.MaxNames` (plan §15k #11). */
export const MAX_SHOWN_VOLUMES = 10_000;

/**
 * The daemon's one spelling of a run id: the UTC stamp, a dash, the pid with NO leading zero (`RunId.TryParse`, E6.S0
 * review S4 — two spellings would name one run twice). Stricter than the daemon in one place: pid 0 is refused, because no
 * run has it.
 */
const RUN_ID = /^[0-9]{8}T[0-9]{6}Z-[1-9][0-9]{0,9}$/;

/** 64 lowercase hex digits — the daemon's `DockerJson.IsFullId`, the only shape A4's `--only` lines may take. */
const VOLUME_NAME = /^[0-9a-f]{64}$/;

export function actionIdOf(value: unknown): ActionId | undefined {
  return ACTION_IDS.find((id) => id === value);
}

export function runIdOf(value: unknown): RunId | undefined {
  return typeof value === 'string' && RUN_ID.test(value) ? (value as RunId) : undefined;
}

export function volumeNameOf(value: unknown): VolumeName | undefined {
  return typeof value === 'string' && VOLUME_NAME.test(value) ? (value as VolumeName) : undefined;
}
