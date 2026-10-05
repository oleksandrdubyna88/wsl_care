import { ROW_IDS, rowIdOf, type RowId } from '../cleanup/rowIds';

/**
 * The CLOSED set of messages the panel's page may send the host (plan §15g m10, extended by §15j M8), each validated
 * EXACTLY — no extra key, no other type. Nothing the page sends becomes argv: `refresh` and `startWsl` trigger a round the
 * host builds from its own closed verb set, `openSettings` opens the settings UI, `ready` asks for the current view,
 * `rendered` reports how many rows the page drew (the extension-host scenarios read it), and `installDaemon` (E5.S3) asks
 * the host to start its *Install daemon* flow — bare, like the others.
 *
 * <p>E6.S3's four: `clean` (one row's button) and `cleanSelected` (*Clean selected*) carry ROW IDS of the compiled closed
 * enum (`cleanup/rowIds.ts`) — each once, never a name, a volume, a flag or a token: the host previews, shows the modal,
 * holds the preview and confirms it itself; `runFullCheck` is bare; `stop` carries an INDEX into the list of stoppable runs
 * the host itself read from `status.running` — the run id never comes from the page (the host checks the one it holds
 * against the daemon's spelling once more before a call).</p>
 *
 * <p>E6.S4's `openRunLogs` (*Logs* beside *Last cleanup*) is bare too: the host opens the Logs page on the run
 * `status.lastCleanup` names — the id is the host's.</p>
 */

export type PageMessage =
  | { readonly type: 'ready' }
  | { readonly type: 'rendered'; readonly rows: number }
  | { readonly type: 'refresh' }
  | { readonly type: 'openSettings' }
  | { readonly type: 'startWsl' }
  | { readonly type: 'installDaemon' }
  | { readonly type: 'openRunLogs' }
  | { readonly type: 'clean'; readonly rowIds: readonly RowId[] }
  | { readonly type: 'cleanSelected'; readonly rowIds: readonly RowId[] }
  | { readonly type: 'runFullCheck' }
  | { readonly type: 'stop'; readonly index: number };

const BARE: ReadonlySet<string> = new Set(['ready', 'refresh', 'openSettings', 'startWsl', 'installDaemon', 'runFullCheck', 'openRunLogs']);

/** The host holds at most this many stoppable runs plus one (the daemon has ONE running state; the list is a list for shape only). */
export const MAX_STOP_INDEX = 3;

type Raw = Readonly<Record<string, unknown>>;

function isRecord(raw: unknown): raw is Raw {
  return typeof raw === 'object' && raw !== null && !Array.isArray(raw);
}

function bare(type: unknown, keys: readonly string[]): PageMessage | undefined {
  return typeof type === 'string' && BARE.has(type) && keys.length === 1 ? ({ type } as PageMessage) : undefined;
}

function isIndex(value: unknown, max: number): value is number {
  return typeof value === 'number' && Number.isInteger(value) && value >= 0 && value <= max;
}

function isRowList(value: unknown): value is readonly unknown[] {
  return Array.isArray(value) && value.length > 0 && value.length <= ROW_IDS.length;
}

/** Row ids: a non-empty list of the enum's values, each once. */
function rowIdsOf(value: unknown): readonly RowId[] | undefined {
  if (!isRowList(value)) {
    return undefined;
  }
  const ids = value.flatMap((v) => rowIdOf(v) ?? []);

  return ids.length === value.length && new Set(ids).size === ids.length ? ids : undefined;
}

type WithValue = (raw: Raw) => PageMessage | undefined;

/** A row's `clean` is ONE row (review A2: its modal names one); several are `cleanSelected`'s. */
const MOST_ROWS: { readonly [K in 'clean' | 'cleanSelected']: number } = { clean: 1, cleanSelected: ROW_IDS.length };

function withRows(type: 'clean' | 'cleanSelected', raw: Raw): PageMessage | undefined {
  const rowIds = rowIdsOf(raw.rowIds);

  return rowIds === undefined || rowIds.length > MOST_ROWS[type] ? undefined : { type, rowIds };
}

/** The messages that carry ONE value besides their type — exactly two keys each. */
const WITH_VALUE: Readonly<Record<string, WithValue>> = {
  rendered: (raw) => (isIndex(raw.rows, Number.MAX_SAFE_INTEGER) ? { type: 'rendered', rows: raw.rows } : undefined),
  stop: (raw) => (isIndex(raw.index, MAX_STOP_INDEX) ? { type: 'stop', index: raw.index } : undefined),
  clean: (raw) => withRows('clean', raw),
  cleanSelected: (raw) => withRows('cleanSelected', raw),
};

function readerOf(type: unknown): WithValue | undefined {
  return typeof type === 'string' && Object.hasOwn(WITH_VALUE, type) ? WITH_VALUE[type] : undefined;
}

function valued(raw: Raw, keys: readonly string[]): PageMessage | undefined {
  const read = readerOf(raw.type);

  return read !== undefined && keys.length === 2 ? read(raw) : undefined;
}

/** Every message type the page may send — what the flow catalogue derives its `message <type>` rows from. */
export const PAGE_MESSAGE_TYPES: readonly PageMessage['type'][] = [...BARE, ...Object.keys(WITH_VALUE)] as PageMessage['type'][];

/** The message, or `undefined` for anything outside the closed set — which the host drops. */
export function parsePageMessage(raw: unknown): PageMessage | undefined {
  if (!isRecord(raw)) {
    return undefined;
  }
  const keys = Object.keys(raw);

  return bare(raw.type, keys) ?? valued(raw, keys);
}
