import assert from 'node:assert/strict';
import { test } from 'node:test';

import { WindowsCareClient } from '../client/WindowsCareClient';
import type { VerbOutcome } from '../client/outcome';
import { Poller } from '../poll/poller';
import { OutcomeStore } from '../state/outcomeStore';
import { fakeWorld, UBUNTU_RUNNING } from './support/fakeWorld';
import { answered, headBody } from './support/outcomes';

/**
 * E19.S2b review (coai plan round): a hung bundled exe ends at ITS ceiling as the client's own timeout failure — the runner's kill
 * goes through the fake runner (a real child process of the strict fake, killed at the ceiling) — and the distribution's slot is
 * untouched by it.
 */

const EXTENSION = { scheme: 'file', fsPath: 'C:\\Users\\u\\.vscode\\extensions\\pub.ai-os-care-0.6.0-win32-x64' };
const EXE = `${EXTENSION.fsPath}\\bin\\wsl-care.exe`;
const CEILING_MS = 1_500;

test('a hung windows status ends at its ceiling as a timeout, the runner kills the exe, and the distro slot is untouched', async () => {
  const world = fakeWorld({ ...UBUNTU_RUNNING, bundledExe: EXE, bundledStatusDelayMs: 60_000 });
  try {
    const client = new WindowsCareClient({ runner: world.runner, platform: 'win32', extension: EXTENSION, timeoutMs: () => 20_000, statusTimeoutMs: () => CEILING_MS });
    const store = new OutcomeStore();
    const distro: VerbOutcome = answered('status', headBody('status'));
    const poller = new Poller({
      run: () => Promise.resolve(distro), store, focused: () => true, refreshSeconds: () => undefined, timers: { every: () => () => undefined }, target: () => '',
      windows: { usable: () => typeof client.exe() === 'string', status: () => client.status() },
    });
    const started = performance.now();
    await poller.tick();
    const elapsed = performance.now() - started;
    assert.deepEqual(store.snapshot().windowsStatus, { kind: 'timedOut', timeoutMs: CEILING_MS, verb: 'status' }, 'the client\'s own timeout failure');
    assert.ok(elapsed < 20_000, `ended by the ceiling, not by the 60 s the exe would take (${Math.round(elapsed)} ms)`);
    assert.equal(store.snapshot().status, distro, 'the distribution\'s slot is the very outcome it was given');
    assert.deepEqual(world.files(), [EXE], 'the exe was started once, by its absolute path');
  } finally {
    world.dispose();
  }
});
