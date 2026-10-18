import * as childProcess from 'node:child_process';

import { endTree, type TreeKillDeps } from './treeKill';

/**
 * The runner seam — the ONE module of the extension that starts a process (`structure.test.ts` fails when any other
 * shipped module imports `child_process`). It takes `{file, args}` and starts exactly that file with exactly those
 * arguments, never through a shell (`shell: false`): what the client built is what the program receives, so nothing in
 * a distribution name or a daemon word can be expanded, split or redirected on the way (`common.security`: exe + argv).
 *
 * <p>Every wait has a ceiling (`common.reliability`): the request's `timeoutMs`, then `child.kill()`, then at most
 * `KILL_GRACE_MS` for the child to go. For `wsl.exe` that ends the Linux process too — measured 2026-10-03: killing the
 * `wsl.exe` of `--exec sleep 37.123` took the `sleep` with it (the relay's hang-up; a process that ignores SIGHUP is
 * reparented to PID 1 instead), research/2026-10-03_wsl_exe_facts.md. Output is bounded: past `OUTPUT_LIMITS` the child
 * is killed and the answer says so, rather than buffering without end.</p>
 *
 * <p>The streams are handed back as BYTES. Their encoding is the caller's decision: `wsl.exe` writes UTF-16LE, the
 * Linux program behind it UTF-8 (measured), and only the client knows which call it made.</p>
 */

export interface ProcessRequest {
  /** The program, by absolute path — the client passes `%SystemRoot%\System32\wsl.exe`, never a bare name. */
  readonly file: string;
  readonly args: readonly string[];
  readonly timeoutMs: number;
  /** Variables added to the inherited environment. */
  readonly env?: Readonly<Record<string, string>>;
  /**
   * Bytes for the child's stdin, written and then ENDED (E6.S2, plan §15j M2) — A4's shown list for `act … --only -`.
   * Absent: the child's stdin is closed from the start, so it reads an immediate end and nothing waits on it.
   */
  readonly stdin?: Buffer;
  /**
   * Variables taken OUT of the child's environment, whatever the inherited one or `env` says — matched case-insensitively,
   * as Windows names them. The root calls take out `WSLENV` (E6.S2 review S2): it is how `wsl.exe` carries Windows
   * variables into the distribution, and the Windows user's choice of them must not shape a root daemon's environment.
   */
  readonly withoutEnv?: readonly string[];
  /**
   * When the run ends by its ceiling (or its output bound), end the child's whole PROCESS TREE, not only the child (WR-E2,
   * PLAN_windows_runner.md plan round 4fc3f328 finding 8, the Opus own review B1/M2/M3). It is the family kill rule — the host's own
   * child only, each process judged by pid and creation time, never by image name — done by ONE fixed, verifying PowerShell
   * (`process/treeKill.ts`) while the child is still running; the child is killed plainly after it, and the `timedOut` result says
   * through `treeEnded` whether the tree was VERIFIED ended. Off Windows, or without a usable `SystemRoot`, only the child is killed
   * and `treeEnded` is false. Absent: the child alone, as every other caller always had it (a timeout's `treeEnded` is false there too).
   */
  readonly killTree?: boolean;
}

export type ProcessResult =
  | { readonly kind: 'exited'; readonly code: number; readonly stdout: Buffer; readonly stderr: Buffer }
  | { readonly kind: 'signalled'; readonly signal: string; readonly stdout: Buffer; readonly stderr: Buffer }
  /** `treeEnded`: true only when a tree kill was asked for and VERIFIED (the root and every descendant gone); false otherwise, no tree kill asked included. */
  | { readonly kind: 'timedOut'; readonly timeoutMs: number; readonly stdout: Buffer; readonly stderr: Buffer; readonly treeEnded: boolean }
  | { readonly kind: 'tooMuchOutput'; readonly stream: 'stdout' | 'stderr'; readonly limitBytes: number }
  | { readonly kind: 'failedToStart'; readonly reason: string };

export type Runner = (request: ProcessRequest) => Promise<ProcessResult>;

/** The most a child may write before it is killed: a `preview` answer is tens of kilobytes, a status answer ~70 KB. */
export const OUTPUT_LIMITS = { stdout: 16 * 1024 * 1024, stderr: 1024 * 1024 } as const;

/** How long a killed child gets to exit before the runner stops waiting for it. */
export const KILL_GRACE_MS = 2_000;

/** Why the closed runner started nothing. */
export const CLOSED_REASON = 'the extension runs in Test mode without a fake wsl.exe: nothing is started (fail closed)';

/**
 * An exit code as the signed 32-bit value the program meant. Windows reports exit codes unsigned, so `wsl.exe`
 * refusing (-1) reaches Node as 4294967295 — measured 2026-10-03.
 */
export function signed32(code: number): number {
  return code > 0x7fffffff ? code - 0x100000000 : code;
}

