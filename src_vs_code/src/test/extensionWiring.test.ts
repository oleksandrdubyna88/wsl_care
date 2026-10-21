import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';
import * as ts from 'typescript';

import { SOURCE_ROOT } from './support/paths';

/**
 * E19.S2b review: the poller is handed the Windows `status` source where the extension builds it. `extension.ts` needs the `vscode`
 * module, so no unit test runs it; this reads it as a program (the extension-host scenario that asserts the call is in
 * `test/host/suite.ts`, run by `test:host`) — dropping the `windows:` line turns this red.
 */

/** The property names of every `new Poller({ … })` object literal in a source. */
function pollerOptionNames(text: string): string[][] {
  const found: string[][] = [];
  const visit = (node: ts.Node): void => {
    if (ts.isNewExpression(node) && ts.isIdentifier(node.expression) && node.expression.text === 'Poller') {
      const options = node.arguments?.[0];
      found.push(options !== undefined && ts.isObjectLiteralExpression(options) ? options.properties.flatMap((p) => (p.name !== undefined && ts.isIdentifier(p.name) ? [p.name.text] : [])) : []);
    }
    ts.forEachChild(node, visit);
  };
  visit(ts.createSourceFile('scan.ts', text, ts.ScriptTarget.ES2022, true));

  return found;
}

test('the extension hands its poller the windows status source', () => {
  const found = pollerOptionNames(fs.readFileSync(path.join(SOURCE_ROOT, 'extension.ts'), 'utf8'));
  assert.equal(found.length, 1, 'the extension builds exactly one Poller');
  assert.ok(found[0]?.includes('windows'), `its options: ${JSON.stringify(found[0])}`);
});

test('the wiring scan finds a poller built without the windows source', () => {
  assert.deepEqual(pollerOptionNames('const p = new Poller({ run, store });'), [['run', 'store']]);
});
