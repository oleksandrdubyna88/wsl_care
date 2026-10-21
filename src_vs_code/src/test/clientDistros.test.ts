import assert from 'node:assert/strict';
import { test } from 'node:test';

import type { Failure, VerbOutcome } from '../client/outcome';
import { PREVIEW_CONTAINER_ASSUMPTION, runReadTail, VERB_TIMEOUT_MS, VERBS, type RunRead } from '../client/verbs';
import { WslCareClient } from '../client/WslCareClient';
import type { ProcessResult } from '../process/runner';
import { DEFAULT_TARGET, listedNameOf, pickedTarget } from '../wsl/distroTarget';
import { listed, pick } from './support/distroNames';
import { TEST_ENV } from './support/fakeWorld';
import { golden } from './support/paths';
import { daemonArgv, exited, exitedUtf16, LIST_QUIET, LIST_RUNNING, LIST_VERBOSE, recordingRunner, verboseTable, type RecordingRunner, type Scripted } from './support/recordingRunner';

/**
 * E18.W1a-1 (todo/PLAN_disk_health_E18.md, RED 2-8 and 4b): the client's per-distro road. The DEFAULT target is today's road, byte
 * for byte (`client.test.ts`, `distroSwitch.test.ts`); a PICKED target is one `-l -v` that is both the listed check and the version
 * read, then the running question, then the `-d` — and never `--list --quiet`.
 */

const STATUS = JSON.stringify({ ...golden('head', 'status'), productVersion: '0.1.0' });

interface Machine {
  readonly name: string;
  readonly running: boolean;
  readonly version?: number;
}

const UBUNTU_AND_DEBIAN: readonly Machine[] = [{ name: 'Ubuntu', running: true }, { name: 'Debian', running: true }];

/** What `wsl.exe` answers for a machine of `rows`: the three questions, the `*` on `defaultName`. */
function wsl(rows: readonly Machine[], defaultName: string | undefined = 'Ubuntu'): Record<string, Scripted> {
  return {
    [LIST_QUIET]: exitedUtf16(0, rows.map((r) => `${r.name}\r\n`).join('')),
    [LIST_VERBOSE]: exitedUtf16(0, verboseTable(rows, defaultName)),
    [LIST_RUNNING]: exitedUtf16(0, rows.filter((r) => r.running).map((r) => `${r.name}\r\n`).join('')),
  };
}

/** A daemon that answers every verb for each distro in `names`. */
function daemons(names: readonly string[]): Record<string, Scripted> {
  const script: Record<string, Scripted> = {};
  for (const name of names) {
    script[daemonArgv(name, VERBS.status)] = exited(0, STATUS);
    script[daemonArgv(name, VERBS.preview)] = exited(0, JSON.stringify(golden('head', 'preview')));
    script[daemonArgv(name, VERBS.doctor)] = exited(0, JSON.stringify(golden('head', 'doctor')));
    script[daemonArgv(name, VERBS.version)] = exited(0, '0.1.0\n');
  }

  return script;
}

function clientOf(script: Record<string, Scripted>, setting = ''): { c: WslCareClient; rec: RecordingRunner } {
  const rec = recordingRunner(script);

  return { c: new WslCareClient({ runner: rec.runner, platform: 'win32', env: TEST_ENV, distroSetting: () => setting }), rec };
}

function daemonCalls(rec: RecordingRunner): string[] {
  return rec.argvs().filter((a) => a.startsWith('-d '));
}

function kindOf(outcome: VerbOutcome | Failure | { readonly kind: string }): string {
  return outcome.kind;
}

// ---- RED 4: a picked name is minted only from a list ----

test('a name that is not in the list, or starts with "-", cannot be minted into a DistroName', () => {
  assert.equal(listedNameOf('-x', ['-x', 'Ubuntu']), undefined, 'a name starting with "-" would be read by wsl.exe as an option after -d');
  assert.equal(listedNameOf('Debian', ['Ubuntu']), undefined, 'a name WSL does not list');
  assert.equal(listedNameOf('ubuntu', ['Ubuntu']), undefined, 'matching is exact');
  assert.equal(listedNameOf('Ubuntu', ['Ubuntu', 'Debian']), 'Ubuntu');
});

// ---- RED 2: listDistros() ----

