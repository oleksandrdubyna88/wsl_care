/**
 * The CLOSED set of messages the Archive page may send the host (plan §15s D6, E10.S2a — the panel's m10 rule, as the Logs page
 * has it in `logsPage/logsMessages.ts`), each validated EXACTLY: no extra key, no other type, no other value shape. Nothing the
 * page sends becomes argv: the one read is built by the host (`archive list --json`), and
 *
 * - `expandMonth` / `collapseMonth` carry an INDEX into the months of the list the HOST read — the host checks it against that
 *   list and keeps the month open by its agent and month; a month name, an agent, a session path or an entry id never comes from
 *   the page;
 * - `showPaths` / `hidePaths` switch the host's own toggle;
 * - `ready` asks for the current view (the host reads only when it holds nothing), `refresh` reads again, `rendered` reports how
 *   many agent blocks the page drew (the extension-host scenarios read it).
 *
 * An index is checked here only for its SHAPE (a whole number from 0); whether it names a month is the host's question, and a
 * month the list no longer holds is told on the page.
 */

export type ArchiveMessage =
  | { readonly type: 'ready' }
  | { readonly type: 'refresh' }
  | { readonly type: 'showPaths' }
  | { readonly type: 'hidePaths' }
  | { readonly type: 'expandMonth'; readonly index: number }
  | { readonly type: 'collapseMonth'; readonly index: number }
  | { readonly type: 'rendered'; readonly blocks: number };

type Raw = Readonly<Record<string, unknown>>;

function isIndex(value: unknown): value is number {
  return typeof value === 'number' && Number.isSafeInteger(value) && value >= 0;
}

/** Each type's exact keys besides `type`, and the check of their values. */
interface Shape {
  readonly keys: readonly string[];
  readonly valid: (raw: Raw) => boolean;
}

const BARE: Shape = { keys: [], valid: () => true };
const INDEXED: Shape = { keys: ['index'], valid: (raw) => isIndex(raw.index) };

const SHAPES: { readonly [K in ArchiveMessage['type']]: Shape } = {
  ready: BARE,
  refresh: BARE,
  showPaths: BARE,
  hidePaths: BARE,
  expandMonth: INDEXED,
  collapseMonth: INDEXED,
  rendered: { keys: ['blocks'], valid: (raw) => isIndex(raw.blocks) },
};

/** Every message type the Archive page may send — what the flow catalogue derives its `archive <type>` rows from. */
export const ARCHIVE_MESSAGE_TYPES = Object.keys(SHAPES) as readonly ArchiveMessage['type'][];

function isRecord(raw: unknown): raw is Raw {
  return typeof raw === 'object' && raw !== null && !Array.isArray(raw);
}

function shapeOf(type: unknown): Shape | undefined {
  return typeof type === 'string' && Object.hasOwn(SHAPES, type) ? SHAPES[type as ArchiveMessage['type']] : undefined;
}

function exactKeys(raw: Raw, shape: Shape): boolean {
  const keys = Object.keys(raw).filter((key) => key !== 'type');

  return keys.length === shape.keys.length && shape.keys.every((key) => keys.includes(key));
}

function checked(raw: Raw): ArchiveMessage | undefined {
  const shape = shapeOf(raw.type);

  return shape !== undefined && exactKeys(raw, shape) && shape.valid(raw) ? ({ ...raw } as ArchiveMessage) : undefined;
}

/** The message, or `undefined` for anything outside the closed set — which the host drops, starting nothing. */
export function parseArchiveMessage(raw: unknown): ArchiveMessage | undefined {
  return isRecord(raw) ? checked(raw) : undefined;
}
