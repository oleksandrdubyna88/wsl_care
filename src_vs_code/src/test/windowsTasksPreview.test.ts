import assert from 'node:assert/strict';
import { test } from 'node:test';

import { TASKS_PREVIEW_TAIL, TASKS_STAGE_TAIL } from '../client/verbs';
import { previewResultOf } from '../client/windowsTasks';
import type { ProcessResult } from '../process/runner';
import { runTasksOp, type TasksFlowDeps, type TasksOutcome } from '../windowsTasks/tasksFlow';
import { PENDING_KEY, readPending } from '../windowsTasks/tasksPending';
import { previewText } from '../windowsTasks/previewText';
import { treeVerdict } from '../windowsTasks/tasksView';
import { tasksWorld, type World, type WorldOptions } from './support/tasksWorld';
import { exited, STAGED_EXE, stageFixture } from './support/windowsTasks';

/**
 * *Preview the Windows TEMP cleanup* (WR-T3 of PLAN_windows_runner.md §3.12; the story's plan round 5ee25a08, R0: the preview needs a VERIFIED staged
 * copy, so it stages ON DEMAND through the install's own stage step): the REAL flow over the recorder — a stage, then `tasks preview cleanup --json` on
 * THAT staged exe, the report rendered by the extension and opened read-only. No render, no guard, no engine, no modal, no PowerShell, no pending record.
 */

const NOW = 1_000_000;
const STAGE = TASKS_STAGE_TAIL.join(' ');
const PREVIEW = TASKS_PREVIEW_TAIL.join(' ');

async function previewed(options: WorldOptions = {}): Promise<{ readonly w: World; readonly outcome: TasksOutcome }> {
  const w = tasksWorld(options);

  return { w, outcome: await runTasksOp({ kind: 'preview' }, w.deps) };
}

function reportAt(w: World, at = 0): { readonly message: string; readonly failed: boolean } {
  return w.recorder.reports[at] ?? { message: '', failed: false };
}

const SHOWN = (outcome: TasksOutcome): string => JSON.stringify(outcome);

function previewAnswerText(name = 'preview-cleanup.json'): string {
  const result = previewResultOf(exited(0, stageFixture(name)));
  assert.equal(result.kind, 'answered');

  return result.kind === 'answered' ? previewText(result.report).text : '';
}

test('a preview stages first, then previews on the staged copy, opens the text read-only and reports the headline', async () => {
  const { w, outcome } = await previewed();
  assert.equal(outcome.kind, 'previewed', SHOWN(outcome));
  assert.deepEqual(w.daemon(), [STAGE, PREVIEW], `expected [stage, preview], got [${w.daemon().join(', ')}]`);
  const request = w.calls.filter((c) => c.who === 'daemon').at(1)?.request;
  assert.equal(request?.file, STAGED_EXE, 'the preview ran on the staged copy');
  assert.deepEqual(request?.args, [...TASKS_PREVIEW_TAIL]);
  assert.equal(request?.killTree, true);
  assert.deepEqual(w.events, ['stage', 'preview', 'showPreview', 'report']);
  assert.deepEqual(w.recorder.previews, [previewAnswerText()], 'the tab holds the extension\'s own rendering of the report');
  assert.equal(reportAt(w).failed, false);
  assert.match(reportAt(w).message, /logical bytes/);
  assert.deepEqual(w.recorder.prompts, [], 'a read-only preview asks nothing');
  assert.deepEqual(w.ps(), [], 'and starts no PowerShell');
  assert.equal(w.store.writes, 0, 'and writes no record');
  assert.deepEqual(w.recorder.requests, []);
});

test('no argv of a preview is anything but the two closed tails — never act, --confirm or --timer', async () => {
  const { w } = await previewed();
  const argvs = w.calls.map((c) => c.request.args.join(' '));
  assert.deepEqual(argvs, [STAGE, PREVIEW]);
  for (const argv of argvs) {
    assert.doesNotMatch(argv, /(^|\s)act(\s|$)|--confirm|--timer|config/, `an argv the extension may not send: ${argv}`);
  }
});

