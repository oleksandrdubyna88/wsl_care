import * as path from 'node:path';

import type { ProcessResult, Runner } from '../process/runner';
import { signed32 } from '../process/runner';
import { STAGED_DIGEST_SHAPE } from '../shared/shapes';
import { sha256OfFile } from './fileDigest';
import { answerExitsOf, readRefusal } from './runReadChecks';
import { RUN_READ_TIMEOUT_MS, runReadTail, TASKS_DEFAULT_TIMEOUT_MS, TASKS_REMOVE_TAIL, TASKS_STAGE_TAIL, tasksRenderTail, VERBS, WINDOWS_READ_NAMES, type RunReadName, type TaskName, type WindowsRunRead } from './verbs';
import { endingReason, firstLine, readOutcomeOf, removeResultOf, renderResultOf, stagedDigest, stageResultOf, type RemoveResult, type RenderResult, type StageResult, type WindowsReadOutcome } from './windowsTasks';

/**
 * The client of the Windows daemon (E7.S5a, research/PLAN_bundle_windows_binary.md §3.7; widened by WR-E1,
 * PLAN_windows_runner.md §3.8) — the ONE module that names the exe, in both of its places (`structure.test.ts` and
 * `bundledExeStructure.test.ts` hold it, with planted companions). The exe is one of two files, named by an {@link ExeSource}:
 *
 * - `bundled` — the `.vsix`'s `bin/wsl-care.exe`, the attested member of `daemon-v<BUNDLED_DAEMON>`, started by ABSOLUTE path under
 *   the extension's own folder (`extensionUri` + `bin/wsl-care.exe`) — never from `PATH`;
 * - `staged(<digest>)` — the verified copy `tasks stage` made under the daemon's staging folder, `<LocalAppData>\wsl-care\bin\<digest>\wsl-care.exe`; the
 *   client derives the folder itself from the profile's LocalAppData it is given and the fixed segments, and refuses a LocalAppData that
 *   is not in its normal form (a `..`, a `.`, forward slashes, doubled or trailing separators), so the folder cannot be steered.
 *   The digest is checked to be 64 LOWER-case hex digits BEFORE any path is built or any process started
 *   ({@link stagedDigest}): it is a folder name, and a folder name from outside must never be able to say `..`.
 *
 * Both only when the extension host is Windows and the folder is a local disk (a `file:` URI for the extension: it is
 * `extensionKind: ["ui"]`, so VS Code runs it in the LOCAL Windows UI host even for a WSL workspace). Every call goes through the
 * runner it is GIVEN — the same seam as `WslCareClient` (the Test-mode fake or the closed runner), never the Windows-Time
 * `runnerFor({ kind: 'real' })` — no shell, `windowsHide` (CREATE_NO_WINDOW), the runner's ceilings.
 *
 * What it asks, and nothing else (each tail is spelt in `verbs.ts` only):
 *
 * - the bundled exe: `--version`, `tasks stage` and `tasks stage --remove`;
 * - the staged exe: `tasks render <watch|archive>`;
 * - either exe: the run reads (`read`) — values checked before a spawn, as `WslCareClient.read` checks them.
 *
 * It never runs `tasks run` (the task's own action, whose supervisor spells `--timer`), never registers anything, and is never
 * elevated, never run from a scheduled task or as SYSTEM: the extension folder is the user's own. A refused start (Smart App
 * Control, say) reaches the runner as Node's error message, which is shown as Windows gave it — never a guessed code.
 */

/** The exe's file name — the one place it is named. */
const EXE_NAME = 'wsl-care.exe';

/** `bin/wsl-care.exe` under the extension's folder. */
const BUNDLED_EXE: readonly [string, string] = ['bin', EXE_NAME];

/** The daemon's staging folder under the profile's LocalAppData (`WindowsHostPaths`: the state folder `wsl-care`, then `bin`). */
const STAGING_SEGMENTS: readonly [string, string] = ['wsl-care', 'bin'];

/**
 * A local disk's absolute path: a drive letter, a colon, a separator. `path.win32.isAbsolute` also says yes to a share
 * (`\\server\share\…`) and to a root without a drive (`\Users\…`) — neither is the user's own local extension folder (own
 * Fable review of the code, note 11).
 */
const DRIVE_ROOT = /^[A-Za-z]:[\\/]/;

/** Which exe a call starts. */
export type ExeSource = { readonly kind: 'bundled' } | { readonly kind: 'staged'; readonly digest: string };

/** The bundled exe. */
export const BUNDLED: ExeSource = { kind: 'bundled' };

