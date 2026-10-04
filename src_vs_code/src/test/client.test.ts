import assert from 'node:assert/strict';
import { test } from 'node:test';

import type { VerbOutcome } from '../client/outcome';
import { VERB_NAMES, VERB_TIMEOUT_MS, VERBS } from '../client/verbs';
import { LIST_TIMEOUT_MS, WslCareClient } from '../client/WslCareClient';
import type { ProcessResult } from '../process/runner';
import { TEST_ENV } from './support/fakeWorld';
import { golden } from './support/paths';
import { daemonArgv, exited, exitedUtf16, LIST_QUIET, LIST_RUNNING, LIST_VERBOSE, recordingRunner, verboseTable, type Scripted } from './support/recordingRunner';

/**
 * `WslCareClient` against a recording runner that starts nothing: the exact argv it builds, the questions it asks WSL
 * first, what it refuses before any spawn, and that a stopped distribution is never touched with `-d`.
 */

const WSL = 'C:\\Windows\\System32\\wsl.exe';
const STATUS = JSON.stringify({ ...golden('head', 'status'), productVersion: '0.1.0' });

function wslAnswers(names: readonly string[], running: readonly string[], defaultName: string | undefined): Record<string, Scripted> {
  return {
    [LIST_QUIET]: exitedUtf16(0, names.map((n) => `${n}\r\n`).join('')),
    [LIST_VERBOSE]: exitedUtf16(0, verboseTable(names.map((name) => ({ name, running: running.includes(name) })), defaultName)),
    [LIST_RUNNING]: exitedUtf16(0, running.map((n) => `${n}\r\n`).join('')),
  };
}

function client(script: Record<string, Scripted>, distro = '', platform = 'win32'): { c: WslCareClient; rec: ReturnType<typeof recordingRunner> } {
  const rec = recordingRunner(script);
  return { c: new WslCareClient({ runner: rec.runner, platform, env: TEST_ENV, distroSetting: () => distro }), rec };
}

function failureKind(outcome: VerbOutcome): string {
  return outcome.kind;
}

test('a status call asks WSL for the list, the default and the running set, then runs exactly the one daemon argv', async () => {
  const { c, rec } = client({ ...wslAnswers(['Ubuntu', 'docker-desktop'], ['Ubuntu'], 'Ubuntu'), [daemonArgv('Ubuntu', VERBS.status)]: exited(0, STATUS) });
  const outcome = await c.run('status');
  assert.equal(outcome.kind, 'answered', JSON.stringify(outcome));
  assert.deepEqual(rec.argvs(), [LIST_QUIET, LIST_VERBOSE, LIST_RUNNING, '-d Ubuntu --cd / --exec /opt/wsl-care/bin/wsl-care status --json']);
  assert.ok(rec.requests.every((r) => r.file === WSL), 'every call starts the absolute System32 wsl.exe');
  assert.equal(rec.requests[3]?.timeoutMs, VERB_TIMEOUT_MS.status);
  assert.ok(rec.requests.slice(0, 3).every((r) => r.timeoutMs === LIST_TIMEOUT_MS));
});

test('each verb sends exactly its closed tail after --exec /opt/wsl-care/bin/wsl-care, with its own ceiling', async () => {
  for (const verb of VERB_NAMES) {
    const answer = verb === 'version' ? exited(0, '0.1.0\n') : exited(0, verb === 'status' ? STATUS : JSON.stringify(golden('head', verb)));
    const { c, rec } = client({ ...wslAnswers(['Ubuntu'], ['Ubuntu'], 'Ubuntu'), [daemonArgv('Ubuntu', VERBS[verb])]: answer, [daemonArgv('Ubuntu', VERBS.version)]: exited(0, '0.1.0\n') }, 'Ubuntu');
    const outcome = await c.run(verb);
    assert.equal(outcome.kind, 'answered', `${verb}: ${JSON.stringify(outcome)}`);
    const daemonCalls = rec.requests.filter((r) => r.args[0] === '-d');
    assert.equal(daemonCalls[0]?.args.join(' '), daemonArgv('Ubuntu', VERBS[verb]), verb);
    assert.equal(daemonCalls[0]?.timeoutMs, VERB_TIMEOUT_MS[verb], verb);
  }
});

test('a configured distribution skips the default lookup and is used as given', async () => {
  const { c, rec } = client({ ...wslAnswers(['Ubuntu', 'Ubuntu-26.04'], ['Ubuntu', 'Ubuntu-26.04'], 'Ubuntu'), [daemonArgv('Ubuntu-26.04', VERBS.status)]: exited(0, STATUS) }, 'Ubuntu-26.04');
  const outcome = await c.run('status');
  assert.equal(outcome.kind, 'answered');
  assert.deepEqual(rec.argvs(), [LIST_QUIET, LIST_RUNNING, daemonArgv('Ubuntu-26.04', VERBS.status)]);
});

