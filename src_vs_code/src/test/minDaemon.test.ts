import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

import { INSTALL_DAEMON, MIN_DAEMON_FOR_RENDER } from '../client/handshake';
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

test('the checked-in min-daemon.json — what the release guard reads at the tag — is MIN_DAEMON_FOR_RENDER and INSTALL_DAEMON, and nothing else', () => {
  assert.deepEqual(readJson(path.join(EXTENSION_ROOT, 'min-daemon.json')), { minDaemonForRender: MIN_DAEMON_FOR_RENDER, installDaemon: INSTALL_DAEMON },
    'change the constant and this file in the same commit: the guard refuses a release whose minimum daemon is not published and verified');
});

test('the bundle step emitted dist/min-daemon.json from the compiled constants (npm test bundles before it tests)', () => {
  assert.deepEqual(readJson(path.join(EXTENSION_ROOT, 'dist', 'min-daemon.json')), { minDaemonForRender: MIN_DAEMON_FOR_RENDER, installDaemon: INSTALL_DAEMON });
});
