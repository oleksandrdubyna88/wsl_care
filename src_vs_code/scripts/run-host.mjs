#!/usr/bin/env node
/**
 * Runs the extension-host scenarios (`out/test/host/suite.js`) inside a REAL VS Code — 1.85.0 (the `engines.vscode`
 * floor) AND the current stable — through `@vscode/test-electron` (plan §16 E5.S2, §15g m5). Ported in shape from
 * ConnectOtherAIs' `scripts/run-host.mjs`: test-electron only downloads and launches the editor (no mocha, no second
 * test framework); what runs inside is our own scenario list, which throws on the first failure, and on an empty list.
 *
 * Two launches per version:
 *
 * - **fake** — Test mode with the strict fake `wsl.exe` named (`WSL_CARE_TEST_FAKE_WSL`) over a scenario file the
 *   suite rewrites between scenarios (`WSL_CARE_FAKE_SCENARIO`); every call reaches the fake and is logged;
 * - **closed** — Test mode WITHOUT the fake: the extension must start nothing at all (plan §15g M6, fail closed).
 *
 * No launch can reach the real `wsl.exe`: in Test mode the runner is the fake or the closed one, never the real one
 * (`process/runnerSelection.ts`), and the fake is a Node script started by the extension host's own executable.
 *
 * The downloads land in `.vscode-test/` (git-ignored; CI caches it). The exit code is the whole point: a launch that
 * fails, a scenario that fails, or a suite that ran nothing is non-zero.
 */
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { runTests } from '@vscode/test-electron';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const SUITE = join(ROOT, 'out', 'test', 'host', 'suite.js');
const FAKE = join(ROOT, 'out', 'test', 'fake', 'fakeWsl.js');
const GOLDEN = join(ROOT, '..', 'contracts', 'golden', 'head');
const VERSIONS = (process.env.WSL_CARE_HOST_VERSIONS ?? '1.85.0,stable').split(',').map((v) => v.trim()).filter((v) => v.length > 0);

/** How long one launch (download included) may take before that is the failure. */
const LAUNCH_MS = 10 * 60 * 1000;

/**
 * Variables a run started from INSIDE VS Code inherits and which break the child editor — `ELECTRON_RUN_AS_NODE=1` makes
 * Code.exe behave as plain Node (ConnectOtherAIs measured it). Removed before every launch.
 */
const POISONED = ['ELECTRON_RUN_AS_NODE', 'VSCODE_PID', 'VSCODE_CWD', 'VSCODE_IPC_HOOK', 'VSCODE_NLS_CONFIG',
  'VSCODE_CODE_CACHE_PATH', 'VSCODE_ESM_ENTRYPOINT', 'VSCODE_L10N_BUNDLE_LOCATION',
  'VSCODE_HANDLES_UNCAUGHT_ERRORS', 'VSCODE_CRASH_REPORTER_PROCESS_TYPE'];

const bounded = (ms, what) => new Promise((_, reject) => {
  setTimeout(() => { reject(new Error(`${what} did not finish within ${Math.round(ms / 1000)} s`)); }, ms).unref();
});

/** One launch of `version` in `mode`, in its own workspace, user-data and scenario folders, removed after. */
async function launch(version, mode) {
  const folder = mkdtempSync(join(tmpdir(), 'wsl-care-host-'));
  const scenario = join(folder, 'scenario.json');
  writeFileSync(scenario, JSON.stringify({ distros: [{ name: 'Ubuntu', running: true }], defaultDistro: 'Ubuntu', binary: 'present', answers: GOLDEN, log: join(folder, 'calls.jsonl') }));
  const env = { WSL_CARE_HOST_MODE: mode, WSL_CARE_HOST_GOLDEN: GOLDEN, WSL_CARE_FAKE_SCENARIO: scenario };
  if (mode === 'fake') {
    env.WSL_CARE_TEST_FAKE_WSL = FAKE;
  }
  try {
    await Promise.race([
      runTests({
        version,
        cachePath: join(ROOT, '.vscode-test'),
        extensionDevelopmentPath: ROOT,
        extensionTestsPath: SUITE,
        launchArgs: [folder, '--disable-extensions', '--disable-workspace-trust', `--user-data-dir=${join(folder, 'user-data')}`],
        extensionTestsEnv: env,
      }),
      bounded(LAUNCH_MS, `VS Code ${version} (${mode})`),
    ]);
    console.log(`extension-host scenarios: VS Code ${version}, ${mode} — passed`);
  } finally {
    rmSync(folder, { recursive: true, force: true });
  }
}

async function main() {
  for (const name of POISONED) {
    delete process.env[name];
  }
  for (const version of VERSIONS) {
    for (const mode of ['fake', 'closed']) {
      await launch(version, mode);
    }
  }
}

main()
  .then(() => process.exit(0))
  .catch((reason) => {
    console.error('extension-host scenarios: FAILED');
    console.error(reason instanceof Error ? reason.message : String(reason));
    process.exit(1);
  });
