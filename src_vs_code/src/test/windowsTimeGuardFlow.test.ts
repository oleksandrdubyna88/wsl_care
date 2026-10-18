import assert from 'node:assert/strict';
import { test } from 'node:test';

import type { DurableStore } from '../cleanup/journal';
import type { ProcessRequest, ProcessResult } from '../process/runner';
import { parsePageMessage } from '../panel/messages';
import { buildPanelView } from '../panel/viewModel';
import type { Snapshot } from '../state/outcomeStore';
import { INSTALL_CONFIRM, REMOVE_CONFIRM, runGuardOp, type GuardFlowDeps } from '../windowsTime/guardFlow';
import { WindowsTimeGuardHost, type GuardHostDeps, type GuardUi } from '../windowsTime/guardHost';
import { PENDING_KEY, readPending } from '../windowsTime/guardPending';
import { commandLineLength, ELEVATED_COMMAND_LINE_MAX, installScript, OP_EXIT, QUERY_SCRIPT, REMOVE_SCRIPT } from '../windowsTime/guardScripts';
import { GUARD_NAME, guardScript, guardSummary, type GuardOptions } from '../windowsTime/guardTask';
import { newGuardRecorder, type GuardRecorder } from '../windowsTime/guardRecorder';
import { EXIT } from '../windowsTime/windowsTimeFix';
import { elevatedRequest } from '../powershell/elevatedLauncher';
import { SchedulerHost } from '../windowsTasks/schedulerHost';
import { elevatedProgramOf, failedRead, fixedEngine, PS51_READ, STORE_PWSH, STORE_READ } from './support/engines';
import { MapStore } from './support/memento';
import { schedulerStdout } from './support/schedulerAnswers';

/**
 * *Install / Remove the Windows Time guard* (PLAN_windows_time_task.md D4, D8): the exact elevated script is shown BEFORE
 * the modal, only the modal's confirm persists the pending run and starts the ONE elevated PowerShell, every exit is one
 * closed outcome, a timed-out run keeps holding the buttons, and the host re-reads Task Scheduler after every answer.
 * Every surface is a recorder — no test starts a PowerShell.
 */

const OPTIONS: GuardOptions = { setAutomaticStart: true, everyHours: 4, minMinutesBetweenStarts: 10, delaySeconds: 60, timeLimitMinutes: 5 };
const ENV = { SystemRoot: 'C:\\Windows' } as const;

function exited(code: number, stdout = ''): ProcessResult {
  return { kind: 'exited', code, stdout: Buffer.from(stdout), stderr: Buffer.alloc(0) };
}

interface Harness {
  readonly deps: GuardFlowDeps;
  readonly events: string[];
  readonly shown: string[];
  readonly prompts: string[];
  readonly requests: ProcessRequest[];
  readonly reports: { readonly message: string; readonly failed: boolean }[];
  readonly store: MapStore;
}

function harness(answer: boolean, result: ProcessResult, env: Readonly<Record<string, string>> = ENV, now = 1_000_000): Harness {
  const events: string[] = [];
  const shown: string[] = [];
  const prompts: string[] = [];
  const requests: ProcessRequest[] = [];
  const reports: { message: string; failed: boolean }[] = [];
  const store = new MapStore();
  return {
    events, shown, prompts, requests, reports, store,
    deps: {
      env,
      options: () => OPTIONS,
      timeoutMs: () => 180_000,
      show: (text) => { events.push('show'); shown.push(text); return Promise.resolve(); },
      confirm: (prompt) => { events.push('confirm'); prompts.push(`${prompt.message}\n${prompt.detail}\n${prompt.confirm}`); return Promise.resolve(answer); },
      run: (request) => { events.push(`run(pending=${readPending(store, now)?.op ?? 'none'})`); requests.push(request); return Promise.resolve(result); },
      report: (message, failed) => { events.push('report'); reports.push({ message, failed }); },
      durable: store,
      nowUtcMs: () => now,
      afterRun: () => { events.push('query'); },
      engine: fixedEngine(PS51_READ),
    },
  };
}

function decoded(request: ProcessRequest | undefined): string {
  const b64 = /-EncodedCommand ([A-Za-z0-9+/=]+)'/.exec(request?.args[3] ?? '')?.[1] ?? '';
  return Buffer.from(b64, 'base64').toString('utf16le');
}

