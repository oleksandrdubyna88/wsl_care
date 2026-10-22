import * as vscode from 'vscode';

import { buildVersion } from './buildStamp';
import { WslCareClient, type RunOptions } from './client/WslCareClient';
import { WindowsCareClient } from './client/WindowsCareClient';
import { ceilingMs } from './client/ceilings';
import { BUNDLED_DAEMON } from './client/handshake';
import { BundledHost } from './bundled/bundledHost';
import { installDaemon } from './install/installDaemon';
import { installUiFor, newInstallRecorder, type InstallRecorder } from './install/installUi';
import { performance } from 'node:perf_hooks';

import { ArchiveHost } from './archive/archiveHost';
import { newArchiveRecorder, type ArchiveRecorder } from './archive/archiveRecorder';
import { archiveUiFor } from './archive/archiveUi';
import { ArchivePanel } from './archivePage/archivePanel';
import { CleanupHost } from './cleanup/cleanupHost';
import { newCleanRecorder, type CleanRecorder } from './cleanup/cleanRecorder';
import { gpuPanelActions, gpuParts, gpuRegistrations, type GpuParts } from './gpu/gpuWiring';
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
import { clientRunner, readPickedBy, type WslCareTestApi } from './testApi';
import { fileInfoOf } from './powershell/elevatedEngine';
import { EngineHost } from './powershell/engineHost';
import { engineQueryFor, newEngineRecorder, type EngineRecorder } from './powershell/engineRecorder';
import { SchedulerHost } from './scheduler/schedulerHost';
import { TasksHost } from './windowsTasks/tasksHost';
import { sweepPending as sweepTasksPending } from './windowsTasks/tasksPending';
import { newTasksRecorder, tasksUiFor, type TasksRecorder } from './windowsTasks/tasksUi';
import { WindowsTimeGuardHost, type GuardUi } from './windowsTime/guardHost';
import { sweepPending } from './windowsTime/guardPending';
import type { GuardOptions } from './windowsTime/guardTask';
import { QUERY_SCRIPT } from './windowsTime/guardScripts';
import { guardUiFor, newGuardRecorder, recordingQuery, type GuardRecorder } from './windowsTime/guardUi';
import { startWindowsTime } from './windowsTime/windowsTimeFix';
import { newWindowsTimeRecorder, windowsTimeUiFor, type WindowsTimeRecorder } from './windowsTime/windowsTimeUi';
import { distroSettingText } from './wsl/distros';
import { DEFAULT_TARGET, type DistroTarget } from './wsl/distroTarget';

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
 *
 * E10.S2a adds the Archive page (`archivePage/`): a `WebviewPanel` opened by *Open archive…* in the panel's archive section and by
 * `wslCare.openArchive`, restored after a reload by its serializer; its one read is the client's unprivileged `archive list --json`.
 */

const OPEN_PANEL = 'wslCare.openPanel';