/** How long the verifying tree kill may run before it is stopped and the tree counts as not ended: a PowerShell start and one CIM read, on a loaded machine. */
export const TREE_KILL_BOUND_MS = 10_000;

/** A margin taken off this host's start time, which is computed from its uptime and so only approximate. */
const HOST_START_MARGIN_MS = 5_000;

/** The tree kill's real parts: this host's pid and start, the environment, and the one place a killer process is started. */
function productionTreeKill(): TreeKillDeps {
  return {
    env: process.env,
    hostPid: process.pid,
    hostStartedAtMs: Math.round(Date.now() - process.uptime() * 1000) - HOST_START_MARGIN_MS,
    boundMs: TREE_KILL_BOUND_MS,
    spawnKiller: (request) => childProcess.spawn(request.file, [...request.args], { shell: false, windowsHide: true, stdio: 'ignore' }),
  };
}

type Stream = 'stdout' | 'stderr';

/** What a run does with its child, and how a tree kill is made. */
export interface RunKill {
  readonly killTree: boolean;
  readonly treeKill: TreeKillDeps;
  /** How long a killed child gets to close before the run is completed without it; {@link KILL_GRACE_MS} unless a test shortens it. */
  readonly graceMs?: number;
}

/** The child as a run needs it: its pid, whether it has ended, and a plain kill. */
type Child = Pick<childProcess.ChildProcess, 'pid' | 'exitCode' | 'signalCode' | 'kill'>;

/** A timeout carries the tree kill's verdict; every other ending is left as it is. */
function withVerdict(ending: ProcessResult, treeEnded: boolean): ProcessResult {
  return ending.kind === 'timedOut' ? { ...ending, treeEnded } : ending;
}

/** One child's run: the streams it wrote, and how the run ended. */
export class Run {
  private readonly chunks: Record<Stream, Buffer[]> = { stdout: [], stderr: [] };
  private readonly sizes: Record<Stream, number> = { stdout: 0, stderr: 0 };
  private ending: ProcessResult | undefined;
  /** The tree kill in progress, when one was asked for: nothing settles before its verdict (bounded by `boundMs`). */
  private tree: Promise<void> | undefined;
  /** A run completes ONCE: the child's close, the grace timer and a failure to start all go through {@link complete}. */
  private completed = false;
  private grace: NodeJS.Timeout | undefined;

  constructor(private readonly child: Child, private readonly done: (result: ProcessResult) => void, private readonly kill: RunKill = { killTree: false, treeKill: productionTreeKill() }) {}

  take(stream: Stream, chunk: Buffer): void {
    this.sizes[stream] += chunk.length;
    if (this.sizes[stream] > OUTPUT_LIMITS[stream]) {
      this.stop({ kind: 'tooMuchOutput', stream, limitBytes: OUTPUT_LIMITS[stream] });
      return;
    }
    this.chunks[stream].push(chunk);
  }

  timedOut(timeoutMs: number): void {
    this.stop({ kind: 'timedOut', timeoutMs, treeEnded: false, ...this.streams() });
  }

  /** The child ended — by itself, or after the runner had already decided how this run ends. */
  ended(code: number | null, signal: NodeJS.Signals | null): void {
    this.settle(() => natural(code, signal, this.streams()));
  }

  failed(error: Error): void {
    this.settle(() => ({ kind: 'failedToStart', reason: error.message }));
  }

  /** The run is over: with the ending decided earlier, else with `otherwise` — but never before a tree kill in progress has its verdict. */
  private settle(otherwise: () => ProcessResult): void {
    const finish = (): void => { this.complete(this.ending ?? otherwise()); };
    if (this.tree === undefined) {
      finish();
    } else {
      void this.tree.then(finish);
    }
  }

  /** Hands the result to `done` the first time only, and stops the grace timer that would have handed it again. */
  private complete(result: ProcessResult): void {
    if (this.completed) {
      return;
    }
    this.completed = true;
    clearTimeout(this.grace);
    this.done(result);
  }

  private streams(): { stdout: Buffer; stderr: Buffer } {
    return { stdout: Buffer.concat(this.chunks.stdout), stderr: Buffer.concat(this.chunks.stderr) };
  }

  /** Decide the ending, kill the child, and stop waiting after the grace even if it never reports back. */
  private stop(ending: ProcessResult): void {
    if (this.ending !== undefined) {
      return;
    }
    this.ending = ending;
    if (this.kill.killTree) {
      // The tree first, while its root is still running (the walk starts from it); then the plain kill as the net.
      this.tree = endTree(this.child, this.kill.treeKill).then((treeEnded) => { this.ending = withVerdict(ending, treeEnded); this.killChild(); });
    } else {
      this.killChild();
    }
  }

  /** The plain kill, and — whatever the child does after it — the end of the wait after the grace. */
  private killChild(): void {
    this.child.kill();
    this.grace = setTimeout(() => { this.complete(this.ending ?? { kind: 'failedToStart', reason: 'the run had no ending' }); }, this.kill.graceMs ?? KILL_GRACE_MS);
    this.grace.unref();
  }
}

