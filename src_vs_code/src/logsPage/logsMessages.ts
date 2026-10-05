import { realDay } from './period';

/**
 * The CLOSED set of messages the Logs page may send the host (plan §15j M8 — the panel's m10 rule, `panel/messages.ts`,
 * extended to the second webview), each validated EXACTLY: no extra key, no other type, no other value shape. Nothing the
 * page sends becomes argv by itself:
 *
 * - `today`, `yesterday`, `thisRun` NAME a period — the host builds the window (`period.ts`) and, for *This run*, takes the
 *   run id from what IT read (`status.lastCleanup`, or the run it already shows) — a run id never comes from the page;
 * - `day` / `range` carry real calendar `yyyy-MM-dd` texts (the date picker's values) — the host clamps them to the
 *   retention and checks the order before it builds the window;
 * - `expand` / `collapse` carry an INDEX into the run list the host read itself;
 * - `ready` asks for the current view — the host posts the answers it holds for the current period and reads it only when
 *   it holds none (review C5: a tab returning keeps its expanded runs and asks nothing); `refresh` reads the current period
 *   again; `rendered` reports how many blocks the page drew (the extension-host scenarios read it).
 */

export type LogsMessage =
  | { readonly type: 'ready' }
  | { readonly type: 'refresh' }
  | { readonly type: 'today' }
  | { readonly type: 'yesterday' }
  | { readonly type: 'thisRun' }
  | { readonly type: 'day'; readonly day: string }
  | { readonly type: 'range'; readonly from: string; readonly to: string }
  | { readonly type: 'expand'; readonly index: number }
  | { readonly type: 'collapse'; readonly index: number }
  | { readonly type: 'rendered'; readonly blocks: number };

/** The largest run index the page may name — a bound on the shape; the host checks it against the list it holds. */
export const MAX_RUN_INDEX = 9_999;

type Raw = Readonly<Record<string, unknown>>;

function isIndex(value: unknown, max: number): value is number {
  return typeof value === 'number' && Number.isInteger(value) && value >= 0 && value <= max;
}

/** Each type's exact keys besides `type`, and the check of their values. */
interface Shape {
  readonly keys: readonly string[];
  readonly valid: (raw: Raw) => boolean;
}

const BARE: Shape = { keys: [], valid: () => true };

const SHAPES: { readonly [K in LogsMessage['type']]: Shape } = {
  ready: BARE,
  refresh: BARE,
  today: BARE,
  yesterday: BARE,
  thisRun: BARE,
  day: { keys: ['day'], valid: (raw) => realDay(raw.day) !== undefined },
  range: { keys: ['from', 'to'], valid: (raw) => realDay(raw.from) !== undefined && realDay(raw.to) !== undefined },
  expand: { keys: ['index'], valid: (raw) => isIndex(raw.index, MAX_RUN_INDEX) },
  collapse: { keys: ['index'], valid: (raw) => isIndex(raw.index, MAX_RUN_INDEX) },
  rendered: { keys: ['blocks'], valid: (raw) => isIndex(raw.blocks, Number.MAX_SAFE_INTEGER) },
};

/** Every message type the Logs page may send — what the flow catalogue derives its `logs <type>` rows from. */
export const LOGS_MESSAGE_TYPES = Object.keys(SHAPES) as readonly LogsMessage['type'][];

function isRecord(raw: unknown): raw is Raw {
  return typeof raw === 'object' && raw !== null && !Array.isArray(raw);
}

function shapeOf(type: unknown): Shape | undefined {
  return typeof type === 'string' && Object.hasOwn(SHAPES, type) ? SHAPES[type as LogsMessage['type']] : undefined;
}

function exactKeys(raw: Raw, shape: Shape): boolean {
  const keys = Object.keys(raw).filter((key) => key !== 'type');

  return keys.length === shape.keys.length && shape.keys.every((key) => keys.includes(key));
}

function checked(raw: Raw): LogsMessage | undefined {
  const shape = shapeOf(raw.type);

  return shape !== undefined && exactKeys(raw, shape) && shape.valid(raw) ? ({ ...raw } as LogsMessage) : undefined;
}

/** The message, or `undefined` for anything outside the closed set — which the host drops, starting nothing. */
export function parseLogsMessage(raw: unknown): LogsMessage | undefined {
  return isRecord(raw) ? checked(raw) : undefined;
}
