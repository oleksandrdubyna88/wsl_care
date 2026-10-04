import type { InstallRecorder } from './install/installUi';
import type { ProcessRequest, Runner } from './process/runner';
import type { RunnerChoice } from './process/runnerSelection';
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
  /** The version the running bundle was built for (`buildStamp.ts`). */
  buildVersion(): string;
}

/** The runner wrapped so every request is logged before it is handed on — the test API's call log. */
export function loggedRunner(inner: Runner, log: string[]): Runner {
  return (request: ProcessRequest) => {
    log.push(request.args.join(' '));
    return inner(request);
  };
}
