import type { StatusAnswer, Verdict, VerbOutcome } from '../client/outcome';
import { failureText } from '../failureText';
import type { GpuBarMark } from '../gpu/gpuState';
import { at, unavailableAncestor } from '../panel/jsonPath';
import type { VolumeBarMark } from './volumeMark';

export { NO_VOLUME_MARK, volumeMarkOf, volumeMarkOfOutcome, type VolumeBarMark } from './volumeMark';

/**
 * The status bar (plan §7.2; §15g B1), as a pure function of the newest `status` outcome:
 * `WSL RAM <used>% · swap <x>G · <n> containers`, coloured by the worst RELEVANT verdict — the verdicts about what the
 * bar shows (memory and swap, and the kernel's allocation-failure / OOM alerts, which are memory events), a busy machine, and
 * Windows' own memory pressure. A daemon that
 * predates `verdicts` leaves the bar uncoloured and says so in the tooltip; a figure the daemon could not read is `?`,
 * never 0. Every failure is its short state (`failureText`); a stopped distribution is "WSL stopped" — the client made
 * no call that would start it.
 */

export type BarLevel = 'none' | 'warn' | 'critical';

export interface BarView {
  readonly text: string;
  readonly tooltip: string;
  readonly level: BarLevel;
}

/** E14 S6: `pressure.` (cpu / io PSI, the daemon's "machine busy" rule) colours the bar too — a busy machine is what it shows.
 * E15.S5: `host.` (Windows memory pressure: `host.commit`, `host.compression`, `host.vmmem`) too — the memory behind the VM. */
export const RELEVANT_VERDICT_PREFIXES: readonly string[] = ['memory.', 'kernel.', 'pressure.', 'host.'];

/** E14 S5: `memory.committed` warns of what the kernel PROMISED, not of a state the bar shows — the panel lists it, the bar does not. */
export const BAR_EXCLUDED_VERDICT_IDS: readonly string[] = ['memory.committed'];

const CLICK = 'Click to open the AI OS Care panel.';

const RANK: Readonly<Record<Verdict['level'], number>> = { ok: 0, unknown: 0, warn: 1, critical: 2 };
const LEVEL_OF_RANK: readonly BarLevel[] = ['none', 'warn', 'critical'];

function isRelevant(verdict: Verdict): boolean {
  return RELEVANT_VERDICT_PREFIXES.some((prefix) => verdict.id.startsWith(prefix)) && !BAR_EXCLUDED_VERDICT_IDS.includes(verdict.id);
}

/** The worst relevant level; `unknown` colours nothing. */
export function worstLevel(verdicts: readonly Verdict[]): BarLevel {
  return LEVEL_OF_RANK[Math.max(0, ...verdicts.filter(isRelevant).map((v) => RANK[v.level]))] ?? 'none';
}

interface Figure {
  readonly text: string;
  readonly reason: string;
}

function reasonOf(value: unknown): string {
  const reason = at(value, 'reason');
  return typeof reason === 'string' ? reason : 'not reported';
}

/** The number `field` of the `{available, …}` object at `path`, or `?` with why — the object's reason, or an unavailable
 * ancestor's. */
function figure(body: unknown, path: string, field: string, show: (n: number) => string): Figure {
  // A part the daemon could not read (`vm`, `vm.memory`) arrives as `{ available: false, reason }` with no children:
  // its reason is the figure's.
  const value = at(body, path) ?? unavailableAncestor(body, path);
  const n = at(value, field);
  if (at(value, 'available') === true && typeof n === 'number') {
    return { text: show(n), reason: '' };
  }

  return { text: '?', reason: reasonOf(value) };
}

function containersText(body: unknown): Figure {
  const count = figure(body, 'vm.containers', 'count', String);
  return { text: `${count.text} ${count.text === '1' ? 'container' : 'containers'}`, reason: count.reason };
}

function verdictLines(answer: StatusAnswer): string[] {
  if (answer.verdicts === undefined) {
    return ['This daemon reports no verdicts: update the daemon to see warnings.'];
  }

  return answer.verdicts.filter((v) => isRelevant(v) && v.level !== 'ok').map((v) => `${v.id}: ${v.level} — ${v.value ?? ''}`);
}

function answeredView(answer: StatusAnswer, distro: string): BarView {
  const used = figure(answer.body, 'vm.memory.availablePercent', 'value', (v) => String(Math.round(100 - v)));
  const swap = figure(answer.body, 'vm.memory.swapUsed', 'bytes', (b) => (b / 1024 ** 3).toFixed(1));
  const containers = containersText(answer.body);
  // One line per distinct reason: an unavailable vm.memory explains RAM and swap alike.
  const reasons = [...new Set([used, swap, containers].filter((f) => f.reason !== '').map((f) => `Not read: ${f.reason}`))];

  return {
    text: `WSL RAM ${used.text}% · swap ${swap.text}G · ${containers.text}`,
    tooltip: [`AI OS Care — ${distro}`, ...reasons, ...verdictLines(answer), CLICK].join('\n'),
    level: answer.verdicts === undefined ? 'none' : worstLevel(answer.verdicts),
  };
}

const CHECKING: BarView = { text: 'AI OS Care: checking…', tooltip: `Asking the daemon for its status. ${CLICK}`, level: 'none' };

function failureView(status: Exclude<VerbOutcome, { kind: 'answered' }>): BarView {
  const words = failureText(status);

  return { text: status.kind === 'stopped' ? words.label : `AI OS Care: ${words.label}`, tooltip: `${words.sentence}\n${CLICK}`, level: 'none' };
}

const BAR_RANK: Readonly<Record<BarLevel, number>> = { none: 0, warn: 1, critical: 2 };

/** What a mark laid over the bar is made of — the GPU host's and the volume mark's shapes are this one. */
interface BarMark {
  readonly text: string;
  readonly level: BarLevel;
  readonly lines: readonly string[];
}

/** Appends the mark's text, puts its lines first in the tooltip and raises the level — never LOWERS it; an empty mark changes nothing. */
function withMark(view: BarView, mark: BarMark): BarView {
  if (mark.text === '') {
    return view;
  }

  return { text: view.text + mark.text, tooltip: [...mark.lines, view.tooltip].join('\n'), level: BAR_RANK[mark.level] > BAR_RANK[view.level] ? mark.level : view.level };
}

/**
 * The GPU mark (PLAN_gpu_health.md D6) on whatever the bar shows — WSL's figures, "WSL stopped", a failure: ` · GPU off` and
 * the error colour while an adapter is not working, ` · GPU ?` and the warning colour while the check could not run, the
 * adapters' lines first in the tooltip. A mark never LOWERS the bar's level. Pure.
 */
export function withGpuMark(view: BarView, mark: GpuBarMark): BarView {
  return withMark(view, mark);
}

/**
 * The volume mark (E19.S1) beside the GPU's: ` · D: low space` and the error colour while `disk.volumeFree` is critical, the volume's
 * free space and the reading's age as the tooltip's first line (`volumeMark.ts` reads them from the status answer). A mark never
 * LOWERS the bar's level. Pure.
 */
export function withVolumeMark(view: BarView, mark: VolumeBarMark): BarView {
  return withMark(view, mark);
}

export function barView(status: VerbOutcome | undefined): BarView {
  if (status === undefined) {
    return CHECKING;
  }
  if (status.kind !== 'answered') {
    return failureView(status);
  }

  return status.answer.verb === 'status' ? answeredView(status.answer, status.distro) : CHECKING;
}