function natural(code: number | null, signal: NodeJS.Signals | null, streams: { stdout: Buffer; stderr: Buffer }): ProcessResult {
  return code === null ? { kind: 'signalled', signal: signal ?? 'unknown', ...streams } : { kind: 'exited', code: signed32(code), ...streams };
}

function start(request: ProcessRequest): childProcess.ChildProcess {
  return childProcess.spawn(request.file, [...request.args], {
    shell: false,
    windowsHide: true,
    stdio: [request.stdin === undefined ? 'ignore' : 'pipe', 'pipe', 'pipe'],
    env: environmentOf(request),
  });
}

/** The inherited environment plus `env`, minus every `withoutEnv` name (case-insensitively). */
function environmentOf(request: ProcessRequest): NodeJS.ProcessEnv {
  const without = new Set((request.withoutEnv ?? []).map((name) => name.toUpperCase()));

  return Object.fromEntries(Object.entries({ ...process.env, ...request.env }).filter(([name]) => !without.has(name.toUpperCase())));
}

/**
 * Hand the child its stdin and END it — the end is what tells `act … --only -` the list is complete (the daemon refuses a
 * list with no end after 10 s, plan §15j M2) — measured through `wsl.exe` 2026-10-04: 650 000 bytes (a full 10 000-name
 * list) arrive byte for byte with the end, exit 0, ~250 ms, also under `WSL_UTF8=1` (research/2026-10-03_wsl_exe_facts.md row 20; the same with
 * `-u root` is an E6 live-gate item). A child that exits without reading everything makes the pipe fail
 * (EPIPE); that failure is not an outcome of its own: the child's exit — its refusal with a reason — is the answer the
 * caller reads, so the stream's error is taken here, where an unhandled one would end the extension host.
 */
function feed(child: childProcess.ChildProcess, stdin: Buffer | undefined): void {
  if (stdin === undefined || child.stdin === null) {
    return;
  }
  child.stdin.on('error', () => undefined);
  child.stdin.end(stdin);
}

/** Resolve exactly once, and stop the ceiling's timer when it does. */
function once(resolve: (result: ProcessResult) => void): { settle: (result: ProcessResult) => void; arm: (timer: NodeJS.Timeout) => void } {
  let settled = false;
  let timer: NodeJS.Timeout | undefined;

  return {
    arm: (t) => { timer = t; },
    settle: (result) => {
      if (!settled) {
        settled = true;
        clearTimeout(timer);
        resolve(result);
      }
    },
  };
}

function watch(child: childProcess.ChildProcess, request: ProcessRequest, resolve: (result: ProcessResult) => void): void {
  const ending = once(resolve);
  const run = new Run(child, ending.settle, { killTree: request.killTree === true, treeKill: productionTreeKill() });
  ending.arm(setTimeout(() => run.timedOut(request.timeoutMs), request.timeoutMs));
  child.stdout?.on('data', (chunk: Buffer) => run.take('stdout', chunk));
  child.stderr?.on('data', (chunk: Buffer) => run.take('stderr', chunk));
  child.on('error', (error) => run.failed(error));
  child.on('close', (code, signal) => run.ended(code, signal));
  feed(child, request.stdin);
}

function reasonOf(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

/** The real runner: starts `request.file` with `request.args`, no shell, bounded in time and output. */
export const spawnRunner: Runner = (request) =>
  new Promise((resolve) => {
    try {
      watch(start(request), request, resolve);
    } catch (error) {
      resolve({ kind: 'failedToStart', reason: reasonOf(error) });
    }
  });

/**
 * A runner that starts a NODE SCRIPT in place of the requested program: `node <script> <args…>` — how the strict fake
 * `wsl.exe` is started in tests (a `.cmd` cannot be spawned without a shell on current Node, plan §15g M6). The
 * requested program is handed to the script as `WSL_CARE_FAKE_REQUESTED_FILE`, so the fake can refuse a start that is
 * not the absolute System32 `wsl.exe`. `ELECTRON_RUN_AS_NODE=1` makes the extension host's own executable behave as
 * Node, which is what `process.execPath` is inside VS Code.
 */
export function nodeScriptRunner(script: string, env: Readonly<Record<string, string>> = {}): Runner {
  return (request) =>
    spawnRunner({
      file: process.execPath,
      args: [script, ...request.args],
      timeoutMs: request.timeoutMs,
      env: { ...env, ...request.env, ELECTRON_RUN_AS_NODE: '1', WSL_CARE_FAKE_REQUESTED_FILE: request.file },
      ...(request.stdin === undefined ? {} : { stdin: request.stdin }),
      ...(request.withoutEnv === undefined ? {} : { withoutEnv: request.withoutEnv }),
    });
}

/** The runner of a Test-mode extension that was given no fake: it starts nothing, ever. */
export const closedRunner: Runner = () => Promise.resolve({ kind: 'failedToStart', reason: CLOSED_REASON });
