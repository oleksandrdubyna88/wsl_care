import * as vscode from 'vscode';

import { buildVersion } from './buildStamp';
import { WslCareClient } from './client/WslCareClient';
import { installDaemon } from './install/installDaemon';
import { installUiFor, newInstallRecorder, type InstallRecorder } from './install/installUi';
import { PanelProvider } from './panel/panelProvider';
import { Poller, type Timers } from './poll/poller';
import { chooseRunner, runnerFor, type RunnerChoice } from './process/runnerSelection';
import { OutcomeStore } from './state/outcomeStore';
import { StatusBar } from './statusBar/statusBar';
import { clientRunner, type WslCareTestApi } from './testApi';

/**
 * WSL Care — the read-only extension over the `wsl-care` daemon (plan §7, E5). It runs on the Windows side
 * (`extensionKind: ["ui"]`) and reaches the daemon only through `WslCareClient`, which starts the absolute
 * `%SystemRoot%\System32\wsl.exe` with one of four read-only verbs; no root call path exists in it (§15f #5).
 *
 * E5.S2 hangs on it: the status bar (`statusBar/`), the read-only panel (`panel/`), both reading ONE store of the newest
 * outcomes (`state/outcomeStore.ts`), and the poller (`poll/poller.ts`) that decides when the daemon is asked — the
 * focused window only, `status` only, never a `-d` call to a stopped distribution. Activation asks `status` once when
 * the window is focused (it is no longer "starts no process": the bar needs a first answer).
 *
 * E5.S3 adds *Install daemon* (`install/`): a command and a panel button that validate the distribution, show the pinned
 * command in a modal and TYPE it into a terminal in that distribution — never run it.
 */

const OPEN_PANEL = 'wslCare.openPanel';

const REAL_TIMERS: Timers = {
  every: (ms, run) => {
    const handle = setInterval(run, ms);
    return () => clearInterval(handle);
  },
};

function settings(): vscode.WorkspaceConfiguration {
  // Application-scoped (package.json): a workspace's .vscode/settings.json cannot steer either setting.
  return vscode.workspace.getConfiguration('wslCare');
}

interface Parts {
  readonly testMode: boolean;
  readonly client: WslCareClient;
  readonly install: InstallRecorder;
  readonly choice: RunnerChoice;
  readonly calls: string[];
  readonly store: OutcomeStore;
  readonly poller: Poller;
  readonly focus: { override: boolean | undefined };
}

function build(context: vscode.ExtensionContext): Parts {
  const testMode = context.extensionMode === vscode.ExtensionMode.Test;
  const choice = chooseRunner(testMode, process.env);
  const calls: string[] = [];
  const client = new WslCareClient({
    runner: clientRunner(testMode, runnerFor(choice), calls),
    platform: process.platform,
    env: process.env,
    distroSetting: () => settings().get<string>('distro', ''),
  });
  const store = new OutcomeStore();
  const focus: { override: boolean | undefined } = { override: undefined };
  const poller = new Poller({
    run: (verb, options) => client.run(verb, options),
    store,
    focused: () => focus.override ?? vscode.window.state.focused,
    refreshSeconds: () => settings().get<unknown>('refreshSeconds'),
    timers: REAL_TIMERS,
  });

  return { testMode, client, install: newInstallRecorder(), choice, calls, store, poller, focus };
}

/** *Install daemon*: the client resolves the distribution, the modal and the terminal are real — or recorded in Test mode. */
function installer(parts: Parts): () => void {
  const ui = installUiFor(parts.testMode, parts.install);

  return () => { void installDaemon({ target: () => parts.client.terminalTarget(), ...ui }); };
}

function wire(context: vscode.ExtensionContext, parts: Parts): { bar: StatusBar; panel: PanelProvider } {
  const { poller, store, focus } = parts;
  const install = installer(parts);
  const bar = new StatusBar(store, OPEN_PANEL);
  const panel = new PanelProvider(context.extensionUri, store, {
    refresh: (options) => poller.refreshPanel(options),
    openSettings: () => { void vscode.commands.executeCommand('workbench.action.openSettings', 'wslCare'); },
    installDaemon: install,
  });
  context.subscriptions.push(
    bar,
    panel,
    { dispose: () => poller.dispose() },
    vscode.window.registerWebviewViewProvider(PanelProvider.viewId, panel),
    vscode.commands.registerCommand(OPEN_PANEL, () => vscode.commands.executeCommand(`${PanelProvider.viewId}.focus`)),
    vscode.commands.registerCommand('wslCare.refresh', () => poller.refreshPanel()),
    vscode.commands.registerCommand('wslCare.startWsl', () => poller.refreshPanel({ startIfStopped: true })),
    vscode.commands.registerCommand('wslCare.installDaemon', install),
    vscode.window.onDidChangeWindowState((state) => { if (focus.override === undefined) { poller.focusChanged(state.focused); } }),
    vscode.workspace.onDidChangeConfiguration((event) => configurationChanged(event, parts, panel)),
  );

  return { bar, panel };
}

function configurationChanged(event: vscode.ConfigurationChangeEvent, parts: Parts, panel: PanelProvider): void {
  if (event.affectsConfiguration('wslCare.refreshSeconds')) {
    parts.poller.settingsChanged();
  }
  if (event.affectsConfiguration('wslCare.distro')) {
    void (panel.isVisible() ? parts.poller.refreshPanel() : parts.poller.tick());
  }
}

function testApi(parts: Parts, bar: StatusBar, panel: PanelProvider): WslCareTestApi {
  const { poller, calls, focus } = parts;

  return {
    calls: () => [...calls],
    resetCalls: () => { calls.length = 0; },
    runnerKind: () => parts.choice.kind,
    statusBar: () => bar.view(),
    setFocused: (focused) => { focus.override = focused; poller.focusChanged(focused); },
    tick: () => poller.tick(),
    refreshPanel: () => poller.refreshPanel(),
    startWsl: () => poller.refreshPanel({ startIfStopped: true }),
    settled: () => poller.settled(),
    lastRendered: () => panel.lastRendered(),
    install: () => parts.install,
    buildVersion,
  };
}

export function activate(context: vscode.ExtensionContext): WslCareTestApi | undefined {
  const parts = build(context);
  const { bar, panel } = wire(context, parts);
  parts.poller.start();

  return context.extensionMode === vscode.ExtensionMode.Test ? testApi(parts, bar, panel) : undefined;
}

export function deactivate(): void {
  // Everything is disposed through context.subscriptions.
}
