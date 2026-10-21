import assert from 'node:assert/strict';
import { test } from 'node:test';

import { TASKS_REMOVE_TAIL, TASKS_STAGE_TAIL, tasksRenderTail } from '../client/verbs';
import { UNELEVATED_SWITCHES } from '../powershell/unelevatedLauncher';
import type { Engine } from '../powershell/elevatedEngine';
import type { ProcessRequest, ProcessResult } from '../process/runner';
import { OP_EXIT } from '../scheduler/registerScript';
import { runTasksOp, type TasksFlowDeps, type TasksOutcome, type TasksOp } from '../windowsTasks/tasksFlow';
import { PENDING_KEY, readPending } from '../windowsTasks/tasksPending';
import { installTaskScript, removeTasksScript } from '../windowsTasks/tasksScripts';
import { elevatedProgramOf, PS51, PS51_READ, STORE_READ } from './support/engines';
import { exited } from './support/recordingRunner';
import { decoded, tasksWorld, type World, type WorldOptions } from './support/tasksWorld';
import { BUNDLED_EXE_FILE, EXTENSION, renderAnswer, STAGED_DIGEST, STAGED_EXE } from './support/windowsTasks';
import { isElevatedPowerShell, revealed } from './support/noRealWsl';
import { USER_SID } from './support/schedulerAnswers';

/**
 * *Install the Windows watch / archive task* and *Remove the Windows tasks* (WR-E3 of PLAN_windows_runner.md §3.4, §3.8): the REAL Windows client
 * over a runner that starts nothing (the daemon's golden render answers and the stage fixtures), the flow's surfaces recorded in ONE event
 * list. Every ending is one closed outcome; the pending record is written before the launch and cleared on every answer but a timeout; nothing
 * runs on a decline, a busy, a refused render or an engine that disagrees. No test starts a PowerShell — the recorder receives the request.
 */

const WATCH: TasksOp = { kind: 'install', task: 'watch' };
const ARCHIVE: TasksOp = { kind: 'install', task: 'archive' };
const REMOVE: TasksOp = { kind: 'remove' };
const NOW = 1_000_000;

async function run(op: TasksOp, options: WorldOptions = {}): Promise<{ readonly w: World; readonly outcome: TasksOutcome }> {
  const w = tasksWorld(options);

  return { w, outcome: await runTasksOp(op, w.deps) };
}

function message(w: World, at = 0): string {
  return w.recorder.reports[at]?.message ?? '';
}

function timedOut(treeEnded: boolean): ProcessResult {
  return { kind: 'timedOut', timeoutMs: 60_000, treeEnded, stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) };
}

const NOT_BUILT = (outcome: TasksOutcome): string => `${outcome.kind}: ${JSON.stringify(outcome)}`;

// ---- the order, and what refuses before anything runs ----

test('the order is stage → render → show → modal → pending → one unelevated PowerShell → read', async () => {
  const { w, outcome } = await run(WATCH);
  assert.deepEqual(w.events, ['stage', 'render', 'engine', 'show', 'confirm', 'reread', 'run(pending=install:watch)', 'query', 'report']);
  assert.deepEqual(w.daemon(), [TASKS_STAGE_TAIL.join(' '), tasksRenderTail('watch').join(' ')]);
  assert.equal(w.ps().length, 1);
  assert.equal(outcome.kind, 'done', NOT_BUILT(outcome));
  assert.equal(w.recorder.reports[0]?.failed, false);
  assert.equal(w.recorder.shown[0]?.text, decoded(w.ps()[0]), 'what was shown is byte for byte what PowerShell decodes');
  assert.ok(w.recorder.shown[0]?.text.includes(String(renderAnswer('watch from the staged copy').xml)), 'the XML shown is the render\'s, verbatim');
  assert.equal(w.store.get(PENDING_KEY), undefined);
});

test('a digest mismatch stages nothing and registers nothing', async () => {
  const { w, outcome } = await run(WATCH, { client: { digest: 'fedcba9876543210'.repeat(4) } });
  assert.equal(outcome.kind, 'notStarted', NOT_BUILT(outcome));
  assert.match(message(w), new RegExp(`${STAGED_DIGEST}.*fedcba9876543210|fedcba9876543210.*${STAGED_DIGEST}`));
  assert.deepEqual(w.daemon(), [TASKS_STAGE_TAIL.join(' ')], 'no render was asked');
  assert.deepEqual(w.recorder.shown, []);
  assert.deepEqual(w.recorder.prompts, []);
  assert.deepEqual(w.ps(), []);
  assert.equal(w.store.writes, 0);
});

