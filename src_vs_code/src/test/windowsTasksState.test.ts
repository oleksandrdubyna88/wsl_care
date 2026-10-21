import assert from 'node:assert/strict';
import * as childProcess from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { test } from 'node:test';

import type { TaskName } from '../client/verbs';
import { renderResultOf, type RenderResult } from '../client/windowsTasks';
import { guardSummary, guardTaskXml, SUMMARY_FUNCTION } from '../windowsTime/guardTask';
import { SCHEDULER_RESULTS, summariesMatch } from '../windowsTime/guardState';
import { parseSchedulerAnswer, SCHEDULER_RESULT, SCHEDULER_RESULT_WORDS } from '../scheduler/schedulerAnswer';
import { windowsTasksState, type CopyState, type WindowsTasksState } from '../windowsTasks/tasksState';
import { NO_WINDOWS_TASKS, windowsTasksView, type WindowsTasksView } from '../windowsTasks/tasksView';
import { MUTATING } from './support/noRealWsl';
import { schedulerStdout, USER_SID, type TaskFixture } from './support/schedulerAnswers';
import { summaryOfXml } from './support/summaryOfXml';
import { exited, renderAnswer, RENDER_GOLDEN, STAGED_DIGEST, STAGED_EXE } from './support/windowsTasks';
import { parseXml } from './support/xmlTree';

/**
 * The Windows tasks' part (WR-E2 of PLAN_windows_runner.md §3.8): per task the plan's D5 rows — not installed · installed, last
 * run <t>: <sentence> · install again to update · disabled · running (0x41301) · unreadable / not Windows — and the copy line
 * and the dry line, all pure, so every row and every plan-round finding (4fc3f328) is a unit test.
 */

const PIN = '0.4.0';
const LOCAL = (iso: string): string => `<${iso}>`;

function render(task: TaskName, name = `${task} from the staged copy`): RenderResult {
  return renderResultOf(exited(0, renderAnswer(name)), task, STAGED_DIGEST, STAGED_EXE);
}

const WATCH = render('watch');
const ARCHIVE = render('archive');

/** What Task Scheduler stores and reads back for a render's summary: a trigger delay `PT60S` becomes `PT1M` (measured 2026-10-10). */
function registered(result: RenderResult): readonly string[] {
  return (result.kind === 'registrable' || result.kind === 'notRegistrable' ? result.answer.summary : []).map((line) => line.replaceAll('delay=PT60S', 'delay=PT1M'));
}

const STAGED: CopyState = { kind: 'staged', digest: STAGED_DIGEST, renders: { watch: WATCH, archive: ARCHIVE } };

function task(name: TaskName, over: Partial<TaskFixture> = {}): TaskFixture {
  return { name, summary: registered(name === 'watch' ? WATCH : ARCHIVE), ...over };
}

function stateOf(tasks: readonly TaskFixture[], copy: CopyState = STAGED, folder: 'present' | 'absent' | { unreadable: string } = 'present'): WindowsTasksState {
  return windowsTasksState(parseSchedulerAnswer(schedulerStdout({ tasks, folder })), copy);
}

function viewOf(state: WindowsTasksState): WindowsTasksView {
  return windowsTasksView(state, PIN, LOCAL);
}

function lineOf(view: WindowsTasksView, id: TaskName): string {
  return view.tasks.find((t) => t.id === id)?.line ?? '';
}

// ---- each state row renders ----

