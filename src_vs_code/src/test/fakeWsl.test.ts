import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { test } from 'node:test';

import type { ProcessResult } from '../process/runner';
import { FAKE_EXIT, missingBinaryStderr, NO_SUCH_DISTRO, OLD_GLIBC_STDERR } from './fake/fakeWsl';
import { fakeWorld, UBUNTU_RUNNING, type FakeWorld, type ScenarioInput } from './support/fakeWorld';
import { GOLDEN_ROOT } from './support/paths';

/**
 * The strict fake's OWN tests (`common.generated-code-tests` §3: a fake is code under test, and may be stricter than
 * the real thing, never more permissive). Every answer is checked against the MEASURED real one
 * (research/2026-10-03_wsl_exe_facts.md); every refusal against an argv the client must never send. The fake is
 * started exactly as the client starts it: through `nodeScriptRunner`, as `C:\Windows\System32\wsl.exe`.
 */

const WSL = 'C:\\Windows\\System32\\wsl.exe';
const DAEMON_CALL = ['-d', 'Ubuntu', '--cd', '/', '--exec', '/opt/wsl-care/bin/wsl-care'];
/** wsl.exe's -1 as the runner reads it on this platform: -1 on Windows (signed), 255 where exit(-1) truncates. */
const WSL_REFUSAL = process.platform === 'win32' ? -1 : 255;

async function within(input: ScenarioInput, body: (world: FakeWorld) => Promise<void>): Promise<void> {
  const world = fakeWorld(input);
  try {
    await body(world);
  } finally {
    world.dispose();
  }
}

function ask(world: FakeWorld, args: readonly string[], timeoutMs = 15_000, file = WSL): Promise<ProcessResult> {
  return world.runner({ file, args, timeoutMs });
}

function exitOf(result: ProcessResult): { code: number; stdout: Buffer; stderr: string } {
  assert.ok(result.kind === 'exited', JSON.stringify(result));
  return { code: result.code, stdout: result.stdout, stderr: result.stderr.toString('utf8') };
}

test('--list --quiet answers the names in UTF-16LE with CRLF — the measured bytes', async () => {
  await within(UBUNTU_RUNNING, async (world) => {
    const { code, stdout } = exitOf(await ask(world, ['--list', '--quiet']));
    assert.equal(code, 0);
    assert.deepEqual([...stdout.subarray(0, 4)], [0x55, 0x00, 0x62, 0x00]);
    assert.equal(stdout.toString('utf16le'), 'Ubuntu\r\ndocker-desktop\r\n');
  });
});

test('with WSL_UTF8 emulated the list is UTF-8, as measured', async () => {
  await within({ ...UBUNTU_RUNNING, wslUtf8: true }, async (world) => {
    assert.equal(exitOf(await ask(world, ['--list', '--quiet'])).stdout.toString('utf8'), 'Ubuntu\r\ndocker-desktop\r\n');
  });
});

test('-l -v answers the measured table, the default marked with *', async () => {
  await within({ distros: [{ name: 'Ubuntu', running: true }, { name: 'docker-desktop', running: true }, { name: 'Ubuntu-26.04', running: true }], defaultDistro: 'Ubuntu', binary: 'present' }, async (world) => {
    const measured = '  NAME              STATE           VERSION\r\n* Ubuntu            Running         2\r\n  docker-desktop    Running         2\r\n  Ubuntu-26.04      Running         2\r\n';
    assert.equal(exitOf(await ask(world, ['-l', '-v'])).stdout.toString('utf16le'), measured);
  });
});

