import type { Verb } from './verbs';

/** A JSON object as the client hands it on: keys it does not know are kept and ignored, never an error. */
export type JsonObject = Readonly<Record<string, unknown>>;

/** One threshold verdict of `status --json` (E5.S0), as far as the client reads it. */
export interface Verdict {
  readonly id: string;
  readonly level: 'ok' | 'warn' | 'critical' | 'unknown';
  readonly value: string | undefined;
  readonly limit: string | undefined;
  readonly reason: string | undefined;
}

/** What the daemon's version text says (plan §6: `x.y.z(+sha)?`, `unknown` for an unstamped build). */
export type DaemonVersion =
  | { readonly kind: 'release'; readonly text: string; readonly parts: readonly [number, number, number] }
  | { readonly kind: 'development'; readonly text: string }
  | { readonly kind: 'unstamped' }
  | { readonly kind: 'unrecognised'; readonly text: string }
  | { readonly kind: 'notRead'; readonly reason: string };

/** `status --json`: the body, plus the two members added in E5.S0 — both OPTIONAL (plan §6 compatibility rule). */
export interface StatusAnswer {
  readonly verb: 'status';
  readonly schemaVersion: number;
  readonly productVersion: string | undefined;
  readonly verdicts: readonly Verdict[] | undefined;
  readonly body: JsonObject;
}

/** `preview --all --json` or `doctor --json`: the body, its schema checked. */
export interface BodyAnswer {
  readonly verb: 'preview' | 'doctor';
  readonly schemaVersion: number;
  readonly body: JsonObject;
}

/** `--version`: the text the daemon printed, read. */
export interface VersionAnswer {
  readonly verb: 'version';
  readonly version: DaemonVersion;
}

export type Answer = StatusAnswer | BodyAnswer | VersionAnswer;

/**
 * Everything that can stand between a verb and its answer. Each is a separate kind because each wants a different
 * sentence in the panel; none of them is "an error happened".
 */
export type Failure =
  /** The extension runs on the Windows side only (`extensionKind: ["ui"]`); anywhere else nothing is started. */
  | { readonly kind: 'notWindows'; readonly platform: string }
  /** `%SystemRoot%\System32\wsl.exe` cannot be named (no `SystemRoot`, or not a drive path) — PATH is never searched. */
  | { readonly kind: 'wslMissing'; readonly detail: string }
  /** The configured distribution was refused BEFORE it reached a `-d`: out of pattern, or not in `wsl.exe --list`. */
  | { readonly kind: 'distroRefused'; readonly distro: string; readonly reason: string }
  /** The setting is empty and `wsl.exe -l -v` marks no single default with `*`. */
  | { readonly kind: 'noDefaultDistro'; readonly detail: string }
  /** The distribution is not running: no `-d` call was made, so polling never starts the VM (plan §15f #8, §15g m3). */
  | { readonly kind: 'stopped'; readonly distro: string }
  /** `wsl.exe` itself refused or could not start; its own (UTF-16LE) message. */
  | { readonly kind: 'wslFailed'; readonly message: string }
  /** The measured signature of a missing `/opt/wsl-care/bin/wsl-care` — and nothing else — reads as "not installed". */
  | { readonly kind: 'notInstalled'; readonly distro: string }
  /** The binary needs a newer glibc than the distribution has (Ubuntu ≥ 24.04 / glibc 2.39, plan §15f #5). */
  | { readonly kind: 'unsupportedDistro'; readonly distro: string; readonly detail: string }
  /** Exit 2: the daemon refused the arguments; only its `wsl-care:` lines. */
  | { readonly kind: 'refused'; readonly messages: readonly string[] }
  /** Exit 70: a defect in the daemon. */
  | { readonly kind: 'internalDefect'; readonly messages: readonly string[] }
  /** Exit 130: stopped by a signal before it finished. */
  | { readonly kind: 'interrupted' }
  /** The verb's ceiling passed; `wsl.exe` was killed (which ends the Linux process — measured). */
  | { readonly kind: 'timedOut'; readonly timeoutMs: number }
  /** `preview` timed out with more running containers than its ceiling assumes (§15h #1). */
  | { readonly kind: 'previewTooManyContainers'; readonly containers: number; readonly timeoutMs: number }
  /** Any other ending: the exit code and only the daemon's `wsl-care:` lines. */
  | { readonly kind: 'unknownFailure'; readonly code: number | undefined; readonly messages: readonly string[] }
  /** Exit 0, but the answer is not what the verb promises (not JSON, no `schemaVersion`, too large). */
  | { readonly kind: 'unparseable'; readonly detail: string }
  /** A `schemaVersion` major this extension does not know: only THIS verb's view is blanked (plan §6). */
  | { readonly kind: 'needsNewerExtension'; readonly schemaVersion: number }
  /** A released daemon older than `MIN_DAEMON_FOR_RENDER`. */
  | { readonly kind: 'daemonTooOld'; readonly version: string; readonly minimum: string };

/** What one call of a verb ends in: its answer with the daemon version it was judged against, or why not. */
export type VerbOutcome =
  | { readonly kind: 'answered'; readonly verb: Verb; readonly distro: string; readonly answer: Answer; readonly daemonVersion: DaemonVersion }
  | (Failure & { readonly verb: Verb });
