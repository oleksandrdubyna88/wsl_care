import { MIN_DAEMON_FOR_ACTIONS } from '../client/handshake';
import type { DaemonVersion, JsonObject } from '../client/outcome';
import { ACTION_IDS, BUTTON_ONLY_IDS, type ActionId, type ActionIds } from './rootIds';
import type { RootFailure } from './rootOutcome';
import { daemonLimitsOf, type DaemonLimits } from '../shared/daemonLimits';

/**
 * May this daemon be asked to act, and on which ids (E6.S2, plan §15f #2 / #3, §15j M5)?
 *
 * - **The capabilities are the AUTHORITY.** `status.capabilities` must hold every capability the op needs; the daemon's
 *   version only supplies the MESSAGE ("the daemon is x.y; cleanups need 0.1.0 or newer — Update daemon"). So an unstamped
 *   or `0.0.0+sha` build that advertises them acts, and a numbered release that does not, does not.
 * - **The ids are the compiled registry ∩ `status.actions`** — an id this extension does not know, or one the daemon does
 *   not report, is refused before anything starts, naming what IS offered.
 *
 * Re-checked at EVERY root op (the preview and its confirm each read a fresh `status`), so a daemon downgraded between the
 * two is caught at the confirm.
 */

/** What the gate let through: the ids that may be acted on, in the registry's order. */
export interface GateOpen {
  readonly kind: 'open';
  readonly allowed: readonly ActionId[];
  /** The daemon's published limits in that same status (daemon #17) — what the root call is sized under. */
  readonly limits: DaemonLimits;
}

function strings(value: unknown): readonly string[] {
  return Array.isArray(value) ? value.filter((v): v is string => typeof v === 'string') : [];
}

/** The version text the "Update daemon" message names. */
function versionText(version: DaemonVersion): string {
  return version.kind === 'release' || version.kind === 'development' || version.kind === 'unrecognised' ? version.text : 'unknown';
}

/** The gate for an op that needs `required`, over a `status` body and the daemon version it was judged against. */
export function actionGate(status: JsonObject, version: DaemonVersion, required: readonly string[]): GateOpen | RootFailure {
  const advertised = strings(status.capabilities);
  const missing = required.filter((capability) => !advertised.includes(capability));
  if (missing.length > 0) {
    return { kind: 'actionsUnavailable', version: versionText(version), minimum: MIN_DAEMON_FOR_ACTIONS, missing };
  }
  const reported = strings(status.actions);

  return { kind: 'open', allowed: ACTION_IDS.filter((id) => reported.includes(id) && !BUTTON_ONLY_IDS.includes(id)), limits: daemonLimitsOf(status) };
}

/** The requested ids as the gate allows them — each once, in the registry's order — or the refusal naming the others. */
export function pickIds(requested: readonly string[], gate: GateOpen): ActionIds | RootFailure {
  const refused = requested.filter((id) => !gate.allowed.some((allowed) => allowed === id));
  const picked = gate.allowed.filter((id) => requested.includes(id));
  const [first, ...rest] = picked;

  return refused.length > 0 || first === undefined ? { kind: 'idsRefused', refused: [...new Set(refused)], allowed: gate.allowed } : [first, ...rest];
}