test('--list --running --quiet lists only the running distributions; with none, nothing — or a sentence and -1', async () => {
  await within({ distros: [{ name: 'Ubuntu', running: false }, { name: 'docker-desktop', running: true }], binary: 'present' }, async (world) => {
    assert.equal(exitOf(await ask(world, ['--list', '--running', '--quiet'])).stdout.toString('utf16le'), 'docker-desktop\r\n');
  });
  await within({ distros: [{ name: 'Ubuntu', running: false }], binary: 'present' }, async (world) => {
    const { code, stdout } = exitOf(await ask(world, ['--list', '--running', '--quiet']));
    assert.deepEqual([code, stdout.length], [0, 0]);
  });
  await within({ distros: [{ name: 'Ubuntu', running: false }], binary: 'present', noneRunning: 'message' }, async (world) => {
    const { code, stdout } = exitOf(await ask(world, ['--list', '--running', '--quiet']));
    assert.equal(code, WSL_REFUSAL);
    assert.match(stdout.toString('utf16le'), /no running distributions/);
  });
});

test('the four read-only verbs answer: the golden bytes for the JSON ones, the version text for --version (the positive)', async () => {
  await within({ ...UBUNTU_RUNNING, version: '0.1.0+abc' }, async (world) => {
    for (const verb of [['status', '--json'], ['preview', '--all', '--json'], ['doctor', '--json']]) {
      const { code, stdout } = exitOf(await ask(world, [...DAEMON_CALL, ...verb]));
      assert.equal(code, 0, verb.join(' '));
      assert.deepEqual(stdout, fs.readFileSync(path.join(GOLDEN_ROOT, 'head', `${verb[0]}.json`)), verb.join(' '));
    }
    assert.equal(exitOf(await ask(world, [...DAEMON_CALL, '--version'])).stdout.toString('utf8'), '0.1.0+abc\n');
    assert.deepEqual(world.files(), [WSL, WSL, WSL, WSL], 'the log names what the runner was asked to start');
  });
});

test('every argv the read-only client must never send is refused, naming why', async () => {
  const never: readonly (readonly string[])[] = [
    ['-d', 'Ubuntu', '-u', 'root', '--cd', '/', '--exec', '/opt/wsl-care/bin/wsl-care', 'status', '--json'],
    ['-d', 'Ubuntu', '--user', 'root', '--exec', '/opt/wsl-care/bin/wsl-care', 'status', '--json'],
    ['-d', 'Ubuntu', '--cd', '/', '--', '/opt/wsl-care/bin/wsl-care', 'status', '--json'],
    ['-d', 'Ubuntu', '--cd', '/', '-e', '/opt/wsl-care/bin/wsl-care', 'status', '--json'],
    [...DAEMON_CALL, 'collect', '--timer'],
    [...DAEMON_CALL, 'collect'],
    [...DAEMON_CALL, 'act', 'A4', '--confirm'],
    [...DAEMON_CALL, 'act', 'A4', '--preview', '--manual', '--json'],
    [...DAEMON_CALL, 'config', 'set', 'dryRun', 'false'],
    [...DAEMON_CALL, 'config', 'reset', 'dryRun'],
    [...DAEMON_CALL, 'status'],
    [...DAEMON_CALL, 'status', '--json', '--manual'],
    [...DAEMON_CALL, 'preview', '--json'],
    [...DAEMON_CALL, 'logs', '--json'],
    ['-d', 'Ubuntu', '--exec', '/opt/wsl-care/bin/wsl-care', 'status', '--json'],
    ['-d', 'Ubuntu', '--cd', '/', '--exec', '/usr/bin/id'],
    ['--shutdown'],
    ['--terminate', 'Ubuntu'],
    ['-l'],
    ['--list'],
  ];
  await within(UBUNTU_RUNNING, async (world) => {
    for (const argv of never) {
      const { code, stderr } = exitOf(await ask(world, argv));
      assert.equal(code, FAKE_EXIT.refused, `${argv.join(' ')} → ${code} ${stderr}`);
      assert.match(stderr, /^fake wsl: refused /, argv.join(' '));
    }
  });
});

test('a distribution not in the list answers as wsl.exe does: -1 and the measured UTF-16LE sentence on stdout', async () => {
  await within(UBUNTU_RUNNING, async (world) => {
    const { code, stdout } = exitOf(await ask(world, ['-d', 'Debian', '--cd', '/', '--exec', '/opt/wsl-care/bin/wsl-care', 'status', '--json']));
    assert.equal(code, WSL_REFUSAL);
    assert.equal(stdout.toString('utf16le'), NO_SUCH_DISTRO);
  });
});