const REAL_TIMERS: Timers = {
  every: (ms, run) => {
    const handle = setInterval(run, ms);
    return () => clearInterval(handle);
  },
  nowMs: () => performance.now(),
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
  /** The ONE Task Scheduler read of every task under `\wsl-care` (WR-E2) — the guard's and the Windows tasks' part alike. */
  readonly scheduler: SchedulerHost;
  /** WR-E2: the Windows tasks' part of the panel — the shared Task Scheduler read plus the staged copy's probe (read only). */
  readonly tasks: TasksHost;
  /** WR-E3: the Windows tasks' recorded document, prompts, requests and reports in Test mode — and the answers they give. */
  readonly tasksRecorder: TasksRecorder;
  /** Which PowerShell runs the Windows Time runs elevated (PLAN_pwsh_engine_windows_time.md §11 R3). */
  readonly engine: EngineHost;
  readonly engineRecorder: EngineRecorder;
  readonly gpu: GpuParts;
  /** E7.S5a: the bundled Windows daemon's row — the client of bin/wsl-care.exe, on the SAME runner seam as the client. */
  readonly bundled: BundledHost;
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
  const windowsClient = windowsClientOf(context, runner);
  const poller = pollerOf(client, cleanup, windowsClient, store, focus);

  const cleanRecorder = newCleanRecorder();
  // The extension's log (review C11): a fault at a button's detached edge is written here as well as told.
  const log = vscode.window.createOutputChannel('AI OS Care', { log: true });
  context.subscriptions.push(log);
  const host = new CleanupHost({
    durable: context.globalState, controller: cleanup, read: (request, target) => client.read(request, target), outcomes: store,
    askStatus: (target) => poller.askStatus(target), refreshPanel: () => poller.refreshPanel(), focused: () => focus.override ?? vscode.window.state.focused,
    ui: cleanUiFor(testMode, cleanRecorder), timers: ONE_SHOT, now: () => performance.now(), wallNow: () => Date.now(), log: (line) => log.error(line), numbers,
  });

  const guardRecorder = newGuardRecorder();
  const engineRecorder = newEngineRecorder();
  const engine = engineHostOf(testMode, engineRecorder);
  const guardUi = guardUiFor(testMode, guardRecorder, runnerFor({ kind: 'real' }), context.subscriptions);
  const scheduler = schedulerOf(testMode, guardRecorder);
  const guard = windowsTimeGuard(context, guardUi, scheduler, engine);
  const tasksRecorder = newTasksRecorder();
  const archiveRecorder = newArchiveRecorder();
  const archive = archiveHostOf(testMode, client, runner, store, host, archiveRecorder, log);

  return {
    testMode, client, cleanup, install: newInstallRecorder(), windowsTime: newWindowsTimeRecorder(), guardRecorder, guard, scheduler, engine, engineRecorder, tasksRecorder, cleanRecorder, host, archive, archiveRecorder, choice, calls, store, poller, focus, log,
    bundled: new BundledHost({ version: () => windowsClient.version(), pin: BUNDLED_DAEMON }),
    tasks: tasksHostOf(context, { testMode, recorder: tasksRecorder, scheduler, windowsClient, engine, log: (line) => log.error(line) }),
    gpu: gpuParts(context, testMode, numbers, (line) => log.error(line)),
  };
}

/**
 * E7.S5a (research/PLAN_bundle_windows_binary.md §3.7): the bundled exe's client on the SAME runner as the read-only client — the
 * Test-mode fake or the closed runner, never the Windows-Time real runner — so `test:host` never starts the real exe. It starts
 * `<extension>/bin/wsl-care.exe --version` only when the panel asks (its refresh), never at activation.
 */
/**
 * The panel's members of the Windows parts: the bundled daemon's row (E7.S5a) and — WR-E2 — the Windows tasks' lines, each its view
 * and its change.
 */
function windowsPanelActions(bundled: BundledHost, windowsTasks: TasksHost): Pick<PanelActions, 'windowsDaemon' | 'onWindowsDaemonChange' | 'windowsTasks' | 'onWindowsTasksChange'> {
  return {
    windowsDaemon: () => bundled.view(),
    onWindowsDaemonChange: (listener) => bundled.onChange(listener),
    windowsTasks: () => windowsTasks.view(),
    onWindowsTasksChange: (listener) => windowsTasks.onChange(listener),
  };
}

/** WSL's list; on an answer the controller forgets the root check of every distribution it no longer holds (the client prunes its own maps). */
async function listedDistros(client: WslCareClient, cleanup: CleanupController): ReturnType<WslCareClient['listDistros']> {
  const list = await client.listDistros();
  if ('distros' in list) {
    cleanup.forgetUnlisted(list.distros.map((info) => info.name));
  }

  return list;
}

/** The poller: the distribution's client, and — E19.S2b — the bundled Windows daemon's `status` on the same tick, only where its client can start the exe. */
function pollerOf(client: WslCareClient, cleanup: CleanupController, windowsClient: WindowsCareClient, store: OutcomeStore, focus: { readonly override: boolean | undefined }): Poller {
  return new Poller({
    run: (verb, target, options) => client.run(verb, target, options),
    listDistros: () => listedDistros(client, cleanup),
    store,
    focused: () => focus.override ?? vscode.window.state.focused,
    refreshSeconds: () => settings().get<unknown>('refreshSeconds'),
    timers: REAL_TIMERS,
    target: () => distroSettingText(distroSetting()),
    windows: { usable: () => typeof windowsClient.exe() === 'string', status: () => windowsClient.status() },
  });
}

