import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

import { FAILURE_KINDS } from '../failureText';
import { exitFailure } from '../root/rootFailures';
import { ROOT_FAILURE_KINDS, rootFailureText } from '../root/rootFailureText';
import type { RootFailure } from '../root/rootOutcome';
import { REPOSITORY_ROOT } from './support/paths';

/**
 * Every exit code of the generated contract is read by the root paths as a kind of its OWN (plan §15j m3): the list is
 * DERIVED from `contracts/exit-codes.json`, so a code the daemon adds falls on this test, not on a person reading
 * "failed (exit 81)". And every root kind has words — a label and a sentence — that name no argv word.
 */

const EXIT_CODES_JSON = path.join(REPOSITORY_ROOT, 'contracts', 'exit-codes.json');

function contractCodes(): { name: string; code: number }[] {
  return (JSON.parse(fs.readFileSync(EXIT_CODES_JSON, 'utf8')) as { codes: { name: string; code: number }[] }).codes;
}

function exitOf(code: number, stderr = 'wsl-care: a reason\n'): RootFailure {
  return exitFailure({ kind: 'exited', code, stdout: Buffer.alloc(0), stderr: Buffer.from(stderr, 'utf8') }, 'Ubuntu', false);
}

test('every non-zero code of contracts/exit-codes.json reads as a DISTINCT kind; only RunFailed (1) is the generic failure', () => {
  const codes = contractCodes().filter((c) => c.code !== 0);
  assert.ok(codes.length >= 15, `only ${codes.length} codes read`);
  const kinds = codes.map((c) => ({ name: c.name, kind: exitOf(c.code).kind }));
  const generic = kinds.filter((k) => k.kind === 'unknownFailure').map((k) => k.name);
  assert.deepEqual(generic, ['runFailed'], 'a code the root paths do not name falls through to "failed (exit n)"');
  assert.equal(new Set(kinds.map((k) => k.kind)).size, kinds.length, JSON.stringify(kinds));
});

test('the daemon\'s own wsl-care: lines are the messages; its coloured log lines are dropped', () => {
  const failure = exitOf(69, '\u001b[31m[12:00:00 ERR]\u001b[0m systemd\nwsl-care: needs systemd: no /run/systemd/system\n');
  assert.deepEqual(failure, { kind: 'detachUnavailable', messages: ['wsl-care: needs systemd: no /run/systemd/system'] });
});

test('exit 2 is a refusal of the PIPED LIST only when the call piped one; otherwise the ordinary refusal', () => {
  const result = { kind: 'exited' as const, code: 2, stdout: Buffer.alloc(0), stderr: Buffer.from('wsl-care: act: bad\n', 'utf8') };
  assert.equal(exitFailure(result, 'Ubuntu', true).kind, 'shownListRefused');
  assert.equal(exitFailure(result, 'Ubuntu', false).kind, 'refused');
});

test('every root kind has a label and a sentence; the client\'s kinds keep their own words', () => {
  const samples: Record<string, RootFailure> = {
    rootBusy: { kind: 'rootBusy', distro: 'Ubuntu' },
    actionsUnavailable: { kind: 'actionsUnavailable', version: '0.0.9', minimum: '0.1.0', missing: ['act.detach'] },
    idsRefused: { kind: 'idsRefused', refused: ['A99'], allowed: ['A4'] },
    runIdRefused: { kind: 'runIdRefused' },
    rootRefused: { kind: 'rootRefused', reason: { kind: 'timedOut', timeoutMs: 20_000 } },
    previewNotHeld: { kind: 'previewNotHeld' },
    distroChanged: { kind: 'distroChanged', previewed: 'Ubuntu', now: 'Debian' },
    shownListInvalid: { kind: 'shownListInvalid', reason: 'x' },
    shownListRefused: { kind: 'shownListRefused', messages: ['wsl-care: x'] },
  };
  for (const kind of ROOT_FAILURE_KINDS) {
    const failure = samples[kind] ?? ({ kind, messages: ['wsl-care: x'], running: undefined } as unknown as RootFailure);
    const words = rootFailureText(failure);
    assert.ok(words.label.length > 0 && words.sentence.length > 0, kind);
    assert.equal(/(^|\s)(-u|--[a-z]+)(\s|$)/.test(words.sentence + ' ' + words.label), false, `${kind} spells an argv word: ${words.sentence}`);
  }
  assert.equal(ROOT_FAILURE_KINDS.some((k) => (FAILURE_KINDS as readonly string[]).includes(k)), false);
  assert.equal(rootFailureText({ kind: 'stopped', distro: 'Ubuntu' }).label, 'WSL stopped');
});

test('"Update daemon" names the daemon version, the minimum and what it does not offer', () => {
  const words = rootFailureText({ kind: 'actionsUnavailable', version: '0.0.9', minimum: '0.1.0', missing: ['act.detach', 'act.onlyStdin'] });
  assert.equal(words.label, 'Update daemon');
  assert.match(words.sentence, /0\.0\.9.*0\.1\.0.*act\.detach, act\.onlyStdin/);
});

test('names that reach the native message are made printable: a bidi override in a refused id is shown as U+FFFD', () => {
  const hostile = 'A4' + String.fromCharCode(0x202e) + 'x';
  const words = rootFailureText({ kind: 'idsRefused', refused: [hostile], allowed: ['A4'] });
  assert.equal(words.sentence.includes(String.fromCharCode(0x202e)), false);
  assert.ok(words.sentence.includes('A4' + String.fromCharCode(0xfffd) + 'x'));
});
