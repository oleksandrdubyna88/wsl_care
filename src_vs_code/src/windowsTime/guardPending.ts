import type { DurableStore } from '../cleanup/journal';
import { clearPendingAt, readPendingAt, sweepPendingAt, writePendingAt, type PendingTimes } from '../state/pendingRun';
import type { PendingGuardOp } from './guardState';

/**
 * The elevated install / remove a window started and has not seen end (PLAN_windows_time_task.md D8 c4/o11,
 * `common.durable-status` rule 1): written to `globalState` BEFORE the launch, cleared when the launcher answers — kept,
 * after a TIMEOUT, until its deadline, because the elevated child (the UAC broker's, not ours) may still be running or its
 * UAC prompt still open. While it stands, both buttons are disabled in every window and across a reload, so an install and
 * a removal can never run at once; a deadline in the past is swept by the next read. The deadline rule is the shared one
 * (`state/pendingRun.ts`, since *Enable <GPU>* needed the same).
 */

export const PENDING_KEY = 'wslCare.windowsTimeGuard.pending';

export interface PendingRecord extends PendingGuardOp, PendingTimes {}

const OPS: ReadonlySet<unknown> = new Set(['install', 'remove']);

function opOf(value: Readonly<Record<string, unknown>>): PendingGuardOp | undefined {
  return OPS.has(value.op) ? { op: value.op as PendingGuardOp['op'] } : undefined;
}

/** The pending run while it stands: a malformed value, or one past its deadline, is none. */
export function readPending(store: DurableStore, nowUtcMs: number): PendingRecord | undefined {
  return readPendingAt(store, PENDING_KEY, opOf, nowUtcMs);
}

export async function writePending(store: DurableStore, record: PendingRecord): Promise<void> {
  await writePendingAt(store, PENDING_KEY, record);
}

export async function clearPending(store: DurableStore): Promise<void> {
  await clearPendingAt(store, PENDING_KEY);
}

/** At activation: a record past its deadline (or malformed) is removed, so nothing is stuck "waiting" forever. */
export async function sweepPending(store: DurableStore, nowUtcMs: number): Promise<void> {
  await sweepPendingAt(store, PENDING_KEY, opOf, nowUtcMs);
}
