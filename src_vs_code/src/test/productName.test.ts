import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

import { BUNDLE, EXTENSION_ROOT } from './support/paths';
import { listLines } from './support/vsixCheck';

/**
 * The extension's product name (owner decision 2026-10-06): the extension is **AI OS Care**, its permanent id
 * `ai-os-care`. The DAEMON keeps its name — the binary `/opt/wsl-care/bin/wsl-care`, `wsl-care.exe`, its `wsl-care:`
 * messages, its units — and the settings / command keys stay `wslCare.*` (identifiers, never shown as the name).
 *
 * Two guarantees, each over what SHIPS — the files `vsix-files.txt` lets into the `.vsix`, the bundle included (`npm
 * test` bundles first), so a string added anywhere in the shipped sources is in the scan without being listed here:
 *
 * 1. no shipped text file says "WSL Care" — the old product name — in any spelling;
 * 2. on the Marketplace page (the README, the CHANGELOG and the manifest's display strings) every `wsl-care` is one of
 *    the few daemon references below, never the extension's name.
 *
 * Each scan has a companion that proves it still reads real content: it finds the new name where it must be, and a
 * planted old name is reported.
 */

/** Whitespace only: `wsl-care` is the daemon and `wslCare.*` are keys, neither is the old product NAME. */
const OLD_NAME = /WSL\s+Care/i;
const NEW_NAME = 'AI OS Care';

/** The daemon references the Marketplace text may carry — the daemon is still `wsl-care`. Nothing else. */
const DAEMON_REFERENCES: readonly RegExp[] = [/\/opt\/wsl-care\/bin\/wsl-care/g, /`wsl-care` daemon/g, /\bwsl-care daemon/g];

/** The display strings of a manifest node: the keys a person reads (a list under one of them is read item by item), never ids, paths or patterns. */
const DISPLAY_KEYS = new Set([
  'displayName', 'description', 'title', 'name', 'category', 'markdownDescription', 'patternErrorMessage',
  'enumDescriptions', 'markdownEnumDescriptions', 'keywords', 'categories',
]);

/** Only the PNG is binary; the SVG icon is text and is scanned like every other shipped file. */
const BINARY = /\.png$/i;

interface Shipped {
  readonly file: string;
  readonly text: string;
}

/** Every text file the `.vsix` allowlist names, read from disk (the bundle is `dist/extension.js`). */
function shippedTexts(): Shipped[] {
  assert.ok(fs.existsSync(BUNDLE), 'npm test bundles before it tests');
  const files = listLines(fs.readFileSync(path.join(EXTENSION_ROOT, 'vsix-files.txt'), 'utf8'));

  return files.filter((file) => !BINARY.test(file)).map((file) => ({ file, text: fs.readFileSync(path.join(EXTENSION_ROOT, file), 'utf8') }));
}

/**
 * A line as a reader sees it: esbuild writes every non-ASCII character of the bundle as a `\\uXXXX` escape, and markup
 * may spell a space `&nbsp;` — so a no-break space between the two words would otherwise slip past the pattern.
 */
