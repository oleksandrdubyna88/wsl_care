#!/usr/bin/env node
/**
 * Runs every compiled test file this extension HAS, found by walking `out/test` rather than by a glob — ported from
 * dew_flow_vscode_kit's `scripts/run-tests.mjs` (itself ConnectOtherAIs'): a quoted glob reaches node verbatim on Linux,
 * unquoted cmd passes it verbatim on Windows, and `node --test <dir>` once executed the DIRECTORY as a module. A walk
 * has no shell and no platform, and it cannot forget a file, which is what makes the printed count worth reading.
 *
 * Recursive (the kit's is flat): the scenario flows live in `out/test/scenarios`.
 *
 * Every test process is started with `--require out/test/support/noRealWsl.js`: a tripwire that makes any attempt to
 * spawn a `wsl` / `wsl.exe` throw inside the test process, so no test can reach the real WSL (plan §16 E5.S1
 * acceptance) — and `noRealWsl.test.ts` asserts the tripwire is armed in the process it runs in, so a runner that
 * stopped passing the flag is a red suite rather than a silent loss.
 */
import { spawnSync } from 'node:child_process';
import { existsSync, readdirSync } from 'node:fs';
import { join } from 'node:path';

const TEST_DIR = join('out', 'test');
const SUFFIX = '.test.js';
const TRIPWIRE = join(TEST_DIR, 'support', 'noRealWsl.js');

/** Every `*.test.js` under `dir`, depth first, sorted; an absent directory contributes nothing. */
function discover(dir) {
  let entries;
  try {
    entries = readdirSync(dir, { withFileTypes: true });
  } catch {
    return [];
  }

  return entries
    .sort((a, b) => a.name.localeCompare(b.name))
    .flatMap((entry) => {
      const full = join(dir, entry.name);
      if (entry.isDirectory()) {
        return discover(full);
      }

      return entry.name.endsWith(SUFFIX) ? [full] : [];
    });
}

const files = discover(TEST_DIR);
if (files.length === 0) {
  console.error(`no compiled test files in ${TEST_DIR} — did the compile step run?`);
  process.exit(1);
}
if (!existsSync(TRIPWIRE)) {
  console.error(`the tripwire ${TRIPWIRE} is missing — refusing to run tests that could reach the real wsl.exe`);
  process.exit(1);
}
console.log(`run-tests: ${files.length} compiled test file(s)`);

const result = spawnSync(process.execPath, ['--require', `./${TRIPWIRE.split('\\').join('/')}`, '--test', ...files], { stdio: 'inherit' });
process.exit(result.status ?? 1);