test('listDistros sends exactly -l -v then --list --running --quiet and answers one entry per row, the * row as the default', async () => {
  const { c, rec } = clientOf(wsl([{ name: 'Ubuntu', running: true }, { name: 'Debian', running: false }, { name: 'Alpine', running: true, version: 1 }]));
  const list = await c.listDistros();
  assert.deepEqual(rec.argvs(), [LIST_VERBOSE, LIST_RUNNING], 'the version column and the unlocalised running list — and nothing else');
  assert.ok('distros' in list, JSON.stringify(list));
  assert.deepEqual(list.distros, [
    { name: 'Ubuntu', running: true, version: 2, isDefault: true, stateText: 'Running' },
    { name: 'Debian', running: false, version: 2, isDefault: false, stateText: 'Stopped' },
    { name: 'Alpine', running: true, version: 1, isDefault: false, stateText: 'Running' },
  ]);
  assert.equal(list.default, 'Ubuntu');
});

test('listDistros: a distro missing from the running list is not running; no * row is no default', async () => {
  const { c } = clientOf(wsl([{ name: 'Ubuntu', running: false }, { name: 'Debian', running: true }], ''));
  const list = await c.listDistros();
  assert.ok('distros' in list, JSON.stringify(list));
  assert.deepEqual(list.distros.map((d) => [d.name, d.running]), [['Ubuntu', false], ['Debian', true]]);
  assert.equal(list.default, undefined);
});

test('listDistros: a failed running question makes every distro running: undefined (state not known) and is not a failure of the list', async () => {
  const { c } = clientOf({ ...wsl(UBUNTU_AND_DEBIAN), [LIST_RUNNING]: exitedUtf16(-1, 'There are no running distributions.\r\n') });
  const list = await c.listDistros();
  assert.ok('distros' in list, JSON.stringify(list));
  assert.deepEqual(list.distros.map((d) => d.running), [undefined, undefined]);
  assert.deepEqual(list.distros.map((d) => d.name), ['Ubuntu', 'Debian']);
});

test('listDistros: a failed -l -v is the Failure and nothing else — the running question is not asked', async () => {
  const { c, rec } = clientOf({ ...wsl(UBUNTU_AND_DEBIAN), [LIST_VERBOSE]: exitedUtf16(-1, 'The Windows Subsystem for Linux is not installed.\r\n') });
  assert.deepEqual(await c.listDistros(), { kind: 'wslFailed', message: 'The Windows Subsystem for Linux is not installed.' });
  assert.deepEqual(rec.argvs(), [LIST_VERBOSE]);
});

test('listDistros refuses before any spawn off Windows', async () => {
  const rec = recordingRunner({});
  const c = new WslCareClient({ runner: rec.runner, platform: 'linux', env: TEST_ENV, distroSetting: () => '' });
  assert.deepEqual(await c.listDistros(), { kind: 'notWindows', platform: 'linux' });
  assert.equal(rec.requests.length, 0);
});

test('a distro whose name fails the setting pattern (a spaced one) is not listed — it can never be minted', async () => {
  const text = `  ${'NAME'.padEnd(20)}${'STATE'.padEnd(16)}VERSION\r\n* ${'Ubuntu'.padEnd(20)}${'Running'.padEnd(16)}2\r\n  ${'My Distro'.padEnd(20)}${'Running'.padEnd(16)}2\r\n  ${'Debian'.padEnd(20)}${'Stopped'.padEnd(16)}2\r\n`;
  const { c } = clientOf({ [LIST_VERBOSE]: exitedUtf16(0, text), [LIST_RUNNING]: exitedUtf16(0, 'Ubuntu\r\nMy Distro\r\n') });
  const list = await c.listDistros();
  assert.ok('distros' in list, JSON.stringify(list));
  assert.deepEqual(list.distros.map((d) => d.name), ['Ubuntu', 'Debian']);
});

// ---- RED 3: the picked road ----

test('run(status, picked Debian) sends -l -v, --list --running --quiet, then -d Debian — and NEVER --list --quiet', async () => {
  const { c, rec } = clientOf({ ...wsl(UBUNTU_AND_DEBIAN), ...daemons(['Ubuntu', 'Debian']) });
  const outcome = await c.run('status', pick('Debian'));
  assert.ok(outcome.kind === 'answered', JSON.stringify(outcome));
  assert.equal(outcome.distro, 'Debian');
  assert.deepEqual(rec.argvs(), [LIST_VERBOSE, LIST_RUNNING, daemonArgv('Debian', VERBS.status)]);
  assert.ok(!rec.argvs().includes(LIST_QUIET));
  assert.equal(rec.requests[0]?.timeoutMs, 15_000, 'the picked road asks WSL under the same ceiling as the default one');
  assert.equal(rec.requests[2]?.timeoutMs, VERB_TIMEOUT_MS.status);
});