test('install: the elevated script is SHOWN, then the modal; only its confirm persists the pending run and runs it; then the status is read again', async () => {
  const h = harness(true, exited(0));
  assert.deepEqual(await runGuardOp('install', h.deps), { kind: 'done' });
  assert.deepEqual(h.events, ['show', 'confirm', 'run(pending=install)', 'query', 'report']);
  assert.equal(h.shown[0], installScript(OPTIONS));
  assert.equal(decoded(h.requests[0]), h.shown[0], 'what was shown is byte for byte what runs elevated (own review o7)');
  assert.match(h.prompts[0] ?? '', new RegExp(`${INSTALL_CONFIRM.replace(/[()]/g, '\\$&')}$`));
  assert.ok((h.prompts[0] ?? '').includes(guardScript(OPTIONS)), 'the modal names the action verbatim');
  assert.match(h.prompts[0] ?? '', /at startup, at logon, when the Windows Time service logs that it is stopping, when its start type is changed \(each 60 s after the event\), and every 4 h/, 'the modal names all five triggers — the start-type change too');
  assert.match(h.prompts[0] ?? '', /sets the service to start Automatic when it does not already/, 'the start-type line is conditional, and the modal says so');
  assert.equal(h.store.get(PENDING_KEY), undefined, 'an answered run clears the pending record');
  assert.equal(h.reports[0]?.failed, false);
});

test('a declined modal runs nothing, persists nothing and says nothing', async () => {
  const h = harness(false, exited(0));
  assert.deepEqual(await runGuardOp('install', h.deps), { kind: 'declined' });
  assert.deepEqual(h.events, ['show', 'confirm']);
  assert.equal(h.store.writes, 0);
});

test('remove shows its script in the modal (no document) and runs exactly it', async () => {
  const h = harness(true, exited(0));
  assert.deepEqual(await runGuardOp('remove', h.deps), { kind: 'done' });
  assert.deepEqual(h.events, ['confirm', 'run(pending=remove)', 'query', 'report']);
  assert.ok((h.prompts[0] ?? '').includes(REMOVE_SCRIPT));
  assert.ok((h.prompts[0] ?? '').endsWith(REMOVE_CONFIRM));
  assert.match(h.prompts[0] ?? '', /start type is NOT changed back/);
  assert.equal(decoded(h.requests[0]), REMOVE_SCRIPT);
  assert.match(h.reports[0]?.message ?? '', /removed — its task, its folder and its stamp/);
});

test('every exit of the elevated run is one closed outcome with its own sentence', async () => {
  const cases: readonly (readonly [number, string, RegExp, boolean])[] = [
    [EXIT.declined, 'declined', /UAC prompt was declined/, false],
    [EXIT.launchFailed, 'failed', /elevated PowerShell could not be started/, true],
    [1, 'failed', /failed before its first step \(exit 1\)/, true],
    [OP_EXIT.registerFailed, 'failed', /did not register the task/, true],
    [OP_EXIT.unregisterFailed, 'failed', /did not delete the task/, true],
    [OP_EXIT.cleanupFailed, 'failed', /stamp under HKLM/, true],
    [4294967295, 'failed', /exited -1/, true],
  ];
  for (const [code, kind, sentence, failed] of cases) {
    const h = harness(true, exited(code));
    assert.equal((await runGuardOp('install', h.deps)).kind, kind, String(code));
    assert.match(h.reports[0]?.message ?? '', sentence, String(code));
    assert.equal(h.reports[0]?.failed, failed, String(code));
    assert.equal(h.store.get(PENDING_KEY), undefined, `${code}: an answer clears the pending run`);
  }
});

test('codex c4: a TIMED-OUT run keeps holding the buttons until its deadline — the elevated child may still run', async () => {
  const h = harness(true, { kind: 'timedOut', timeoutMs: 180_000, treeEnded: false, stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) }, ENV, 1_000_000);
  assert.equal((await runGuardOp('install', h.deps)).kind, 'timedOut');
  assert.deepEqual(readPending(h.store, 1_000_000), { op: 'install', startedAtUtcMs: 1_000_000, deadlineUtcMs: 1_360_000 });
  assert.match(h.reports[0]?.message ?? '', /answering it still runs it/);
  const second = harness(true, exited(0), ENV, 1_100_000);
  await second.store.update(PENDING_KEY, h.store.get(PENDING_KEY));
  assert.deepEqual(await runGuardOp('remove', second.deps), { kind: 'busy' });
  assert.deepEqual(second.events, ['report'], 'while it stands, nothing is shown, asked or run — in any window');
  const later = harness(true, exited(0), ENV, 1_360_000);
  await later.store.update(PENDING_KEY, h.store.get(PENDING_KEY));
  assert.deepEqual(await runGuardOp('remove', later.deps), { kind: 'done' }, 'past its deadline it holds nothing');
});

