import * as vscode from 'vscode';

import { newNonce, panelOptions, panelShell } from '../panel/panelHtml';
import { LogsController, type LogsControllerOptions } from './logsController';
import { PanelSlot } from './panelSlot';
import type { Period } from './period';

/**
 * The Logs page (plan §7.4) as VS Code shows it: ONE `WebviewPanel` in the editor area, opened by *Logs* in the panel's
 * title bar (`wslCare.openLogs`) and by *Logs* beside *Last cleanup* (that run). Thin wiring over `LogsController`:
 *
 * - the page is the panel's STATIC shell (`panel/panelHtml.ts`, page `logs`): a fresh nonce per render, the same
 *   nonce-only CSP, scripts on, command URIs OFF, resources only from `media/` (§7.2's rules, one implementation) — and
 *   the panel's ONE stylesheet, `media/panel.css`;
 * - what the page sends goes to the controller's closed set; what the controller builds is posted back as `{type: 'view'}`;
 * - exactly ONE panel receives the views (`panelSlot.ts`, review K3): it is created or revealed synchronously, before any
 *   await, and a panel VS Code restores while one is open is disposed;
 * - a page open when the window reloads is restored by `registerWebviewPanelSerializer` (the manifest's
 *   `onWebviewPanel:wslCare.logs`): it reopens on the PERSISTED period — the controller's, not the webview's state;
 * - a new `status` (the store's) re-posts the view (review C3): *This run* follows what the daemon has answered.
 */

export class LogsPanel implements vscode.Disposable {
  static readonly viewType = 'wslCare.logs';

  readonly controller: LogsController;
  private readonly slot = new PanelSlot<vscode.WebviewPanel>();

  constructor(private readonly extensionUri: vscode.Uri, options: Omit<LogsControllerOptions, 'post'>, private readonly log: (line: string) => void) {
    this.controller = new LogsController({ ...options, post: (view) => { void this.slot.current()?.webview.postMessage({ type: 'view', view }); } });
  }

  /** Opens (or reveals) the page — on `period` when given, else on the persisted one. */
  async show(period?: Period): Promise<void> {
    const media = vscode.Uri.joinPath(this.extensionUri, 'media');
    const { panel, created } = this.slot.open(() => vscode.window.createWebviewPanel(LogsPanel.viewType, 'WSL Care — Logs', vscode.ViewColumn.Active, panelOptions(media)));
    if (!created) {
      await this.controller.open(period);
      return;
    }
    this.wire(panel);
    if (period !== undefined) {
      // The selection is made before its persist awaits; the new page's `ready` reads it.
      await this.controller.choose(period);
    }
  }

  /** The serializer's restore after a reload — wired when no panel is open, disposed when one is. */
  restore(panel: vscode.WebviewPanel): void {
    if (this.slot.adopt(panel)) {
      this.wire(panel);
    }
  }

  /** The store's newest `status` changed: the open page is re-posted. */
  statusChanged(): void {
    if (this.slot.current() !== undefined) {
      this.controller.statusChanged();
    }
  }

  isOpen(): boolean {
    return this.slot.current() !== undefined;
  }

  dispose(): void {
    this.slot.dispose();
  }

  private wire(panel: vscode.WebviewPanel): void {
    const media = vscode.Uri.joinPath(this.extensionUri, 'media');
    panel.webview.options = panelOptions(media);
    panel.webview.html = panelShell({
      nonce: newNonce(),
      scriptUri: panel.webview.asWebviewUri(vscode.Uri.joinPath(media, 'logs.js')).toString(),
      styleUri: panel.webview.asWebviewUri(vscode.Uri.joinPath(media, 'panel.css')).toString(),
      page: 'logs',
    });
    // The detached edge (as cleanupHost's): a fault is written to the extension's log, never an unhandled rejection.
    panel.webview.onDidReceiveMessage((raw: unknown) => { this.controller.receive(raw).catch((error: unknown) => this.log(`logs page: ${String(error)}`)); });
  }
}