test('a render that is not registrable registers nothing', async () => {
  const { w, outcome } = await run(WATCH, { renders: { watch: 'watch from the extension folder' } });
  assert.equal(outcome.kind, 'notStarted', NOT_BUILT(outcome));
  assert.match(message(w), /is not a staged copy/);
  assert.deepEqual([w.recorder.shown, w.recorder.prompts, w.ps()], [[], [], []]);
  assert.equal(w.store.writes, 0);
});

test('a decline runs nothing', async () => {
  const { w, outcome } = await run(WATCH, { answer: false });
  assert.equal(outcome.kind, 'declined', NOT_BUILT(outcome));
  assert.equal(w.ps().length, 0, `a declined modal started a PowerShell (requests: ${w.ps().length})`);
  assert.deepEqual(w.daemon(), [TASKS_STAGE_TAIL.join(' '), tasksRenderTail('watch').join(' ')]);
  assert.equal(w.store.writes, 0);
  assert.equal(w.recorder.reports.length, 1);
  assert.equal(w.recorder.reports[0]?.failed, false);
  assert.ok(message(w).includes(STAGED_EXE), 'it names the staged copy it leaves behind');
  assert.match(message(w), /Remove the Windows tasks/);
});

test('a decline of the removal says nothing and runs nothing', async () => {
  const { w, outcome } = await run(REMOVE, { answer: false });
  assert.equal(outcome.kind, 'declined');
  assert.deepEqual([w.ps(), w.recorder.reports, w.daemon()], [[], [], []]);
  assert.equal(w.store.writes, 0);
});

test('busy, a guard refusal and an engine disagreement write nothing and run nothing', async () => {
  const busy = tasksWorld();
  await busy.store.update(PENDING_KEY, { op: 'remove', startedAtUtcMs: NOW - 1, deadlineUtcMs: NOW + 100_000 });
  const writes = busy.store.writes;
  assert.equal((await runTasksOp(WATCH, busy.deps)).kind, 'busy');
  assert.deepEqual(busy.events, ['report'], 'nothing staged, shown, asked or run while any window\'s record stands');
  assert.equal(busy.store.writes, writes, 'the other window\'s record is left as it was');

  const refused = await run(WATCH, { read: { user: 'S-1-5-21-1-2-3-1002' } });
  assert.equal(refused.outcome.kind, 'notStarted');
  assert.deepEqual([refused.w.recorder.shown, refused.w.ps(), refused.w.store.writes], [[], [], 0]);

  const moved = await run(WATCH, { engine: [STORE_READ, PS51_READ] });
  assert.equal(moved.outcome.kind, 'notRun');
  assert.deepEqual([moved.w.ps(), moved.w.store.writes], [[], 0]);
  assert.equal(moved.w.events.at(-2), 'reread');
});

test('a run another window confirmed while this modal was open makes this one busy — nothing runs', async () => {
  const w = tasksWorld();
  const deps: TasksFlowDeps = { ...w.deps, ui: { ...w.deps.ui, confirm: async (prompt) => { await w.store.update(PENDING_KEY, { op: 'remove', startedAtUtcMs: NOW - 1, deadlineUtcMs: NOW + 100_000 }); return w.deps.ui.confirm(prompt); } } };
  assert.equal((await runTasksOp(WATCH, deps)).kind, 'busy');
  assert.deepEqual(w.ps(), []);
  assert.equal(readPending(w.store, NOW)?.op, 'remove', 'the other window\'s record is left as it was');
});

test('the engine is read before the modal and AGAIN before the launch; a change runs nothing', async () => {
  const { w, outcome } = await run(WATCH, { engine: [STORE_READ, PS51_READ] });
  assert.equal(outcome.kind, 'notRun');
  assert.deepEqual(w.events.filter((e) => e === 'engine' || e === 'reread'), ['engine', 'reread']);
  assert.deepEqual([w.ps().length, w.store.writes], [0, 0]);
  assert.match(message(w), /Nothing ran: the elevated program changed/);
});

// ---- the pending record: finding (1) of the plan round ----

