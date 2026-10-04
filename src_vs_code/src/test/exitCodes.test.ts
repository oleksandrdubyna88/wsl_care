import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

import { DAEMON_EXIT } from '../client/exitCodes';
import { REPOSITORY_ROOT } from './support/paths';

/**
 * The client's exit-code names are the daemon's (`common.testing`: a contract with two implementations — enumerate,
 * never retype). The C# enum is READ, member by member, and every code the client names must be there with the same
 * value; the C# file stays the one source the numbers come from. (§15f #10's checked-in `contracts/*.json` held equal
 * by a daemon test is E6.S0's.)
 */

const EXIT_CODE_CS = path.join(REPOSITORY_ROOT, 'src_daemon', 'src', 'WslCare.Cli', 'ExitCode.cs');

/** `Name = 12,` lines of the enum, as name → value. */
function daemonExitCodes(text: string): Map<string, number> {
  return new Map([...text.matchAll(/^\s*([A-Z][A-Za-z]+)\s*=\s*(\d+)\s*,/gm)].map((m) => [m[1] ?? '', Number(m[2])]));
}

function pascal(name: string): string {
  return name.charAt(0).toUpperCase() + name.slice(1);
}

test('the enum reader finds the daemon\'s codes (its known instances)', () => {
  const codes = daemonExitCodes(fs.readFileSync(EXIT_CODE_CS, 'utf8'));
  assert.equal(codes.get('Ok'), 0);
  assert.equal(codes.get('Usage'), 2);
  assert.ok(codes.size >= 10, `only ${codes.size} members read`);
});

test('every exit code the client names is the daemon\'s, by name and value', () => {
  const codes = daemonExitCodes(fs.readFileSync(EXIT_CODE_CS, 'utf8'));
  for (const [name, value] of Object.entries(DAEMON_EXIT)) {
    assert.equal(codes.get(pascal(name)), value, `${name}: the daemon's ExitCode.${pascal(name)} is ${codes.get(pascal(name))}`);
  }
});

test('a renamed or renumbered member would be caught: the reader sees a planted change', () => {
  const planted = daemonExitCodes('internal enum ExitCode\n{\n    Ok = 0,\n    Usage = 64,\n}');
  assert.equal(planted.get('Usage'), 64);
  assert.equal(planted.get('RunFailed'), undefined);
});

// ---- E6.S2 (plan §15j m3): every code of the generated contract is named here, and nothing else is ----

const EXIT_CODES_JSON = path.join(REPOSITORY_ROOT, 'contracts', 'exit-codes.json');

function contractCodes(): Map<string, number> {
  const json = JSON.parse(fs.readFileSync(EXIT_CODES_JSON, 'utf8')) as { codes: { name: string; code: number }[] };
  return new Map(json.codes.map((c) => [c.name, c.code]));
}

test('the client names EXACTLY the codes of contracts/exit-codes.json — the daemon writes that file from ExitCode', () => {
  const contract = contractCodes();
  assert.ok(contract.size >= 16, `only ${contract.size} codes read from the contract`);
  assert.deepEqual(Object.fromEntries(Object.entries(DAEMON_EXIT).sort()), Object.fromEntries([...contract.entries()].sort()),
    'a code the daemon added without a name here is an exit the act paths would read as an unknown failure');
});

test('the root paths\' codes are among them by name: 69, 71, 73, 75, 76, 77, 78, 79, 80', () => {
  const contract = contractCodes();
  for (const [name, code] of [['detachUnavailable', 69], ['detachStartFailed', 71], ['queueFull', 73], ['busy', 75], ['wedged', 76], ['needsRoot', 77], ['observeOnly', 78], ['stateUnreadable', 79], ['requestGone', 80]] as const) {
    assert.equal(contract.get(name), code, name);
  }
});
