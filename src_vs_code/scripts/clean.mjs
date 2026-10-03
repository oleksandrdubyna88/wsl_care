#!/usr/bin/env node
/**
 * Removes the build directories named on the command line, so a compile never runs tests that a deleted source left
 * behind in `out/`, and a bundle never carries code that no longer exists (ported from dew_flow_vscode_kit).
 *
 *     node scripts/clean.mjs out
 *     node scripts/clean.mjs dist
 *
 * A script rather than an inline `node -e`, because the quoting differs between cmd, pwsh and sh.
 */
import { rmSync } from 'node:fs';

const targets = process.argv.slice(2);
if (targets.length === 0) {
  console.error('clean: name at least one directory to remove');
  process.exit(2);
}
for (const target of targets) {
  rmSync(target, { recursive: true, force: true });
}
