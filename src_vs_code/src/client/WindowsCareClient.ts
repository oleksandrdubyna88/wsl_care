import * as path from 'node:path';

import type { ProcessResult, Runner } from '../process/runner';
import { signed32 } from '../process/runner';
import { VERBS } from './verbs';

/**
 * The minimal client of the BUNDLED Windows daemon (E7.S5a, research/PLAN_bundle_windows_binary.md §3.7) — the ONE module that
 * names the exe (`structure.test.ts` holds it, with a planted companion). The `.vsix` carries `bin/wsl-care.exe`, the attested
 * member of `daemon-v<BUNDLED_DAEMON>`; this client starts exactly that file:
 *
 * - by ABSOLUTE path under the extension's own folder (`extensionUri` + `bin/wsl-care.exe`) — never from `PATH`;
 * - only when the extension host is Windows and the extension sits on a local disk (`file:` URI). The extension is
 *   `extensionKind: ["ui"]`, so VS Code runs it in the LOCAL Windows UI host even for a WSL workspace;
 * - through the runner it is GIVEN — the same seam as `WslCareClient` (the Test-mode fake or the closed runner), never the
 *   Windows-Time `runnerFor({ kind: 'real' })` — no shell, `windowsHide` (CREATE_NO_WINDOW), the runner's ceilings;
 * - with `VERBS.version` alone (only `verbs.ts` spells `--version`).
 *
 * It is never elevated, never run from a scheduled task or as SYSTEM, and its path is never registered anywhere (§4): the
 * extension folder is the user's own. A refused start (Smart App Control, say) reaches the runner as Node's error message,
 * which is shown as Windows gave it — never a guessed code.
 */

/** `bin/wsl-care.exe` under the extension's folder — the one place the exe is named. */
const BUNDLED_EXE: readonly [string, string] = ['bin', 'wsl-care.exe'];

/**
 * A local disk's absolute path: a drive letter, a colon, a separator. `path.win32.isAbsolute` also says yes to a share
 * (`\\server\share\…`) and to a root without a drive (`\Users\…`) — neither is the user's own local extension folder (own
 * Fable review of the code, note 11).
 */
const DRIVE_ROOT = /^[A-Za-z]:[\\/]/;

/** Where the extension is installed — `vscode.ExtensionContext.extensionUri`, as plain data so the client stays vscode-free. */
export interface ExtensionLocation {
  readonly scheme: string;
  readonly fsPath: string;
}

export interface WindowsClientOptions {
  readonly runner: Runner;
  readonly platform: string;
  readonly extension: ExtensionLocation;
  /** `wslCare.timeouts.versionSeconds`, in milliseconds. */
  readonly timeoutMs: () => number;
}

/** What `--version` of the bundled exe answered: its version line, why it was not started, or why it did not answer. */
export type BundledRead =
  | { readonly kind: 'version'; readonly text: string }
  | { readonly kind: 'unavailable'; readonly reason: string }
  | { readonly kind: 'failed'; readonly reason: string };

function firstLine(data: Buffer): string {
  return data.toString('utf8').split(/\r?\n/).map((line) => line.trim()).find((line) => line !== '') ?? '';
}

function exitedRead(code: number, stdout: Buffer, stderr: Buffer): BundledRead {
  const text = firstLine(stdout);
  if (code !== 0) {
    return { kind: 'failed', reason: `it exited ${code}: ${firstLine(stderr)}` };
  }

  return text === '' ? { kind: 'failed', reason: 'it printed no version' } : { kind: 'version', text };
}

const NOT_EXITED: { readonly [K in Exclude<ProcessResult['kind'], 'exited'>]: (r: Extract<ProcessResult, { kind: K }>) => string } = {
  timedOut: (r) => `it did not answer within ${Math.round(r.timeoutMs / 1000)} s (wslCare.timeouts.versionSeconds)`,
  failedToStart: (r) => `it could not start: ${r.reason}`,
  signalled: (r) => `it was stopped (${r.signal})`,
  tooMuchOutput: () => 'it printed more than the extension reads',
};

/** The runner's result as one read — pure, so every answer is a unit test. */
export function bundledReadOf(result: ProcessResult): BundledRead {
  return result.kind === 'exited'
    ? exitedRead(signed32(result.code), result.stdout, result.stderr)
    : { kind: 'failed', reason: (NOT_EXITED[result.kind] as (r: ProcessResult) => string)(result) };
}

export class WindowsCareClient {
  constructor(private readonly options: WindowsClientOptions) {}

  /** The bundled exe's absolute path — or why none is started here. */
  exe(): string | { readonly reason: string } {
    const { platform, extension } = this.options;
    if (platform !== 'win32') {
      return { reason: 'the bundled daemon is the Windows build, and this extension host is not Windows' };
    }
    const file = path.win32.join(extension.fsPath, ...BUNDLED_EXE);

    return extension.scheme === 'file' && DRIVE_ROOT.test(file) ? file : { reason: `the extension is not on a local disk (${extension.scheme}:${extension.fsPath})` };
  }

  /** `--version` of the bundled exe, through the given seam — nothing started when {@link exe} says why not. */
  async version(): Promise<BundledRead> {
    const file = this.exe();
    if (typeof file !== 'string') {
      return { kind: 'unavailable', reason: file.reason };
    }

    return bundledReadOf(await this.options.runner({ file, args: VERBS.version, timeoutMs: this.options.timeoutMs() }));
  }
}
