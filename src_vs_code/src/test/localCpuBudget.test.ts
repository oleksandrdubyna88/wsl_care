import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { test } from 'node:test';

import { EXTENSION_ROOT, REPOSITORY_ROOT } from './support/paths';

/**
 * Local test runs take at most 8 workers and run at lowered CPU priority — the family rule
 * `common.local-cpu-budget` (`.agents/conventions/common/local-cpu-budget.md`).
 *
 * Measured 2026-10-10 on a 24-thread workstation running several agent sessions: a runner left at its default takes
 * one worker per CPU and holds the machine at 100 %. Node's `--test` default is CPUs - 1; xUnit's is one thread per CPU.
 *
 * The extension's CI job checks no submodule out, which is also where the rule does not apply: the cases that need the
 * mount skip, saying why, rather than pass on nothing.
 */

const MOUNT = path.join(REPOSITORY_ROOT, '.agents', 'conventions');
const MOUNT_PRESENT = fs.existsSync(path.join(MOUNT, 'tools', 'low-priority.mjs'));
const read = (file: string): string => fs.readFileSync(file, 'utf8').replace(/\r\n/g, '\n');

test('the runner caps node --test at min(8, CPUs - 1), keeps its tripwire, and lowers itself first', () => {
  const runner = read(path.join(EXTENSION_ROOT, 'scripts', 'run-tests.mjs'));
  assert.match(runner, /const TEST_CONCURRENCY = Math\.max\(1, Math\.min\(8, availableParallelism\(\) - 1\)\);/);
  assert.match(runner, /'--test', `--test-concurrency=\$\{TEST_CONCURRENCY\}`/, 'the spawn passes the cap');
  // The noRealWsl tripwire must still be preloaded into every test process — the cap must not have displaced it.
  assert.match(runner, /\['--require', `\.\/\$\{TRIPWIRE/, 'the spawn still preloads the tripwire');
  assert.match(runner, /await import\('\.\/lower-priority\.mjs'\);/, 'the runner lowers itself before it spawns');
});

/** Every xUnit test project under the daemon's tests, found by what it references rather than by its name. */
function xunitProjects(): string[] {
  const dir = path.join(REPOSITORY_ROOT, 'src_daemon', 'tests');
  return fs.readdirSync(dir, { withFileTypes: true })
    .filter((entry) => entry.isDirectory())
    .flatMap((entry) => fs.readdirSync(path.join(dir, entry.name))
      .filter((file) => file.endsWith('.csproj'))
      .map((file) => path.join(dir, entry.name, file)))
    .filter((project) => /<PackageReference Include="xunit\.v3"/.test(read(project)));
}

test('every xUnit project caps its threads at 8 or fewer and copies the cap to its output for local builds', () => {
  const projects = xunitProjects();
  assert.ok(projects.length >= 4, `found only ${projects.map((p) => path.basename(p)).join(', ')}`);
  for (const project of projects) {
    // Measured 2026-10-11 on xunit.v3 4.0.1: an uncopied xunit.runner.json is never read (24 threads). Not on CI:
    // the value is exact, so a 4-vCPU runner would be RAISED to 8, and CI is exempt.
    assert.match(read(project), /<Content Include="xunit\.runner\.json" CopyToOutputDirectory="PreserveNewest" Condition="'\$\(CI\)' != 'true'" \/>/,
      `${path.basename(project)} does not copy xunit.runner.json to its output`);
    const config = JSON.parse(read(path.join(path.dirname(project), 'xunit.runner.json'))) as { maxParallelThreads?: unknown };
    assert.ok(Number.isInteger(config.maxParallelThreads) && (config.maxParallelThreads as number) >= 1 && (config.maxParallelThreads as number) <= 8,
      `${path.basename(project)}: maxParallelThreads is ${String(config.maxParallelThreads)}`);
  }
});

test('the documented test executables and builds run lowered', () => {
  const lines = ['.agents/PROJECT.md', 'README.md']
    .flatMap((file) => read(path.join(REPOSITORY_ROOT, file)).split('\n'))
    .map((line) => line.trim())
    // An environment prefix (`WSL_CARE_REQUIRE_LIVE=1 …`) is not part of the command.
    .map((line) => line.replace(/^(?:[A-Z_][A-Z0-9_]*=\S+\s+)+/, ''))
    .filter((line) => /^(\.\/src_daemon\/tests\/\S+\.exe|dotnet build|node \S+low-priority\.mjs)/.test(line));
  assert.ok(lines.some((line) => /\.Tests\.exe/.test(line)), 'the scan no longer finds a documented test executable');
  for (const line of lines) {
    assert.match(line, /^node \.agents\/conventions\/tools\/low-priority\.mjs /, `runs at normal priority: ${line}`);
  }
});

test('the preload is the family template, verbatim but for its MOUNT line', { skip: MOUNT_PRESENT ? false : 'the mount is absent (CI checks no submodule out)' }, () => {
  const mountLine = /^const MOUNT = new URL\("([^"]+)", import\.meta\.url\);$/m;
  const template = read(path.join(MOUNT, 'tools', 'templates', 'lower-priority-preload.mjs'));
  const copy = read(path.join(EXTENSION_ROOT, 'scripts', 'lower-priority.mjs'));
  assert.equal(copy.replace(mountLine, 'MOUNT'), template.replace(mountLine, 'MOUNT'));
  assert.equal(path.resolve(EXTENSION_ROOT, 'scripts', copy.match(mountLine)?.[1] ?? ''), MOUNT);
});

test('the preload really lowers the runner and every process it starts', { skip: MOUNT_PRESENT ? false : 'the mount is absent (CI checks no submodule out)' }, () => {
  const env: NodeJS.ProcessEnv = { ...process.env };
  delete env.CI;
  delete env.LOW_PRIORITY_PRELOAD_NOTICE;
  const child = "require('node:child_process').spawnSync(process.execPath,['-e',\"console.log(require('node:os').getPriority(0))\"],{stdio:'inherit'})";
  const result = spawnSync(process.execPath,
    ['--import', './scripts/lower-priority.mjs', '-e', `console.log(require('node:os').getPriority(0));${child}`],
    { cwd: EXTENSION_ROOT, env, encoding: 'utf8', timeout: 30_000 });
  assert.equal(result.status, 0, result.stderr);
  const lowered = process.platform === 'win32' ? os.constants.priority.PRIORITY_BELOW_NORMAL : 19;
  const expected = Math.max(os.getPriority(0), lowered);
  assert.deepEqual(result.stdout.trim().split(/\r?\n/).map(Number), [expected, expected]);
});
