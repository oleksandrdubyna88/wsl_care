import type { WindowsDaemonView } from '../bundled/bundledView';
import * as vscode from 'vscode';

import type { RunOptions } from '../client/WslCareClient';
import type { OutcomeStore } from '../state/outcomeStore';
import { parsePageMessage, type PageMessage } from './messages';
import { newNonce, panelOptions, panelShell } from './panelHtml';
import type { ArchiveControls, CleanupControls, GpuView, GuardView, WindowsTasksView } from './view';
import type { RelaysControls } from '../relays/relaysView';
import { buildPanelView } from './viewModel';
import type { RowId } from '../cleanup/rowIds';

/**
 * The panel, a `WebviewView` in the AI OS Care side bar (plan §7.2). Thin wiring: the page is the static shell
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
  /** PLAN_windows_time_guard.md D7: *Start Windows Time* — the host's modal, then ONE elevated PowerShell. */
  readonly startWindowsTime: () => void;
  /** E6.S3: a row's *Clean* (`selected` false) or *Clean selected* — the host's transaction from here on. */
  readonly clean: (rowIds: readonly RowId[], selected: boolean) => void;
  readonly runFullCheck: () => void;
  /** The index into the stoppable runs the host read itself — never a run id from the page. */
  readonly stop: (index: number) => void;
  /** The cleanup controls, derived by the host from the daemon's state and its journal. */
  readonly cleanup: () => CleanupControls;
  /** Called whenever the cleanup state changes (the journal, a result, the optimistic flag); returns an unsubscribe. */
  readonly onCleanupChange: (listener: () => void) => () => void;
  /** E6.S4: *Logs* beside *Last cleanup* — the Logs page on the run `status.lastCleanup` names. */
  readonly openRunLogs: () => void;
  /** PLAN_windows_time_task.md: the guard's line (Task Scheduler's answer + the pending run), and its two flows. */
  readonly guard: () => GuardView;
  readonly onGuardChange: (listener: () => void) => () => void;
  readonly installWindowsTimeGuard: () => void;
  readonly removeWindowsTimeGuard: () => void;
  /** E10.S1: the archive's controls (the daemon's archive reads + the flow in flight), and its two flows. */
  readonly archive: () => ArchiveControls;
  readonly onArchiveChange: (listener: () => void) => () => void;
  readonly chooseArchiveFolder: () => void;
  readonly stopArchiving: () => void;
  /** E10.S1b: *Archive now* — A13 through the cleanup host's transaction. */
  readonly archiveNow: () => void;
  /** PLAN_gpu_health.md D6: the GPU alerts (Windows' answer + the pending enable), and *Enable <GPU>* by the adapter's index in the READ the button came from. */
  readonly gpu: () => GpuView;
  readonly onGpuChange: (listener: () => void) => () => void;
  readonly enableGpu: (index: number, readAt: number) => void;
  /** E10.S2a: *Open archive…* — the Archive page, which reads `archive list` itself. */
  readonly openArchive: () => void;
  /** S7b.2b: the client-gone relays' line and button (the cleanup host's; its changes arrive with the cleanup's). */
  readonly relays: () => RelaysControls;
  /** S7b.2b: *Stop client-gone relays* — A21 through the cleanup host's transaction. */
  readonly stopRelays: () => void;
  /** E7.S5a: the bundled Windows daemon's row — read on the panel's refresh, never at activation. */
  readonly windowsDaemon: () => WindowsDaemonView;
  readonly onWindowsDaemonChange: (listener: () => void) => () => void;
  /** WR-E2: the Windows tasks' lines — the shared Task Scheduler read and the staged copy's probe (`windowsTasks/tasksHost.ts`). */
  readonly windowsTasks: () => WindowsTasksView;
  readonly onWindowsTasksChange: (listener: () => void) => () => void;
}

export class PanelProvider implements vscode.WebviewViewProvider, vscode.Disposable {
  static readonly viewId = 'wslCare.panel';

  private view: vscode.WebviewView | undefined;
  private rendered: number | undefined;
  private readonly unsubscribe: readonly (() => void)[];

  constructor(private readonly extensionUri: vscode.Uri, private readonly store: OutcomeStore, private readonly actions: PanelActions) {
    this.unsubscribe = [store.onChange(() => this.post()), actions.onCleanupChange(() => this.post()), actions.onGuardChange(() => this.post()), actions.onArchiveChange(() => this.post()), actions.onGpuChange(() => this.post()), actions.onWindowsDaemonChange(() => this.post()), actions.onWindowsTasksChange(() => this.post())];
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
    this.unsubscribe.forEach((unsubscribe) => unsubscribe());
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
      startWindowsTime: () => this.actions.startWindowsTime(),
      installWindowsTimeGuard: () => this.actions.installWindowsTimeGuard(),
      removeWindowsTimeGuard: () => this.actions.removeWindowsTimeGuard(),
      chooseArchiveFolder: () => this.actions.chooseArchiveFolder(),
      stopArchiving: () => this.actions.stopArchiving(),
      archiveNow: () => this.actions.archiveNow(),
      openArchive: () => this.actions.openArchive(),
      stopRelays: () => this.actions.stopRelays(),
      clean: (m) => this.actions.clean(m.rowIds, false),
      cleanSelected: (m) => this.actions.clean(m.rowIds, true),
      runFullCheck: () => this.actions.runFullCheck(),
      stop: (m) => this.actions.stop(m.index),
      openRunLogs: () => this.actions.openRunLogs(),
      enableGpu: (m) => this.actions.enableGpu(m.index, m.readAt),
    };
    if (message !== undefined) {
      (handlers[message.type] as (m: PageMessage) => void)(message);
    }
  }

  private post(): void {
    void this.view?.webview.postMessage({ type: 'view', view: buildPanelView(this.store.snapshot(), this.actions.cleanup(), this.actions.guard(), this.actions.archive(), this.actions.gpu(), this.actions.relays(), this.actions.windowsDaemon(), this.actions.windowsTasks()) });
  }
}