test('run(status, DEFAULT_TARGET) is today\'s sequence unchanged: --list --quiet, -l -v (setting empty), --list --running --quiet, -d', async () => {
  const { c, rec } = clientOf({ ...wsl(UBUNTU_AND_DEBIAN), ...daemons(['Ubuntu']) });
  assert.equal((await c.run('status', DEFAULT_TARGET)).kind, 'answered');
  assert.deepEqual(rec.argvs(), [LIST_QUIET, LIST_VERBOSE, LIST_RUNNING, daemonArgv('Ubuntu', VERBS.status)]);
});

test('a picked target ignores the wslCare.distro setting, even an invalid one — the name came from WSL\'s list, not from the setting', async () => {
  const { c, rec } = clientOf({ ...wsl(UBUNTU_AND_DEBIAN), ...daemons(['Debian']) }, '-u');
  const outcome = await c.run('status', pick('Debian'));
  assert.equal(outcome.kind, 'answered', JSON.stringify(outcome));
  assert.deepEqual(daemonCalls(rec), [daemonArgv('Debian', VERBS.status)]);
});

test('read(request, picked) goes through the same picked road and reaches the picked distro', async () => {
  const request: RunRead = { read: 'runsShow', runId: '20261021T101010Z-12345' };
  const body = JSON.stringify(golden('head', 'status'));
  const { c, rec } = clientOf({ ...wsl(UBUNTU_AND_DEBIAN), [daemonArgv('Debian', runReadTail(request))]: exited(0, body) });
  const outcome = await c.read(request, pick('Debian'));
  assert.ok(outcome.kind === 'read', JSON.stringify(outcome));
  assert.equal(outcome.distro, 'Debian');
  assert.deepEqual(rec.argvs(), [LIST_VERBOSE, LIST_RUNNING, daemonArgv('Debian', runReadTail(request))]);
});

// ---- RED 4: unlisted ----

test('a picked distro that the -l -v table does not report is distroRefused, with no -d call', async () => {
  const minted = listed('Debian');
  const { c, rec } = clientOf(wsl([{ name: 'Ubuntu', running: true }]));
  const outcome = await c.run('status', pickedTarget(minted));
  assert.equal(kindOf(outcome), 'distroRefused', JSON.stringify(outcome));
  assert.deepEqual(rec.argvs(), [LIST_VERBOSE]);
});

// ---- RED 5: stopped ----

test('a picked distro that is not running answers stopped with no -d; startIfStopped sends the -d (the explicit act)', async () => {
  const machine: Machine[] = [{ name: 'Ubuntu', running: true }, { name: 'Debian', running: false }];
  const stopped = clientOf({ ...wsl(machine), ...daemons(['Debian']) });
  assert.deepEqual(await stopped.c.run('status', pick('Debian')), { kind: 'stopped', verb: 'status', distro: 'Debian' });
  assert.deepEqual(daemonCalls(stopped.rec), []);

  const started = clientOf({ ...wsl(machine), ...daemons(['Debian']) });
  const outcome = await started.c.run('status', pick('Debian'), { startIfStopped: true });
  assert.equal(outcome.kind, 'answered', JSON.stringify(outcome));
  assert.deepEqual(started.rec.argvs(), [LIST_VERBOSE, LIST_RUNNING, daemonArgv('Debian', VERBS.status)]);
});

test('a failed running question answers stopped for a picked distro too — a read never starts a distribution', async () => {
  const { c, rec } = clientOf({ ...wsl(UBUNTU_AND_DEBIAN), ...daemons(['Debian']), [LIST_RUNNING]: exitedUtf16(-1, 'There are no running distributions.\r\n') });
  assert.deepEqual(await c.run('status', pick('Debian')), { kind: 'stopped', verb: 'status', distro: 'Debian' });
  assert.deepEqual(daemonCalls(rec), []);
});

// ---- RED 6: WSL 1 ----

test('a picked WSL 1 distro answers wsl1 with no -d — and no --list --running --quiet: the version is judged before the running check', async () => {
  const { c, rec } = clientOf({ ...wsl([{ name: 'Ubuntu', running: true }, { name: 'Legacy', running: true, version: 1 }]), ...daemons(['Legacy']) });
  const outcome = await c.run('status', pickedTarget(listed('Legacy', ['Ubuntu', 'Legacy'])));
  assert.deepEqual(outcome, { kind: 'wsl1', verb: 'status', distro: 'Legacy' });
  assert.deepEqual(rec.argvs(), [LIST_VERBOSE]);
});

