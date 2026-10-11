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
  /** The index into the answer's entries the page sends back to select it (E10.S2b). */
  readonly index: number;
  readonly cells: readonly string[];
  /** It may be selected for a restore: verified, removed at its source, an id of the daemon's shape. */
  readonly selectable: boolean;
  readonly selected: boolean;
}

/** One month of one agent: the index the page sends back to open or close it, and — open — its entries. */
export interface ArchiveMonth {
  readonly index: number;
  readonly month: string;
  /** "2 listed · 1 verified · 400 B". */
  readonly summary: string;
  readonly expanded: boolean;
  /** *Select month* may be pressed: the month holds a selectable entry not yet selected, and the selection has room (E10.S2b). */
  readonly selectEnabled: boolean;
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

/** The restore selection and its button (E10.S2b): every word and every state decided by the host. */
export interface ArchiveSelectionView {
  readonly count: number;
  /** "2 selected — one restore takes at most 1000", or why nothing can be selected. */
  readonly summary: string;
  readonly restoreEnabled: boolean;
  readonly clearEnabled: boolean;
  /** The restore line: *Restoring…* while a restore runs (from `status.running` and the journal), or why *Restore* is greyed. */
  readonly line: string;
  readonly lineLevel: ViewLevel;
}

export interface ArchivePageView {
  readonly heading: string;
  /** The list this view was built from (E10.S2b): every index message carries it back; an older one is refused. */
  readonly generation: number;
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
  /** *Refresh* may be pressed: no read is in flight (own review #5: a second read would start a second `wsl.exe`). */
  readonly refreshEnabled: boolean;
  /** The entry table's column headers. */
  readonly headers: readonly string[];
  readonly agents: readonly ArchiveAgent[];
  readonly selection: ArchiveSelectionView;
}
