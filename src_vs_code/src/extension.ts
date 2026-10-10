import * as vscode from 'vscode';

import { buildVersion } from './buildStamp';
import { WslCareClient, type RunOptions } from './client/WslCareClient';
import { installDaemon } from './install/installDaemon';
import { installUiFor, newInstallRecorder, type InstallRecorder } from './install/installUi';
import { performance } from 'node:perf_hooks';

import { ArchiveHost } from './archive/archiveHost';
import { newArchiveRecorder, type ArchiveRecorder } from './archive/archiveRecorder';
import { archiveUiFor } from './archive/archiveUi';
import { CleanupHost } from './cleanup/cleanupHost';
import { newCleanRecorder, type CleanRecorder } from './cleanup/cleanRecorder';
import { cleanUiFor } from './cleanup/cleanUi';
import { lastCleanupPeriod } from './logsPage/logsController';
import { LogsPanel } from './logsPage/logsPanel';
import { PanelProvider, type PanelActions } from './panel/panelProvider';
import { Poller, type Timers } from './poll/poller';
import { chooseRunner, runnerFor, type RunnerChoice } from './process/runnerSelection';
import { CleanupController } from './root/cleanupController';
import { readNumbers, type Numbers } from './settings/numbers';
import { OutcomeStore } from './state/outcomeStore';
import { daemonLimitsOf, type DaemonLimits } from './shared/daemonLimits';
import { StatusBar } from './statusBar/statusBar';
import { clientRunner, type WslCareTestApi } from './testApi';
import { WindowsTimeGuardHost } from './windowsTime/guardHost';
import { sweepPending } from './windowsTime/guardPending';
import type { GuardOptions } from './windowsTime/guardTask';
import { guardUiFor, newGuardRecorder, type GuardRecorder } from './windowsTime/guardUi';
import { startWindowsTime } from './windowsTime/windowsTimeFix';
import { newWindowsTimeRecorder, windowsTimeUiFor, type WindowsTimeRecorder } from './windowsTime/windowsTimeUi';
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
 * E5.S2 hangs on it: the status bar (`statusBar/`), the panel (`panel/`), both reading ONE store of the newest
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

/** Every number setting (`settings/numbers.ts`), read NOW — each user reads it at each use, so a change applies at once. */
function numbers(): Numbers {
  return readNumbers((key) => settings().get<unknown>(key));
}

/** The daemon's published limits from the store's newest `status` (daemon #17) — the fallback until one answers them. */
function limitsOf(store: OutcomeStore): DaemonLimits {
  const status = store.snapshot().status;

  return daemonLimitsOf(status !== undefined && status.kind === 'answered' && status.answer.verb === 'status' ? status.answer.body : undefined);
}

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
  readonly windowsTime: WindowsTimeRecorder;
  readonly guardRecorder: GuardRecorder;
  readonly guard: WindowsTimeGuardHost;
  readonly cleanRecorder: CleanRecorder;
  readonly host: CleanupHost;
  readonly archive: ArchiveHost;
  readonly archiveRecorder: ArchiveRecorder;
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
    numbers,
    limits: () => limitsOf(store),
  });
  const cleanup = new CleanupController({ client, runner, now: () => performance.now(), sleep: (ms) => new Promise((resolve) => { setTimeout(resolve, ms); }), numbers });
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
  const log = vscode.window.createOutputChannel('AI OS Care', { log: true });
  context.subscriptions.push(log);
  const host = new CleanupHost({
    durable: context.globalState, controller: cleanup, read: (request) => client.read(request), outcomes: store,
    askStatus: () => poller.askStatus(), refreshPanel: () => poller.refreshPanel(), focused: () => focus.override ?? vscode.window.state.focused,
    ui: cleanUiFor(testMode, cleanRecorder), timers: ONE_SHOT, now: () => performance.now(), wallNow: () => Date.now(), log: (line) => log.error(line), numbers,
  });

  const guardRecorder = newGuardRecorder();
  const guard = windowsTimeGuard(context, testMode, guardRecorder);
  // E10.S1: the archive's reads and its one user-layer write go through the SAME runner and the client's target checks.
  const archiveRecorder = newArchiveRecorder();
  const archive = new ArchiveHost({
    read: (request) => client.read(request), target: () => client.rootTarget(), runner, outcomes: store, ui: archiveUiFor(testMode, archiveRecorder),
    log: (line) => log.error(line), numbers, limits: () => limitsOf(store),
    // E10.S1b: Archive now is greyed while a cleanup is in flight; the cleanup's changes re-render the archive's controls; the
    // archive host refuses on its own conditions first, then the cleanup host runs A13 through its transaction.
    cleanupFree: () => host.controls().enabled, onCleanupChange: (listener) => host.onChange(listener), archiveNow: () => host.archiveNow(),
  });

  return { testMode, client, cleanup, install: newInstallRecorder(), windowsTime: newWindowsTimeRecorder(), guardRecorder, guard, cleanRecorder, host, archive, archiveRecorder, choice, calls, store, poller, focus, log };
}

