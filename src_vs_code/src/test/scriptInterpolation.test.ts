import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import { test } from 'node:test';

import { fromExtensionRoot, shippedSources } from './support/paths';

/**
 * `typescript.doctrine` § 1: never interpolate into a `<script>` with `JSON.stringify` — an HTML parser ends a script
 * element at the first `</script>` it meets, inside a string literal included. The doctrine asks a repository that
 * renders a webview to copy this scan BEFORE it renders anything; E5.S2 renders the first one, so it is here from E5.S1
 * (plan §15g M7). Ported from dew_flow_vscode_kit's `src/test/scriptInterpolation.test.ts` (2026-10-03) — a copy, not a
 * dependency (E5 does not depend on the kit).
 *
 * E5's webview needs no escaper at all (§15g M7: a static shell, data by `postMessage`, DOM by `textContent`), so the
 * allowlist starts EMPTY. A line added to it must name the exact text and why it is not a page; the companion test
 * fails when an allowlisted line moves or disappears, so the list cannot go stale in the direction that keeps it green.
 */

const SHAPE = /\$\{\s*JSON\.stringify\(/;

interface Allowed {
  readonly file: string;
  /** The trimmed text of the line, exactly. */
  readonly line: string;
  readonly why: string;
}

const ALLOWED: readonly Allowed[] = [];

interface Finding {
  readonly file: string;
  readonly line: number;
  readonly text: string;
}

/** Every line of `text` carrying the shape, as `{ file, line, text }`. */
function findingsIn(file: string, text: string): Finding[] {
  return text.split('\n').flatMap((line, index) => (SHAPE.test(line) ? [{ file, line: index + 1, text: line.trim() }] : []));
}

function isAllowed(finding: Finding): boolean {
  return ALLOWED.some((allowed) => allowed.file === finding.file && allowed.line === finding.text);
}

test('no shipped source interpolates JSON.stringify into a template literal, outside the allowlist', () => {
  const files = shippedSources();
  assert.ok(files.length > 0, 'the scan found no shipped source under src/');

  const findings = files.flatMap((file) => findingsIn(fromExtensionRoot(file), fs.readFileSync(file, 'utf8'))).filter((finding) => !isAllowed(finding));

  assert.deepEqual(findings, [], `JSON.stringify interpolated into a template literal:\n${findings.map((f) => `${f.file}:${f.line} — ${f.text}`).join('\n')}`);
});

test('every allowlisted line is still exactly where it is said to be — a stale allowlist entry is red', () => {
  for (const allowed of ALLOWED) {
    const file = shippedSources().find((f) => fromExtensionRoot(f) === allowed.file);
    assert.ok(file !== undefined, `${allowed.file}: allowlisted (${allowed.why}) but not a shipped source`);
    const found = findingsIn(allowed.file, fs.readFileSync(file, 'utf8')).filter((finding) => finding.text === allowed.line);
    assert.equal(found.length, 1, `${allowed.file}: the allowlisted line (${allowed.why}) was found ${found.length} times`);
  }
});

test('the scan finds the shape in a fixture in every spelling, and leaves a sanctioned escaper alone', () => {
  const fixture = [
    "const page = `<script>var rows = ${JSON.stringify(rows)};</script>`;",
    'const loose = `${ JSON.stringify( rows ) }`;',
    'const safe = `<script>var rows = ${jsonForScript(rows)};</script>`;',
    "const text = JSON.stringify(value).replace(/</g, '\\\\u003c');",
  ].join('\n');

  assert.deepEqual(findingsIn('src/fixture.ts', fixture).map((f) => f.line), [1, 2]);
});
