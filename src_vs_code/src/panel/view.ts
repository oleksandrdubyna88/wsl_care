/**
 * What the host posts to the page — plain strings, numbers and arrays only (structured-cloneable, and nothing the page
 * could mistake for markup). The page builds its DOM from this with `createElement` / `textContent` and nothing else
 * (`media/panel.js`, `panelPage.test.ts`). Every daemon string in it has passed `safeText` (`format.ts`).
 */

/** How a row's value came about — the page styles by it; none of them is ever an empty value. */
export type RowState =
  /** A figure the daemon answered. */
  | 'value'
  /** The daemon answered `available: false` — "unavailable — <reason>". */
  | 'unavailable'
  /** Not in the E5 verbs — "arrives in E#". */
  | 'arrives'
  /** The answering daemon predates the field — "update the daemon to see this" (plan §6 compatibility rule). */
  | 'missing'
  /** The verb itself failed (stopped, not installed, a newer schema …) — its short state. */
  | 'blocked'
  /** Not asked yet. */
  | 'checking';

export type ViewLevel = 'none' | 'ok' | 'warn' | 'critical' | 'unknown';

export interface ViewRow {
  readonly id: string;
  readonly label: string;
  readonly value: string;
  readonly state: RowState;
  readonly level: ViewLevel;
  /** Column headers of `items` (empty when the row has none). */
  readonly headers: readonly string[];
  /** A list under the row — processes, cleanup candidates, checks — one array of cell texts per line. */
  readonly items: readonly (readonly string[])[];
}

export interface ViewSection {
  readonly id: string;
  readonly title: string;
  readonly rows: readonly ViewRow[];
}

/** The buttons the page may show; each posts its own id back, and the host accepts only these (`messages.ts`). */
export type PageAction = 'refresh' | 'openSettings' | 'startWsl' | 'installDaemon';

/** One cleanup row's button (E6.S3): the row id of the closed enum, its label, and why it is greyed when it is. */
export interface CleanRow {
  readonly rowId: string;
  readonly label: string;
  readonly enabled: boolean;
  /** "387 · 59.6 GB", "nothing to clean", "unavailable — <reason>". */
  readonly note: string;
}

/**
 * The cleanup controls (E6.S3, `common.durable-status`): every state here is DERIVED by the host from what the daemon
 * reported (`status.running`, `status.capabilities`, the preview's rows) and the host's persisted journal — never only from
 * a flag the page or the window holds — so a reload shows the same "Cleaning… A4" the daemon reports.
 */
export interface CleanupControls {
  /** Whether a cleanup may be started now (each row also needs something to clean). */
  readonly enabled: boolean;
  /** What is in flight: "Cleaning… A4", "Queued… A4", "Wedged: …", "Waiting for run …'s result…" — or ''. */
  readonly state: string;
  readonly stateLevel: ViewLevel;
  /** Why the buttons are greyed ('' when they are not). */
  readonly reason: string;
  readonly rows: readonly CleanRow[];
  readonly fullCheck: boolean;
  /** A wedged run the daemon can stop: the index the page sends back, and the button's label. */
  readonly stop: { readonly index: number; readonly label: string } | undefined;
  /** A wedged run the daemon cannot stop (outside its units): the words, with its pid. */
  readonly stopText: string;
  /** The terminal answers this window showed, newest first. */
  readonly results: readonly { readonly sentence: string; readonly level: ViewLevel }[];
  /** "Docker after": the preview's reclaimable total, labelled with the time it was read. */
  readonly dockerAfter: string;
}

export interface PanelView {
  readonly heading: string;
  /** One sentence above the sections ('' when there is nothing to say). */
  readonly notice: string;
  readonly noticeLevel: ViewLevel;
  readonly actions: readonly { readonly id: PageAction; readonly label: string }[];
  readonly sections: readonly ViewSection[];
  readonly cleanup: CleanupControls;
}
