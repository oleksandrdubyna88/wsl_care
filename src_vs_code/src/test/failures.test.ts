import assert from 'node:assert/strict';
import { test } from 'node:test';

import { DAEMON_EXIT, WSL_EXE_FAILED } from '../client/exitCodes';
import { classifyExit, daemonMessages, GLIBC_MISSING, notInstalledSignature, launchFailure } from '../client/failures';
import { missingBinaryStderr, NO_SUCH_DISTRO, OLD_GLIBC_STDERR } from './fake/fakeWsl';

/**
 * How a non-zero ending is read (plan §15g M6). The one collision the plan predicted is real: a missing
 * `/opt/wsl-care/bin/wsl-care` exits 1 — the daemon's own `RunFailed` — so "not installed" is read ONLY from the
 * measured signature, for exactly our path; every other 1 is an unknown failure with the daemon's own lines.
 */

const DAEMON = '/opt/wsl-care/bin/wsl-care';
const none = Buffer.alloc(0);
const utf8 = (s: string): Buffer => Buffer.from(s, 'utf8');

/** The measured stderr of 2026-10-03, byte for byte but the relay pid. */
const MEASURED_MISSING = '<3>WSL (190724 - Relay) ERROR: CreateProcessCommon:818: execvpe(/opt/wsl-care/bin/wsl-care) failed: No such file or directory\n';

/** What the daemon's console sink writes before a refusal: Serilog lines with colour (E1.S3 found it on stderr). */
const SERILOG_NOISE = '\u001b[90m[19:02:11 \u001b[0m\u001b[33mINF\u001b[0m\u001b[90m]\u001b[0m WslCare.Cli: request status --json\n';

test('the measured missing-binary signature, and only for our path at exit 1, is "not installed"', () => {
  assert.equal(missingBinaryStderr(190724), MEASURED_MISSING, 'the fake writes the measured line');
  assert.match(MEASURED_MISSING.trimEnd(), notInstalledSignature(DAEMON));
  assert.deepEqual(classifyExit(DAEMON_EXIT.runFailed, none, utf8(MEASURED_MISSING), 'Ubuntu', DAEMON), { kind: 'notInstalled', distro: 'Ubuntu' });
});

test('a missing OTHER path, a permission refusal or the signature at another exit code is not "not installed"', () => {
  const other = '<3>WSL (1 - Relay) ERROR: CreateProcessCommon:818: execvpe(/opt/other/bin/tool) failed: No such file or directory\n';
  const denied = '<3>WSL (1 - Relay) ERROR: CreateProcessCommon:818: execvpe(/opt/wsl-care/bin/wsl-care) failed: Permission denied\n';
  assert.equal(classifyExit(1, none, utf8(other), 'Ubuntu', DAEMON).kind, 'unknownFailure');
  assert.equal(classifyExit(1, none, utf8(denied), 'Ubuntu', DAEMON).kind, 'unknownFailure');
  assert.equal(classifyExit(2, none, utf8(MEASURED_MISSING), 'Ubuntu', DAEMON).kind, 'refused');
});

test('the daemon\'s own exit 1 (RunFailed) is an unknown failure carrying only its wsl-care: lines', () => {
  const result = classifyExit(DAEMON_EXIT.runFailed, none, utf8(`${SERILOG_NOISE}wsl-care: could not record the run: disk full\n`), 'Ubuntu', DAEMON);
  assert.deepEqual(result, { kind: 'unknownFailure', code: 1, messages: ['wsl-care: could not record the run: disk full'] });
});

test('a binary needing a newer glibc is an unsupported distribution, whatever the exit code', () => {
  assert.match(OLD_GLIBC_STDERR, GLIBC_MISSING);
  for (const code of [1, 127]) {
    const result = classifyExit(code, none, utf8(OLD_GLIBC_STDERR), 'Ubuntu-22.04', DAEMON);
    assert.equal(result.kind, 'unsupportedDistro', String(code));
    assert.ok(result.kind === 'unsupportedDistro');
    assert.equal(result.distro, 'Ubuntu-22.04');
    assert.match(result.detail, /GLIBC_2\.38/);
  }
});

