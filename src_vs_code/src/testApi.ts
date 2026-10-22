import type { ArchiveHost } from './archive/archiveHost';
import type { ArchiveRecorder } from './archive/archiveRecorder';
import type { BundledHost } from './bundled/bundledHost';
import type { CleanupHost } from './cleanup/cleanupHost';
import type { CleanRecorder } from './cleanup/cleanRecorder';
import type { GpuHost } from './gpu/gpuHost';
import type { GpuRecorder } from './gpu/gpuUi';
import type { InstallRecorder } from './install/installUi';
import type { TasksHost } from './windowsTasks/tasksHost';
import type { TasksRecorder } from './windowsTasks/tasksRecorder';
import type { WindowsTimeGuardHost } from './windowsTime/guardHost';
import type { GuardRecorder } from './windowsTime/guardUi';
import type { WindowsTimeRecorder } from './windowsTime/windowsTimeUi';
import type { LogsPanel } from './logsPage/logsPanel';
import type { ArchivePanel } from './archivePage/archivePanel';
import type { Failure } from './client/outcome';
import type { RunOptions } from './client/WslCareClient';
import type { Poller } from './poll/poller';
import type { ProcessRequest, Runner } from './process/runner';
import { listedNameOf, type DistroList } from './wsl/distroTarget';
import type { RunnerChoice } from './process/runnerSelection';
import type { CleanupController } from './root/cleanupController';
import type { BarView } from './statusBar/statusBarModel';

/**
 * What `activate` returns IN TEST MODE ONLY (`context.extensionMode === ExtensionMode.Test`; `undefined` otherwise) —
 * the handles the extension-host scenarios (`src/test/host/suite.ts`, `@vscode/test-electron`) need to observe what
 * no VS Code API exposes: the status-bar item's text and colour, the rows the webview reported drawing, every request
 * that reached the runner seam, and the window's focus (which a test runner cannot set, and which decides polling).
 */
export interface WslCareTestApi {
  /** Every request that reached the runner seam since the last reset: its argv joined with spaces. */
  calls(): readonly string[];
  resetCalls(): void;
  runnerKind(): RunnerChoice['kind'];
  statusBar(): BarView;
  /** Overrides `window.state.focused` and tells the poller, as a real focus change would. */
  setFocused(focused: boolean): void;
  /** What the polling interval does when it fires. */
  tick(): Promise<void>;
  refreshPanel(): Promise<void>;
  startWsl(): Promise<void>;
  settled(): Promise<void>;
  lastRendered(): number | undefined;
  /** *Install daemon*'s recorded modal prompts, terminals and reports — and the answer the recorded modal gives. */
  install(): InstallRecorder;
  /** *Start Windows Time*'s recorded modal prompts, requests and reports — and the answers they give (no PowerShell runs). */
  windowsTime(): WindowsTimeRecorder;
  /** The Windows Time guard's host — its state, its view and its two flows (PLAN_windows_time_task.md). */
  windowsTimeGuard(): WindowsTimeGuardHost;
  /** WR-E2: the Windows tasks' part — its probe of the staged copy and its lines, over the same shared Task Scheduler read. */
  windowsTasks(): TasksHost;
  /** WR-E3: the Windows tasks' recorded document, prompts, requests and reports — and the answers they give (no PowerShell runs). */
  tasksRecorder(): TasksRecorder;
  /** The guard's recorded document, prompts, elevated requests, queries and reports — and the answers they give. */
  guardRecorder(): GuardRecorder;
  /** The GPU host (PLAN_gpu_health.md) — its state, its view, its bar mark and its enable. */
  gpu(): GpuHost;
  /** *Enable <GPU>*'s recorded prompts, elevated requests, queries and reports — and the answers they give (no PowerShell runs). */
  gpuRecorder(): GpuRecorder;
  /** E7.S5a: the host of the panel's Windows row — the bundled exe's last `--version`, read through the runner seam. */
  bundled(): BundledHost;
  /** The host-side cleanup controller (E6.S2) — the API E6.S3's buttons will call; in Test mode the host suite reaches it here. */
  cleanup(): CleanupController;
  /** E6.S3: the cleanup buttons' host side — its journal, follower and transaction — as the panel's messages reach it. */
  cleanupHost(): CleanupHost;
  /** E6.S3: the cleanup flow's recorded modals and notifications — and the answers they give. */
  cleanRecorder(): CleanRecorder;
  /** E6.S4: the Logs page — its controller (period, rendered blocks) as the page's messages reach it. */
  logs(): LogsPanel;
  /** E10.S2a: the Archive page — its controller (the list, the open months, rendered blocks) as the page's messages reach it. */
  archivePage(): ArchivePanel;
  /** E10.S1: the archive's host — its reads, its view and its two flows — and its recorded dialog, modals and notifications. */
  archive(): ArchiveHost;
  archiveRecorder(): ArchiveRecorder;
  /** E18.W1a: every distribution WSL reports (`-l -v` then `--list --running --quiet`); the store drops the picked snapshots of names it no longer holds. */
  listDistros(): Promise<DistroList | Failure>;
  /**
   * E18.W1a: reads a PICKED distribution with no UI — asks WSL for the list first, mints the name from it (a name WSL does not list THROWS: a
   * test defect), then `status` / `preview` / `doctor` into the store's picked slot of that name. `options.startIfStopped` is the explicit start.
   */
  readPicked(name: string, options?: RunOptions): Promise<void>;
  /** The version the running bundle was built for (`buildStamp.ts`). */
  buildVersion(): string;
}

/**
 * The runner the client is given: logged into `log` in Test mode ONLY. Outside it nothing reads the log, and a window
 * left open for weeks would grow it with every poll (E5 code round, security review #4) — so the real runner is handed
 * over unwrapped. `testApi.test.ts` holds both and that `extension.ts` goes through here.
 */
export function clientRunner(testMode: boolean, inner: Runner, log: string[]): Runner {
  return testMode ? loggedRunner(inner, log) : inner;
}

/** The runner wrapped so every request is logged before it is handed on — the test API's call log. */
export function loggedRunner(inner: Runner, log: string[]): Runner {
  return (request: ProcessRequest) => {
    log.push(request.args.join(' '));
    return inner(request);
  };
}

/**
 * The test API's picked read: the list FIRST (a store that has been told no list ignores every picked write), the name minted from it
 * with `listedNameOf` — the one road to a `DistroName` — and then the read. A name WSL does not list, or a list that failed, THROWS:
 * it is a defect of the test that asked, never a read of an invented distribution.
 */
export async function readPickedBy(poller: Pick<Poller, 'listDistros' | 'readPicked'>, name: string, options?: RunOptions): Promise<void> {
  const list = await poller.listDistros();
  if (!('distros' in list)) {
    throw new Error(`readPicked(${name}): WSL's list failed (${list.kind})`);
  }
  const minted = listedNameOf(name, list.distros.map((info) => info.name));
  if (minted === undefined) {
    throw new Error(`readPicked(${name}): WSL does not list that distribution (it lists: ${list.distros.map((info) => info.name).join(', ')})`);
  }
  await poller.readPicked(minted, options);
}