test('a -d to a STOPPED distribution is refused — the real wsl.exe would start the VM', async () => {
  await within({ distros: [{ name: 'Ubuntu', running: false }], binary: 'present' }, async (world) => {
    const { code, stderr } = exitOf(await ask(world, [...DAEMON_CALL, 'status', '--json']));
    assert.equal(code, FAKE_EXIT.wouldStart);
    assert.match(stderr, /would START the stopped distribution/);
  });
});

test('a missing binary answers the measured execvpe signature at exit 1', async () => {
  await within({ ...UBUNTU_RUNNING, binary: 'missing' }, async (world) => {
    const { code, stderr, stdout } = exitOf(await ask(world, [...DAEMON_CALL, '--version']));
    assert.equal(code, 1);
    assert.equal(stdout.length, 0);
    assert.match(stderr, /^<3>WSL \(\d+ - Relay\) ERROR: CreateProcessCommon:818: execvpe\(\/opt\/wsl-care\/bin\/wsl-care\) failed: No such file or directory\n$/);
    assert.equal(missingBinaryStderr(190724), '<3>WSL (190724 - Relay) ERROR: CreateProcessCommon:818: execvpe(/opt/wsl-care/bin/wsl-care) failed: No such file or directory\n');
  });
});

test('an old glibc answers the loader\'s refusal', async () => {
  await within({ ...UBUNTU_RUNNING, binary: 'oldGlibc' }, async (world) => {
    const { code, stderr } = exitOf(await ask(world, [...DAEMON_CALL, 'status', '--json']));
    assert.equal(code, 1);
    assert.equal(stderr, OLD_GLIBC_STDERR);
  });
});

test('a scripted daemon exit writes its stderr and code', async () => {
  await within({ ...UBUNTU_RUNNING, daemonExit: { code: 2, stderr: 'wsl-care: no\n' } }, async (world) => {
    const { code, stderr } = exitOf(await ask(world, [...DAEMON_CALL, 'doctor', '--json']));
    assert.deepEqual([code, stderr], [2, 'wsl-care: no\n']);
  });
});

test('a slow daemon is cut by the runner\'s ceiling', async () => {
  await within({ ...UBUNTU_RUNNING, delayMs: 30_000 }, async (world) => {
    const result = await ask(world, [...DAEMON_CALL, 'status', '--json'], 800);
    assert.equal(result.kind, 'timedOut');
  });
});

test('started as anything but an absolute ...\\System32\\wsl.exe, the fake refuses', async () => {
  await within(UBUNTU_RUNNING, async (world) => {
    for (const file of ['wsl.exe', 'wsl', 'C:\\tools\\wsl.exe', '.\\System32\\wsl.exe', 'C:\\Windows\\System32\\wsl.exe.bat']) {
      assert.equal(exitOf(await ask(world, ['--list', '--quiet'], 15_000, file)).code, FAKE_EXIT.wrongFile, file);
    }
    assert.equal(exitOf(await ask(world, ['--list', '--quiet'], 15_000, 'D:\\Win\\System32\\WSL.EXE')).code, 0, 'another SystemRoot is still System32\\wsl.exe');
  });
});

test('startable: a -d to a stopped distribution starts it, as the real wsl.exe does — answered, and running from then on', async () => {
  await within({ distros: [{ name: 'Ubuntu', running: false }], binary: 'present', startable: true }, async (world) => {
    assert.equal(exitOf(await ask(world, ['--list', '--running', '--quiet'])).stdout.toString('utf16le'), '');
    const { code, stdout } = exitOf(await ask(world, [...DAEMON_CALL, 'status', '--json']));
    assert.equal(code, 0);
    assert.equal(JSON.parse(stdout.toString('utf8')).schemaVersion, 1);
    assert.equal(exitOf(await ask(world, ['--list', '--running', '--quiet'])).stdout.toString('utf16le'), 'Ubuntu\r\n');
  });
});

