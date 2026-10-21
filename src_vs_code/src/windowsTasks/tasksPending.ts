import type { DurableStore } from '../cleanup/journal';
import { TASK_NAMES, type TaskName } from '../client/verbs';
import { clearPendingAt, readPendingAt, sweepPendingAt, writePendingAt, type PendingTimes } from '../state/pendingRun';

/**
 * The install / remove of the Windows tasks a window started and has not seen end (WR-E3 of PLAN_windows_runner.md §3.8,
 * `common.durable-status` rule 1): written to `globalState` BEFORE the one PowerShell starts, cleared when it answers — with ANY answer,
 * a failure, a crash or a refused start included — and kept, after a TIMEOUT, until its deadline, because the PowerShell the extension
 * ended at its ceiling may have left a registration half done (the tree kill is verified, not assumed). While it stands, every button of
 * the Windows tasks is disabled in every window and across a reload, so an install and a removal can never run at once; a deadline in the
 * past is swept by the next read and at activation. The deadline rule is the shared one (`state/pendingRun.ts`); the key is this part's own
 * (the guard keeps its own — the two scripts tolerate each other: an install creates the folder when it is missing, a removal deletes it
 * only when it is empty).
 */

export const PENDING_KEY = 'wslCare.windowsTasks.pending';

/** What a pending run is: an install of one task, the start now of one, or the removal of both. */
export type PendingTasksOp = { readonly op: 'install'; readonly task: TaskName } | { readonly op: 'run'; readonly task: TaskName } | { readonly op: 'remove' };

export type PendingRecord = PendingTasksOp & PendingTimes;

/** The ops that name a task. */
const TASKED: ReadonlySet<unknown> = new Set(['install', 'run']);

function opOf(value: Readonly<Record<string, unknown>>): PendingTasksOp | undefined {
  if (value.op === 'remove') {
    return { op: 'remove' };
  }
  const task = TASK_NAMES.find((name) => name === value.task);

  return task !== undefined && TASKED.has(value.op) ? { op: value.op as 'install' | 'run', task } : undefined;
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

/**
 * Whether the ONE PowerShell of `record` must have answered or timed out by `nowUtcMs`: its deadline is twice its own ceiling (the flow writes
 * `started + 2 × ceiling`), so the ceiling is the first half of the span. Before that, another window's run may still be registering — a read
 * that already shows the task must not release its buttons (a second window's Remove would run beside the first one's Register); after it,
 * the record is only waiting out the post-timeout half, and a read that shows the task finished may settle it early.
 */
export function ceilingPassed(record: PendingTimes, nowUtcMs: number): boolean {
  return nowUtcMs >= record.startedAtUtcMs + (record.deadlineUtcMs - record.startedAtUtcMs) / 2;
}

/** At activation: a record past its deadline (or malformed) is removed, so nothing is stuck "waiting" forever. */
export async function sweepPending(store: DurableStore, nowUtcMs: number): Promise<void> {
  await sweepPendingAt(store, PENDING_KEY, opOf, nowUtcMs);
}
