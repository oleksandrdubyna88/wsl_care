/**
 * A daemon answer as the tests hold it: an object of unknown fields — CHECKED, never cast (TypeScript doctrine §3; the
 * E10.S1b second code round's finding on `JSON.parse(…) as Body`). A golden that is an array, a scalar or `null` is refused where
 * it is read, with the fixture's name, rather than failing later in whatever test first indexes it.
 */

export type Body = Record<string, unknown>;

/** An object that is not an array and not `null` — the one shape every daemon answer has. */
export function isBody(value: unknown): value is Body {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** `value` as a body — or an error naming `what`, so the fixture that broke is the one reported. */
export function bodyOf(value: unknown, what: string): Body {
  if (!isBody(value)) {
    throw new Error(`${what} is not an object: ${JSON.stringify(value)}`);
  }

  return value;
}

/** The body at `key` of `body` — a nested block such as `status.running`. */
export function bodyAt(body: Body, key: string): Body {
  return bodyOf(body[key], key);
}

/** The strings at `key` of `body` — a list such as `status.capabilities`, every entry a string. */
export function stringsAt(body: Body, key: string): string[] {
  const value = body[key];
  if (!Array.isArray(value) || !value.every((entry): entry is string => typeof entry === 'string')) {
    throw new Error(`${key} is not a list of strings: ${JSON.stringify(value)}`);
  }

  return value;
}
