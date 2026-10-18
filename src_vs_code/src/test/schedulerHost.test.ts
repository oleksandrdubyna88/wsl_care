import assert from 'node:assert/strict';
import { test } from 'node:test';

import type { ProcessRequest, ProcessResult } from '../process/runner';
import { SchedulerHost } from '../scheduler/schedulerHost';
import { QUERY_SCRIPT } from '../windowsTime/guardScripts';
import { schedulerStdout } from './support/schedulerAnswers';

/**
 * The ONE read in flight (WR-E2, PLAN_windows_runner.md §3.8 and the story's plan round 4fc3f328, findings 4, 7, 8): nothing is
 * asked until somebody asks, one query runs at a time, the query is bounded by the setting and ends its whole PowerShell tree
 * at the ceiling, and every consumer hears the same answer.
 */

const ENV = { SystemRoot: 'C:\\Windows' } as const;

function exited(stdout: string): ProcessResult {
  return { kind: 'exited', code: 0, stdout: Buffer.from(stdout), stderr: Buffer.alloc(0) };
}

function hostOf(answer: (n: number) => Promise<ProcessResult>, timeoutMs = 30_000): { host: SchedulerHost; requests: ProcessRequest[] } {
  const requests: ProcessRequest[] = [];
  const host = new SchedulerHost({ env: ENV, queryTimeoutMs: () => timeoutMs, script: QUERY_SCRIPT, query: (request) => { requests.push(request); return answer(requests.length); } });

  return { host, requests };
}

test('plan round 4: nothing is read until somebody asks — no query at construction, none on its own afterwards', async () => {
  const { host, requests } = hostOf(() => Promise.resolve(exited(schedulerStdout())));
  await new Promise((resolve) => { setTimeout(resolve, 50); });
  assert.deepEqual(requests, []);
  assert.equal(host.current().kind, 'unknown');
  assert.equal(host.checking(), false);
});

test('one read at a time: three refreshes at once are the flight and ONE more, never three — and every listener hears start and answer', async () => {
  const { host, requests } = hostOf(() => Promise.resolve(exited(schedulerStdout())));
  const heard: boolean[] = [];
  host.onChange(() => { heard.push(host.checking()); });
  await Promise.all([host.refresh(), host.refresh(), host.refresh()]);
  assert.equal(requests.length, 2);
  assert.equal(host.current().kind, 'read');
  assert.equal(heard[0], true, 'told when the query starts');
  assert.equal(heard.at(-1), false, 'and when the last one answered');
  assert.equal(host.checking(), false);
});

test('a refresh asked during a flight is answered by one MORE read, not by the stale one', async () => {
  let release: () => void = () => undefined;
  const gate = new Promise<void>((resolve) => { release = resolve; });
  const { host, requests } = hostOf(async (n) => { if (n === 1) { await gate; } return exited(schedulerStdout({ folder: n === 1 ? 'absent' : 'present' })); });
  const first = host.refresh();
  const second = host.refresh();
  release();
  await Promise.all([first, second]);
  assert.equal(requests.length, 2);
  const read = host.current();
  assert.equal(read.kind === 'read' ? read.folder.kind : '', 'present', 'the newer answer is the one held');
});

test('plan round 7, 8: the query is the fixed script under the setting\'s ceiling — read at every query — and asks the runner for the tree kill', async () => {
  let ceiling = 30_000;
  const requests: ProcessRequest[] = [];
  const host = new SchedulerHost({ env: ENV, queryTimeoutMs: () => ceiling, script: QUERY_SCRIPT, query: (r) => { requests.push(r); return Promise.resolve(exited(schedulerStdout())); } });
  await host.refresh();
  ceiling = 45_000;
  await host.refresh();
  assert.deepEqual(requests.map((r) => r.timeoutMs), [30_000, 45_000]);
  assert.equal(requests[0]?.args[3], QUERY_SCRIPT);
  assert.ok(requests.every((r) => r.killTree === true), 'the PowerShell\'s whole tree is ended at the ceiling');
  assert.equal(requests[0]?.file, 'C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe');
});

test('plan round 8: a read that hit its ceiling says it timed out — the runner\'s own ending, with the ceiling named', async () => {
  const { host } = hostOf(() => Promise.resolve({ kind: 'timedOut', timeoutMs: 30_000, treeEnded: false, stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) }));
  await host.refresh();
  assert.deepEqual(host.current(), { kind: 'unknown', cause: 'timedOut', timeoutMs: 30_000, treeEnded: false, reason: 'the Task Scheduler query did not answer within 30 s (wslCare.timeouts.windowsTimeGuardQuerySeconds)' });
});

test('no SystemRoot: no PowerShell can be named, so the read says why — and starts nothing', async () => {
  const requests: ProcessRequest[] = [];
  const host = new SchedulerHost({ env: {}, queryTimeoutMs: () => 1_000, script: QUERY_SCRIPT, query: (r) => { requests.push(r); return Promise.resolve(exited('')); } });
  await host.refresh();
  assert.deepEqual(requests, []);
  assert.equal(host.current().kind, 'unknown');
  assert.match((host.current() as { reason: string }).reason, /SystemRoot/);
});

test('two consumers, one read: a listener added by each hears the same single query', async () => {
  const { host, requests } = hostOf(() => Promise.resolve(exited(schedulerStdout())));
  const a: string[] = [];
  const b: string[] = [];
  host.onChange(() => { a.push(host.current().kind); });
  host.onChange(() => { b.push(host.current().kind); });
  await host.refresh();
  assert.equal(requests.length, 1);
  assert.deepEqual(a, b);
  assert.equal(a.at(-1), 'read');
});

test('the Opus own review m3: a listener that throws neither stops the ones after it nor rejects the refresh — the read it was told about stands', async () => {
  const { host } = hostOf(() => Promise.resolve(exited(schedulerStdout())));
  let heard = 0;
  host.onChange(() => { throw new Error('a listener\'s own fault'); });
  host.onChange(() => { heard += 1; });
  await host.refresh();
  assert.ok(heard >= 2, 'the second listener heard the start and the answer');
  assert.equal(host.current().kind, 'read');
});