import type { DurableStore } from '../cleanup/journal';
import type { ProcessRequest, ProcessResult, Runner } from '../process/runner';
import { signed32 } from '../process/runner';
import { noticeText } from '../text/safeText';
import { CONFIRM_LABEL, EXIT, type FixPrompt } from '../windowsTime/windowsTimeFix';
import { clearGpuPending, PENDING_GRACE_MS, readGpuPending, writeGpuPending } from './gpuPending';
import { DISABLED_CODE, ENABLE_EXIT, enableRequest, enableScript } from './gpuScripts';
import { canEnable, meaningOf, type Adapter, type GpuState } from './gpuState';

/**
 * *Enable <GPU>* (PLAN_gpu_health.md D4, D5) in the order that makes it safe: the adapter is the host's OWN reading, chosen
 * by an index; the request is built (or the reason none can be is told); a modal shows the exact elevated script and the
 * device; ONLY its confirm persists the pending run and starts the ONE elevated PowerShell; then Windows is read again and
 * the outcome is worded from what it NOW says about that device. Nothing runs on a decline, while another enable stands, or
 * for an adapter that is not at code 22.
 */

/** What the launcher's exit said — before Windows is read again. */
export type EnableExit =
  | { readonly kind: 'ran' }
  | { readonly kind: 'declined' }
  | { readonly kind: 'nothingDone'; readonly why: string }
  | { readonly kind: 'failed'; readonly sentence: string }
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

  return why === undefined ? { kind: 'failed', sentence: FAILED[code] ?? `the elevated PowerShell exited ${code}` } : { kind: 'nothingDone', why };
}

function exitedOf(code: number): EnableExit {
  return SETTLED[code] ?? notRanOf(code);
}

const OF_RESULT: { readonly [K in ProcessResult['kind']]: (r: Extract<ProcessResult, { kind: K }>) => EnableExit } = {
  exited: (r) => exitedOf(signed32(r.code)),
  timedOut: (r) => ({ kind: 'timedOut', timeoutMs: r.timeoutMs }),
  failedToStart: (r) => ({ kind: 'failed', sentence: `the PowerShell launcher could not start: ${r.reason}` }),
  signalled: (r) => ({ kind: 'failed', sentence: `the PowerShell launcher was stopped (${r.signal})` }),
  tooMuchOutput: () => ({ kind: 'failed', sentence: 'the PowerShell launcher printed more than the extension reads' }),
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

/** The outcome, from the launcher's exit and the read that followed it (D4) — pure, so every row is a unit test. */
export function enableSentence(adapter: Adapter, exit: EnableExit, state: GpuState): { readonly sentence: string; readonly failed: boolean } {
  const now = nowWords(adapter.name, afterOf(state, adapter.id), state);
  const lead: { readonly [K in EnableExit['kind']]: (e: Extract<EnableExit, { kind: K }>) => { readonly text: string; readonly failed: boolean } } = {
    ran: () => ({ text: '', failed: false }),
    declined: () => ({ text: 'Nothing was changed: the UAC prompt was declined.', failed: false }),
    nothingDone: (e) => ({ text: `Nothing was done: ${e.why}.`, failed: false }),
    failed: (e) => ({ text: `Enabling ${adapter.name} failed: ${e.sentence}.`, failed: true }),
    timedOut: (e) => ({ text: `The enable did not finish within ${Math.round(e.timeoutMs / 1000)} s (wslCare.timeouts.gpuEnableSeconds) — if the UAC prompt is still open, answering it still runs it.`, failed: true }),
  };
  const head = (lead[exit.kind] as (e: EnableExit) => { readonly text: string; readonly failed: boolean })(exit);
  if (exit.kind === 'declined') {
    return { sentence: noticeText(head.text), failed: false };
  }

  return { sentence: noticeText([head.text, now.sentence].filter((t) => t !== '').join(' ')), failed: head.failed || now.failed };
}

export function enablePrompt(adapter: Adapter, settleSeconds: number): FixPrompt {
  return {
    message: `Enable ${adapter.name}?`,
    detail: [
      `Windows reports it as disabled (code ${DISABLED_CODE}). One PowerShell runs ELEVATED — Windows asks you first (UAC) — and runs exactly this:`,
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
  readonly exists: (file: string) => boolean;
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

export type EnableResult = 'notOffered' | 'busy' | 'notStarted' | 'declined' | 'ran';

/** An adapter the enable may act on: present in the host's reading, at code 22, with an id it passes. */
function offered(adapter: Adapter | undefined): adapter is Adapter {
  return adapter !== undefined && canEnable(adapter);
}

/** Why nothing may start, or the one request — checked in this order: offered, not already running, buildable. */
function precondition(adapter: Adapter | undefined, deps: EnableDeps, timeoutMs: number): { readonly stop: EnableResult } | { readonly adapter: Adapter; readonly request: ProcessRequest } {
  if (!offered(adapter)) {
    deps.report(noticeText('No disabled GPU is at that place any more — the panel is read again.'), false);
    return { stop: 'notOffered' };
  }
  if (readGpuPending(deps.durable, deps.nowUtcMs()) !== undefined) {
    return { stop: 'busy' };
  }
  const request = enableRequest(deps.env, adapter.id, timeoutMs, deps.exists, deps.settleSeconds());

  return typeof request === 'string' ? notStarted(adapter, request, deps) : { adapter, request };
}

function notStarted(adapter: Adapter, reason: string, deps: EnableDeps): { readonly stop: EnableResult } {
  deps.report(noticeText(`${adapter.name} could not be enabled from here: ${reason}.`), true);
  return { stop: 'notStarted' };
}

/** ONE enable of `adapter` — the host's own reading. */
export async function enableGpu(chosen: Adapter | undefined, deps: EnableDeps): Promise<EnableResult> {
  const timeoutMs = deps.timeoutMs();
  const ready = precondition(chosen, deps, timeoutMs);
  if ('stop' in ready) {
    return ready.stop;
  }
  const { adapter, request } = ready;
  if (!(await deps.confirm(enablePrompt(adapter, deps.settleSeconds())))) {
    return 'declined';
  }
  const startedAtUtcMs = deps.nowUtcMs();
  await writeGpuPending(deps.durable, { name: adapter.name, id: adapter.id, startedAtUtcMs, deadlineUtcMs: startedAtUtcMs + timeoutMs + PENDING_GRACE_MS });
  const exit = enableExitOf(await deps.run(request));
  // After a TIMEOUT the elevated child may still run (its UAC prompt open): the record stands until its deadline.
  if (exit.kind !== 'timedOut') {
    await clearGpuPending(deps.durable);
  }
  const told = enableSentence(adapter, exit, await deps.reread());
  deps.report(told.sentence, told.failed);

  return 'ran';
}
