import * as fs from 'node:fs';
import * as vscode from 'vscode';

import type { Numbers } from '../settings/numbers';
import { runnerFor } from '../process/runnerSelection';
import { enableChoices, noChoiceMessage, type EnableChoice } from './gpuCommand';
import { GpuHost } from './gpuHost';
import type { FileInfo } from './gpuScripts';
import type { GpuState, GpuView } from './gpuState';
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
  /** The command and the panel button: the adapter at `index` of the read `readAt` (the button), or a pick among the disabled ones (the palette). */
  readonly enable: (index?: number, readAt?: number) => void;
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
    fileInfo: fileInfoOf,
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
  const run = (chosen: Chosen): void => { void host.enable(chosen.index, chosen.readAt).catch((e: unknown) => { log(`Enable GPU failed: ${String(e)}`); }); };
  const enable = (index?: number, readAt?: number): void => {
    if (index !== undefined && readAt !== undefined) {
      run({ index, readAt });
      return;
    }
    void pick(host, testMode).then((chosen) => { if (chosen !== undefined) { run(chosen); } });
  };
  context.subscriptions.push({ dispose: () => host.dispose() });

  return { host, recorder, enable };
}

/** Node's `lstat` of a program the enable might elevate — `undefined` when it cannot be read (absent; EACCES on an app-execution alias). */
function fileInfoOf(file: string): FileInfo | undefined {
  try {
    const info = fs.lstatSync(file);

    return { bytes: info.size, symlink: info.isSymbolicLink() };
  } catch {
    return undefined;
  }
}

/** The panel's three GPU members (`PanelActions`): the view, its change, and the button (its index and the read it came from). */
export function gpuPanelActions(gpu: GpuParts): { readonly gpu: () => GpuView; readonly onGpuChange: (listener: () => void) => () => void; readonly enableGpu: (index: number, readAt: number) => void } {
  return { gpu: () => gpu.host.view(), onGpuChange: (listener) => gpu.host.onChange(listener), enableGpu: (index, readAt) => gpu.enable(index, readAt) };
}

/** The command (the palette's duplicate of the button) and the settings listener. */
export function gpuRegistrations(gpu: GpuParts): vscode.Disposable[] {
  return [
    vscode.commands.registerCommand('wslCare.enableGpu', () => gpu.enable()),
    vscode.workspace.onDidChangeConfiguration((event) => { if (event.affectsConfiguration('wslCare.gpu')) { gpu.host.settingsChanged(); } }),
  ];
}

/** The adapter the palette chose: its index in the host's read, and that read's time stamp (what the button would post). */
interface Chosen {
  readonly index: number;
  readonly readAt: number;
}

/** The time stamp of a read — 0 for a state that is not one (no choice is offered from it, so it is never compared). */
function readAtOf(state: GpuState): number {
  return state.kind === 'read' ? state.atUtcMs : 0;
}

/**
 * The palette's choice: the only disabled adapter, or a QuickPick among several (the first in Test mode). The read before it
 * is SHOWN while it runs — it may take the query ceiling, up to two minutes on a slow PowerShell (code round 2).
 */
async function pick(host: GpuHost, testMode: boolean): Promise<Chosen | undefined> {
  await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: 'Reading Windows\' display adapters…' }, () => host.refresh());
  const state = host.current();
  const choices = enableChoices(state);
  if (choices.length === 0) {
    void vscode.window.showInformationMessage(noChoiceMessage(state));
    return undefined;
  }
  const index = await chosenIndex(choices, testMode);

  return index === undefined ? undefined : { index, readAt: readAtOf(state) };
}

/** One disabled adapter needs no pick; in Test mode the first is taken (no QuickPick can be answered there). */
function pickedAtOnce(choices: readonly EnableChoice[], testMode: boolean): boolean {
  return choices.length === 1 || testMode;
}

function chosenIndex(choices: readonly EnableChoice[], testMode: boolean): Promise<number | undefined> {
  return pickedAtOnce(choices, testMode) ? Promise.resolve(choices[0]?.index) : quickPick(choices);
}

async function quickPick(choices: readonly EnableChoice[]): Promise<number | undefined> {
  const picked = await vscode.window.showQuickPick(choices.map((c) => ({ label: `Enable ${c.label}`, index: c.index })), { placeHolder: 'Which disabled GPU should be enabled?' });

  return picked?.index;
}
