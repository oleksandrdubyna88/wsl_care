import * as vscode from 'vscode';

import { buildVersion } from './buildStamp';
import { WslCareClient } from './client/WslCareClient';
import { installDaemon } from './install/installDaemon';
import { installUiFor, newInstallRecorder, type InstallRecorder } from './install/installUi';
import { performance } from 'node:perf_hooks';

import { CleanupHost } from './cleanup/cleanupHost';
import { newCleanRecorder, type CleanRecorder } from './cleanup/cleanRecorder';
import { cleanUiFor } from './cleanup/cleanUi';
import { lastCleanupPeriod } from './logsPage/logsController';
import { LogsPanel } from './logsPage/logsPanel';
import { PanelProvider } from './panel/panelProvider';
import { Poller, type Timers } from './poll/poller';
import { chooseRunner, runnerFor, type RunnerChoice } from './process/runnerSelection';
import { CleanupController } from './root/cleanupController';
import { OutcomeStore } from './state/outcomeStore';
import { StatusBar } from './statusBar/statusBar';
import { clientRunner, type WslCareTestApi } from './testApi';
import { distroSettingText } from './wsl/distros';

/**
 * AI OS Care — the extension over the `wsl-care` daemon (plan §7, E5–E6). It runs on the Windows side
 * (`extensionKind: ["ui"]`) and reaches the daemon only through `WslCareClient`, which starts the absolute
 * `%SystemRoot%\System32\wsl.exe` with one of four read-only verbs (and, since E6.S3, the two run reads) — and, since E6.S2,
 * through the cleanup controller (`root/cleanupController.ts`), the ONE host-side holder of the root boundary
 * (`root/rootCall.ts`: a closed union of five root calls). E6.S3 hangs the buttons on it (`cleanup/cleanupHost.ts`): the
 * panel's closed messages reach the host transaction (`cleanup/cleanFlow.ts`), whose runs are written to `globalState`
 * and followed by the durable poll (`cleanup/runFollower.ts`) to their end — across a reload.
 *
 * E5.S2 hangs on it: the status bar (`statusBar/`), the read-only panel (`panel/`), both reading ONE store of the newest
 * outcomes (`state/outcomeStore.ts`), and the poller (`poll/poller.ts`) that decides when the daemon is asked — the
 * focused window only, `status` only, never a `-d` call to a stopped distribution. Activation asks `status` once when
 * the window is focused (it is no longer "starts no process": the bar needs a first answer).
 *
 * E5.S3 adds *Install daemon* (`install/`): a command and a panel button that validate the distribution, show the pinned
 * command in a modal and TYPE it into a terminal in that distribution — never run it.
 *
 * E6.S4 adds the Logs page (`logsPage/`): a `WebviewPanel` opened by *Logs* in the panel title (`wslCare.openLogs`) and
 * beside *Last cleanup* (that run), restored after a reload by its serializer; its period persisted in `globalState`, its
 * reads (`logs` / `runs` / `runs show`) the client's unprivileged run reads.
 */

const OPEN_PANEL = 'wslCare.openPanel';

const REAL_TIMERS: Timers = {
  every: (ms, run) => {
    const handle = setInterval(run, ms);
    return () => clearInterval(handle);
  },
};

const ONE_SHOT = {
  after: (ms: number, run: () => void): (() => void) => {
    const handle = setTimeout(run, ms);
    return () => clearTimeout(handle);
  },
};

function settings(): vscode.WorkspaceConfiguration {
  // Application-scoped (package.json): a workspace's .vscode/settings.json cannot steer either setting.
  return vscode.workspace.getConfiguration('wslCare');
}

/** `wslCare.distro` as settings.json holds it (a hand-edited value may be any JSON type) — read by the client for every
 * call and, as `distroSettingText`, by the poller to stamp each round with its target. */
function distroSetting(): unknown {
  return settings().get<unknown>('distro', '');
}

interface Parts {
  readonly testMode: boolean;
  readonly client: WslCareClient;
  readonly cleanup: CleanupController;
  readonly install: InstallRecorder;
  readonly cleanRecorder: CleanRecorder;
  readonly host: CleanupHost;
  readonly choice: RunnerChoice;
  readonly calls: string[];
  readonly store: OutcomeStore;
  readonly poller: Poller;
  readonly focus: { override: boolean | undefined };
  readonly log: vscode.LogOutputChannel;
}

function build(context: vscode.ExtensionContext): Parts {
  const testMode = context.extensionMode === vscode.ExtensionMode.Test;
  const choice = chooseRunner(testMode, process.env);
  const calls: string[] = [];
  // ONE runner for the read-only client and the root controller: the same seam, the same Test-mode fake or closed runner.
  const runner = clientRunner(testMode, runnerFor(choice), calls);
  const client = new WslCareClient({
    runner,
    platform: process.platform,
    env: process.env,
    distroSetting,
  });
  const cleanup = new CleanupController({ client, runner, now: () => performance.now(), sleep: (ms) => new Promise((resolve) => { setTimeout(resolve, ms); }) });
  const store = new OutcomeStore();
  const focus: { override: boolean | undefined } = { override: undefined };
  const poller = new Poller({
    run: (verb, options) => client.run(verb, options),
    store,
    focused: () => focus.override ?? vscode.window.state.focused,
    refreshSeconds: () => settings().get<unknown>('refreshSeconds'),
    timers: REAL_TIMERS,
    target: () => distroSettingText(distroSetting()),
  });

  const cleanRecorder = newCleanRecorder();
  // The extension's log (review C11): a fault at a button's detached edge is written here as well as told.
  const log = vscode.window.createOutputChannel('WSL Care', { log: true });
  context.subscriptions.push(log);
  const host = new CleanupHost({
    durable: context.globalState, controller: cleanup, read: (request) => client.read(request), outcomes: store,
    askStatus: () => poller.askStatus(), refreshPanel: () => poller.refreshPanel(), focused: () => focus.override ?? vscode.window.state.focused,
    ui: cleanUiFor(testMode, cleanRecorder), timers: ONE_SHOT, now: () => performance.now(), wallNow: () => Date.now(), log: (line) => log.error(line),
  });

  return { testMode, client, cleanup, install: newInstallRecorder(), cleanRecorder, host, choice, calls, store, poller, focus, log };
}