test('§15h #4: a -d value starting with "-" is refused, naming why — wsl.exe would read it as an option', async () => {
  await within({ distros: [{ name: '-x', running: true }], binary: 'present' }, async (world) => {
    const { code, stderr } = exitOf(await ask(world, ['-d', '-x', '--cd', '/', '--exec', '/opt/wsl-care/bin/wsl-care', 'status', '--json']));
    assert.equal(code, FAKE_EXIT.refused);
    assert.match(stderr, /read by wsl\.exe as an option/);
  });
});

test('§15h #4: a LISTED distribution with a name outside the setting pattern is answered as it is (the positive)', async () => {
  await within({ distros: [{ name: 'Ubuntu+Dev~2', running: true }], defaultDistro: 'Ubuntu+Dev~2', binary: 'present' }, async (world) => {
    const { code } = exitOf(await ask(world, ['-d', 'Ubuntu+Dev~2', '--cd', '/', '--exec', '/opt/wsl-care/bin/wsl-care', 'status', '--json']));
    assert.equal(code, 0);
  });
});

// ---- E6.S2: the root shapes (plan §15j M1 / M2 / m11, §15k #10) ----
//
// The fake answers exactly the five root calls of the closed union — written out HERE, independently of rootCall.ts —
// and refuses, stricter than the real daemon would at the argv level: a SYNCHRONOUS confirm, stdin anywhere but
// `--only -`, an id outside the contract's registry ∩ the scenario daemon's `status.actions`, an op whose capability that
// daemon does not advertise, `--timer` / `--user` / `config` anywhere, and any other order of the words.

const ROOT_CALL = ['-d', 'Ubuntu', '-u', 'root', '--cd', '/', '--exec', '/opt/wsl-care/bin/wsl-care'];
const RUN = '20000101T000000Z-1';

function names(count: number): string[] {
  return Array.from({ length: count }, (_, i) => `${i.toString(16).padStart(8, '0')}${'ab'.repeat(28)}`);
}

function lines(list: readonly string[]): Buffer {
  return Buffer.from(list.map((n) => `${n}\n`).join(''), 'utf8');
}

function rootAsk(world: FakeWorld, tail: readonly string[], stdin?: Buffer): Promise<ProcessResult> {
  return world.runner({ file: WSL, args: [...ROOT_CALL, ...tail], timeoutMs: 15_000, ...(stdin === undefined ? {} : { stdin }) });
}

function json(result: ProcessResult): Record<string, unknown> {
  const { code, stdout, stderr } = exitOf(result);
  assert.equal(code, 0, stderr);
  return JSON.parse(stdout.toString('utf8')) as Record<string, unknown>;
}

test('root: the five shapes of the closed union are answered (the positives)', async () => {
  await within({ ...UBUNTU_RUNNING, version: '0.1.0' }, async (world) => {
    const preview = json(await rootAsk(world, ['act', 'A4', '--preview', '--json']));
    assert.equal((preview.actions as { id: string; shown: string[] }[])[0]?.shown.length, 387);
    assert.equal(json(await rootAsk(world, ['act', 'A4', '--confirm', '--manual', '--detach', '--only', '-', '--json'], lines(names(387)))).result, 'accepted');
    assert.equal(json(await rootAsk(world, ['act', 'A5,A10', '--confirm', '--manual', '--detach', '--json'])).result, 'accepted');
    assert.deepEqual(json(await rootAsk(world, ['act', '--stop', RUN, '--json'])), { schemaVersion: 1, result: 'stopping', kind: 'act', runId: RUN, unit: `wsl-care-act@${RUN}.service`, productVersion: 'unknown' });
    assert.equal(json(await rootAsk(world, ['collect', '--detach', '--json'])).kind, 'collect');
    assert.equal(exitOf(await rootAsk(world, ['--version'])).stdout.toString('utf8'), '0.1.0\n');
  });
});

