import assert from 'node:assert/strict';
import { test } from 'node:test';

import { WindowsCareClient, type BundledRead } from '../client/WindowsCareClient';
import { closedRunner, type ProcessRequest, type ProcessResult } from '../process/runner';

/**
 * The minimal client of the bundled Windows daemon (E7.S5a, research/PLAN_bundle_windows_binary.md §3.7): it starts ONLY
 * `<extension>/bin/wsl-care.exe`, by absolute path, with the `--version` verb, through the runner seam it is given — and
 * nothing at all off Windows or for an extension that is not on a local disk.
 */

const EXTENSION = { scheme: 'file', fsPath: 'C:\\Users\\u\\.vscode\\extensions\\pub.ai-os-care-0.5.0-win32-x64' };
const EXE = 'C:\\Users\\u\\.vscode\\extensions\\pub.ai-os-care-0.5.0-win32-x64\\bin\\wsl-care.exe';

function recording(result: ProcessResult): { runner: (r: ProcessRequest) => Promise<ProcessResult>; requests: ProcessRequest[] } {
  const requests: ProcessRequest[] = [];
  return { requests, runner: (request) => { requests.push(request); return Promise.resolve(result); } };
}

function exited(code: number, stdout: string, stderr = ''): ProcessResult {
  return { kind: 'exited', code, stdout: Buffer.from(stdout), stderr: Buffer.from(stderr) };
}

function client(runner: (r: ProcessRequest) => Promise<ProcessResult>, platform = 'win32', extension = EXTENSION): WindowsCareClient {
  return new WindowsCareClient({ runner, platform, extension, timeoutMs: () => 20_000 });
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
  assert.match(JSON.stringify(await client(recording({ kind: 'timedOut', timeoutMs: 20_000, stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) }).runner).version()), /did not answer within 20 s \(wslCare\.timeouts\.versionSeconds\)/);
  assert.match(JSON.stringify(await client(recording(exited(0, '   ')).runner).version()), /printed no version/);
});

test('the closed runner (Test mode without the fake) starts nothing and is told as a failed start', async () => {
  const read = await client(closedRunner).version();
  assert.equal(read.kind, 'failed');
});
