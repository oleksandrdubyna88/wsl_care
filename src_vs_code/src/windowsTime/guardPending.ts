import type { DurableStore } from '../cleanup/journal';
import type { PendingGuardOp } from './guardState';

/**
 * The elevated install / remove a window started and has not seen end (PLAN_windows_time_task.md D8 c4/o11,
 * `common.durable-status` rule 1): written to `globalState` BEFORE the launch, cleared when the launcher answers — kept,
 * after a TIMEOUT, until its deadline, because the elevated child (the UAC broker's, not ours) may still be running or its
 * UAC prompt still open. While it stands, both buttons are disabled in every window and across a reload, so an install and
 * a removal can never run at once; a deadline in the past is swept by the next read.
 */

export const PENDING_KEY = 'wslCare.windowsTimeGuard.pending';

export interface PendingRecord extends PendingGuardOp {
  readonly startedAtUtcMs: number;
  readonly deadlineUtcMs: number;
}

function isRecord(value: unknown): value is Readonly<Record<string, unknown>> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

const OPS: ReadonlySet<unknown> = new Set(['install', 'remove']);

function hasInstants(value: Readonly<Record<string, unknown>>): boolean {
  return Number.isFinite(value.startedAtUtcMs) && Number.isFinite(value.deadlineUtcMs);
}

function recordOf(value: unknown): PendingRecord | undefined {
  if (!isRecord(value) || !OPS.has(value.op)) {
    return undefined;
  }

  return hasInstants(value) ? { op: value.op as PendingRecord['op'], startedAtUtcMs: value.startedAtUtcMs as number, deadlineUtcMs: value.deadlineUtcMs as number } : undefined;
}

/** The pending run while it stands: a malformed value, or one past its deadline, is none. */
export function readPending(store: DurableStore, nowUtcMs: number): PendingRecord | undefined {
  const record = recordOf(store.get(PENDING_KEY));

  return record !== undefined && nowUtcMs < record.deadlineUtcMs ? record : undefined;
}

export async function writePending(store: DurableStore, record: PendingRecord): Promise<void> {
  await store.update(PENDING_KEY, { ...record });
}

export async function clearPending(store: DurableStore): Promise<void> {
  await store.update(PENDING_KEY, undefined);
}

/** At activation: a record past its deadline (or malformed) is removed, so nothing is stuck "waiting" forever. */
export async function sweepPending(store: DurableStore, nowUtcMs: number): Promise<void> {
  if (store.get(PENDING_KEY) !== undefined && readPending(store, nowUtcMs) === undefined) {
    await clearPending(store);
  }
}
