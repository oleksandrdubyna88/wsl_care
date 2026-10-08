import assert from 'node:assert/strict';
import { test } from 'node:test';

import { at, child, parseXml } from './support/xmlTree';

/**
 * The strict XML reader the Windows Time guard's tests parse the task definition with (`common.generated-code-tests` §3: a
 * fake gets its own tests, and may be stricter than the real thing, never more permissive).
 */

test('it reads elements, attributes, text and the predefined entities', () => {
  const root = parseXml('<?xml version="1.0" encoding="UTF-16"?>\n<a x="1" y="&quot;q&quot;"><b>t &amp; &lt;u&gt;</b><c/><b>2</b></a>');
  assert.equal(root.name, 'a');
  assert.deepEqual(root.attributes, { x: '1', y: '"q"' });
  assert.deepEqual(root.children.map((c) => c.name), ['b', 'c', 'b']);
  assert.equal(root.children[0]?.text, 't & <u>');
  assert.equal(at(parseXml('<a><b><c>deep</c></b></a>'), 'b', 'c').text, 'deep');
});

test('it refuses what it does not model — never a silent partial read', () => {
  for (const bad of ['<a><b></a>', '<a>', '<a></a><b></b>', 'x<a></a>', '<a>&nbsp;</a>', '<a>& </a>', '<a><!-- c --></a>', '<a><![CDATA[x]]></a>', '<a b=1></a>']) {
    assert.throws(() => parseXml(bad), /xmlTree/, bad);
  }
});

test('child() insists on exactly one', () => {
  const root = parseXml('<a><b/><b/></a>');
  assert.throws(() => child(root, 'b'), /has 2 <b>/);
  assert.throws(() => child(root, 'z'), /has 0 <z>/);
});