/**
 * ONE client of the bundled exe, shared by the panel's Windows row (`--version`), the poller (`status`, E19.S2b) and the Windows
 * tasks' part (WR-E2: the bundled exe's digest and `tasks render` on the staged copy); the `status` ceiling is the `windowsStatus` call
 * of `client/ceilings.ts`, `tasks render`'s is `wslCare.timeouts.windowsTasksStageSeconds` — each read from the settings at every call.
 * The staged copy lives under the profile's `%LOCALAPPDATA%`, which the client takes as an option and derives the folder from.
 */
function windowsClientOf(context: vscode.ExtensionContext, runner: ReturnType<typeof clientRunner>): WindowsCareClient {
  const localAppData = process.env.LOCALAPPDATA;

  return new WindowsCareClient({
    runner,
    platform: process.platform,
    extension: { scheme: context.extensionUri.scheme, fsPath: context.extensionUri.fsPath },
    timeoutMs: () => numbers().versionSeconds * 1000,
    statusTimeoutMs: () => ceilingMs(numbers(), { call: 'windowsStatus' }),
    tasksTimeoutMs: () => numbers().windowsTasksStageSeconds * 1000,
    previewTimeoutMs: () => ceilingMs(numbers(), { call: 'windowsTempPreview' }),
    ...(localAppData === undefined ? {} : { localAppData }),
  });
}

/** What the Windows tasks' host is built from: the shared Task Scheduler read, the one client of the bundled exe, the shared engine read and the Test-mode recorder. */
interface TasksParts {
  readonly testMode: boolean;
  readonly recorder: TasksRecorder;
  readonly scheduler: SchedulerHost;
  readonly windowsClient: WindowsCareClient;
  readonly engine: EngineHost;
  readonly log: (line: string) => void;
}

/**
 * The Windows tasks' part: it consumes the ONE Task Scheduler read and probes the staged copy through the shared client (WR-E2); since WR-E3 its
 * install and remove run ONE UNELEVATED PowerShell each — the engine `engine` chose, through the REAL runner (a recorder in Test mode, where the
 * tripwire would refuse the real thing besides). A pending run past its deadline is swept at activation, so nothing stays "waiting" after a crash.
 */
function tasksHostOf(context: vscode.ExtensionContext, parts: TasksParts): TasksHost {
  // A store that refuses leaves the record to its deadline: logged, never an unhandled rejection. (The guard's own sweep line is unchanged.)
  void sweepTasksPending(context.globalState, Date.now()).catch((e: unknown) => { parts.log(`Windows tasks: the sweep of a stale pending record failed: ${String(e)}`); });

  return new TasksHost({
    scheduler: parts.scheduler,
    client: parts.windowsClient,
    pin: BUNDLED_DAEMON,
    formatInstant: (iso) => new Date(iso).toLocaleString(),
    ui: tasksUiFor(parts.testMode, parts.recorder, runnerFor({ kind: 'real' }), context.subscriptions),
    engine: parts.engine,
    durable: context.globalState,
    nowUtcMs: () => Date.now(),
    sleep: (ms) => new Promise((resolve) => { setTimeout(resolve, ms); }),
    opTimeoutMs: () => numbers().windowsTasksSeconds * 1000,
    stageTimeoutMs: () => numbers().windowsTasksStageSeconds * 1000,
    busyRetryMs: () => numbers().windowsTasksBusyRetrySeconds * 1000,
    extensionFolder: context.extensionUri.fsPath,
    log: parts.log,
  });
}

/**
 * E10.S1: the archive's reads and its one user-layer write go through the SAME runner and the client's target checks.
 * E10.S1b: Archive now is greyed while a cleanup is in flight; the cleanup's changes re-render the archive's controls; the
 * archive host refuses on its own conditions first, then the cleanup host runs A13 through its transaction. (Its own
 * function since the GPU host joined `build()`, which the line bound then held to 50.)
 */