/** The Windows Time guard's settings, read NOW (PLAN_windows_time_task.md D6): the task is built from them at install. */
function guardOptions(): GuardOptions {
  const n = numbers();
  return {
    setAutomaticStart: settings().get<boolean>('windowsTime.setAutomaticStart', true) !== false,
    everyHours: n.guardEveryHours,
    minMinutesBetweenStarts: n.guardMinMinutesBetweenStarts,
    delaySeconds: n.guardDelaySeconds,
    timeLimitMinutes: n.guardTimeLimitMinutes,
  };
}

/**
 * The Windows Time guard (PLAN_windows_time_task.md): its host reads Task Scheduler through the REAL runner (never the fake
 * wsl.exe; a recorder in Test mode), and its install / remove run ONE elevated PowerShell each. A pending run past its
 * deadline is swept at activation, so nothing stays "waiting" after a crash.
 */
function windowsTimeGuard(context: vscode.ExtensionContext, testMode: boolean, recorder: GuardRecorder): WindowsTimeGuardHost {
  void sweepPending(context.globalState, Date.now());

  return new WindowsTimeGuardHost({
    env: process.env,
    options: guardOptions,
    opTimeoutMs: () => numbers().windowsTimeGuardSeconds * 1000,
    queryTimeoutMs: () => numbers().windowsTimeGuardQuerySeconds * 1000,
    ui: guardUiFor(testMode, recorder, runnerFor({ kind: 'real' }), context.subscriptions),
    durable: context.globalState,
    nowUtcMs: () => Date.now(),
    formatInstant: (iso) => new Date(iso).toLocaleString(),
  });
}

/** A guard flow from a button or the palette — its faults logged at the detached edge, never thrown into VS Code. */
function guardFlow(parts: Parts, op: 'install' | 'remove'): () => void {
  return () => { void parts.guard.run(op).catch((e: unknown) => { parts.log.error(`Windows Time guard ${op} failed: ${String(e)}`); }); };
}

/** *Install daemon*: the client resolves the distribution, the modal and the terminal are real — or recorded in Test mode. */
function installer(parts: Parts): () => void {
  const ui = installUiFor(parts.testMode, parts.install);

  return () => { void installDaemon({ target: () => parts.client.terminalTarget(), ...ui }); };
}

/**
 * *Start Windows Time* (PLAN_windows_time_guard.md D7): the modal shows the exact commands, ONE elevated PowerShell runs
 * them through the real runner (never the fake wsl.exe, never in Test mode — a recorder there), and a finished fix starts
 * *Run full check now* so the daemon's persisted verdicts show the result. One fix at a time: a second click while one runs
 * starts nothing (the progress notification says what is running).
 */
function windowsTimeFixer(parts: Parts): () => void {
  const ui = windowsTimeUiFor(parts.testMode, parts.windowsTime, runnerFor({ kind: 'real' }));
  let running = false;

  return () => {
    if (running) {
      return;
    }
    running = true;
    void startWindowsTime({
      env: process.env,
      setAutomaticStart: () => settings().get<boolean>('windowsTime.setAutomaticStart', true) !== false,
      timeoutMs: () => numbers().windowsTimeFixSeconds * 1000,
      afterDone: () => { void parts.host.runFullCheck(); },
      ...ui,
    }).catch((e: unknown) => { parts.log.error(`Start Windows Time failed: ${String(e)}`); }).finally(() => { running = false; });
  };
}

function logsPanel(context: vscode.ExtensionContext, parts: Parts): LogsPanel {
  const { client, store, log } = parts;

  return new LogsPanel(context.extensionUri, {
    durable: context.globalState, read: (request) => client.read(request), status: () => store.snapshot().status, wallNow: () => Date.now(), numbers,
  }, (line) => log.error(line));
}

/**
 * The panel's refresh: the guard's Task Scheduler query, the panel round (`status`, `preview`, `doctor`) and, once that round's
 * `status` is in the store (its capabilities decide what is asked), the archive's two reads (E10.S1) — never more often.
 */
function panelRefresh(parts: Parts): (options?: RunOptions) => Promise<void> {
  return (options) => {
    void parts.guard.refresh();
    const round = parts.poller.refreshPanel(options);
    void round.then(() => parts.archive.refresh()).catch((e: unknown) => { parts.log.error(`archive: the refresh failed: ${String(e)}`); });

    return round;
  };
}

