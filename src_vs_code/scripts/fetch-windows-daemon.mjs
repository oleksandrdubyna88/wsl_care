#!/usr/bin/env node
/**
 * `npm run fetch:windows-daemon` (E7.S5a, research/PLAN_bundle_windows_binary.md §3.2, §3.6): fetches the Windows daemon the
 * `.vsix` carries into `src_vs_code/bin/` — `bin/wsl-care.exe` and its `.sha256` — through THE fetch script,
 * `.github/scripts/fetch-windows-daemon.sh`, which verifies the release's asset set, its exact attestation and the one zip
 * member. The version is `bundledDaemon` of the checked-in `min-daemon.json`, read as JSON (one reader in this context —
 * pull-request CI runs this; the release reads the guard's `bundled_daemon`). `gh` must be signed in (`gh auth login`, or
 * `GH_TOKEN`).
 *
 * The script runs under bash: `bash` off Windows; on Windows the `bin\bash.exe` of the Git for Windows installation the `git`
 * on PATH belongs to — never a bare `bash`, which there may be `System32\bash.exe`, the WSL launcher.
 *
 * Exit: the fetch script's own (0 fetched, 1 refused, 2 usage); 2 here when min-daemon.json or a bash cannot be read.
 */
import { spawnSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const SCRIPT = join(ROOT, '..', '.github', 'scripts', 'fetch-windows-daemon.sh');

function fail(message) {
  console.error(`fetch:windows-daemon: ${message}`);
  process.exit(2);
}

/** The bundled pin, read as JSON — never a pattern over TypeScript. */
function bundledDaemon() {
  try {
    const value = JSON.parse(readFileSync(join(ROOT, 'min-daemon.json'), 'utf8')).bundledDaemon;
    return typeof value === 'string' && /^\d+\.\d+\.\d+$/.test(value) ? value : fail(`min-daemon.json carries no bundledDaemon "x.y.z" (${String(value)})`);
  } catch (error) {
    return fail(`min-daemon.json cannot be read: ${error instanceof Error ? error.message : String(error)}`);
  }
}

/** Git for Windows' bash: `git --exec-path` is <install>/mingw64/libexec/git-core; bash.exe is <install>/bin/bash.exe. */
function gitBash() {
  const exec = spawnSync('git', ['--exec-path'], { encoding: 'utf8', timeout: 20_000 });
  const found = exec.status === 0 ? join(exec.stdout.trim(), '..', '..', '..', 'bin', 'bash.exe') : '';

  return found !== '' && existsSync(found) ? found : fail('no Git for Windows bash found beside the git on PATH');
}

const bash = process.platform === 'win32' ? gitBash() : 'bash';
const slash = (path) => path.replace(/\\/g, '/');
const run = spawnSync(bash, [slash(SCRIPT), bundledDaemon(), slash(ROOT)], { stdio: 'inherit', timeout: 600_000 });
process.exit(run.status ?? 2);