test('the EMPTY setting\'s default resolving to a WSL 1 * row answers wsl1 with no -d (the table was read anyway)', async () => {
  const { c, rec } = clientOf({ ...wsl([{ name: 'Legacy', running: true, version: 1 }, { name: 'Debian', running: true }], 'Legacy'), ...daemons(['Legacy']) });
  assert.deepEqual(await c.run('status', DEFAULT_TARGET), { kind: 'wsl1', verb: 'status', distro: 'Legacy' });
  assert.deepEqual(daemonCalls(rec), []);
  assert.deepEqual(rec.argvs(), [LIST_QUIET, LIST_VERBOSE]);
});

test('a SET setting naming a WSL 1 distro keeps today\'s road (a -d is sent): WSL 1 is judged only where the table is read anyway', async () => {
  const { c, rec } = clientOf({ ...wsl([{ name: 'Legacy', running: true, version: 1 }]), ...daemons(['Legacy']) }, 'Legacy');
  assert.equal((await c.run('status', DEFAULT_TARGET)).kind, 'answered');
  assert.deepEqual(rec.argvs(), [LIST_QUIET, LIST_RUNNING, daemonArgv('Legacy', VERBS.status)]);
});

// ---- RED 4b: rootTarget and terminalTarget never start a distro behind the user's back ----

test('rootTarget(picked stopped) and rootTarget(picked WSL 1) answer the Failure and nothing carries a -d', async () => {
  const stopped = clientOf(wsl([{ name: 'Ubuntu', running: true }, { name: 'Debian', running: false }]));
  assert.deepEqual(await stopped.c.rootTarget(pick('Debian')), { kind: 'stopped', distro: 'Debian' });
  assert.deepEqual(stopped.rec.argvs(), [LIST_VERBOSE, LIST_RUNNING]);

  const legacy = clientOf(wsl([{ name: 'Ubuntu', running: true }, { name: 'Legacy', running: true, version: 1 }]));
  assert.deepEqual(await legacy.c.rootTarget(pickedTarget(listed('Legacy', ['Legacy']))), { kind: 'wsl1', distro: 'Legacy' });
  assert.deepEqual(legacy.rec.argvs(), [LIST_VERBOSE]);
});

test('rootTarget(picked running) is the launcher and the picked name; rootTarget(DEFAULT_TARGET) is today\'s answer', async () => {
  const picked = clientOf(wsl(UBUNTU_AND_DEBIAN));
  assert.deepEqual(await picked.c.rootTarget(pick('Debian')), { wsl: 'C:\\Windows\\System32\\wsl.exe', distro: 'Debian' });
  const standard = clientOf(wsl(UBUNTU_AND_DEBIAN));
  assert.deepEqual(await standard.c.rootTarget(DEFAULT_TARGET), { wsl: 'C:\\Windows\\System32\\wsl.exe', distro: 'Ubuntu' });
  assert.deepEqual(standard.rec.argvs(), [LIST_QUIET, LIST_VERBOSE, LIST_RUNNING]);
});

test('terminalTarget(picked WSL 1) answers wsl1 and builds no terminal; picked stopped is a target that says it is not running', async () => {
  const legacy = clientOf(wsl([{ name: 'Legacy', running: true, version: 1 }, { name: 'Ubuntu', running: true }]));
  assert.deepEqual(await legacy.c.terminalTarget(pickedTarget(listed('Legacy', ['Legacy']))), { kind: 'wsl1', distro: 'Legacy' });

  const stopped = clientOf(wsl([{ name: 'Ubuntu', running: true }, { name: 'Debian', running: false }]));
  assert.deepEqual(await stopped.c.terminalTarget(pick('Debian')), { kind: 'terminal', shellPath: 'C:\\Windows\\System32\\wsl.exe', shellArgs: ['-d', 'Debian', '--cd', '~'], distro: 'Debian', running: false });
  assert.deepEqual(stopped.rec.argvs(), [LIST_VERBOSE, LIST_RUNNING], 'the terminal is built, never opened: no -d call is made');

  const running = clientOf(wsl(UBUNTU_AND_DEBIAN));
  const target = await running.c.terminalTarget(pick('Debian'));
  assert.ok(target.kind === 'terminal' && target.running === true, JSON.stringify(target));
});

