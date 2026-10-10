import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

import { judgedFolderOf, type JudgedFolder } from '../archive/judgedFolder';
import { ceilingMs } from '../client/ceilings';
import { callConfig, configOutcomeOf, configRequest, configTail, type ConfigOp } from '../config/configCall';
import { DEFAULT_NUMBERS } from '../settings/numbers';
import { FAKE_EXIT } from './fake/fakeWsl';
import { fakeWorld, UBUNTU_RUNNING } from './support/fakeWorld';
import { GOLDEN_ROOT } from './support/paths';
import { exited } from './support/recordingRunner';

/**
 * Plan §15s, E10.S1: THE user-layer writer. One key, two values — a folder the daemon's `check-base` accepted, or the empty value
 * that stops the archive — unprivileged, its argv built by the client's builder, its text answer read by its exit alone.
 */

const TARGET = { wsl: 'C:\\Windows\\System32\\wsl.exe', distro: 'Ubuntu' };
const PREFIX = ['-d', 'Ubuntu', '--cd', '/', '--exec', '/opt/wsl-care/bin/wsl-care'];

function golden(name: string): Record<string, unknown> {
  return JSON.parse(fs.readFileSync(path.join(GOLDEN_ROOT, 'head', name), 'utf8')) as Record<string, unknown>;
}

function judged(): JudgedFolder {
  const folder = judgedFolderOf(golden('archive-check-base.json'));
  assert.ok(folder !== undefined, 'the accepted golden yields its folder');
  return folder;
}

test('the two ops and their tails, exactly: the judged folder, or the empty value — one key, never config reset', () => {
  assert.deepEqual(configTail({ op: 'setBaseFolder', folder: judged() }), ['config', 'set', 'archive.baseFolder', '/mnt/v/ai-archive']);
  assert.deepEqual(configTail({ op: 'clearBaseFolder' }), ['config', 'set', 'archive.baseFolder', '']);
});

test('the request: the client\'s argv, never -u, no stdin, no environment change, the run-read ceiling', () => {
  for (const op of [{ op: 'setBaseFolder', folder: judged() }, { op: 'clearBaseFolder' }] satisfies ConfigOp[]) {
    const request = configRequest(TARGET, op);
    assert.deepEqual(request.args, [...PREFIX, ...configTail(op)]);
    assert.equal(request.file, TARGET.wsl);
    assert.equal(request.args.includes('-u'), false);
    assert.equal(request.stdin, undefined);
    assert.equal(request.withoutEnv, undefined);
    assert.equal(request.timeoutMs, ceilingMs(DEFAULT_NUMBERS, { call: 'runRead' }));
  }
});

test('a value that was never judged does not type-check as a folder to write', () => {
  // @ts-expect-error — a plain string is not a JudgedFolder: only judgedFolderOf makes one.
  const op: ConfigOp = { op: 'setBaseFolder', folder: 'V:\\anything' };
  assert.equal(op.op, 'setBaseFolder');
});

test('judgedFolderOf: the accepted report\'s folder, as the daemon spelt it — and nothing from a refused or odd report', () => {
  assert.equal(judgedFolderOf(golden('archive-check-base.json')), '/mnt/v/ai-archive');
  assert.equal(judgedFolderOf(golden('archive-check-base-refused.json')), undefined, 'a refused folder is never written');
  const accepted = golden('archive-check-base.json');
  for (const odd of [{ accepted: 'true' }, { folder: 42 }, { folder: '' }, { folder: '-u' }, { folder: 'a\u0007b' }, { folder: 'x'.repeat(1025) }]) {
    assert.equal(judgedFolderOf({ ...accepted, ...odd }), undefined, JSON.stringify(odd).slice(0, 60));
  }
});

test('the outcome is read by the exit alone: 0 written (its line), 2 refused, 81 not as root, 70 a defect', () => {
  assert.deepEqual(configOutcomeOf(exited(0, 'archive.baseFolder = /mnt/v/ai-archive (user)\n'), 'Ubuntu'), { kind: 'written', line: 'archive.baseFolder = /mnt/v/ai-archive (user)' });
  assert.deepEqual(configOutcomeOf(exited(2, '', 'wsl-care: archive.baseFolder: refused (on-system-drive): no. Nothing was written.\n'), 'Ubuntu'), { kind: 'refused', messages: ['wsl-care: archive.baseFolder: refused (on-system-drive): no. Nothing was written.'] });
  assert.equal(configOutcomeOf(exited(81, '', 'wsl-care: run it as that user.\n'), 'Ubuntu').kind, 'notAsRoot');
  assert.equal(configOutcomeOf(exited(70, '', 'wsl-care: internal error: x\n'), 'Ubuntu').kind, 'internalDefect');
  assert.deepEqual(configOutcomeOf({ kind: 'timedOut', timeoutMs: 20_000, stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) }, 'Ubuntu'), { kind: 'timedOut', timeoutMs: 20_000 });
  assert.equal(configOutcomeOf({ kind: 'failedToStart', reason: 'no' }, 'Ubuntu').kind, 'wslFailed');
});

test('through the strict fake: the judged folder and the empty value are written; a value it did not judge is refused', async () => {
  const world = fakeWorld(UBUNTU_RUNNING);
  try {
    assert.equal((await callConfig(world.runner, TARGET, { op: 'setBaseFolder', folder: judged() })).kind, 'written');
    assert.equal((await callConfig(world.runner, TARGET, { op: 'clearBaseFolder' })).kind, 'written');
    const unjudged = await callConfig(world.runner, TARGET, { op: 'setBaseFolder', folder: '/mnt/v/elsewhere' as JudgedFolder });
    assert.deepEqual(unjudged.kind, 'unknownFailure');
    assert.ok(unjudged.kind === 'unknownFailure' && unjudged.code === FAKE_EXIT.refused);
  } finally {
    world.dispose();
  }
});

test('through the strict fake: a scripted refusal reaches the caller as the daemon\'s own line', async () => {
  const world = fakeWorld({ ...UBUNTU_RUNNING, configExit: { code: 2, stderr: 'wsl-care: archive.baseFolder: refused (gone): x. Nothing was written.\n' } });
  try {
    assert.deepEqual(await callConfig(world.runner, TARGET, { op: 'clearBaseFolder' }), { kind: 'refused', messages: ['wsl-care: archive.baseFolder: refused (gone): x. Nothing was written.'] });
  } finally {
    world.dispose();
  }
});
