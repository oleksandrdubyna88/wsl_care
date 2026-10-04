import * as vscode from 'vscode';

import type { RunOptions } from '../client/WslCareClient';
import type { OutcomeStore } from '../state/outcomeStore';
import { parsePageMessage, type PageMessage } from './messages';
import { newNonce, panelOptions, panelShell } from './panelHtml';
import { buildPanelView } from './viewModel';

/**
 * The read-only panel, a `WebviewView` in the WSL Care side bar (plan §7.2). Thin wiring: the page is the static shell
 * of `panelHtml.ts` with a fresh nonce per render; its data is the view model of the store's snapshot, sent by
 * `postMessage` whenever the store changes; what the page sends back is validated against the closed set of
 * `messages.ts` and mapped to the host's own actions — nothing from the page reaches the client as data.
 *
 * Opening the panel (or making it visible again) asks `status`, then `preview` and `doctor` — the only times those two
 * are asked (§15g M1).
 */

export interface PanelActions {
  readonly refresh: (options?: RunOptions) => Promise<void>;
  readonly openSettings: () => void;
  readonly installDaemon: () => void;
}

export class PanelProvider implements vscode.WebviewViewProvider, vscode.Disposable {
  static readonly viewId = 'wslCare.panel';

  private view: vscode.WebviewView | undefined;
  private rendered: number | undefined;
  private readonly unsubscribe: () => void;

  constructor(private readonly extensionUri: vscode.Uri, private readonly store: OutcomeStore, private readonly actions: PanelActions) {
    this.unsubscribe = store.onChange(() => this.post());
  }

  resolveWebviewView(view: vscode.WebviewView): void {
    this.view = view;
    const media = vscode.Uri.joinPath(this.extensionUri, 'media');
    view.webview.options = panelOptions(media);
    view.webview.html = panelShell({
      nonce: newNonce(),
      scriptUri: view.webview.asWebviewUri(vscode.Uri.joinPath(media, 'panel.js')).toString(),
      styleUri: view.webview.asWebviewUri(vscode.Uri.joinPath(media, 'panel.css')).toString(),
    });
    view.webview.onDidReceiveMessage((raw: unknown) => this.receive(parsePageMessage(raw)));
    view.onDidChangeVisibility(() => this.visibilityChanged());
    view.onDidDispose(() => { this.view = undefined; });
    void this.actions.refresh();
  }

  /** How many rows the page last reported drawing — read by the extension-host scenarios. */
  lastRendered(): number | undefined {
    return this.rendered;
  }

  isVisible(): boolean {
    return this.view?.visible === true;
  }

  dispose(): void {
    this.unsubscribe();
  }

  private visibilityChanged(): void {
    if (this.isVisible()) {
      void this.actions.refresh();
    }
  }

  private receive(message: PageMessage | undefined): void {
    const handlers: { readonly [K in PageMessage['type']]: (m: Extract<PageMessage, { type: K }>) => void } = {
      ready: () => this.post(),
      rendered: (m) => { this.rendered = m.rows; },
      refresh: () => { void this.actions.refresh(); },
      startWsl: () => { void this.actions.refresh({ startIfStopped: true }); },
      openSettings: () => this.actions.openSettings(),
      installDaemon: () => this.actions.installDaemon(),
    };
    if (message !== undefined) {
      (handlers[message.type] as (m: PageMessage) => void)(message);
    }
  }

  private post(): void {
    void this.view?.webview.postMessage({ type: 'view', view: buildPanelView(this.store.snapshot()) });
  }
}