test('terminalTarget(DEFAULT_TARGET) is today\'s: --list --quiet only, no running key', async () => {
  const { c, rec } = clientOf(wsl([{ name: 'Ubuntu', running: false }, { name: 'Debian', running: true }]), 'Debian');
  assert.deepEqual(await c.terminalTarget(DEFAULT_TARGET), { kind: 'terminal', shellPath: 'C:\\Windows\\System32\\wsl.exe', shellArgs: ['-d', 'Debian', '--cd', '~'], distro: 'Debian' });
  assert.deepEqual(rec.argvs(), [LIST_QUIET]);
});

// ---- RED 7: in-flight keys ----

interface Gate {
  readonly promise: Promise<ProcessResult>;
  open(): void;
}

function gate(answer: ProcessResult): Gate {
  let open: () => void = () => undefined;
  const promise = new Promise<ProcessResult>((resolve) => { open = () => resolve(answer); });

  return { promise, open };
}

test('two picked distros\' previews in flight at once are two calls with two keys', async () => {
  const ubuntu = gate(exited(0, JSON.stringify(golden('head', 'preview'))));
  const debian = gate(exited(0, JSON.stringify(golden('head', 'preview'))));
  const { c, rec } = clientOf({ ...wsl(UBUNTU_AND_DEBIAN), ...daemons(['Ubuntu', 'Debian']), [daemonArgv('Ubuntu', VERBS.preview)]: () => ubuntu.promise, [daemonArgv('Debian', VERBS.preview)]: () => debian.promise });
  const first = c.run('preview', pick('Ubuntu'));
  const second = c.run('preview', pick('Debian'));
  const again = c.run('preview', pick('Debian'));
  for (let i = 0; i < 50; i += 1) {
    await new Promise((resolve) => setImmediate(resolve));
  }
  ubuntu.open();
  debian.open();
  const [a, b, joined] = await Promise.all([first, second, again]);
  assert.ok(a.kind === 'answered' && b.kind === 'answered', JSON.stringify([a.kind, b.kind]));
  assert.deepEqual([a.distro, b.distro], ['Ubuntu', 'Debian']);
  assert.equal(joined, b, 'the same picked distro in flight is shared: one call, two callers');
  assert.equal(rec.argvs().filter((x) => x === daemonArgv('Debian', VERBS.preview)).length, 1);
  assert.equal(rec.argvs().filter((x) => x === daemonArgv('Ubuntu', VERBS.preview)).length, 1);
});

test('a default call and a picked call for the same distro are two calls — never shared', async () => {
  const held = gate(exited(0, STATUS));
  const { c, rec } = clientOf({ ...wsl(UBUNTU_AND_DEBIAN), ...daemons(['Ubuntu']), [daemonArgv('Ubuntu', VERBS.status)]: () => held.promise });
  const byDefault = c.run('status', DEFAULT_TARGET);
  const byPick = c.run('status', pick('Ubuntu'));
  for (let i = 0; i < 50; i += 1) {
    await new Promise((resolve) => setImmediate(resolve));
  }
  held.open();
  const [one, two] = await Promise.all([byDefault, byPick]);
  assert.notEqual(one, two, 'a setting change must never be coupled to a picked read');
  assert.equal(rec.argvs().filter((x) => x === daemonArgv('Ubuntu', VERBS.status)).length, 2);
});

// ---- RED 8: knownVersion / knownContainers per distro ----

function crowdedStatus(count: number): string {
  const body = golden('head', 'status') as { vm: { containers: Record<string, unknown> } } & Record<string, unknown>;

  return JSON.stringify({ ...body, productVersion: '0.1.0', vm: { ...body.vm, containers: { ...body.vm.containers, count } } });
}

const PREVIEW_TIMEOUT: ProcessResult = { kind: 'timedOut', timeoutMs: VERB_TIMEOUT_MS.preview, treeEnded: false, stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) };