test('own code review k3: a run another window confirmed while this modal was open makes this one busy — nothing runs', async () => {
  const h = harness(true, exited(0));
  const deps: GuardFlowDeps = { ...h.deps, confirm: async (prompt) => { await h.store.update(PENDING_KEY, { op: 'remove', startedAtUtcMs: 999_000, deadlineUtcMs: 2_000_000 }); return h.deps.confirm(prompt); } };
  assert.deepEqual(await runGuardOp('install', deps), { kind: 'busy' });
  assert.deepEqual(h.requests, []);
  assert.deepEqual(readPending(h.store, 1_000_000)?.op, 'remove', 'the other window\'s record is left as it was');
});

test('no SystemRoot, or a launcher that cannot start: told, nothing shown', async () => {
  const none = harness(true, exited(0), {});
  assert.equal((await runGuardOp('install', none.deps)).kind, 'notStarted');
  assert.deepEqual(none.events, ['report']);
  const enoent = harness(true, { kind: 'failedToStart', reason: 'ENOENT' });
  assert.equal((await runGuardOp('install', enoent.deps)).kind, 'notStarted');
  assert.match(enoent.reports[0]?.message ?? '', /could not start: ENOENT/);
});

// ---- the host: one query in flight, one flow at a time, the view the panel posts ----

/** A guard host whose Task Scheduler read is the ONE shared read (WR-E2), asked through `ui.query`. */
function guardOf(ui: GuardUi, over: Partial<GuardHostDeps> = {}): WindowsTimeGuardHost {
  return new WindowsTimeGuardHost({
    env: ENV, options: () => OPTIONS, opTimeoutMs: () => 180_000, ui,
    scheduler: new SchedulerHost({ env: ENV, queryTimeoutMs: () => 30_000, query: ui.query }),
    durable: new MapStore(), nowUtcMs: () => 1_000, formatInstant: (iso) => iso, engine: fixedEngine(PS51_READ),
    ...over,
  });
}

/** A guard UI that shows and asks nothing and answers every query with `query`. */
function quietUi(query: GuardUi['query']): GuardUi {
  return { show: () => Promise.resolve(), confirm: () => Promise.resolve(false), run: () => Promise.resolve(exited(0)), query, report: () => undefined };
}

function host(recorder: GuardRecorder, store = new MapStore()): WindowsTimeGuardHost {
  return guardOf({ show: (t) => { recorder.shown.push(t); return Promise.resolve(); }, confirm: (p) => { recorder.prompts.push(p); return Promise.resolve(recorder.answer); }, run: (r) => { recorder.requests.push(r); return Promise.resolve(recorder.result); }, query: (r) => { recorder.queries.push(r); return Promise.resolve(recorder.queryResult); }, report: (message, failed) => { recorder.reports.push({ message, failed }); } }, { durable: store });
}

test('the host reads Task Scheduler with the fixed query, at most one more while one is in flight, and tells the panel', async () => {
  const recorder = newGuardRecorder();
  const guard = host(recorder);
  let changes = 0;
  guard.onChange(() => { changes += 1; });
  assert.equal(guard.view().line, 'Windows Time guard: checking…');
  assert.deepEqual(guard.view().buttons, []);
  await Promise.all([guard.refresh(), guard.refresh(), guard.refresh()]);
  assert.equal(recorder.queries.length, 2, 'three refreshes at once: the flight and ONE more, never three');
  assert.equal(recorder.queries[0]?.args[3], QUERY_SCRIPT);
  assert.ok(changes >= 2, 'the panel is told when the query starts and when it answers');
  assert.match(guard.view().line, /not installed/);
  assert.doesNotMatch(guard.view().line, /checking/);
});

