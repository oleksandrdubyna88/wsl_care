import type { ViewLevel } from '../panel/view';

/**
 * What the host posts to the Archive page (plan §15s D6, E10.S2a) — plain strings, booleans, numbers and arrays only, every daemon
 * string through `safeText`, every figure already spelt by `text/format.ts`. The page computes NOTHING: it lays these out with
 * `createElement` / `textContent` (`media/archive.js`, `archivePage.test.ts`). Built so the page can move into the shared webview
 * kit later (`todo/PLAN_disk_health.md` E18): every decision is the host's, and a row is named by an index into what it holds.
 */

/** One labelled fact of the answer: the base folder, the side, the distribution. */
export interface ArchiveLine {
  readonly id: string;
  readonly label: string;
  readonly value: string;
}

/** One archived entry, as its row's cells: session path, files, size, status, verified, archived at. */
export interface ArchiveEntryRow {
  readonly cells: readonly string[];
}

/** One month of one agent: the index the page sends back to open or close it, and — open — its entries. */
export interface ArchiveMonth {
  readonly index: number;
  readonly month: string;
  /** "2 listed · 1 verified · 400 B". */
  readonly summary: string;
  readonly expanded: boolean;
  /** Only while expanded; a closed month builds no rows. */
  readonly rows: readonly ArchiveEntryRow[];
}

export interface ArchiveAgent {
  readonly agent: string;
  readonly summary: string;
  readonly months: readonly ArchiveMonth[];
}

/** How the list came about: an answer, a read in flight, a failure, or a daemon that cannot list (it is not asked). */
export type ArchivePageState = 'reading' | 'answered' | 'failed' | 'unavailable';

export interface ArchivePageView {
  readonly heading: string;
  /** D9: "this distribution's archive only — …". */
  readonly scope: string;
  readonly notice: string;
  readonly noticeLevel: ViewLevel;
  readonly state: ArchivePageState;
  readonly lines: readonly ArchiveLine[];
  /** The answer's own notes (an unreadable month, a changed mount), skipped lines, an outcome other than done — or why there is no list. */
  readonly notes: readonly string[];
  /** The session paths are shown whole (the toggle is pressed). */
  readonly pathsShown: boolean;
  readonly pathsLabel: string;
  /** The entry table's column headers. */
  readonly headers: readonly string[];
  readonly agents: readonly ArchiveAgent[];
}
