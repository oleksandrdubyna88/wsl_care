import assert from 'node:assert/strict';
import { test } from 'node:test';

import { isDistroName, parseDefaultDistro, parseQuietList, parseVerboseTable } from '../wsl/distros';
import { wslExecutable } from '../wsl/wslExecutable';
import { decodeWslText, stripAnsi, textLines } from '../wsl/wslText';
import { verboseTable } from './support/recordingRunner';

/**
 * What `wsl.exe` says, read the way it says it — every fixture here is a MEASURED shape (2026-10-03, WSL 2.7.10.0,
 * research/2026-10-03_wsl_exe_facts.md), not a guess.
 */

const MEASURED_QUIET = Buffer.from('Ubuntu\r\ndocker-desktop\r\nUbuntu-26.04\r\n', 'utf16le');

test('the launcher is always the absolute System32 wsl.exe under SystemRoot — never a bare name PATH would resolve', () => {
  assert.equal(wslExecutable({ SystemRoot: 'C:\\WINDOWS' }), 'C:\\WINDOWS\\System32\\wsl.exe');
  assert.equal(wslExecutable({ SystemRoot: 'D:\\Win' }), 'D:\\Win\\System32\\wsl.exe');
});

test('without a drive-absolute SystemRoot there is no launcher — no fallback to a name, a UNC share or PATH', () => {
  for (const env of [{}, { SystemRoot: '' }, { SystemRoot: 'Windows' }, { SystemRoot: '\\\\server\\share\\Windows' }, { SystemRoot: '/mnt/c/Windows' }, { PATH: 'C:\\Windows\\System32' }]) {
    assert.equal(wslExecutable(env), undefined, JSON.stringify(env));
  }
});

test('wsl.exe output is decoded as UTF-16LE — measured: --list --quiet begins 55 00 62 00', () => {
  assert.deepEqual([...MEASURED_QUIET.subarray(0, 4)], [0x55, 0x00, 0x62, 0x00]);
  assert.equal(decodeWslText(MEASURED_QUIET), 'Ubuntu\r\ndocker-desktop\r\nUbuntu-26.04\r\n');
});

test('with WSL_UTF8=1 the same list is UTF-8 (measured) and still decodes', () => {
  assert.equal(decodeWslText(Buffer.from('Ubuntu\r\ndocker-desktop\r\n', 'utf8')), 'Ubuntu\r\ndocker-desktop\r\n');
});

test('a UTF-16LE byte-order mark is honoured and dropped; an empty answer is empty', () => {
  assert.equal(decodeWslText(Buffer.concat([Buffer.from([0xff, 0xfe]), Buffer.from('Ubuntu\r\n', 'utf16le')])), 'Ubuntu\r\n');
  assert.equal(decodeWslText(Buffer.alloc(0)), '');
});

test('Linux output is UTF-8 and is not mistaken for UTF-16 — measured: printf of "café ж" arrived as 63 61 66 c3 a9 20 d0 b6', () => {
  assert.equal(decodeWslText(Buffer.from([0x63, 0x61, 0x66, 0xc3, 0xa9, 0x20, 0xd0, 0xb6])), 'café ж');
});

test('lines split on CRLF and LF, trailing blanks dropped', () => {
  assert.deepEqual(textLines('a\r\nb\n\r\n  \nc'), ['a', 'b', 'c']);
  assert.deepEqual(textLines(''), []);
});

test('ANSI colour and cursor sequences are stripped, the text kept', () => {
  assert.equal(stripAnsi('\u001b[33mWRN\u001b[0m wsl-care: x \u001b[1;31mred\u001b[m'), 'WRN wsl-care: x red');
  assert.equal(stripAnsi('\u001b]0;title\u0007plain'), 'plain');
  assert.equal(stripAnsi('no escapes'), 'no escapes');
});

test('--list --quiet gives the distribution names, in order', () => {
  assert.deepEqual(parseQuietList(decodeWslText(MEASURED_QUIET)), ['Ubuntu', 'docker-desktop', 'Ubuntu-26.04']);
  assert.deepEqual(parseQuietList(''), []);
});

test('the default is the one row -l -v marks with * — measured layout; header words are localised, the marker is not', () => {
  const measured = '  NAME              STATE           VERSION\r\n* Ubuntu            Running         2\r\n  docker-desktop    Running         2\r\n  Ubuntu-26.04      Running         2\r\n';
  assert.equal(verboseTable([{ name: 'Ubuntu', running: true }, { name: 'docker-desktop', running: true }, { name: 'Ubuntu-26.04', running: true }], 'Ubuntu'), measured, 'the test table is the measured one');
  assert.equal(parseDefaultDistro(measured), 'Ubuntu');
  const localised = '  NAME              ZUSTAND         VERSION\r\n  Ubuntu            Wird ausgeführt 2\r\n* Debian            Beendet         2\r\n';
  assert.equal(parseDefaultDistro(localised), 'Debian');
});

test('no marked row, or two, is no default', () => {
  assert.equal(parseDefaultDistro('  NAME  STATE  VERSION\r\n  Ubuntu  Running  2\r\n'), undefined);
  assert.equal(parseDefaultDistro('* Ubuntu  Running  2\r\n* Debian  Running  2\r\n'), undefined);
  assert.equal(parseDefaultDistro(''), undefined);
});