test('the host runs one flow at a time, greys the buttons while the elevated run is pending, and re-reads afterwards', async () => {
  const recorder = newGuardRecorder();
  let release: (r: ProcessResult) => void = () => undefined;
  const viewsWhileRunning: string[] = [];
  const pendingRun = new Promise<ProcessResult>((resolve) => { release = resolve; });
  const store = new MapStore();
  const slow = guardOf({ show: () => Promise.resolve(), confirm: () => Promise.resolve(true), run: () => { viewsWhileRunning.push(slowView()); return pendingRun; }, query: (r) => { recorder.queries.push(r); return Promise.resolve(exited(0, presentStdout())); }, report: () => undefined }, { durable: store });
  const slowView = (): string => `${slow.view().line}|${slow.view().buttons.map((b) => `${b.id}:${b.enabled}`).join(',')}`;
  const first = slow.run('install');
  assert.equal(await slow.run('remove'), undefined, 'a second press while one runs starts nothing');
  await new Promise((r) => setImmediate(r));
  release(exited(0));
  assert.deepEqual(await first, { kind: 'done' });
  await new Promise((r) => setImmediate(r));
  assert.match(viewsWhileRunning[0] ?? '', /Waiting for the elevated PowerShell that installs it.*installWindowsTimeGuard:false,removeWindowsTimeGuard:false/);
  assert.equal(recorder.queries.length, 1, 'the status was read again after the run answered');
  assert.match(slow.view().line, /installed — not run yet/);
});

/** The guard installed and never run, as the ONE query prints it. */
function presentStdout(): string {
  return schedulerStdout({ tasks: [{ name: GUARD_NAME, lastRunUtc: 'never', lastResult: '267011', summary: guardSummary(OPTIONS) }] });
}

test('own code review k4: a refresh asked during a query in flight is answered by one MORE query, not by the stale one', async () => {
  const recorder = newGuardRecorder();
  const answers = [exited(0, schedulerStdout()), exited(0, presentStdout())];
  let release: () => void = () => undefined;
  const gate = new Promise<void>((resolve) => { release = resolve; });
  const guard = guardOf(quietUi(async (r) => { recorder.queries.push(r); const answer = answers.shift() ?? exited(1); if (recorder.queries.length === 1) { await gate; } return answer; }));
  const first = guard.refresh();
  assert.match(guard.view().line, /checking…/);
  const second = guard.refresh();
  release();
  await Promise.all([first, second]);
  assert.equal(recorder.queries.length, 2);
  assert.match(guard.view().line, /installed — not run yet$/, 'the newer answer is the one shown');
});

test('gemini: a pending run Task Scheduler already shows finished is cleared on the next read — a reload does not wait out the deadline', async () => {
  const store = new MapStore();
  await store.update(PENDING_KEY, { op: 'install', startedAtUtcMs: 500, deadlineUtcMs: 400_000 });
  const guard = guardOf(quietUi(() => Promise.resolve(exited(0, presentStdout()))), { durable: store });
  assert.match(guard.view().line, /Waiting for the elevated PowerShell/);
  await guard.refresh();
  assert.equal(store.get(PENDING_KEY), undefined);
  assert.match(guard.view().line, /installed — not run yet$/);
  const still = new MapStore();
  await still.update(PENDING_KEY, { op: 'remove', startedAtUtcMs: 500, deadlineUtcMs: 400_000 });
  const removing = guardOf(quietUi(() => Promise.resolve(exited(0, presentStdout()))), { durable: still });
  await removing.refresh();
  assert.notEqual(still.get(PENDING_KEY), undefined, 'a removal is not finished while the task is still there');
});

test('gemini: a changed guard setting re-derives the line at once — no query needed', () => {
  let everyHours = 4;
  let changes = 0;
  const guard = guardOf(quietUi(() => Promise.resolve(exited(0, presentStdout()))), { options: () => ({ ...OPTIONS, everyHours }) });
  guard.onChange(() => { changes += 1; });
  everyHours = 5;
  guard.settingsChanged();
  assert.equal(changes, 1);
});

test('the Opus own review m3: a pending-run settle that fails (the durable store refuses) when ANOTHER consumer\'s read answers is no unhandled rejection — and a listener that throws does not stop the guard hearing the read', async () => {
  const unhandled: unknown[] = [];
  const onUnhandled = (reason: unknown): void => { unhandled.push(reason); };
  process.on('unhandledRejection', onUnhandled);
  try {
    const store = new MapStore();
    await store.update(PENDING_KEY, { op: 'install', startedAtUtcMs: 500, deadlineUtcMs: 400_000 });
    const failing: DurableStore = { get: (key) => store.get(key), update: () => Promise.reject(new Error('the store refused')) };
    const query: GuardUi['query'] = () => Promise.resolve(exited(0, presentStdout()));
    const scheduler = new SchedulerHost({ env: ENV, queryTimeoutMs: () => 30_000, query });
    const guard = guardOf(quietUi(query), { durable: failing, scheduler });
    let heard = 0;
    guard.onChange(() => { throw new Error('a listener\'s own fault'); });
    guard.onChange(() => { heard += 1; });
    await scheduler.refresh();
    await new Promise((resolve) => { setTimeout(resolve, 50); });
    assert.deepEqual(unhandled, [], 'the failed settle was caught');
    assert.ok(heard >= 1, 'the second listener heard the read although the first threw');
  } finally {
    process.off('unhandledRejection', onUnhandled);
  }
});

