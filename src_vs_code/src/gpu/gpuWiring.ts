import * as fs from 'node:fs';
import * as vscode from 'vscode';

import type { Numbers } from '../settings/numbers';
import { runnerFor } from '../process/runnerSelection';
import { enableChoices, noChoiceMessage, type EnableChoice } from './gpuCommand';
import { GpuHost } from './gpuHost';
import type { GpuView } from './gpuState';
import { sweepGpuPending } from './gpuPending';
import { gpuUiFor, newGpuRecorder, type GpuRecorder } from './gpuUi';

/**
 * The GPU story's VS Code wiring (PLAN_gpu_health.md), kept out of `extension.ts`: its host reads Windows through the REAL
 * runner (never the fake wsl.exe; a recorder in Test mode), its enable runs ONE elevated PowerShell, and a pending enable
 * past its deadline is swept at activation, so nothing stays "waiting" after a crash.
 */

export interface GpuParts {
  readonly host: GpuHost;
  readonly recorder: GpuRecorder;
  /** The command and the panel button: the adapter at `index` (the button), or a pick among the disabled ones (the palette). */
  readonly enable: (index?: number) => void;
}

const REAL_TIMERS = (ms: number, run: () => void): (() => void) => {
  const handle = setInterval(run, ms);
  return () => clearInterval(handle);
};

export function gpuParts(context: vscode.ExtensionContext, testMode: boolean, numbers: () => Numbers, log: (line: string) => void): GpuParts {
  void sweepGpuPending(context.globalState, Date.now());
  const recorder = newGpuRecorder();
  const host = new GpuHost({
    env: process.env,
    platform: process.platform,
    exists: (file) => fs.existsSync(file),
    queryTimeoutMs: () => numbers().gpuQuerySeconds * 1000,
    enableTimeoutMs: () => numbers().gpuEnableSeconds * 1000,
    enableSettleSeconds: () => numbers().gpuEnableSettleSeconds,
    ollamaTailBytes: () => numbers().gpuOllamaLogTailKiB * 1024,
    checkMinutes: () => numbers().gpuCheckMinutes,
    ui: gpuUiFor(testMode, recorder, runnerFor({ kind: 'real' })),
    durable: context.globalState,
    nowUtcMs: () => Date.now(),
    every: REAL_TIMERS,
    after: (ms, run) => { const handle = setTimeout(run, ms); return () => clearTimeout(handle); },
    formatTime: (utcMs) => new Date(utcMs).toLocaleTimeString(),
  });
  const run = (index: number): void => { void host.enable(index).catch((e: unknown) => { log(`Enable GPU failed: ${String(e)}`); }); };
  const enable = (index?: number): void => {
    if (index !== undefined) {
      run(index);
      return;
    }
    void pick(host, testMode).then((chosen) => { if (chosen !== undefined) { run(chosen); } });
  };
  context.subscriptions.push({ dispose: () => host.dispose() });

  return { host, recorder, enable };
}

/** The panel's three GPU members (`PanelActions`): the view, its change, and the button. */
export function gpuPanelActions(gpu: GpuParts): { readonly gpu: () => GpuView; readonly onGpuChange: (listener: () => void) => () => void; readonly enableGpu: (index: number) => void } {
  return { gpu: () => gpu.host.view(), onGpuChange: (listener) => gpu.host.onChange(listener), enableGpu: (index) => gpu.enable(index) };
}

/** The command (the palette's duplicate of the button) and the settings listener. */
export function gpuRegistrations(gpu: GpuParts): vscode.Disposable[] {
  return [
    vscode.commands.registerCommand('wslCare.enableGpu', () => gpu.enable()),
    vscode.workspace.onDidChangeConfiguration((event) => { if (event.affectsConfiguration('wslCare.gpu')) { gpu.host.settingsChanged(); } }),
  ];
}

/** The palette's choice: the only disabled adapter, or a QuickPick among several (the first in Test mode). */
async function pick(host: GpuHost, testMode: boolean): Promise<number | undefined> {
  await host.refresh();
  const choices = enableChoices(host.current());
  if (choices.length === 0) {
    void vscode.window.showInformationMessage(noChoiceMessage(host.current()));
    return undefined;
  }

  return pickedAtOnce(choices, testMode) ? choices[0]?.index : quickPick(choices);
}

/** One disabled adapter needs no pick; in Test mode the first is taken (no QuickPick can be answered there). */
function pickedAtOnce(choices: readonly EnableChoice[], testMode: boolean): boolean {
  return choices.length === 1 || testMode;
}

async function quickPick(choices: readonly EnableChoice[]): Promise<number | undefined> {
  const picked = await vscode.window.showQuickPick(choices.map((c) => ({ label: `Enable ${c.label}`, index: c.index })), { placeHolder: 'Which disabled GPU should be enabled?' });

  return picked?.index;
}
