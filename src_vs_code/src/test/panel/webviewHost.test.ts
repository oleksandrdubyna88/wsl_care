import assert from 'node:assert/strict';
import { test } from 'node:test';

import { parsePageMessage } from '../../panel/messages';
import { newNonce, panelOptions, panelShell } from '../../panel/panelHtml';

/**
 * The webview's host side (plan §15g M7, m10): a STATIC shell with a fresh nonce per render and a strict CSP, the
 * webview options (scripts on, command URIs off, resources only from `media/`), and the CLOSED set of messages the
 * host accepts from the page — validated exactly, because nothing the page sends may become anything else.
 */

const SHELL = { nonce: '0123456789abcdef0123456789abcdef', scriptUri: 'https://file+.vscode-resource/media/panel.js', styleUri: 'https://file+.vscode-resource/media/panel.css' };

function cspOf(html: string): Map<string, string[]> {
  const found = /<meta http-equiv="Content-Security-Policy" content="([^"]*)">/.exec(html);
  assert.ok(found?.[1] !== undefined, 'the shell carries a CSP meta element');
  return new Map(found[1].split(';').map((d) => d.trim()).filter((d) => d.length > 0).map((d) => {
    const [name = '', ...sources] = d.split(/\s+/);
    return [name, sources];
  }));
}

test('the CSP: default-src none; scripts and styles ONLY by this render\'s nonce; nothing else allowed', () => {
  const csp = cspOf(panelShell(SHELL));
  assert.deepEqual([...csp.keys()].sort(), ['default-src', 'script-src', 'style-src']);
  assert.deepEqual(csp.get('default-src'), ["'none'"]);
  assert.deepEqual(csp.get('script-src'), [`'nonce-${SHELL.nonce}'`]);
  assert.deepEqual(csp.get('style-src'), [`'nonce-${SHELL.nonce}'`]);
});

test('the shell loads exactly one script and one stylesheet, from media/, each with the nonce — and holds no inline code', () => {
  const html = panelShell(SHELL);
  const scripts = [...html.matchAll(/<script\b([^>]*)>([\s\S]*?)<\/script>/g)];
  assert.equal(scripts.length, 1);
  assert.equal(scripts[0]?.[1], ` nonce="${SHELL.nonce}" src="${SHELL.scriptUri}"`);
  assert.equal(scripts[0]?.[2], '', 'no inline script body');
  assert.match(html, new RegExp(`<link rel="stylesheet" nonce="${SHELL.nonce}" href="${SHELL.styleUri.replace(/[.+]/g, '\\$&')}">`));
  assert.doesNotMatch(html, /\son[a-z]+=/i, 'no inline event handler');
  assert.doesNotMatch(html, /style="/, 'no inline style');
  assert.match(html, /<main id="panel"/);
});

test('the shell refuses a nonce or a URI that is not what it expects, instead of writing it into the page', () => {
  assert.throws(() => panelShell({ ...SHELL, nonce: 'abc' }), /nonce/);
  assert.throws(() => panelShell({ ...SHELL, nonce: `${SHELL.nonce}" onload="x` }), /nonce/);
  assert.throws(() => panelShell({ ...SHELL, scriptUri: 'https://x/"><script>alert(1)</script>' }), /URI/);
});

test('every render gets a fresh 128-bit nonce from crypto.randomBytes', () => {
  const nonces = new Set(Array.from({ length: 50 }, () => newNonce()));
  assert.equal(nonces.size, 50);
  for (const nonce of nonces) {
    assert.match(nonce, /^[0-9a-f]{32}$/);
  }
});

test('the webview options: scripts on, command URIs OFF, local resources only from media/', () => {
  const media = { path: '/ext/media' };
  assert.deepEqual(panelOptions(media), { enableScripts: true, enableCommandUris: false, localResourceRoots: [media] });
});

test('the page may send only ready, rendered, refresh, openSettings, startWsl and installDaemon — each in its exact shape', () => {
  assert.deepEqual(parsePageMessage({ type: 'ready' }), { type: 'ready' });
  assert.deepEqual(parsePageMessage({ type: 'refresh' }), { type: 'refresh' });
  assert.deepEqual(parsePageMessage({ type: 'openSettings' }), { type: 'openSettings' });
  assert.deepEqual(parsePageMessage({ type: 'startWsl' }), { type: 'startWsl' });
  assert.deepEqual(parsePageMessage({ type: 'installDaemon' }), { type: 'installDaemon' });
  assert.deepEqual(parsePageMessage({ type: 'rendered', rows: 42 }), { type: 'rendered', rows: 42 });
});

test('anything else from the page is dropped: unknown types, extra keys, a smuggled argument, bad counts, non-objects', () => {
  const refused: unknown[] = [
    { type: 'act', id: 'A4' }, { type: 'refresh', distro: 'Ubuntu' }, { type: 'startWsl', args: ['-u', 'x'] },
    { type: 'rendered', rows: -1 }, { type: 'rendered', rows: 1.5 }, { type: 'rendered', rows: '3' }, { type: 'rendered' },
    { type: 'ready', extra: true }, null, undefined, 'refresh', ['refresh'], 7, {},
  ];
  for (const raw of refused) {
    assert.equal(parsePageMessage(raw), undefined, JSON.stringify(raw));
  }
});