// ---- the panel and the page's message set ----

test('the panel view carries the guard\'s line, and the page may ask for the two flows only as BARE messages', () => {
  const snapshot: Snapshot = { status: undefined, preview: undefined, doctor: undefined, checking: false };
  const view = buildPanelView(snapshot, undefined, { line: 'Windows Time guard: not installed', level: 'none', buttons: [{ id: 'installWindowsTimeGuard', label: 'Install the Windows Time guard', enabled: true }], engine: '' });
  assert.equal(view.windowsTimeGuard.line, 'Windows Time guard: not installed');
  assert.match(buildPanelView(snapshot).windowsTimeGuard.line, /checking…/);
  for (const type of ['installWindowsTimeGuard', 'removeWindowsTimeGuard']) {
    assert.deepEqual(parsePageMessage({ type }), { type });
    assert.equal(parsePageMessage({ type, xml: '<Task/>' }), undefined, 'no text from the page reaches the task');
    assert.equal(parsePageMessage({ type, options: { everyHours: 1 } }), undefined);
  }
});

// ---- PLAN_pwsh_engine_windows_time.md D3, D5, §11 R5, R7: the engine, read before the modal and AGAIN before the launch ----

function withEngine(h: Harness, source: GuardFlowDeps['engine']): GuardFlowDeps {
  return { ...h.deps, engine: source };
}

test('pwsh D3: install with a verified Store PowerShell 7 — engine, script, modal naming it, a NEW read, then the pending run elevating that path', async () => {
  const h = harness(true, exited(0));
  assert.deepEqual(await runGuardOp('install', withEngine(h, fixedEngine(STORE_READ, STORE_READ, h.events))), { kind: 'done' });
  assert.deepEqual(h.events, ['engine', 'show', 'confirm', 'reread', 'run(pending=install)', 'query', 'report']);
  assert.equal(elevatedProgramOf(h.requests[0]), STORE_PWSH);
  assert.ok((h.prompts[0] ?? '').includes(`The elevated program: PowerShell 7 (${STORE_PWSH})`));
  assert.match(h.prompts[0] ?? '', /The task runs as SYSTEM in Windows PowerShell 5\.1/, 'D7: the task itself stays on 5.1, and the modal says so');
  assert.match(h.reports[0]?.message ?? '', /Elevated program: PowerShell 7 \(.+\)\.$/);
});

test('pwsh D3: an engine that changed while the modal was open runs nothing and writes no pending record', async () => {
  const h = harness(true, exited(0));
  assert.equal((await runGuardOp('remove', withEngine(h, fixedEngine(STORE_READ, PS51_READ)))).kind, 'notRun');
  assert.deepEqual(h.requests, []);
  assert.equal(h.store.writes, 0);
  assert.match(h.reports[0]?.message ?? '', /^Nothing ran: the elevated program changed while the question was open/);
  assert.equal(h.reports[0]?.failed, true);
});

test('pwsh §11 R7: a re-read that did not answer starts nothing and says the read did not answer', async () => {
  const h = harness(true, exited(0));
  assert.equal((await runGuardOp('install', withEngine(h, fixedEngine(STORE_READ, failedRead('the read exited 1'))))).kind, 'notRun');
  assert.equal(h.store.writes, 0);
  assert.match(h.reports[0]?.message ?? '', /right before the launch did not answer \(the read exited 1\)/);
});

test('pwsh §11 R5: a run another window confirmed DURING the re-read still makes this one busy — the busy check comes after the read', async () => {
  const h = harness(true, exited(0));
  const source: GuardFlowDeps['engine'] = {
    engine: () => Promise.resolve(STORE_READ),
    reread: async () => { await h.store.update(PENDING_KEY, { op: 'remove', startedAtUtcMs: 999_000, deadlineUtcMs: 2_000_000 }); return STORE_READ; },
    last: () => STORE_READ,
  };
  assert.deepEqual(await runGuardOp('install', withEngine(h, source)), { kind: 'busy' });
  assert.deepEqual(h.requests, []);
  assert.equal(readPending(h.store, 1_000_000)?.op, 'remove');
});