test('each state row renders: not installed · installed with its last run · disabled · running · update needed · unreadable', () => {
  const view = viewOf(stateOf([task('watch', { lastRunUtc: '2026-10-16T08:00:00.0000000Z', lastResult: '0' })]));
  assert.equal(lineOf(view, 'archive'), 'Windows archive task: not installed');
  assert.equal(lineOf(view, 'watch'), 'Windows watch task: installed, last run <2026-10-16T08:00:00.0000000Z>: succeeded');
  assert.deepEqual(view.tasks.map((t) => [t.id, t.level]), [['watch', 'ok'], ['archive', 'none']]);

  assert.equal(lineOf(viewOf(stateOf([task('watch', { enabled: 'False' })])), 'watch'), 'Windows watch task: installed, but disabled in Task Scheduler — install again to enable it');
  assert.equal(lineOf(viewOf(stateOf([task('watch', { state: '4', lastResult: '267009' })])), 'watch'), 'Windows watch task: installed, running (0x41301)');
  assert.match(lineOf(viewOf(stateOf([task('watch', { summary: registered(WATCH).map((l) => l.replace('level=0', 'level=1')) })])), 'watch'), /^Windows watch task: installed, but not as this version would install it .* install again to update$/);
  assert.equal(lineOf(viewOf(stateOf([task('watch', { unreadable: '0x80070005' })])), 'watch'), 'Windows watch task: unreadable — Task Scheduler did not let this account read its definition (0x80070005)');
});

test('the level of each row: a failing last run, a disabled task and an update warn; running and a normal run are ok; not installed is none', () => {
  const level = (over: Partial<TaskFixture>): string => viewOf(stateOf([task('watch', over)])).tasks[0]?.level ?? '';
  assert.equal(level({ lastResult: '0' }), 'ok');
  assert.equal(level({ lastResult: '75' }), 'ok', 'exit 75: another run held the run lock — normal');
  assert.equal(level({ lastResult: '1' }), 'warn');
  assert.equal(level({ lastResult: '84' }), 'warn');
  assert.equal(level({ enabled: 'False' }), 'warn');
  assert.equal(level({ state: '4' }), 'ok');
  assert.equal(level({ unreadable: '0x80070005' }), 'unknown');
});

test('the last result in words: the daemon\'s exits and Task Scheduler\'s own codes — or the bare code', () => {
  const words = (code: string): string => lineOf(viewOf(stateOf([task('watch', { lastResult: code })])), 'watch');
  assert.match(words('75'), /skipped — another run held the run lock/);
  assert.match(words('81'), /refused — it ran elevated/);
  assert.match(words('84'), /ended at its deadline — its whole tree was ended/);
  assert.match(words('78'), /the configuration is in error \(exit 78\)/);
  assert.match(words('1'), /the run failed \(exit 1\)/);
  assert.match(words('267014'), /stopped by its time limit/);
  assert.match(words('-2147216609'), /skipped — a run of it was still going/);
  assert.match(words('-2147024891'), /result 0x80070005$/);
  assert.match(words('9'), /exit 9$/);
});

test('precedence: unreadable, then disabled, then running, then "install again", then the last run', () => {
  const copyNotStaged: CopyState = { kind: 'notStaged', digest: STAGED_DIGEST };
  assert.match(lineOf(viewOf(stateOf([task('watch', { enabled: 'False', state: '4' })])), 'watch'), /disabled/);
  assert.match(lineOf(viewOf(stateOf([task('watch', { state: '4' })], copyNotStaged)), 'watch'), /running/);
  assert.match(lineOf(viewOf(stateOf([task('watch')], copyNotStaged)), 'watch'), /install again to update/);
});

// ---- plan round 9: never run ----

test('plan round 9: a task that never ran reads "never run" — never undefined, never NaN, whatever its last-run fields held', () => {
  for (const over of [{ lastRunUtc: 'never', lastResult: '267011' }, { lastRunUtc: '', lastResult: '' }, { lastResult: '267011' }, { lastResult: '' }] as const) {
    const line = lineOf(viewOf(stateOf([task('watch', over)])), 'watch');
    assert.equal(line, 'Windows watch task: installed, never run', JSON.stringify(over));
    assert.doesNotMatch(line, /undefined|NaN/);
  }
});

// ---- plan round 1: nothing installed ----

test('plan round 1: on a machine never installed (no \\wsl-care folder) every task reads "not installed" — no failure anywhere', () => {
  const view = viewOf(stateOf([], STAGED, 'absent'));
  assert.deepEqual(view.tasks.map((t) => t.line), ['Windows watch task: not installed', 'Windows archive task: not installed']);
  assert.deepEqual(view.tasks.map((t) => t.level), ['none', 'none']);
});

