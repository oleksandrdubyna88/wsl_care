import { isJson, list, num, text, type Json } from '../panel/read';
import { safeText } from '../panel/format';
import { gb, localMinuteOf, metricText } from '../text/format';
import type { BlockState, LogsBlock, LogsLine, LogsTable, ReadState } from './logsView';

/**
 * The Logs page's period blocks (plan §7.4) — Totals, Runs, Max / min and the trend — each a straight READING of the
 * daemon's `logs` / `runs` answer: one field, one figure, spelt by `text/format.ts`. Nothing is summed, subtracted or
 * counted here (the daemon's `logs` already did that, over the records it holds); a field that is not in the answer reads as
 * "not answered", never as 0. Every daemon string passes `safeText` (`panel/read.ts`).
 */

const NOT_ANSWERED = 'not answered';

/** A byte figure in GB, or "not answered". */
export function bytesOf(value: unknown, key: string): string {
  const bytes = num(value, key);

  return bytes === undefined ? NOT_ANSWERED : gb(bytes);
}

/** A local time to the minute, or "not answered". */
export function timeOf(value: unknown, key: string): string {
  return localMinuteOf(text(value, key, ''), NOT_ANSWERED);
}

function line(id: string, label: string, value: string): LogsLine {
  return { id, label, value };
}

/** A block whose read is not (or not yet) an answer: what it is waiting for, or why it has none. */
export function pendingBlock(id: string, title: string, read: ReadState): LogsBlock {
  const states: { readonly [K in ReadState['kind']]: { readonly state: BlockState; readonly note: string } } = {
    idle: { state: 'reading', note: 'Not asked yet.' },
    reading: { state: 'reading', note: 'Reading…' },
    answered: { state: 'answered', note: '' },
    failed: { state: 'failed', note: `unavailable — ${read.kind === 'failed' ? safeText(read.sentence) : ''}` },
    unreadable: { state: 'failed', note: `the run history could not be read: ${read.kind === 'unreadable' ? safeText(read.problem) : ''} — no figures` },
  };
  const { state, note } = states[read.kind];

  return { id, title, state, lines: [], tables: [], notes: [note] };
}

/** The block of an answered read, or its pending form. */
export function blockOf(id: string, title: string, read: ReadState, build: (body: Json) => Omit<LogsBlock, 'id' | 'title' | 'state'>): LogsBlock {
  return read.kind === 'answered' ? { id, title, state: 'answered', ...build(read.body) } : pendingBlock(id, title, read);
}

function table(id: string, title: string, headers: readonly string[], rows: readonly (readonly string[])[]): LogsTable {
  return { id, title, headers, rows };
}

// ---- Totals ----

function perActionRow(action: Json): string[] {
  return [text(action, 'id'), text(action, 'runs'), text(action, 'count'), bytesOf(action, 'freedBytes'), text(action, 'dryRuns'), bytesOf(action, 'wouldFreeBytes'), text(action, 'failed')];
}

export function totals(body: Json): Omit<LogsBlock, 'id' | 'title' | 'state'> {
  return {
    lines: [line('freed', 'Freed in total', bytesOf(body, 'freedBytes')), line('objects', 'Objects removed', text(body, 'objectsRemoved', NOT_ANSWERED))],
    tables: [table('perAction', 'Per action', ['Action', 'Runs', 'Objects', 'Freed', 'Dry runs', 'Would free', 'Failed'], list(body, 'perAction').map(perActionRow))],
    notes: [],
  };
}

// ---- Runs ----

/** The counts of `logs.runs`, each with its label — in the order §7.4 lists them. */
const COUNTS: readonly (readonly [string, string])[] = [
  ['total', 'Runs'],
  ['withCleanup', 'With a cleanup'],
  ['withoutCleanup', 'Without a cleanup'],
  ['dryRun', 'Dry runs'],
  ['timer', 'By the timer'],
  ['manual', 'From a button'],
  ['cli', 'From the command line'],
  ['failed', 'Failed'],
  ['interrupted', 'Interrupted'],
];