function archiveHostOf(testMode: boolean, client: WslCareClient, runner: ReturnType<typeof clientRunner>, store: OutcomeStore, host: CleanupHost, recorder: ArchiveRecorder, log: vscode.LogOutputChannel): ArchiveHost {
  return new ArchiveHost({
    read: (request, target) => client.read(request, target), target: (target) => client.rootTarget(target), runner, outcomes: store, ui: archiveUiFor(testMode, recorder),
    log: (line) => log.error(line), numbers, limits: () => limitsOf(store),
    cleanupFree: () => host.controls().enabled, onCleanupChange: (listener) => host.onChange(listener), archiveNow: (target) => host.archiveNow(target),
  });
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
 * The engine read (PLAN_pwsh_engine_windows_time.md §11 R3): the REAL runner (never the fake wsl.exe; a recorder in Test mode),
 * shared by *Start Windows Time* and the guard's install / remove, and read again by the panel's refresh.
 */
function engineHostOf(testMode: boolean, recorder: EngineRecorder): EngineHost {
  return new EngineHost({ env: process.env, fileInfo: fileInfoOf, timeoutMs: () => numbers().engineProbeSeconds * 1000, query: engineQueryFor(testMode, recorder, runnerFor({ kind: 'real' })) });
}

/**
 * The Windows Time guard (PLAN_windows_time_task.md): its host reads Task Scheduler through the REAL runner (never the fake
 * wsl.exe; a recorder in Test mode), and its install / remove run ONE elevated PowerShell each — the engine `engine` chose.
 * A pending run past its deadline is swept at activation, so nothing stays "waiting" after a crash.
 */
function windowsTimeGuard(context: vscode.ExtensionContext, ui: GuardUi, scheduler: SchedulerHost, engine: EngineHost): WindowsTimeGuardHost {
  void sweepPending(context.globalState, Date.now());

  return new WindowsTimeGuardHost({
    env: process.env,
    options: guardOptions,
    opTimeoutMs: () => numbers().windowsTimeGuardSeconds * 1000,
    ui,
    scheduler,
    durable: context.globalState,
    nowUtcMs: () => Date.now(),
    formatInstant: (iso) => new Date(iso).toLocaleString(),
    engine,
  });
}

/**
 * The ONE Task Scheduler read of every task under `\wsl-care` (WR-E2), in `src/scheduler/`: its script is the guard's facts plus the
 * folder's tasks (`windowsTime/guardScripts.ts`), its capability to run it comes from the process layer — the real UNELEVATED
 * runner, or the recorder in Test mode — and is not one of the guard's surfaces; bounded by
 * `wslCare.timeouts.windowsTimeGuardQuerySeconds`. The guard and the Windows tasks' part both consume it.
 */
function schedulerOf(testMode: boolean, recorder: GuardRecorder): SchedulerHost {
  const query = testMode ? recordingQuery(recorder) : runnerFor({ kind: 'real' });

  return new SchedulerHost({ env: process.env, queryTimeoutMs: () => numbers().windowsTimeGuardQuerySeconds * 1000, script: QUERY_SCRIPT, query });
}

/** Two subscriptions as one: the guard's part re-renders on Task Scheduler's answers AND on the engine read's (D6). */
function bothChanges(first: () => void, second: () => void): () => void {
  return () => { first(); second(); };
}

/** A guard flow from a button or the palette — its faults logged at the detached edge, never thrown into VS Code. */
function guardFlow(parts: Parts, op: 'install' | 'remove'): () => void {
  return () => { void parts.guard.run(op).catch((e: unknown) => { parts.log.error(`Windows Time guard ${op} failed: ${String(e)}`); }); };
}

/** The three flows of the Windows tasks, as the panel's buttons and the palette commands run them. */
type TasksFlows = Pick<PanelActions, 'installWindowsWatchTask' | 'installWindowsArchiveTask' | 'removeWindowsTasks' | 'runWindowsWatchTask' | 'runWindowsArchiveTask' | 'installWindowsCleanupTask' | 'previewWindowsTempCleanup' | 'runWindowsCleanupTask'>;

/** A Windows tasks flow from a button or the palette — its faults logged at the detached edge, never thrown into VS Code. */
function tasksFlow(parts: Parts, op: Parameters<TasksHost['run']>[0]): () => void {
  return () => { void parts.tasks.run(op).catch((e: unknown) => { parts.log.error(`Windows tasks ${op.kind} failed: ${String(e)}`); }); };
}

function tasksFlowsOf(parts: Parts): TasksFlows {
  return {
    installWindowsWatchTask: tasksFlow(parts, { kind: 'install', task: 'watch' }),
    installWindowsArchiveTask: tasksFlow(parts, { kind: 'install', task: 'archive' }),
    removeWindowsTasks: tasksFlow(parts, { kind: 'remove' }),
    runWindowsWatchTask: tasksFlow(parts, { kind: 'run', task: 'watch' }),
    runWindowsArchiveTask: tasksFlow(parts, { kind: 'run', task: 'archive' }),
    installWindowsCleanupTask: tasksFlow(parts, { kind: 'install', task: 'cleanup' }),
    previewWindowsTempCleanup: tasksFlow(parts, { kind: 'preview' }),
    runWindowsCleanupTask: tasksFlow(parts, { kind: 'run', task: 'cleanup' }),
  };
}

/** *Install daemon*: the client resolves the distribution, the modal and the terminal are real — or recorded in Test mode. */
function installer(parts: Parts, target: DistroTarget): () => void {
  const ui = installUiFor(parts.testMode, parts.install);

  return () => { void installDaemon({ target: () => parts.client.terminalTarget(target), ...ui }); };
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
      afterDone: () => { void parts.host.runFullCheck(DEFAULT_TARGET); },
      engine: parts.engine,
      ...ui,
    }).catch((e: unknown) => { parts.log.error(`Start Windows Time failed: ${String(e)}`); }).finally(() => { running = false; });
  };
}

