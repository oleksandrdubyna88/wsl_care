import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { test } from 'node:test';

import { sha256OfFile } from '../client/fileDigest';
import { BUNDLED_DAEMON } from '../client/handshake';
import { staged, WindowsCareClient } from '../client/WindowsCareClient';
import { shippedSources } from './support/paths';
import { BUNDLED_EXE_FILE, EXTENSION, exited, LOCAL_APP_DATA, recording, renderAnswer, STAGED_DIGEST, STAGED_EXE, stageFixture, windowsClient } from './support/windowsTasks';

/**
 * WR-E1, the Opus own review of the story (PLAN_windows_runner.md §14): the staging folder is derived, not given; a stage answer
 * names the path the client expects; a render is registrable only for THIS build and THIS file; the Windows reads are the five the
 * docs say; a busy exit with another outcome is no busy; the client computes the digest it expects; the fixtures are the daemon's
 * words.
 */

// ---- M1: the staging folder is derived from LocalAppData and the fixed segments, and a free-form root is refused ----

test('M1: a LocalAppData that is not in its normal form — a .. segment, a . segment, forward slashes, doubled or trailing separators — names no staged exe', async () => {
  const roots = [
    'C:\\Users\\user\\..\\other',
    'C:\\Users\\user\\AppData\\.\\Local',
    'C:\\Users\\user\\AppData\\Local\\..\\..\\..\\Windows',
    'C:/Users/user/AppData/Local',
    'C:\\Users\\user\\\\AppData\\Local',
    'C:\\Users\\user\\AppData\\Local\\',
    '..\\Local',
    '\\\\server\\share\\Local',
    'relative',
    '',
  ];
  for (const root of roots) {
    const r = recording(exited(0, ''));
    const c = windowsClient(r.runner, { localAppData: root });
    assert.equal(typeof c.exe(staged(STAGED_DIGEST)) === 'string', false, JSON.stringify(root));
    assert.equal((await c.tasksRender('watch', STAGED_DIGEST)).kind, 'unavailable', JSON.stringify(root));
    assert.deepEqual(r.requests, [], `nothing started for ${root}`);
  }
});

test('M1: the staging folder is exactly <LocalAppData>\\wsl-care\\bin — the client cannot be handed another', () => {
  assert.equal(windowsClient(recording(exited(0, '')).runner).exe(staged(STAGED_DIGEST)), `${LOCAL_APP_DATA}\\wsl-care\\bin\\${STAGED_DIGEST}\\wsl-care.exe`);
  assert.equal(windowsClient(recording(exited(0, '')).runner, { localAppData: 'D:\\Profiles\\u\\AppData\\Local' }).exe(staged(STAGED_DIGEST)), `D:\\Profiles\\u\\AppData\\Local\\wsl-care\\bin\\${STAGED_DIGEST}\\wsl-care.exe`);
});

test('M1: a stage answer whose path is not the staged exe the client expects for that digest is provenanceRefused — compared without case, for a new copy and a reused one', async () => {
  for (const fixture of ['stage-staged.json', 'stage-reused.json']) {
    for (const wrong of ['C:\\Windows\\Temp\\wsl-care.exe', `C:\\Users\\user\\AppData\\Local\\wsl-care\\bin\\${'f'.repeat(64)}\\wsl-care.exe`, '', `D:\\x\\bin\\${STAGED_DIGEST}\\wsl-care.exe`]) {
      const result = await windowsClient(recording(exited(0, { ...stageFixture(fixture), path: wrong })).runner).tasksStage();
      assert.equal(result.kind, 'provenanceRefused', `${fixture} ${wrong}`);
      assert.match(JSON.stringify(result), /path/);
    }
    const sameWithoutCase = await windowsClient(recording(exited(0, { ...stageFixture(fixture), path: STAGED_EXE.toUpperCase() })).runner).tasksStage();
    assert.equal(sameWithoutCase.kind, fixture === 'stage-staged.json' ? 'staged' : 'reused', 'NTFS paths are case-insensitive');
  }
});

test('M1: with no staging folder known there is nothing to compare, so a stage is unavailable and starts nothing', async () => {
  const r = recording(exited(0, stageFixture('stage-staged.json')));
  assert.equal((await windowsClient(r.runner, { localAppData: null }).tasksStage()).kind, 'unavailable');
  assert.deepEqual(r.requests, []);
});

// ---- M2: a render is registrable only for this build and for the file the client ran ----

