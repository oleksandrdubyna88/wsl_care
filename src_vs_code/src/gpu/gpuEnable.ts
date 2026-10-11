import type { DurableStore } from '../cleanup/journal';
import type { ProcessRequest, ProcessResult, Runner } from '../process/runner';
import { signed32 } from '../process/runner';
import { noticeText } from '../text/safeText';
import { CONFIRM_LABEL, EXIT, type FixPrompt } from '../windowsTime/windowsTimeFix';
import { clearGpuPending, PENDING_GRACE_MS, readGpuPending, writeGpuPending } from './gpuPending';
import { engineChangedSentence, engineOf, withStoreNote, type Engine, type FileInfoOf, type PwshFacts } from '../powershell/elevatedEngine';
import { DISABLED_CODE, ENABLE_EXIT, enableRequestFor, enableScript } from './gpuScripts';
import { canEnable, meaningOf, NOT_ASKED, pwshFactsOf, type Adapter, type GpuState } from './gpuState';

/**
 * *Enable <GPU>* (PLAN_gpu_health.md D4, D5) in the order that makes it safe: the adapter is the host's OWN reading, chosen
 * by an index; the request is built (or the reason none can be is told); a modal shows the exact elevated script, the
 * device and the engine; ONLY its confirm — and a SECOND look at the pending record, after the modal — persists the pending
 * run and starts the ONE elevated PowerShell; then Windows is read again and the outcome is worded from what it NOW says
 * about that device, and names the engine that ran. Nothing runs on a decline, while another enable stands, or for an
 * adapter that is not at code 22.
 *
 * <p><b>The pending record is advisory coordination, not a lock</b> (code round 2; `common.reliability` — a lock names its
 * atomic operation): `globalState` has no compare-and-swap, so two windows that both pass the check and both confirm within
 * the same instant can both launch — two UAC prompts, and the elevated script's own re-check (still code 22) makes the second
 * change nothing. The second look after the modal closes the long window (the modal's open time), as the Windows Time
 * guard's flow does; the residual is the instant between that look and the write. An atomic claim (a directory created
 * without `recursive` under the shared storage) is the epic's follow-up, named in the plan.</p>
 */

/** What the launcher's exit said — before Windows is read again. */
export type EnableExit =
  | { readonly kind: 'ran' }
  | { readonly kind: 'declined' }
  | { readonly kind: 'nothingDone'; readonly why: string }
  /** `launchFailed`: the launcher's exit 13 — the elevated program itself could not be started. */
  | { readonly kind: 'failed'; readonly sentence: string; readonly launchFailed: boolean }
  | { readonly kind: 'timedOut'; readonly timeoutMs: number };

const NOTHING_DONE: Readonly<Record<number, string>> = {
  [ENABLE_EXIT.absent]: 'Windows no longer lists that device',
  [ENABLE_EXIT.notDisplay]: 'that device is not a display adapter',
  [ENABLE_EXIT.notDisabled]: 'that device is no longer disabled',
};

const FAILED: Readonly<Record<number, string>> = {
  [ENABLE_EXIT.importFailed]: 'the elevated PowerShell could not load its PnpDevice module',
  [ENABLE_EXIT.enableFailed]: 'Enable-PnpDevice failed',
  [EXIT.launchFailed]: 'the elevated PowerShell could not be started',
};

const SETTLED: Readonly<Record<number, EnableExit>> = { [ENABLE_EXIT.done]: { kind: 'ran' }, [EXIT.declined]: { kind: 'declined' } };

function notRanOf(code: number): EnableExit {
  const why = NOTHING_DONE[code];

  return why === undefined ? { kind: 'failed', sentence: FAILED[code] ?? `the elevated PowerShell exited ${code}`, launchFailed: code === EXIT.launchFailed } : { kind: 'nothingDone', why };
}

/** A failed exit's sentence, with the Store note when the elevated program itself could not be started and it was the package. */
function failedWords(exit: Extract<EnableExit, { kind: 'failed' }>, engine: Engine): string {
  return withStoreNote(exit.sentence, exit.launchFailed, engine);
}

function exitedOf(code: number): EnableExit {
  return SETTLED[code] ?? notRanOf(code);
}

const OF_RESULT: { readonly [K in ProcessResult['kind']]: (r: Extract<ProcessResult, { kind: K }>) => EnableExit } = {
  exited: (r) => exitedOf(signed32(r.code)),
  timedOut: (r) => ({ kind: 'timedOut', timeoutMs: r.timeoutMs }),
  failedToStart: (r) => ({ kind: 'failed', sentence: `the PowerShell launcher could not start: ${r.reason}`, launchFailed: false }),
  signalled: (r) => ({ kind: 'failed', sentence: `the PowerShell launcher was stopped (${r.signal})`, launchFailed: false }),
  tooMuchOutput: () => ({ kind: 'failed', sentence: 'the PowerShell launcher printed more than the extension reads', launchFailed: false }),
};

