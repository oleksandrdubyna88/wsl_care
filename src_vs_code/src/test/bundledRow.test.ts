import assert from 'node:assert/strict';
import { test } from 'node:test';

import { BundledHost } from '../bundled/bundledHost';
import { NO_WINDOWS_DAEMON, windowsDaemonView } from '../bundled/bundledView';
import type { BundledRead } from '../client/WindowsCareClient';

/**
 * The panel's Windows row (E7.S5a, research/PLAN_bundle_windows_binary.md §3.7): the bundled exe's version against BUNDLED_DAEMON,
 * a mismatch shown, a refused start shown with Node's message, nothing off Windows — and the host starts nothing until the panel
 * asks.
 */

const PIN = '0.4.0';

test('the bundled version that matches the pin is the build this extension carries; any other is shown as a mismatch', () => {
  assert.deepEqual(windowsDaemonView({ kind: 'version', text: '0.4.0+0123456789abcdef0123456789abcdef01234567' }, PIN), { line: 'Windows daemon (bundled): 0.4.0+0123456789abcdef0123456789abcdef01234567 — the build this extension carries', level: 'none' });
  for (const text of ['0.3.0+abc', '0.0.0+dev', 'unknown', 'garbage<script>']) {
    const view = windowsDaemonView({ kind: 'version', text }, PIN);
    assert.equal(view.level, 'warn', text);
    assert.match(view.line, /— not 0\.4\.0, the build this extension was packaged with: reinstall the extension$/, text);
  }
});

test('a refused start shows its reason; nothing is said off Windows; "checking…" before the first read', () => {
  assert.deepEqual(windowsDaemonView({ kind: 'failed', reason: 'it could not start: spawn UNKNOWN: An Application Control policy has blocked this file.' }, PIN), { line: 'Windows daemon (bundled): could not run — it could not start: spawn UNKNOWN: An Application Control policy has blocked this file.', level: 'warn' });
  assert.deepEqual(windowsDaemonView({ kind: 'unavailable', reason: 'not Windows' }, PIN), NO_WINDOWS_DAEMON);
  assert.deepEqual(windowsDaemonView(undefined, PIN), { line: 'Windows daemon (bundled): checking…', level: 'none' });
});

test('the host starts nothing until the panel asks, shares a read in flight, and tells every listener', async () => {
  let asked = 0;
  let answer: (read: BundledRead) => void = () => undefined;
  const host = new BundledHost({ version: () => { asked++; return new Promise((resolve) => { answer = resolve; }); }, pin: PIN });
  assert.equal(asked, 0, 'nothing at construction — never at activation');
  assert.match(host.view().line, /checking…/);
  let heard = 0;
  host.onChange(() => { throw new Error('a broken listener'); });
  host.onChange(() => { heard++; });
  const a = host.refresh();
  const b = host.refresh();
  assert.equal(asked, 1, 'one read in flight');
  answer({ kind: 'version', text: '0.4.0+abc' });
  await Promise.all([a, b]);
  assert.equal(heard, 1, 'a throwing listener stops neither the others nor the refresh');
  assert.equal(host.view().level, 'none');
});

test('a version() that throws is a read that could not start — never a rejected refresh', async () => {
  const host = new BundledHost({ version: () => Promise.reject(new Error('EPERM')), pin: PIN });
  await host.refresh();
  assert.match(host.view().line, /could not run — it could not start: Error: EPERM/);
});
