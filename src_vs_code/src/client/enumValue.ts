/**
 * How every daemon enum is read (plan §6, §15j m1): `schemaVersion` stays 1 for ADDITIVE changes, and a new enum value is
 * additive — a `running.state` of a later daemon, a detach `result` this build has never seen. So a value outside the
 * known set is neither a crash nor silently one of the known values: it reads as "unknown (<value>)", which the caller
 * treats as its unknown case and a person can still read.
 *
 * <p>The raw value is attacker-settable text from a daemon answer, so the label goes through the one sanitiser
 * (`text/safeText.ts`: control and bidirectional-override characters made visible) and keeps at most 40 characters.</p>
 */
import { safeText } from '../text/safeText';

export type EnumRead<T extends string> = { readonly kind: 'known'; readonly value: T } | { readonly kind: 'unknown'; readonly label: string };

/** The longest stretch of an unknown value a label carries. */
const LABEL_MAX = 40;

function isScalar(value: unknown): value is string | number | boolean {
  return ['string', 'number', 'boolean'].includes(typeof value);
}

function shown(value: unknown): string {
  if (value === undefined) {
    return 'absent';
  }

  return isScalar(value) ? safeText(String(value), LABEL_MAX) : 'not a string';
}

export function readEnum<T extends string>(value: unknown, known: readonly T[]): EnumRead<T> {
  const found = known.find((k) => k === value);

  return found === undefined ? { kind: 'unknown', label: `unknown (${shown(value)})` } : { kind: 'known', value: found };
}