export function enableExitOf(result: ProcessResult): EnableExit {
  return (OF_RESULT[result.kind] as (r: ProcessResult) => EnableExit)(result);
}

/** The device as Windows NOW reports it, found by the id the enable acted on. */
function afterOf(state: GpuState, id: string): Adapter | undefined {
  return state.kind === 'read' ? state.adapters.find((a) => a.id === id) : undefined;
}

/** What Windows says now, in words — the part of every sentence after a run. */
interface Told {
  readonly sentence: string;
  readonly failed: boolean;
}

/** What the device's code is now, in words. */
function codeWords(name: string, code: number): Told {
  if (code === 0) {
    return { sentence: `${name} is enabled — code 0. Start Ollama again so it finds the GPU.`, failed: false };
  }

  return code === DISABLED_CODE
    ? { sentence: `${name} is still disabled (code ${DISABLED_CODE}).`, failed: true }
    : { sentence: `${name} is enabled, but still not working (code ${code}: ${meaningOf(code)}) — restart Windows.`, failed: true };
}

/** What Windows says now, in words — the part of every sentence after a run. */
function nowWords(name: string, after: Adapter | undefined, state: GpuState): Told {
  if (state.kind === 'unknown') {
    return { sentence: `Whether ${name} works now could not be read: ${state.reason}.`, failed: true };
  }

  return after === undefined ? { sentence: `Windows no longer lists ${name}.`, failed: true } : codeWords(name, after.code);
}

/**
 * The outcome, from the launcher's exit and the read that followed it (D4), naming the ENGINE that ran elevated (the owner,
 * 2026-10-10: PowerShell 7 by its MSI or Store package path, or Windows PowerShell 5.1) — pure, so every row is a unit test.
 */
export function enableSentence(adapter: Adapter, exit: EnableExit, state: GpuState, engine: Engine): { readonly sentence: string; readonly failed: boolean } {
  const by = engine.label === '' ? '' : ` Elevated program: ${engine.label}.`;
  const now = nowWords(adapter.name, afterOf(state, adapter.id), state);
  const lead: { readonly [K in EnableExit['kind']]: (e: Extract<EnableExit, { kind: K }>) => { readonly text: string; readonly failed: boolean } } = {
    ran: () => ({ text: '', failed: false }),
    declined: () => ({ text: 'Nothing was changed: the UAC prompt was declined.', failed: false }),
    nothingDone: (e) => ({ text: `Nothing was done: ${e.why}.`, failed: false }),
    failed: (e) => ({ text: `Enabling ${adapter.name} failed: ${failedWords(e, engine)}.`, failed: true }),
    timedOut: (e) => ({ text: `The enable did not finish within ${Math.round(e.timeoutMs / 1000)} s (wslCare.timeouts.gpuEnableSeconds) — if the UAC prompt is still open, answering it still runs it.`, failed: true }),
  };
  const head = (lead[exit.kind] as (e: EnableExit) => { readonly text: string; readonly failed: boolean })(exit);
  if (exit.kind === 'declined') {
    return { sentence: noticeText(head.text + by), failed: false };
  }

  return { sentence: noticeText([head.text, now.sentence].filter((t) => t !== '').join(' ') + by), failed: head.failed || now.failed };
}

export function enablePrompt(adapter: Adapter, settleSeconds: number, elevated: string): FixPrompt {
  return {
    message: `Enable ${adapter.name}?`,
    detail: [
      `Windows reports it as disabled (code ${DISABLED_CODE}). One PowerShell runs ELEVATED — Windows asks you first (UAC) — and runs exactly this:`,
      `The elevated program: ${elevated}`,
      '',
      enableScript(adapter.id, settleSeconds),
      '',
      `The device: ${adapter.name} (${adapter.id}). Nothing else on Windows is changed. Afterwards the display adapters are read again and the panel says what Windows reports.`,
    ].join('\n'),
    confirm: CONFIRM_LABEL,
  };
}

export interface EnableDeps {
  readonly env: Readonly<Record<string, string | undefined>>;
  /** Node's `lstat` of a program the enable might elevate (the MSI pwsh), or `undefined` when it cannot be read. */
  readonly fileInfo: FileInfoOf;
  /** The two PowerShell 7 facts of the host's LAST read of Windows (the query's `pwsh.*` tags), or `undefined`. */
  readonly pwsh: PwshFacts | undefined;
  readonly timeoutMs: () => number;
  /** `wslCare.gpu.enableSettleSeconds`: how long the elevated script waits after the enable before it ends. */
  readonly settleSeconds: () => number;
  readonly confirm: (prompt: FixPrompt) => Promise<boolean>;
  readonly run: Runner;
  readonly report: (message: string, failed: boolean) => void;
  readonly durable: DurableStore;
  readonly nowUtcMs: () => number;
  /** Reads Windows again and answers what it now says (the host's query). */
  readonly reread: () => Promise<GpuState>;
}

