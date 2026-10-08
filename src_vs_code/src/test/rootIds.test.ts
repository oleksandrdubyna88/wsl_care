import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

import { readEnum } from '../client/enumValue';
import { ACTION_IDS, actionIdOf, MAX_SHOWN_VOLUMES, runIdOf, volumeNameOf } from '../root/rootIds';
import { REPOSITORY_ROOT } from './support/paths';

/**
 * The typed values a root call may carry (E6.S2): action ids from the COMPILED registry, run ids in the daemon's one
 * spelling, anonymous-volume names of 64 lowercase hex digits — each refused by a validator before it can become argv or
 * a stdin line. And the reader every daemon enum goes through: a value this extension does not know reads as
 * "unknown (<value>)", never as a crash (plan §15j m1).
 */

const ACTIONS_JSON = path.join(REPOSITORY_ROOT, 'contracts', 'actions.json');

test('the compiled registry is EXACTLY the ids of contracts/actions.json — the daemon writes that file from ActionId.All', () => {
  const contract = JSON.parse(fs.readFileSync(ACTIONS_JSON, 'utf8')) as { ids: string[] };
  assert.ok(contract.ids.length >= 19, `only ${contract.ids.length} ids read`);
  assert.deepEqual([...ACTION_IDS].sort(), [...contract.ids].sort());
});

test('an action id is one of the registry, spelt exactly — anything else is refused', () => {
  assert.equal(actionIdOf('A4'), 'A4');
  assert.equal(actionIdOf('A5Testcontainers'), 'A5Testcontainers');
  assert.equal(actionIdOf('A19'), 'A19', 'daemon E14 S2a added A19');
  for (const bad of ['a4', 'A4 ', 'A20', 'A4,A5', '--timer', '', 'A', 4, undefined, null, ['A4']]) {
    assert.equal(actionIdOf(bad), undefined, JSON.stringify(bad));
  }
});

test('a run id is the daemon\'s one spelling — yyyyMMddTHHmmssZ-<pid>, the pid without a leading zero', () => {
  assert.equal(runIdOf('20261004T101500Z-4242'), '20261004T101500Z-4242');
  assert.equal(runIdOf('20000101T000000Z-1'), '20000101T000000Z-1');
  for (const bad of ['20261004T101500Z-04242', '20261004T101500Z-0', '20261004T101500Z-', '20261004T1015Z-1', '20261004T101500Z-1 --timer', '-u', '../x', '', 7, undefined]) {
    assert.equal(runIdOf(bad), undefined, JSON.stringify(bad));
  }
});

test('a volume name is 64 lowercase hex digits and nothing else — a stdin line can never carry anything but a name', () => {
  const name = 'ab'.repeat(32);
  assert.equal(volumeNameOf(name), name);
  for (const bad of ['AB'.repeat(32), `${name}\n`, `${name} `, 'ab'.repeat(31), `${'ab'.repeat(32)}0`, 'g'.repeat(64), `-${'a'.repeat(63)}`, 64, undefined]) {
    assert.equal(volumeNameOf(bad), undefined, JSON.stringify(bad));
  }
  assert.equal(MAX_SHOWN_VOLUMES, 10_000, 'the daemon\'s ShownList.MaxNames (plan §15k #11)');
});

test('an enum value this extension knows is read as itself', () => {
  assert.deepEqual(readEnum('queued', ['none', 'queued', 'live'] as const), { kind: 'known', value: 'queued' });
});

test('an enum value it does not know reads as "unknown (<value>)" — never a crash, never silently another value', () => {
  assert.deepEqual(readEnum('paused', ['none', 'queued'] as const), { kind: 'unknown', label: 'unknown (paused)' });
  assert.deepEqual(readEnum(undefined, ['none'] as const), { kind: 'unknown', label: 'unknown (absent)' });
  assert.deepEqual(readEnum(7, ['none'] as const), { kind: 'unknown', label: 'unknown (7)' });
  assert.deepEqual(readEnum({ state: 'x' }, ['none'] as const), { kind: 'unknown', label: 'unknown (not a string)' });
});

test('an unknown value is shown through the one sanitiser: control and bidi characters made visible, at most 40 characters', () => {
  const hostile = 'ok' + String.fromCharCode(0x202e, 0x7, 0xd, 0xa) + 'x'.repeat(100);
  const read = readEnum(hostile, ['none'] as const);
  assert.equal(read.kind, 'unknown');
  assert.ok(read.kind === 'unknown');
  assert.equal(read.label, `unknown (ok${String.fromCharCode(0xfffd).repeat(4)}${'x'.repeat(34)}…)`);
});