/** The staged copy under `digest` (checked when a path is built from it). */
export function staged(digest: string): ExeSource {
  return { kind: 'staged', digest };
}

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
  /**
   * The profile's `%LOCALAPPDATA%` — absolute, on a local drive, in its normal form. The staging folder is `<it>\wsl-care\bin`,
   * derived here, never given. Without it (or with a malformed one) no staged exe is named and no stage is run (the call says why).
   */
  readonly localAppData?: string;
  /** The SHA-256 of a file, lower-case hex — how `tasksStage` learns the digest of the bundled exe; {@link sha256OfFile} unless a test says otherwise. */
  readonly digestOf?: (file: string) => Promise<string>;
  /** How long `tasks stage` / `tasks render` may run, in milliseconds; WR-E2's `windowsTasksStageSeconds` — {@link TASKS_DEFAULT_TIMEOUT_MS} until then. */
  readonly tasksTimeoutMs?: () => number;
  /** How long a run read may run; the run reads' default ceilings until a setting says otherwise. */
  readonly readTimeoutMs?: (read: RunReadName) => number;
}

/** What `--version` of the bundled exe answered: its version line, why it was not started, or why it did not answer. */
export type BundledRead =
  | { readonly kind: 'version'; readonly text: string }
  | { readonly kind: 'unavailable'; readonly reason: string }
  | { readonly kind: 'failed'; readonly reason: string };

function exitedRead(code: number, stdout: Buffer, stderr: Buffer): BundledRead {
  const text = firstLine(stdout);
  if (code !== 0) {
    return { kind: 'failed', reason: `it exited ${code}: ${firstLine(stderr)}` };
  }

  return text === '' ? { kind: 'failed', reason: 'it printed no version' } : { kind: 'version', text };
}

/** The runner's result as one read — pure, so every answer is a unit test. */
export function bundledReadOf(result: ProcessResult): BundledRead {
  return result.kind === 'exited'
    ? exitedRead(signed32(result.code), result.stdout, result.stderr)
    : { kind: 'failed', reason: endingReason(result, 'wslCare.timeouts.versionSeconds') };
}

export class WindowsCareClient {
  constructor(private readonly options: WindowsClientOptions) {}

  /**
   * The exe's absolute path — or why none is started here. A staged digest that is not 64 lower-case hex digits THROWS, before
   * any path is built: it is a programming error of the caller, never a state of the machine.
   */
  exe(source: ExeSource = BUNDLED): string | { readonly reason: string } {
    return source.kind === 'staged' ? this.stagedExe(stagedDigest(source.digest)) : this.bundledExe();
  }

  private bundledExe(): string | { readonly reason: string } {
    const { platform, extension } = this.options;
    if (platform !== 'win32') {
      return { reason: 'the bundled daemon is the Windows build, and this extension host is not Windows' };
    }
    const file = path.win32.join(extension.fsPath, ...BUNDLED_EXE);

    return extension.scheme === 'file' && DRIVE_ROOT.test(file) ? file : { reason: `the extension is not on a local disk (${extension.scheme}:${extension.fsPath})` };
  }

  private stagedExe(digest: string): string | { readonly reason: string } {
    const { platform, localAppData } = this.options;
    if (platform !== 'win32') {
      return { reason: 'the staged daemon copy is the Windows build, and this extension host is not Windows' };
    }

    return localAppData !== undefined && isNormalDrivePath(localAppData)
      ? path.win32.join(localAppData, ...STAGING_SEGMENTS, digest, EXE_NAME)
      : { reason: 'the profile\'s LocalAppData is not known as a normal local drive path, so no staged copy is named' };
  }

  /** `--version` of the bundled exe, through the given seam — nothing started when {@link exe} says why not. */
  async version(): Promise<BundledRead> {
    const file = this.exe();
    if (typeof file !== 'string') {
      return { kind: 'unavailable', reason: file.reason };
    }

    return bundledReadOf(await this.options.runner({ file, args: VERBS.version, timeoutMs: this.options.timeoutMs() }));
  }

  /**
   * `tasks render <task> --json` on the STAGED copy `digest` — read-only: the XML, the summary and whether the copy may be
   * registered. Typed from the exit code AND the JSON; a copy that answers for another digest is not registrable.
   */
  async tasksRender(task: TaskName, digest: string): Promise<RenderResult> {
    const file = this.exe(staged(digest));
    if (typeof file !== 'string') {
      return { kind: 'unavailable', reason: file.reason };
    }
    const timeoutMs = this.tasksTimeout();

    return renderResultOf(await this.options.runner({ file, args: tasksRenderTail(task), timeoutMs }), task, digest, file);
  }

