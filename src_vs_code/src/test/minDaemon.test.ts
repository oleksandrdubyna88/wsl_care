import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

import { MIN_DAEMON_FOR_ACTIONS, MIN_DAEMON_FOR_RENDER, versionAtLeast } from '../client/handshake';
import { EXTENSION_ROOT } from './support/paths';

/**
 * The minimum daemon as an ARTEFACT (E5 code round #2/#5): the release guard reads it at the tag checkout from the
 * checked-in `min-daemon.json` with a JSON parser — never from `handshake.ts` with a line pattern — and the bundle step
 * emits `dist/min-daemon.json` from the compiled constant, which `scripts/check-vsix.mjs` compares with both. This file
 * holds the checked-in copy equal to the constant on every pull request, so it cannot drift until a release day.
 */

function readJson(file: string): unknown {
  return JSON.parse(fs.readFileSync(file, 'utf8')) as unknown;
}

test('the checked-in min-daemon.json — what the release guard reads at the tag — is MIN_DAEMON_FOR_RENDER and MIN_DAEMON_FOR_ACTIONS, and nothing else', () => {
  assert.deepEqual(readJson(path.join(EXTENSION_ROOT, 'min-daemon.json')), { minDaemonForRender: MIN_DAEMON_FOR_RENDER, minDaemonForActions: MIN_DAEMON_FOR_ACTIONS },
    'change the constant and this file in the same commit: the guard refuses a release whose minimum daemon is not published and verified');
});

test('the bundle step emitted dist/min-daemon.json from the compiled constant (npm test bundles before it tests)', () => {
  assert.deepEqual(readJson(path.join(EXTENSION_ROOT, 'dist', 'min-daemon.json')), { minDaemonForRender: MIN_DAEMON_FOR_RENDER, minDaemonForActions: MIN_DAEMON_FOR_ACTIONS });
});

// ---- coai E6.S2 code round #0: the actions minimum is never below the render minimum ----

test('MIN_DAEMON_FOR_ACTIONS is at or above MIN_DAEMON_FOR_RENDER, by the extension\'s own version comparison', () => {
  assert.equal(versionAtLeast(MIN_DAEMON_FOR_ACTIONS, MIN_DAEMON_FOR_RENDER), true,
    `Install daemon types ${MIN_DAEMON_FOR_ACTIONS}; below the render minimum ${MIN_DAEMON_FOR_RENDER} it would install a daemon this extension refuses to render`);
});

test('the comparison compares numbers field by field — 0.10.0 is above 0.9.0, a pre-release text is never "at least"', () => {
  assert.equal(versionAtLeast('0.10.0', '0.9.0'), true);
  assert.equal(versionAtLeast('0.1.0', '0.1.0'), true);
  assert.equal(versionAtLeast('0.0.9', '0.1.0'), false);
  assert.equal(versionAtLeast('1.0.0', '0.99.99'), true);
  assert.equal(versionAtLeast('unknown', '0.1.0'), false);
});