test('finding 1: the record is cleared on ANY answer but a timeout, and kept to its deadline after one', async () => {
  const endings: readonly (readonly [string, (request: ProcessRequest) => Promise<ProcessResult>])[] = [
    ['exit 0', () => Promise.resolve(exited(0))],
    ['exit 30', () => Promise.resolve(exited(30, '', 'Register-ScheduledTask: access denied'))],
    ['exit 33', () => Promise.resolve(exited(33))],
    ['exit 1', () => Promise.resolve(exited(1, '', 'boom'))],
    ['exit -1', () => Promise.resolve(exited(4294967295))],
    ['failedToStart', () => Promise.resolve<ProcessResult>({ kind: 'failedToStart', reason: 'ENOENT' })],
    ['a runner that throws', () => Promise.reject(new Error('the runner broke'))],
    ['signalled', () => Promise.resolve<ProcessResult>({ kind: 'signalled', signal: 'SIGKILL', stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) })],
    ['tooMuchOutput', () => Promise.resolve<ProcessResult>({ kind: 'tooMuchOutput', stream: 'stdout', limitBytes: 1 })],
  ];
  for (const [name, answer] of endings) {
    const { w } = await run(WATCH, { run: answer });
    assert.equal(w.store.get(PENDING_KEY), undefined, `${name}: an answer left the pending record`);
    assert.equal(w.recorder.reports.length, 1, `${name}: not told exactly once`);
    assert.ok(w.events.includes('query'), `${name}: the status was not read again`);
  }
  const first = await run(WATCH, { run: () => Promise.resolve(timedOut(true)) });
  assert.equal(first.outcome.kind, 'timedOut');
  assert.deepEqual(readPending(first.w.store, NOW), { op: 'install', task: 'watch', startedAtUtcMs: NOW, deadlineUtcMs: NOW + 2 * 60_000 }, 'a timed-out run did not keep its record');
  const early = tasksWorld({ store: first.w.store, now: NOW + 1 });
  assert.equal((await runTasksOp(REMOVE, early.deps)).kind, 'busy');
  const atDeadline = tasksWorld({ store: first.w.store, now: NOW + 2 * 60_000 });
  assert.equal((await runTasksOp(REMOVE, atDeadline.deps)).kind, 'done', 'past its deadline it holds nothing');
});

test('the record is written BEFORE the launch', async () => {
  const { w } = await run(ARCHIVE);
  assert.ok(w.events.includes('run(pending=install:archive)'), `order: ${w.events.join(' → ')}`);
  const removal = await run(REMOVE);
  assert.ok(removal.w.events.includes('run(pending=remove)'), `order: ${removal.w.events.join(' → ')}`);
});

// ---- the request: never elevated, closed argv, the tree ----

test('an install request is never elevated, carries the fixed switches and asks for the tree kill', async () => {
  for (const op of [WATCH, ARCHIVE, REMOVE]) {
    const { w } = await run(op);
    const request = w.ps()[0];
    assert.ok(request !== undefined, 'a request was made');
    assert.equal(isElevatedPowerShell(request.file, request.args), false, 'the install request asks for elevation');
    assert.doesNotMatch(revealed(request.args), /runas|-Verb|Start-Process|ProcessStartInfo/i);
    assert.deepEqual(request.args.slice(0, 3), [...UNELEVATED_SWITCHES]);
    assert.equal(request.file, PS51);
    assert.equal(request.killTree, true, 'the registration request does not ask for the tree kill');
    assert.equal(request.timeoutMs, 60_000);
    assert.equal(elevatedProgramOf(request), '');
  }
});

test('every argv is a closed tail', async () => {
  const closed = new Set([TASKS_STAGE_TAIL.join(' '), TASKS_REMOVE_TAIL.join(' '), tasksRenderTail('watch').join(' '), tasksRenderTail('archive').join(' ')]);
  for (const op of [WATCH, ARCHIVE, REMOVE]) {
    const { w } = await run(op);
    for (const argv of w.daemon()) {
      assert.ok(closed.has(argv), `an argv the extension may not send: ${argv}`);
    }
  }
});

test('the script carries the daemon\'s XML for the task asked, and the removal\'s script is the constant', async () => {
  const watch = await run(WATCH);
  assert.equal(decoded(watch.w.ps()[0]), installTaskScript('watch', String(renderAnswer('watch from the staged copy').xml)));
  const archive = await run(ARCHIVE);
  assert.equal(decoded(archive.w.ps()[0]), installTaskScript('archive', String(renderAnswer('archive from the staged copy').xml)));
  const removal = await run(REMOVE);
  assert.equal(decoded(removal.w.ps()[0]), removeTasksScript());
  assert.ok((removal.w.recorder.prompts[0]?.detail ?? '').includes(removeTasksScript()), 'the removal modal holds the script verbatim');
});

// ---- refusals of the render (the guards, end to end) ----

const XML = (body: Record<string, unknown>): string => String(body.xml);