/** *Install daemon*: the client resolves the distribution, the modal and the terminal are real — or recorded in Test mode. */
function installer(parts: Parts): () => void {
  const ui = installUiFor(parts.testMode, parts.install);

  return () => { void installDaemon({ target: () => parts.client.terminalTarget(), ...ui }); };
}

function logsPanel(context: vscode.ExtensionContext, parts: Parts): LogsPanel {
  const { client, store, log } = parts;

  return new LogsPanel(context.extensionUri, {
    durable: context.globalState, read: (request) => client.read(request), status: () => store.snapshot().status, wallNow: () => Date.now(),
  }, (line) => log.error(line));
}

function wire(context: vscode.ExtensionContext, parts: Parts): { bar: StatusBar; panel: PanelProvider; logs: LogsPanel } {
  const { poller, store, focus, host } = parts;
  const install = installer(parts);
  const logs = logsPanel(context, parts);
  const bar = new StatusBar(store, OPEN_PANEL);
  const panel = new PanelProvider(context.extensionUri, store, {
    refresh: (options) => poller.refreshPanel(options),
    openSettings: () => { void vscode.commands.executeCommand('workbench.action.openSettings', 'wslCare'); },
    installDaemon: install,
    clean: (rowIds, selected) => { void host.clean(rowIds, selected); },
    runFullCheck: () => { void host.runFullCheck(); },
    stop: (index) => { void host.stop(index); },
    cleanup: () => host.controls(),
    onCleanupChange: (listener) => host.onChange(listener),
    openRunLogs: () => { void logs.show(lastCleanupPeriod(store.snapshot().status)); },
  });
  context.subscriptions.push(
    bar,
    panel,
    { dispose: () => poller.dispose() },
    { dispose: () => host.dispose() },
    vscode.window.registerWebviewViewProvider(PanelProvider.viewId, panel),
    vscode.commands.registerCommand(OPEN_PANEL, () => vscode.commands.executeCommand(`${PanelProvider.viewId}.focus`)),
    vscode.commands.registerCommand('wslCare.refresh', () => poller.refreshPanel()),
    vscode.commands.registerCommand('wslCare.startWsl', () => poller.refreshPanel({ startIfStopped: true })),
    vscode.commands.registerCommand('wslCare.installDaemon', install),
    logs,
    vscode.commands.registerCommand('wslCare.openLogs', () => logs.show()),
    vscode.window.registerWebviewPanelSerializer(LogsPanel.viewType, { deserializeWebviewPanel: (restored) => { logs.restore(restored); return Promise.resolve(); } }),
    vscode.window.onDidChangeWindowState((state) => { if (focus.override === undefined) { poller.focusChanged(state.focused); host.start(); } }),
    vscode.workspace.onDidChangeConfiguration((event) => configurationChanged(event, parts, panel)),
  );

  return { bar, panel, logs };
}

function configurationChanged(event: vscode.ConfigurationChangeEvent, parts: Parts, panel: PanelProvider): void {
  if (event.affectsConfiguration('wslCare.refreshSeconds')) {
    parts.poller.settingsChanged();
  }
  if (event.affectsConfiguration('wslCare.distro')) {
    void (panel.isVisible() ? parts.poller.refreshPanel() : parts.poller.tick());
  }
}

function testApi(parts: Parts, bar: StatusBar, panel: PanelProvider, logs: LogsPanel): WslCareTestApi {
  const { poller, calls, focus } = parts;

  return {
    calls: () => [...calls],
    resetCalls: () => { calls.length = 0; },
    runnerKind: () => parts.choice.kind,
    statusBar: () => bar.view(),
    setFocused: (focused) => { focus.override = focused; poller.focusChanged(focused); parts.host.start(); },
    tick: () => poller.tick(),
    refreshPanel: () => poller.refreshPanel(),
    startWsl: () => poller.refreshPanel({ startIfStopped: true }),
    settled: () => poller.settled(),
    lastRendered: () => panel.lastRendered(),
    install: () => parts.install,
    cleanup: () => parts.cleanup,
    cleanupHost: () => parts.host,
    cleanRecorder: () => parts.cleanRecorder,
    logs: () => logs,
    buildVersion,
  };
}

export function activate(context: vscode.ExtensionContext): WslCareTestApi | undefined {
  const parts = build(context);
  const { bar, panel, logs } = wire(context, parts);
  parts.poller.start();
  parts.host.start();

  return context.extensionMode === vscode.ExtensionMode.Test ? testApi(parts, bar, panel, logs) : undefined;
}

export function deactivate(): void {
  // Everything is disposed through context.subscriptions.
}
