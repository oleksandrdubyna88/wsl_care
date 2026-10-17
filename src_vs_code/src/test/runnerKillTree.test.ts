import assert from 'node:assert/strict';
import * as childProcess from 'node:child_process';
import { EventEmitter } from 'node:events';
import { test } from 'node:test';

import { Run, spawnRunner, type ProcessResult } from '../process/runner';
import { endTree, treeKillRequest, type Killer, type TreeKillDeps, type TreeKillRequest } from '../process/treeKill';

/**
 * WR-E2, plan round 4fc3f328 finding 8 and the Opus own review B1, M2, M3: a Task Scheduler read that outlives its ceiling is ended
 * with its whole PROCESS TREE — by the runner's own kill, asked for with `killTree`, and under the family kill rule: the processes of
 * THIS host's own child only, each judged by pid AND creation time, never by image name. The tree kill is ONE fixed, verifying
 * PowerShell started by its absolute path: it confirms the root's parent is this host and that it started after this host did, walks
 * the descendants keeping a child only when it was created no earlier than its parent, stops each by id and READS BACK that none is
 * left. The pure request is a unit test everywhere; the real tree (a PowerShell child and ITS grandchild, both this test's own
 * helpers, the grandchild ending by itself after ~44 s) runs on the Windows leg.
 */

const ENV = { SystemRoot: 'C:\\Windows' } as const;
const POWERSHELL = 'C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe';

function request(rootPid = 4321, hostPid = 100, startedAtMs = 1_760_000_000_000): TreeKillRequest {
  const made = treeKillRequest(ENV, rootPid, hostPid, startedAtMs);
  assert.ok(made !== undefined, 'a request is made');

  return made;
}

test('the tree kill is ONE fixed PowerShell by absolute path — the verifying script is the command, the three numbers its only variable parts', () => {
  const made = request();
  assert.equal(made.file, POWERSHELL);
  assert.deepEqual(made.args.slice(0, 3), ['-NoProfile', '-NonInteractive', '-Command']);
  const script = made.args[3] ?? '';
  assert.equal(made.args.length, 4);
  for (const word of ['$originId = 4321', '$hostId = 100', 'FromUnixTimeMilliseconds(1760000000000)']) {
    assert.ok(script.includes(word), word);
  }
  const other = request(7, 8, 9).args[3] ?? '';
  assert.equal(other.replaceAll('$originId = 7', '$originId = 4321').replaceAll('$hostId = 8', '$hostId = 100').replaceAll('FromUnixTimeMilliseconds(9)', 'FromUnixTimeMilliseconds(1760000000000)'), script, 'nothing else differs');
});

test('the script verifies before it ends anything: the root is a child of THIS host that started after it, each descendant no older than its parent, ended by id — and read back', () => {
  const script = request().args[3] ?? '';
  const at = (text: string): number => script.indexOf(text);
  const order = ['ParentProcessId -ne $hostId', 'CreationDate.ToUniversalTime() -lt $notBefore', '$_.CreationDate -ge $parent.CreationDate', 'Stop-Process -Id $p.ProcessId', 'survivors'].map(at);
  assert.ok(order.every((i) => i >= 0), `every step present: ${JSON.stringify(order)}`);
  assert.deepEqual([...order].sort((a, b) => a - b), order, 'the root is judged, then the walk, then the stop, then the read-back');
  assert.doesNotMatch(script, /taskkill|\/IM\b|-Name\b|Get-Process|Stop-Process -Name/i, 'never by image name');
  assert.match(script, /Stop-Process -Id \$p\.ProcessId -Force/);
  assert.match(script, /exit 0\s*$/);
});

test('no request is made for a pid, host pid or start time that cannot be trusted — nothing is guessed', () => {
  for (const pid of [0, -1, 1.5, Number.NaN]) {
    assert.equal(treeKillRequest(ENV, pid, 100, 1_760_000_000_000), undefined, `root ${pid}`);
    assert.equal(treeKillRequest(ENV, 4321, pid, 1_760_000_000_000), undefined, `host ${pid}`);
  }
  for (const at of [0, -5, Number.NaN, Number.POSITIVE_INFINITY, 1.5]) {
    assert.equal(treeKillRequest(ENV, 4321, 100, at), undefined, `start ${at}`);
  }
  assert.equal(treeKillRequest(ENV, 100, 100, 1_760_000_000_000), undefined, 'the host is never its own root');
  assert.equal(treeKillRequest({}, 4321, 100, 1_760_000_000_000), undefined, 'no SystemRoot, no PowerShell to name');
});

test('the script is a string literal of the shipped bundle, which the publication scan reads word by word: it holds none of the words that scan forbids outside the root module (the full run found "$root")', () => {
  const script = request().args[3] ?? '';
  assert.doesNotMatch(script, /\broot\b/i, 'bundleScan: no region but the root module spells the word root');
  assert.doesNotMatch(script, /(^|\s)config(\s|$)|(^|\s)-u(\s|=|$)|--(user|timer|confirm|manual|preview|detach|only|process|entry|stop|request)\b/);
});

