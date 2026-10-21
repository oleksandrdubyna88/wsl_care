import assert from 'node:assert/strict';
import { test } from 'node:test';

import { parseAnswer } from '../client/handshake';
import type { VerbOutcome } from '../client/outcome';
import { WindowsCareClient, type BundledRead } from '../client/WindowsCareClient';
import { WINDOWS_SIDE } from '../client/windowsStatus';
import { closedRunner, type ProcessRequest, type ProcessResult } from '../process/runner';
import { fakeWorld, UBUNTU_RUNNING } from './support/fakeWorld';
import { headBody } from './support/outcomes';

/**
 * The minimal client of the bundled Windows daemon (E7.S5a, research/PLAN_bundle_windows_binary.md §3.7): it starts ONLY
 * `<extension>/bin/wsl-care.exe`, by absolute path, with the `--version` verb, through the runner seam it is given — and
 * nothing at all off Windows or for an extension that is not on a local disk.
 */

const EXTENSION = { scheme: 'file', fsPath: 'C:\\Users\\u\\.vscode\\extensions\\pub.ai-os-care-0.5.0-win32-x64' };
const STATUS_MS = 12_000;
const EXE = 'C:\\Users\\u\\.vscode\\extensions\\pub.ai-os-care-0.5.0-win32-x64\\bin\\wsl-care.exe';

function recording(result: ProcessResult): { runner: (r: ProcessRequest) => Promise<ProcessResult>; requests: ProcessRequest[] } {
  const requests: ProcessRequest[] = [];
  return { requests, runner: (request) => { requests.push(request); return Promise.resolve(result); } };
}

function exited(code: number, stdout: string, stderr = ''): ProcessResult {
  return { kind: 'exited', code, stdout: Buffer.from(stdout), stderr: Buffer.from(stderr) };
}

function client(runner: (r: ProcessRequest) => Promise<ProcessResult>, platform = 'win32', extension = EXTENSION): WindowsCareClient {
  return new WindowsCareClient({ runner, platform, extension, timeoutMs: () => 20_000, statusTimeoutMs: () => STATUS_MS });
}

test('it starts exactly <extension>\\bin\\wsl-care.exe, by absolute path, with --version and the version ceiling — and reads the answer', async () => {
  const r = recording(exited(0, '0.4.0+0123456789abcdef0123456789abcdef01234567\r\n'));
  const read = await client(r.runner).version();
  assert.deepEqual(r.requests, [{ file: EXE, args: ['--version'], timeoutMs: 20_000 }]);
  assert.deepEqual(read, { kind: 'version', text: '0.4.0+0123456789abcdef0123456789abcdef01234567' } satisfies BundledRead);
});

test('off Windows, or for an extension not on a local disk, nothing is started', async () => {
  for (const [platform, extension] of [['linux', EXTENSION], ['darwin', EXTENSION], ['win32', { scheme: 'vscode-remote', fsPath: '/home/u/.vscode-server/extensions/x' }], ['win32', { scheme: 'file', fsPath: 'relative\\folder' }],
    // Own Fable review of the code, note 11: a share (UNC) and a root without a drive are not the extension's local disk.
    ['win32', { scheme: 'file', fsPath: '\\\\server\\share\\extensions\\pub.ai-os-care-0.5.0-win32-x64' }],
    ['win32', { scheme: 'file', fsPath: '\\Users\\u\\.vscode\\extensions\\pub.ai-os-care-0.5.0-win32-x64' }]] as const) {
    const r = recording(exited(0, '0.4.0'));
    const read = await client(r.runner, platform, extension).version();
    assert.equal(read.kind, 'unavailable', `${platform} ${extension.scheme}`);
    assert.equal(r.requests.length, 0, 'nothing started');
  }
});

test('a refused start shows Node\'s message as Windows gave it — never a guessed code; a failing exit and a timeout are said too', async () => {
  const refused = await client(recording({ kind: 'failedToStart', reason: 'spawn UNKNOWN: An Application Control policy has blocked this file.' }).runner).version();
  assert.deepEqual(refused, { kind: 'failed', reason: 'it could not start: spawn UNKNOWN: An Application Control policy has blocked this file.' });
  assert.match(JSON.stringify(await client(recording(exited(3, '', 'boom\nmore')).runner).version()), /exited 3: boom/);
  assert.match(JSON.stringify(await client(recording({ kind: 'timedOut', timeoutMs: 20_000, treeEnded: false, stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) }).runner).version()), /did not answer within 20 s \(wslCare\.timeouts\.versionSeconds\)/);
  assert.match(JSON.stringify(await client(recording(exited(0, '   ')).runner).version()), /printed no version/);
});

test('the closed runner (Test mode without the fake) starts nothing and is told as a failed start', async () => {
  const read = await client(closedRunner).version();
  assert.equal(read.kind, 'failed');
});

// ---- E19.S2b (todo/PLAN_disk_hygiene_windows_E19.md decision 12): the Windows `status` read ----

/** The Windows reading's body: the distro's golden with the side named — no Windows golden exists yet (the daemon's scenario harness writes the `wsl` side only). */
function windowsBody(): Record<string, unknown> {
  return { ...headBody('status'), side: 'windows' };
}

