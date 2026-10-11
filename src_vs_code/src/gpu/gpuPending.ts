import type { DurableStore } from '../cleanup/journal';
import { clearPendingAt, readPendingAt, sweepPendingAt, writePendingAt, type PendingTimes } from '../state/pendingRun';
import { safeText } from '../text/safeText';
import type { PendingEnable } from './gpuState';

/**
 * *Enable <GPU>*'s in-flight state (PLAN_gpu_health.md D5, `common.durable-status`): persisted BEFORE the elevated launcher
 * starts, cleared when it answers or when Windows already shows the device enabled, and swept after its deadline (the
 * enable ceiling + a grace), so a reload mid-run shows "waiting…" and never sticks there. Its own key; the deadline rule is
 * the shared one (`state/pendingRun.ts`).
 */

export const GPU_PENDING_KEY = 'wslCare.gpu.pendingEnable';

/** After the enable ceiling, how long a pending record still stands — the UAC broker's child may outlive our wait. */
export const PENDING_GRACE_MS = 60_000;

export interface GpuPendingRecord extends PendingEnable, PendingTimes {
  readonly id: string;
}

const NAME_MAX = 120;

function parse(value: Readonly<Record<string, unknown>>): (PendingEnable & { readonly id: string }) | undefined {
  return typeof value.name === 'string' && typeof value.id === 'string' ? { name: safeText(value.name, NAME_MAX), id: value.id } : undefined;
}

export function readGpuPending(store: DurableStore, nowUtcMs: number): GpuPendingRecord | undefined {
  return readPendingAt(store, GPU_PENDING_KEY, parse, nowUtcMs);
}

export async function writeGpuPending(store: DurableStore, record: GpuPendingRecord): Promise<void> {
  await writePendingAt(store, GPU_PENDING_KEY, record);
}

export async function clearGpuPending(store: DurableStore): Promise<void> {
  await clearPendingAt(store, GPU_PENDING_KEY);
}

export async function sweepGpuPending(store: DurableStore, nowUtcMs: number): Promise<void> {
  await sweepPendingAt(store, GPU_PENDING_KEY, parse, nowUtcMs);
}