  /**
   * `tasks stage --json` on the BUNDLED exe: it copies itself to the staging folder and reads the copy back. The client computes
   * the SHA-256 of the bundled exe ITSELF (a read, before anything starts) and judges the answer against it: a different digest, a
   * different path than the staged exe it would start, or a build that is not `BUNDLED_DAEMON`, is `provenanceRefused` — never taken
   * for a staged copy. A bundled exe that cannot be read fails the stage before the daemon is asked.
   */
  async tasksStage(): Promise<StageResult> {
    const file = this.exe();
    if (typeof file !== 'string') {
      return { kind: 'unavailable', reason: file.reason };
    }
    const expected = await this.expectedCopy(file);
    if ('kind' in expected) {
      return expected;
    }
    const timeoutMs = this.tasksTimeout();

    return stageResultOf(await this.options.runner({ file, args: TASKS_STAGE_TAIL, timeoutMs }), expected);
  }

  /** What the stage must report: the bundled exe's digest and the staged exe the client would start for it — or why that is unknown. */
  private async expectedCopy(bundled: string): Promise<{ readonly digest: string; readonly path: string } | { readonly kind: 'unavailable' | 'failed'; readonly reason: string }> {
    const digest = await this.digestOfBundled(bundled);
    if (typeof digest !== 'string') {
      return digest;
    }
    if (!STAGED_DIGEST_SHAPE.test(digest)) {
      return { kind: 'failed', reason: 'the digest computed for the bundled exe is not 64 lower-case hex digits' };
    }
    const copy = this.exe(staged(digest));

    return typeof copy === 'string' ? { digest, path: copy } : { kind: 'unavailable', reason: copy.reason };
  }

  /** The SHA-256 of the bundled exe, read by the digest seam — or why it could not be learned. */
  private async digestOfBundled(bundled: string): Promise<string | { readonly kind: 'failed'; readonly reason: string }> {
    try {
      return await (this.options.digestOf ?? sha256OfFile)(bundled);
    } catch (e) {
      return { kind: 'failed', reason: 'the bundled exe could not be read to learn its digest: ' + messageOf(e) };
    }
  }

  /** `tasks stage --remove --json` on the BUNDLED exe: every staged copy removed (one whose exe still runs is kept and named). */
  async tasksStageRemove(): Promise<RemoveResult> {
    const file = this.exe();
    if (typeof file !== 'string') {
      return { kind: 'unavailable', reason: file.reason };
    }
    const timeoutMs = this.tasksTimeout();

    return removeResultOf(await this.options.runner({ file, args: TASKS_REMOVE_TAIL, timeoutMs }));
  }

  /**
   * A run read of the Windows side (`runs show`, `runs`, `logs`, `archive status`, `archive list`…) through `source`'s exe. The
   * values are checked BEFORE anything starts, exactly as `WslCareClient.read` checks them (`runReadChecks.ts`).
   */
  async read(request: WindowsRunRead, source: ExeSource = BUNDLED): Promise<WindowsReadOutcome> {
    const refusal = windowsReadRefusal(request);

    return refusal === undefined ? this.readFrom(request, source) : { kind: 'readRefused', read: request.read, detail: refusal };
  }

  private async readFrom(request: WindowsRunRead, source: ExeSource): Promise<WindowsReadOutcome> {
    const file = this.exe(source);
    if (typeof file !== 'string') {
      return { kind: 'unavailable', read: request.read, reason: file.reason };
    }
    const timeoutMs = this.options.readTimeoutMs?.(request.read) ?? RUN_READ_TIMEOUT_MS[request.read];

    return readOutcomeOf(await this.options.runner({ file, args: runReadTail(request), timeoutMs }), request.read, answerExitsOf(request.read));
  }

  private tasksTimeout(): number {
    return this.options.tasksTimeoutMs?.() ?? TASKS_DEFAULT_TIMEOUT_MS;
  }
}

/** Whether `text` is a path on a local drive that `path.win32.normalize` would leave as it is: no `..`, no `.`, no forward slash, no doubled or trailing separator. */
function isNormalDrivePath(text: string): boolean {
  return DRIVE_ROOT.test(text) && path.win32.normalize(text) === text && !text.endsWith('\\') && !text.split('\\').includes('..');
}

/** Why a request is not one of the five Windows reads, or why its values may not be sent. */
function windowsReadRefusal(request: WindowsRunRead): string | undefined {
  return WINDOWS_READ_NAMES.includes(request.read) ? readRefusal(request) : `${request.read} is not a read of the Windows side`;
}

function messageOf(e: unknown): string {
  return e instanceof Error ? e.message : String(e);
}
