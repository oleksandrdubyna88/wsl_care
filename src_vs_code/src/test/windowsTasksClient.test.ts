import assert from 'node:assert/strict';
import { test } from 'node:test';

import { BUNDLED_DAEMON } from '../client/handshake';
import { TASKS_PREVIEW_TAIL } from '../client/verbs';
import { staged, WindowsCareClient } from '../client/WindowsCareClient';
import type { ProcessResult } from '../process/runner';
import type { Body } from './support/body';
import { BUNDLED_EXE_FILE, EXTENSION, exited, LOCAL_APP_DATA, recording, renderAnswer, STAGED_DIGEST, STAGED_EXE, stageFixture, windowsClient } from './support/windowsTasks';

/**
 * WR-E1 of PLAN_windows_runner.md §3.8: `WindowsCareClient` widened. The staged exe is named by a digest that is checked first; the
 * `tasks` verbs come back TYPED from the exit code AND the JSON, never as one generic error; a stage answer is judged against the
 * digest the extension computed and the version it carries; the run reads share their checks with the WSL client. Nothing here
 * starts a process: the runner is a recorder.
 */

const client = windowsClient;

const NO_PROCESS = recording(exited(0, 'must not be reached'));

// ---- finding 1: the staged digest is 64 lower-case hex digits, or nothing is built ----

test('staged(<digest>) names <LocalAppData>\\wsl-care\\bin\\<digest>\\wsl-care.exe, by absolute path', () => {
  assert.equal(client(NO_PROCESS.runner).exe(staged(STAGED_DIGEST)), STAGED_EXE);
  assert.equal(client(NO_PROCESS.runner).exe({ kind: 'bundled' }), BUNDLED_EXE_FILE);
  assert.equal(client(NO_PROCESS.runner).exe(), BUNDLED_EXE_FILE, 'the default is still the bundled exe');
});

const BAD_DIGESTS: readonly (readonly [string, string])[] = [
  ['a path above the staging folder', '../../other.exe'],
  ['a path above it, backslashes', '..\\..\\other'],
  ['a drive path', 'C:\\Windows\\System32'],
  ['upper case', STAGED_DIGEST.toUpperCase()],
  ['one upper-case digit', `A${STAGED_DIGEST.slice(1)}`],
  ['63 digits', STAGED_DIGEST.slice(1)],
  ['65 digits', `${STAGED_DIGEST}0`],
  ['a trailing newline', `${STAGED_DIGEST}\n`],
  ['a leading space', ` ${STAGED_DIGEST.slice(1)}`],
  ['64 characters that are not hex', 'g'.repeat(64)],
  ['empty', ''],
];

test('a staged digest that is not 64 lower-case hex digits throws before any path is built or any process started', async () => {
  for (const [what, digest] of BAD_DIGESTS) {
    const r = recording(exited(0, ''));
    const c = client(r.runner);
    assert.throws(() => c.exe(staged(digest)), RangeError, `exe: ${what}`);
    await assert.rejects(() => c.tasksRender('watch', digest), RangeError, `tasksRender: ${what}`);
    await assert.rejects(() => c.read({ read: 'archiveStatus' }, staged(digest)), RangeError, `read: ${what}`);
    assert.deepEqual(r.requests, [], `nothing started for ${what}`);
  }
});

test('the digest is checked before the platform or the staging folder: a bad one throws even where no staged exe could be named', () => {
  const [, digest] = BAD_DIGESTS[0] ?? ['', ''];
  assert.throws(() => client(NO_PROCESS.runner, { platform: 'linux' }).exe(staged(digest)), RangeError);
  assert.throws(() => client(NO_PROCESS.runner, { localAppData: null }).exe(staged(digest)), RangeError);
});

test('no staged exe is named off Windows, without a staging folder, or for a folder that is not on a local disk — and nothing is started', async () => {
  for (const options of [{ platform: 'linux' }, { localAppData: null }, { localAppData: '\\\\server\\share' }, { localAppData: 'relative' }, { localAppData: '\\Users\\user\\AppData\\Local' }] as const) {
    const r = recording(exited(0, ''));
    const c = client(r.runner, options);
    const named = c.exe(staged(STAGED_DIGEST));
    assert.equal(typeof named === 'string', false, JSON.stringify(options));
    assert.equal((await c.tasksRender('watch', STAGED_DIGEST)).kind, 'unavailable', JSON.stringify(options));
    assert.deepEqual(r.requests, []);
  }
});

