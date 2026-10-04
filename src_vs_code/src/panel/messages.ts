/**
 * The CLOSED set of messages the panel's page may send the host (plan §15g m10), each validated EXACTLY — no extra
 * key, no other type. Nothing the page sends becomes argv: `refresh` and `startWsl` trigger a round the host builds
 * from its own closed verb set, `openSettings` opens the settings UI, `ready` asks for the current view, and `rendered`
 * reports how many rows the page drew (the extension-host scenarios read it). E5.S3 adds `installDaemon`.
 */

export type PageMessage =
  | { readonly type: 'ready' }
  | { readonly type: 'rendered'; readonly rows: number }
  | { readonly type: 'refresh' }
  | { readonly type: 'openSettings' }
  | { readonly type: 'startWsl' };

const BARE: ReadonlySet<string> = new Set(['ready', 'refresh', 'openSettings', 'startWsl']);

function isRecord(raw: unknown): raw is Readonly<Record<string, unknown>> {
  return typeof raw === 'object' && raw !== null && !Array.isArray(raw);
}

function bare(type: unknown, keys: readonly string[]): PageMessage | undefined {
  return typeof type === 'string' && BARE.has(type) && keys.length === 1 ? ({ type } as PageMessage) : undefined;
}

function isRowCount(rows: unknown): rows is number {
  return typeof rows === 'number' && Number.isInteger(rows) && rows >= 0;
}

function rendered(raw: Readonly<Record<string, unknown>>, keys: readonly string[]): PageMessage | undefined {
  const rows = raw.rows;

  return keys.length === 2 && isRowCount(rows) ? { type: 'rendered', rows } : undefined;
}

/** The message, or `undefined` for anything outside the closed set — which the host drops. */
export function parsePageMessage(raw: unknown): PageMessage | undefined {
  if (!isRecord(raw)) {
    return undefined;
  }
  const keys = Object.keys(raw);

  return raw.type === 'rendered' ? rendered(raw, keys) : bare(raw.type, keys);
}
