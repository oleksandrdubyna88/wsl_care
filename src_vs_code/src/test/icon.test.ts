import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

import { decodePng, ICON_SIZE, iconPixels, iconPng } from './support/iconPng';
import { EXTENSION_ROOT } from './support/paths';

/**
 * The Marketplace icon (plan §15f #6) is DRAWN by `support/iconPng.ts` — original, no third-party art — and the
 * committed `media/icon.png` is held to that recipe by its PIXELS (`npm run icon:make` rewrites it). A tolerance of one
 * sample in sixteen on a handful of edge pixels, because the rasteriser's `Math.atan2` / `Math.hypot` may differ in the
 * last bit between Node releases while the zlib that compressed it certainly may.
 */

const ICON = path.join(EXTENSION_ROOT, 'media', 'icon.png');

test('media/icon.png is a 256 × 256 8-bit RGBA PNG with valid CRCs and only IHDR, IDAT, IEND — no text, no time, nothing about its maker', () => {
  const png = decodePng(fs.readFileSync(ICON));
  assert.equal(png.width, ICON_SIZE);
  assert.equal(png.height, ICON_SIZE);
  assert.equal(png.bitDepth, 8);
  assert.equal(png.colourType, 6, 'RGBA');
  assert.ok(png.crcsValid);
  assert.deepEqual(png.chunkTypes, ['IHDR', 'IDAT', 'IEND']);
});

test('its pixels are the ones the recipe draws (within one sample of sixteen on at most 0.5 % of pixels)', () => {
  const committed = decodePng(fs.readFileSync(ICON)).raw;
  const drawn = iconPixels();
  assert.equal(committed.length, drawn.length);
  let differing = 0;
  for (let i = 0; i < drawn.length; i++) {
    const delta = Math.abs((committed[i] ?? 0) - (drawn[i] ?? 0));
    assert.ok(delta <= 16, `byte ${i} differs by ${delta}`);
    differing += delta === 0 ? 0 : 1;
  }
  assert.ok(differing <= drawn.length * 0.005, `${differing} bytes differ`);
});

test('the recipe draws something: transparent corners, an opaque centre, and both gauge colours present', () => {
  const raw = decodePng(iconPng()).raw;
  const row = 1 + ICON_SIZE * 4;
  const alpha = (x: number, y: number): number => raw[y * row + 1 + x * 4 + 3] ?? -1;
  assert.equal(alpha(0, 0), 0, 'the rounded corner is transparent');
  assert.equal(alpha(128, 128), 255, 'the centre is opaque');
  const colours = new Set<string>();
  for (let y = 0; y < ICON_SIZE; y += 4) {
    for (let x = 0; x < ICON_SIZE; x += 4) {
      const at = y * row + 1 + x * 4;
      colours.add(`${raw[at]},${raw[at + 1]},${raw[at + 2]}`);
    }
  }
  assert.ok(colours.has('63,184,175') && colours.has('51,71,91'), 'the filled and the empty part of the gauge');
});