function asRead(line: string): string {
  return line.replace(/\\u([0-9a-fA-F]{4})/g, (_, hex: string) => String.fromCharCode(Number.parseInt(hex, 16))).replace(/&(nbsp|#160|#xa0);/gi, ' ');
}

/** Where a text still says the old product name: `file:line` for each line that does. */
function oldNameFindings(shipped: readonly Shipped[]): string[] {
  return shipped.flatMap(({ file, text }) => text.split('\n').flatMap((line, index) => (OLD_NAME.test(asRead(line)) ? [`${file}:${index + 1}`] : [])));
}

/** The display strings of the manifest: the top-level ones the Marketplace shows and every display key under contributes. */
function manifestDisplayStrings(manifest: Record<string, unknown>): string[] {
  const { displayName, description, keywords, categories, contributes } = manifest;

  return displayStringsOf({ displayName, description, keywords, categories, contributes });
}

/** The strings under a display key, at any depth — an array's items inherit the key that holds the array. */
function displayStringsOf(node: unknown, key = ''): string[] {
  if (typeof node === 'string') {
    return DISPLAY_KEYS.has(key) ? [node] : [];
  }
  if (Array.isArray(node)) {
    return node.flatMap((item) => displayStringsOf(item, key));
  }
  if (node === null || typeof node !== 'object') {
    return [];
  }

  return Object.entries(node).flatMap(([child, value]) => displayStringsOf(value, child));
}

/** The `wsl-care` spellings left in a text once the sanctioned daemon references are taken out. */
function nonDaemonMentions(text: string): string[] {
  const rest = DAEMON_REFERENCES.reduce((left, reference) => left.replace(reference, ''), text);

  return rest.split('\n').filter((line) => /wsl-care/i.test(line));
}

function marketplaceTexts(): Shipped[] {
  const manifest = JSON.parse(fs.readFileSync(path.join(EXTENSION_ROOT, 'package.json'), 'utf8')) as Record<string, unknown>;

  return [
    { file: 'package.json (display strings)', text: manifestDisplayStrings(manifest).join('\n') },
    ...['README.md', 'CHANGELOG.md'].map((file) => ({ file, text: fs.readFileSync(path.join(EXTENSION_ROOT, file), 'utf8') })),
  ];
}

test('no shipped file of the .vsix says the old product name "WSL Care"', () => {
  assert.deepEqual(oldNameFindings(shippedTexts()), []);
});

test('the old-name scan reads real content: the new name is in the manifest and the bundle, and a planted old name is found', () => {
  const shipped = shippedTexts();
  const byFile = new Map(shipped.map((s) => [s.file, s.text]));
  assert.ok(byFile.get('package.json')?.includes(`"displayName": "${NEW_NAME}"`), 'the manifest carries the new display name');
  assert.ok(byFile.get('dist/extension.js')?.includes(`${NEW_NAME}: checking`), 'the bundle carries the status bar text under the new name');
  assert.ok(shipped.some((s) => s.file === 'media/panel.js'), 'the page script is scanned');
  for (const spelling of ['WSL Care', 'wsl care', `WSL${String.fromCharCode(0xa0)}Care`, 'WSL  Care']) {
    assert.deepEqual(oldNameFindings([{ file: 'planted.txt', text: `one\nthe ${spelling} panel\n` }]), ['planted.txt:2'], spelling);
  }
  assert.deepEqual(oldNameFindings([{ file: 'keys.txt', text: 'wslCare.refresh\n/opt/wsl-care/bin/wsl-care\n' }]), [], 'keys and the daemon path are not the old name');
  assert.ok(shipped.some((s) => s.file === 'media/wsl-care.svg'), 'the SVG icon is text and is scanned');
  for (const escaped of ['var a = "WSL\\u00a0Care";', '<p>WSL&nbsp;Care</p>', '<p>WSL&#160;Care</p>']) {
    assert.deepEqual(oldNameFindings([{ file: 'bundle.js', text: escaped }]), ['bundle.js:1'], escaped);
  }
});

test('the Marketplace text names wsl-care only as the daemon — never as the extension', () => {
  for (const { file, text } of marketplaceTexts()) {
    assert.deepEqual(nonDaemonMentions(text), [], file);
  }
});

test('the daemon-reference allowlist is alive: the README\'s daemon reference is taken out, a planted extension name is not', () => {
  const readme = fs.readFileSync(path.join(EXTENSION_ROOT, 'README.md'), 'utf8');
  assert.match(readme, /`wsl-care` daemon/, 'the README still names the daemon it reads');
  assert.ok(manifestDisplayStrings(JSON.parse(fs.readFileSync(path.join(EXTENSION_ROOT, 'package.json'), 'utf8')) as Record<string, unknown>).includes(NEW_NAME), 'the display strings are read');
  assert.deepEqual(nonDaemonMentions('the `wsl-care` daemon\n/opt/wsl-care/bin/wsl-care status'), []);
  assert.deepEqual(nonDaemonMentions('install the wsl-care extension'), ['install the wsl-care extension']);
  const planted = { keywords: ['wsl-care keyword'], contributes: { configuration: { properties: { x: { enumDescriptions: ['the wsl-care panel'], pattern: 'wsl-care' } } } } };
  assert.deepEqual(manifestDisplayStrings(planted), ['wsl-care keyword', 'the wsl-care panel'], 'list items under a display key are read; a pattern is not');
});
