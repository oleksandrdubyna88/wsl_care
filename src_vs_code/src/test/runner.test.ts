import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import * as fs from 'node:fs';
import { createRequire } from 'node:module';
import * as os from 'node:os';
import * as path from 'node:path';
import { test } from 'node:test';

import { CLOSED_REASON, closedRunner, KILL_GRACE_MS, nodeScriptRunner, OUTPUT_LIMITS, signed32, spawnRunner, type ProcessResult } from '../process/runner';

/**
 * The runner seam — the ONLY module that imports `child_process` (structure.test.ts). Driven against `node -e` children
 * (never `wsl.exe`: the tripwire forbids it), because what is tested is the launcher: argv passed verbatim with no
 * shell, bytes kept as bytes, every wait bounded and the child killed when it is not.
 */

const node = process.execPath;

function script(body: string, timeoutMs = 10_000): Promise<ProcessResult> {
  return spawnRunner({ file: node, args: ['-e', body], timeoutMs });
}

function alive(pid: number): boolean {
  try {
    process.kill(pid, 0);
    return true;
  } catch {
    return false;
  }
}

test('an exit hands back its code and both streams as BYTES, untouched', async () => {
  const result = await script("process.stdout.write(Buffer.from('U\\u0000b\\u0000', 'latin1')); process.stderr.write('caf\\u00e9'); process.exit(3)");
  assert.equal(result.kind, 'exited');
  assert.ok(result.kind === 'exited');
  assert.equal(result.code, 3);
  assert.deepEqual([...result.stdout], [0x55, 0x00, 0x62, 0x00], 'UTF-16LE bytes arrive as they were written');
  assert.equal(result.stderr.toString('utf8'), 'café');
});

test('argv reaches the child verbatim — no shell expands, splits or quotes it', async () => {
  const tricky = ['$HOME', '%PATH%', 'a b', '"quoted"', '&& echo pwned', "it's", '*', '-u root'];
  const result = await spawnRunner({ file: node, args: ['-e', 'process.stdout.write(JSON.stringify(process.argv.slice(1)))', ...tricky], timeoutMs: 10_000 });
  assert.ok(result.kind === 'exited', JSON.stringify(result));
  assert.deepEqual(JSON.parse(result.stdout.toString('utf8')), tricky);
});

test('a child past its ceiling is reported timed out within the ceiling plus the kill grace, and is dead afterwards', async () => {
  const started = Date.now();
  const result = await script('process.stdout.write(String(process.pid)); setTimeout(() => {}, 60_000)', 700);
  const elapsed = Date.now() - started;
  assert.equal(result.kind, 'timedOut', JSON.stringify(result));
  assert.ok(result.kind === 'timedOut');
  assert.equal(result.timeoutMs, 700);
  assert.ok(elapsed < 700 + KILL_GRACE_MS + 3_000, `the runner waited ${elapsed} ms`);
  const pid = Number(result.stdout.toString('utf8'));
  assert.ok(pid > 0, 'the child printed its pid before hanging');
  assert.equal(alive(pid), false, `the timed-out child ${pid} is still running`);
});

test('a child that floods stdout past the cap is killed and reported, not buffered without bound', async () => {
  const result = await script(`const b = Buffer.alloc(1024 * 1024, 120); for (let i = 0; i < ${OUTPUT_LIMITS.stdout / (1024 * 1024) + 2}; i++) process.stdout.write(b);`, 60_000);
  assert.deepEqual(result, { kind: 'tooMuchOutput', stream: 'stdout', limitBytes: OUTPUT_LIMITS.stdout });
});

test('a child that floods stderr past its cap is killed and reported', async () => {
  const result = await script(`process.stderr.write(Buffer.alloc(${OUTPUT_LIMITS.stderr + 4096}, 120)); setTimeout(() => {}, 60_000)`, 60_000);
  assert.deepEqual(result, { kind: 'tooMuchOutput', stream: 'stderr', limitBytes: OUTPUT_LIMITS.stderr });
});

test('a program that does not exist fails to start, as an answer rather than an exception', async () => {
  const result = await spawnRunner({ file: 'C:\\no\\such\\program-e5s1.exe', args: [], timeoutMs: 5_000 });
  assert.equal(result.kind, 'failedToStart');
});

test('extra variables reach the child on top of the inherited environment', async () => {
  const result = await spawnRunner({
    file: node,
    args: ['-e', 'process.stdout.write(JSON.stringify([process.env.WSL_CARE_E5S1_PROBE, typeof process.env.PATH]))'],
    timeoutMs: 10_000,
    env: { WSL_CARE_E5S1_PROBE: 'seen' },
  });
  assert.ok(result.kind === 'exited', JSON.stringify(result));
  assert.deepEqual(JSON.parse(result.stdout.toString('utf8')), ['seen', 'string']);
});

test('an exit code Windows reports unsigned reads as the signed 32-bit value wsl.exe meant', () => {
  assert.equal(signed32(4294967295), -1, 'wsl.exe refusing: measured as 4294967295');
  assert.equal(signed32(0), 0);
  assert.equal(signed32(2), 2);
  assert.equal(signed32(130), 130);
  assert.equal(signed32(2147483647), 2147483647);
  assert.equal(signed32(2147483648), -2147483648);
});

