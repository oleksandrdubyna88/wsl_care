#!/usr/bin/env node
/**
 * Checks the packaged .vsix — the ARTEFACT, not the source (plan §15g M8; the family's "Verify the ARTEFACT" rule):
 *
 *   1. `vsce ls --no-dependencies` lists EXACTLY vsix-files.txt (what .vscodeignore's allowlist lets in);
 *   2. the .vsix itself is unzipped and its entries are checked by src/test/support/vsixCheck.ts — exactly the allowlist,
 *      no drive / home / mnt / wsl-share path, no user name of THIS machine (derived now, never stored) or word of
 *      vsix-denylist.txt, no e-mail address, no source map, and a build stamp equal to the package version;
 *   3. with --release, the placeholder publisher is refused.
 *
 *     node scripts/check-vsix.mjs [<file.vsix>] [--release]      (default: wsl-care-<version>.vsix; reads out/ — compile first)
 *
 * Exit 0 clean, 1 findings (each printed), 2 usage / missing inputs. The findings name the entry and the kind of leak;
 * a denied word itself is never printed.
 */
import { spawnSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { homedir, userInfo } from 'node:os';
import { basename, dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const require = createRequire(import.meta.url);
const SUPPORT = join(ROOT, 'out', 'test', 'support');

function fail(message, code) {
  console.error(`check-vsix: ${message}`);
  process.exit(code);
}

if (!existsSync(join(SUPPORT, 'vsixCheck.js'))) {
  fail('out/test/support/vsixCheck.js is missing — run `npm run compile` (or npm test) first', 2);
}
const { listLines, machineUserNames, vsixFindings } = require(join(SUPPORT, 'vsixCheck.js'));
const { readZip } = require(join(SUPPORT, 'zipFile.js'));

const args = process.argv.slice(2);
const release = args.includes('--release');
const positional = args.filter((a) => a !== '--release');
const { version } = JSON.parse(readFileSync(join(ROOT, 'package.json'), 'utf8'));
const vsix = positional[0] ?? join(ROOT, `wsl-care-${version}.vsix`);
if (positional.length > 1 || !existsSync(vsix)) {
  fail(`usage: check-vsix.mjs [<file.vsix>] [--release] — ${vsix} does not exist (npm run package writes it)`, 2);
}

const expected = listLines(readFileSync(join(ROOT, 'vsix-files.txt'), 'utf8'));
const denylist = listLines(readFileSync(join(ROOT, 'vsix-denylist.txt'), 'utf8'));

// vsce's own listing, from the installed devDependency (the lock file's exact version), started without a shell.
const vsce = join(ROOT, 'node_modules', '@vscode', 'vsce', 'vsce');
const listed = spawnSync(process.execPath, [vsce, 'ls', '--no-dependencies'], { cwd: ROOT, encoding: 'utf8', timeout: 120_000 });
if (listed.status !== 0) {
  fail(`vsce ls failed (${listed.status ?? listed.signal}): ${listed.stderr}`, 2);
}
const lsFindings = [];
const lsLines = listLines(listed.stdout);
if (JSON.stringify([...lsLines].sort()) !== JSON.stringify([...expected].sort())) {
  lsFindings.push(`vsce ls --no-dependencies lists [${lsLines.join(', ')}], vsix-files.txt says [${expected.join(', ')}]`);
}

const names = machineUserNames([userInfo().username, process.env.USERNAME, process.env.USER, basename(homedir())]);
const findings = [
  ...lsFindings,
  ...vsixFindings(readZip(readFileSync(vsix)), { expectedFiles: expected, deniedWords: [...names, ...denylist], release }),
];

if (findings.length > 0) {
  console.error(`check-vsix: ${basename(vsix)} — ${findings.length} finding(s):`);
  for (const finding of findings) {
    console.error(`  - ${finding}`);
  }
  process.exit(1);
}
console.log(`check-vsix: ${basename(vsix)} — ${expected.length} allowlisted files, no leak, build stamp ${version}${release ? ', publisher set' : ''}; ${names.length} machine user name(s) and ${denylist.length} denylist word(s) checked`);
