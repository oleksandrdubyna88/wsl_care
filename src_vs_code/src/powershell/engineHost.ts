import type { ProcessRequest, ProcessResult, Runner } from '../process/runner';
import { signed32 } from '../process/runner';
import { safeText } from '../text/safeText';
import { engineChangedSentence, engineOf, MODULES, PWSH_PROBE, pwshFactsFromTags, TAG_HELPER, type Engine, type FileInfoOf, type PwshFacts } from './elevatedEngine';
import { powerShellOf } from './elevatedLauncher';
import { tagsOf } from './taggedAnswer';

/**
 * The engine read (PLAN_pwsh_engine_windows_time.md §11 R3): which PowerShell an elevated Windows Time run starts —
 * PowerShell 7 resolved and signature-checked, else Windows PowerShell 5.1 — from ONE unelevated, read-only probe of its own,
 * never from another story's query. The panel's refresh reads it (the Windows Time part names the engine); *Start Windows
 * Time* and the guard's install / remove ask {@link EngineHost.engine} before their modal and {@link EngineHost.reread} after
 * it, right before the launch. A read that did not answer runs 5.1 — G1's policy, the owner's rule — and its label says
 * PowerShell 7 could not be checked, and why.
 */

/** The probe, alone: the module path pinned first, progress off (research G5), the shared probe, a clean exit. */
export const ENGINE_QUERY = [MODULES, "$ProgressPreference = 'SilentlyContinue'", TAG_HELPER, PWSH_PROBE, 'exit 0'].join('\n');

/** The read-only query, UNELEVATED: the absolute Windows PowerShell with the fixed script — no `runas`, no payload. */
export function engineQueryRequest(env: Readonly<Record<string, string | undefined>>, timeoutMs: number): ProcessRequest | string {
  const powerShell = powerShellOf(env);

  return typeof powerShell === 'string' ? { file: powerShell, args: ['-NoProfile', '-NonInteractive', '-Command', ENGINE_QUERY], timeoutMs } : powerShell.reason;
}

/** One read: the engine it chose, and why PowerShell 7 could not be checked ('' when the read answered). */
export interface EngineRead {
  readonly engine: Engine;
  readonly failure: string;
}

const NOT_ANSWERED: { readonly [K in Exclude<ProcessResult['kind'], 'exited'>]: (r: Extract<ProcessResult, { kind: K }>) => string } = {
  timedOut: (r) => `the read did not answer within ${Math.round(r.timeoutMs / 1000)} s (wslCare.timeouts.engineProbeSeconds)`,
  failedToStart: (r) => `the read could not start: ${r.reason}`,
  signalled: (r) => `the read was stopped (${r.signal})`,
  tooMuchOutput: () => 'the read printed more than the extension reads',
};

/** 5.1, labelled with why PowerShell 7 could not be checked (no label at all when not even Windows PowerShell can be named). */
function failedRead(env: Readonly<Record<string, string | undefined>>, fileInfo: FileInfoOf, why: string): EngineRead {
  const windows = engineOf(env, fileInfo, undefined);

  return { engine: windows.label === '' ? windows : { ...windows, label: `${windows.label} — PowerShell 7 could not be checked: ${why}` }, failure: why };
}

/** Whether the probe printed neither of its two verdicts (it faulted inside its try, or printed nothing). */
function unchecked(facts: PwshFacts): boolean {
  return facts.store === undefined && facts.msi === undefined;
}

function answeredRead(stdout: string, env: Readonly<Record<string, string | undefined>>, fileInfo: FileInfoOf): EngineRead {
  const { tags, twice } = tagsOf(stdout);
  if (twice !== undefined) {
    return failedRead(env, fileInfo, `the read answered a tag twice (${safeText(twice, 60)})`);
  }
  const facts = pwshFactsFromTags(tags);

  return unchecked(facts) ? failedRead(env, fileInfo, 'the read printed no answer') : { engine: engineOf(env, fileInfo, facts), failure: '' };
}

function exitedRead(code: number, stdout: Buffer, env: Readonly<Record<string, string | undefined>>, fileInfo: FileInfoOf): EngineRead {
  return code === 0 ? answeredRead(stdout.toString('utf8'), env, fileInfo) : failedRead(env, fileInfo, `the read exited ${code}`);
}

/** The read's process result as one engine — pure, so every answer is a unit test. */
export function engineReadOf(result: ProcessResult, env: Readonly<Record<string, string | undefined>>, fileInfo: FileInfoOf): EngineRead {
  return result.kind === 'exited' ? exitedRead(signed32(result.code), result.stdout, env, fileInfo) : failedRead(env, fileInfo, (NOT_ANSWERED[result.kind] as (r: ProcessResult) => string)(result));
}