test('the script runner starts node with the script first, the argv after it, and tells the script what was asked for', async () => {
  const folder = fs.mkdtempSync(path.join(os.tmpdir(), 'wsl-care-runner-'));
  try {
    const script = path.join(folder, 'echo.js');
    fs.writeFileSync(script, 'process.stdout.write(JSON.stringify([process.env.WSL_CARE_FAKE_REQUESTED_FILE, process.env.ELECTRON_RUN_AS_NODE, process.env.WSL_CARE_E5S1_EXTRA, process.argv.slice(2)]));');
    const runner = nodeScriptRunner(script, { WSL_CARE_E5S1_EXTRA: 'x' });
    const result = await runner({ file: 'C:\\Windows\\System32\\wsl.exe', args: ['--list', '--quiet', '$HOME'], timeoutMs: 10_000 });
    assert.ok(result.kind === 'exited', JSON.stringify(result));
    assert.deepEqual(JSON.parse(result.stdout.toString('utf8')), ['C:\\Windows\\System32\\wsl.exe', '1', 'x', ['--list', '--quiet', '$HOME']]);
  } finally {
    fs.rmSync(folder, { recursive: true, force: true });
  }
});

test('the closed runner starts nothing and says why', async () => {
  const raw = createRequire(__filename)('node:child_process') as Record<string, unknown>;
  const original = raw.spawn;
  let spawned = 0;
  raw.spawn = (...args: unknown[]): unknown => {
    spawned += 1;
    return (original as (...a: unknown[]) => unknown)(...args);
  };
  try {
    const result = await closedRunner({ file: 'C:\\Windows\\System32\\wsl.exe', args: ['--list', '--quiet'], timeoutMs: 1_000 });
    assert.deepEqual(result, { kind: 'failedToStart', reason: CLOSED_REASON });
    assert.equal(spawned, 0);
  } finally {
    raw.spawn = original;
  }
});

// ---- E6.S2 (plan §15j M2): the runner seam takes an optional stdin — written, then ENDED ----

/** A child that reads stdin to its end and prints how many bytes arrived and their SHA-256. */
const STDIN_DIGEST = "const h = require('node:crypto').createHash('sha256'); let n = 0; process.stdin.on('data', (c) => { n += c.length; h.update(c); }); process.stdin.on('end', () => process.stdout.write(JSON.stringify([n, h.digest('hex')])));";

/** 10 000 anonymous-volume names, one per line — the most A4's shown list may carry (650 000 bytes, research facts row 20). */
function tenThousandNames(): Buffer {
  const lines = Array.from({ length: 10_000 }, (_, i) => `${i.toString(16).padStart(8, '0')}${'ab'.repeat(28)}\n`);
  return Buffer.from(lines.join(''), 'utf8');
}

test('stdin reaches the child byte for byte and is ENDED: 650 000 bytes arrive whole and the child sees its end', async () => {
  const input = tenThousandNames();
  assert.equal(input.length, 650_000);
  const result = await spawnRunner({ file: node, args: ['-e', STDIN_DIGEST], timeoutMs: 10_000, stdin: input });
  assert.ok(result.kind === 'exited', JSON.stringify(result));
  const expected = createHash('sha256').update(input).digest('hex');
  assert.deepEqual(JSON.parse(result.stdout.toString('utf8')), [650_000, expected]);
});

test('without stdin the child reads an immediate end — nothing is held open for it', async () => {
  const result = await spawnRunner({ file: node, args: ['-e', STDIN_DIGEST], timeoutMs: 10_000 });
  assert.ok(result.kind === 'exited', JSON.stringify(result));
  assert.deepEqual(JSON.parse(result.stdout.toString('utf8')), [0, createHash('sha256').digest('hex')]);
});

test('a child that exits without reading its stdin still gives its answer — the unread input is no crash', async () => {
  const result = await spawnRunner({ file: node, args: ['-e', 'process.exit(2)'], timeoutMs: 10_000, stdin: tenThousandNames() });
  assert.ok(result.kind === 'exited', JSON.stringify(result));
  assert.equal(result.code, 2);
});

test('the script runner hands stdin to the script it starts in place of the requested program', async () => {
  const folder = fs.mkdtempSync(path.join(os.tmpdir(), 'wsl-care-runner-'));
  try {
    const script = path.join(folder, 'digest.js');
    fs.writeFileSync(script, STDIN_DIGEST);
    const input = Buffer.from('a\nb\n', 'utf8');
    const result = await nodeScriptRunner(script)({ file: 'C:\\Windows\\System32\\wsl.exe', args: ['--version'], timeoutMs: 10_000, stdin: input });
    assert.ok(result.kind === 'exited', JSON.stringify(result));
    assert.deepEqual(JSON.parse(result.stdout.toString('utf8')), [4, createHash('sha256').update(input).digest('hex')]);
  } finally {
    fs.rmSync(folder, { recursive: true, force: true });
  }
});

// ---- E6.S2 review S2: a request can take variables OUT of the inherited environment (WSLENV, for every root call) ----

test('S2: withoutEnv removes a variable from the inherited environment — even when the request itself sets it', async () => {
  const before = process.env.WSLENV;
  process.env.WSLENV = 'PATH/l:USERPROFILE/p';
  try {
    const probe = ['-e', 'process.stdout.write(JSON.stringify([process.env.WSLENV ?? null, typeof process.env.PATH]))'];
    const kept = await spawnRunner({ file: node, args: probe, timeoutMs: 10_000 });
    assert.ok(kept.kind === 'exited');
    assert.deepEqual(JSON.parse(kept.stdout.toString('utf8')), ['PATH/l:USERPROFILE/p', 'string'], 'without withoutEnv it is inherited (the subject is present)');
    const stripped = await spawnRunner({ file: node, args: probe, timeoutMs: 10_000, env: { WSLENV: 'x' }, withoutEnv: ['WSLENV'] });
    assert.ok(stripped.kind === 'exited');
    assert.deepEqual(JSON.parse(stripped.stdout.toString('utf8')), [null, 'string'], 'gone, and the rest of the environment kept');
  } finally {
    if (before === undefined) {
      delete process.env.WSLENV;
    } else {
      process.env.WSLENV = before;
    }
  }
});
