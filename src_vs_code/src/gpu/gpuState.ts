import type { ProcessResult } from '../process/runner';
import { signed32 } from '../process/runner';
import { safeText } from '../text/safeText';
import { tagsOf } from '../powershell/taggedAnswer';
import { pwshFactsFromTags, type PwshFacts, type PwshProbe } from '../powershell/elevatedEngine';
import { DISABLED_CODE, INSTANCE_ID, TAGS } from './gpuScripts';
import { FIX_WORDS, problemOf, type FixKind } from './gpuProblems';
import { choiceSentence, type OllamaRead } from './ollamaLog';

/**
 * What Windows said about its display adapters (PLAN_gpu_health.md D1, D3) as ONE closed state, and the alert, the line
 * and the bar mark derived from it — pure, so every row of the plan's tables is a unit test. A query that did not answer
 * is `unknown` with its reason, never "no GPU" and never "fine" (plan round, finding 2).
 */

export interface Adapter {
  readonly name: string;
  /** The PnP instance id as Windows reported it — the ONLY id an enable may act on. */
  readonly id: string;
  /** `ConfigManagerErrorCode`: 0 works. */
  readonly code: number;
  readonly driver: string;
}

export type GpuState =
  | { readonly kind: 'unknown'; readonly reason: string }
  | { readonly kind: 'read'; readonly adapters: readonly Adapter[]; readonly atUtcMs: number; readonly pwsh?: PwshProbe; readonly msi?: PwshProbe; readonly programFiles?: string };

export const NOT_ASKED: GpuState = { kind: 'unknown', reason: 'checking…' };

/** The read's PowerShell 7 facts, for the enable — nothing for a state that is not a read. */
export function pwshFactsOf(state: GpuState): PwshFacts | undefined {
  return state.kind === 'read' ? { store: state.pwsh, msi: state.msi, ...(state.programFiles === undefined ? {} : { programFiles: state.programFiles }) } : undefined;
}

/**
 * At most this many adapters are read — and the page's index is held below it (`messages.ts`). An answer naming more is
 * an explicit UNKNOWN, never a partial read shown as complete (code round). The owner's machine has two.
 */
export const MAX_ADAPTERS = 32;

const NAME_MAX = 120;

const INTEGER = /^-?\d+$/;

/** The `adapter=<i>` block marker: printed once per adapter, read by nobody. */
const GPU_IGNORED: ReadonlySet<string> = new Set([TAGS.adapter]);

function tag(tags: ReadonlyMap<string, string>, name: string, i: number): string {
  return tags.get(`${name}.${i}`) ?? '';
}

function adapterAt(tags: ReadonlyMap<string, string>, i: number): Adapter | undefined {
  const code = tag(tags, TAGS.code, i);
  const name = tags.get(`${TAGS.name}.${i}`);

  return name === undefined || !INTEGER.test(code)
    ? undefined
    : { name: safeText(name, NAME_MAX), id: tag(tags, TAGS.id, i), code: Number(code), driver: safeText(tag(tags, TAGS.driver, i), NAME_MAX) };
}

/** The count the query printed LAST — absent (a cut-off answer) or not a count is `undefined`. */
function countOf(tags: ReadonlyMap<string, string>): number | undefined {
  const count = tags.get(TAGS.count) ?? '';

  return /^\d+$/.test(count) ? Number(count) : undefined;
}

function overCap(count: number): GpuState {
  return { kind: 'unknown', reason: `Windows reported ${count} display adapters; this extension reads at most ${MAX_ADAPTERS}` };
}

/** The query's stdout as one state: every adapter up to the count it printed LAST, or `unknown` with why. */
export function parseGpuAnswer(stdout: string, atUtcMs: number): GpuState {
  const { tags, twice } = tagsOf(stdout, GPU_IGNORED);
  if (twice !== undefined) {
    return { kind: 'unknown', reason: `the display-adapter query answered a tag twice (${safeText(twice, NAME_MAX)})` };
  }
  const count = countOf(tags);
  if (count === undefined) {
    return { kind: 'unknown', reason: 'the display-adapter query printed no complete answer' };
  }

  return count > MAX_ADAPTERS ? overCap(count) : adaptersOf(tags, count, atUtcMs);
}

function adaptersOf(tags: ReadonlyMap<string, string>, count: number, atUtcMs: number): GpuState {
  const adapters = Array.from({ length: count }, (_v, i) => i).flatMap((i) => adapterAt(tags, i) ?? []);
  if (adapters.length !== count) {
    return { kind: 'unknown', reason: 'the display-adapter query answered an adapter without its name or code' };
  }
  return { kind: 'read', adapters, atUtcMs, ...factFields(pwshFactsFromTags(tags)) };
}

