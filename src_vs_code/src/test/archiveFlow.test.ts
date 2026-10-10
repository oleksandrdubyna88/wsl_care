import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

import { chooseArchiveFolder, pickedPath, STOP_LABEL, stopArchiving, USE_FOLDER_LABEL, type ArchiveFlowDeps } from '../archive/archiveFlow';
import { newArchiveRecorder, recordingArchiveUi, type ArchiveRecorder } from '../archive/archiveRecorder';
import type { Failure, ReadOutcome } from '../client/outcome';
import type { RunRead } from '../client/verbs';
import type { ProcessRequest } from '../process/runner';
import { DEFAULT_NUMBERS } from '../settings/numbers';
import { FALLBACK_LIMITS } from '../shared/daemonLimits';
import { GOLDEN_ROOT } from './support/paths';
import { exited } from './support/recordingRunner';

/**
 * Plan §15s, E10.S1: the archive's two flows over a recorded UI and a scripted runner. The order is the contract: pick → the
 * daemon judges → a refusal is told and NOTHING is written → an accepted folder's modal → only *Use this folder* writes, the
 * daemon's own spelling of it; *Stop archiving* writes the empty value after its modal.
 */

function golden(name: string): Record<string, unknown> {
  return JSON.parse(fs.readFileSync(path.join(GOLDEN_ROOT, 'head', name), 'utf8')) as Record<string, unknown>;
}

const TARGET = { wsl: 'C:\\Windows\\System32\\wsl.exe', distro: 'Ubuntu' };

interface World {
  readonly deps: ArchiveFlowDeps;
  readonly recorder: ArchiveRecorder;
  readonly reads: RunRead[];
  readonly writes: ProcessRequest[];
  readonly busy: string[];
}

function world(options: { report?: Record<string, unknown>; checkFails?: Failure; target?: Failure; writeExit?: number } = {}): World {
  const recorder = newArchiveRecorder();
  const reads: RunRead[] = [];
  const writes: ProcessRequest[] = [];
  const busy: string[] = [];
  const read = (request: RunRead): Promise<ReadOutcome> => {
    reads.push(request);
    return Promise.resolve(options.checkFails === undefined ? { kind: 'read', read: request.read, distro: 'Ubuntu', body: options.report ?? golden('archive-check-base.json') } : { ...options.checkFails, read: request.read });
  };
  const deps: ArchiveFlowDeps = {
    ui: recordingArchiveUi(recorder), read, target: () => Promise.resolve(options.target ?? TARGET),
    runner: (request) => { writes.push(request); return Promise.resolve(exited(options.writeExit ?? 0, 'archive.baseFolder = x (user)\n', options.writeExit === undefined ? '' : 'wsl-care: no.\n')); },
    numbers: () => DEFAULT_NUMBERS, limits: () => FALLBACK_LIMITS, busy: (text) => busy.push(text),
  };

  return { deps, recorder, reads, writes, busy };
}

const CONFIG_SET = ['-d', 'Ubuntu', '--cd', '/', '--exec', '/opt/wsl-care/bin/wsl-care', 'config', 'set', 'archive.baseFolder'];

test('choose: pick → check-base of the picked folder → the modal → Use this folder writes the DAEMON\'s spelling of it', async () => {
  const w = world();
  w.recorder.picked = 'v:\\ai-archive';
  w.recorder.answer = true;
  assert.equal(await chooseArchiveFolder(w.deps), 'written');
  assert.deepEqual(w.reads, [{ read: 'archiveCheckBase', path: 'V:\\ai-archive' }], 'asked as Windows spells it (upper-case drive)');
  assert.equal(w.recorder.modals.length, 1);
  assert.equal(w.recorder.modals[0]?.confirm, USE_FOLDER_LABEL);
  assert.match(w.recorder.modals[0]?.message ?? '', /V:\\ai-archive/);
  assert.match(w.recorder.modals[0]?.detail ?? '', /will write to \/mnt\/v\/ai-archive/);
  assert.match(w.recorder.modals[0]?.detail ?? '', /Warning: the Windows profile is unknown/);
  assert.deepEqual(w.writes.map((r) => r.args), [[...CONFIG_SET, '/mnt/v/ai-archive']]);
  assert.equal(w.recorder.notices.at(-1)?.level, 'info');
  assert.deepEqual(w.busy, ['Checking the folder…', '', 'Writing the archive setting…', '']);
});

