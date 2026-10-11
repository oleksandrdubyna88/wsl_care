/**
 * A21's preview answer (S7b.2b), shaped as the daemon's `act A21 --preview --json` writes it: the action's preview with its items
 * (at most 20, `preview.maxItems`), and — an `IBoundToShownList` action — `shown` (every target's `pid:start`, capped at
 * `act.maxShownNames`) and `shownTruncated` exactly when that cap cut it. The daemon-written line
 * (`contracts/golden/head/act-a21-preview-action.json`) holds two relays; a test that needs other lists, counts or states builds
 * the body here, from the same shape.
 */

/** `count` relays from pid 12000 up, start ticks 36500 up — each `pid:start` as the daemon's `SuspectSignals.Shown` spells it. */
export function relayKeys(count: number, from = 0): string[] {
  return Array.from({ length: count }, (_, i) => `${12_000 + from + i}:${36_500 + from + i}`);
}

export interface RelayPreviewOptions {
  /** The daemon's count of selected relays — `shown.length` unless a test makes them disagree. */
  readonly count?: number;
  readonly truncated?: boolean;
  readonly available?: boolean;
  readonly reason?: string;
  readonly basis?: string;
}

/** A21's action entry of a preview answer. */
export function relayAction(shown: readonly string[], options: RelayPreviewOptions = {}): Record<string, unknown> {
  const available = options.available ?? true;
  const items = shown.slice(0, 20).map((key) => {
    const pid = String(key).split(':')[0] ?? '';
    return { kind: 'relay', name: `${pid} creds-mcp`, bytes: 2_048_000, note: `the creds-mcp.exe relay, pid ${pid}, under Relay(7411) (pid 7410), no CPU for 42 min; its client is gone: re-parented, not born there, no other holder of its stdio pipes; SIGTERM ends it and its Windows process creds-mcp.exe; the agent's session may need /mcp to reconnect it` };
  });
  const preview = {
    what: "the target user's interop relays of catalogued Windows MCP servers (creds-mcp) whose client is gone",
    available,
    count: available ? (options.count ?? shown.length) : null,
    bytes: null,
    basis: options.basis ?? 'interop relays read from /proc (exe /init, the raw argv, the parent, fd 0-2, every process\'s fd table: 412 process(es), 9031 fd link(s) in 180 ms); 0 relay(s) of user kept: ',
    facts: { heldMemoryBytes: available ? 2_048_000 * shown.length : 0 },
    refusal: available ? '' : (options.reason ?? 'the process table could not be read'),
    items: available ? items : [],
    skip: '',
    urgent: '',
  };

  return {
    id: 'A21',
    summary: 'SIGTERM only (never SIGKILL) of the target user\'s interop relays of catalogued Windows MCP servers whose client is gone',
    status: 'previewed',
    reason: available ? '' : (options.reason ?? 'the process table could not be read'),
    preview,
    ...(available ? { shown: [...shown] } : {}),
    ...(options.truncated === true ? { shownTruncated: true } : {}),
  };
}

/** A whole `act A21 --preview --json` answer. */
export function relayPreviewBody(shown: readonly string[], options: RelayPreviewOptions = {}): Record<string, unknown> {
  return { schemaVersion: 1, mode: 'preview', result: 'previewed', targetUser: { found: true, name: 'user', home: '/home/user', source: 'single login account' }, actions: [relayAction(shown, options)], productVersion: '0.4.0+0000000000000000000000000000000000000000' };
}
