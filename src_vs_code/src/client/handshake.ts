import type { Answer, DaemonVersion, Failure, JsonObject, StatusAnswer, Verdict } from './outcome';
import type { Verb } from './verbs';

/**
 * The per-verb schema handshake and the version rule — plan §6, "the compatibility rule" (§15g M2), written before the
 * first public consumer:
 *
 * - `schemaVersion` changes only on a BREAKING change; an additive field never bumps it.
 * - The client ignores keys it does not know and treats every field added after 0.1.0 as optional — an absent one reads
 *   as "update the daemon to see this", never as 0 and never as an error.
 * - Refusal is PER VERB: an unknown `preview` major blanks only *Cleanup*, an unknown `doctor` major only *Health*.
 * - The daemon's version is `status.productVersion`, else what `--version` prints (`x.y.z(+sha)?`); `unknown` is an
 *   unstamped build — render, do not refuse.
 */

export const SUPPORTED_SCHEMA: readonly number[] = [1];

export const MIN_DAEMON_FOR_RENDER = '0.1.0';

const MINIMUM: readonly [number, number, number] = [0, 1, 0];

const VERSION = /^(\d+)\.(\d+)\.(\d+)(?:\+[0-9A-Za-z.-]+)?$/;

/**
 * Read a version text. `0.0.0` (with or without `+<sha>`) is the number every build carries before its first release
 * (`src_daemon/version.txt`): a build from source, rendered like an unstamped one rather than refused as "older than
 * 0.1.0" — no released daemon is ever 0.0.0.
 */
export function parseDaemonVersion(text: string): DaemonVersion {
  const trimmed = text.trim();
  const found = VERSION.exec(trimmed);
  if (found === null) {
    return trimmed === 'unknown' ? { kind: 'unstamped' } : { kind: 'unrecognised', text: trimmed };
  }
  const parts: [number, number, number] = [Number(found[1]), Number(found[2]), Number(found[3])];

  return parts.every((p) => p === 0) ? { kind: 'development', text: trimmed } : { kind: 'release', text: trimmed, parts };
}

function older([major, minor, patch]: readonly [number, number, number], [toMajor, toMinor, toPatch]: readonly [number, number, number]): boolean {
  if (major !== toMajor) {
    return major < toMajor;
  }

  return minor !== toMinor ? minor < toMinor : patch < toPatch;
}

/** A released daemon older than `MIN_DAEMON_FOR_RENDER` is refused; every other version renders. */
export function versionRefusal(version: DaemonVersion): Failure | undefined {
  if (version.kind !== 'release' || !older(version.parts, MINIMUM)) {
    return undefined;
  }

  return { kind: 'daemonTooOld', version: version.text, minimum: MIN_DAEMON_FOR_RENDER };
}

function isObject(value: unknown): value is JsonObject {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function parseJson(text: string): unknown {
  try {
    return JSON.parse(text) as unknown;
  } catch {
    return undefined;
  }
}

function optionalString(value: unknown): string | undefined {
  return typeof value === 'string' ? value : undefined;
}

const LEVELS: readonly Verdict['level'][] = ['ok', 'warn', 'critical', 'unknown'];

function levelOf(value: unknown): Verdict['level'] {
  return LEVELS.find((level) => level === value) ?? 'unknown';
}

/** One verdict, or nothing when it has no id; an unknown level reads as `unknown`; other keys are ignored. */
function verdictOf(value: unknown): Verdict[] {
  if (!isObject(value) || typeof value.id !== 'string') {
    return [];
  }

  return [{ id: value.id, level: levelOf(value.level), value: optionalString(value.value), limit: optionalString(value.limit), reason: optionalString(value.reason) }];
}

function verdictsOf(value: unknown): readonly Verdict[] | undefined {
  return Array.isArray(value) ? value.flatMap(verdictOf) : undefined;
}

function statusAnswer(schemaVersion: number, body: JsonObject): StatusAnswer {
  return { verb: 'status', schemaVersion, productVersion: optionalString(body.productVersion), verdicts: verdictsOf(body.verdicts), body };
}

/** The body of a JSON verb, its schema checked; a refusal names why. */
function checkedBody(stdout: string): { body: JsonObject; schemaVersion: number } | Failure {
  const parsed = parseJson(stdout);
  if (parsed === undefined) {
    return { kind: 'unparseable', detail: 'the answer is not JSON' };
  }
  if (!isObject(parsed)) {
    return { kind: 'unparseable', detail: 'the answer is not a JSON object' };
  }

  return schemaOf(parsed);
}

function schemaOf(body: JsonObject): { body: JsonObject; schemaVersion: number } | Failure {
  const schemaVersion = body.schemaVersion;
  if (typeof schemaVersion !== 'number' || !Number.isInteger(schemaVersion)) {
    return { kind: 'unparseable', detail: 'the answer carries no integer schemaVersion' };
  }

  return SUPPORTED_SCHEMA.includes(schemaVersion) ? { body, schemaVersion } : { kind: 'needsNewerExtension', schemaVersion };
}

/** A daemon answer read for `verb`: its typed parts, or why it cannot be rendered. */
export function parseAnswer(verb: Verb, stdout: string): Answer | Failure {
  if (verb === 'version') {
    return { verb, version: parseDaemonVersion(stdout) };
  }
  const checked = checkedBody(stdout);
  if ('kind' in checked) {
    return checked;
  }

  return verb === 'status' ? statusAnswer(checked.schemaVersion, checked.body) : { verb, schemaVersion: checked.schemaVersion, body: checked.body };
}