test('a task outside the closed list is refused before argv is built (a cast cannot widen it)', async () => {
  await assert.rejects(() => client(NO_PROCESS.runner).tasksRender('run' as 'watch', STAGED_DIGEST), RangeError);
  await assert.rejects(() => client(NO_PROCESS.runner).tasksRender('watch --timer' as 'watch', STAGED_DIGEST), RangeError);
  assert.deepEqual(NO_PROCESS.requests, []);
});

// ---- finding 4: tasks render, typed ----

test('tasks render <task> runs on the STAGED exe with the render tail and the tasks ceiling, and reads a registrable answer', async () => {
  const answer = renderAnswer('watch from the staged copy');
  const r = recording(exited(0, answer));
  const result = await client(r.runner).tasksRender('watch', STAGED_DIGEST);
  assert.deepEqual(r.requests, [{ file: STAGED_EXE, args: ['tasks', 'render', 'watch', '--json'], timeoutMs: 60_000 }]);
  assert.equal(result.kind, 'registrable');
  if (result.kind === 'registrable') {
    assert.equal(result.answer.task, 'watch');
    assert.equal(result.answer.digest, STAGED_DIGEST);
    assert.equal(result.answer.xml, answer.xml);
    assert.deepEqual(result.answer.summary, answer.summary);
    assert.equal(result.answer.dry.week, 'notStarted');
    assert.equal(result.answer.executionTimeLimitMinutes, 20);
  }
});

test('the tasks ceiling is the one the caller sets', async () => {
  const r = recording(exited(0, renderAnswer('archive from the staged copy')));
  const c = new WindowsCareClient({ runner: r.runner, platform: 'win32', extension: EXTENSION, timeoutMs: () => 20_000, statusTimeoutMs: () => 20_000, previewTimeoutMs: () => 180_000, localAppData: LOCAL_APP_DATA, tasksTimeoutMs: () => 90_000 });
  assert.equal((await c.tasksRender('archive', STAGED_DIGEST)).kind, 'registrable');
  assert.deepEqual(r.requests, [{ file: STAGED_EXE, args: ['tasks', 'render', 'archive', '--json'], timeoutMs: 90_000 }]);
});

test('a render that is not registrable (exit 0) is surfaced as notRegistrable, with the daemon\'s reason and the whole answer', async () => {
  const answer = renderAnswer('watch from the extension folder');
  const result = await client(recording(exited(0, answer)).runner).tasksRender('watch', STAGED_DIGEST);
  assert.equal(result.kind, 'notRegistrable');
  if (result.kind === 'notRegistrable') {
    assert.equal(result.reason, answer.reason);
    assert.equal(result.answer.registrable, false);
  }
});

test('a render that says registrable for ANOTHER digest than the copy asked is not registrable', async () => {
  const other = 'f'.repeat(64);
  const result = await client(recording(exited(0, { ...renderAnswer('watch from the staged copy'), digest: other })).runner).tasksRender('watch', STAGED_DIGEST);
  assert.equal(result.kind, 'notRegistrable');
  assert.match(JSON.stringify(result), new RegExp(`${other}`));
});

test('render exit 2 is the wrong side (the distro\'s binary) and exit 78 a configuration in error — each with the daemon\'s own sentence', async () => {
  const wrong = await client(recording(exited(2, '', '"wsl-care tasks" is the Windows binary\'s: nothing was done\n')).runner).tasksRender('watch', STAGED_DIGEST);
  assert.deepEqual(wrong, { kind: 'wrongSide', reason: '"wsl-care tasks" is the Windows binary\'s: nothing was done' });
  const bad = await client(recording(exited(78, '', 'tasks render watch: the configuration is in error (dryRun: not a boolean); no task is rendered from it\n')).runner).tasksRender('watch', STAGED_DIGEST);
  assert.deepEqual(bad, { kind: 'configurationInError', reason: 'tasks render watch: the configuration is in error (dryRun: not a boolean); no task is rendered from it' });
});