export type EnableResult = 'notOffered' | 'busy' | 'notStarted' | 'declined' | 'ran' | 'stale';

/** An adapter the enable may act on: present in the host's reading, at code 22, with an id it passes. */
function offered(adapter: Adapter | undefined): adapter is Adapter {
  return adapter !== undefined && canEnable(adapter);
}

/** Whether an enable already stands — this window's or another's — told when it does. */
function busy(deps: EnableDeps): boolean {
  const pending = readGpuPending(deps.durable, deps.nowUtcMs());
  if (pending !== undefined) {
    deps.report(noticeText(`An enable of ${pending.name} is already waiting for its UAC prompt (or its end) — nothing else is started.`), false);
  }

  return pending !== undefined;
}

/** What the modal shows and the run uses — from ONE look at the engine and ONE at the settle setting (own review 2, F1). */
interface Ready {
  readonly adapter: Adapter;
  readonly request: ProcessRequest;
  readonly engine: Engine;
  readonly settle: number;
}

/** Why nothing may start, or the one request — checked in this order: offered, not already running, buildable. */
function precondition(adapter: Adapter | undefined, deps: EnableDeps, timeoutMs: number): { readonly stop: EnableResult } | Ready {
  if (!offered(adapter)) {
    deps.report(noticeText('No disabled GPU is at that place any more — Windows is read again.'), false);
    return { stop: 'notOffered' };
  }
  if (busy(deps)) {
    return { stop: 'busy' };
  }
  const engine = engineOf(deps.env, deps.fileInfo, deps.pwsh);
  const settle = deps.settleSeconds();
  const request = enableRequestFor(deps.env, adapter.id, timeoutMs, engine, settle);

  return typeof request === 'string' ? notStarted(adapter, request, deps) : { adapter, request, engine, settle };
}

function notStarted(adapter: Adapter, reason: string, deps: EnableDeps): { readonly stop: EnableResult } {
  deps.report(noticeText(`${adapter.name} could not be enabled from here: ${reason}.`), true);
  return { stop: 'notStarted' };
}

/**
 * The confirmed run: the elevated program verified AGAIN from a fresh read of Windows (code round 3; 2026-10-11: that read
 * resolves the Store package and checks its signature right before the launch — a program other than the one the modal
 * named starts nothing), the pending record, the ONE elevated launcher, the read after it, and the sentence naming the engine.
 */
async function launched(adapter: Adapter, request: ProcessRequest, engine: Engine, deps: EnableDeps, timeoutMs: number): Promise<EnableResult> {
  const now = engineOf(deps.env, deps.fileInfo, pwshFactsOf(await deps.reread()));
  if (now.file !== engine.file) {
    deps.report(noticeText(engineChangedSentence(engine, now)), true);
    return 'notStarted';
  }
  const startedAtUtcMs = deps.nowUtcMs();
  await writeGpuPending(deps.durable, { name: adapter.name, id: adapter.id, startedAtUtcMs, deadlineUtcMs: startedAtUtcMs + timeoutMs + PENDING_GRACE_MS });
  const exit = enableExitOf(await deps.run(request));
  // After a TIMEOUT the elevated child may still run (its UAC prompt open): the record stands until its deadline.
  if (exit.kind !== 'timedOut') {
    await clearGpuPending(deps.durable);
  }
  // A declined UAC prompt changed nothing: no second read for it (own review 2, note 7).
  const told = enableSentence(adapter, exit, exit.kind === 'declined' ? NOT_ASKED : await deps.reread(), engine);
  deps.report(told.sentence, told.failed);

  return 'ran';
}

/** ONE enable of `adapter` — the host's own reading. */
export async function enableGpu(chosen: Adapter | undefined, deps: EnableDeps): Promise<EnableResult> {
  const timeoutMs = deps.timeoutMs();
  const ready = precondition(chosen, deps, timeoutMs);
  if ('stop' in ready) {
    return ready.stop;
  }
  const { adapter, request, engine, settle } = ready;
  if (!(await deps.confirm(enablePrompt(adapter, settle, engine.label)))) {
    return 'declined';
  }
  // AGAIN, after the modal: another window may have confirmed its own enable while this modal was open (code round 2; the
  // Windows Time guard flow's rule). Its record stands; this window starts nothing.
  if (busy(deps)) {
    return 'busy';
  }

  return launched(adapter, request, engine, deps, timeoutMs);
}