test('an out-of-pattern distribution is refused before ANY spawn', async () => {
  for (const bad of ['-u', '--user root', 'Ubuntu; rm -rf /', '$(id)', 'a b']) {
    const { c, rec } = client({}, bad);
    const outcome = await c.run('status');
    assert.equal(failureKind(outcome), 'distroRefused', bad);
    assert.equal(rec.requests.length, 0, `${bad}: nothing may be started`);
  }
});

test('a distribution wsl.exe --list does not report is refused before any -d call', async () => {
  const { c, rec } = client(wslAnswers(['Ubuntu'], ['Ubuntu'], 'Ubuntu'), 'Debian');
  const outcome = await c.run('status');
  assert.deepEqual(outcome, { kind: 'distroRefused', verb: 'status', distro: 'Debian', reason: 'wsl.exe --list does not report it' });
  assert.deepEqual(rec.argvs(), [LIST_QUIET]);
});

test('matching is exact: a differently-cased name is not the listed one', async () => {
  const { c, rec } = client(wslAnswers(['Ubuntu'], ['Ubuntu'], 'Ubuntu'), 'ubuntu');
  assert.equal(failureKind(await c.run('status')), 'distroRefused');
  assert.ok(rec.argvs().every((a) => !a.startsWith('-d ')));
});

test('a STOPPED distribution gets no -d call at all — polling never starts the VM', async () => {
  const { c, rec } = client(wslAnswers(['Ubuntu', 'docker-desktop'], ['docker-desktop'], 'Ubuntu'));
  const outcome = await c.run('status');
  assert.deepEqual(outcome, { kind: 'stopped', verb: 'status', distro: 'Ubuntu' });
  assert.ok(rec.argvs().every((a) => !a.startsWith('-d ')), rec.argvs().join(' | '));
});

test('when the running check itself fails, the distribution is treated as stopped — still no -d call', async () => {
  const { c, rec } = client({ ...wslAnswers(['Ubuntu'], [], 'Ubuntu'), [LIST_RUNNING]: exitedUtf16(-1, 'There are no running distributions.\r\n') });
  assert.equal(failureKind(await c.run('status')), 'stopped');
  assert.ok(rec.argvs().every((a) => !a.startsWith('-d ')));
});

test('no default marked and nothing configured: no distribution is guessed', async () => {
  const { c, rec } = client(wslAnswers(['Ubuntu', 'Debian'], ['Ubuntu'], undefined));
  assert.equal(failureKind(await c.run('status')), 'noDefaultDistro');
  assert.ok(rec.argvs().every((a) => !a.startsWith('-d ')));
});

test('a default whose name fails the pattern is refused like a configured one', async () => {
  const { c, rec } = client(wslAnswers(['-u'], ['-u'], '-u'));
  assert.equal(failureKind(await c.run('status')), 'distroRefused');
  assert.ok(rec.argvs().every((a) => !a.startsWith('-d ')));
});

test('off Windows nothing is started; without SystemRoot nothing is started', async () => {
  const off = client({}, '', 'linux');
  assert.deepEqual(await off.c.run('status'), { kind: 'notWindows', verb: 'status', platform: 'linux' });
  assert.equal(off.rec.requests.length, 0);
  const rec = recordingRunner({});
  const noRoot = new WslCareClient({ runner: rec.runner, platform: 'win32', env: {}, distroSetting: () => '' });
  assert.equal(failureKind(await noRoot.run('status')), 'wslMissing');
  assert.equal(rec.requests.length, 0);
});

test('wsl.exe failing to list is a WSL failure, and nothing further is asked', async () => {
  const { c, rec } = client({ [LIST_QUIET]: { kind: 'failedToStart', reason: 'ENOENT' } });
  const outcome = await c.run('status');
  assert.deepEqual(outcome, { kind: 'wslFailed', verb: 'status', message: 'wsl.exe could not be started: ENOENT' });
  assert.equal(rec.requests.length, 1);
});

test('wsl.exe refusing one of its own questions is a WSL failure carrying its UTF-16LE sentence, whatever the exit code', async () => {
  for (const code of [-1, 1]) {
    const { c } = client({ [LIST_QUIET]: exitedUtf16(code, 'The Windows Subsystem for Linux is not installed.\r\n') });
    assert.deepEqual(await c.run('status'), { kind: 'wslFailed', verb: 'status', message: 'The Windows Subsystem for Linux is not installed.' }, String(code));
  }
});

test('wsl.exe not answering one of its own questions within its ceiling is a WSL failure naming the ceiling', async () => {
  const { c } = client({ [LIST_QUIET]: { kind: 'timedOut', timeoutMs: LIST_TIMEOUT_MS, stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) } });
  assert.deepEqual(await c.run('status'), { kind: 'wslFailed', verb: 'status', message: `wsl.exe did not answer within ${LIST_TIMEOUT_MS} ms` });
});

