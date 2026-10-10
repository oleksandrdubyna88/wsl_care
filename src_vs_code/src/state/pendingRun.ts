import type { DurableStore } from '../cleanup/journal';

/**
 * An elevated run a window started and has not seen end, persisted in `globalState` under its own key
 * (`common.durable-status` rule 1): written BEFORE the launch, cleared when the launcher answers, and — because the elevated
 * child is the UAC broker's, not ours, and may still run or its prompt still be open — kept after a TIMEOUT until its
 * deadline. A record past its deadline (or malformed) is none, and is swept at activation, so nothing stays "waiting".
 *
 * The shared half of the Windows Time guard's pending install / remove (`windowsTime/guardPending.ts`) and *Enable <GPU>*'s
 * pending enable (`gpu/gpuPending.ts`), extracted when the second one arrived (`common.reuse-first` step 2.2): each keeps its
 * own key and its own record shape; the deadline rule is written once.
 */

export interface PendingTimes {
  readonly startedAtUtcMs: number;
  readonly deadlineUtcMs: number;
}

export type Parse<T> = (value: Readonly<Record<string, unknown>>) => T | undefined;

function isRecord(value: unknown): value is Readonly<Record<string, unknown>> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function timesOf(value: Readonly<Record<string, unknown>>): PendingTimes | undefined {
  return Number.isFinite(value.startedAtUtcMs) && Number.isFinite(value.deadlineUtcMs)
    ? { startedAtUtcMs: value.startedAtUtcMs as number, deadlineUtcMs: value.deadlineUtcMs as number }
    : undefined;
}

function recordOf<T>(value: unknown, parse: Parse<T>): (T & PendingTimes) | undefined {
  if (!isRecord(value)) {
    return undefined;
  }
  const own = parse(value);
  const times = timesOf(value);

  return own === undefined || times === undefined ? undefined : { ...own, ...times };
}

/** The pending run under `key` while it stands: a malformed value, or one past its deadline, is none. */
export function readPendingAt<T>(store: DurableStore, key: string, parse: Parse<T>, nowUtcMs: number): (T & PendingTimes) | undefined {
  const record = recordOf(store.get(key), parse);

  return record !== undefined && nowUtcMs < record.deadlineUtcMs ? record : undefined;
}

export async function writePendingAt<T extends object>(store: DurableStore, key: string, record: T & PendingTimes): Promise<void> {
  await store.update(key, { ...record });
}

export async function clearPendingAt(store: DurableStore, key: string): Promise<void> {
  await store.update(key, undefined);
}

/** At activation: a record past its deadline (or malformed) is removed. */
export async function sweepPendingAt<T>(store: DurableStore, key: string, parse: Parse<T>, nowUtcMs: number): Promise<void> {
  if (store.get(key) !== undefined && readPendingAt(store, key, parse, nowUtcMs) === undefined) {
    await clearPendingAt(store, key);
  }
}
