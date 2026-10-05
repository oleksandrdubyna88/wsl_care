import type { CleanupHost } from './cleanup/cleanupHost';
import type { CleanRecorder } from './cleanup/cleanUi';
import type { InstallRecorder } from './install/installUi';
import type { ProcessRequest, Runner } from './process/runner';
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
  /** The host-side cleanup controller (E6.S2) — the API E6.S3's buttons will call; in Test mode the host suite reaches it here. */
  cleanup(): CleanupController;
  /** E6.S3: the cleanup buttons' host side — its journal, follower and transaction — as the panel's messages reach it. */
  cleanupHost(): CleanupHost;
  /** E6.S3: the cleanup flow's recorded modals and notifications — and the answers they give. */
  cleanRecorder(): CleanRecorder;
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
