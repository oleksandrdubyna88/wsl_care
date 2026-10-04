import { randomBytes } from 'node:crypto';

/**
 * The panel's webview page as the host hands it to VS Code (plan §15g M7, m10): a STATIC shell. It carries no daemon
 * data at all — the data arrives afterwards by `postMessage` and the page builds its DOM with `createElement` /
 * `textContent` (`media/panel.js`) — so there is nothing here to escape, and the TypeScript doctrine's
 * `scriptInterpolation` scan stays green with an EMPTY allowlist.
 *
 * <p>The only values interpolated are this render's nonce (128 bits from `crypto.randomBytes`, checked to be hex) and
 * the two `media/` URIs VS Code produced (checked to hold no quote or angle bracket). The CSP allows nothing by
 * default and scripts / styles only by that nonce: no inline handler, no `eval`, no remote load.</p>
 */

export interface ShellParts {
  readonly nonce: string;
  readonly scriptUri: string;
  readonly styleUri: string;
}

const NONCE = /^[0-9a-f]{32}$/;
const SAFE_URI = /^[^"'<>\s]+$/;

/** A fresh nonce for one render. */
export function newNonce(): string {
  return randomBytes(16).toString('hex');
}

function checked(parts: ShellParts): ShellParts {
  if (!NONCE.test(parts.nonce)) {
    throw new Error('panelShell: the nonce must be 32 hex characters');
  }
  if (!SAFE_URI.test(parts.scriptUri) || !SAFE_URI.test(parts.styleUri)) {
    throw new Error('panelShell: a media URI holds a quote, an angle bracket or a space');
  }

  return parts;
}

export function panelShell(parts: ShellParts): string {
  const { nonce, scriptUri, styleUri } = checked(parts);

  return [
    '<!DOCTYPE html>',
    '<html lang="en">',
    '<head>',
    '<meta charset="utf-8">',
    `<meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'nonce-${nonce}'; style-src 'nonce-${nonce}';">`,
    '<meta name="viewport" content="width=device-width, initial-scale=1">',
    `<link rel="stylesheet" nonce="${nonce}" href="${styleUri}">`,
    '<title>WSL Care</title>',
    '</head>',
    '<body>',
    '<main id="panel"></main>',
    `<script nonce="${nonce}" src="${scriptUri}"></script>`,
    '</body>',
    '</html>',
  ].join('\n');
}

/** The webview options: scripts on (the page script), command URIs OFF, resources only from `media/`. */
export function panelOptions<T>(media: T): { enableScripts: boolean; enableCommandUris: boolean; localResourceRoots: T[] } {
  return { enableScripts: true, enableCommandUris: false, localResourceRoots: [media] };
}