test('M2: an older retained copy that answers registrable for itself is not registrable — its version is not BUNDLED_DAEMON', async () => {
  const older = await windowsClient(recording(exited(0, { ...renderAnswer('watch from the staged copy'), version: '0.3.9' })).runner).tasksRender('watch', STAGED_DIGEST);
  assert.equal(older.kind, 'notRegistrable');
  assert.match(JSON.stringify(older), /0\.3\.9/);
  assert.match(JSON.stringify(older), new RegExp(BUNDLED_DAEMON.replace(/\./g, '\\.')));
});

test('M2: a registrable render whose command is not the file the client ran is not registrable; the same path in another case is', async () => {
  const other = `${LOCAL_APP_DATA}\\wsl-care\\bin\\${'f'.repeat(64)}\\wsl-care.exe`;
  const elsewhere = await windowsClient(recording(exited(0, { ...renderAnswer('watch from the staged copy'), command: other })).runner).tasksRender('watch', STAGED_DIGEST);
  assert.equal(elsewhere.kind, 'notRegistrable');
  assert.match(JSON.stringify(elsewhere), /f{64}/);
  const sameCase = await windowsClient(recording(exited(0, { ...renderAnswer('watch from the staged copy'), command: STAGED_EXE.toLowerCase() })).runner).tasksRender('watch', STAGED_DIGEST);
  assert.equal(sameCase.kind, 'registrable');
});

// ---- m1: read() takes the five Windows reads only ----

test('m1: archive preview and archive check-base are not Windows reads — refused before anything starts', async () => {
  const r = recording(exited(0, { schemaVersion: 1 }));
  const c = windowsClient(r.runner);
  assert.equal((await c.read({ read: 'archivePreview' } as never)).kind, 'readRefused');
  assert.equal((await c.read({ read: 'archiveCheckBase', path: 'C:\\x' } as never)).kind, 'readRefused');
  assert.deepEqual(r.requests, []);
  for (const request of [{ read: 'archiveStatus' }, { read: 'archiveList' }, { read: 'runsShow', runId: '20261005T100000Z-77' }] as const) {
    assert.equal((await c.read(request)).kind, 'read', request.read);
  }
});

// ---- m2: exit 75 with a readable answer that is not busy is no busy ----

test('m2: exit 75 with a readable answer whose outcome is not busy is a failure — stage and remove — and with a busy answer or none it is busy', async () => {
  const stage = await windowsClient(recording(exited(75, stageFixture('stage-staged.json'))).runner).tasksStage();
  assert.equal(stage.kind, 'failed');
  assert.match(JSON.stringify(stage), /exited 75 but the answer says/);
  const remove = await windowsClient(recording(exited(75, stageFixture('stage-remove-removed.json'))).runner).tasksStageRemove();
  assert.equal(remove.kind, 'failed');
  assert.equal((await windowsClient(recording(exited(75, stageFixture('stage-busy.json'))).runner).tasksStage()).kind, 'busy');
  assert.equal((await windowsClient(recording(exited(75, stageFixture('stage-remove-busy.json'))).runner).tasksStageRemove()).kind, 'busy');
  assert.equal((await windowsClient(recording(exited(75, 'not json')).runner).tasksStage()).kind, 'busy');
});

// ---- the coai code round (session ef8a966f) ----

test('code round finding 2: exit 75 with a readable answer that names NO outcome is a failure, not a busy — stage and remove', async () => {
  const body = JSON.stringify({ schemaVersion: 1 });
  assert.equal((await windowsClient(recording(exited(75, body)).runner).tasksStage()).kind, 'failed');
  assert.equal((await windowsClient(recording(exited(75, body)).runner).tasksStageRemove()).kind, 'failed');
});

test('code round finding 8: a LocalAppData that names no staged copy says which value it received', async () => {
  const c = windowsClient(recording(exited(0, '')).runner, { localAppData: 'C:\\Users\\user\\AppData\\Local\\' });
  const render = await c.tasksRender('watch', STAGED_DIGEST);
  assert.equal(render.kind, 'unavailable');
  const reason = 'reason' in render ? String(render.reason) : '';
  assert.ok(reason.includes('"C:\\Users\\user\\AppData\\Local\\"'), `the received value is named, so the person sees the trailing separator: ${reason}`);
});

// ---- m3: one table of "it did not end" sentences ----

