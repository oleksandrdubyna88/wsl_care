import { canEnable, type GpuState } from './gpuState';

/**
 * *AI OS Care: Enable a disabled GPU…* (PLAN_gpu_health.md D7) duplicates the alert's button: which adapters it may offer,
 * from the host's OWN reading — the indexes the button would post. Pure.
 */

export interface EnableChoice {
  readonly index: number;
  readonly label: string;
}

/** Every adapter at code 22 whose id the enable would pass, by index. */
export function enableChoices(state: GpuState): readonly EnableChoice[] {
  return state.kind === 'read' ? state.adapters.flatMap((a, index) => (canEnable(a) ? [{ index, label: a.name }] : [])) : [];
}

export const NONE_DISABLED = 'No GPU is disabled: Windows reports none of its display adapters at code 22.';