export function runCounts(body: Json): Omit<LogsBlock, 'id' | 'title' | 'state'> {
  const runs = isJson(body.runs) ? body.runs : {};
  const counts = COUNTS.map(([key, label]) => line(key, label, text(runs, key, NOT_ANSWERED)));

  return {
    lines: [...counts, line('wouldFreeBytes', 'The dry runs would have freed', bytesOf(runs, 'wouldFreeBytes')), line('unparseableLines', 'History lines that could not be read', text(body, 'unparseableLines', NOT_ANSWERED))],
    tables: [],
    notes: [],
  };
}

// ---- Max / min ----

const METRIC_LABELS: Readonly<Record<string, string>> = {
  memAvailablePercent: 'MemAvailable (%)',
  memAvailableBytes: 'MemAvailable',
  pageCacheBytes: 'Page cache',
  swapUsedBytes: 'Swap used',
  rootUsedPercent: '/ used',
  dockerReclaimableBytes: 'Docker reclaimable',
  containerStarts24h: 'Container starts (24 h)',
};

/** §15j M7: `vmmemWSL` is measured on the Windows side, by `wsl-care.exe` — not in the daemon's records. */
export const VMMEM_ARRIVES = 'arrives in E7.S3 / E11';

function extremeRun(body: Json, key: string): string {
  const run = body[key];

  return isJson(run) ? `${bytesOf(run, 'freedBytes')} — run ${text(run, 'runId')}, ${timeOf(run, 'startedAt')}` : 'no run of the period freed anything';
}

function pointCells(metric: Json, key: 'max' | 'min'): string[] {
  const point = metric[key];
  const value = num(point, 'value');
  if (!isJson(point) || value === undefined) {
    return ['not recorded', '—'];
  }

  return [metricText(text(metric, 'name'), text(metric, 'unit', ''), value), timeOf(point, 'at')];
}

function metricRow(metric: Json): string[] {
  const name = text(metric, 'name');
  const label = Object.hasOwn(METRIC_LABELS, name) ? (METRIC_LABELS[name] as string) : name;

  return [label, ...pointCells(metric, 'max'), ...pointCells(metric, 'min'), text(metric, 'samples')];
}

export function extremes(body: Json): Omit<LogsBlock, 'id' | 'title' | 'state'> {
  const rows = [...list(body, 'metrics').map(metricRow), ['vmmemWSL (Windows)', VMMEM_ARRIVES, '—', VMMEM_ARRIVES, '—', '—']];

  return {
    lines: [line('mostFreed', 'Freed the most', extremeRun(body, 'mostFreed')), line('leastFreed', 'Freed the least (not zero)', extremeRun(body, 'leastFreed'))],
    tables: [table('metrics', 'Per figure', ['Figure', 'Max', 'At', 'Min', 'At', 'Samples'], rows)],
    notes: [],
  };
}

// ---- the trend: the MemAvailable sparkline's points, as a table ----

function trendRow(run: Json): string[] {
  const metrics = run.metrics as Json;
  const value = (name: string, unit: string): string => {
    const n = num(metrics, name);
    return n === undefined ? 'not recorded' : metricText(name, unit, n);
  };

  return [timeOf(run, 'startedAt'), value('memAvailablePercent', '%'), value('swapUsedBytes', 'bytes')];
}

export function trend(body: Json): Omit<LogsBlock, 'id' | 'title' | 'state'> {
  const points = list(body, 'runs').filter((run) => isJson(run.metrics));

  return {
    lines: [],
    tables: [table('trend', 'MemAvailable and swap, run by run', ['Time', 'MemAvailable', 'Swap used'], points.map(trendRow))],
    notes: ['Each full run records these figures (RunLine.metrics); a run that recorded none (an act, a swept run) is not a point. Drawn as a table — no sparkline is drawn.'],
  };
}