test('a SYSTEM / highest-available / S4U / password / service-account / group principal in the XML registers nothing', async () => {
  const cases: readonly (readonly [string, (xml: string) => string, RegExp])[] = [
    ['S-1-5-18', (xml) => xml.replaceAll(USER_SID, 'S-1-5-18'), /S-1-5-18/],
    ['S-1-5-19', (xml) => xml.replaceAll(USER_SID, 'S-1-5-19'), /S-1-5-19/],
    ['S-1-5-20', (xml) => xml.replaceAll(USER_SID, 'S-1-5-20'), /S-1-5-20/],
    ['HighestAvailable', (xml) => xml.replace('LeastPrivilege', 'HighestAvailable'), /HighestAvailable/],
    ['S4U', (xml) => xml.replace('InteractiveToken', 'S4U'), /S4U/],
    ['Password', (xml) => xml.replace('InteractiveToken', 'Password'), /Password/],
    ['ServiceAccount', (xml) => xml.replace('InteractiveToken', 'ServiceAccount'), /ServiceAccount/],
    ['GroupId', (xml) => xml.replace('<Principal id="Author">', '<Principal id="Author"><GroupId>S-1-5-32-544</GroupId>'), /<GroupId>/],
  ];
  for (const [name, doctor, why] of cases) {
    const { w, outcome } = await run(WATCH, { editRender: (body) => ({ ...body, xml: doctor(XML(body)) }) });
    assert.equal(outcome.kind, 'notStarted', `${name}: a ${name} principal was registered`);
    assert.match(message(w), why, name);
    assert.deepEqual([w.recorder.shown, w.recorder.prompts, w.ps(), w.store.writes], [[], [], [], 0], `${name}: a SYSTEM principal was shown to the person`);
  }
});

test('a task for another account than the reader registers nothing; an unanswered read registers nothing', async () => {
  const other = await run(WATCH, { read: { user: 'S-1-5-21-1-2-3-1002' } });
  assert.equal(other.outcome.kind, 'notStarted', 'a task for another account was shown');
  assert.ok(message(other.w).includes(USER_SID) && message(other.w).includes('S-1-5-21-1-2-3-1002'), 'it names both accounts');
  const unknown = await run(WATCH, { read: { kind: 'unknown', cause: 'failed', reason: 'no answer' } });
  assert.equal(unknown.outcome.kind, 'notStarted');
  assert.match(message(unknown.w), /has not answered who you are/);
  assert.deepEqual([unknown.w.recorder.shown, unknown.w.ps()], [[], []]);
});

