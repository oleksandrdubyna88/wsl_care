import assert from 'node:assert/strict';
import { test } from 'node:test';

import { WindowsCareClient } from '../client/WindowsCareClient';
import { BUNDLED_EXE_FILE, EXTENSION, recording, exited, STAGED_DIGEST, windowsClient } from './support/windowsTasks';

/**
 * The bundled exe's SHA-256 as the CLIENT computes it (WR-E2, the story's plan round 4fc3f328, finding 5): the digest in the panel's
 * "Windows daemon copy" line and the folder of the staged copy it asks `tasks render` about. A file read — nothing is started.
 */

test('the digest is the one the client\'s own seam computed for the bundled exe — and no process was started for it', async () => {
  const seen: string[] = [];
  const { runner, requests } = recording(exited(0));
  const client = new WindowsCareClient({ runner, platform: 'win32', extension: EXTENSION, timeoutMs: () => 20_000, statusTimeoutMs: () => 20_000, digestOf: (file) => { seen.push(file); return Promise.resolve(STAGED_DIGEST); } });
  assert.deepEqual(await client.bundledDigest(), { kind: 'digest', digest: STAGED_DIGEST });
  assert.deepEqual(seen, [BUNDLED_EXE_FILE]);
  assert.deepEqual(requests, []);
});

test('off Windows, or off a local disk, no bundled exe is named: the digest is unavailable, with the reason, and nothing is read', async () => {
  const { runner } = recording(exited(0));
  const read: string[] = [];
  const digestOf = (file: string): Promise<string> => { read.push(file); return Promise.resolve(STAGED_DIGEST); };
  const linux = new WindowsCareClient({ runner, platform: 'linux', extension: EXTENSION, timeoutMs: () => 1, statusTimeoutMs: () => 1, digestOf });
  const remote = new WindowsCareClient({ runner, platform: 'win32', extension: { scheme: 'vscode-remote', fsPath: '/home/u/.vscode/extensions/x' }, timeoutMs: () => 1, statusTimeoutMs: () => 1, digestOf });
  assert.deepEqual(await linux.bundledDigest(), { kind: 'unavailable', reason: 'the bundled daemon is the Windows build, and this extension host is not Windows' });
  assert.equal((await remote.bundledDigest()).kind, 'unavailable');
  assert.deepEqual(read, []);
});

test('a bundled exe that cannot be read, or a digest that is not 64 lower-case hex digits, is a failure with its reason — never a digest', async () => {
  const { runner } = recording(exited(0));
  const unreadable = new WindowsCareClient({ runner, platform: 'win32', extension: EXTENSION, timeoutMs: () => 1, statusTimeoutMs: () => 1, digestOf: () => Promise.reject(new Error('EBUSY: resource busy')) });
  assert.deepEqual(await unreadable.bundledDigest(), { kind: 'failed', reason: 'the bundled exe could not be read to learn its digest: EBUSY: resource busy' });
  for (const odd of ['', 'ABCDEF', STAGED_DIGEST.toUpperCase(), `${STAGED_DIGEST}0`]) {
    const bad = windowsClient(runner, { digest: odd });
    assert.equal((await bad.bundledDigest()).kind, 'failed', JSON.stringify(odd));
  }
});
