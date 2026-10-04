import { age, gb, gib, safeText } from './format';

/**
 * Typed reads of a daemon answer's parts, for the row renderers. Every string that leaves here has passed `safeText`;
 * every figure that is `available: false` reads "unavailable — <reason>"; a part that is not there reads as such —
 * never as 0 and never as an empty string.
 */

export type Json = Readonly<Record<string, unknown>>;

export const UNAVAILABLE = 'unavailable — ';

export function isJson(value: unknown): value is Json {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function scalar(field: unknown): string | undefined {
  return typeof field === 'number' || typeof field === 'boolean' ? String(field) : undefined;
}

/** A field spelt as text: a non-empty string cleaned, a number or boolean spelt; anything else `undefined`. */
function spelt(field: unknown): string | undefined {
  if (typeof field === 'string') {
    return field === '' ? undefined : safeText(field);
  }

  return scalar(field);
}

/** A field as text (strings cleaned, numbers and booleans spelt), or `fallback`. */
export function text(value: unknown, key: string, fallback = '—'): string {
  return spelt(isJson(value) ? value[key] : undefined) ?? fallback;
}

/** A numeric field, or `undefined`. */
export function num(value: unknown, key: string): number | undefined {
  const field = isJson(value) ? value[key] : undefined;
  return typeof field === 'number' ? field : undefined;
}

/** A numeric field the daemon always sends with its object (a total, a count): 0 only if the object lacks it. */
export function amount(value: unknown, key: string): number {
  return num(value, key) ?? 0;
}

/** An array field (or the value itself when it is an array), as a list of objects. */
export function list(value: unknown, key?: string): Json[] {
  const field = key === undefined ? value : isJson(value) ? value[key] : undefined;
  return Array.isArray(field) ? field.filter(isJson) : [];
}

/** Whether `value` is an `{ available: false, reason }` figure. */
export function isUnavailable(value: unknown): boolean {
  return isJson(value) && value.available === false;
}

export function unavailableText(value: unknown): string {
  return UNAVAILABLE + text(value, 'reason', 'no reason given');
}

/** An `{ available, bytes }` figure, shown with `unit`, or why not. */
export function sized(value: unknown, unit: (bytes: number) => string = gib): string {
  if (isUnavailable(value)) {
    return unavailableText(value);
  }
  const bytes = num(value, 'bytes');

  return bytes === undefined ? '—' : unit(bytes);
}

/** An `{ available, value }` reading through `show`, or why not. */
export function reading(value: unknown, show: (n: number) => string): string {
  const n = num(value, 'value');
  if (n === undefined) {
    return isUnavailable(value) ? unavailableText(value) : '—';
  }

  return show(n);
}

/** "from the full run <runId>, <age>" — the age a slow part carries (plan §6: slow parts from the last full run). */
export function fromRun(value: unknown): string {
  const seconds = num(value, 'ageSeconds');
  return `from the full run ${text(value, 'runId')}, ${seconds === undefined ? 'age unknown' : age(seconds)}`;
}

export { gb, gib };
