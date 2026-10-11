import { confirmNeeds, previewNeeds } from '../root/cleanupController';
import type { Snapshot } from '../state/outcomeStore';
import { advertisedOf, offersAction, statusBodyOf } from '../state/statusBody';
import { safeText } from '../text/safeText';

/**
 * The client-gone relays' part of the panel (S7b.2b, daemon E14 S7b.2): ONE line from the daemon's `status.interopRelays` — how many
 * Windows MCP relays this window's user has, how many are re-parented, and whether the timer stops client-gone ones, with the
 * command that flips it (the extension never writes daemon config) — and the *Stop client-gone relays…* button, which runs A21
 * through the cleanup host's transaction. Derived by the host, never a page flag; pure.
 */

export type RelaysLevel = 'none' | 'warn';

export interface RelaysControls {
  /** '' when the daemon publishes no `interopRelays` (an older daemon, the Windows binary): no line, no button. */
  readonly line: string;
  readonly level: RelaysLevel;
  /** '' — or why the button is greyed, in a sentence. */
  readonly why: string;
  readonly button: { readonly id: 'stopRelays'; readonly label: string; readonly enabled: boolean } | undefined;
}

export const STOP_RELAYS_LABEL = 'Stop client-gone relays…';

/** The relays' page actions and their labels — what `commandButtons.test.ts` checks a page button against. */
export const RELAYS_LABELS: Readonly<Record<'stopRelays', string>> = { stopRelays: STOP_RELAYS_LABEL };

/**
 * What *Stop client-gone relays* needs advertised: exactly what the controller's preview and confirm of A21 need — ONE definition,
 * the controller's, so a capability A21 gains there greys the button here (as *Archive now*'s).
 */
export const STOP_RELAYS_CAPABILITIES: readonly string[] = [...new Set([...previewNeeds(['A21']), ...confirmNeeds(['A21'])])];

const REASON_MAX = 200;

/** The cleanup host's side of the button: its controls are free, or why not. */
export interface CleanupSide {
  readonly enabled: boolean;
  readonly reason: string;
}

function isObject(value: unknown): value is Readonly<Record<string, unknown>> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function count(value: unknown): string {
  return typeof value === 'number' && Number.isInteger(value) && value >= 0 ? String(value) : '?';
}

/** The timer's switch, as the daemon says it is, and the daemon's own command that flips it. */
function switchText(block: Readonly<Record<string, unknown>>): string {
  const on = block.autoStop === true;

  return `the timer stops client-gone relays: ${on ? 'on' : 'off'}${commandText(block.command, on)}`;
}

/** The daemon's command that flips the switch, as the line says it — or nothing when the daemon gave none. */
function commandText(command: unknown, on: boolean): string {
  return typeof command === 'string' && command !== '' ? ` — to turn it ${on ? 'off' : 'on'}: ${safeText(command, REASON_MAX)}` : '';
}

function lineOf(block: Readonly<Record<string, unknown>>): { readonly line: string; readonly level: RelaysLevel } {
  if (block.available !== true) {
    const reason = typeof block.reason === 'string' ? safeText(block.reason, REASON_MAX) : 'no reason given';
    return { line: `Windows MCP relays: unavailable — ${reason} · ${switchText(block)}`, level: 'warn' };
  }

  return { line: `Windows MCP relays: ${count(block.count)} (${count(block.reparented)} re-parented) · ${switchText(block)}`, level: 'none' };
}

/** The button's conditions, in the order they are told — the cleanup host's own first. */
function blocker(snapshot: Snapshot, cleanup: CleanupSide): string {
  if (!cleanup.enabled) {
    return cleanup.reason === '' ? 'a cleanup is in flight' : cleanup.reason;
  }

  return daemonBlocker(snapshot);
}

/** The daemon's side: it offers A21 and advertises the road — '' when it does. */
function daemonBlocker(snapshot: Snapshot): string {
  if (!offersAction(snapshot, 'A21')) {
    return 'the daemon does not offer A21';
  }
  const advertised = advertisedOf(snapshot) ?? [];
  const missing = STOP_RELAYS_CAPABILITIES.filter((c) => !advertised.includes(c));

  return missing.length === 0 ? '' : `the daemon does not advertise ${missing.join(', ')}`;
}

/**
 * Why *Stop client-gone relays* cannot start — '' when it can. The host refuses on the same conditions the button greys on: the
 * palette's command and a stale page send the same bare request.
 */
export function stopRelaysBlocker(snapshot: Snapshot, cleanup: CleanupSide): string {
  return isObject(statusBodyOf(snapshot)?.interopRelays) ? blocker(snapshot, cleanup) : 'the daemon publishes no interop relays (an older daemon, or the Windows side)';
}

export function deriveRelays(snapshot: Snapshot, cleanup: CleanupSide): RelaysControls {
  const block = statusBodyOf(snapshot)?.interopRelays;
  if (!isObject(block)) {
    return NO_RELAYS;
  }
  const why = blocker(snapshot, cleanup);

  return { ...lineOf(block), why, button: { id: 'stopRelays', label: STOP_RELAYS_LABEL, enabled: why === '' } };
}

export const NO_RELAYS: RelaysControls = { line: '', level: 'none', why: '', button: undefined };
