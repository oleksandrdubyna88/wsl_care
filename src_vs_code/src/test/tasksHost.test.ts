import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

import { WindowsCareClient } from '../client/WindowsCareClient';
import type { ProcessRequest, ProcessResult } from '../process/runner';
import { SchedulerHost } from '../scheduler/schedulerHost';
import { TasksHost } from '../windowsTasks/tasksHost';
import { NO_WINDOWS_TASKS } from '../windowsTasks/tasksView';
import { QUERY_SCRIPT } from '../windowsTime/guardScripts';
import { WindowsTimeGuardHost } from '../windowsTime/guardHost';
import { GUARD_NAME, guardSummary } from '../windowsTime/guardTask';
import { fixedEngine, PS51_READ } from './support/engines';
import { MapStore } from './support/memento';
import { schedulerStdout, type TaskFixture } from './support/schedulerAnswers';
import { SOURCE_ROOT } from './support/paths';
import { EXTENSION, exited, LOCAL_APP_DATA, renderAnswer, STAGED_DIGEST, STAGED_EXE } from './support/windowsTasks';

/**
 * The host of the Windows tasks' part (WR-E2): the panel's refresh is its ONE trigger; one refresh is one Task Scheduler read
 * (shared with the guard) and one probe of the staged copy — `tasks render` through the Windows client, typed. Every recorder
 * here is a runner that starts nothing.
 */

const ENV = { SystemRoot: 'C:\\Windows' } as const;
const PIN = '0.4.0';

const ENOENT = { kind: 'failedToStart', reason: `spawn ${STAGED_EXE} ENOENT` } as const satisfies ProcessResult;

type RenderAnswerer = (task: string, request: ProcessRequest) => ProcessResult;

const GOLDEN: RenderAnswerer = (task) => exited(0, renderAnswer(`${task} from the staged copy`));

interface World {
  readonly host: TasksHost;
  readonly scheduler: SchedulerHost;
  readonly renders: ProcessRequest[];
  readonly queries: ProcessRequest[];
}

interface WorldOptions {
  readonly renderOf?: RenderAnswerer;
  readonly tasks?: readonly TaskFixture[];
  readonly folder?: 'present' | 'absent';
  readonly platform?: string;
  readonly digestOf?: () => Promise<string>;
  readonly queryResult?: ProcessResult;
}

function world(options: WorldOptions = {}): World {
  const renders: ProcessRequest[] = [];
  const queries: ProcessRequest[] = [];
  const renderOf = options.renderOf ?? GOLDEN;
  const client = new WindowsCareClient({
    runner: (request) => { renders.push(request); return Promise.resolve(renderOf(request.args[2] ?? '', request)); },
    platform: options.platform ?? 'win32',
    extension: EXTENSION,
    timeoutMs: () => 20_000,
    statusTimeoutMs: () => 20_000,
    localAppData: LOCAL_APP_DATA,
    digestOf: options.digestOf ?? (() => Promise.resolve(STAGED_DIGEST)),
  });
  const answer = options.queryResult ?? exited(0, schedulerStdout({ tasks: options.tasks ?? [], folder: options.folder ?? 'present' }));
  const scheduler = new SchedulerHost({ env: ENV, queryTimeoutMs: () => 30_000, script: QUERY_SCRIPT, query: (request) => { queries.push(request); return Promise.resolve(answer); } });

  return { host: new TasksHost({ scheduler, client, pin: PIN, formatInstant: (iso) => `<${iso}>` }), scheduler, renders, queries };
}

/** What the panel's refresh does for the Windows parts: ONE shared Task Scheduler read, and the tasks' probe of the staged copy (xtension.ts, `panelRefresh`). */
function panelRefresh(w: World): Promise<void> {
  return Promise.all([w.scheduler.refresh(), w.host.refresh()]).then(() => undefined);
}

function registered(task: 'watch' | 'archive'): readonly string[] {
  const answer = renderAnswer(`${task} from the staged copy`).summary as string[];

  return answer.map((line) => line.replaceAll('delay=PT60S', 'delay=PT1M'));
}

// ---- plan round 4: the trigger ----