test('a distribution name is letters, digits, dot, dash and underscore, never starting with a dash — so it can never be an option', () => {
  for (const ok of ['Ubuntu', 'Ubuntu-26.04', 'docker-desktop', 'my_distro', 'a', 'A'.repeat(64)]) {
    assert.equal(isDistroName(ok), true, ok);
  }
  for (const bad of ['', '-u', '--user', '-d', 'Ubuntu 24', 'a;b', 'a&b', '$(id)', 'Ubuntu\n', 'Ubu/ntu', '.hidden', 'A'.repeat(65), 'Убунту']) {
    assert.equal(isDistroName(bad), false, JSON.stringify(bad));
  }
});

// ---- E18.W1a-1 (RED 1): the verbose table, one parser ----

/**
 * The WSL 1 row shape is Microsoft's documented layout, NOT measured on this machine (no WSL 1 distro here): the same columns with the
 * version digit `1`. If a real WSL 1 machine prints otherwise, this fixture is corrected and the deviation recorded (plan W1a §7).
 */
const MIXED = [
  '  NAME              STATE           VERSION',
  '* Ubuntu            Running         2',
  '  Legacy            Stopped         1',
  '  My Distro         Running         2',
  '  Future            Running         3',
  '  -x                Running         2',
  '  Debian            Wird ausgeführt 2',
  '',
].join('\r\n');

test('parseVerboseTable over the measured table: three rows, Ubuntu marked, every version 2, the state word kept', () => {
  const measured = verboseTable([{ name: 'Ubuntu', running: true }, { name: 'docker-desktop', running: true }, { name: 'Ubuntu-26.04', running: false }], 'Ubuntu');
  assert.deepEqual(parseVerboseTable(measured), [
    { name: 'Ubuntu', marked: true, stateText: 'Running', version: 2 },
    { name: 'docker-desktop', marked: false, stateText: 'Running', version: 2 },
    { name: 'Ubuntu-26.04', marked: false, stateText: 'Stopped', version: 2 },
  ]);
});

test('parseVerboseTable over the localised table: the two-word state is ONE row\'s stateText; the header is skipped', () => {
  const localised = '  NAME              ZUSTAND         VERSION\r\n  Ubuntu            Wird ausgeführt 2\r\n* Debian            Beendet         2\r\n';
  assert.deepEqual(parseVerboseTable(localised), [
    { name: 'Ubuntu', marked: false, stateText: 'Wird ausgeführt', version: 2 },
    { name: 'Debian', marked: true, stateText: 'Beendet', version: 2 },
  ]);
});

test('parseVerboseTable over the mixed fixture: a WSL 1 row reads version 1, a 3 reads undefined, "-x" and a spaced name are dropped, neighbours parse', () => {
  assert.deepEqual(parseVerboseTable(MIXED), [
    { name: 'Ubuntu', marked: true, stateText: 'Running', version: 2 },
    { name: 'Legacy', marked: false, stateText: 'Stopped', version: 1 },
    { name: 'Future', marked: false, stateText: 'Running', version: undefined },
    { name: 'Debian', marked: false, stateText: 'Wird ausgeführt', version: 2 },
  ]);
});

test('parseVerboseTable: an empty answer is no rows; a table with only a header is no rows', () => {
  assert.deepEqual(parseVerboseTable(''), []);
  assert.deepEqual(parseVerboseTable('  NAME              STATE           VERSION\r\n'), []);
});

test('parseVerboseTable drops a spaced name only when the header tells the state column — without a header the first token is the name, as parseDefaultDistro has always read it', () => {
  assert.deepEqual(parseVerboseTable('* Ubuntu  Running  2\r\n  Debian  Stopped  2\r\n').map((r) => r.name), ['Ubuntu', 'Debian']);
});

test('parseDefaultDistro runs over the same parser and keeps its answers: the marked row of the mixed table, even for a name a DistroName cannot be minted from', () => {
  assert.equal(parseDefaultDistro(MIXED), 'Ubuntu');
  assert.equal(parseDefaultDistro('  NAME              STATE           VERSION\r\n  Ubuntu            Running         2\r\n* -x                Running         2\r\n'), '-x', 'the default road refuses it itself, naming the option rule');
  assert.equal(parseDefaultDistro('  NAME  STATE  VERSION\r\n* Ubuntu+Dev~2  Running  2\r\n'), 'Ubuntu+Dev~2');
});

test('security check: the header is the line IMMEDIATELY before the first row — a stray line above it does not become the state column', () => {
  const stray = 'Some notice a launcher printed before the table\r\n';
  const text = `${stray}  NAME              STATE           VERSION\r\n* Ubuntu            Running         2\r\n  Debian            Wird ausgeführt 2\r\n  My Distro         Running         2\r\n`;
  assert.deepEqual(parseVerboseTable(text).map((r) => [r.name, r.stateText]), [['Ubuntu', 'Running'], ['Debian', 'Wird ausgeführt']]);
});