// ---- endTree: the guard, the verdict, the bound ----

class FakeKiller extends EventEmitter implements Killer {
  stopped = 0;

  kill(): boolean {
    this.stopped += 1;

    return true;
  }
}

function deps(killer: FakeKiller, requests: TreeKillRequest[], boundMs = 5_000): TreeKillDeps {
  return { env: ENV, hostPid: 100, hostStartedAtMs: 1_760_000_000_000, boundMs, spawnKiller: (r) => { requests.push(r); return killer; } };
}

const RUNNING = { pid: 4321, exitCode: null, signalCode: null } as const;

test('the Opus own review B1: a root that has already EXITED (or was signalled, or has no pid) is never tree-killed — its pid may be another process\'s by now — and no request is made', async () => {
  for (const root of [{ pid: 4321, exitCode: 0, signalCode: null }, { pid: 4321, exitCode: null, signalCode: 'SIGTERM' as const }, { pid: undefined, exitCode: null, signalCode: null }]) {
    const requests: TreeKillRequest[] = [];
    assert.equal(await endTree(root, deps(new FakeKiller(), requests)), false, JSON.stringify(root));
    assert.deepEqual(requests, []);
  }
});

test('a running root: ONE killer, its exit 0 is the verdict "ended"; any other exit, or a killer that cannot start, is "not ended"', async () => {
  const ok = new FakeKiller();
  const requests: TreeKillRequest[] = [];
  const verdict = endTree(RUNNING, deps(ok, requests));
  ok.emit('exit', 0);
  assert.equal(await verdict, true);
  assert.equal(requests.length, 1);
  for (const code of [3, 4, 5, 7, null]) {
    const killer = new FakeKiller();
    const failed = endTree(RUNNING, deps(killer, []));
    killer.emit('exit', code);
    assert.equal(await failed, false, `exit ${code}`);
  }
  const broken = new FakeKiller();
  const noStart = endTree(RUNNING, deps(broken, []));
  broken.emit('error', new Error('spawn UNKNOWN'));
  assert.equal(await noStart, false);
});

test('the Opus own review M2: a killer that hangs is stopped at its bound and the tree counts as not ended — the runner never waits on it forever', async () => {
  const hung = new FakeKiller();
  assert.equal(await endTree(RUNNING, deps(hung, [], 30)), false);
  assert.equal(hung.stopped, 1, 'the hung killer was stopped');
});

// ---- Run: stop() after exit, order of kills, the verdict on the result ----

interface FakeChild extends EventEmitter {
  pid: number;
  exitCode: number | null;
  signalCode: NodeJS.Signals | null;
  killed: number;
  kill(): boolean;
}

function fakeChild(events: string[] = []): FakeChild {
  const child = new EventEmitter() as FakeChild;
  child.pid = 4321;
  child.exitCode = null;
  child.signalCode = null;
  child.killed = 0;
  child.kill = (): boolean => { child.killed += 1; events.push('child.kill'); return true; };

  return child;
}

function treeEndedOf(results: readonly ProcessResult[]): readonly unknown[] {
  return results.map((r) => (r.kind === 'timedOut' ? ('treeEnded' in r ? r.treeEnded : 'absent') : 'not a timeout'));
}

test('the Opus own review B1: after the child emitted exit — its pipes still open, the ceiling firing — stop() makes NO tree-kill request, kills the child plainly and says the tree was not ended', async () => {
  const child = fakeChild();
  const requests: TreeKillRequest[] = [];
  const results: ProcessResult[] = [];
  const run = new Run(child, (r) => { results.push(r); }, { killTree: true, treeKill: deps(new FakeKiller(), requests) });
  child.exitCode = 0;
  child.emit('exit', 0, null);
  run.timedOut(1_000);
  run.ended(0, null);
  await new Promise((resolve) => { setTimeout(resolve, 20); });
  assert.deepEqual(requests, []);
  assert.equal(child.killed, 1);
  assert.deepEqual(treeEndedOf(results), [false]);
});

test('the Opus own review M3: a run that ended its tree reports treeEnded true on the timeout — only AFTER the killer\'s verdict, the child killed plainly after it (the walk starts from the child, so it must still be there)', async () => {
  const events: string[] = [];
  const child = fakeChild(events);
  const killer = new FakeKiller();
  const results: ProcessResult[] = [];
  const run = new Run(child, (r) => { results.push(r); }, { killTree: true, treeKill: { ...deps(killer, []), spawnKiller: () => { events.push('killer started'); return killer; } } });
  run.timedOut(1_000);
  assert.deepEqual(events, ['killer started'], 'the tree is asked for first; the child is still there');
  run.ended(null, 'SIGTERM');
  await new Promise((resolve) => { setTimeout(resolve, 20); });
  assert.deepEqual(results, [], 'the result waits for the verdict: it cannot say "ended" before it is known');
  killer.emit('exit', 0);
  await new Promise((resolve) => { setTimeout(resolve, 20); });
  assert.deepEqual(events, ['killer started', 'child.kill']);
  assert.deepEqual(treeEndedOf(results), [true]);
});

