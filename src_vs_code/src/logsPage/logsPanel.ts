import * as vscode from 'vscode';

import { newNonce, panelOptions, panelShell } from '../panel/panelHtml';
import { LogsController, type LogsControllerOptions } from './logsController';
import type { Period } from './period';

/**
 * The Logs page (plan §7.4) as VS Code shows it: ONE `WebviewPanel` in the editor area, opened by *Logs* in the panel's
 * title bar (`wslCare.openLogs`) and by *Logs* beside *Last cleanup* (that run). Thin wiring over `LogsController`:
 *
 * - the page is the panel's STATIC shell (`panel/panelHtml.ts`, page `logs`): a fresh nonce per render, the same
 *   nonce-only CSP, scripts on, command URIs OFF, resources only from `media/` (§7.2's rules, one implementation) — and
 *   the panel's ONE stylesheet, `media/panel.css`;
 * - what the page sends goes to the controller's closed set; what the controller builds is posted back as `{type: 'view'}`;
 * - a page open when the window reloads is restored by `registerWebviewPanelSerializer` (the manifest's
 *   `onWebviewPanel:wslCare.logs`): it reopens on the PERSISTED period — the controller's, not the webview's state.
 */

export class LogsPanel implements vscode.Disposable {
  static readonly viewType = 'wslCare.logs';

  readonly controller: LogsController;
  private panel: vscode.WebviewPanel | undefined;

  constructor(private readonly extensionUri: vscode.Uri, options: Omit<LogsControllerOptions, 'post'>, private readonly log: (line: string) => void) {
    this.controller = new LogsController({ ...options, post: (view) => { void this.panel?.webview.postMessage({ type: 'view', view }); } });
  }

  /** Opens (or reveals) the page — on `period` when given, else on the persisted one. */
  async show(period?: Period): Promise<void> {
    if (this.panel !== undefined) {
      this.panel.reveal();
      await this.controller.open(period);
      return;
    }
    if (period !== undefined) {
      await this.controller.choose(period);
    }
    const media = vscode.Uri.joinPath(this.extensionUri, 'media');
    this.attach(vscode.window.createWebviewPanel(LogsPanel.viewType, 'WSL Care — Logs', vscode.ViewColumn.Active, panelOptions(media)));
  }

  /** The serializer's restore after a reload: the same wiring, the persisted period (the page's `ready` reads it). */
  restore(panel: vscode.WebviewPanel): void {
    this.attach(panel);
  }

  isOpen(): boolean {
    return this.panel !== undefined;
  }

  dispose(): void {
    this.panel?.dispose();
  }

  private attach(panel: vscode.WebviewPanel): void {
    this.panel = panel;
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
    panel.onDidDispose(() => {
      if (this.panel === panel) {
        this.panel = undefined;
      }
    });
  }
}