/** The read's PowerShell 7 facts as the state's optional fields — each left out when the probe did not print it. */
function factFields(facts: PwshFacts): { readonly pwsh?: PwshProbe; readonly msi?: PwshProbe; readonly programFiles?: string } {
  const fields: [string, unknown][] = [['pwsh', facts.store], ['msi', facts.msi], ['programFiles', facts.programFiles]];

  return Object.fromEntries(fields.filter(([, value]) => value !== undefined));
}

const NOT_ANSWERED: { readonly [K in Exclude<ProcessResult['kind'], 'exited'>]: (r: Extract<ProcessResult, { kind: K }>) => string } = {
  timedOut: (r) => `the display-adapter query did not answer within ${Math.round(r.timeoutMs / 1000)} s (wslCare.timeouts.gpuQuerySeconds)`,
  failedToStart: (r) => `the display-adapter query could not start: ${r.reason}`,
  signalled: (r) => `the display-adapter query was stopped (${r.signal})`,
  tooMuchOutput: () => 'the display-adapter query printed more than the extension reads',
};

/** The query's process result as one state. */
export function gpuStateOf(result: ProcessResult, atUtcMs: number): GpuState {
  if (result.kind !== 'exited') {
    return { kind: 'unknown', reason: (NOT_ANSWERED[result.kind] as (r: ProcessResult) => string)(result) };
  }
  const code = signed32(result.code);

  return code === 0 ? parseGpuAnswer(result.stdout.toString('utf8'), atUtcMs) : { kind: 'unknown', reason: `the display-adapter query exited ${code}` };
}

/** The fix for an adapter's code — `enable` only for 22, `unknown` for a code the table does not name. */
export function fixOf(code: number): FixKind {
  return problemOf(code)?.fix ?? 'unknown';
}

/** "it is disabled" — or "code N" for one the table does not name. */
export function meaningOf(code: number): string {
  return problemOf(code)?.meaning ?? `code ${code}`;
}

/** Whether the enable may be offered for this adapter: code 22 AND an id it would pass to the elevated command. */
export function canEnable(adapter: Adapter): boolean {
  return adapter.code === DISABLED_CODE && INSTANCE_ID.test(adapter.id);
}

export type GpuLevel = 'ok' | 'critical' | 'unknown';

/** The page action the alert's button posts (with the adapter's index). */
export type GpuAction = 'enableGpu';

export const GPU_ACTIONS: readonly GpuAction[] = ['enableGpu'];

export interface GpuAlert {
  readonly level: Exclude<GpuLevel, 'ok'>;
  /** "GPU AMD Radeon AI PRO R9700 is not working (code 22: it is disabled)". */
  readonly line: string;
  /** The fix, and whether it needs a restart. */
  readonly fix: string;
  /** What it costs — and Ollama's own last word when its log was read. */
  readonly consequence: string;
  /**
   * The button, by the adapter's INDEX in the READ it was rendered from (`readAt`, the read's time stamp): the page posts
   * those two numbers and nothing else; the host holds the id, and refuses an index from an older read (code round 2).
   */
  readonly button: { readonly index: number; readonly label: string; readonly enabled: boolean; readonly readAt: number } | undefined;
}

export interface GpuView {
  /** The alerts, shown FIRST in the panel — empty when every adapter works. */
  readonly alerts: readonly GpuAlert[];
  /** One muted line for *Health* when every adapter works: "GPU: … — working (checked 14:31)". '' otherwise. */
  readonly line: string;
}

/** A pending enable this window persisted, while it stands (`gpuPending.ts`). */
export interface PendingEnable {
  readonly name: string;
}

const CONSEQUENCE = 'Programs that use this GPU — local models such as Ollama — run on the CPU or do not start.';

/** The fix in words; whether it needs a restart is IN the words (`NEEDS_RESTART` holds them to it, `gpuState.test.ts`). */
function fixWords(code: number): string {
  return FIX_WORDS[fixOf(code)].replace('code N', `code ${code}`);
}

/** Ollama's part of the consequence: its last choice, why its log could not be read (code round), or nothing. */
function ollamaWords(ollama: OllamaRead | undefined): string {
  if (ollama === undefined) {
    return '';
  }

  return 'library' in ollama ? ` ${choiceSentence(ollama)}` : unreadableWords(ollama);
}

