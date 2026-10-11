import type { BundledRead } from '../client/WindowsCareClient';
import { parseDaemonVersion } from '../client/handshake';
import { safeText } from '../text/safeText';

/**
 * The panel's Windows row (E7.S5a, research/PLAN_bundle_windows_binary.md §3.7): the version the BUNDLED `bin/wsl-care.exe`
 * answers, compared with `BUNDLED_DAEMON` — the build this extension was packaged with. The WSL side runs whatever the owner
 * installed and keeps its own line; there is no "one version on both sides". Pure: the host holds the read.
 */

export interface WindowsDaemonView {
  /** One line for *Health*; '' when nothing is said (off Windows, or an extension not on a local disk). */
  readonly line: string;
  readonly level: 'none' | 'warn';
}

export const NO_WINDOWS_DAEMON: WindowsDaemonView = { line: '', level: 'none' };

const PREFIX = 'Windows daemon (bundled): ';

/** A version text the exe printed — untrusted output, so bounded and made safe before it is shown. */
const MAX_VERSION = 120;

function versionView(text: string, pin: string): WindowsDaemonView {
  const parsed = parseDaemonVersion(text);
  const shown = safeText(text, MAX_VERSION);

  return parsed.kind === 'release' && parsed.parts.join('.') === pin
    ? { line: `${PREFIX}${shown} — the build this extension carries`, level: 'none' }
    : { line: `${PREFIX}${shown} — not ${pin}, the build this extension was packaged with: reinstall the extension`, level: 'warn' };
}

const BY_KIND: { readonly [K in BundledRead['kind']]: (r: Extract<BundledRead, { kind: K }>, pin: string) => WindowsDaemonView } = {
  version: (r, pin) => versionView(r.text, pin),
  unavailable: () => NO_WINDOWS_DAEMON,
  failed: (r) => ({ line: `${PREFIX}could not run — ${safeText(r.reason, 300)}`, level: 'warn' }),
};

/** The row for the host's last read — "checking…" before the first one answered. */
export function windowsDaemonView(read: BundledRead | undefined, pin: string): WindowsDaemonView {
  return read === undefined
    ? { line: `${PREFIX}checking…`, level: 'none' }
    : (BY_KIND[read.kind] as (r: BundledRead, pin: string) => WindowsDaemonView)(read, pin);
}