test('pwsh D4, D5: exit 33 names the ScheduledTasks module; exit 13 on the Store engine adds the Store note', async () => {
  const imported = harness(true, exited(OP_EXIT.importFailed));
  await runGuardOp('install', withEngine(imported, fixedEngine(STORE_READ)));
  assert.match(imported.reports[0]?.message ?? '', /could not load its ScheduledTasks module/);
  const launch = harness(true, exited(EXIT.launchFailed));
  await runGuardOp('install', withEngine(launch, fixedEngine(STORE_READ)));
  assert.match(launch.reports[0]?.message ?? '', /the Store package may not be available to the account that elevates/);
});

test('pwsh §11 R9: the largest install, elevating the LONGEST engine path, stays under the elevated command-line bound', () => {
  const request = elevatedRequest(ENV, installScript({ setAutomaticStart: true, everyHours: 168, minMinutesBetweenStarts: 1440, delaySeconds: 3600, timeLimitMinutes: 60 }), 180_000, STORE_PWSH) as ProcessRequest;
  assert.ok(commandLineLength(request) < ELEVATED_COMMAND_LINE_MAX, `${commandLineLength(request)} characters`);
});

// ---- PLAN_pwsh_engine_windows_time.md D6: the Windows Time part names its engines ----

test('pwsh D6: the guard part names the elevated runs\' engine from the LAST engine read, and the task\'s SYSTEM 5.1 — "checking" before any read', () => {
  const named = new WindowsTimeGuardHost({ ...hostDepsWith(fixedEngine(STORE_READ)) }).view();
  assert.equal(named.engine, `Elevated runs here (Start Windows Time, install / remove the guard): PowerShell 7 (${STORE_PWSH}). The guard's task runs as SYSTEM in Windows PowerShell 5.1.`);
  const none: GuardFlowDeps['engine'] = { engine: () => Promise.resolve(PS51_READ), reread: () => Promise.resolve(PS51_READ), last: () => undefined };
  assert.match(new WindowsTimeGuardHost(hostDepsWith(none)).view().engine, /^Elevated runs here \(Start Windows Time, install \/ remove the guard\): checking which PowerShell… The guard's task runs as SYSTEM in Windows PowerShell 5\.1\.$/);
});

function hostDepsWith(engine: GuardFlowDeps['engine']): ConstructorParameters<typeof WindowsTimeGuardHost>[0] {
  const recorder = newGuardRecorder();
  const query: GuardUi['query'] = () => Promise.resolve(recorder.queryResult);
  return {
    env: ENV, options: () => OPTIONS, opTimeoutMs: () => 180_000,
    ui: { show: () => Promise.resolve(), confirm: () => Promise.resolve(false), run: () => Promise.resolve(recorder.result), query, report: () => undefined },
    scheduler: new SchedulerHost({ env: ENV, queryTimeoutMs: () => 30_000, query }),
    durable: new MapStore(), nowUtcMs: () => 1_000, formatInstant: (iso) => iso, engine,
  };
}

test('own code review, note 13: a timed-out guard run names the engine; exit 13 on the MSI engine carries no Store note', async () => {
  const timedOut = harness(true, { kind: 'timedOut', timeoutMs: 180_000, treeEnded: false, stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) });
  await runGuardOp('install', withEngine(timedOut, fixedEngine(STORE_READ)));
  assert.match(timedOut.reports[0]?.message ?? '', /did not finish within 180 s .+ Elevated program: PowerShell 7 \(.+\)\.$/);
  const msi = 'C:\\Program Files\\PowerShell\\7\\pwsh.exe';
  const msiRead = { engine: { file: msi, label: `PowerShell 7 (${msi})`, kind: 'msi' as const }, failure: '' };
  const launch = harness(true, exited(EXIT.launchFailed));
  await runGuardOp('remove', withEngine(launch, fixedEngine(msiRead)));
  assert.match(launch.reports[0]?.message ?? '', /could not be started\. Elevated program: PowerShell 7/);
  assert.doesNotMatch(launch.reports[0]?.message ?? '', /Store package/);
});