test('root: a preview of ids beyond the golden\'s A4 answers an entry for each id asked, and only those', async () => {
  await within(UBUNTU_RUNNING, async (world) => {
    const preview = json(await rootAsk(world, ['act', 'A5,A4,A10', '--preview', '--json']));
    assert.deepEqual((preview.actions as { id: string }[]).map((a) => a.id), ['A5', 'A4', 'A10']);
  });
});

test('root: the stdin of an --only - confirm is recorded in the call log, byte for byte', async () => {
  await within(UBUNTU_RUNNING, async (world) => {
    const input = lines(names(3));
    await rootAsk(world, ['act', 'A4', '--confirm', '--manual', '--detach', '--only', '-', '--json'], input);
    assert.deepEqual(world.stdins(), [input.toString('utf8')]);
  });
});

test('root: a SYNCHRONOUS confirm is refused — a confirm is always --detach (plan §15j m11)', async () => {
  await within(UBUNTU_RUNNING, async (world) => {
    for (const tail of [['act', 'A10', '--confirm', '--manual', '--json'], ['act', 'A10', '--confirm', '--json'], ['act', 'A4', '--confirm', '--manual', '--only', '-', '--json']]) {
      const { code, stderr } = exitOf(await rootAsk(world, tail, tail.includes('--only') ? lines(names(1)) : undefined));
      assert.equal(code, FAKE_EXIT.refused, tail.join(' '));
      assert.match(stderr, /synchronous confirm/, tail.join(' '));
    }
  });
});

test('root: stdin anywhere but a confirm\'s --only - is refused', async () => {
  await within(UBUNTU_RUNNING, async (world) => {
    for (const tail of [['act', 'A4', '--preview', '--json'], ['act', 'A10', '--confirm', '--manual', '--detach', '--json'], ['collect', '--detach', '--json'], ['act', '--stop', RUN, '--json'], ['--version']]) {
      const { code, stderr } = exitOf(await rootAsk(world, tail, lines(names(1))));
      assert.equal(code, FAKE_EXIT.refused, tail.join(' '));
      assert.match(stderr, /stdin outside --only -/, tail.join(' '));
    }
  });
});

test('root: an --only - list that is not one 64-hex name per line, or --only - without A4, is refused', async () => {
  await within(UBUNTU_RUNNING, async (world) => {
    const confirmA4 = ['act', 'A4', '--confirm', '--manual', '--detach', '--only', '-', '--json'];
    for (const bad of [Buffer.from('not-a-name\n'), Buffer.from(`${names(1)[0]}`), Buffer.from(`${names(1)[0]} --timer\n`)]) {
      assert.equal(exitOf(await rootAsk(world, confirmA4, bad)).code, FAKE_EXIT.refused, bad.toString());
    }
    assert.equal(exitOf(await rootAsk(world, ['act', 'A10', '--confirm', '--manual', '--detach', '--only', '-', '--json'], lines(names(1)))).code, FAKE_EXIT.refused);
    assert.equal(exitOf(await rootAsk(world, confirmA4, Buffer.alloc(0))).code, 0, 'an empty list binds A4 to nothing — legal');
  });
});

test('root: an id outside the contract registry ∩ the daemon\'s status.actions is refused, naming it', async () => {
  await within(UBUNTU_RUNNING, async (world) => {
    for (const ids of ['A13', 'A99', 'A4,A13', 'a4']) {
      const { code, stderr } = exitOf(await rootAsk(world, ['act', ids, '--preview', '--json']));
      assert.equal(code, FAKE_EXIT.refused, ids);
      assert.match(stderr, /outside the intersection/, ids);
    }
  });
});

