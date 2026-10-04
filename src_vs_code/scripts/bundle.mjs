#!/usr/bin/env node
/**
 * Bundles the extension into dist/extension.js — the one file the .vsix runs (plan §15g M8). esbuild's own JavaScript
 * API with the options the CLI step used to carry, plus ONE addition: the build stamp. `WSL_CARE_BUILD_STAMP` is
 * replaced with "wsl-care-build <the version in package.json>" (src/buildStamp.ts), so the bundle records the version it
 * was built for and scripts/check-vsix.mjs refuses a .vsix whose bundle was built for another — the family's
 * "Verify the ARTEFACT" rule (a .vsix once shipped JavaScript three versions old because the bundle step had not run).
 *
 *   - CommonJS for node18 — the Node of VS Code 1.85 (engines.vscode ^1.85.0);
 *   - `vscode` external — the editor provides it;
 *   - no source map and no sourcesContent — nothing of the sources or this machine's paths ships;
 *   - not minified — the bundle scan reads it, and a reader of the .vsix can too.
 *
 * It also EMITS dist/min-daemon.json — `{ "minDaemonForRender": "<x.y.z>" }`, the minimum daemon this build renders and
 * installs (E5 code round #2/#5). The value is read by RUNNING src/client/handshake.ts (esbuild's transform, then a
 * bounded node:vm with an empty context — the module imports types only), never with a pattern over its text.
 * scripts/check-vsix.mjs compares it with the compiled constant and with the checked-in src_vs_code/min-daemon.json —
 * the artefact the release guard reads at the tag, with a JSON parser, instead of parsing TypeScript.
 *
 * Run through `npm run bundle` (which cleans dist/ first) and, from vsce, through `vscode:prepublish`. manifest.test.ts
 * reads these options back with the TypeScript parser.
 */
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { runInNewContext } from 'node:vm';
import { buildSync, transformSync } from 'esbuild';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const RELEASE_VERSION = /^\d+\.\d+\.\d+$/;
const { version } = JSON.parse(readFileSync(join(ROOT, 'package.json'), 'utf8'));
if (typeof version !== 'string' || !RELEASE_VERSION.test(version)) {
  console.error(`bundle: package.json carries no x.y.z version (${String(version)})`);
  process.exit(1);
}

/** MIN_DAEMON_FOR_RENDER as the module itself exports it — transformed by esbuild and run, not matched as text. */
function minDaemonForRender() {
  const { code } = transformSync(readFileSync(join(ROOT, 'src', 'client', 'handshake.ts'), 'utf8'), { loader: 'ts', format: 'cjs', target: 'node18' });
  const module = { exports: {} };
  runInNewContext(code, { module, exports: module.exports }, { timeout: 5000 });
  const value = module.exports.MIN_DAEMON_FOR_RENDER;
  if (typeof value !== 'string' || !RELEASE_VERSION.test(value)) {
    console.error(`bundle: src/client/handshake.ts exports no x.y.z MIN_DAEMON_FOR_RENDER (${String(value)})`);
    process.exit(1);
  }

  return value;
}

buildSync({
  absWorkingDir: ROOT,
  entryPoints: ['src/extension.ts'],
  outfile: 'dist/extension.js',
  bundle: true,
  external: ['vscode'],
  format: 'cjs',
  platform: 'node',
  target: 'node18',
  sourcemap: false,
  minify: false,
  logLevel: 'warning',
  define: { WSL_CARE_BUILD_STAMP: JSON.stringify(`wsl-care-build ${version}`) },
});

const minDaemon = minDaemonForRender();
mkdirSync(join(ROOT, 'dist'), { recursive: true });
writeFileSync(join(ROOT, 'dist', 'min-daemon.json'), `${JSON.stringify({ minDaemonForRender: minDaemon }, null, 2)}\n`);
console.log(`bundle: dist/extension.js built for ${version}, dist/min-daemon.json says ${minDaemon}`);
