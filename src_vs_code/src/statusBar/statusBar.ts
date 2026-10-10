import * as vscode from 'vscode';

import type { OutcomeStore } from '../state/outcomeStore';
import type { GpuBarMark } from '../gpu/gpuState';
import { barView, withGpuMark, type BarLevel, type BarView } from './statusBarModel';

/**
 * The status-bar item: thin wiring of `barView` (the pure model, `statusBarModel.ts`) to VS Code. Colours come from the
 * theme — `statusBarItem.warningBackground` / `statusBarItem.errorBackground`, the two backgrounds VS Code lets an
 * item set — so they follow the user's theme. A click opens the panel. Since PLAN_gpu_health.md the GPU host's mark is laid
 * over it (`withGpuMark`), so a GPU that dropped out shows with the panel closed.
 */

/** Where the bar reads the GPU mark from (the GPU host), and how it hears that it changed. */
export interface GpuMarkSource {
  readonly mark: () => GpuBarMark;
  readonly onChange: (listener: () => void) => () => void;
}

const NO_SOURCE: GpuMarkSource = { mark: () => ({ text: '', level: 'none', lines: [] }), onChange: () => () => undefined };

const BACKGROUND: Readonly<Record<BarLevel, string | undefined>> = {
  none: undefined,
  warn: 'statusBarItem.warningBackground',
  critical: 'statusBarItem.errorBackground',
};

export class StatusBar implements vscode.Disposable {
  private readonly item: vscode.StatusBarItem;
  private readonly unsubscribe: readonly (() => void)[];
  private current: BarView = barView(undefined);

  constructor(private readonly store: OutcomeStore, openCommand: string, private readonly gpu: GpuMarkSource = NO_SOURCE) {
    this.item = vscode.window.createStatusBarItem('wslCare.status', vscode.StatusBarAlignment.Left, 50);
    this.item.name = 'AI OS Care';
    this.item.command = openCommand;
    this.apply(this.compose());
    this.unsubscribe = [store.onChange(() => this.apply(this.compose())), gpu.onChange(() => this.apply(this.compose()))];
    this.item.show();
  }

  /** What the item shows now — read by the extension-host scenarios. */
  view(): BarView {
    return this.current;
  }

  dispose(): void {
    this.unsubscribe.forEach((unsubscribe) => unsubscribe());
    this.item.dispose();
  }

  private compose(): BarView {
    return withGpuMark(barView(this.store.snapshot().status), this.gpu.mark());
  }

  private apply(view: BarView): void {
    this.current = view;
    this.item.text = view.text;
    this.item.tooltip = view.tooltip;
    const background = BACKGROUND[view.level];
    this.item.backgroundColor = background === undefined ? undefined : new vscode.ThemeColor(background);
  }
}