test('choose: a dismissed dialog asks nothing and writes nothing', async () => {
  const w = world();
  assert.equal(await chooseArchiveFolder(w.deps), 'cancelled');
  assert.equal(w.recorder.picks, 1);
  assert.deepEqual([w.reads, w.writes, w.recorder.modals], [[], [], []]);
});

test('choose: a REFUSED folder is told with its rule — no modal, nothing written', async () => {
  const w = world({ report: golden('archive-check-base-refused.json') });
  w.recorder.picked = 'D:\\x';
  w.recorder.answer = true;
  assert.equal(await chooseArchiveFolder(w.deps), 'refused');
  assert.deepEqual([w.writes, w.recorder.modals], [[], []]);
  assert.equal(w.recorder.notices[0]?.level, 'warn');
  assert.match(w.recorder.notices[0]?.sentence ?? '', /^That folder cannot hold the archive \(overlap\): .* Nothing was written\.$/);
});

test('choose: a declined modal writes nothing', async () => {
  const w = world();
  w.recorder.picked = 'V:\\ai-archive';
  assert.equal(await chooseArchiveFolder(w.deps), 'cancelled');
  assert.deepEqual(w.writes, []);
});

test('choose: a check that failed is told and nothing is written; a target that failed is told and nothing is started', async () => {
  const failedCheck = world({ checkFails: { kind: 'stopped', distro: 'Ubuntu' } });
  failedCheck.recorder.picked = 'V:\\a';
  assert.equal(await chooseArchiveFolder(failedCheck.deps), 'failed');
  assert.match(failedCheck.recorder.notices[0]?.sentence ?? '', /^The folder could not be checked: The distribution "Ubuntu" is not running/);
  const noTarget = world({ target: { kind: 'stopped', distro: 'Ubuntu' } });
  noTarget.recorder.picked = 'V:\\a';
  noTarget.recorder.answer = true;
  assert.equal(await chooseArchiveFolder(noTarget.deps), 'failed');
  assert.deepEqual(noTarget.writes, []);
  assert.match(noTarget.recorder.notices.at(-1)?.sentence ?? '', /^The archive setting was not written: /);
});

test('choose: the daemon refusing the write (2) is told with its own line', async () => {
  const w = world({ writeExit: 2 });
  w.recorder.picked = 'V:\\ai-archive';
  w.recorder.answer = true;
  assert.equal(await chooseArchiveFolder(w.deps), 'failed');
  assert.match(w.recorder.notices.at(-1)?.sentence ?? '', /The daemon refused the request\. wsl-care: no\./);
});

test('stop: its modal, then the EMPTY value — and a declined modal writes nothing', async () => {
  const declined = world();
  assert.equal(await stopArchiving(declined.deps), 'cancelled');
  assert.deepEqual(declined.writes, []);
  assert.equal(declined.recorder.modals[0]?.confirm, STOP_LABEL);
  const w = world();
  w.recorder.answer = true;
  assert.equal(await stopArchiving(w.deps), 'written');
  assert.deepEqual(w.writes.map((r) => r.args), [[...CONFIG_SET, '']]);
  assert.match(w.recorder.notices.at(-1)?.sentence ?? '', /^Archiving stopped/);
});

test('pickedPath: a lower-case drive letter is raised; UNC and other paths are kept as they are', () => {
  assert.equal(pickedPath('v:\\ai archive'), 'V:\\ai archive');
  assert.equal(pickedPath('V:\\a'), 'V:\\a');
  assert.equal(pickedPath('\\\\nas\\share\\a'), '\\\\nas\\share\\a');
});

// ---- the code round (coai 237ecc90): finding 4 ----

test('code round #4: a write that TIMED OUT is "unknown", not "failed" — the setting may be in force', async () => {
  const w = world();
  w.recorder.picked = 'V:\\ai-archive';
  w.recorder.answer = true;
  const timedOut: ArchiveFlowDeps = { ...w.deps, runner: () => Promise.resolve({ kind: 'timedOut', timeoutMs: 20_000, stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) }) };
  assert.equal(await chooseArchiveFolder(timedOut), 'unknown');
  assert.equal(w.recorder.notices.at(-1)?.level, 'warn');
  assert.match(w.recorder.notices.at(-1)?.sentence ?? '', /^The archive setting may or may not have been written/);
});
