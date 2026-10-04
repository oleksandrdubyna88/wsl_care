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
 * Run through `npm run bundle` (which cleans dist/ first) and, from vsce, through `vscode:prepublish`. manifest.test.ts
 * reads these options back with the TypeScript parser.
 */
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { buildSync } from 'esbuild';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const { version } = JSON.parse(readFileSync(join(ROOT, 'package.json'), 'utf8'));
if (typeof version !== 'string' || !/^\d+\.\d+\.\d+$/.test(version)) {
  console.error(`bundle: package.json carries no x.y.z version (${String(version)})`);
  process.exit(1);
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
console.log(`bundle: dist/extension.js built for ${version}`);
