import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import { test } from 'node:test';

import { Element, runPageScript, type Page } from './support/pageHarness';
import { WEBVIEW_BUNDLE } from './support/paths';

/**
 * W0 code round: the SHIPPED full view (`dist/webview/full.js`, what the .vsix carries) is RUN in the strict page harness —
 * not just built and scanned. With the mount point the page expects and with none, it must not throw, post nothing (W0 shows
 * nothing yet) and acquire the VS Code API exactly once.
 *
 * "Exactly once" is counted by a prelude that takes the real API once and hands the page that same object while counting; an
 * epilogue listener answers a `message` with the count, so the answer is a post the test can read.
 */

const PRELUDE = `var __api = acquireVsCodeApi(); var __calls = 0; acquireVsCodeApi = function () { __calls += 1; return __api; };\n`;
const EPILOGUE = `\n;window.addEventListener('message', function () { __api.postMessage({ acquireCalls: __calls }); });`;

function run(mounts: Readonly<Record<string, Element>>): Page {
  assert.ok(fs.existsSync(WEBVIEW_BUNDLE), 'npm test bundles before it tests');
  return runPageScript(PRELUDE + fs.readFileSync(WEBVIEW_BUNDLE, 'utf8') + EPILOGUE, mounts);
}

test('the shipped full view runs with its mount point: it clears it, throws nothing, posts nothing, acquires the API once', () => {
  const view = new Element('MAIN');
  const stale = new Element('P');
  stale.text = 'stale';
  view.children.push(stale);
  stale.parent = view;

  const page = run({ view });
  assert.deepEqual(page.posted, [], 'W0 posts nothing');
  assert.equal(view.children.length, 0, 'the mount point is emptied');
  page.message({});
  assert.deepEqual(page.posted, [{ acquireCalls: 1 }]);
});

test('the shipped full view runs with NO mount point: no throw, nothing posted, the API acquired once', () => {
  const page = run({});
  assert.deepEqual(page.posted, []);
  page.message({});
  assert.deepEqual(page.posted, [{ acquireCalls: 1 }]);
});

test('the counting prelude has teeth: a page that acquires the API twice is counted twice', () => {
  const page = runPageScript(`${PRELUDE}acquireVsCodeApi(); acquireVsCodeApi();${EPILOGUE}`);
  page.message({});
  assert.deepEqual(page.posted, [{ acquireCalls: 2 }]);
});
