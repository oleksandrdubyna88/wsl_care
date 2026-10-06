import * as vscode from 'vscode';

import type { OutcomeStore } from '../state/outcomeStore';
import { barView, type BarLevel, type BarView } from './statusBarModel';

/**
 * The status-bar item: thin wiring of `barView` (the pure model, `statusBarModel.ts`) to VS Code. Colours come from the
 * theme — `statusBarItem.warningBackground` / `statusBarItem.errorBackground`, the two backgrounds VS Code lets an
 * item set — so they follow the user's theme. A click opens the panel.
 */

const BACKGROUND: Readonly<Record<BarLevel, string | undefined>> = {
  none: undefined,
  warn: 'statusBarItem.warningBackground',
  critical: 'statusBarItem.errorBackground',
};

export class StatusBar implements vscode.Disposable {
  private readonly item: vscode.StatusBarItem;
  private readonly unsubscribe: () => void;
  private current: BarView = barView(undefined);

  constructor(store: OutcomeStore, openCommand: string) {
    this.item = vscode.window.createStatusBarItem('wslCare.status', vscode.StatusBarAlignment.Left, 50);
    this.item.name = 'AI OS Care';
    this.item.command = openCommand;
    this.apply(barView(store.snapshot().status));
    this.unsubscribe = store.onChange((snapshot) => this.apply(barView(snapshot.status)));
    this.item.show();
  }

  /** What the item shows now — read by the extension-host scenarios. */
  view(): BarView {
    return this.current;
  }

  dispose(): void {
    this.unsubscribe();
    this.item.dispose();
  }

  private apply(view: BarView): void {
    this.current = view;
    this.item.text = view.text;
    this.item.tooltip = view.tooltip;
    const background = BACKGROUND[view.level];
    this.item.backgroundColor = background === undefined ? undefined : new vscode.ThemeColor(background);
  }
}