test('a render that cannot be read is a failure with its reason: bad JSON, the wrong task, a missing field, another exit, a timeout, a failed start', async () => {
  const answer = renderAnswer('watch from the staged copy');
  const { xml: _xml, ...withoutXml } = answer;
  const cases: readonly (readonly [string, ProcessResult, RegExp])[] = [
    ['not JSON', exited(0, 'tasks render watch: registrable'), /not JSON/],
    ['another task', exited(0, { ...answer, task: 'archive' }), /archive.*watch|watch.*archive/],
    ['no xml', exited(0, withoutXml), /xml/],
    ['registrable not a boolean', exited(0, { ...answer, registrable: 'yes' }), /registrable/],
    ['a newer schema', exited(0, { ...answer, schemaVersion: 99 }), /newer/],
    ['exit 1', exited(1, '', 'boom\nmore'), /exited 1: boom/],
    ['exit 70', exited(70, '', 'internal'), /exited 70/],
    ['timed out', { kind: 'timedOut', timeoutMs: 60_000, treeEnded: false, stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) }, /did not answer within 60 s/],
    ['could not start', { kind: 'failedToStart', reason: 'spawn UNKNOWN: An Application Control policy has blocked this file.' }, /Application Control policy/],
  ];
  for (const [what, result, reason] of cases) {
    const read = await client(recording(result).runner).tasksRender('watch', STAGED_DIGEST);
    assert.equal(read.kind, 'failed', what);
    assert.match(JSON.stringify(read), reason, what);
  }
});

// ---- finding 2: tasks stage, typed from the exit code AND the JSON ----

test('tasks stage runs on the BUNDLED exe with the stage tail; exit 0 + staged is staged, and exit 0 + reused is reused', async () => {
  const staged1 = recording(exited(0, stageFixture('stage-staged.json')));
  const result = await client(staged1.runner).tasksStage();
  assert.deepEqual(staged1.requests, [{ file: BUNDLED_EXE_FILE, args: ['tasks', 'stage', '--json'], timeoutMs: 60_000 }]);
  assert.equal(result.kind, 'staged');
  if (result.kind === 'staged') {
    assert.equal(result.report.digest, STAGED_DIGEST);
    assert.equal(result.report.version, BUNDLED_DAEMON);
    assert.equal(result.report.path, STAGED_EXE);
  }
  const reused = await client(recording(exited(0, stageFixture('stage-reused.json'))).runner).tasksStage();
  assert.equal(reused.kind, 'reused');
  assert.equal(reused.kind === 'reused' && reused.report.retained.length, 1, 'the retained older copy is carried');
});

test('exit 1 + refused is refused with the reason — never a staged copy, though the answer still names a digest (no stale read)', async () => {
  const fixture = stageFixture('stage-refused.json');
  assert.equal(fixture.digest, STAGED_DIGEST, 'the fixture really carries a digest');
  const result = await client(recording(exited(1, fixture)).runner).tasksStage();
  assert.equal(result.kind, 'refused');
  assert.equal(result.kind === 'refused' && result.reason, fixture.reason);
});

test('exit 75 is busy — with the daemon\'s reason when it printed one, and without a parseable answer too', async () => {
  const busy = await client(recording(exited(75, stageFixture('stage-busy.json'))).runner).tasksStage();
  assert.deepEqual(busy, { kind: 'busy', reason: stageFixture('stage-busy.json').reason });
  const bare = await client(recording(exited(75, '', 'busy')).runner).tasksStage();
  assert.equal(bare.kind, 'busy');
});

test('the exit code and the JSON must agree: staged with exit 1, refused or busy with exit 0, and an empty answer, are failures', async () => {
  const cases: readonly (readonly [string, ProcessResult, RegExp])[] = [
    ['exit 1 but the answer says staged', exited(1, stageFixture('stage-staged.json')), /exited 1 but.*staged/],
    ['exit 0 but the answer says refused', exited(0, stageFixture('stage-refused.json')), /exited 0 but.*refused/],
    ['exit 0 but the answer says busy', exited(0, stageFixture('stage-busy.json')), /exited 0 but.*busy/],
    ['exit 0 and nothing printed', exited(0, ''), /not JSON/],
    ['exit 0 and an outcome from a later daemon', exited(0, { ...stageFixture('stage-staged.json'), outcome: 'quarantined' }), /quarantined/],
    ['exit 70', exited(70, '', 'internal error'), /exited 70: internal error/],
  ];
  for (const [what, result, reason] of cases) {
    const read = await client(recording(result).runner).tasksStage();
    assert.equal(read.kind, 'failed', what);
    assert.match(JSON.stringify(read), reason, what);
  }
});

