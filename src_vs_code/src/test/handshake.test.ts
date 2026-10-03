import assert from 'node:assert/strict';
import { test } from 'node:test';

import { MIN_DAEMON_FOR_RENDER, parseAnswer, parseDaemonVersion, SUPPORTED_SCHEMA, versionRefusal } from '../client/handshake';
import type { Answer, Failure } from '../client/outcome';
import { golden, goldenSets } from './support/paths';

/**
 * The per-verb schema handshake and the version rule (plan §6, §15g M2): `SUPPORTED_SCHEMA = [1]`,
 * `MIN_DAEMON_FOR_RENDER = 0.1.0`; unknown keys ignored; every field added after 0.1.0 optional; an unknown major
 * blanks only that verb; `unknown` (unstamped) renders. Replayed over EVERY golden set present — `head` today, and
 * `daemon-0.1.0` the day the E5 live gate freezes it (derived from the folder, so nobody has to add it here).
 */

const VERBS = ['status', 'preview', 'doctor'] as const;

function isAnswer(result: Answer | Failure): result is Answer {
  return 'verb' in result;
}

function edited(set: string, verb: (typeof VERBS)[number], edit: (body: Record<string, unknown>) => void): string {
  const body = golden(set, verb);
  edit(body);
  return JSON.stringify(body);
}

test('the compiled compatibility constants are the plan\'s', () => {
  assert.deepEqual(SUPPORTED_SCHEMA, [1]);
  assert.equal(MIN_DAEMON_FOR_RENDER, '0.1.0');
});

test('the head golden set exists, and every set present is replayed', () => {
  assert.ok(goldenSets().includes('head'), `contracts/golden/head is missing: ${goldenSets().join(', ')}`);
});

for (const set of goldenSets()) {
  for (const verb of VERBS) {
    test(`${set}/${verb}.json parses with its schema accepted`, () => {
      const result = parseAnswer(verb, JSON.stringify(golden(set, verb)));
      assert.ok(isAnswer(result), JSON.stringify(result));
      assert.equal(result.verb, verb);
      assert.ok('schemaVersion' in result && result.schemaVersion === 1);
    });

    test(`${set}/${verb}.json with an unknown schemaVersion major says the extension is too old for THIS verb`, () => {
      const result = parseAnswer(verb, edited(set, verb, (b) => { b.schemaVersion = 2; }));
      assert.deepEqual(result, { kind: 'needsNewerExtension', schemaVersion: 2 });
    });

    test(`${set}/${verb}.json with an unknown key is read as if the key were absent`, () => {
      const plain = parseAnswer(verb, JSON.stringify(golden(set, verb)));
      const extra = parseAnswer(verb, edited(set, verb, (b) => { b.addedInSomeLaterDaemon = { nested: [1, 2, 3] }; }));
      assert.ok(isAnswer(plain) && isAnswer(extra));
      assert.deepEqual(stripBody(extra), stripBody(plain), 'everything the client reads is the same');
    });
  }

  test(`${set}/status.json carries the verdicts and the product version the panel colours from`, () => {
    const result = parseAnswer('status', JSON.stringify(golden(set, 'status')));
    assert.ok(isAnswer(result) && result.verb === 'status');
    assert.equal(result.productVersion, 'unknown', 'the goldens normalise the version to the unstamped value');
    assert.ok(result.verdicts !== undefined && result.verdicts.length > 0);
    const memory = result.verdicts.find((v) => v.id === 'memory.available');
    assert.deepEqual(memory, { id: 'memory.available', level: 'ok', value: '66.8 %', limit: 'warn < 25 %, critical < 15 %', reason: 'MemAvailable is above both thresholds' });
  });

  test(`${set}/status.json without verdicts or productVersion (a daemon before E5.S0) is tolerated, both read as absent`, () => {
    const result = parseAnswer('status', edited(set, 'status', (b) => { delete b.verdicts; delete b.productVersion; }));
    assert.ok(isAnswer(result) && result.verb === 'status', JSON.stringify(result));
    assert.equal(result.verdicts, undefined);
    assert.equal(result.productVersion, undefined);
  });
}