// ---- plan round 2: not staged ----

test('plan round 2: the copy is not staged — the copy line says so, a registered task reads "this version\'s copy is not staged, install again to update", an absent one "not installed"', () => {
  const view = viewOf(stateOf([task('watch')], { kind: 'notStaged', digest: STAGED_DIGEST }));
  assert.deepEqual(view.copy, { line: 'Windows daemon copy: 0.4.0 (0123456789ab) not staged', level: 'none' });
  assert.equal(lineOf(view, 'watch'), 'Windows watch task: installed — this version\'s copy is not staged, install again to update');
  assert.equal(lineOf(view, 'archive'), 'Windows archive task: not installed');
  assert.equal(view.dry, '', 'no render answered, so no dry state is claimed');
});

// ---- plan round 3: per-task isolation, at the view ----

test('plan round 3: one unreadable task is that task\'s line — the other still reads, the copy line stands', () => {
  const view = viewOf(stateOf([task('watch', { lastResult: 'banana' }), task('archive')]));
  assert.match(lineOf(view, 'watch'), /^Windows watch task: unreadable — its lastResult field is "banana", not a number$/);
  assert.match(lineOf(view, 'archive'), /^Windows archive task: installed, last run /);
  assert.match(view.copy.line, /staged$/);
});

// ---- plan round 5: the copy line ----

test('plan round 5: the copy line is the version the extension carries, the first 12 hex of the digest, and staged or not staged', () => {
  const read = parseSchedulerAnswer(schedulerStdout());
  const line = (copy: CopyState): string => windowsTasksView(windowsTasksState(read, copy), PIN, LOCAL).copy.line;
  assert.equal(line(STAGED), 'Windows daemon copy: 0.4.0 (0123456789ab) staged');
  assert.equal(line({ kind: 'notStaged', digest: 'fedcba9876543210'.repeat(4) }), 'Windows daemon copy: 0.4.0 (fedcba987654) not staged');
  assert.equal(line({ kind: 'checking' }), 'Windows daemon copy: checking…');
  assert.equal(line({ kind: 'unreadable', digest: STAGED_DIGEST, reason: 'it did not answer within 60 s (wslCare.timeouts.windowsTasksStageSeconds)' }), 'Windows daemon copy: 0.4.0 (0123456789ab) could not be checked — it did not answer within 60 s (wslCare.timeouts.windowsTasksStageSeconds)');
  assert.equal(line({ kind: 'digestUnreadable', reason: 'EBUSY' }), 'Windows daemon copy: not known — EBUSY');
  assert.deepEqual(windowsTasksView(windowsTasksState(read, { kind: 'unavailable' }), PIN, LOCAL), NO_WINDOWS_TASKS, 'off Windows nothing is said');
});

test('a staged copy the daemon calls not registrable says so, with the daemon\'s reason, as a warning', () => {
  const bad = render('watch', 'watch from the extension folder');
  const view = viewOf(stateOf([], { kind: 'staged', digest: STAGED_DIGEST, renders: { watch: bad } }));
  assert.equal(bad.kind, 'notRegistrable');
  assert.match(view.copy.line, /^Windows daemon copy: 0\.4\.0 \(0123456789ab\) staged — not registrable: .* is not a staged copy/);
  assert.equal(view.copy.level, 'warn');
});

// ---- the up-to-date compare ----

test('up to date: a registered task whose summary is the one `tasks render` answers (Task Scheduler\'s PT1M for PT60S included) reads as current', () => {
  assert.match(lineOf(viewOf(stateOf([task('watch'), task('archive')])), 'watch'), /^Windows watch task: installed, last run /);
  assert.ok(registered(WATCH).some((l) => l.includes('delay=PT1M')), 'the fixture carries the stored spelling');
  assert.ok(!WATCH.kind.startsWith('not'), 'the golden watch render is registrable');
});

