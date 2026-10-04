import assert from 'node:assert/strict';
import * as fs from 'node:fs';
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
