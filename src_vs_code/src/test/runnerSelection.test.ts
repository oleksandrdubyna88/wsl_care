import assert from 'node:assert/strict';
import * as path from 'node:path';
import { test } from 'node:test';

import { CLOSED_REASON } from '../process/runner';
import { chooseRunner, FAKE_WSL_VARIABLE, runnerFor } from '../process/runnerSelection';
import { FAKE_SCRIPT } from './support/paths';

/**
 * Which runner the extension wires (plan §15g M6): the real one in production and development; the strict fake ONLY
 * when the extension host says Test mode AND names the fake; Test mode without a fake fails CLOSED — it spawns nothing.
 * `extension.ts` passes `context.extensionMode === ExtensionMode.Test`; an environment variable alone never switches
 * a production extension to anything.
 */

test('outside Test mode the runner is the real one, whatever the environment names', () => {
  assert.deepEqual(chooseRunner(false, {}), { kind: 'real' });
  assert.deepEqual(chooseRunner(false, { [FAKE_WSL_VARIABLE]: FAKE_SCRIPT }), { kind: 'real' }, 'a production extension is never redirected by a variable');
});

test('Test mode with the fake named by an absolute .js path wires the fake', () => {
  assert.deepEqual(chooseRunner(true, { [FAKE_WSL_VARIABLE]: FAKE_SCRIPT }), { kind: 'fake', script: FAKE_SCRIPT });
});

test('Test mode without a usable fake is closed: nothing will be spawned', () => {
  for (const env of [{}, { [FAKE_WSL_VARIABLE]: '' }, { [FAKE_WSL_VARIABLE]: 'fakeWsl.js' }, { [FAKE_WSL_VARIABLE]: path.join(path.dirname(FAKE_SCRIPT), 'fakeWsl.cmd') }]) {
    const choice = chooseRunner(true, env);
    assert.equal(choice.kind, 'closed', JSON.stringify(env));
  }
});

test('the closed choice yields a runner that answers failedToStart without starting anything', async () => {
  const runner = runnerFor(chooseRunner(true, {}));
  const result = await runner({ file: 'C:\\Windows\\System32\\wsl.exe', args: ['--list', '--quiet'], timeoutMs: 1_000 });
  assert.deepEqual(result, { kind: 'failedToStart', reason: CLOSED_REASON });
});

test('the fake choice yields a runner that starts the fake script', async () => {
  const runner = runnerFor({ kind: 'fake', script: FAKE_SCRIPT });
  // No scenario: the fake refuses to answer — proof that it, and not anything else, was started.
  const result = await runner({ file: 'C:\\Windows\\System32\\wsl.exe', args: ['--list', '--quiet'], timeoutMs: 10_000 });
  assert.ok(result.kind === 'exited', JSON.stringify(result));
  assert.equal(result.code, 97);
  assert.match(result.stderr.toString('utf8'), /fake wsl: no scenario/);
});
