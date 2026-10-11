import * as vscode from 'vscode';

import { newNonce, panelOptions, panelShell } from '../panel/panelHtml';
import { PanelSlot } from '../panel/panelSlot';
import { ArchiveController, type ArchiveControllerOptions } from './archiveController';

/**
 * The Archive page (plan §15s D6, E10.S2a) as VS Code shows it: ONE `WebviewPanel` in the editor area, opened by *Open archive…*
 * in the panel's *AI agents* section and by the command `wslCare.openArchive`. Thin wiring over `ArchiveController`, in the Logs
 * page's shape (`logsPage/logsPanel.ts`):
 *
 * - the page is the panel's STATIC shell (`panel/panelHtml.ts`, page `archive`): a fresh nonce per render, the nonce-only CSP,
 *   scripts on, command URIs OFF, resources only from `media/` — and the panel's ONE stylesheet, `media/panel.css`;
 * - what the page sends goes to the controller's closed set; what the controller builds is posted back as `{type: 'view'}`;
 * - exactly ONE panel receives the views (`panel/panelSlot.ts`): created or revealed synchronously, before any await;
 * - a page open when the window reloads is restored by `registerWebviewPanelSerializer` (`onWebviewPanel:wslCare.archive`) and
 *   reads the list again on its `ready` — nothing of the page is persisted, and a CLOSED page's state is dropped (own review #2);
 * - a new `status` (the store's) moves the capability gate: a daemon that now lists is read, one that no longer does ends the list.
 */

export class ArchivePanel implements vscode.Disposable {
  static readonly viewType = 'wslCare.archive';

  readonly controller: ArchiveController;
  private readonly slot = new PanelSlot<vscode.WebviewPanel>();

  constructor(private readonly extensionUri: vscode.Uri, options: Omit<ArchiveControllerOptions, 'post'>, private readonly log: (line: string) => void) {
    this.controller = new ArchiveController({ ...options, post: (view) => { void this.slot.current()?.webview.postMessage({ type: 'view', view }); } });
  }

  /** Opens (or reveals) the page; an already open page reads the list again. */
  async show(): Promise<void> {
    const media = vscode.Uri.joinPath(this.extensionUri, 'media');
    const { panel, created } = this.slot.open(() => vscode.window.createWebviewPanel(ArchivePanel.viewType, 'AI OS Care — Archive', vscode.ViewColumn.Active, panelOptions(media)));
    if (created) {
      // The new page's `ready` reads the list.
      this.wire(panel);
      return;
    }
    await this.controller.refresh();
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
      void this.controller.statusChanged();
    }
  }

  /** The cleanup host's restore facts may have moved (E10.S2b): a restore's end reads the open page's list again. */
  restoreChanged(): void {
    this.controller.restoreChanged().catch((error: unknown) => this.log(`archive page: ${String(error)}`));
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
      scriptUri: panel.webview.asWebviewUri(vscode.Uri.joinPath(media, 'archive.js')).toString(),
      styleUri: panel.webview.asWebviewUri(vscode.Uri.joinPath(media, 'panel.css')).toString(),
      page: 'archive',
    });
    // Own review #2: a closed page's state is dropped, so the next page starts afresh (one new read, the session paths short).
    panel.onDidDispose(() => { this.controller.pageClosed(); });
    // The detached edge (as the Logs page's): a fault is written to the extension's log, never an unhandled rejection.
    panel.webview.onDidReceiveMessage((raw: unknown) => { this.controller.receive(raw).catch((error: unknown) => this.log(`archive page: ${String(error)}`)); });
  }
}