test('stage exit 2 is the wrong side; a timeout and a failed start are failures that say so', async () => {
  assert.equal((await client(recording(exited(2, '', 'not the Windows binary')).runner).tasksStage()).kind, 'wrongSide');
  assert.match(JSON.stringify(await client(recording({ kind: 'timedOut', timeoutMs: 60_000, treeEnded: false, stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) }).runner).tasksStage()), /did not answer within 60 s/);
  assert.match(JSON.stringify(await client(recording({ kind: 'failedToStart', reason: 'spawn EPERM' }).runner).tasksStage()), /spawn EPERM/);
});

test('off Windows, nothing is staged and nothing is started', async () => {
  const r = recording(exited(0, stageFixture('stage-staged.json')));
  assert.equal((await client(r.runner, { platform: 'linux' }).tasksStage()).kind, 'unavailable');
  assert.equal((await client(r.runner, { platform: 'linux' }).tasksStageRemove()).kind, 'unavailable');
  assert.deepEqual(r.requests, []);
});

// ---- finding 3: provenance ----

test('a stage answer for another digest than the one the extension computed is refused as provenanceRefused — for a new copy and a reused one', async () => {
  const other = 'e'.repeat(64);
  for (const fixture of ['stage-staged.json', 'stage-reused.json']) {
    const result = await client(recording(exited(0, stageFixture(fixture))).runner, { digest: other }).tasksStage();
    assert.equal(result.kind, 'provenanceRefused', fixture);
    assert.match(JSON.stringify(result), new RegExp(`${other}`), 'the refusal names the digest it expected');
    assert.match(JSON.stringify(result), new RegExp(STAGED_DIGEST), 'and the digest it got');
  }
});

test('a stage answer whose version is not BUNDLED_DAEMON is refused as provenanceRefused', async () => {
  for (const version of ['0.3.9', '9.9.9', '', `${BUNDLED_DAEMON}+0123456789abcdef`]) {
    const result = await client(recording(exited(0, { ...stageFixture('stage-staged.json'), version })).runner).tasksStage();
    assert.equal(result.kind, 'provenanceRefused', JSON.stringify(version));
    assert.match(JSON.stringify(result), new RegExp(BUNDLED_DAEMON.replace(/\./g, '\\.')), 'it names the version it expected');
  }
});

test('a stage answer whose digest is not even a digest (empty, upper case, short) is provenanceRefused, never compared loosely', async () => {
  for (const digest of ['', STAGED_DIGEST.toUpperCase(), STAGED_DIGEST.slice(1)]) {
    const result = await client(recording(exited(0, { ...stageFixture('stage-staged.json'), digest })).runner).tasksStage();
    assert.equal(result.kind, 'provenanceRefused', JSON.stringify(digest));
  }
});

test('the fixtures carry the version this extension pins — a bump of BUNDLED_DAEMON must regenerate them', () => {
  assert.equal(stageFixture('stage-staged.json').version, BUNDLED_DAEMON);
  assert.equal(renderAnswer('watch from the staged copy').version, BUNDLED_DAEMON);
});

// ---- tasks stage --remove ----

test('tasks stage --remove runs on the BUNDLED exe; removed carries what went and what was kept; busy and the wrong side are their own kinds', async () => {
  const r = recording(exited(0, stageFixture('stage-remove-removed.json')));
  const result = await client(r.runner).tasksStageRemove();
  assert.deepEqual(r.requests, [{ file: BUNDLED_EXE_FILE, args: ['tasks', 'stage', '--remove', '--json'], timeoutMs: 60_000 }]);
  assert.equal(result.kind, 'removed');
  if (result.kind === 'removed') {
    assert.equal(result.report.removed.length, 1);
    assert.equal(result.report.kept[0]?.why, 'its exe is still executing');
    assert.deepEqual(result.report.untouched, ['notes.txt']);
    assert.equal(result.report.rootRemoved, false);
  }
  assert.deepEqual(await client(recording(exited(75, stageFixture('stage-remove-busy.json'))).runner).tasksStageRemove(), { kind: 'busy', reason: stageFixture('stage-remove-busy.json').reason });
  assert.equal((await client(recording(exited(2, '', 'distro')).runner).tasksStageRemove()).kind, 'wrongSide');
});

