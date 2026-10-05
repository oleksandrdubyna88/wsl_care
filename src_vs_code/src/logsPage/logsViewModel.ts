import { safeText } from '../panel/format';
import { list, text, type Json } from '../panel/read';
import { minuteOf } from '../text/format';
import { blockOf, extremes, pendingBlock, runCounts, totals, trend } from './logsBlocks';
import type { LogsBlock, LogsState, LogsView, PeriodButton, Picker, ReadState, RunList, RunRow } from './logsView';
import type { Period } from './period';
import { detailBlock, RUN_HEADERS, runCells, thisRunBlocks } from './runDetail';

/**
 * The Logs page's view model (plan §7.4): the host's state — the period, the window it built, the answers it read — laid
 * out as the page shows it. Pure; the blocks read the answers field by field (`logsBlocks.ts`, `runDetail.ts`), and this
 * module only chooses which blocks a period has and how its controls stand. A *This run* page has the run and its detail;
 * every other period has Totals, Runs, Max / min, the trend and the run list.
 */

const NAMES: { readonly [K in Period['kind']]: string } = {
  thisRun: 'This run',
  today: 'Today',
  yesterday: 'Yesterday',
  day: 'A day',
  range: 'A range',
};

function daysText(state: LogsState): string {
  const window = state.window;
  if (window === undefined) {
    return '';
  }
  const days = window.firstDay === window.lastDay ? window.firstDay : `${window.firstDay} to ${window.lastDay}`;
  const clamped = window.clamped ? ` (clamped to the ${state.retentionDays} days the daemon keeps)` : '';

  return `${days}${clamped} — local days, asked as ${minuteOf(window.from)} to ${minuteOf(window.to)}`;
}

function periodLabel(state: LogsState): string {
  const period = state.period;

  return period.kind === 'thisRun' ? `${NAMES.thisRun} — run ${period.runId}` : `${NAMES[period.kind]}, ${daysText(state)}`;
}

function periodButtons(state: LogsState): PeriodButton[] {
  const kind = state.period.kind;
  const button = (id: PeriodButton['id'], reason: string): PeriodButton => ({ id, label: NAMES[id], pressed: kind === id, enabled: reason === '', reason });

  return [button('thisRun', state.thisRunReason), button('today', ''), button('yesterday', '')];
}

function pickerOf(state: LogsState): Picker {
  const { oldest, newest } = state.retained;
  const { firstDay, lastDay } = state.window ?? { firstDay: newest, lastDay: newest };

  return { min: oldest, max: newest, day: firstDay, from: firstDay, to: lastDay, dayPressed: state.period.kind === 'day', rangePressed: state.period.kind === 'range' };
}

function dayBlocks(state: LogsState): LogsBlock[] {
  return [
    blockOf('totals', 'Totals', state.logs, totals),
    blockOf('runs', 'Runs', state.logs, runCounts),
    blockOf('extremes', 'Max / min', state.logs, extremes),
    blockOf('trend', 'Trend', state.runs, trend),
  ];
}

function detailsNote(logs: ReadState): string[] {
  return logs.kind === 'answered' ? [`objects not read for ${text(logs.body, 'detailsNotRead')} run(s) with a cleanup — expand a run to list them (runs show)`] : [];
}

function rowsOf(body: Json, details: LogsState['details']): RunRow[] {
  return list(body, 'runs').map((run, index) => {
    const detail = details.get(index);
    return { index, cells: runCells(run), expanded: detail !== undefined, detail: detail === undefined ? [] : [detailBlock(detail)] };
  });
}

const NO_LIST: RunList = { shown: false, state: 'answered', headers: RUN_HEADERS, rows: [], notes: [] };

function runListOf(state: LogsState): RunList {
  if (state.period.kind === 'thisRun') {
    return NO_LIST;
  }
  if (state.runs.kind !== 'answered') {
    const pending = pendingBlock('runList', 'Runs', state.runs);
    return { shown: true, state: pending.state, headers: RUN_HEADERS, rows: [], notes: pending.notes };
  }

  return { shown: true, state: 'answered', headers: RUN_HEADERS, rows: rowsOf(state.runs.body, state.details), notes: [`${text(state.runs.body, 'count')} runs in the period`, ...detailsNote(state.logs)] };
}

export function buildLogsView(state: LogsState): LogsView {
  return {
    heading: 'WSL Care — Logs',
    notice: safeText(state.notice),
    noticeLevel: state.noticeLevel,
    periodLabel: periodLabel(state),
    periods: periodButtons(state),
    picker: pickerOf(state),
    blocks: state.period.kind === 'thisRun' ? thisRunBlocks(state.show) : dayBlocks(state),
    runList: runListOf(state),
  };
}
