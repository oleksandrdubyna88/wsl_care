import assert from 'node:assert/strict';
import * as childProcess from 'node:child_process';
import { test } from 'node:test';

import { EngineHost, ENGINE_QUERY, engineQueryRequest, engineReadOf } from '../powershell/engineHost';
import { MODULES, PWSH_PROBE, TAG_HELPER } from '../powershell/elevatedEngine';
import type { ProcessRequest, ProcessResult } from '../process/runner';
import { isElevatedPowerShell, isMachineChange, MUTATING } from './support/noRealWsl';

/**
 * The engine read (PLAN_pwsh_engine_windows_time.md §11 R3): ONE unelevated, read-only probe of which PowerShell runs elevated,
 * behind a host that every elevated Windows Time flow asks — the last read's engine (reading first when there is none) and a
 * NEW read right before the launch (R6). No test here starts a PowerShell but the Windows-leg one, which only reads.
 */

const ENV = { SystemRoot: 'C:\\Windows', ProgramFiles: 'C:\\Program Files' } as const;
const PS51 = 'C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe';
const STORE = 'C:\\Program Files\\WindowsApps\\Microsoft.PowerShell_7.6.6.0_x64__8wekyb3d8bbwe\\pwsh.exe';
const NO_FILES = (): undefined => undefined;

function exited(stdout: string, code = 0): ProcessResult {
  return { kind: 'exited', code, stdout: Buffer.from(stdout), stderr: Buffer.alloc(0) };
}

const SIGNED_STORE = ['pwsh.programFiles=C:\\Program Files', 'pwsh.store=signed', `pwsh.path=${STORE}`, 'pwsh.msi=absent', 'pwsh.msiPath=C:\\Program Files\\PowerShell\\7\\pwsh.exe'].join('\r\n');

test('the engine query pins the module path, runs the shared probe with progress off, and only reads', () => {
  assert.equal(ENGINE_QUERY, [MODULES, "$ProgressPreference = 'SilentlyContinue'", TAG_HELPER, PWSH_PROBE, 'exit 0'].join('\n'));
  assert.equal(MUTATING.test(ENGINE_QUERY), false);
  const request = engineQueryRequest(ENV, 20_000) as ProcessRequest;
  assert.deepEqual(request, { file: PS51, args: ['-NoProfile', '-NonInteractive', '-Command', ENGINE_QUERY], timeoutMs: 20_000 });
  assert.equal(isElevatedPowerShell(request.file, request.args) || isMachineChange(request.file, request.args), false);
  assert.equal(typeof engineQueryRequest({}, 20_000), 'string', 'no SystemRoot: no request');
});

test('a read that verified the Store package names PowerShell 7 by its path', () => {
  const read = engineReadOf(exited(SIGNED_STORE), ENV, NO_FILES);
  assert.deepEqual(read, { engine: { file: STORE, label: `PowerShell 7 (${STORE})`, kind: 'store' }, failure: '' });
});

test('a read that answered no PowerShell 7 at all names Windows PowerShell 5.1 plainly', () => {
  const read = engineReadOf(exited(['pwsh.programFiles=C:\\Program Files', 'pwsh.store=absent', 'pwsh.path=', 'pwsh.msi=absent', 'pwsh.msiPath=C:\\Program Files\\PowerShell\\7\\pwsh.exe'].join('\n')), ENV, NO_FILES);
  assert.deepEqual(read, { engine: { file: PS51, label: `Windows PowerShell 5.1 (${PS51})`, kind: 'windows' }, failure: '' });
});

test('a read that did not answer, answered nothing, or answered a tag twice runs 5.1 — and its label says PowerShell 7 could not be checked, and why', () => {
  const cases: readonly (readonly [ProcessResult, RegExp])[] = [
    [{ kind: 'timedOut', timeoutMs: 20_000, stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) }, /did not answer within 20 s \(wslCare\.timeouts\.engineProbeSeconds\)/],
    [{ kind: 'failedToStart', reason: 'ENOENT' }, /could not start: ENOENT/],
    [exited('', 1), /exited 1/],
    [exited(''), /printed no answer/],
    [exited(`${SIGNED_STORE}\npwsh.store=unsigned`), /a tag twice \(pwsh\.store\)/],
  ];
  for (const [result, why] of cases) {
    const read = engineReadOf(result, ENV, NO_FILES);
    assert.equal(read.engine.kind, 'windows', JSON.stringify(result));
    assert.equal(read.engine.file, PS51);
    assert.match(read.engine.label, /^Windows PowerShell 5\.1 \(.+\) — PowerShell 7 could not be checked: /);
    assert.match(read.failure, why);
    assert.match(read.engine.label, why);
  }
});