test('a --version that could not be read is not remembered: the next verb asks again', async () => {
  let versionCalls = 0;
  const script = {
    ...wslAnswers(['Ubuntu'], ['Ubuntu'], 'Ubuntu'),
    [daemonArgv('Ubuntu', VERBS.doctor)]: exited(0, JSON.stringify(golden('head', 'doctor'))),
    [daemonArgv('Ubuntu', VERBS.version)]: (): ProcessResult => {
      versionCalls += 1;
      return versionCalls === 1 ? { kind: 'timedOut', timeoutMs: 1, stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) } : exited(0, '0.1.0\n');
    },
  };
  const { c } = client(script);
  const first = await c.run('doctor');
  assert.ok(first.kind === 'answered');
  assert.deepEqual(first.daemonVersion, { kind: 'notRead', reason: 'timedOut' }, 'an unread version renders (the schema still guards the shape)');
  const second = await c.run('doctor');
  assert.ok(second.kind === 'answered');
  assert.deepEqual(second.daemonVersion, { kind: 'release', text: '0.1.0', parts: [0, 1, 0] });
  assert.equal(versionCalls, 2);
});

test('a timed-out daemon call reports the verb\'s ceiling', async () => {
  const timedOut: ProcessResult = { kind: 'timedOut', timeoutMs: VERB_TIMEOUT_MS.doctor, stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) };
  const { c } = client({ ...wslAnswers(['Ubuntu'], ['Ubuntu'], 'Ubuntu'), [daemonArgv('Ubuntu', VERBS.doctor)]: timedOut });
  assert.deepEqual(await c.run('doctor'), { kind: 'timedOut', verb: 'doctor', timeoutMs: VERB_TIMEOUT_MS.doctor });
});

test('one call per verb in flight: a second run of the same verb shares the first; another verb runs alongside', async () => {
  let release: (r: ProcessResult) => void = () => undefined;
  const gate = new Promise<ProcessResult>((resolve) => { release = resolve; });
  const { c, rec } = client({ ...wslAnswers(['Ubuntu'], ['Ubuntu'], 'Ubuntu'), [daemonArgv('Ubuntu', VERBS.status)]: () => gate, [daemonArgv('Ubuntu', VERBS.version)]: exited(0, '0.1.0\n') });
  const first = c.run('status');
  const second = c.run('status');
  const version = await c.run('version');
  assert.equal(version.kind, 'answered');
  release(exited(0, STATUS));
  const [a, b] = await Promise.all([first, second]);
  assert.equal(a, b, 'the same outcome object: one call, two callers');
  assert.equal(rec.argvs().filter((x) => x === daemonArgv('Ubuntu', VERBS.status)).length, 1);
  const third = await c.run('status');
  assert.equal(third.kind, 'answered');
  assert.equal(rec.argvs().filter((x) => x === daemonArgv('Ubuntu', VERBS.status)).length, 2, 'a call after the first ended runs again');
});

test('the status answer is judged against its own productVersion — no --version call', async () => {
  const { c, rec } = client({ ...wslAnswers(['Ubuntu'], ['Ubuntu'], 'Ubuntu'), [daemonArgv('Ubuntu', VERBS.status)]: exited(0, STATUS) });
  const outcome = await c.run('status');
  assert.ok(outcome.kind === 'answered');
  assert.deepEqual(outcome.daemonVersion, { kind: 'release', text: '0.1.0', parts: [0, 1, 0] });
  assert.ok(!rec.argvs().includes(daemonArgv('Ubuntu', VERBS.version)));
});

test('a status from a daemon that predates productVersion asks --version once, and a released 0.0.9 blanks the view', async () => {
  const old = JSON.stringify({ schemaVersion: 1, side: 'wsl' });
  const { c, rec } = client({ ...wslAnswers(['Ubuntu'], ['Ubuntu'], 'Ubuntu'), [daemonArgv('Ubuntu', VERBS.status)]: exited(0, old), [daemonArgv('Ubuntu', VERBS.version)]: exited(0, '0.0.9\n') });
  assert.deepEqual(await c.run('status'), { kind: 'daemonTooOld', verb: 'status', version: '0.0.9', minimum: '0.1.0' });
  assert.equal(rec.argvs().filter((x) => x === daemonArgv('Ubuntu', VERBS.version)).length, 1);
});