function logsPanel(context: vscode.ExtensionContext, parts: Parts): LogsPanel {
  const { client, store, log } = parts;

  return new LogsPanel(context.extensionUri, {
    durable: context.globalState, read: (request) => client.read(request, DEFAULT_TARGET), status: () => store.snapshot().status, wallNow: () => Date.now(), numbers,
  }, (line) => log.error(line));
}

/**
 * The panel's refresh: the ONE shared Task Scheduler read — the guard host and the Windows tasks' host both FOLLOW its change, neither
 * asks it itself (WR-E2, the coai code round finding 0) — and the tasks' probe of the staged copy, the panel round (`status`,
 * `preview`, `doctor`) and, once that round's `status` is in the store (its capabilities decide what is asked), the archive's two
 * reads (E10.S1) — never more often. These are the ONE call sites of the shared read and of the probe: they run when the panel
 * opens, becomes visible or is refreshed, and nowhere else (an elevated guard run answering reads once more, through the guard).
 */
function panelRefresh(parts: Parts): (options?: RunOptions) => Promise<void> {
  return (options) => {
    void parts.scheduler.refresh();
    void parts.tasks.refresh();
    void parts.engine.refresh();
    void parts.bundled.refresh();
    void parts.gpu.host.refresh();
    const round = parts.poller.refreshPanel(options);
    void round.then(() => parts.archive.refresh()).catch((e: unknown) => { parts.log.error(`archive: the refresh failed: ${String(e)}`); });

    return round;
  };
}

/** E10.S1: the archive's part of the panel — its controls, their changes, and its two flows (the panel button and the command). */
function archiveActions(parts: Parts, page: ArchivePanel): Pick<PanelActions, 'archive' | 'onArchiveChange' | 'chooseArchiveFolder' | 'stopArchiving' | 'archiveNow' | 'openArchive'> {
  return {
    archive: () => parts.archive.view(),
    onArchiveChange: (listener) => parts.archive.onChange(listener),
    chooseArchiveFolder: () => { void parts.archive.choose(DEFAULT_TARGET); },
    stopArchiving: () => { void parts.archive.stop(DEFAULT_TARGET); },
    // E10.S1b own review #1: the archive host refuses on the archive's conditions, then hands it to the cleanup host (its own).
    archiveNow: () => { void parts.archive.archiveNow(DEFAULT_TARGET); },
    // E10.S2a: the Archive page — it reads the list itself.
    openArchive: () => { void page.show(); },
  };
}

/** S7b.2b: the client-gone relays' line and button — the cleanup host's — and *Stop client-gone relays* (the button and the command). */
function relayActions(parts: Parts): Pick<PanelActions, 'relays' | 'stopRelays'> {
  return {
    relays: () => parts.host.relays(),
    stopRelays: () => { void parts.host.stopRelays(DEFAULT_TARGET); },
  };
}