function stripBody(answer: Answer): unknown {
  const { body: _body, ...rest } = answer as Answer & { body?: unknown };
  return rest;
}

test('an answer that is not a JSON object with an integer schemaVersion is unparseable, naming why', () => {
  for (const [text, why] of [['', /not JSON/], ['not json', /not JSON/], ['[1,2]', /not a JSON object/], ['{"x":1}', /schemaVersion/], ['{"schemaVersion":"1"}', /schemaVersion/], ['{"schemaVersion":1.5}', /schemaVersion/]] as const) {
    const result = parseAnswer('doctor', text);
    assert.equal('kind' in result ? result.kind : 'answer', 'unparseable', text);
    assert.match('detail' in result ? result.detail : '', why, text);
  }
});

test('a verdict with an unknown level reads as unknown; one without an id is left out; extra keys ignored', () => {
  const body = { schemaVersion: 1, verdicts: [{ id: 'a', level: 'purple', extra: true }, { level: 'ok' }, { id: 'b', level: 'warn', value: '1', limit: '2', reason: '3' }] };
  const result = parseAnswer('status', JSON.stringify(body));
  assert.ok(isAnswer(result) && result.verb === 'status');
  assert.deepEqual(result.verdicts, [
    { id: 'a', level: 'unknown', value: undefined, limit: undefined, reason: undefined },
    { id: 'b', level: 'warn', value: '1', limit: '2', reason: '3' },
  ]);
});

test('a verdicts member that is not an array reads as absent, not as an error', () => {
  const result = parseAnswer('status', JSON.stringify({ schemaVersion: 1, verdicts: 'nope', productVersion: 7 }));
  assert.ok(isAnswer(result) && result.verb === 'status');
  assert.equal(result.verdicts, undefined);
  assert.equal(result.productVersion, undefined);
});

test('the version text is read as plan §6 spells it', () => {
  assert.deepEqual(parseDaemonVersion('0.1.0'), { kind: 'release', text: '0.1.0', parts: [0, 1, 0] });
  assert.deepEqual(parseDaemonVersion('1.12.3+0123abcd\n'), { kind: 'release', text: '1.12.3+0123abcd', parts: [1, 12, 3] });
  assert.deepEqual(parseDaemonVersion('unknown'), { kind: 'unstamped' });
  assert.deepEqual(parseDaemonVersion('0.0.0+7aeb02dd39a9841915112456532d3803e4cb6026'), { kind: 'development', text: '0.0.0+7aeb02dd39a9841915112456532d3803e4cb6026' });
  assert.deepEqual(parseDaemonVersion('0.0.0'), { kind: 'development', text: '0.0.0' });
  assert.deepEqual(parseDaemonVersion('v0.1'), { kind: 'unrecognised', text: 'v0.1' });
  assert.deepEqual(parseDaemonVersion(''), { kind: 'unrecognised', text: '' });
});

test('a released daemon below 0.1.0 is refused for rendering; 0.1.0 and above, development builds and unstamped builds render', () => {
  assert.equal(versionRefusal(parseDaemonVersion('0.1.0')), undefined);
  assert.equal(versionRefusal(parseDaemonVersion('0.2.0+abc')), undefined);
  assert.equal(versionRefusal(parseDaemonVersion('1.0.0')), undefined);
  assert.equal(versionRefusal(parseDaemonVersion('unknown')), undefined, 'unstamped: render, do not refuse (plan §6)');
  assert.equal(versionRefusal(parseDaemonVersion('0.0.0+abc')), undefined, 'a build from source before the first release');
  assert.equal(versionRefusal({ kind: 'notRead', reason: 'x' }), undefined);
  assert.deepEqual(versionRefusal(parseDaemonVersion('0.0.9')), { kind: 'daemonTooOld', version: '0.0.9', minimum: '0.1.0' });
});