test('root: an op whose capability the scenario daemon does not advertise is refused', async () => {
  await within(UBUNTU_RUNNING, async (world) => {
    const answers = path.join(world.folder, 'answers-no-detach');
    fs.mkdirSync(answers);
    for (const file of fs.readdirSync(path.join(GOLDEN_ROOT, 'head'))) {
      fs.copyFileSync(path.join(GOLDEN_ROOT, 'head', file), path.join(answers, file));
    }
    const status = JSON.parse(fs.readFileSync(path.join(answers, 'status.json'), 'utf8')) as { capabilities: string[] };
    fs.writeFileSync(path.join(answers, 'status.json'), JSON.stringify({ ...status, capabilities: status.capabilities.filter((c) => c !== 'act.detach' && c !== 'act.stop') }));
    world.rewrite({ answers });
    for (const tail of [['collect', '--detach', '--json'], ['act', 'A10', '--confirm', '--manual', '--detach', '--json'], ['act', '--stop', RUN, '--json']]) {
      const { code, stderr } = exitOf(await rootAsk(world, tail));
      assert.equal(code, FAKE_EXIT.refused, tail.join(' '));
      assert.match(stderr, /does not advertise/, tail.join(' '));
    }
    assert.equal(exitOf(await rootAsk(world, ['act', 'A4', '--preview', '--json'])).code, 0, 'the preview needs act.shownList, still advertised');
  });
});

test('root: every other root argv is refused — the timer\'s mark, another user, config, a read verb, another order', async () => {
  const never: readonly (readonly string[])[] = [
    [...ROOT_CALL, 'collect', '--timer', '--json'],
    [...ROOT_CALL, 'collect', '--detach', '--timer', '--json'],
    [...ROOT_CALL, 'act', 'A10', '--confirm', '--timer', '--detach', '--json'],
    [...ROOT_CALL, 'act', 'A10', '--confirm', '--detach', '--json'],
    [...ROOT_CALL, 'act', 'A10', '--confirm', '--manual', '--detach'],
    [...ROOT_CALL, 'act', 'A10', '--preview', '--manual', '--json'],
    [...ROOT_CALL, 'act', 'A4', '--confirm', '--manual', '--detach', '--volume', 'ab'.repeat(32), '--json'],
    [...ROOT_CALL, 'act', '--stop', '20000101T000000Z-01', '--json'],
    [...ROOT_CALL, 'act', '--request', RUN],
    [...ROOT_CALL, 'config', 'set', 'dryRun', 'false'],
    [...ROOT_CALL, 'status', '--json'],
    [...ROOT_CALL, 'collect', '--json'],
    ['-d', 'Ubuntu', '-u', 'someone', '--cd', '/', '--exec', '/opt/wsl-care/bin/wsl-care', 'collect', '--detach', '--json'],
    ['-d', 'Ubuntu', '--user', 'root', '--cd', '/', '--exec', '/opt/wsl-care/bin/wsl-care', 'collect', '--detach', '--json'],
    ['-u', 'root', '-d', 'Ubuntu', '--cd', '/', '--exec', '/opt/wsl-care/bin/wsl-care', 'collect', '--detach', '--json'],
    ['-d', 'Ubuntu', '-u', 'root', '--exec', '/opt/wsl-care/bin/wsl-care', 'collect', '--detach', '--json'],
    ['-d', 'Ubuntu', '-u', 'root', '--cd', '/', '--', '/opt/wsl-care/bin/wsl-care', 'collect', '--detach', '--json'],
  ];
  await within(UBUNTU_RUNNING, async (world) => {
    for (const argv of never) {
      const { code, stderr } = exitOf(await ask(world, argv));
      assert.equal(code, FAKE_EXIT.refused, `${argv.join(' ')} → ${code} ${stderr}`);
    }
  });
});

test('root: a scripted detach answers unknown, and a scripted root exit writes its stderr and code', async () => {
  await within({ ...UBUNTU_RUNNING, root: { detach: 'unknown' } }, async (world) => {
    assert.equal(json(await rootAsk(world, ['collect', '--detach', '--json'])).result, 'unknown');
  });
  await within({ ...UBUNTU_RUNNING, root: { exit: { code: 75, stderr: 'wsl-care: busy\n' } } }, async (world) => {
    const { code, stderr } = exitOf(await rootAsk(world, ['collect', '--detach', '--json']));
    assert.deepEqual([code, stderr], [75, 'wsl-care: busy\n']);
    assert.equal(exitOf(await rootAsk(world, ['--version'])).code, 0, 'the root check is scripted apart');
  });
  await within({ ...UBUNTU_RUNNING, root: { checkExit: { code: 1, stderr: 'no\n' } } }, async (world) => {
    assert.equal(exitOf(await rootAsk(world, ['--version'])).code, 1);
  });
});