test('a killer that did not verify the tree leaves treeEnded false on the timeout; no killTree asked leaves it absent', async () => {
  const child = fakeChild();
  const killer = new FakeKiller();
  const results: ProcessResult[] = [];
  const run = new Run(child, (r) => { results.push(r); }, { killTree: true, treeKill: deps(killer, []) });
  run.timedOut(1_000);
  killer.emit('exit', 7);
  run.ended(null, 'SIGTERM');
  await new Promise((resolve) => { setTimeout(resolve, 20); });
  const plain = fakeChild();
  const plainResults: ProcessResult[] = [];
  const plainRun = new Run(plain, (r) => { plainResults.push(r); }, { killTree: false, treeKill: deps(new FakeKiller(), []) });
  plainRun.timedOut(1_000);
  plainRun.ended(null, 'SIGTERM');
  await new Promise((resolve) => { setTimeout(resolve, 20); });
  assert.deepEqual(treeEndedOf(results), [false]);
  assert.deepEqual(treeEndedOf(plainResults), ['absent']);
  assert.equal(plain.killed, 1);
});

// ---- the real tree, on the Windows leg ----

const WINDOWS_ONLY = { skip: process.platform !== 'win32' ? 'a Windows PowerShell, CIM and the tree it ends exist on the Windows leg only' : false } as const;

test('the verifying script is parsed by PowerShell itself without an error — read only, never executed', WINDOWS_ONLY, () => {
  const script = request().args[3] ?? '';
  const parse = "$e = $null; $t = $null; [System.Management.Automation.Language.Parser]::ParseInput($args[0], [ref]$t, [ref]$e) | Out-Null; if ($e.Count -gt 0) { Write-Output $e[0].Message; exit 1 }; exit 0";
  const result = childProcess.spawnSync(POWERSHELL, ['-NoProfile', '-NonInteractive', '-Command', `& { ${parse} }`, script], { encoding: 'utf8', timeout: 60_000 });
  assert.equal(result.status, 0, `${result.stdout}${result.stderr}`);
});

/**
 * A Windows PowerShell that starts a `ping.exe` grandchild (45 pings, about 44 s: it ends by itself), prints its pid and waits. Not
 * a node child: every node process puts the children it starts into a kill-on-close job (libuv), so a node grandchild dies WITH its
 * parent whatever the runner does, and could not tell a tree kill from a plain one.
 */
const HELPER = "$p = Start-Process -FilePath ping.exe -ArgumentList '-n','45','127.0.0.1' -PassThru -WindowStyle Hidden; 'grandchild=' + $p.Id; Start-Sleep -Seconds 45";

function alive(pid: number): boolean {
  try {
    process.kill(pid, 0);
    return true;
  } catch (e) {
    return (e as NodeJS.ErrnoException).code === 'EPERM';
  }
}

async function goneWithin(pid: number, ms: number): Promise<boolean> {
  const until = Date.now() + ms;
  while (alive(pid) && Date.now() < until) {
    await new Promise((resolve) => { setTimeout(resolve, 100); });
  }

  return !alive(pid);
}

async function ranOut(killTree: boolean): Promise<{ readonly grandchild: number; readonly result: ProcessResult }> {
  const result = await spawnRunner({ file: POWERSHELL, args: ['-NoProfile', '-NonInteractive', '-Command', HELPER], timeoutMs: 15_000, ...(killTree ? { killTree: true } : {}) });
  assert.equal(result.kind, 'timedOut', JSON.stringify(result));
  const printed = result.kind === 'timedOut' ? result.stdout.toString('utf8') : '';
  const grandchild = Number(/grandchild=(\d+)/.exec(printed)?.[1]);
  assert.ok(Number.isInteger(grandchild) && grandchild > 0, 'the helper printed its grandchild\'s pid');

  return { grandchild, result };
}

test('a run that hit its ceiling with killTree leaves no grandchild behind — and its result says the tree was VERIFIED ended', WINDOWS_ONLY, async () => {
  const { grandchild, result } = await ranOut(true);
  assert.equal(await goneWithin(grandchild, 8_000), true, `the grandchild ${grandchild} outlived the run it belonged to`);
  assert.deepEqual(treeEndedOf([result]), [true]);
});

test('and without killTree the grandchild is still there and no verdict is given — the control: the option is what ends the tree', WINDOWS_ONLY, async () => {
  const { grandchild, result } = await ranOut(false);
  await new Promise((resolve) => { setTimeout(resolve, 500); });
  assert.equal(alive(grandchild), true, 'a plain kill ends the child alone');
  assert.deepEqual(treeEndedOf([result]), ['absent']);
});
