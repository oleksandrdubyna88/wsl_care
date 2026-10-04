import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';
import * as ts from 'typescript';

import type { ProcessRequest, ProcessResult, Runner } from '../process/runner';
import { clientRunner } from '../testApi';
import { EXTENSION_ROOT } from './support/paths';

/**
 * The call log is the TEST API's (E5 code round, security review #4): it exists for the extension-host scenarios, and
 * outside Test mode nothing reads it — so nothing may be kept, or a window left open for weeks would grow it with every
 * poll. `clientRunner` is the one place the client's runner is chosen; `extension.ts` must go through it.
 */

const REQUEST: ProcessRequest = { file: 'C:\\Windows\\System32\\wsl.exe', args: ['--list', '--running', '--quiet'], timeoutMs: 1_000 };
const ANSWER: ProcessResult = { kind: 'exited', code: 0, stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) };

function counting(): { runner: Runner; count: () => number } {
  let n = 0;
  return { runner: async () => { n += 1; return ANSWER; }, count: () => n };
}

test('outside Test mode no request is logged, however many run — the runner is the real one, unwrapped', async () => {
  const inner = counting();
  const log: string[] = [];
  const runner = clientRunner(false, inner.runner, log);

  for (let i = 0; i < 100; i += 1) {
    assert.deepEqual(await runner(REQUEST), ANSWER);
  }

  assert.deepEqual(log, [], 'nothing reads the log outside Test mode, so nothing is kept');
  assert.equal(inner.count(), 100, 'every request still reaches the real runner');
  assert.equal(runner, inner.runner, 'no wrapper at all');
});

test('in Test mode every request is logged before it is handed on — what the extension-host scenarios read', async () => {
  const inner = counting();
  const log: string[] = [];
  const runner = clientRunner(true, inner.runner, log);

  await runner(REQUEST);

  assert.deepEqual(log, ['--list --running --quiet']);
  assert.equal(inner.count(), 1);
});

function calledNames(file: string): string[] {
  const text = fs.readFileSync(file, 'utf8');
  const source = ts.createSourceFile(file, text, ts.ScriptTarget.Latest, true, ts.ScriptKind.TS);
  const names: string[] = [];
  const visit = (node: ts.Node): void => {
    if (ts.isCallExpression(node) && ts.isIdentifier(node.expression)) {
      names.push(node.expression.text);
    }
    ts.forEachChild(node, visit);
  };
  visit(source);

  return names;
}

test('extension.ts chooses the client\'s runner through clientRunner and never wraps it in the log by itself', () => {
  const names = calledNames(path.join(EXTENSION_ROOT, 'src', 'extension.ts'));
  assert.ok(names.includes('clientRunner'), 'the wiring goes through the one place that knows the mode');
  assert.ok(!names.includes('loggedRunner'), 'a direct loggedRunner(...) logs in production too');
});