test('root: a root call to a STOPPED distribution is refused like every -d — a root call never starts the VM', async () => {
  await within({ distros: [{ name: 'Ubuntu', running: false }], binary: 'present' }, async (world) => {
    assert.equal(exitOf(await rootAsk(world, ['collect', '--detach', '--json'])).code, FAKE_EXIT.wouldStart);
  });
});

test('root: S2 — a root call that still carries WSLENV in its environment is refused; a read call may carry it', async () => {
  await within(UBUNTU_RUNNING, async (world) => {
    const withEnv = (args: readonly string[]): Promise<ProcessResult> => world.runner({ file: WSL, args, timeoutMs: 15_000, env: { WSLENV: 'PATH/l' } });
    const { code, stderr } = exitOf(await withEnv([...ROOT_CALL, '--version']));
    assert.equal(code, FAKE_EXIT.refused);
    assert.match(stderr, /WSLENV reached a root call/);
    assert.equal(exitOf(await withEnv([...DAEMON_CALL, 'status', '--json'])).code, 0);
  });
});

// ---- E6.S3: the two run reads — unprivileged, their values the daemon's own shapes ----

test('run reads: runs show answers the scenario\'s runs-show file with the asked run id; runs answers its window (the positives)', async () => {
  await within({ ...UBUNTU_RUNNING, runsShow: 'runs-show-done.json' }, async (world) => {
    const asked = '20261005T100000Z-77';
    const show = json(await ask(world, [...DAEMON_CALL, 'runs', 'show', asked, '--json']));
    assert.equal(show.state, 'done');
    assert.equal(show.runId, asked, 'the asked run id is the one answered');
    assert.equal((show.run as { runId: string }).runId, asked);
    const runs = json(await ask(world, [...DAEMON_CALL, 'runs', '--from', '2026-10-05T10:00:00Z', '--to', '2026-10-05T10:05:00Z', '--json']));
    assert.equal(runs.count, 3);
  });
  await within(UBUNTU_RUNNING, async (world) => {
    assert.equal(json(await ask(world, [...DAEMON_CALL, 'runs', 'show', RUN, '--json'])).state, 'unknown', 'without a scenario file: unknown, as the daemon answers a run it never saw');
  });
});

test('run reads: every other shape is refused — a bad run id, an instant without its offset, a window ending first, -u, --period, extra words', async () => {
  const never: readonly (readonly string[])[] = [
    [...DAEMON_CALL, 'runs', 'show', '20000101T000000Z-01', '--json'],
    [...DAEMON_CALL, 'runs', 'show', RUN],
    [...DAEMON_CALL, 'runs', 'show', RUN, '--json', '--detail'],
    [...DAEMON_CALL, 'runs', '--from', '2026-10-05', '--to', '2026-10-06', '--json'],
    [...DAEMON_CALL, 'runs', '--from', '2026-10-05T10:00:00', '--to', '2026-10-05T11:00:00', '--json'],
    [...DAEMON_CALL, 'runs', '--from', '2026-10-05T11:00:00Z', '--to', '2026-10-05T10:00:00Z', '--json'],
    [...DAEMON_CALL, 'runs', '--period', 'today', '--json'],
    [...DAEMON_CALL, 'runs', '--json'],
    [...DAEMON_CALL, 'runs', 'log', RUN, '--json'],
    [...ROOT_CALL, 'runs', 'show', RUN, '--json'],
  ];
  await within(UBUNTU_RUNNING, async (world) => {
    for (const argv of never) {
      const { code, stderr } = exitOf(await ask(world, argv));
      assert.equal(code, FAKE_EXIT.refused, `${argv.join(' ')} → ${code} ${stderr}`);
    }
  });
});