test('a reused copy previews too', async () => {
  const { w, outcome } = await previewed({ stage: [['stage-reused.json', 0]] });
  assert.equal(outcome.kind, 'previewed', SHOWN(outcome));
  assert.deepEqual(w.daemon(), [STAGE, PREVIEW]);
});

test('a stage that is busy twice, refused, or whose copy is not the bundled exe previews nothing', async () => {
  const busy = await previewed({ stage: [['stage-busy.json', 75], ['stage-busy.json', 75]], busyRetryMs: 4_000 });
  assert.equal(busy.outcome.kind, 'notStarted', SHOWN(busy.outcome));
  assert.match(reportAt(busy.w).message, /run lock was held twice/);
  assert.deepEqual(busy.w.sleeps, [4_000], 'ONE retry after the setting\'s wait');
  assert.deepEqual(busy.w.daemon(), [STAGE, STAGE], 'a preview call was made after a refused stage');
  const refused = await previewed({ stage: [['stage-refused.json', 1]] });
  assert.equal(refused.outcome.kind, 'notStarted');
  assert.deepEqual(refused.w.daemon(), [STAGE]);
  const other = await previewed({ client: { digest: 'fedcba9876543210'.repeat(4) } });
  assert.equal(other.outcome.kind, 'notStarted', SHOWN(other.outcome));
  assert.match(reportAt(other.w).message, /bundled exe this extension carries/);
  assert.deepEqual(other.w.daemon(), [STAGE], 'a preview call was made after a refused stage');
  for (const { w } of [busy, refused, other]) {
    assert.equal(reportAt(w).failed, true);
    assert.deepEqual(w.recorder.previews, []);
  }
});

test('a preview while any record stands is busy: no stage, no preview, nothing written', async () => {
  const w = tasksWorld();
  await w.store.update(PENDING_KEY, { op: 'run', task: 'watch', startedAtUtcMs: NOW - 10, deadlineUtcMs: NOW + 120_000 });
  const writes = w.store.writes;
  const outcome = await runTasksOp({ kind: 'preview' }, w.deps);
  assert.equal(outcome.kind, 'busy', SHOWN(outcome));
  assert.deepEqual(w.daemon(), [], 'a stage was made while a record stands');
  assert.equal(w.recorder.reports.length, 1, 'told once');
  assert.match(reportAt(w).message, /still waiting/, 'busy is information for every op (E3\'s own rule), and says what holds the buttons');
  assert.equal(w.store.writes, writes);
});

test('a preview writes no record of its own: the store is empty while the tab opens and after it ended', async () => {
  const w = tasksWorld();
  let during: unknown = 'not asked';
  const deps: TasksFlowDeps = { ...w.deps, ui: { ...w.deps.ui, showPreview: async (text) => { during = readPending(w.store, NOW); await w.deps.ui.showPreview(text); } } };
  const outcome = await runTasksOp({ kind: 'preview' }, deps);
  assert.equal(outcome.kind, 'previewed');
  assert.equal(during, undefined, 'store.get(PENDING_KEY) !== undefined while the preview was open');
  assert.equal(w.store.get(PENDING_KEY), undefined);
  assert.equal(w.store.writes, 0);
});

test('exit 81 is told as elevated with the daemon\'s words and the UAC sentence', async () => {
  const sentence = 'tasks preview cleanup: an elevated (administrator) process is refused (token): it sees entries owned by the Administrators group. Nothing was read.';
  const { w, outcome } = await previewed({ preview: [['', 81, `${sentence}\n`]] });
  assert.equal(outcome.kind, 'elevated', SHOWN(outcome));
  assert.equal(reportAt(w).failed, true);
  assert.match(reportAt(w).message, /UAC/);
  assert.match(reportAt(w).message, /an elevated \(administrator\) process is refused/);
  assert.match(reportAt(w).message, /EnableLUA=0/);
  assert.deepEqual(w.recorder.previews, [], 'a refused preview shows nothing');
  assert.deepEqual(w.events, ['stage', 'preview', 'report']);
});

