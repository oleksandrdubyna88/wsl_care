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
export type PageAction = 'refresh' | 'openSettings' | 'startWsl';

export interface PanelView {
  readonly heading: string;
  /** One sentence above the sections ('' when there is nothing to say). */
  readonly notice: string;
  readonly noticeLevel: ViewLevel;
  readonly actions: readonly { readonly id: PageAction; readonly label: string }[];
  readonly sections: readonly ViewSection[];
}
