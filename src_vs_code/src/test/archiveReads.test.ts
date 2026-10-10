import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

import type { ReadOutcome } from '../client/outcome';
import { RUN_READ_TIMEOUT_MS, runReadTail } from '../client/verbs';
import { WslCareClient } from '../client/WslCareClient';
import { TEST_ENV } from './support/fakeWorld';
import { GOLDEN_ROOT } from './support/paths';
import { daemonArgv, exited, exitedUtf16, LIST_QUIET, LIST_RUNNING, LIST_VERBOSE, recordingRunner, verboseTable, type Scripted } from './support/recordingRunner';

/**
 * Plan §15s, E10.S1: the three archive reads — `archive status --json`, `archive preview --json`, `archive check-base <path> --json`
 * — unprivileged (never `-u`), after the three WSL questions, never to a stopped distribution; the path (the first value of a path
 * the client ever sends) checked BEFORE anything starts by the daemon's own rule (not empty, not starting with `-`, no control
 * character) and the key's limit (1024); their answers read over the daemon's goldens.
 */

function goldenText(name: string): string {
  return fs.readFileSync(path.join(GOLDEN_ROOT, 'head', name), 'utf8');
}

function wsl(): Record<string, Scripted> {
  return {
    [LIST_QUIET]: exitedUtf16(0, 'Ubuntu\r\n'),
    [LIST_VERBOSE]: exitedUtf16(0, verboseTable([{ name: 'Ubuntu', running: true }], 'Ubuntu')),
    [LIST_RUNNING]: exitedUtf16(0, 'Ubuntu\r\n'),
  };
}

function client(script: Record<string, Scripted>): { c: WslCareClient; rec: ReturnType<typeof recordingRunner> } {
  const rec = recordingRunner(script);
  return { c: new WslCareClient({ runner: rec.runner, platform: 'win32', env: TEST_ENV, distroSetting: () => '' }), rec };
}

function bodyOf(outcome: ReadOutcome): Record<string, unknown> {
  assert.equal(outcome.kind, 'read', JSON.stringify(outcome));
  assert.ok(outcome.kind === 'read');
  return outcome.body;
}

const FOLDER = 'V:\\ai archive\\base';
const STATUS = daemonArgv('Ubuntu', ['archive', 'status', '--json']);
const PREVIEW = daemonArgv('Ubuntu', ['archive', 'preview', '--json']);
const CHECK = daemonArgv('Ubuntu', ['archive', 'check-base', FOLDER, '--json']);

test('the three archive tails are exactly the daemon verbs, each ending in --json', () => {
  assert.deepEqual(runReadTail({ read: 'archiveStatus' }), ['archive', 'status', '--json']);
  assert.deepEqual(runReadTail({ read: 'archivePreview' }), ['archive', 'preview', '--json']);
  assert.deepEqual(runReadTail({ read: 'archiveCheckBase', path: FOLDER }), ['archive', 'check-base', FOLDER, '--json']);
});

test('archive status: after the three WSL questions, unprivileged, the run-read ceiling', async () => {
  const { c, rec } = client({ ...wsl(), [STATUS]: exited(0, goldenText('archive-status.json')) });
  const outcome = await c.read({ read: 'archiveStatus' });
  assert.ok('outcome' in bodyOf(outcome) || 'side' in bodyOf(outcome));
  assert.deepEqual(rec.argvs(), [LIST_QUIET, LIST_VERBOSE, LIST_RUNNING, STATUS]);
  assert.equal(rec.requests.at(-1)?.timeoutMs, RUN_READ_TIMEOUT_MS.archiveStatus);
  assert.ok(rec.requests.every((r) => !r.args.includes('-u')));
});

test('archive preview: its own ceiling, above the daemon\'s archive.previewTimeoutSeconds at its range maximum', async () => {
  const { c, rec } = client({ ...wsl(), [PREVIEW]: exited(0, goldenText('archive-preview.json')) });
  const outcome = await c.read({ read: 'archivePreview' });
  assert.ok(Array.isArray(bodyOf(outcome).agents));
  assert.equal(rec.argvs().at(-1), PREVIEW);
  assert.equal(rec.requests.at(-1)?.timeoutMs, RUN_READ_TIMEOUT_MS.archivePreview);
  assert.ok(RUN_READ_TIMEOUT_MS.archivePreview > 600_000, 'the daemon may take up to 600 s (the key\'s maximum)');
});

test('archive check-base: the picked folder as ONE argument, spaces and backslashes as they are', async () => {
  const { c, rec } = client({ ...wsl(), [CHECK]: exited(0, goldenText('archive-check-base.json')) });
  const outcome = await c.read({ read: 'archiveCheckBase', path: FOLDER });
  assert.equal(bodyOf(outcome).accepted, true);
  assert.equal(rec.argvs().at(-1), CHECK);
  assert.ok(rec.requests.every((r) => !r.args.includes('-u')));
});

for (const [bad, why] of [
  ['', 'an empty path'],
  ['-rf', 'a path starting with a dash (an option to the daemon)'],
  ['V:\\a\u0007b', 'a control character'],
  [`V:\\${'x'.repeat(1030)}`, 'longer than the key takes'],
] as const) {
  test(`archive check-base refuses ${why} before anything starts`, async () => {
    const { c, rec } = client(wsl());
    const outcome = await c.read({ read: 'archiveCheckBase', path: bad });
    assert.equal(outcome.kind, 'readRefused');
    assert.equal(rec.requests.length, 0, 'nothing is started for a value the daemon would refuse');
  });
}