test('up to date: every field of the summary counts — the principal, the interval, the limit and the action each make a task "not as this version would install it"', () => {
  const changes: readonly (readonly [string, (line: string) => string])[] = [
    ['principal', (l) => l.replace(/principal=\S+/, 'principal=S-1-5-21-1-1-1-1001')],
    ['interval', (l) => l.replace('interval=PT5M', 'interval=PT6M')],
    ['limit', (l) => l.replace('limit=PT20M', 'limit=PT21M')],
    ['action', (l) => l.replace('args=tasks run watch --json', 'args=tasks run watch')],
    ['path', (l) => l.replace('wsl-care.exe args', 'other.exe args')],
  ];
  for (const [name, change] of changes) {
    const line = lineOf(viewOf(stateOf([task('watch', { summary: registered(WATCH).map(change) })])), 'watch');
    assert.match(line, /not as this version would install it/, name);
  }
  assert.match(lineOf(viewOf(stateOf([task('watch', { summary: registered(WATCH).slice(0, 3) })])), 'watch'), /not as this version would install it/, 'a missing action line');
});

test('up to date: a render that did not answer for a task leaves it "cannot be compared" — a line of its own, never a failure of the rest', () => {
  const noAnswer: CopyState = { kind: 'staged', digest: STAGED_DIGEST, renders: { watch: WATCH, archive: { kind: 'failed', reason: 'it did not answer within 60 s (wslCare.timeouts.windowsTasksStageSeconds)' } } };
  const view = viewOf(stateOf([task('watch'), task('archive')], noAnswer));
  assert.match(lineOf(view, 'watch'), /installed, last run/);
  assert.equal(lineOf(view, 'archive'), 'Windows archive task: installed — it cannot be compared with this version\'s copy (it did not answer within 60 s (wslCare.timeouts.windowsTasksStageSeconds)), install again to update');
});

// ---- the dry line ----

test('the dry line is the daemon\'s own sentence from a render that answered — and nothing before one did', () => {
  const view = viewOf(stateOf([]));
  assert.equal(view.dry, 'Windows tasks, dry run: the setting dryRun is on; the dry-run week has not started: the first timer or task run starts it');
  assert.equal(viewOf(stateOf([], { kind: 'checking' })).dry, '');
});

// ---- plan round 6, 8: errors are visible lines ----

test('plan round 6: a folder this account cannot read is a line on EACH task naming Task Scheduler\'s HRESULT — never an empty section', () => {
  const view = viewOf(stateOf([], STAGED, { unreadable: '0x80070005' }));
  const sentence = 'unreadable — Task Scheduler does not let this account read it (0x80070005) — installed or not cannot be told';
  assert.deepEqual(view.tasks.map((t) => t.line), [`Windows watch task: ${sentence}`, `Windows archive task: ${sentence}`]);
});

test('plan round 6, 8: a read that did not answer is "unknown" with its reason on each task — and a timeout says it timed out and that its PowerShell was ended', () => {
  const unknown = (read: Parameters<typeof windowsTasksState>[0]): WindowsTasksView => windowsTasksView(windowsTasksState(read, STAGED), PIN, LOCAL);
  const failed = unknown({ kind: 'unknown', cause: 'failed', reason: 'the Task Scheduler query exited 1' });
  assert.equal(lineOf(failed, 'watch'), 'Windows watch task: unknown — the Task Scheduler query exited 1');
  const reason = 'the Task Scheduler query did not answer within 30 s (wslCare.timeouts.windowsTimeGuardQuerySeconds)';
  const timedOut = unknown({ kind: 'unknown', cause: 'timedOut', timeoutMs: 30_000, reason, treeEnded: true });
  assert.equal(lineOf(timedOut, 'archive'), 'Windows archive task: unknown — the Task Scheduler read timed out after 30 s (wslCare.timeouts.windowsTimeGuardQuerySeconds); its PowerShell and everything it started were ended');
  assert.equal(lineOf(unknown({ kind: 'unknown', cause: 'checking', reason: 'checking…' }), 'watch'), 'Windows watch task: checking…');
});

