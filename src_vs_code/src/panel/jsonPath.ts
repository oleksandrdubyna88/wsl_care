/**
 * Reads a field of a daemon answer by the path the field map names (`fieldMap.ts`): dotted keys, and `name[id=x]` for
 * the member of an array whose `id` is `x` (ids may contain dots: `verdicts[id=clock.jumps]`). A path that is not of
 * that shape throws — a typo in the table is a red test, never a silently empty row.
 */

const PATH = /^[A-Za-z][A-Za-z0-9]*(?:\[id=[^\]]+\])?(?:\.[A-Za-z][A-Za-z0-9]*(?:\[id=[^\]]+\])?)*$/;
const STEP = /([A-Za-z][A-Za-z0-9]*)(?:\[id=([^\]]+)\])?/y;

interface Step {
  readonly key: string;
  readonly id: string | undefined;
}

function stepsOf(path: string): Step[] {
  if (!PATH.test(path)) {
    throw new Error(`"${path}" is not a field path (key, key.key, key[id=x])`);
  }
  const steps: Step[] = [];
  STEP.lastIndex = 0;
  for (let found = STEP.exec(path); found !== null; found = STEP.exec(path)) {
    steps.push({ key: found[1] ?? '', id: found[2] });
    STEP.lastIndex += 1;
  }

  return steps;
}

function isObject(value: unknown): value is Readonly<Record<string, unknown>> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function member(value: unknown, id: string): unknown {
  return Array.isArray(value) ? value.find((item) => isObject(item) && item.id === id) : undefined;
}

function take(value: unknown, step: Step): unknown {
  const next = isObject(value) ? value[step.key] : undefined;

  return step.id === undefined ? next : member(next, step.id);
}

/** The value at `path` in `body`, or `undefined` when any step is missing. */
export function at(body: unknown, path: string): unknown {
  return stepsOf(path).reduce<unknown>((value, step) => take(value, step), body);
}

/**
 * When the value at `path` is missing because an ANCESTOR was answered `{ available: false, reason }` (the daemon writes a
 * part it could not read — `vm`, `vm.memory` — that way, with no children), that ancestor: the deepest one on the path.
 * `undefined` when no ancestor says so — the field is then genuinely absent (an older daemon).
 */
export function unavailableAncestor(body: unknown, path: string): Readonly<Record<string, unknown>> | undefined {
  return stepsOf(path).reduce<Walk>(stepInto, { value: body, found: undefined }).found;
}

/** Where a walk down a path is: the value reached, and the deepest `{ available: false }` met on the way. */
interface Walk {
  readonly value: unknown;
  readonly found: Readonly<Record<string, unknown>> | undefined;
}

function isUnavailablePart(value: unknown): value is Readonly<Record<string, unknown>> {
  return isObject(value) && value.available === false;
}

/** One step further down; a walk that already left the answer stays where it stopped. */
function stepInto(walk: Walk, step: Step): Walk {
  if (walk.value === undefined) {
    return walk;
  }
  const value = take(walk.value, step);

  return { value, found: isUnavailablePart(value) ? value : walk.found };
}
