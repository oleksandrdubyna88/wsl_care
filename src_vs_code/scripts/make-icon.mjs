#!/usr/bin/env node
/**
 * Writes media/icon.png — the Marketplace icon (plan §15f #6) — from the code that DRAWS it,
 * src/test/support/iconPng.ts (compiled into out/). No third-party art and no image tool: a rounded square, a gauge ring
 * and a dot, rasterised by hand and encoded with Node's zlib. icon.test.ts holds the committed file's pixels equal to
 * that code, so the icon cannot drift from its recipe.
 *
 *     npm run icon:make     (compiles first; reads out/)
 */
import { writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const require = createRequire(import.meta.url);
const { iconPng } = require(join(ROOT, 'out', 'test', 'support', 'iconPng.js'));
const target = join(ROOT, 'media', 'icon.png');

writeFileSync(target, iconPng());
console.log('make-icon: media/icon.png written');