test('logs (E6.S4): logs --from --to --json answers the scenario\'s logs file — logs-local-day.json by default (the positives)', async () => {
  await within(UBUNTU_RUNNING, async (world) => {
    const logs = json(await ask(world, [...DAEMON_CALL, 'logs', '--from', '2026-10-04T21:00:00Z', '--to', '2026-10-05T21:00:00Z', '--json']));
    assert.equal(logs.freedBytes, 308003000);
  });
  await within({ ...UBUNTU_RUNNING, logs: 'runs-local-day.json' }, async (world) => {
    const answered = json(await ask(world, [...DAEMON_CALL, 'logs', '--from', '2026-10-04T21:00:00Z', '--to', '2026-10-05T21:00:00Z', '--json']));
    assert.equal(answered.count, 3, 'the scenario names the file');
  });
});

test('§15o: the fake carries a line\'s kind through unchanged — runs show (whose run id it rewrites) and runs', async () => {
  const folder = fs.mkdtempSync(path.join(os.tmpdir(), 'wsl-care-kind-'));
  try {
    const done = JSON.parse(fs.readFileSync(path.join(GOLDEN_ROOT, 'head', 'runs-show-done.json'), 'utf8')) as { run: Record<string, unknown> };
    fs.writeFileSync(path.join(folder, 'runs-show-kind.json'), JSON.stringify({ ...done, run: { ...done.run, kind: 'collect', actions: [] } }));
    fs.writeFileSync(path.join(folder, 'runs-kind.json'), JSON.stringify({ schemaVersion: 1, count: 1, runs: [{ ...done.run, kind: 'act' }] }));
    await within({ ...UBUNTU_RUNNING, answers: folder, runsShow: 'runs-show-kind.json', runs: 'runs-kind.json' }, async (world) => {
      const show = json(await ask(world, [...DAEMON_CALL, 'runs', 'show', '20261005T100000Z-77', '--json']));
      assert.deepEqual([(show.run as { kind: string }).kind, (show.run as { runId: string }).runId], ['collect', '20261005T100000Z-77']);
      const runs = json(await ask(world, [...DAEMON_CALL, 'runs', '--from', '2026-10-05T10:00:00Z', '--to', '2026-10-05T10:05:00Z', '--json']));
      assert.equal((runs.runs as { kind: string }[])[0]?.kind, 'act');
    });
  } finally {
    fs.rmSync(folder, { recursive: true, force: true });
  }
});

test('logs (E6.S4): every other shape is refused — --period, --detail, --action, a bare day, a window ending first, -u, no --json', async () => {
  const from = '2026-10-04T21:00:00Z';
  const to = '2026-10-05T21:00:00Z';
  const never: readonly (readonly string[])[] = [
    [...DAEMON_CALL, 'logs', '--period', 'today', '--json'],
    [...DAEMON_CALL, 'logs', '--from', from, '--to', to, '--json', '--detail'],
    [...DAEMON_CALL, 'logs', '--from', from, '--to', to, '--action', 'A4', '--json'],
    [...DAEMON_CALL, 'logs', '--from', '2026-10-05', '--to', '2026-10-06', '--json'],
    [...DAEMON_CALL, 'logs', '--from', to, '--to', from, '--json'],
    [...DAEMON_CALL, 'logs', '--from', from, '--to', to],
    [...DAEMON_CALL, 'logs', '--json'],
    [...ROOT_CALL, 'logs', '--from', from, '--to', to, '--json'],
  ];
  await within(UBUNTU_RUNNING, async (world) => {
    for (const argv of never) {
      const { code, stderr } = exitOf(await ask(world, argv));
      assert.equal(code, FAKE_EXIT.refused, `${argv.join(' ')} → ${code} ${stderr}`);
    }
  });
});

test('run reads: a run read to a STOPPED distribution is refused like every -d', async () => {
  await within({ ...UBUNTU_RUNNING, distros: [{ name: 'Ubuntu', running: false }] }, async (world) => {
    assert.equal(exitOf(await ask(world, [...DAEMON_CALL, 'runs', 'show', RUN, '--json'])).code, FAKE_EXIT.wouldStart);
  });
});