test('a preview timeout is judged against ITS distro\'s container count: Ubuntu 140, then Debian 3, then Ubuntu\'s preview', async () => {
  const count = PREVIEW_CONTAINER_ASSUMPTION + 40;
  const { c } = clientOf({
    ...wsl(UBUNTU_AND_DEBIAN), ...daemons(['Ubuntu', 'Debian']),
    [daemonArgv('Ubuntu', VERBS.status)]: exited(0, crowdedStatus(count)),
    [daemonArgv('Debian', VERBS.status)]: exited(0, crowdedStatus(3)),
    [daemonArgv('Ubuntu', VERBS.preview)]: PREVIEW_TIMEOUT,
    [daemonArgv('Debian', VERBS.preview)]: PREVIEW_TIMEOUT,
  });
  await c.run('status', DEFAULT_TARGET);
  await c.run('status', pick('Debian'));
  assert.deepEqual(await c.run('preview', DEFAULT_TARGET), { kind: 'previewTooManyContainers', verb: 'preview', containers: count, timeoutMs: VERB_TIMEOUT_MS.preview }, 'Ubuntu\'s own count, not Debian\'s later one');
  assert.equal((await c.run('preview', pick('Debian'))).kind, 'timedOut', 'Debian ran 3 containers: a plain timeout');
});

const OLD_STATUS = JSON.stringify({ schemaVersion: 1, side: 'wsl' });

function versionAsks(rec: RecordingRunner, distro: string): number {
  return rec.argvs().filter((x) => x === daemonArgv(distro, VERBS.version)).length;
}

test('a distro\'s --version is remembered per distro: Debian\'s is not asked again after Ubuntu\'s was', async () => {
  const { c, rec } = clientOf({
    ...wsl(UBUNTU_AND_DEBIAN), ...daemons(['Ubuntu', 'Debian']),
    [daemonArgv('Ubuntu', VERBS.status)]: exited(0, OLD_STATUS),
    [daemonArgv('Debian', VERBS.status)]: exited(0, OLD_STATUS),
  });
  await c.run('status', pick('Debian'));
  await c.run('status', DEFAULT_TARGET);
  await c.run('doctor', pick('Debian'));
  assert.equal(versionAsks(rec, 'Debian'), 1, 'Debian\'s version was read once and remembered');
  assert.equal(versionAsks(rec, 'Ubuntu'), 1);
});

test('after a listDistros() that no longer lists Debian, its remembered version is gone', async () => {
  let table: readonly Machine[] = UBUNTU_AND_DEBIAN;
  const script: Record<string, Scripted> = {
    ...wsl(UBUNTU_AND_DEBIAN), ...daemons(['Ubuntu', 'Debian']),
    [LIST_VERBOSE]: () => exitedUtf16(0, verboseTable(table, 'Ubuntu')),
    [LIST_RUNNING]: () => exitedUtf16(0, table.filter((r) => r.running).map((r) => `${r.name}\r\n`).join('')),
    [daemonArgv('Debian', VERBS.status)]: exited(0, OLD_STATUS),
    [daemonArgv('Debian', VERBS.preview)]: PREVIEW_TIMEOUT,
  };
  const { c, rec } = clientOf(script);
  await c.run('status', pick('Debian'));
  await c.run('doctor', pick('Debian'));
  assert.equal(versionAsks(rec, 'Debian'), 1);

  table = [{ name: 'Ubuntu', running: true }];
  const without = await c.listDistros();
  assert.ok('distros' in without && without.distros.length === 1, JSON.stringify(without));
  table = UBUNTU_AND_DEBIAN;
  await c.run('doctor', pick('Debian'));
  assert.equal(versionAsks(rec, 'Debian'), 2, 'the unlisted distro\'s entry was dropped: the next call asks --version again');
});

test('after a listDistros() that no longer lists Ubuntu, its container count is gone: a preview timeout is a plain one again', async () => {
  let table: readonly Machine[] = UBUNTU_AND_DEBIAN;
  const { c } = clientOf({
    ...wsl(UBUNTU_AND_DEBIAN), ...daemons(['Ubuntu', 'Debian']),
    [LIST_VERBOSE]: () => exitedUtf16(0, verboseTable(table, 'Debian')),
    [LIST_RUNNING]: () => exitedUtf16(0, table.filter((r) => r.running).map((r) => r.name + String.fromCharCode(13, 10)).join('')),
    [daemonArgv('Ubuntu', VERBS.status)]: exited(0, crowdedStatus(PREVIEW_CONTAINER_ASSUMPTION + 40)),
    [daemonArgv('Ubuntu', VERBS.preview)]: PREVIEW_TIMEOUT,
  });
  await c.run('status', pick('Ubuntu'));
  assert.equal((await c.run('preview', pick('Ubuntu'))).kind, 'previewTooManyContainers');

  table = [{ name: 'Debian', running: true }];
  await c.listDistros();
  table = UBUNTU_AND_DEBIAN;
  assert.equal((await c.run('preview', pick('Ubuntu'))).kind, 'timedOut', 'the count of a distro the list dropped is not remembered');
});