test('plan round 4: nothing is asked at construction, and nothing but a refresh asks anything — no timer of its own, in the host or in the shared read', async () => {
  const w = world();
  await new Promise((resolve) => { setTimeout(resolve, 50); });
  assert.deepEqual([w.renders.length, w.queries.length], [0, 0]);
  assert.equal(w.host.view().copy.line, 'Windows daemon copy: checking…');
  const scanned = ['windowsTasks', 'scheduler'].flatMap((dir) => fs.readdirSync(path.join(SOURCE_ROOT, dir)).filter((f) => f.endsWith('.ts')).map((f) => path.join(dir, f)));
  assert.ok(scanned.includes(path.join('windowsTasks', 'tasksHost.ts')) && scanned.includes(path.join('scheduler', 'schedulerHost.ts')), 'the scan reads the host and the shared read');
  for (const file of scanned) {
    assert.doesNotMatch(fs.readFileSync(path.join(SOURCE_ROOT, file), 'utf8').replace(/\/\*[\s\S]*?\*\//g, '').replace(/\/\/.*$/gm, ''), /setInterval|setTimeout|PeriodicTimer|\btimers\b/, `${file} owns no timer`);
  }
});

test('the coai code round, finding 0: the panel\'s refresh asks the shared read ONCE and the tasks\' probe ONCE — extension.ts has one call site of each, inside panelRefresh, and the guard does not ask beside them', () => {
  const text = fs.readFileSync(path.join(SOURCE_ROOT, 'extension.ts'), 'utf8');
  assert.equal(text.split('parts.scheduler.refresh()').length - 1, 1, 'one call site of the shared read');
  assert.equal(text.split('parts.tasks.refresh()').length - 1, 1, 'one call site of the probe');
  const refresh = /function panelRefresh\([\s\S]*?\n}\n/.exec(text)?.[0] ?? '';
  assert.ok(refresh.includes('parts.scheduler.refresh()') && refresh.includes('parts.tasks.refresh()'), 'both inside panelRefresh');
  assert.ok(!refresh.includes('parts.guard.refresh()'), 'the guard follows the shared read: asking too would be a second query');
});

function guardOn(scheduler: SchedulerHost): WindowsTimeGuardHost {
  return new WindowsTimeGuardHost({
    env: ENV,
    options: () => ({ setAutomaticStart: true, everyHours: 4, minMinutesBetweenStarts: 10, delaySeconds: 60, timeLimitMinutes: 5 }),
    opTimeoutMs: () => 180_000,
    ui: { show: () => Promise.resolve(), confirm: () => Promise.resolve(false), run: () => Promise.resolve(exited(0)), report: () => undefined },
    scheduler,
    durable: new MapStore(),
    nowUtcMs: () => 1_000,
    formatInstant: (iso) => iso,
    engine: fixedEngine(PS51_READ),
  });
}

test('the coai code round, finding 0: ONE panel refresh is exactly one query request — the guard host and the tasks host both follow the scheduler\'s change, neither starts a read of its own', async () => {
  const w = world({ tasks: [{ name: GUARD_NAME, summary: guardSummary({ setAutomaticStart: true, everyHours: 4, minMinutesBetweenStarts: 10, delaySeconds: 60, timeLimitMinutes: 5 }) }, { name: 'watch', summary: registered('watch') }] });
  const guard = guardOn(w.scheduler);
  assert.match(guard.view().line, /checking…/);
  await panelRefresh(w);
  assert.equal(w.queries.length, 1, 'one request for the one refresh');
  assert.match(guard.view().line, /^Windows Time guard: installed/, 'the guard\'s line follows the shared read');
  assert.match(w.host.view().tasks[0]?.line ?? '', /^Windows watch task: installed/, 'and so do the tasks\' lines');
  await w.host.refresh();
  assert.equal(w.queries.length, 1, 'the tasks host\'s own refresh probes the copy and never reads Task Scheduler');
});

test('the coai code round, findings 6 + 15: a burst of panel refreshes hashes the bundled exe ONCE, not once per probe', async () => {
  let hashed = 0;
  const w = world({ digestOf: () => { hashed += 1; return Promise.resolve(STAGED_DIGEST); } });
  await Promise.all([panelRefresh(w), panelRefresh(w), panelRefresh(w)]);
  assert.equal(w.renders.length, 4, 'two probes of two renders: the burst is the flight and one more');
  assert.equal(hashed, 1, 'one read of the 13.7 MB exe for every probe');
});
test('one refresh is ONE Task Scheduler read and one probe of the copy: two renders on a staged copy, answered and typed', async () => {
  const w = world({ tasks: [{ name: 'watch', summary: registered('watch'), lastResult: '0' }] });
  await panelRefresh(w);
  assert.equal(w.queries.length, 1);
  assert.deepEqual(w.renders.map((r) => r.args.slice(0, 3)), [['tasks', 'render', 'watch'], ['tasks', 'render', 'archive']]);
  assert.ok(w.renders.every((r) => r.file === STAGED_EXE), 'asked of the staged copy and of nothing else');
  const view = w.host.view();
  assert.equal(view.copy.line, 'Windows daemon copy: 0.4.0 (0123456789ab) staged');
  assert.match(view.tasks[0]?.line ?? '', /^Windows watch task: installed, last run <2026-10-16T08:00:00\.0000000Z>: succeeded$/);
  assert.equal(view.tasks[1]?.line, 'Windows archive task: not installed');
  assert.match(view.dry, /^Windows tasks, dry run: the setting dryRun is on/);
});

// ---- plan round 2: not staged ----

test('plan round 2: a copy that is not there ("not found") is "not staged" — and the second task is NOT rendered', async () => {
  const w = world({ renderOf: () => ENOENT, tasks: [{ name: 'watch', summary: registered('watch') }] });
  await panelRefresh(w);
  assert.equal(w.renders.length, 1, 'one probe, no further render once nothing is staged');
  assert.equal(w.host.view().copy.line, 'Windows daemon copy: 0.4.0 (0123456789ab) not staged');
  assert.equal(w.host.view().tasks[0]?.line, 'Windows watch task: installed — this version\'s copy is not staged, install again to update');
  assert.equal(w.host.view().tasks[1]?.line, 'Windows archive task: not installed');
});

test('a copy that is there but would not start (an Application Control block, say) is NOT "not staged": the start\'s own words are the line', async () => {
  const w = world({ renderOf: () => ({ kind: 'failedToStart', reason: 'spawn UNKNOWN: An Application Control policy has blocked this file.' }) });
  await panelRefresh(w);
  assert.equal(w.renders.length, 1);
  assert.match(w.host.view().copy.line, /^Windows daemon copy: 0\.4\.0 \(0123456789ab\) could not be checked — it could not start: spawn UNKNOWN: An Application Control policy has blocked this file\.$/);
  assert.equal(w.host.view().copy.level, 'warn');
});

test('a render that timed out names the setting; one the daemon refused (an older daemon, exit 2) is the daemon\'s sentence', async () => {
  const timedOut = world({ renderOf: () => ({ kind: 'timedOut', timeoutMs: 60_000, treeEnded: false, stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) }) });
  await panelRefresh(timedOut);
  assert.match(timedOut.host.view().copy.line, /could not be checked — it did not answer within 60 s/);
  const old = world({ renderOf: () => exited(2, '', 'unknown command: tasks') });
  await panelRefresh(old);
  assert.match(old.host.view().copy.line, /could not be checked — unknown command: tasks$/);
});

test('one task whose render did not answer is that task\'s "cannot be compared" — the other task\'s render still counts', async () => {
  const w = world({
    renderOf: (task) => (task === 'watch' ? exited(0, renderAnswer('watch from the staged copy')) : { kind: 'timedOut', timeoutMs: 60_000, treeEnded: false, stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) }),
    tasks: [{ name: 'watch', summary: registered('watch') }, { name: 'archive', summary: registered('archive') }],
  });
  await panelRefresh(w);
  assert.match(w.host.view().tasks[0]?.line ?? '', /installed, last run/);
  assert.match(w.host.view().tasks[1]?.line ?? '', /installed — it cannot be compared with this version's copy \(it did not answer within 60 s/);
});

test('a staged copy the daemon will not let a task name is "staged — not registrable", with the daemon\'s reason', async () => {
  const w = world({ renderOf: (task) => exited(0, renderAnswer(task === 'watch' ? 'watch from the extension folder' : 'archive from the staged copy')) });
  await panelRefresh(w);
  assert.match(w.host.view().copy.line, /staged — not registrable: .* is not a staged copy/);
});

// ---- plan round 5: the copy line's sources ----

test('plan round 5: the digest is the client\'s own hash of the bundled exe (first 12 hex), the version the extension\'s pin, "staged" a render that answered', async () => {
  const other = 'fedcba9876543210'.repeat(4);
  const w = world({ digestOf: () => Promise.resolve(other), renderOf: () => ENOENT });
  await panelRefresh(w);
  assert.equal(w.host.view().copy.line, 'Windows daemon copy: 0.4.0 (fedcba987654) not staged');
  assert.ok(w.renders[0]?.file.includes(other), 'the staged exe asked is the one folder-named by that digest');
});

test('a bundled exe that cannot be read: no staged copy can be named, nothing is rendered, and the line says why', async () => {
  const w = world({ digestOf: () => Promise.reject(new Error('EBUSY: resource busy')) });
  await panelRefresh(w);
  assert.equal(w.renders.length, 0);
  assert.equal(w.host.view().copy.line, 'Windows daemon copy: not known — the bundled exe could not be read to learn its digest: EBUSY: resource busy');
});

test('off Windows nothing is started and the part says nothing at all', async () => {
  const w = world({ platform: 'linux' });
  await panelRefresh(w);
  assert.equal(w.renders.length, 0);
  assert.deepEqual(w.host.view(), NO_WINDOWS_TASKS);
});

// ---- the one read, shared ----

test('the Task Scheduler read is the shared one: the guard\'s task is in the same answer, and a second consumer costs no second query', async () => {
  const w = world({ tasks: [{ name: GUARD_NAME, summary: guardSummary({ setAutomaticStart: true, everyHours: 4, minMinutesBetweenStarts: 10, delaySeconds: 60, timeLimitMinutes: 5 }) }, { name: 'watch', summary: registered('watch') }] });
  let heard = 0;
  w.scheduler.onChange(() => { heard += 1; });
  await panelRefresh(w);
  assert.equal(w.queries.length, 1);
  assert.ok(heard >= 2);
  assert.match(w.host.view().tasks[0]?.line ?? '', /^Windows watch task: installed/);
});

test('three refreshes at once are the flight and ONE more — never three reads, never three probes', async () => {
  const w = world();
  await Promise.all([panelRefresh(w), panelRefresh(w), panelRefresh(w)]);
  assert.equal(w.queries.length, 2);
  assert.equal(w.renders.length, 4, 'two probes of two renders');
});

test('plan round 6, 8: a read that timed out — the panel says so on each task, and the copy line is still its own', async () => {
  const w = world({ queryResult: { kind: 'timedOut', timeoutMs: 30_000, stdout: Buffer.alloc(0), stderr: Buffer.alloc(0), treeEnded: true } });
  await panelRefresh(w);
  assert.match(w.host.view().tasks[0]?.line ?? '', /^Windows watch task: unknown — the Task Scheduler read timed out after 30 s \(wslCare\.timeouts\.windowsTimeGuardQuerySeconds\); its PowerShell and everything it started were ended$/);
  assert.equal(w.host.view().copy.line, 'Windows daemon copy: 0.4.0 (0123456789ab) staged');
});

test('the Opus own review M3, end to end: a timeout whose tree the runner could not verify ended says it could not confirm — the claim follows the runner\'s word', async () => {
  const w = world({ queryResult: { kind: 'timedOut', timeoutMs: 30_000, stdout: Buffer.alloc(0), stderr: Buffer.alloc(0), treeEnded: false } });
  await panelRefresh(w);
  assert.match(w.host.view().tasks[0]?.line ?? '', /; the extension could not confirm that its PowerShell and everything it started were ended$/);
});

test('a client that throws is that probe\'s unreadable copy — the refresh resolves, the panel is told, nothing is thrown into VS Code', async () => {
  const w = world({ renderOf: () => { throw new Error('boom'); } });
  await panelRefresh(w);
  assert.match(w.host.view().copy.line, /could not be checked — .*boom/);
});

test('every listener is told when the probe starts and when it ends; one that throws neither stops the others nor rejects the refresh', async () => {
  const w = world();
  const heard: string[] = [];
  w.host.onChange(() => { throw new Error('a listener\'s own fault'); });
  w.host.onChange(() => { heard.push(w.host.view().copy.line); });
  await panelRefresh(w);
  assert.ok(heard.length >= 2);
  assert.equal(heard.at(-1), 'Windows daemon copy: 0.4.0 (0123456789ab) staged');
});