function unreadableWords(read: Exclude<OllamaRead, { readonly library: string }>): string {
  return read.kind === 'unreadable' ? ` Ollama's log could not be read (${read.reason}).` : '';
}

function consequence(ollama: OllamaRead | undefined): string {
  return CONSEQUENCE + ollamaWords(ollama);
}

function buttonFor(adapter: Adapter, index: number, pending: PendingEnable | undefined, readAt: number): GpuAlert['button'] {
  return canEnable(adapter) ? { index, label: `Enable ${adapter.name}`, enabled: pending === undefined, readAt } : undefined;
}

function alertFor(adapter: Adapter, index: number, ollama: OllamaRead | undefined, pending: PendingEnable | undefined, readAt: number): GpuAlert {
  const waiting = pending !== undefined && canEnable(adapter) ? `Waiting for the elevated PowerShell that enables ${pending.name} (answer the UAC prompt)… ` : '';

  return {
    level: 'critical',
    line: `GPU ${adapter.name} is not working (code ${adapter.code}: ${meaningOf(adapter.code)})`,
    fix: waiting + fixWords(adapter.code),
    consequence: consequence(ollama),
    button: buttonFor(adapter, index, pending, readAt),
  };
}

/** The working line's tail while a query is in flight (code round 3): the last answer stays on screen, and says a check runs. */
const CHECKING_AGAIN = ' — checking again…';

/** The Health line while a query runs with an alert on screen (round 4): the alert stays as it was, this line says a check runs. */
const CHECKING_LINE = 'GPU: checking again…';

/**
 * The panel's GPU part (D3, D6): one critical alert per adapter that is not working, the unknown state as an alert of its
 * own, and — when all work — one muted line, with `checking` while a query runs. `formatTime` is the presentation edge's
 * local-time formatter.
 */
export function gpuView(state: GpuState, ollama: OllamaRead | undefined, pending: PendingEnable | undefined, formatTime: (utcMs: number) => string, checking = false): GpuView {
  if (state.kind === 'unknown') {
    return unknownView(state);
  }
  const alerts = state.adapters.flatMap((a, i) => (a.code === 0 ? [] : [alertFor(a, i, ollama, pending, state.atUtcMs)]));

  return { alerts, line: healthLine(state, alerts.length > 0, formatTime, checking) };
}

/** The Health line: with an alert on screen only the checking note (or nothing); otherwise the working line. */
function healthLine(state: Extract<GpuState, { kind: 'read' }>, alerted: boolean, formatTime: (utcMs: number) => string, checking: boolean): string {
  if (alerted) {
    return checking ? CHECKING_LINE : '';
  }

  return workingText(state, formatTime, checking);
}

/** "GPU: … — working (checked 14:31)", and " — checking again…" while a query runs. */
function workingText(state: Extract<GpuState, { kind: 'read' }>, formatTime: (utcMs: number) => string, checking: boolean): string {
  return workingLine(state.adapters, formatTime(state.atUtcMs)) + (checking ? CHECKING_AGAIN : '');
}

function unknownView(state: Extract<GpuState, { kind: 'unknown' }>): GpuView {
  return state === NOT_ASKED
    ? { alerts: [], line: 'GPU: checking…' }
    : { alerts: [{ level: 'unknown', line: `GPU state could not be checked: ${state.reason}`, fix: 'Refresh to try again.', consequence: '', button: undefined }], line: '' };
}

function workingLine(adapters: readonly Adapter[], atText: string): string {
  const names = adapters.map((a) => a.name).join(', ');

  return `GPU: ${names === '' ? 'no display adapter reported' : names} — working (checked ${atText})`;
}

export interface GpuBarMark {
  /** Appended to the bar's text: ' · GPU off', ' · GPU ?', or ''. */
  readonly text: string;
  readonly level: 'none' | 'warn' | 'critical';
  /** Tooltip lines. */
  readonly lines: readonly string[];
}

export const NO_MARK: GpuBarMark = { text: '', level: 'none', lines: [] };

/** The status bar's mark (D6): critical while any adapter is not working, a warning while the check could not run. */
export function gpuBarMark(state: GpuState): GpuBarMark {
  if (state.kind === 'unknown') {
    return state === NOT_ASKED ? NO_MARK : { text: ' · GPU ?', level: 'warn', lines: [`GPU state could not be checked: ${state.reason}`] };
  }
  const broken = state.adapters.filter((a) => a.code !== 0);

  return broken.length === 0 ? NO_MARK : { text: ' · GPU off', level: 'critical', lines: broken.map((a) => `GPU ${a.name}: not working (code ${a.code}: ${meaningOf(a.code)})`) };
}