test('m3: the sentence for a process that did not exit is written once — the Windows client and its answers share it, and the version read keeps its setting name', async () => {
  const modules = shippedSources().filter((file) => /client[\\/](WindowsCareClient|windowsTasks)\.ts$/.test(file));
  assert.equal(modules.length, 2);
  const spelling = modules.filter((file) => fs.readFileSync(file, 'utf8').includes('did not answer within'));
  assert.equal(spelling.length, 1, spelling.join(', '));
  const timedOut = { kind: 'timedOut', timeoutMs: 20_000, treeEnded: false, stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) } as const;
  assert.match(JSON.stringify(await windowsClient(recording(timedOut).runner).version()), /did not answer within 20 s \(wslCare\.timeouts\.versionSeconds\)/);
  assert.match(JSON.stringify(await windowsClient(recording(timedOut).runner).tasksStage()), /did not answer within 20 s"/);
});

// ---- m6: the client computes the expected digest of the bundled exe itself ----

test('m6: tasksStage hashes the bundled exe through the digest seam and judges the answer against THAT, the caller supplying nothing', async () => {
  const seen: string[] = [];
  const r = recording(exited(0, stageFixture('stage-staged.json')));
  const c = new WindowsCareClient({ runner: r.runner, platform: 'win32', extension: EXTENSION, timeoutMs: () => 20_000, statusTimeoutMs: () => 20_000, localAppData: LOCAL_APP_DATA, digestOf: (file) => { seen.push(file); return Promise.resolve('e'.repeat(64)); } });
  const result = await c.tasksStage();
  assert.deepEqual(seen, [BUNDLED_EXE_FILE], 'it read the bundled exe, not the staged copy');
  assert.equal(result.kind, 'provenanceRefused');
  assert.match(JSON.stringify(result), /e{64}/);
});

test('m6: a bundled exe that cannot be read fails the stage before anything starts, saying why; a digest that is no digest does the same', async () => {
  for (const digestOf of [() => Promise.reject(new Error('EBUSY: resource busy or locked')), () => Promise.resolve('NOT-A-DIGEST')]) {
    const r = recording(exited(0, stageFixture('stage-staged.json')));
    const c = new WindowsCareClient({ runner: r.runner, platform: 'win32', extension: EXTENSION, timeoutMs: () => 20_000, statusTimeoutMs: () => 20_000, localAppData: LOCAL_APP_DATA, digestOf });
    const result = await c.tasksStage();
    assert.equal(result.kind, 'failed');
    assert.deepEqual(r.requests, []);
  }
});

test('m6: the default digest seam is the SHA-256 of the file\'s bytes, lower-case hex, streamed from disk', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'testsToDelete-wr-e1-'));
  try {
    const file = path.join(dir, 'x.bin');
    fs.writeFileSync(file, 'abc');
    assert.equal(await sha256OfFile(file), 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad');
    await assert.rejects(() => sha256OfFile(path.join(dir, 'missing.bin')), /ENOENT/);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('m6: with the real digest seam a bundled exe that is not on this disk fails the stage naming the read, and starts nothing', async () => {
  const r = recording(exited(0, stageFixture('stage-staged.json')));
  const c = new WindowsCareClient({ runner: r.runner, platform: 'win32', extension: { scheme: 'file', fsPath: 'Z:\\testsToDelete-no-such-extension' }, timeoutMs: () => 20_000, statusTimeoutMs: () => 20_000, localAppData: LOCAL_APP_DATA });
  const result = await c.tasksStage();
  assert.equal(result.kind, 'failed');
  assert.match(JSON.stringify(result), /bundled exe/);
  assert.deepEqual(r.requests, []);
});

// ---- m5: the fixtures are the daemon's words ----

test('m5: the stage fixtures speak as Staging.cs does and agree on one source path — the extension tests\' own publisher, no user-like id', () => {
  const staged1 = stageFixture('stage-staged.json');
  assert.equal(staged1.reason, `${STAGED_EXE} copied, read back equal and recorded in its stage.json`);
  assert.equal(staged1.source, BUNDLED_EXE_FILE);
  assert.equal(stageFixture('stage-reused.json').reason, `${STAGED_EXE} was already staged and verifies; nothing was written to it`);
  for (const name of ['stage-staged.json', 'stage-reused.json', 'stage-refused.json', 'stage-busy.json']) {
    assert.equal(stageFixture(name).source, BUNDLED_EXE_FILE, name);
  }
  assert.match(String(stageFixture('stage-busy.json').reason), /^another run holds the run lock C:\\Users\\user\\AppData\\Local\\wsl-care\\wsl-care\.lock; nothing was done — try again \(it is never waited on\)$/);
  assert.equal(stageFixture('stage-remove-busy.json').reason, stageFixture('stage-busy.json').reason);
});