/** Where an elevated flow gets its engine — the host, or a fixed one in a unit test. */
export interface EngineSource {
  /** The last read's engine, reading first when there is none — asked BEFORE the modal. */
  readonly engine: () => Promise<EngineRead>;
  /** A NEW read — asked AFTER the modal, right before the launch. */
  readonly reread: () => Promise<EngineRead>;
  /** The last read, or `undefined` before the first one answered — what the panel names. */
  readonly last: () => EngineRead | undefined;
}

/** What the Windows Time part says of its engines (D6): the elevated runs' program, and the guard's task on 5.1 as SYSTEM. */
export function guardEngineLine(read: EngineRead | undefined): string {
  const program = read === undefined ? 'checking which PowerShell…' : `${read.engine.label}.`;

  return `Elevated runs here (Start Windows Time, install / remove the guard): ${program} The guard's task runs as SYSTEM in Windows PowerShell 5.1.`;
}

/**
 * Why the launch must not go ahead with `before` (the engine the modal named) after the read taken right before it, or
 * `undefined` when it may: a read that did not answer says so (§11 R7) — never "the program changed" — and a read that chose
 * another program says that (G1's sentence). Both reads failing name the same 5.1, which runs: nothing is refused for a probe.
 */
export function engineProblem(before: Engine, now: EngineRead): string | undefined {
  if (now.engine.file === before.file) {
    return undefined;
  }

  return now.failure === '' ? engineChangedSentence(before, now.engine) : `Nothing ran: the read of the elevated program right before the launch did not answer (${now.failure}). Press the button again.`;
}

export interface EngineHostDeps {
  readonly env: Readonly<Record<string, string | undefined>>;
  /** Node's `lstat` of a program the launch might elevate (the MSI pwsh), or `undefined` when it cannot be read. */
  readonly fileInfo: FileInfoOf;
  /** `wslCare.timeouts.engineProbeSeconds`, in milliseconds. */
  readonly timeoutMs: () => number;
  /** The UNELEVATED read — the real runner, or a recorder in Test mode. */
  readonly query: Runner;
}

/**
 * The last read and ONE read in flight. {@link refresh} shares a read in flight (the panel never queues reads);
 * {@link reread} never does — it waits for one in flight and then asks Windows AGAIN (§11 R6), because the launch must see a
 * read taken after its modal closed.
 */
export class EngineHost {
  private latest: EngineRead | undefined;
  private reading: Promise<EngineRead> | undefined;
  private readonly listeners = new Set<() => void>();

  constructor(private readonly deps: EngineHostDeps) {}

  /** The last read, or `undefined` before the first one answered. */
  last(): EngineRead | undefined {
    return this.latest;
  }

  /** The last read's engine — reading first when there is none yet (§11 R3: the first press never refuses for want of a read). */
  engine(): Promise<EngineRead> {
    return this.latest === undefined ? this.refresh() : Promise.resolve(this.latest);
  }

  /** A read for the panel: the one in flight, or a new one. */
  refresh(): Promise<EngineRead> {
    return this.reading ?? this.start();
  }

  /** A NEW read, started after any read in flight has answered — the one a launch checks its engine against. */
  async reread(): Promise<EngineRead> {
    await this.reading;
    return this.start();
  }

  onChange(listener: () => void): () => void {
    this.listeners.add(listener);
    return () => { this.listeners.delete(listener); };
  }

  private start(): Promise<EngineRead> {
    const read = this.readOnce();
    this.reading = read;
    const done = (): void => {
      if (this.reading === read) {
        this.reading = undefined;
      }
      this.listeners.forEach((listener) => listener());
    };
    void read.then(done, done);
    return read;
  }

  private async readOnce(): Promise<EngineRead> {
    const { env, fileInfo } = this.deps;
    const request = engineQueryRequest(env, this.deps.timeoutMs());
    const read = typeof request === 'string' ? failedRead(env, fileInfo, request) : await this.answered(request);
    this.latest = read;
    return read;
  }

  /** The runner's answer as a read — a runner that throws is a read that could not start, never an unhandled rejection. */
  private async answered(request: ProcessRequest): Promise<EngineRead> {
    try {
      return engineReadOf(await this.deps.query(request), this.deps.env, this.deps.fileInfo);
    } catch (e: unknown) {
      return failedRead(this.deps.env, this.deps.fileInfo, `the read could not start: ${String(e)}`);
    }
  }
}
