import type { JsonObject } from '../client/outcome';
import type { ViewLevel } from '../panel/view';
import type { Period, PeriodWindow } from './period';

/**
 * What the host posts to the Logs page (plan §7.4) — plain strings, booleans, numbers and arrays only, every daemon string
 * through `safeText`, every figure already spelt by `text/format.ts`. The page computes NOTHING: it lays these out with
 * `createElement` / `textContent` (`media/logs.js`, `logsPage.test.ts`), and each block's text is a field of the daemon's
 * answer as it came — no sum, no difference, no count of a list (§7.4: "the page computes nothing itself").
 */

/** One labelled figure. */
export interface LogsLine {
  readonly id: string;
  readonly label: string;
  readonly value: string;
}

/** A table under a block: its headers, then one array of cell texts per row. */
export interface LogsTable {
  readonly id: string;
  readonly title: string;
  readonly headers: readonly string[];
  readonly rows: readonly (readonly string[])[];
}

/** How a block's content came about: the answer, a read in flight, or why there is none. */
export type BlockState = 'answered' | 'reading' | 'failed';

export interface LogsBlock {
  readonly id: string;
  readonly title: string;
  readonly state: BlockState;
  readonly lines: readonly LogsLine[];
  readonly tables: readonly LogsTable[];
  readonly notes: readonly string[];
}

/** One line of the run list: the cells, and — once expanded — what `runs show` answered for it. */
export interface RunRow {
  /** The index the page sends back to expand / collapse it — into the list the host read. */
  readonly index: number;
  readonly cells: readonly string[];
  readonly expanded: boolean;
  readonly detail: readonly LogsBlock[];
}

export type PeriodButtonId = 'thisRun' | 'today' | 'yesterday';

export interface PeriodButton {
  readonly id: PeriodButtonId;
  readonly label: string;
  readonly pressed: boolean;
  readonly enabled: boolean;
  /** Why it is greyed ('' when it is not). */
  readonly reason: string;
}

/** The date picker: the retention's bounds (min / max), and the days it shows. */
export interface Picker {
  readonly min: string;
  readonly max: string;
  readonly day: string;
  readonly from: string;
  readonly to: string;
  readonly dayPressed: boolean;
  readonly rangePressed: boolean;
}

export interface RunList {
  /** False for *This run*: one run has no list. */
  readonly shown: boolean;
  readonly state: BlockState;
  readonly headers: readonly string[];
  readonly rows: readonly RunRow[];
  /** `logs`' `detailsNotRead`, and what it means — or why the list is not there. */
  readonly notes: readonly string[];
}

export interface LogsView {
  readonly heading: string;
  readonly notice: string;
  readonly noticeLevel: ViewLevel;
  /** "Today, 2026-10-05 — local time: 2026-10-04 21:00 UTC to 2026-10-05 21:00 UTC". */
  readonly periodLabel: string;
  readonly periods: readonly PeriodButton[];
  readonly picker: Picker;
  readonly blocks: readonly LogsBlock[];
  readonly runList: RunList;
}

/** One read the host made, as the view needs it. */
export type ReadState =
  | { readonly kind: 'idle' }
  | { readonly kind: 'reading' }
  | { readonly kind: 'answered'; readonly body: JsonObject }
  | { readonly kind: 'failed'; readonly sentence: string }
  /** The daemon answered, but could not read its history (`problem`, exit 4 — review C1): no figure is a fact. */
  | { readonly kind: 'unreadable'; readonly problem: string };

/** Everything the view is built from — the host's state, nothing the page holds. */
export interface LogsState {
  readonly period: Period;
  /** The window of a day period (undefined for *This run*). */
  readonly window: PeriodWindow | undefined;
  readonly retained: { readonly oldest: string; readonly newest: string };
  /** The days of history the daemon keeps — its own value, or 90 until it answers one. */
  readonly retentionDays: number;
  readonly logs: ReadState;
  readonly runs: ReadState;
  /** *This run*'s `runs show`. */
  readonly show: ReadState;
  /** The expanded lines of the run list, by index. */
  readonly details: ReadonlyMap<number, ReadState>;
  /** Why *This run* cannot be asked ('' when it can). */
  readonly thisRunReason: string;
  readonly notice: string;
  readonly noticeLevel: ViewLevel;
}
