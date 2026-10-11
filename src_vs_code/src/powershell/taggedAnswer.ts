/**
 * A PowerShell query's tagged answer (`name=value` per line) — the ONE strict reader, built for the GPU query (code round 2)
 * and shared since PLAN_pwsh_engine_windows_time.md §11 R3 with the engine read: a tag printed TWICE is named, never read
 * first- or last-wins, so a value that carried a line break cannot shadow another tag.
 */

const NONE: ReadonlySet<string> = new Set();

/** A line's tag and value, or nothing: a line without a `=`, and a tag the caller ignores. */
function entryOf(line: string, ignored: ReadonlySet<string>): readonly (readonly [string, string])[] {
  const at = line.indexOf('=');
  const name = line.slice(0, at);

  return at > 0 && !ignored.has(name) ? [[name, line.slice(at + 1).trim()] as const] : [];
}

/** The answer's `name=value` lines, in order. */
function entriesOf(stdout: string, ignored: ReadonlySet<string>): readonly (readonly [string, string])[] {
  return stdout.split(/\r?\n/).flatMap((line) => entryOf(line, ignored));
}

export interface Tags {
  readonly tags: ReadonlyMap<string, string>;
  /** The first tag the answer printed TWICE — a value that could have shadowed another (code round 2) — or `undefined`. */
  readonly twice: string | undefined;
}

/** The answer as a map built ONCE from its lines; a tag printed twice is named, never read first- or last-wins. */
export function tagsOf(stdout: string, ignored: ReadonlySet<string> = NONE): Tags {
  const entries = entriesOf(stdout, ignored);
  const twice = entries.find(([name], i) => entries.findIndex(([other]) => other === name) !== i)?.[0];

  return { tags: new Map(entries), twice };
}