/** The panel's parts beyond the cleanup and the guard: the archive's (E10.S1), the relays' (S7b.2b) and the Windows tasks' five flows (WR-E3, WR-E4). */
function panelExtras(parts: Parts, page: ArchivePanel): ReturnType<typeof archiveActions> & ReturnType<typeof relayActions> & TasksFlows {
  return { ...archiveActions(parts, page), ...relayActions(parts), ...tasksFlowsOf(parts) };
}

/** Their palette commands — each duplicates a panel button. */
function extraCommands(extras: ReturnType<typeof panelExtras>): vscode.Disposable[] {
  return [
    ...archiveCommands(extras),
    vscode.commands.registerCommand('wslCare.stopRelays', extras.stopRelays),
    vscode.commands.registerCommand('wslCare.installWindowsWatchTask', extras.installWindowsWatchTask),
    vscode.commands.registerCommand('wslCare.installWindowsArchiveTask', extras.installWindowsArchiveTask),
    vscode.commands.registerCommand('wslCare.removeWindowsTasks', extras.removeWindowsTasks),
    vscode.commands.registerCommand('wslCare.runWindowsWatchTask', extras.runWindowsWatchTask),
    vscode.commands.registerCommand('wslCare.runWindowsArchiveTask', extras.runWindowsArchiveTask),
    vscode.commands.registerCommand('wslCare.installWindowsCleanupTask', extras.installWindowsCleanupTask),
    vscode.commands.registerCommand('wslCare.previewWindowsTempCleanup', extras.previewWindowsTempCleanup),
    vscode.commands.registerCommand('wslCare.runWindowsCleanupTask', extras.runWindowsCleanupTask),
  ];
}

function archiveCommands(archive: Pick<PanelActions, 'chooseArchiveFolder' | 'stopArchiving' | 'archiveNow'>): vscode.Disposable[] {
  return [
    vscode.commands.registerCommand('wslCare.chooseArchiveFolder', archive.chooseArchiveFolder),
    vscode.commands.registerCommand('wslCare.stopArchiving', archive.stopArchiving),
    vscode.commands.registerCommand('wslCare.archiveNow', archive.archiveNow),
  ];
}

function archivePanel(context: vscode.ExtensionContext, parts: Parts): ArchivePanel {
  const { client, store, log, host } = parts;
  // E10.S2b: *Restore* is the cleanup host's — the page hands it entry ids as text and reads its facts; it never touches root/.
  const restore = { facts: () => ({ inFlight: host.restoreInFlight(), state: host.controls().state, blocker: host.restoreBlocker() }), start: (ids: readonly string[]) => host.restore(ids, DEFAULT_TARGET) };

  return new ArchivePanel(context.extensionUri, { read: (request) => client.read(request, DEFAULT_TARGET), status: () => store.snapshot().status, restore }, (line) => log.error(line));
}

