import { deflateSync, inflateSync } from 'node:zlib';

/**
 * The Marketplace icon (`media/icon.png`, plan §15f #6: a PNG of at least 128 px), DRAWN by this code — no third-party
 * art, no font, no tool beyond Node's zlib. A 256 × 256 RGBA image: a dark rounded square, a 270° gauge ring three-fifths
 * full, and a dot at its centre — "how full is the VM", in the shape of the panel's own figures.
 *
 * `npm run icon:make` (`scripts/make-icon.mjs`) writes the file from `iconPng()`; `icon.test.ts` decodes the committed
 * file and holds its pixels equal to `iconPixels()` — the PIXELS, not the bytes, because zlib's compressed output may
 * differ between Node releases while the image does not. The file carries only IHDR, IDAT and IEND: no text chunk,
 * no timestamp, nothing about the machine that made it.
 */

export const ICON_SIZE = 256;

type Rgb = readonly [number, number, number];

const BACKGROUND: Rgb = [0x1e, 0x2a, 0x36];
const TRACK: Rgb = [0x33, 0x47, 0x5b];
const FILL: Rgb = [0x3f, 0xb8, 0xaf];
const DOT: Rgb = [0xe8, 0xee, 0xf2];

const CORNER = 44;
const CENTRE = ICON_SIZE / 2;
const OUTER = 92;
const INNER = 68;
const DOT_RADIUS = 18;
/** The gauge opens at the bottom: it runs from 135° to 405° (clockwise from +x, y down), and 60 % of it is filled. */
const START = (135 * Math.PI) / 180;
const SWEEP = (270 * Math.PI) / 180;
const FILLED = 0.6;
/** Samples per pixel side: 4 × 4 = 16 per pixel, so edges are smooth. */
const SAMPLES = 4;

function insideRoundedSquare(x: number, y: number): boolean {
  const dx = Math.max(CORNER - x, 0, x - (ICON_SIZE - CORNER));
  const dy = Math.max(CORNER - y, 0, y - (ICON_SIZE - CORNER));

  return dx * dx + dy * dy <= CORNER * CORNER;
}

/** Where along the gauge (0 … 1) the angle of (x, y) lies, or a negative number in the opening. */
function gaugePosition(x: number, y: number): number {
  const angle = Math.atan2(y - CENTRE, x - CENTRE);
  const from = (angle - START + 4 * Math.PI) % (2 * Math.PI);

  return from <= SWEEP ? from / SWEEP : -1;
}

function ringColour(x: number, y: number): Rgb | undefined {
  const r = Math.hypot(x - CENTRE, y - CENTRE);
  const position = r >= INNER && r <= OUTER ? gaugePosition(x, y) : -1;
  if (position < 0) {
    return undefined;
  }

  return position <= FILLED ? FILL : TRACK;
}

/** The colour at one sample point, or undefined outside the icon (transparent). */
function sampleColour(x: number, y: number): Rgb | undefined {
  if (!insideRoundedSquare(x, y)) {
    return undefined;
  }
  if (Math.hypot(x - CENTRE, y - CENTRE) <= DOT_RADIUS) {
    return DOT;
  }

  return ringColour(x, y) ?? BACKGROUND;
}

function pixel(px: number, py: number): [number, number, number, number] {
  const sum = [0, 0, 0, 0];
  for (let sy = 0; sy < SAMPLES; sy++) {
    for (let sx = 0; sx < SAMPLES; sx++) {
      const colour = sampleColour(px + (sx + 0.5) / SAMPLES, py + (sy + 0.5) / SAMPLES);
      if (colour !== undefined) {
        sum[0] += colour[0];
        sum[1] += colour[1];
        sum[2] += colour[2];
        sum[3] += 255;
      }
    }
  }
  const covered = sum[3] / 255;
  const n = SAMPLES * SAMPLES;

  return covered === 0 ? [0, 0, 0, 0] : [Math.round(sum[0] / covered), Math.round(sum[1] / covered), Math.round(sum[2] / covered), Math.round(sum[3] / n)];
}

/** The raw scanlines PNG compresses: per row a filter byte (0, none) and the row's RGBA pixels. */
export function iconPixels(): Buffer {
  const row = 1 + ICON_SIZE * 4;
  const raw = Buffer.alloc(row * ICON_SIZE);
  for (let y = 0; y < ICON_SIZE; y++) {
    for (let x = 0; x < ICON_SIZE; x++) {
      Buffer.from(pixel(x, y)).copy(raw, y * row + 1 + x * 4);
    }
  }

  return raw;
}

const CRC_TABLE = Array.from({ length: 256 }, (_, n) => {
  let c = n;
  for (let k = 0; k < 8; k++) {
    c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
  }
  return c >>> 0;
});

function crc32(bytes: Buffer): number {
  let c = 0xffffffff;
  for (const byte of bytes) {
    c = (CRC_TABLE[(c ^ byte) & 0xff] ?? 0) ^ (c >>> 8);
  }

  return (c ^ 0xffffffff) >>> 0;
}

function chunk(type: string, data: Buffer): Buffer {
  const length = Buffer.alloc(4);
  length.writeUInt32BE(data.length);
  const body = Buffer.concat([Buffer.from(type, 'latin1'), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(body));

  return Buffer.concat([length, body, crc]);
}

const SIGNATURE = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

/** The PNG file: signature, IHDR (256 × 256, 8-bit RGBA), one IDAT, IEND. */
export function iconPng(): Buffer {
  const header = Buffer.alloc(13);
  header.writeUInt32BE(ICON_SIZE, 0);
  header.writeUInt32BE(ICON_SIZE, 4);
  header.set([8, 6, 0, 0, 0], 8);

  return Buffer.concat([SIGNATURE, chunk('IHDR', header), chunk('IDAT', deflateSync(iconPixels(), { level: 9 })), chunk('IEND', Buffer.alloc(0))]);
}

export interface DecodedPng {
  readonly width: number;
  readonly height: number;
  readonly bitDepth: number;
  readonly colourType: number;
  readonly chunkTypes: readonly string[];
  /** The inflated IDAT stream — the raw scanlines. */
  readonly raw: Buffer;
  /** Every chunk's CRC matched its type and data. */
  readonly crcsValid: boolean;
}

/** Reads a PNG back: its chunk list, its header and its decompressed scanlines (no filter is undone — none is used). */
export function decodePng(file: Buffer): DecodedPng {
  if (!file.subarray(0, 8).equals(SIGNATURE)) {
    throw new Error('not a PNG: the signature differs');
  }
  const types: string[] = [];
  const idat: Buffer[] = [];
  let header = Buffer.alloc(13);
  let crcsValid = true;
  for (let at = 8; at < file.length;) {
    const length = file.readUInt32BE(at);
    const type = file.toString('latin1', at + 4, at + 8);
    const data = file.subarray(at + 8, at + 8 + length);
    crcsValid &&= file.readUInt32BE(at + 8 + length) === crc32(file.subarray(at + 4, at + 8 + length));
    types.push(type);
    header = type === 'IHDR' ? Buffer.from(data) : header;
    if (type === 'IDAT') {
      idat.push(Buffer.from(data));
    }
    at += 12 + length;
  }

  return { width: header.readUInt32BE(0), height: header.readUInt32BE(4), bitDepth: header[8] ?? 0, colourType: header[9] ?? 0, chunkTypes: types, raw: inflateSync(Buffer.concat(idat)), crcsValid };
}