test('a timeout names its setting, says what happened to the tree, and shows NO result — not even the report the daemon had already printed', async () => {
  for (const treeEnded of [true, false]) {
    const ended: ProcessResult = { kind: 'timedOut', timeoutMs: 180_000, treeEnded, stdout: Buffer.from(JSON.stringify(stageFixture('preview-cleanup.json'))), stderr: Buffer.alloc(0) };
    const { w, outcome } = await previewed({ ending: (argv) => (argv === PREVIEW ? ended : undefined) });
    assert.equal(outcome.kind, 'timedOut', SHOWN(outcome));
    assert.equal(outcome.kind === 'timedOut' ? outcome.setting : '', 'wslCare.timeouts.windowsTempPreviewSeconds');
    assert.equal(reportAt(w).failed, true);
    assert.match(reportAt(w).message, /did not finish within 180 s \(wslCare\.timeouts\.windowsTempPreviewSeconds\)/);
    assert.ok(reportAt(w).message.includes(treeVerdict(treeEnded, 'the daemon')), reportAt(w).message);
    assert.doesNotMatch(reportAt(w).message, /PowerShell/, 'the preview starts none');
    assert.deepEqual(w.recorder.previews, [], 'a result was shown after a timeout');
    assert.equal(w.store.writes, 0, 'no record: there is nothing to recover after a reload');
  }
});

test('an ENOENT start, the wrong side, a body that does not type and an unknown exit are told, never rendered', async () => {
  const enoent: ProcessResult = { kind: 'failedToStart', reason: 'spawn C:\\Users\\user\\AppData\\Local\\wsl-care\\bin\\x\\wsl-care.exe ENOENT' };
  const rows: readonly (readonly [string, WorldOptions, string, RegExp])[] = [
    ['ENOENT', { ending: (argv) => (argv === PREVIEW ? enoent : undefined) }, 'notStarted', /staged copy was not there when the preview started; press Preview again/],
    ['wrong side', { preview: [['', 2, '"wsl-care tasks" is the Windows binary\'s: nothing was done\n']] }, 'failed', /is the Windows binary's/],
    ['a body that does not type', { preview: [['stage-staged.json', 0]] }, 'failed', /answer carries no/],
    ['an unknown exit', { preview: [['', 4, 'the records are unreadable\n']] }, 'failed', /exited 4/],
  ];
  for (const [what, options, kind, wanted] of rows) {
    const { w, outcome } = await previewed(options);
    assert.equal(outcome.kind, kind, `${what}: ${SHOWN(outcome)}`);
    assert.equal(w.recorder.reports.length, 1, `${what}: not told exactly once`);
    assert.equal(reportAt(w).failed, true, what);
    assert.match(reportAt(w).message, wanted, what);
    assert.deepEqual(w.recorder.previews, [], what);
  }
});

test('a client that throws is a failed outcome, told once', async () => {
  const w = tasksWorld();
  const client = { tasksStage: () => w.client.tasksStage(), tasksRender: w.client.tasksRender.bind(w.client), tasksStageRemove: () => w.client.tasksStageRemove(), exe: w.client.exe.bind(w.client), tasksPreview: () => Promise.reject(new Error('boom')) };
  const outcome = await runTasksOp({ kind: 'preview' }, { ...w.deps, client });
  assert.equal(outcome.kind, 'failed', SHOWN(outcome));
  assert.match(reportAt(w).message, /the run threw: boom/);
  assert.equal(w.recorder.reports.length, 1);
});

test('a preview never calls afterRun and never reads Task Scheduler again', async () => {
  const { w } = await previewed();
  assert.ok(!w.events.includes('query'), `events: ${w.events.join(' → ')}`);
  assert.ok(!w.events.includes('engine') && !w.events.includes('reread') && !w.events.includes('confirm'), `events: ${w.events.join(' → ')}`);
});
