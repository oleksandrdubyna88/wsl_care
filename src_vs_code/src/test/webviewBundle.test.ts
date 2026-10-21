import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { test } from 'node:test';

import { EXTENSION_ROOT, REPOSITORY_ROOT, WEBVIEW_BUNDLE, WEBVIEW_STYLES } from './support/paths';
import { listLines } from './support/vsixCheck';

/**
 * W0 of `todo/PLAN_disk_health_E18.md`, goal 3 and RED items 5 and 6: `npm run bundle` builds a SECOND file, the full view's
 * script `dist/webview/full.js`, and the stylesheet esbuild emits beside it, `dist/webview/full.css` — the kit's
 * `components.css`. They ship in the .vsix like every other file, so what the extension's own bundle scan holds for
 * `dist/extension.js` (no source map, no `sourceMappingURL`, no machine path) is held for them too, and the stylesheet
 * obeys the webview's CSP, `default-src 'none'`: nothing it asks for may be fetched, so no `url(`, no `@import`, no
 * `@font-face`. Each scan has a planted-instance companion (`common.testing`: a structural test that matches nothing
 * passes forever).
 */

const WEBVIEW_FILES = ['dist/webview/full.js', 'dist/webview/full.css'];

function bundleText(file: string): string {
  assert.ok(fs.existsSync(file), `${file} does not exist — npm test bundles before it tests`);
  return fs.readFileSync(file, 'utf8');
}

/** What the stylesheet may not ask for under `default-src 'none'`: a fetch of any kind. */
const FETCHES: readonly { readonly name: string; readonly pattern: RegExp }[] = [
  { name: 'url(', pattern: /url\(/i },
  { name: '@import', pattern: /@import/i },
  { name: '@font-face', pattern: /@font-face/i },
];

function fetchesIn(css: string): string[] {
  return FETCHES.filter((fetch) => fetch.pattern.test(css)).map((fetch) => fetch.name);
}

/** The machine paths a bundle must not carry: this checkout, the repository and the home folder, in both slash forms, and any drive path. */
function machinePaths(): string[] {
  return [EXTENSION_ROOT, REPOSITORY_ROOT, os.homedir()].flatMap((root) => [root, root.split(path.sep).join('/'), root.split(path.sep).join('\\\\')]);
}

const DRIVE_PATH = /(?<![A-Za-z0-9])[A-Za-z]:[\\/]/;

function leaksIn(text: string, roots: readonly string[]): string[] {
  const found = roots.filter((root) => root.length > 3 && text.includes(root)).map((root) => `the path ${JSON.stringify(root)}`);
  if (DRIVE_PATH.test(text)) {
    found.push('a drive path');
  }
  if (/sourceMappingURL=|sourcesContent/.test(text)) {
    found.push('a source map reference');
  }

  return found;
}

test('W0 item 5: npm run bundle writes dist/webview/full.js and dist/webview/full.css', () => {
  assert.ok(fs.existsSync(WEBVIEW_BUNDLE), 'dist/webview/full.js is missing');
  assert.ok(fs.existsSync(WEBVIEW_STYLES), 'dist/webview/full.css is missing');
});

test('W0 item 5: full.css carries the kit\'s token block and asks for nothing the CSP would refuse — no url(, no @import, no @font-face', () => {
  const css = bundleText(WEBVIEW_STYLES);
  assert.ok(css.includes('--kit-fg'), 'the kit\'s token block (--kit-fg) is in the stylesheet');
  assert.deepEqual(fetchesIn(css), []);
});

test('the fetch scan finds each forbidden form in a planted stylesheet, in either case, and leaves a custom property alone', () => {
  for (const planted of ['a { background: url(x.png) }', 'A { background: URL( "x" ) }', '@import "other.css";', '@font-face { font-family: x }']) {
    assert.equal(fetchesIn(planted).length, 1, planted);
  }
  assert.deepEqual(fetchesIn(':root { --kit-fg: var(--vscode-foreground); } /* a custom property is no fetch */'), []);
});

test('W0 item 5: full.js is a browser script — an IIFE that requires nothing — with no source map, no sourceMappingURL and no machine path', () => {
  const text = bundleText(WEBVIEW_BUNDLE);
  assert.match(text, /^(?:"use strict";\r?\n)?\(\(\) => \{/, 'esbuild\'s IIFE (it adds the directive when a bundled module carries one)');
  assert.doesNotMatch(text, /\brequire\(/, 'a webview has no require');
  assert.deepEqual(leaksIn(text, machinePaths()), []);
  assert.deepEqual(leaksIn(bundleText(WEBVIEW_STYLES), machinePaths()), [], 'the stylesheet is held to the same');
  assert.equal(fs.existsSync(`${WEBVIEW_BUNDLE}.map`), false);
  assert.equal(fs.existsSync(`${WEBVIEW_STYLES}.map`), false);
});

test('the leak scan finds a planted machine path in both slash forms, a drive path, and a source map reference', () => {
  assert.ok(leaksIn(`var p = ${JSON.stringify(EXTENSION_ROOT)};`, machinePaths()).length > 0, 'the checkout, as a JSON string');
  assert.ok(leaksIn(`var p = "${EXTENSION_ROOT.split(path.sep).join('/')}/x";`, machinePaths()).length > 0, 'the checkout, with forward slashes');
  assert.deepEqual(leaksIn('var p = "Z:/elsewhere";', []), ['a drive path']);
  assert.deepEqual(leaksIn('//# sourceMappingURL=full.js.map', []), ['a source map reference']);
  assert.deepEqual(leaksIn('var ok = "node_modules/@scope/pkg";', machinePaths()), []);
});

test('W0 item 6: vsix-files.txt and .vscodeignore name both files of the full view', () => {
  const listed = listLines(fs.readFileSync(path.join(EXTENSION_ROOT, 'vsix-files.txt'), 'utf8'));
  const ignore = listLines(fs.readFileSync(path.join(EXTENSION_ROOT, '.vscodeignore'), 'utf8'));
  for (const file of WEBVIEW_FILES) {
    assert.ok(listed.includes(file), `vsix-files.txt does not name ${file}`);
    assert.ok(ignore.includes(`!${file}`), `.vscodeignore does not let ${file} back in`);
  }
});