test('remove: exit 0 with a busy answer, exit 75 with a removed one, and an unreadable answer are failures or busy — never a silent success', async () => {
  const lying = await client(recording(exited(0, stageFixture('stage-remove-busy.json'))).runner).tasksStageRemove();
  assert.equal(lying.kind, 'failed');
  assert.equal((await client(recording(exited(0, 'ok')).runner).tasksStageRemove()).kind, 'failed');
  assert.equal((await client(recording(exited(1, '', 'x')).runner).tasksStageRemove()).kind, 'failed');
});

// ---- the run reads of the Windows side ----

test('a run read runs on the bundled exe by default and on the staged one when asked, with the shared tail, and returns the body', async () => {
  const body: Body = { schemaVersion: 1, state: 'done' };
  const r = recording(exited(0, body));
  const c = client(r.runner);
  const first = await c.read({ read: 'runsShow', runId: '20261005T100000Z-77' });
  const second = await c.read({ read: 'archiveStatus' }, staged(STAGED_DIGEST));
  assert.deepEqual(first, { kind: 'read', read: 'runsShow', body });
  assert.equal(second.kind, 'read');
  assert.deepEqual(r.requests.map((q) => [q.file, ...q.args]), [
    [BUNDLED_EXE_FILE, 'runs', 'show', '20261005T100000Z-77', '--json'],
    [STAGED_EXE, 'archive', 'status', '--json'],
  ]);
});

test('a run read\'s values are checked before anything starts: a run id, instants, a window that ends before it starts', async () => {
  const r = recording(exited(0, { schemaVersion: 1 }));
  const c = client(r.runner);
  assert.equal((await c.read({ read: 'runsShow', runId: '../../x' })).kind, 'readRefused');
  assert.equal((await c.read({ read: 'runs', from: 'yesterday', to: '2026-10-05T10:00:00Z' })).kind, 'readRefused');
  assert.equal((await c.read({ read: 'logs', from: '2026-10-05T10:00:00Z', to: '2026-10-05T09:00:00Z' })).kind, 'readRefused');
  assert.deepEqual(r.requests, []);
});

test('a run read answers after exit 4 (history unreadable) and, for archive list, after exit 1 — and fails on any other exit', async () => {
  const body: Body = { schemaVersion: 1, note: 'partial' };
  assert.equal((await client(recording(exited(4, body)).runner).read({ read: 'archiveStatus' })).kind, 'read');
  assert.equal((await client(recording(exited(1, body)).runner).read({ read: 'archiveList' })).kind, 'read');
  assert.equal((await client(recording(exited(1, body)).runner).read({ read: 'archiveStatus' })).kind, 'failed');
  assert.equal((await client(recording(exited(0, 'nope')).runner).read({ read: 'archiveStatus' })).kind, 'failed');
  assert.equal((await client(recording(exited(0, { schemaVersion: 99 })).runner).read({ read: 'archiveStatus' })).kind, 'failed');
});

test('a run read uses the read ceiling the caller sets, else the default of that read', async () => {
  const r = recording(exited(0, { schemaVersion: 1 }));
  await client(r.runner).read({ read: 'archiveStatus' });
  const c = new WindowsCareClient({ runner: r.runner, platform: 'win32', extension: EXTENSION, timeoutMs: () => 20_000, statusTimeoutMs: () => 20_000, previewTimeoutMs: () => 180_000, readTimeoutMs: () => 12_345 });
  await c.read({ read: 'archiveStatus' });
  assert.equal(r.requests[1]?.timeoutMs, 12_345);
  assert.ok((r.requests[0]?.timeoutMs ?? 0) > 0);
});

test('WR-E3: a timeout of tasks stage --remove is TYPED (a caller that persists a pending run must be able to keep it); the other calls stay failed', async () => {
  const ended: ProcessResult = { kind: 'timedOut', timeoutMs: 60_000, treeEnded: false, stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) };
  assert.deepEqual(await client(recording(ended).runner).tasksStageRemove(), { kind: 'timedOut', timeoutMs: 60_000, treeEnded: false });
  assert.equal((await client(recording(ended).runner).tasksStage()).kind, 'failed');
});

// ---- WR-T3: tasks preview cleanup, typed ----

const PREVIEW_FIXTURE = 'preview-cleanup.json';

/** The preview fixture with `change` applied to a copy of its body. */
function previewBody(change: (body: Record<string, unknown>) => void = () => undefined): Body {
  const body = structuredClone(stageFixture(PREVIEW_FIXTURE)) as Record<string, unknown>;
  change(body);

  return body;
}