test('the Opus own review M3: the timeout line claims the tree ended only when the runner VERIFIED it — else it says it could not confirm', () => {
  const unknown = (extra: { treeEnded?: boolean }): WindowsTasksView => windowsTasksView(windowsTasksState({ kind: 'unknown', cause: 'timedOut', timeoutMs: 30_000, reason: 'the Task Scheduler query did not answer within 30 s (wslCare.timeouts.windowsTimeGuardQuerySeconds)', ...extra }, STAGED), PIN, LOCAL);
  const unconfirmed = 'Windows watch task: unknown — the Task Scheduler read timed out after 30 s (wslCare.timeouts.windowsTimeGuardQuerySeconds); the extension could not confirm that its PowerShell and everything it started were ended';
  assert.equal(lineOf(unknown({ treeEnded: false }), 'watch'), unconfirmed);
  assert.equal(lineOf(unknown({}), 'watch'), unconfirmed, 'no word from the runner is no confirmation');
  assert.match(lineOf(unknown({ treeEnded: true }), 'watch'), /; its PowerShell and everything it started were ended$/);
});

test('the Opus own review m1: while the copy is still being probed a registered task reads "checking…" — not the update warning', () => {
  const view = viewOf(stateOf([task('watch')], { kind: 'checking' }));
  assert.equal(lineOf(view, 'watch'), 'Windows watch task: checking…');
  assert.equal(view.tasks[0]?.level, 'none');
  assert.match(lineOf(viewOf(stateOf([task('watch', { enabled: 'False' })], { kind: 'checking' })), 'watch'), /disabled/, 'what Task Scheduler already said still holds');
});

test('the Opus own review m5: "(0x41301)" is said only for that code — a task Task Scheduler says is running by its State alone reads "running"', () => {
  assert.equal(lineOf(viewOf(stateOf([task('watch', { state: '4', lastResult: '0' })])), 'watch'), 'Windows watch task: installed, running');
  assert.equal(lineOf(viewOf(stateOf([task('watch', { state: '4', lastResult: '267009' })])), 'watch'), 'Windows watch task: installed, running (0x41301)');
  assert.equal(lineOf(viewOf(stateOf([task('watch', { state: '3', lastResult: '267009' })])), 'watch'), 'Windows watch task: installed, running (0x41301)');
});

test('the Opus own review m2: Task Scheduler\'s result codes have ONE table — the guard\'s words are that table with its own time-limit sentence', () => {
  assert.equal(SCHEDULER_RESULT.notRunYet, 0x41303);
  assert.equal(SCHEDULER_RESULTS[SCHEDULER_RESULT.running], SCHEDULER_RESULT_WORDS[SCHEDULER_RESULT.running]);
  assert.equal(SCHEDULER_RESULTS[SCHEDULER_RESULT.skipped], SCHEDULER_RESULT_WORDS[SCHEDULER_RESULT.skipped]);
  assert.equal(SCHEDULER_RESULTS[SCHEDULER_RESULT.timeLimit], 'stopped by its time limit (wslCare.windowsTime.guard.timeLimitMinutes)');
  assert.match(lineOf(viewOf(stateOf([task('watch', { lastResult: '267014' })])), 'watch'), /: stopped by its time limit$/);
});

test('every word a task or the daemon supplied is made printable and bounded before the panel shows it', () => {
  const hostile = 'x\u202Ey\u0007</script><img src=x onerror=alert(1)>'.repeat(60);
  const view = viewOf(stateOf([task('watch', { unreadable: hostile })], { kind: 'unreadable', digest: STAGED_DIGEST, reason: hostile }));
  for (const line of [view.copy.line, ...view.tasks.map((t) => t.line)]) {
    assert.equal([...line].some((c) => c.charCodeAt(0) < 0x20 || (c.charCodeAt(0) >= 0x202a && c.charCodeAt(0) <= 0x202e)), false, 'a control or a bidi override reached the panel');
    assert.ok(line.length < 1_100, String(line.length));
  }
});