test('preview and doctor reuse the version status already read; with none known they ask --version once', async () => {
  const script = { ...wslAnswers(['Ubuntu'], ['Ubuntu'], 'Ubuntu'), [daemonArgv('Ubuntu', VERBS.doctor)]: exited(0, JSON.stringify(golden('head', 'doctor'))), [daemonArgv('Ubuntu', VERBS.preview)]: exited(0, JSON.stringify(golden('head', 'preview'))), [daemonArgv('Ubuntu', VERBS.version)]: exited(0, 'unknown\n'), [daemonArgv('Ubuntu', VERBS.status)]: exited(0, STATUS) };
  const cold = client(script);
  const doctor = await cold.c.run('doctor');
  assert.ok(doctor.kind === 'answered');
  assert.deepEqual(doctor.daemonVersion, { kind: 'unstamped' }, 'unknown renders (plan §6)');
  await cold.c.run('preview');
  assert.equal(cold.rec.argvs().filter((x) => x === daemonArgv('Ubuntu', VERBS.version)).length, 1, 'the version is asked once per distribution');
  const warm = client(script);
  await warm.c.run('status');
  await warm.c.run('doctor');
  assert.equal(warm.rec.argvs().filter((x) => x === daemonArgv('Ubuntu', VERBS.version)).length, 0, 'status already said which daemon it is');
});

test('an unknown schemaVersion blanks ONLY that verb: preview refused, status and doctor still answered', async () => {
  const preview2 = JSON.stringify({ ...golden('head', 'preview'), schemaVersion: 2 });
  const { c } = client({ ...wslAnswers(['Ubuntu'], ['Ubuntu'], 'Ubuntu'), [daemonArgv('Ubuntu', VERBS.status)]: exited(0, STATUS), [daemonArgv('Ubuntu', VERBS.preview)]: exited(0, preview2), [daemonArgv('Ubuntu', VERBS.doctor)]: exited(0, JSON.stringify(golden('head', 'doctor'))) });
  assert.equal((await c.run('status')).kind, 'answered');
  assert.deepEqual(await c.run('preview'), { kind: 'needsNewerExtension', verb: 'preview', schemaVersion: 2 });
  assert.equal((await c.run('doctor')).kind, 'answered');
});

test('the distribution is read at every call: a setting changed between calls is honoured', async () => {
  let setting = 'Ubuntu';
  const rec = recordingRunner({ ...wslAnswers(['Ubuntu', 'Debian'], ['Ubuntu', 'Debian'], 'Ubuntu'), [daemonArgv('Ubuntu', VERBS.status)]: exited(0, STATUS), [daemonArgv('Debian', VERBS.status)]: exited(0, STATUS) });
  const c = new WslCareClient({ runner: rec.runner, platform: 'win32', env: TEST_ENV, distroSetting: () => setting });
  await c.run('status');
  setting = 'Debian';
  const outcome = await c.run('status');
  assert.ok(outcome.kind === 'answered');
  assert.equal(outcome.distro, 'Debian');
});

test('startIfStopped: a stopped distribution gets the ONE -d the user asked for ("Start WSL and check"), still after the running check', async () => {
  const { c, rec } = client({ ...wslAnswers(['Ubuntu'], [], 'Ubuntu'), [daemonArgv('Ubuntu', VERBS.status)]: exited(0, STATUS) });
  const outcome = await c.run('status', { startIfStopped: true });
  assert.equal(outcome.kind, 'answered', JSON.stringify(outcome));
  assert.deepEqual(rec.argvs(), [LIST_QUIET, LIST_VERBOSE, LIST_RUNNING, daemonArgv('Ubuntu', VERBS.status)]);
});

test('startIfStopped never lifts the distribution checks: an unlisted or out-of-pattern distribution is still refused before any -d', async () => {
  const unlisted = client({ ...wslAnswers(['Ubuntu'], [], 'Ubuntu') }, 'Debian');
  assert.equal(failureKind(await unlisted.c.run('status', { startIfStopped: true })), 'distroRefused');
  assert.ok(unlisted.rec.argvs().every((a) => !a.startsWith('-d ')));
  const shaped = client({}, '-u');
  assert.equal(failureKind(await shaped.c.run('status', { startIfStopped: true })), 'distroRefused');
  assert.deepEqual(shaped.rec.argvs(), []);
});

test('a poll in flight is not shared with a start the user asked for — the two are separate calls', async () => {
  let release: () => void = () => undefined;
  const gate = new Promise<void>((resolve) => { release = resolve; });
  const { c, rec } = client({
    ...wslAnswers(['Ubuntu'], [], 'Ubuntu'),
    [LIST_RUNNING]: async () => { await gate; return exitedUtf16(0, ''); },
    [daemonArgv('Ubuntu', VERBS.status)]: exited(0, STATUS),
  });
  const poll = c.run('status');
  const start = c.run('status', { startIfStopped: true });
  release();
  assert.equal((await poll).kind, 'stopped');
  assert.equal((await start).kind, 'answered');
  assert.equal(rec.argvs().filter((a) => a.startsWith('-d ')).length, 1);
});