function previewClient(runner: ReturnType<typeof recording>['runner'], ceilings: () => number = () => 180_000): WindowsCareClient {
  return new WindowsCareClient({ runner, platform: 'win32', extension: EXTENSION, timeoutMs: () => 20_000, statusTimeoutMs: () => 20_000, previewTimeoutMs: ceilings, localAppData: LOCAL_APP_DATA });
}

test('tasks preview cleanup runs on the STAGED exe with the preview tail, the preview ceiling and the tree kill, and types the daemon\'s report', async () => {
  const r = recording(exited(0, previewBody()));
  const result = await previewClient(r.runner).tasksPreview(STAGED_DIGEST);
  assert.deepEqual(r.requests, [{ file: STAGED_EXE, args: TASKS_PREVIEW_TAIL, timeoutMs: 180_000, killTree: true }]);
  assert.deepEqual(TASKS_PREVIEW_TAIL, ['tasks', 'preview', 'cleanup', '--json']);
  assert.equal(result.kind, 'answered', JSON.stringify(result).slice(0, 300));
  if (result.kind === 'answered') {
    assert.equal(result.report.counts.eligible, 2);
    assert.equal(result.report.groups.length, 5);
    assert.equal(result.report.groups[0]?.verdict, 'eligible');
    assert.equal(result.report.dry.week, 'notStarted');
    assert.equal(result.report.dry.until, undefined, 'until is omitted by the daemon when null');
    assert.equal(result.report.wouldBeEligibleLimited, false);
    assert.deepEqual(result.report.allowGlobs.slice(0, 2), ['coai-*', 'wsl-care-test-*']);
  }
});

test('the preview ceiling is read at every call (two calls, two values)', async () => {
  let ceiling = 130_000;
  const r = recording(exited(0, previewBody()));
  const c = previewClient(r.runner, () => ceiling);
  await c.tasksPreview(STAGED_DIGEST);
  ceiling = 3_700_000;
  await c.tasksPreview(STAGED_DIGEST);
  assert.deepEqual(r.requests.map((q) => q.timeoutMs), [130_000, 3_700_000]);
});

test('a preview needs a staged digest of 64 lower-case hex digits, a Windows machine and a staging folder — else nothing starts', async () => {
  for (const [what, digest] of BAD_DIGESTS) {
    const r = recording(exited(0, previewBody()));
    await assert.rejects(() => previewClient(r.runner).tasksPreview(digest), RangeError, what);
    assert.deepEqual(r.requests, [], `nothing started for ${what}`);
  }
  for (const options of [{ platform: 'linux' }, { localAppData: null }] as const) {
    const r = recording(exited(0, previewBody()));
    const result = await windowsClient(r.runner, options).tasksPreview(STAGED_DIGEST);
    assert.equal(result.kind, 'unavailable', JSON.stringify(options));
    assert.deepEqual(r.requests, []);
  }
});

test('a dry.until the daemon prints is read; a preview body that lies about its shape is a failure naming the member', async () => {
  const withUntil = await previewClient(recording(exited(0, previewBody((b) => { (b.dry as Record<string, unknown>).until = '2026-10-08T21:00:00+00:00'; }))).runner).tasksPreview(STAGED_DIGEST);
  assert.equal(withUntil.kind === 'answered' ? withUntil.report.dry.until : 'not answered', '2026-10-08T21:00:00+00:00');
  const group = (b: Record<string, unknown>): Record<string, unknown> => (b.groups as Record<string, unknown>[])[0] as Record<string, unknown>;
  const cases: readonly (readonly [string, (b: Record<string, unknown>) => void, RegExp])[] = [
    ['a task that is not cleanup', (b) => { b.task = 'watch'; }, /task/],
    ['an action that is not A23', (b) => { b.action = 'A4'; }, /action/],
    ['a group verdict outside the two words', (b) => { group(b).verdict = 'deleted'; }, /verdict/],
    ['a negative count', (b) => { (b.counts as Record<string, unknown>).eligible = -1; }, /counts\.eligible/],
    ['a negative group count', (b) => { group(b).count = -3; }, /groups\[0\]\.count/],
    ['a path that is not text', (b) => { group(b).paths = [7]; }, /paths/],
    ['a missing wouldBeEligibleLimited', (b) => { delete b.wouldBeEligibleLimited; }, /wouldBeEligibleLimited/],
    ['a missing groupsNotShown member', (b) => { delete (b.groupsNotShown as Record<string, unknown>).wouldBeEligible; }, /groupsNotShown\.wouldBeEligible/],
    ['a root that is not text', (b) => { b.root = 4; }, /root/],
    ['an allow-list that is not a list of text', (b) => { b.allowGlobs = 'coai-*'; }, /allowGlobs/],
    ['a dry state with no week', (b) => { delete (b.dry as Record<string, unknown>).week; }, /dry\.week/],
    ['a pass with no passesCompleted', (b) => { delete (b.pass as Record<string, unknown>).passesCompleted; }, /passesCompleted/],
    ['an excluded reason with no count', (b) => { delete ((b.excludedByReason as Record<string, unknown>[])[0] as Record<string, unknown>).count; }, /excludedByReason\[0\]\.count/],
  ];
  for (const [what, change, wanted] of cases) {
    const result = await previewClient(recording(exited(0, previewBody(change))).runner).tasksPreview(STAGED_DIGEST);
    assert.equal(result.kind, 'failed', `${what}: ${JSON.stringify(result).slice(0, 200)}`);
    assert.match(result.kind === 'failed' ? result.reason : '', wanted, what);
  }
});