/** The Archive page (E10.S2a): itself, its command (also the archive section's *Open archive…*), the status and the restore (E10.S2b) it follows, its serializer. */
function archivePageSubscriptions(page: ArchivePanel, store: OutcomeStore, host: CleanupHost): vscode.Disposable[] {
  return [
    page,
    vscode.commands.registerCommand('wslCare.openArchive', () => page.show()),
    { dispose: store.onChange(() => page.statusChanged()) },
    { dispose: host.onChange(() => page.restoreChanged()) },
    vscode.window.registerWebviewPanelSerializer(ArchivePanel.viewType, { deserializeWebviewPanel: (restored) => { page.restore(restored); return Promise.resolve(); } }),
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

function wire(context: vscode.ExtensionContext, parts: Parts): { bar: StatusBar; panel: PanelProvider; logs: LogsPanel; archivePage: ArchivePanel } {
  const { poller, store, focus, host } = parts;
  const install = installer(parts, DEFAULT_TARGET);
  const windowsTime = windowsTimeFixer(parts);
  const logs = logsPanel(context, parts);
  const archivePage = archivePanel(context, parts);
  const bar = new StatusBar(store, OPEN_PANEL, { mark: () => parts.gpu.host.barMark(), onChange: (listener) => parts.gpu.host.onChange(listener) });
  const [installGuard, removeGuard] = [guardFlow(parts, 'install'), guardFlow(parts, 'remove')];
  const refreshAll = panelRefresh(parts);
  const extras = panelExtras(parts, archivePage);
  const panel = new PanelProvider(context.extensionUri, store, {
    refresh: refreshAll,
    openSettings: () => { void vscode.commands.executeCommand('workbench.action.openSettings', 'wslCare'); },
    installDaemon: install,
    startWindowsTime: windowsTime,
    clean: (rowIds, selected) => { void host.clean(rowIds, selected, DEFAULT_TARGET); },
    runFullCheck: () => { void host.runFullCheck(DEFAULT_TARGET); },
    stop: (index) => { void host.stop(index, DEFAULT_TARGET); },
    cleanup: () => host.controls(),
    onCleanupChange: (listener) => host.onChange(listener),
    openRunLogs: () => { void logs.show(lastCleanupPeriod(store.snapshot().status)); },
    guard: () => parts.guard.view(),
    onGuardChange: (listener) => bothChanges(parts.guard.onChange(listener), parts.engine.onChange(listener)),
    installWindowsTimeGuard: installGuard,
    removeWindowsTimeGuard: removeGuard,
    ...extras,
    ...gpuPanelActions(parts.gpu),
    ...windowsPanelActions(parts.bundled, parts.tasks),
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
    ...extraCommands(extras),
    ...logsSubscriptions(logs, store), ...archivePageSubscriptions(archivePage, store, host),
    vscode.window.onDidChangeWindowState((state) => { if (focus.override === undefined) { poller.focusChanged(state.focused); host.start(); } }),
    ...settingsListeners(parts, panel),
  );

  return { bar, panel, logs, archivePage };
}

/** Every setting a host re-reads the moment it changes — and the GPU story's own command and listener (`gpu/gpuWiring.ts`). */
function settingsListeners(parts: Parts, panel: PanelProvider): vscode.Disposable[] {
  return [
    vscode.workspace.onDidChangeConfiguration((event) => configurationChanged(event, parts, panel)),
    ...gpuRegistrations(parts.gpu),
    // A guard setting re-derives the guard's line at once: "install it again to update it" follows the setting (gemini).
    vscode.workspace.onDidChangeConfiguration((event) => { if (event.affectsConfiguration('wslCare.windowsTime')) { parts.guard.settingsChanged(); } }),
  ];
}

function configurationChanged(event: vscode.ConfigurationChangeEvent, parts: Parts, panel: PanelProvider): void {
  if (event.affectsConfiguration('wslCare.refreshSeconds')) {
    parts.poller.settingsChanged();
  }
  if (event.affectsConfiguration('wslCare.distro')) {
    void (panel.isVisible() ? parts.poller.refreshPanel() : parts.poller.tick());
  }
}

function testApi(parts: Parts, bar: StatusBar, panel: PanelProvider, logs: LogsPanel, archivePage: ArchivePanel): WslCareTestApi {
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
    listDistros: () => poller.listDistros(),
    readPicked: (name, options) => readPickedBy(poller, name, options),
    lastRendered: () => panel.lastRendered(),
    install: () => parts.install,
    windowsTime: () => parts.windowsTime,
    windowsTimeGuard: () => parts.guard,
    windowsTasks: () => parts.tasks,
    tasksRecorder: () => parts.tasksRecorder,
    guardRecorder: () => parts.guardRecorder,
    gpu: () => parts.gpu.host,
    gpuRecorder: () => parts.gpu.recorder,
    bundled: () => parts.bundled,
    cleanup: () => parts.cleanup,
    cleanupHost: () => parts.host,
    cleanRecorder: () => parts.cleanRecorder,
    logs: () => logs,
    archivePage: () => archivePage,
    archive: () => parts.archive,
    archiveRecorder: () => parts.archiveRecorder,
    buildVersion,
  };
}

export function activate(context: vscode.ExtensionContext): WslCareTestApi | undefined {
  const parts = build(context);
  const { bar, panel, logs, archivePage } = wire(context, parts);
  parts.poller.start();
  parts.host.start();
  parts.gpu.host.start();

  return context.extensionMode === vscode.ExtensionMode.Test ? testApi(parts, bar, panel, logs, archivePage) : undefined;
}

export function deactivate(): void {
  // Everything is disposed through context.subscriptions.
}