function answeredStatus(outcome: VerbOutcome): Extract<VerbOutcome, { kind: 'answered' }> {
  assert.equal(outcome.kind, 'answered', JSON.stringify(outcome));
  return outcome as Extract<VerbOutcome, { kind: 'answered' }>;
}

test('the windows status is read through the bundled exe and parsed like the distro\'s', async () => {
  const text = JSON.stringify(windowsBody());
  const r = recording(exited(0, text));
  const outcome = answeredStatus(await client(r.runner).status());
  assert.deepEqual(r.requests, [{ file: EXE, args: ['status', '--json'], timeoutMs: STATUS_MS }], 'the bundled exe, by absolute path, with the one status tail and its own ceiling');
  assert.equal(outcome.verb, 'status');
  assert.equal(outcome.distro, WINDOWS_SIDE, 'the outcome says it is about the Windows side, not a distribution');
  assert.deepEqual(outcome.answer, parseAnswer('status', text), 'the same checkedBody / statusAnswer the distro\'s status goes through');
  assert.equal(outcome.answer.verb === 'status' && outcome.answer.body.side, 'windows', 'the raw body is kept');
});

test('the windows status through the Test-mode fake: exactly the bundled path, exactly status --json, the golden-derived body', async () => {
  const world = fakeWorld({ ...UBUNTU_RUNNING, bundledExe: EXE });
  try {
    const outcome = answeredStatus(await client(world.runner).status());
    assert.deepEqual(world.files(), [EXE]);
    assert.deepEqual(world.calls(), ['status --json']);
    assert.equal(outcome.answer.verb === 'status' && outcome.answer.body.side, 'windows');
    assert.ok(outcome.answer.verb === 'status' && (outcome.answer.verdicts ?? []).length > 0, 'the golden\'s verdicts are read');
  } finally {
    world.dispose();
  }
});

test('a windows status that fails reads as the failure kinds the client already uses — never a thrown error', async () => {
  const kind = async (result: ProcessResult): Promise<string> => (await client(recording(result).runner).status()).kind;
  assert.deepEqual(await client(recording({ kind: 'timedOut', timeoutMs: STATUS_MS, treeEnded: false, stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) }).runner).status(), { kind: 'timedOut', timeoutMs: STATUS_MS, verb: 'status' });
  assert.equal(await kind(exited(2, '', 'wsl-care: no such verb\r\n')), 'refused');
  assert.equal(await kind(exited(70, '', 'wsl-care: defect\r\n')), 'internalDefect');
  assert.equal(await kind(exited(1, '', 'wsl-care: boom\r\n')), 'unknownFailure');
  assert.equal(await kind(exited(0, 'not json')), 'unparseable');
  assert.equal(await kind(exited(0, JSON.stringify({ schemaVersion: 99 }))), 'needsNewerExtension');
  const refused = await client(recording({ kind: 'failedToStart', reason: 'spawn UNKNOWN: An Application Control policy has blocked this file.' }).runner).status();
  assert.equal(refused.kind, 'unknownFailure');
  assert.match(JSON.stringify(refused), /Application Control policy/, 'Node\'s message as Windows gave it');
  assert.equal((await client(closedRunner).status()).kind, 'unknownFailure', 'the closed runner (Test mode without the fake) starts nothing');
});

test('off Windows, or for an extension not on a local disk, no status is read and nothing is started', async () => {
  for (const [platform, extension] of [['linux', EXTENSION], ['win32', { scheme: 'vscode-remote', fsPath: '/home/u/.vscode-server/extensions/x' }]] as const) {
    const r = recording(exited(0, JSON.stringify(windowsBody())));
    const outcome = await client(r.runner, platform, extension).status();
    assert.notEqual(outcome.kind, 'answered', `${platform} ${extension.scheme}`);
    assert.equal(outcome.verb, 'status');
    assert.equal(r.requests.length, 0, 'nothing started');
  }
  assert.equal((await client(recording(exited(0, '')).runner, 'linux').status()).kind, 'notWindows');
});

test('a native crash text with no wsl-care: line still reaches the messages — the last five non-empty lines, each cut', async () => {
  const crash = ['Unhandled exception. System.InvalidOperationException: boom', ...Array.from({ length: 8 }, (_, i) => `   at Frame${i}`), '', 'x'.repeat(500)].join('\r\n');
  const outcome = await client(recording(exited(134, '', crash)).runner).status();
  assert.equal(outcome.kind, 'unknownFailure');
  const messages = outcome.kind === 'unknownFailure' ? outcome.messages : [];
  assert.equal(messages.length, 5);
  assert.ok(messages.every((m) => m.length <= 200), 'each line cut to a sane length');
  assert.ok(messages.at(-1)?.startsWith('xxx'), 'the LAST lines are kept');
  const prefixed = await client(recording(exited(70, '', 'noise\r\nwsl-care: a defect\r\n')).runner).status();
  assert.deepEqual(prefixed.kind === 'internalDefect' && prefixed.messages, ['wsl-care: a defect'], 'a wsl-care: line wins when there is one');
});