test('a schemaVersion this extension does not read, an empty body, and any other exit are failures; exit 2 is the wrong side; exit 81 is elevated with the daemon\'s words', async () => {
  const newer = await previewClient(recording(exited(0, previewBody((b) => { b.schemaVersion = 2; }))).runner).tasksPreview(STAGED_DIGEST);
  assert.equal(newer.kind, 'failed');
  assert.match(newer.kind === 'failed' ? newer.reason : '', /schemaVersion 2/);
  const empty = await previewClient(recording(exited(0, '')).runner).tasksPreview(STAGED_DIGEST);
  assert.equal(empty.kind, 'failed', 'exit 0 with an empty body');
  const four = await previewClient(recording(exited(4, '', 'tasks preview: the records are unreadable\n')).runner).tasksPreview(STAGED_DIGEST);
  assert.equal(four.kind, 'failed');
  assert.match(four.kind === 'failed' ? four.reason : '', /exited 4: tasks preview: the records are unreadable/);
  const wrong = await previewClient(recording(exited(2, '', '"wsl-care tasks" is the Windows binary\'s\n')).runner).tasksPreview(STAGED_DIGEST);
  assert.deepEqual(wrong, { kind: 'wrongSide', reason: '"wsl-care tasks" is the Windows binary\'s' });
  const sentence = 'tasks preview cleanup: an elevated (administrator) process is refused (token): it sees entries owned by the Administrators group. Nothing was read.';
  const elevated = await previewClient(recording(exited(81, '', `${sentence}\n`)).runner).tasksPreview(STAGED_DIGEST);
  assert.deepEqual(elevated, { kind: 'elevated', reason: sentence });
});

test('a preview that hits its ceiling is typed timedOut with the tree kill\'s verdict and shows NO partial result, even with a whole report on stdout', async () => {
  for (const treeEnded of [true, false]) {
    const ended: ProcessResult = { kind: 'timedOut', timeoutMs: 180_000, treeEnded, stdout: Buffer.from(JSON.stringify(previewBody())), stderr: Buffer.alloc(0) };
    const result = await previewClient(recording(ended).runner).tasksPreview(STAGED_DIGEST);
    assert.deepEqual(result, { kind: 'timedOut', timeoutMs: 180_000, treeEnded });
  }
});

test('a start that finds no file is notStaged; any other failed start and too much output are failures with their reason', async () => {
  const enoent: ProcessResult = { kind: 'failedToStart', reason: 'spawn C:\\Users\\user\\AppData\\Local\\wsl-care\\bin\\x\\wsl-care.exe ENOENT' };
  assert.deepEqual(await previewClient(recording(enoent).runner).tasksPreview(STAGED_DIGEST), { kind: 'notStaged' });
  const blocked: ProcessResult = { kind: 'failedToStart', reason: 'spawn EACCES' };
  const result = await previewClient(recording(blocked).runner).tasksPreview(STAGED_DIGEST);
  assert.equal(result.kind, 'failed');
  assert.match(result.kind === 'failed' ? result.reason : '', /EACCES/);
  const loud: ProcessResult = { kind: 'tooMuchOutput', stream: 'stdout', limitBytes: 16 };
  assert.equal((await previewClient(recording(loud).runner).tasksPreview(STAGED_DIGEST)).kind, 'failed');
});
