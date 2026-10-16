import assert from 'node:assert/strict';
import { test } from 'node:test';

import { spawnRunner, treeKillRequest } from '../process/runner';

/**
 * WR-E2, plan round 4fc3f328, finding 8: a Task Scheduler read that outlives its ceiling is ended with its whole PROCESS TREE —
 * the runner's own kill, asked for with `killTree`, by the pid of the child it started (never by image name). The pure request is
 * a unit test everywhere; the real tree (a PowerShell child and ITS grandchild, both this test's own helpers, the grandchild ending by itself
 * after 25 s) runs on the Windows leg.
 */

test('the tree kill is taskkill by ABSOLUTE path and by PID — /T for the tree, /F to force — and never by image name', () => {
  const request = treeKillRequest(4321, 'C:\\Windows');
  assert.deepEqual(request, { file: 'C:\\Windows\\System32\\taskkill.exe', args: ['/PID', '4321', '/T', '/F'] });
  assert.ok(!(request?.args ?? []).some((a) => a.toUpperCase() === '/IM'), 'no image-name switch');
});

test('no request is made for a pid or a SystemRoot that cannot be trusted: nothing is guessed, the child alone is killed', () => {
  for (const pid of [0, -1, 1.5, Number.NaN]) {
    assert.equal(treeKillRequest(pid, 'C:\\Windows'), undefined, String(pid));
  }
  for (const root of [undefined, '', 'Windows', '\\\\server\\share', '..\\Windows']) {
    assert.equal(treeKillRequest(4321, root), undefined, String(root));
  }
});

const WINDOWS_ONLY = { skip: process.platform !== 'win32' ? 'taskkill and the tree it ends exist on the Windows leg only' : false } as const;

/**
 * A Windows PowerShell that starts a `ping.exe` grandchild (25 pings, about 24 s: it ends by itself), prints its pid and waits. Not
 * a node child: every node process puts the children it starts into a kill-on-close job (libuv), so a node grandchild dies WITH its
 * parent whatever the runner does, and could not tell a tree kill from a plain one.
 */
const HELPER = "$p = Start-Process -FilePath ping.exe -ArgumentList '-n','25','127.0.0.1' -PassThru -WindowStyle Hidden; 'grandchild=' + $p.Id; Start-Sleep -Seconds 25";

function helperFile(): string {
  return `${process.env.SystemRoot ?? 'C:\\Windows'}\\System32\\WindowsPowerShell\\v1.0\\powershell.exe`;
}

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

async function grandchildOf(killTree: boolean): Promise<number> {
  const result = await spawnRunner({ file: helperFile(), args: ['-NoProfile', '-NonInteractive', '-Command', HELPER], timeoutMs: 8_000, ...(killTree ? { killTree: true } : {}) });
  assert.equal(result.kind, 'timedOut', JSON.stringify(result));
  const printed = result.kind === 'timedOut' ? result.stdout.toString('utf8') : '';
  const pid = Number(/grandchild=(\d+)/.exec(printed)?.[1]);
  assert.ok(Number.isInteger(pid) && pid > 0, 'the helper printed its grandchild\'s pid');

  return pid;
}

test('a run that hit its ceiling with killTree leaves no grandchild behind', WINDOWS_ONLY, async () => {
  const grandchild = await grandchildOf(true);
  assert.equal(await goneWithin(grandchild, 8_000), true, `the grandchild ${grandchild} outlived the run it belonged to`);
});

test('and without killTree the grandchild is still there — the control: the option is what ends the tree', WINDOWS_ONLY, async () => {
  const grandchild = await grandchildOf(false);
  await new Promise((resolve) => { setTimeout(resolve, 500); });
  assert.equal(alive(grandchild), true, 'a plain kill ends the child alone');
});