test('exit 2 is a refusal showing only the wsl-care: lines, colour stripped, Serilog lines dropped', () => {
  const stderr = `${SERILOG_NOISE}\u001b[31mwsl-care: unknown option --nope\u001b[0m\n${SERILOG_NOISE}`;
  assert.deepEqual(classifyExit(DAEMON_EXIT.usage, none, utf8(stderr), 'Ubuntu', DAEMON), { kind: 'refused', messages: ['wsl-care: unknown option --nope'] });
});

test('exit 70 is a defect in the daemon, exit 130 an interrupted run', () => {
  assert.deepEqual(classifyExit(DAEMON_EXIT.internal, none, utf8('wsl-care: internal error: boom\n'), 'Ubuntu', DAEMON), { kind: 'internalDefect', messages: ['wsl-care: internal error: boom'] });
  assert.deepEqual(classifyExit(DAEMON_EXIT.interrupted, none, none, 'Ubuntu', DAEMON), { kind: 'interrupted' });
});

test('wsl.exe refusing (-1, its UTF-16LE sentence on STDOUT — measured) is a WSL failure with that sentence', () => {
  const result = classifyExit(WSL_EXE_FAILED, Buffer.from(NO_SUCH_DISTRO, 'utf16le'), none, 'Nope', DAEMON);
  assert.deepEqual(result, { kind: 'wslFailed', message: 'There is no distribution with the supplied name. Error code: Wsl/Service/WSL_E_DISTRO_NOT_FOUND' });
});

test('any other code is an unknown failure with the code and the daemon lines only', () => {
  assert.deepEqual(classifyExit(75, none, utf8('noise\nwsl-care: busy\n'), 'Ubuntu', DAEMON), { kind: 'unknownFailure', code: 75, messages: ['wsl-care: busy'] });
  assert.deepEqual(classifyExit(9, none, none, 'Ubuntu', DAEMON), { kind: 'unknownFailure', code: 9, messages: [] });
});

test('only lines that START with wsl-care: are the daemon\'s message — a log line mentioning it is not', () => {
  assert.deepEqual(daemonMessages('[INF] wsl-care: starting\nwsl-care: refused\r\n  wsl-care: indented\n\u001b[1mwsl-care: bold\u001b[0m'), ['wsl-care: refused', 'wsl-care: bold']);
});

// ---- E6.S2 review L1: launchFailure is THE reading of a launcher ending that is not an exit ----

test('L1: launchFailure reads every non-exit ending, the timeout by its parameter', () => {
  const empty = { stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) };
  assert.deepEqual(launchFailure({ kind: 'failedToStart', reason: 'ENOENT' }, 'daemonCall'), { kind: 'wslFailed', message: 'wsl.exe could not be started: ENOENT' });
  assert.deepEqual(launchFailure({ kind: 'tooMuchOutput', stream: 'stdout', limitBytes: 9 }, 'wslQuestion'), { kind: 'unparseable', detail: 'the answer exceeded 9 bytes on stdout' });
  assert.deepEqual(launchFailure({ kind: 'timedOut', timeoutMs: 5, ...empty }, 'daemonCall'), { kind: 'timedOut', timeoutMs: 5 });
  assert.deepEqual(launchFailure({ kind: 'timedOut', timeoutMs: 5, ...empty }, 'wslQuestion'), { kind: 'wslFailed', message: 'wsl.exe did not answer within 5 ms' });
  assert.deepEqual(launchFailure({ kind: 'signalled', signal: 'SIGTERM', ...empty }, 'daemonCall'), { kind: 'unknownFailure', code: undefined, messages: ['ended by SIGTERM'] });
});