/** E10.S1: the archive's part of the panel — its controls, their changes, and its two flows (the panel button and the command). */
function archiveActions(parts: Parts): Pick<PanelActions, 'archive' | 'onArchiveChange' | 'chooseArchiveFolder' | 'stopArchiving' | 'archiveNow'> {
  return {
    archive: () => parts.archive.view(),
    onArchiveChange: (listener) => parts.archive.onChange(listener),
    chooseArchiveFolder: () => { void parts.archive.choose(); },
    stopArchiving: () => { void parts.archive.stop(); },
    // E10.S1b own review #1: the archive host refuses on the archive's conditions, then hands it to the cleanup host (its own).
    archiveNow: () => { void parts.archive.archiveNow(); },
  };
}

function archiveCommands(archive: Pick<PanelActions, 'chooseArchiveFolder' | 'stopArchiving' | 'archiveNow'>): vscode.Disposable[] {
  return [
    vscode.commands.registerCommand('wslCare.chooseArchiveFolder', archive.chooseArchiveFolder),
    vscode.commands.registerCommand('wslCare.stopArchiving', archive.stopArchiving),
    vscode.commands.registerCommand('wslCare.archiveNow', archive.archiveNow),
  ];
}

/** The Logs page (E6.S4): itself, its title-bar command, the status it follows, and its serializer for a reload. */
function logsSubscriptions(logs: LogsPanel, store: OutcomeStore): vscode.Disposable[] {
  return [
    logs,
    vscode.commands.registerCommand('wslCare.openLogs', () => logs.show()),
    { dispose: store.onChange(() => logs.statusChanged()) },
    vscode.window.registerWebviewPanelSerializer(LogsPanel.viewType, { deserializeWebviewPanel: (restored) => { logs.restore(restored); return Promise.resolve(); } }),
  ];
}

function wire(context: vscode.ExtensionContext, parts: Parts): { bar: StatusBar; panel: PanelProvider; logs: LogsPanel } {
  const { poller, store, focus, host } = parts;
  const install = installer(parts);
  const windowsTime = windowsTimeFixer(parts);
  const logs = logsPanel(context, parts);
  const bar = new StatusBar(store, OPEN_PANEL);
  const installGuard = guardFlow(parts, 'install');
  const removeGuard = guardFlow(parts, 'remove');
  const refreshAll = panelRefresh(parts);
  const archive = archiveActions(parts);
  const panel = new PanelProvider(context.extensionUri, store, {
    refresh: refreshAll,
    openSettings: () => { void vscode.commands.executeCommand('workbench.action.openSettings', 'wslCare'); },
    installDaemon: install,
    startWindowsTime: windowsTime,
    clean: (rowIds, selected) => { void host.clean(rowIds, selected); },
    runFullCheck: () => { void host.runFullCheck(); },
    stop: (index) => { void host.stop(index); },
    cleanup: () => host.controls(),
    onCleanupChange: (listener) => host.onChange(listener),
    openRunLogs: () => { void logs.show(lastCleanupPeriod(store.snapshot().status)); },
    guard: () => parts.guard.view(),
    onGuardChange: (listener) => parts.guard.onChange(listener),
    installWindowsTimeGuard: installGuard,
    removeWindowsTimeGuard: removeGuard,
    ...archive,
  });
  context.subscriptions.push(
    bar,
    panel,
    { dispose: () => poller.dispose() },
    { dispose: () => { host.dispose(); parts.archive.dispose(); } },
    vscode.window.registerWebviewViewProvider(PanelProvider.viewId, panel),
    vscode.commands.registerCommand(OPEN_PANEL, () => vscode.commands.executeCommand(`${PanelProvider.viewId}.focus`)),
    vscode.commands.registerCommand('wslCare.refresh', () => refreshAll()),
    vscode.commands.registerCommand('wslCare.startWsl', () => refreshAll({ startIfStopped: true })),
    vscode.commands.registerCommand('wslCare.installDaemon', install),
    vscode.commands.registerCommand('wslCare.startWindowsTime', windowsTime),
    vscode.commands.registerCommand('wslCare.installWindowsTimeGuard', installGuard),
    vscode.commands.registerCommand('wslCare.removeWindowsTimeGuard', removeGuard),
    ...archiveCommands(archive),
    ...logsSubscriptions(logs, store),
    vscode.window.onDidChangeWindowState((state) => { if (focus.override === undefined) { poller.focusChanged(state.focused); host.start(); } }),
    vscode.workspace.onDidChangeConfiguration((event) => configurationChanged(event, parts, panel)),
    // A guard setting re-derives the guard's line at once: "install it again to update it" follows the setting (gemini).
    vscode.workspace.onDidChangeConfiguration((event) => { if (event.affectsConfiguration('wslCare.windowsTime')) { parts.guard.settingsChanged(); } }),
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
    windowsTime: () => parts.windowsTime,
    windowsTimeGuard: () => parts.guard,
    guardRecorder: () => parts.guardRecorder,
    cleanup: () => parts.cleanup,
    cleanupHost: () => parts.host,
    cleanRecorder: () => parts.cleanRecorder,
    logs: () => logs,
    archive: () => parts.archive,
    archiveRecorder: () => parts.archiveRecorder,
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
