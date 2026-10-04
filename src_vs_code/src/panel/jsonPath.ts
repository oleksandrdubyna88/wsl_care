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