interface Gate {
  readonly host: EngineHost;
  readonly requests: ProcessRequest[];
  readonly release: (stdout: string) => void;
}

/** A host whose query answers only when the test releases it — one pending answer at a time, in order. */
function gated(): Gate {
  const requests: ProcessRequest[] = [];
  const pending: ((result: ProcessResult) => void)[] = [];
  const host = new EngineHost({ env: ENV, fileInfo: NO_FILES, timeoutMs: () => 20_000, query: (request) => { requests.push(request); return new Promise((resolve) => { pending.push(resolve); }); } });

  return { host, requests, release: (stdout) => { pending.shift()?.(exited(stdout)); } };
}

async function settle(): Promise<void> {
  await new Promise((resolve) => setImmediate(resolve));
}

test('engine() reads once when there is no read yet, then answers from the last read without asking again', async () => {
  const g = gated();
  const first = g.host.engine();
  await settle();
  g.release(SIGNED_STORE);
  assert.equal((await first).engine.kind, 'store');
  assert.equal((await g.host.engine()).engine.kind, 'store');
  assert.equal(g.requests.length, 1);
  assert.equal(g.host.last()?.engine.kind, 'store');
});

test('R6: reread() is never answered by a read already in flight — it waits for that one, then asks AGAIN', async () => {
  const g = gated();
  const panel = g.host.refresh();
  await settle();
  const beforeLaunch = g.host.reread();
  await settle();
  assert.equal(g.requests.length, 1, 'the second read starts only after the first answered');
  g.release('pwsh.programFiles=C:\\Program Files\npwsh.store=absent\npwsh.path=\npwsh.msi=absent\npwsh.msiPath=x');
  assert.equal((await panel).engine.kind, 'windows');
  await settle();
  assert.equal(g.requests.length, 2, 'the re-read asked Windows again');
  g.release(SIGNED_STORE);
  assert.equal((await beforeLaunch).engine.kind, 'store', 'the launch sees the NEW read');
});

test('refresh() while a read is in flight shares it — the panel never queues reads', async () => {
  const g = gated();
  const a = g.host.refresh();
  const b = g.host.refresh();
  await settle();
  assert.equal(g.requests.length, 1);
  g.release(SIGNED_STORE);
  assert.deepEqual(await a, await b);
});

test('a listener hears every read that ends, and stops hearing once it unsubscribes', async () => {
  const g = gated();
  let heard = 0;
  const off = g.host.onChange(() => { heard++; });
  const read = g.host.refresh();
  await settle();
  g.release(SIGNED_STORE);
  await read;
  await settle();
  assert.ok(heard >= 1);
  off();
  const before = heard;
  const again = g.host.refresh();
  await settle();
  g.release(SIGNED_STORE);
  await again;
  await settle();
  assert.equal(heard, before);
});

test('the engine query really RUNS on the Windows leg — unelevated, read-only — and answers the probe\'s five tags', { skip: process.platform !== 'win32' ? 'Windows PowerShell exists on the Windows leg only' : false }, () => {
  const result = childProcess.spawnSync(PS51, ['-NoProfile', '-NonInteractive', '-Command', ENGINE_QUERY], { encoding: 'utf8', timeout: 60_000 });
  assert.equal(result.status, 0, `${result.stdout}${result.stderr}`);
  for (const tag of ['pwsh.programFiles=', 'pwsh.store=', 'pwsh.path=', 'pwsh.msi=', 'pwsh.msiPath=']) {
    assert.ok(result.stdout.split(/\r?\n/).some((line) => line.startsWith(tag)), `${tag} in ${result.stdout}`);
  }
  const read = engineReadOf({ kind: 'exited', code: 0, stdout: Buffer.from(result.stdout), stderr: Buffer.alloc(0) }, process.env, () => undefined);
  assert.equal(read.failure, '', read.engine.label);
});

test('coai code round: a PARTIAL probe answer (it faulted after its first verdict, or printed no known folder) is no complete check — 5.1 runs, labelled "could not be checked"', () => {
  const partial = [
    ['pwsh.programFiles=C:\\Program Files', 'pwsh.store=absent', 'pwsh.path='].join('\n'),
    ['pwsh.store=signed', `pwsh.path=${STORE}`, 'pwsh.msi=absent', 'pwsh.msiPath=x'].join('\n'),
  ];
  for (const answer of partial) {
    const read = engineReadOf(exited(answer), ENV, NO_FILES);
    assert.equal(read.engine.kind, 'windows', answer);
    assert.match(read.failure, /printed an incomplete answer/, answer);
    assert.match(read.engine.label, /PowerShell 7 could not be checked: the read printed an incomplete answer/);
  }
});