test('the extension folder\'s exe is never registered', async () => {
  const named = await run(WATCH, { editRender: (body) => ({ ...body, xml: XML(body).replace('Installed and removed', `${EXTENSION.fsPath} Installed and removed`) }) });
  assert.equal(named.outcome.kind, 'notStarted', 'the extension folder\'s exe was offered for registration');
  assert.match(message(named.w), /extension's own folder/);
  assert.deepEqual([named.w.recorder.shown, named.w.ps()], [[], []]);
  const bundled = await run(WATCH, { editRender: (body) => ({ ...body, command: BUNDLED_EXE_FILE, xml: XML(body).replace(STAGED_EXE, BUNDLED_EXE_FILE) }) });
  assert.equal(bundled.outcome.kind, 'notStarted', 'the client refuses a render that is not the copy it started');
  assert.deepEqual(bundled.w.ps(), []);
});

test('an XML with a here-string terminator line cannot be embedded: told, nothing shown', async () => {
  const { w, outcome } = await run(WATCH, { editRender: (body) => ({ ...body, xml: `${XML(body)}\n'@ Remove-Item x` }) });
  assert.equal(outcome.kind, 'notStarted');
  assert.match(message(w), /cannot be embedded/);
  assert.deepEqual([w.recorder.shown, w.ps()], [[], []]);
});

test('the request\'s length is bounded: the golden\'s largest XML fits, a 20 000-character one is refused', async () => {
  const fits = await run(ARCHIVE);
  assert.equal(fits.outcome.kind, 'done');
  const { w, outcome } = await run(WATCH, { editRender: (body) => ({ ...body, xml: XML(body).replace('</RegistrationInfo>', `<Documentation>${'x'.repeat(20_000)}</Documentation></RegistrationInfo>`) }) });
  assert.equal(outcome.kind, 'notStarted');
  assert.match(message(w), /too long for one command line/);
  assert.deepEqual([w.recorder.shown, w.ps()], [[], []]);
});

// ---- the stage: always, once more when busy, never more ----

test('a busy stage is retried ONCE after the setting\'s wait; twice is told', async () => {
  const once = await run(WATCH, { stage: [['stage-busy.json', 75], ['stage-staged.json', 0]], busyRetryMs: 4_000 });
  assert.equal(once.outcome.kind, 'done');
  assert.deepEqual(once.w.sleeps, [4_000]);
  assert.equal(once.w.daemon().filter((a) => a === TASKS_STAGE_TAIL.join(' ')).length, 2, 'expected 2 stage requests');
  const twice = await run(WATCH, { stage: [['stage-busy.json', 75]] });
  assert.equal(twice.outcome.kind, 'notStarted');
  assert.match(message(twice.w), /held twice/);
  assert.equal(twice.w.daemon().filter((a) => a === TASKS_STAGE_TAIL.join(' ')).length, 2, 'expected 2 stage requests');
  assert.ok(!twice.w.daemon().some((a) => a.includes('render')), 'no render after a stage that never happened');
});

test('a refused stage and a reused one: refused is told, reused IS a stage', async () => {
  const refused = await run(WATCH, { stage: [['stage-refused.json', 1]] });
  assert.equal(refused.outcome.kind, 'notStarted');
  assert.deepEqual(refused.w.ps(), []);
  const reused = await run(WATCH, { stage: [['stage-reused.json', 0]] });
  assert.equal(reused.outcome.kind, 'done', 'reused is a verified copy, not "not needed"');
  assert.deepEqual(reused.w.daemon(), [TASKS_STAGE_TAIL.join(' '), tasksRenderTail('watch').join(' ')]);
});

test('the render is fresh: asked after the stage, as a NEW request, every time', async () => {
  const first = await run(WATCH);
  const second = await run(ARCHIVE);
  for (const { w } of [first, second]) {
    assert.equal(w.daemon().filter((a) => a.includes('render')).length, 1, 'expected 1 render after the stage, got 0');
    assert.ok(w.events.indexOf('render') > w.events.indexOf('stage'));
  }
});

// ---- the modals ----

test('the install modal names every fact the plan requires, in words, each from the render', async () => {
  const { w } = await run(WATCH);
  const prompt = w.recorder.prompts[0];
  const detail = prompt?.detail ?? '';
  assert.match(prompt?.message ?? '', /Install the Windows watch task/);
  assert.equal(prompt?.confirm, 'Install it (one PowerShell, not elevated)');
  assert.match(detail, /runs AS YOU — not elevated, no UAC prompt — and registers ONE scheduled task, \\wsl-care\\watch/);
  assert.match(detail, new RegExp(`The program: Windows PowerShell 5.1 \\(${PS51.replaceAll('\\', '\\\\')}\\)`));
  assert.ok(detail.includes(`It runs as your account (${USER_SID}), interactive token, least privilege — never elevated, never as SYSTEM.`));
  assert.match(detail, /at logon \+ 60 s, then every 5 min/);
  assert.match(detail, /ended by its own supervisor after 10 min .* by Task Scheduler after 20 min/);
  assert.ok(detail.includes(`${STAGED_EXE} (SHA-256 ${STAGED_DIGEST}, release 0.4.0)`));
  assert.match(detail, /never the extension folder/);
  assert.match(detail, /The copy is already staged; \*Remove the Windows tasks\* deletes it/);
  assert.match(detail, /Dry run: the setting dryRun is on/);
  assert.match(detail, /While dry the task samples only/);
  assert.match(detail, /samples the Windows MCP servers into the daemon's CPU ledger and acts on nothing/);
  assert.match(detail, /runs only while you are logged on/);
  assert.match(detail, /EnableLUA=0.*exit 81/);
  assert.match(detail, /Remove every Windows task and the copy at any time with \*Remove the Windows tasks\*/);
  const archive = (await run(ARCHIVE)).w.recorder.prompts[0]?.detail ?? '';
  assert.match(archive, /at logon \+ 20 min, then every 4 h/);
  assert.match(archive, /after 276 min/);
  assert.match(archive, /archive preview/);
  assert.match(archive, /While dry the task previews only/);
});

test('a number the render did not report is said so, never guessed', async () => {
  const { w } = await run(WATCH, { editRender: (body) => ({ ...body, numbers: [] }) });
  assert.match(w.recorder.prompts[0]?.detail ?? '', /at logon \+ \(not reported\) s, then every \(not reported\) min/);
});

test('the remove modal holds the script, both tasks, the folder rule, the copies and the kept-copy rule', async () => {
  const { w } = await run(REMOVE);
  const prompt = w.recorder.prompts[0];
  const detail = prompt?.detail ?? '';
  assert.equal(prompt?.confirm, 'Remove them (one PowerShell, not elevated)');
  assert.ok(detail.includes(removeTasksScript()));
  assert.match(detail, /\\wsl-care\\watch, \\wsl-care\\archive and \\wsl-care\\cleanup/);
  assert.match(detail, /the Windows Time guard's task keeps it/);
  assert.match(detail, /a copy whose exe is still executing .* is kept and named/);
  assert.match(detail, /The program: Windows PowerShell 5.1/);
  assert.match(detail, /A task run in progress is not waited for; Task Scheduler may stop it/);
  assert.deepEqual(w.recorder.shown, [], 'the removal shows no document: the script is in the modal');
});

test('a hostile daemon sentence is sanitised in the modal and the report', async () => {
  const hostile = 'evil\u202E[click](command:wslCare.x)';
  const modal = await run(WATCH, { editRender: (body) => ({ ...body, dry: { ...(body.dry as object), reason: hostile } }) });
  const detail = modal.w.recorder.prompts[0]?.detail ?? '';
  assert.doesNotMatch(detail, /\u202E/, 'an unsanitised daemon sentence reached the modal');
  assert.ok(!detail.includes(']('), 'a markdown link survived in the modal');
  assert.ok(detail.includes('] ('));
  const report = await run(WATCH, { renders: { watch: 'watch from the extension folder' }, editRender: (body) => ({ ...body, reason: hostile }) });
  assert.doesNotMatch(message(report.w), /\u202E/);
  assert.ok(!message(report.w).includes(']('));
  assert.ok(message(report.w).includes('] ('), 'an unsanitised daemon sentence reached the report');
});

// ---- every exit is one closed outcome ----

test('every exit of the ONE PowerShell is one closed outcome with its own sentence and the first line of its stderr', async () => {
  const cases: readonly (readonly [ProcessResult, TasksOutcome['kind'], RegExp, boolean])[] = [
    [exited(0), 'done', /The Windows watch task is installed: Task Scheduler runs the Windows MCP watch every 5 min while you are logged on\. Program: Windows PowerShell 5\.1/, false],
    [exited(OP_EXIT.registerFailed, '', 'Register-ScheduledTask: the user name or password is incorrect\r\nsecond line'), 'failed', /did not register the task \(creating the \\wsl-care folder or Register-ScheduledTask failed\): Register-ScheduledTask: the user name or password is incorrect\.[^]*Program:/, true],
    [exited(OP_EXIT.importFailed), 'failed', /could not load its ScheduledTasks module \(Windows PowerShell 5\.1/, true],
    [exited(1, '', 'cannot start'), 'failed', /failed before its first step \(exit 1\): cannot start/, true],
    [exited(4294967295), 'failed', /exited -1/, true],
    [exited(77), 'failed', /exited 77/, true],
    [{ kind: 'failedToStart', reason: 'spawn ENOENT' }, 'notStarted', /could not start: spawn ENOENT/, true],
    [{ kind: 'signalled', signal: 'SIGTERM', stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) }, 'failed', /was stopped \(SIGTERM\)/, true],
    [{ kind: 'tooMuchOutput', stream: 'stdout', limitBytes: 1 }, 'failed', /printed more than the extension reads/, true],
    [timedOut(true), 'timedOut', /did not finish within 60 s \(wslCare\.timeouts\.windowsTasksSeconds\); its PowerShell and everything it started were ended/, true],
    [timedOut(false), 'timedOut', /the extension could not confirm that its PowerShell and everything it started were ended/, true],
  ];
  for (const [result, kind, sentence, failed] of cases) {
    const { w, outcome } = await run(WATCH, { run: () => Promise.resolve(result) });
    assert.equal(outcome.kind, kind, JSON.stringify(result));
    assert.match(message(w), sentence, JSON.stringify(result));
    assert.equal(w.recorder.reports[0]?.failed, failed, JSON.stringify(result));
    assert.equal(w.recorder.reports.length, 1);
  }
});

test('the Store engine\'s failed start carries the Store note; the MSI\'s does not', async () => {
  const msi: Engine = { file: 'C:\\Program Files\\PowerShell\\7\\pwsh.exe', label: 'PowerShell 7 (C:\\Program Files\\PowerShell\\7\\pwsh.exe)', kind: 'msi' };
  const failedStart: ProcessResult = { kind: 'failedToStart', reason: 'spawn ENOENT' };
  const store = await run(WATCH, { engine: [STORE_READ, STORE_READ], run: () => Promise.resolve(failedStart) });
  assert.match(message(store.w), /Store package could not be started by its package path from this extension; install PowerShell 7 \(MSI\)/);
  const direct = await run(WATCH, { engine: [{ engine: msi, failure: '' }, { engine: msi, failure: '' }], run: () => Promise.resolve(failedStart) });
  assert.doesNotMatch(message(direct.w), /Store package/);
  assert.equal(direct.w.ps()[0]?.file, msi.file);
});

// ---- the removal: its own order, and the folder ----

test('the removal runs the tasks\' PowerShell, then the daemon\'s staged-copy removal, only after exit 0 or 32', async () => {
  const cases: readonly (readonly [string, ProcessResult, boolean, TasksOutcome['kind']])[] = [
    ['exit 0', exited(0), true, 'done'],
    ['exit 31', exited(OP_EXIT.unregisterFailed, '', 'access denied'), false, 'failed'],
    ['exit 32', exited(OP_EXIT.cleanupFailed), true, 'done'],
    ['timedOut', timedOut(true), false, 'timedOut'],
    ['failedToStart', { kind: 'failedToStart', reason: 'ENOENT' }, false, 'notStarted'],
  ];
  for (const [name, result, called, kind] of cases) {
    const { w, outcome } = await run(REMOVE, { run: () => Promise.resolve(result) });
    assert.equal(outcome.kind, kind, name);
    assert.equal(w.daemon().includes(TASKS_REMOVE_TAIL.join(' ')), called, `${name}: the remove deleted the folder with a task in it / the copies went before the tasks`);
    assert.equal(readPending(w.store, NOW) !== undefined, name === 'timedOut', `${name}: the record`);
  }
  const folder = await run(REMOVE, { run: () => Promise.resolve(exited(OP_EXIT.cleanupFailed)) });
  assert.match(message(folder.w), /the folder could not be removed/);
  const failed = await run(REMOVE, { run: () => Promise.resolve(exited(OP_EXIT.unregisterFailed, '', 'access denied')) });
  assert.match(message(failed.w), /did not delete a task: access denied/);
});

test('the removal keeps the folder while the guard\'s task is in it, and says so', async () => {
  const { w, outcome } = await run(REMOVE, { read: { tasks: [{ name: 'windows-time-guard', summary: [] }] } });
  assert.equal(outcome.kind, 'done');
  assert.match(message(w), /the folder stays: another task is in it \(windows-time-guard/, 'the remove script deletes the folder unconditionally');
  const alone = await run(REMOVE);
  assert.match(message(alone.w), /\\wsl-care\\watch, \\wsl-care\\archive, \\wsl-care\\cleanup, the folder, and 1 staged copy\. Program: Windows PowerShell 5\.1/);
});

test('a kept executing copy is named, never forced', async () => {
  const { w } = await run(REMOVE);
  assert.match(message(w), /Kept \(an exe still executing\): .*ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff — its exe is still executing; the next install or removal retries it\./);
  assert.match(message(w), /Left alone \(not a staged copy\): notes\.txt\./);
  assert.equal(w.daemon().filter((a) => a === TASKS_REMOVE_TAIL.join(' ')).length, 1, 'no second removal of its own');
});

test('a busy staged-copy removal is retried once; twice is told as "tasks removed, copies not"', async () => {
  const once = await run(REMOVE, { stageRemove: [['stage-remove-busy.json', 75], ['stage-remove-removed.json', 0]] });
  assert.equal(once.outcome.kind, 'done');
  assert.deepEqual(once.w.sleeps, [3_000]);
  const twice = await run(REMOVE, { stageRemove: [['stage-remove-busy.json', 75]] });
  assert.equal(twice.outcome.kind, 'failed');
  assert.match(message(twice.w), /the tasks are removed, but the staged copies were not: the daemon's run lock was held twice/);
  assert.equal(twice.w.daemon().filter((a) => a === TASKS_REMOVE_TAIL.join(' ')).length, 2);
  assert.equal(twice.w.store.get(PENDING_KEY), undefined);
});

test('a TYPED timeout of the daemon staged-copy removal keeps the pending record to its deadline, like the PowerShell own timeout', async () => {
  const timeout: ProcessResult = { kind: 'timedOut', timeoutMs: 60_000, treeEnded: false, stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) };
  const { w, outcome } = await run(REMOVE, { ending: (argv) => (argv === TASKS_REMOVE_TAIL.join(' ') ? timeout : undefined) });
  assert.equal(outcome.kind, 'timedOut', NOT_BUILT(outcome));
  assert.notEqual(readPending(w.store, NOW), undefined, 'a timed-out staged-copy removal did not keep its record');
  assert.equal(readPending(w.store, NOW)?.op, 'remove');
  assert.equal(w.recorder.reports[0]?.failed, true);
  assert.match(message(w), /did not finish within 60 s/);
  assert.match(message(w), /the extension could not confirm that its PowerShell and everything it started were ended/);
  const later = tasksWorld({ store: w.store, now: NOW + 1 });
  assert.equal((await runTasksOp(WATCH, later.deps)).kind, 'busy', 'every button stays held while it stands');
});

test('Fable minor 6: with an unknown read the removal does not claim the folder was removed', async () => {
  const { w, outcome } = await run(REMOVE, { read: { kind: 'unknown', cause: 'failed', reason: 'no answer' } });
  assert.equal(outcome.kind, 'done');
  assert.match(message(w), /, the folder \(when it was empty\), and 1 staged copy\./);
  const known = await run(REMOVE);
  assert.match(message(known.w), /, the folder, and 1 staged copy\./, 'a known read still says the folder');
});

test('Fable minor 7: a pending record that cannot be cleared is logged, and the outcome is still reported', async () => {
  const w = tasksWorld();
  const failing = { get: (key: string) => w.store.get(key), update: (key: string, value: unknown) => (value === undefined ? Promise.reject(new Error('the store refused')) : w.store.update(key, value)) };
  const outcome = await runTasksOp(WATCH, { ...w.deps, durable: failing });
  assert.equal(outcome.kind, 'done', 'the outcome is not swallowed by the store fault');
  assert.equal(w.recorder.reports.length, 1, 'it is told exactly once');
  assert.ok(w.events.includes('query'), 'the status is read again');
  assert.match(w.logs.join('\n'), /the pending record could not be cleared: the store refused/, 'the fault is logged');
});

// ---- WR-T3: the cleanup task is the third name of E3's flow ----

const CLEANUP: TasksOp = { kind: 'install', task: 'cleanup' };

test('the install of cleanup is E3\'s order for the third name: stage → render → show → modal → pending → one unelevated PowerShell → read', async () => {
  const { w, outcome } = await run(CLEANUP);
  assert.deepEqual(w.events, ['stage', 'render', 'engine', 'show', 'confirm', 'reread', 'run(pending=install:cleanup)', 'query', 'report']);
  assert.deepEqual(w.daemon(), [TASKS_STAGE_TAIL.join(' '), tasksRenderTail('cleanup').join(' ')]);
  assert.equal(outcome.kind, 'done', NOT_BUILT(outcome));
  assert.equal(w.recorder.shown[0]?.task, 'cleanup');
  assert.equal(w.recorder.shown[0]?.text, decoded(w.ps()[0]), 'what was shown is byte for byte what PowerShell decodes');
  assert.ok(w.recorder.shown[0]?.text.includes(String(renderAnswer('cleanup from the staged copy').xml)), 'the XML shown is the render\'s, verbatim');
  assert.match(message(w), /The Windows cleanup task is installed: Task Scheduler runs the cleanup of your own old scratch entries in the Windows TEMP folder \(A23\) every 24 h while you are logged on/);
  assert.equal(w.store.get(PENDING_KEY), undefined);
});

test('a render for the watch offered for the cleanup registers nothing: the task path is the task asked\'s', async () => {
  const { w, outcome } = await run(CLEANUP, { editRender: (body) => ({ ...body, taskPath: '\\wsl-care\\watch' }) });
  assert.equal(outcome.kind, 'notStarted', NOT_BUILT(outcome));
  assert.match(message(w), /the render's task path .* is not \\wsl-care\\cleanup/);
  assert.deepEqual(w.ps(), []);
  assert.deepEqual(w.recorder.shown, []);
});

test('the install modal for cleanup names A23, the allow-list key, the age key, never %TEMP%\\claude, logon + 30 min then every 24 h, the deadline, A23\'s OWN dry week, busy 75 and UAC-off', async () => {
  const { w } = await run(CLEANUP);
  const prompt = w.recorder.prompts[0];
  const detail = prompt?.detail ?? '';
  assert.match(prompt?.message ?? '', /Install the Windows cleanup task/);
  assert.match(prompt?.message ?? '', /DELETES/, 'the question says the task deletes');
  assert.match(detail, /registers ONE scheduled task, \\wsl-care\\cleanup/);
  assert.match(detail, /at logon \+ 30 min, then every 24 h/);
  assert.match(detail, /ended by its own supervisor after 276 min .* by Task Scheduler after 286 min/);
  assert.match(detail, /Dry run: the setting dryRun is on/);
  assert.match(detail, /OWN dry week \(A23's, separate from the watch's and the archive's\)/);
  assert.match(detail, /While dry the task previews only and deletes nothing; the dry run ends/);
  assert.match(detail, /deletes your OWN old scratch entries in the Windows TEMP folder/);
  assert.match(detail, /temp\.allowGlobs/);
  assert.match(detail, /temp\.olderThanDays/);
  assert.match(detail, /never %TEMP%\\claude/, 'The input did not match /never %TEMP%\\\\claude/');
  assert.match(detail, /never a git working folder, never a reparse point/);
  assert.match(detail, /temp\.maxItemsPerRun/);
  assert.match(detail, /Press \*Preview the Windows TEMP cleanup\*/);
  assert.match(detail, /A last result of 75 means busy/);
  assert.match(detail, /EnableLUA=0.*exit 81.*the preview answers 81 too/);
  assert.match(detail, /Remove every Windows task and the copy at any time with \*Remove the Windows tasks\*/);
  assert.doesNotMatch(detail, /samples|MCP servers/, 'nothing of the watch\'s words');
});

test('the removal modal and its sentence name three tasks, derived from the closed list', async () => {
  const { w, outcome } = await run(REMOVE);
  assert.match(w.recorder.prompts[0]?.detail ?? '', /deletes the tasks \\wsl-care\\watch, \\wsl-care\\archive and \\wsl-care\\cleanup when present/);
  assert.equal(outcome.kind, 'done', NOT_BUILT(outcome));
  assert.match(message(w), /The Windows tasks are removed — \\wsl-care\\watch, \\wsl-care\\archive, \\wsl-care\\cleanup, the folder, and 1 staged copy/);
  assert.match(removeTasksScript(), /DeleteTask\('cleanup', 0\)/, 'does not match /DeleteTask\\(\'cleanup\', 0\\)/');
});
