import assert from 'node:assert/strict';
import * as childProcess from 'node:child_process';
import { test } from 'node:test';

import { spawnRunner } from '../process/runner';
import { wslExecutable } from '../wsl/wslExecutable';
import { isWslLauncher, TRIPWIRE_MESSAGE } from './support/noRealWsl';

/**
 * Plan §16 E5.S1 acceptance: "no test can reach the real wsl.exe (asserted)". The tripwire is loaded by the runner
 * script into every test process; these tests run in such a process and prove it is ARMED there — if
 * `scripts/run-tests.mjs` stopped passing `--require`, the first test is red, never silently unprotected.
 */

test('the tripwire is armed in this test process', () => {
  assert.equal((globalThis as Record<string, unknown>).__wslCareTripwireArmed, true, 'run-tests.mjs must start every test process with --require out/test/support/noRealWsl.js');
});

test('starting the real System32 wsl.exe from a test throws before anything starts — through every launcher', () => {
  const real = 'C:\\Windows\\System32\\wsl.exe';
  assert.throws(() => childProcess.spawn(real, ['--list']), new RegExp(TRIPWIRE_MESSAGE));
  assert.throws(() => childProcess.spawnSync(real, ['--list']), new RegExp(TRIPWIRE_MESSAGE));
  assert.throws(() => childProcess.execFile(real, ['--list']), new RegExp(TRIPWIRE_MESSAGE));
  assert.throws(() => childProcess.execFileSync(real, ['--list']), new RegExp(TRIPWIRE_MESSAGE));
  assert.throws(() => childProcess.execSync('wsl --list'), new RegExp(TRIPWIRE_MESSAGE));
  assert.throws(() => childProcess.exec('wsl.exe --list'), new RegExp(TRIPWIRE_MESSAGE));
});

test('the product runner, handed the real environment, cannot start the real wsl.exe from a test', async () => {
  const real = wslExecutable({ SystemRoot: 'C:\\Windows' });
  assert.equal(real, 'C:\\Windows\\System32\\wsl.exe');
  const result = await spawnRunner({ file: real ?? '', args: ['--list', '--quiet'], timeoutMs: 5_000 });
  assert.equal(result.kind, 'failedToStart');
  assert.match(result.kind === 'failedToStart' ? result.reason : '', new RegExp(TRIPWIRE_MESSAGE));
});

test('the tripwire names a WSL launcher by its base name on either path style and leaves other programs alone', () => {
  for (const name of ['wsl', 'wsl.exe', 'WSL.EXE', 'C:\\Windows\\System32\\wsl.exe', '/mnt/c/Windows/System32/wsl.exe', 'D:\\x\\WSL']) {
    assert.equal(isWslLauncher(name), true, name);
  }
  for (const name of [process.execPath, 'wslpath', 'C:\\Windows\\System32\\wslhost.exe', 'node', 'wsl.exe.bak']) {
    assert.equal(isWslLauncher(name), false, name);
  }
});