// ---- plan round 10: ONE canonical summary format ----

function goldenAnswers(): readonly { readonly name: string; readonly xml: string; readonly summary: readonly string[] }[] {
  const golden = JSON.parse(fs.readFileSync(RENDER_GOLDEN, 'utf8')) as { answers: { name: string; answer: { xml: string; summary: string[] } }[] };

  return golden.answers.map((a) => ({ name: a.name, xml: a.answer.xml, summary: a.answer.summary }));
}

test('plan round 10: the daemon\'s `tasks render` summary lines (the golden) are what `summariesMatch` expects from SUMMARY_FUNCTION\'s read-back of the golden\'s own XML', () => {
  const answers = goldenAnswers();
  assert.equal(answers.length, 4, 'the scan sees every golden answer');
  for (const { name, xml, summary } of answers) {
    const readBack = summaryOfXml(parseXml(xml));
    assert.equal(summariesMatch(readBack, summary), true, `${name}:\n${readBack.join('\n')}\n--\n${summary.join('\n')}`);
  }
});

test('plan round 10: the same reading holds for the guard — its summary is its own XML read back, so the two tasks\' summaries are ONE format', () => {
  const options = { setAutomaticStart: true, everyHours: 4, minMinutesBetweenStarts: 10, delaySeconds: 60, timeLimitMinutes: 5 };
  assert.equal(summariesMatch(summaryOfXml(parseXml(guardTaskXml(options))), guardSummary(options)), true);
});

test('plan round 10: the reference reading is not vacuous — a golden summary that differs in one field no longer matches', () => {
  const [first] = goldenAnswers();
  assert.ok(first !== undefined);
  const wrong = first.summary.map((l) => l.replace('interval=PT5M', 'interval=PT6M'));
  assert.equal(summariesMatch(summaryOfXml(parseXml(first.xml)), wrong), false);
});

const WINDOWS_ONLY = { skip: process.platform !== 'win32' ? 'Windows PowerShell and Task Scheduler exist on the Windows leg only' : false } as const;

const POWERSHELL = `${process.env.SystemRoot ?? 'C:\\Windows'}\\System32\\WindowsPowerShell\\v1.0\\powershell.exe`;

test('the Windows leg reads back the golden XML as the golden summary: Task Scheduler parses each XML IN MEMORY (nothing registered) and SUMMARY_FUNCTION\'s lines are the golden\'s', WINDOWS_ONLY, () => {
  assert.equal(MUTATING.test(SUMMARY_FUNCTION), false);
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'testsToDelete-wr-e2-'));
  try {
    fs.writeFileSync(path.join(dir, 'summary.ps1'), SUMMARY_FUNCTION, 'utf8');
    for (const { name, xml, summary } of goldenAnswers()) {
      fs.writeFileSync(path.join(dir, 'task.xml'), xml, 'utf8');
      const script = "$ErrorActionPreference = 'Stop'; $s = New-Object -ComObject Schedule.Service; $s.Connect(); $d = $s.NewTask(0); $d.XmlText = [System.IO.File]::ReadAllText((Join-Path $args[0] 'task.xml')); . ([ScriptBlock]::Create([System.IO.File]::ReadAllText((Join-Path $args[0] 'summary.ps1')))); Get-WslCareSummary $d";
      assert.equal(MUTATING.test(script), false, 'a check script may only read');
      const result = childProcess.spawnSync(POWERSHELL, ['-NoProfile', '-NonInteractive', '-Command', `& { ${script} }`, dir], { encoding: 'utf8', timeout: 60_000 });
      assert.equal(result.status, 0, `${name}: ${result.stdout}${result.stderr}`);
      const lines = result.stdout.trim().split(/\r?\n/);
      assert.deepEqual(lines, [...summary], name);
    }
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('the reader\'s own SID is carried by the read, for the install flow\'s comparison with the render\'s userSid', () => {
  const read = parseSchedulerAnswer(schedulerStdout());
  assert.equal(read.kind === 'read' ? read.user : '', USER_SID);
});
