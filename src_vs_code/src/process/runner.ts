import * as childProcess from 'node:child_process';

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
}

export type ProcessResult =
  | { readonly kind: 'exited'; readonly code: number; readonly stdout: Buffer; readonly stderr: Buffer }
  | { readonly kind: 'signalled'; readonly signal: string; readonly stdout: Buffer; readonly stderr: Buffer }
  | { readonly kind: 'timedOut'; readonly timeoutMs: number; readonly stdout: Buffer; readonly stderr: Buffer }
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

type Stream = 'stdout' | 'stderr';

/** One child's run: the streams it wrote, and how the run ended. */
class Run {
  private readonly chunks: Record<Stream, Buffer[]> = { stdout: [], stderr: [] };
  private readonly sizes: Record<Stream, number> = { stdout: 0, stderr: 0 };
  private ending: ProcessResult | undefined;

  constructor(private readonly child: childProcess.ChildProcess, private readonly done: (result: ProcessResult) => void) {}

  take(stream: Stream, chunk: Buffer): void {
    this.sizes[stream] += chunk.length;
    if (this.sizes[stream] > OUTPUT_LIMITS[stream]) {
      this.stop({ kind: 'tooMuchOutput', stream, limitBytes: OUTPUT_LIMITS[stream] });
      return;
    }
    this.chunks[stream].push(chunk);
  }

  timedOut(timeoutMs: number): void {
    this.stop({ kind: 'timedOut', timeoutMs, ...this.streams() });
  }

  /** The child ended — by itself, or after the runner had already decided how this run ends. */
  ended(code: number | null, signal: NodeJS.Signals | null): void {
    this.done(this.ending ?? natural(code, signal, this.streams()));
  }

  failed(error: Error): void {
    this.done(this.ending ?? { kind: 'failedToStart', reason: error.message });
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
    this.child.kill();
    setTimeout(() => this.done(ending), KILL_GRACE_MS).unref();
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
  const run = new Run(child, ending.settle);
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
