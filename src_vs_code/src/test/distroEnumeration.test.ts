import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

import { ArchiveHost } from '../archive/archiveHost';
import { newArchiveRecorder, recordingArchiveUi } from '../archive/archiveRecorder';
import { judgedFolderOf } from '../archive/judgedFolder';
import { CleanupHost } from '../cleanup/cleanupHost';
import { newCleanRecorder, recordingCleanUi } from '../cleanup/cleanRecorder';
import { RUN_READ_NAMES, VERB_NAMES, type RunRead, type RunReadName } from '../client/verbs';
import { WslCareClient } from '../client/WslCareClient';
import { configRequest, type ConfigOp } from '../config/configCall';
import { installDaemon } from '../install/installDaemon';
import { COMMAND_BUTTONS } from '../panel/commandButtons';
import { Poller } from '../poll/poller';
import { CleanupController } from '../root/cleanupController';
import { callRoot, ROOT_OPS, rootRequest, type RootOp, type RootOpName } from '../root/rootCall';
import { entryIdsOf, runIdOf, shownProcessOf, volumeNameOf, type ActionIds } from '../root/rootIds';
import { OutcomeStore } from '../state/outcomeStore';
import { DEFAULT_TARGET, listedNameOf, pickedTarget, type DistroTarget } from '../wsl/distroTarget';
import { fakeWorld, TEST_ENV, type FakeWorld } from './support/fakeWorld';
import { ManualClock } from './support/manualClock';
import { ManualTimers, MapStore } from './support/memento';
import { goldenFile, GOLDEN_ROOT, SOURCE_ROOT } from './support/paths';

/**
 * E18.W1a-3 (todo/PLAN_disk_health_E18.md, RED 20 — THE ENUMERATION, the cornerstone's proof): with the PICKED target `Debian` — the machine's
 * default being `Ubuntu`, both running, both WSL 2 — every verb, every run read, every root op, every config op, the terminal and every host
 * flow is driven through the REAL client, runner and the strict fake, and EVERY `wsl.exe` call it makes carries `-d Debian`. The closed
 * registers are the oracle: each loop below walks a register (`VERB_NAMES`, `RUN_READ_NAMES`, `ROOT_OPS`, the config ops), and the tables are
 * typed `Record<Name, …>`, so a verb or op added to a register without a row here does not compile — and a row whose road ignores the
 * picked distribution is red in (a).
 */

const DAEMON = '/opt/wsl-care/bin/wsl-care';
const RUN = '20000101T000000Z-1';
const ENTRY = '0123456789abcdef';
const WINDOWS = 'C:\\Windows\\System32\\wsl.exe';

// ---- the registers' rows: typed by the register, so a new name fails to compile ----

const VALID_READS: { readonly [K in RunReadName]: RunRead } = {
  runsShow: { read: 'runsShow', runId: RUN },
  runs: { read: 'runs', from: '2026-10-05T00:00:00Z', to: '2026-10-06T00:00:00Z' },
  logs: { read: 'logs', from: '2026-10-05T00:00:00Z', to: '2026-10-06T00:00:00Z' },
  archiveStatus: { read: 'archiveStatus' },
  archivePreview: { read: 'archivePreview' },
  archiveCheckBase: { read: 'archiveCheckBase', path: 'V:\\ai-archive' },
  archiveList: { read: 'archiveList' },
};

function rootOps(): { readonly [K in RootOpName]: RootOp } {
  const a4 = ['A4'] as unknown as ActionIds;
  const volume = volumeNameOf('a'.repeat(64));
  const process = shownProcessOf('1234:5678');
  const run = runIdOf(RUN);
  const entries = entryIdsOf([ENTRY]);
  assert.ok(volume !== undefined && process !== undefined && run !== undefined && entries !== undefined, 'the typed ids are mintable');

  return {
    preview: { op: 'preview', ids: a4 },
    confirm: { op: 'confirm', ids: a4, shown: [volume] },
    stop: { op: 'stop', runId: run },
    fullCheck: { op: 'fullCheck' },
    rootCheck: { op: 'rootCheck' },
    restorePreview: { op: 'restorePreview', entries },
    restoreConfirm: { op: 'restoreConfirm', entries },
  };
}

const A21_CONFIRM = ((): RootOp => {
  const process = shownProcessOf('1234:5678');
  assert.ok(process !== undefined);

  return { op: 'confirm', ids: ['A21'] as unknown as ActionIds, shown: undefined, processes: [process] };
})();

// ---- the world ----

function answersFolder(world: FakeWorld, name: string, status: string): string {
  const folder = path.join(world.folder, name);
  fs.mkdirSync(folder, { recursive: true });
  for (const file of fs.readdirSync(path.join(GOLDEN_ROOT, 'head'))) {
    fs.copyFileSync(path.join(GOLDEN_ROOT, 'head', file), path.join(folder, file));
  }
  fs.copyFileSync(path.join(GOLDEN_ROOT, 'head', status), path.join(folder, 'status.json'));

  return folder;
}

interface Drives {
  /** What each driven road sent to `wsl.exe`, by the label it was driven under. */
  readonly byLabel: Map<string, string[]>;
}

async function within(body: (world: FakeWorld, client: WslCareClient) => Promise<void>): Promise<void> {
  const world = fakeWorld({
    distros: [{ name: 'Ubuntu', running: true }, { name: 'Debian', running: true }], defaultDistro: 'Ubuntu', binary: 'present', version: '0.1.0',
  });
  try {
    await body(world, new WslCareClient({ runner: world.runner, platform: 'win32', env: TEST_ENV, distroSetting: () => '' }));
  } finally {
    world.dispose();
  }
}

function pickedDebian(names: readonly string[]): DistroTarget {
  const minted = listedNameOf('Debian', names);
  assert.ok(minted !== undefined);

  return pickedTarget(minted);
}

const ORIGINS = { distro: /-d (\S+)/ } as const;

function distrosOf(calls: readonly string[]): string[] {
  return calls.flatMap((c) => (c.startsWith('-d ') ? [ORIGINS.distro.exec(c)?.[1] ?? '?'] : []));
}

/** One road driven: its label and the `wsl.exe` calls it made. */
async function drive(world: FakeWorld, drives: Drives, label: string, road: () => Promise<unknown>): Promise<void> {
  const before = world.calls().length;
  await road();
  drives.byLabel.set(label, world.calls().slice(before));
}

/** The cleanup host over the real controller, the real client and the strict fake — the picked road and the default's status in the store. */
function cleanupWindow(world: FakeWorld, client: WslCareClient, store: OutcomeStore): { host: CleanupHost; recorder: ReturnType<typeof newCleanRecorder> } {
  let mono = 0;
  const controller = new CleanupController({ client, runner: world.runner, now: () => mono, sleep: (ms) => { mono += ms; return Promise.resolve(); } });
  const recorder = newCleanRecorder();
  recorder.answer = true;
  const host = new CleanupHost({
    durable: new MapStore(), controller, read: (request, target) => client.read(request, target), outcomes: store,
    askStatus: (target) => client.run('status', target), refreshPanel: () => Promise.resolve(),
    focused: () => true, ui: recordingCleanUi(recorder), timers: new ManualTimers(), now: () => mono, wallNow: () => Date.parse('2026-10-05T10:00:00.000Z'), log: () => undefined,
  });

  return { host, recorder };
}

test('RED 20 · every verb, run read, root op, config op, the terminal and every host flow carries the picked -d Debian — and no call carries -d Ubuntu', async () => {
  await within(async (world, client) => {
    const list = await client.listDistros();
    assert.ok('distros' in list, JSON.stringify(list));
    const names = list.distros.map((d) => d.name);
    const picked = pickedDebian(names);
    assert.ok(picked.kind === 'picked');
    const store = new OutcomeStore();
    store.dropUnlisted(names);
    store.set('status', await client.run('status', DEFAULT_TARGET));
    const poller = new Poller({ run: (verb, target, options) => client.run(verb, target, options), listDistros: () => client.listDistros(), store, focused: () => true, refreshSeconds: () => undefined, timers: new ManualClock(), target: () => '' });
    const archiveRecorder = newArchiveRecorder();
    archiveRecorder.picked = 'V:\\ai-archive';
    archiveRecorder.answer = true;
    const { host } = cleanupWindow(world, client, store);
    const archive = new ArchiveHost({
      read: (request, target) => client.read(request, target), target: (target) => client.rootTarget(target), runner: world.runner, outcomes: store,
      ui: recordingArchiveUi(archiveRecorder), log: () => undefined, cleanupFree: () => host.controls().enabled, archiveNow: (target) => host.archiveNow(target),
    });
    await archive.refresh();
    const drives: Drives = { byLabel: new Map() };
    const start = world.calls().length;

    // every VERB
    for (const verb of VERB_NAMES) {
      await drive(world, drives, `verb:${verb}`, () => client.run(verb, picked));
    }
    // every RUN READ
    for (const name of RUN_READ_NAMES) {
      await drive(world, drives, `read:${name}`, () => client.read(VALID_READS[name], picked));
    }
    // the terminal (built, never opened: its argv is what a terminal would start)
    const terminal = await client.terminalTarget(picked);
    assert.ok(terminal.kind === 'terminal', JSON.stringify(terminal));
    assert.deepEqual([terminal.shellPath, ...terminal.shellArgs], [WINDOWS, '-d', 'Debian', '--cd', '~']);
    // every ROOT OP, built by rootRequest over rootTarget(picked), started through the runner
    const root = await client.rootTarget(picked);
    assert.ok('wsl' in root, JSON.stringify(root));
    const ops = rootOps();
    for (const name of ROOT_OPS) {
      const request = rootRequest(root, ops[name]);
      assert.ok(request !== undefined, name);
      assert.deepEqual(request.args.slice(0, 2), ['-d', 'Debian'], `${name}: the built argv`);
      await drive(world, drives, `root:${name}`, () => callRoot(world.runner, root, ops[name]));
    }
    await drive(world, drives, 'root:confirm(A21, one process)', () => callRoot(world.runner, root, A21_CONFIRM));
    // every CONFIG OP
    const accepted = judgedFolderOf(goldenFile('archive-check-base.json'));
    assert.ok(accepted !== undefined, 'the golden check-base answer is an accepted folder');
    const configOps: readonly ConfigOp[] = [{ op: 'setBaseFolder', folder: accepted }, { op: 'clearBaseFolder' }];
    for (const op of configOps) {
      assert.deepEqual(configRequest(root, op).args.slice(0, 2), ['-d', 'Debian']);
      await drive(world, drives, `config:${op.op}`, () => world.runner(configRequest(root, op)));
    }
    // every HOST FLOW
    await drive(world, drives, 'host:clean', () => host.clean(['A4'], false, picked));
    await drive(world, drives, 'host:runFullCheck', () => host.runFullCheck(picked));
    await drive(world, drives, 'host:archiveNow', () => host.archiveNow(picked));
    await drive(world, drives, 'host:restore', () => host.restore([ENTRY], picked));
    await drive(world, drives, 'host:stopRelays', () => host.stopRelays(picked));
    store.setPicked(picked.distro, 'status', await client.run('status', picked));
    world.rewrite({ answers: answersFolder(world, 'wedged', 'status-running-wedged.json') });
    store.setPicked(picked.distro, 'status', await client.run('status', picked));
    await drive(world, drives, 'host:stop', () => host.stop(0, picked));
    world.rewrite({ answers: path.join(GOLDEN_ROOT, 'head') });
    await drive(world, drives, 'archive:choose', () => archive.choose(picked));
    await drive(world, drives, 'archive:stop', () => archive.stop(picked));
    await drive(world, drives, 'archive:archiveNow', () => archive.archiveNow(picked));
    // the install flow and the explicit start
    const opened: string[] = [];
    await drive(world, drives, 'install', () => installDaemon({
      target: () => client.terminalTarget(picked), confirm: () => Promise.resolve(true), report: () => undefined,
      openTerminal: (spec) => { opened.push(spec.shellArgs.join(' ')); return { show: () => undefined, sendText: () => undefined }; },
    }));
    assert.deepEqual(opened, ['-d Debian --cd ~']);
    await drive(world, drives, 'start', () => poller.readPicked(picked.distro, { startIfStopped: true }));

    const seen = world.calls().slice(start);
    // (a) every recorded argv that carries -d carries -d Debian, and none carries -d Ubuntu
    assert.ok(seen.some((c) => c.startsWith('-d ')), 'the roads reached the daemon at all');
    assert.deepEqual([...new Set(distrosOf(seen))], ['Debian'], `calls: ${seen.filter((c) => c.startsWith('-d ') && !c.startsWith('-d Debian ')).join(' | ')}`);
    // (b) the registers are the oracle: every row of every register was driven and produced at least one -d Debian call
    const expected = [
      ...VERB_NAMES.map((v) => `verb:${v}`), ...RUN_READ_NAMES.map((r) => `read:${r}`), ...ROOT_OPS.map((o) => `root:${o}`),
      'root:confirm(A21, one process)', 'config:setBaseFolder', 'config:clearBaseFolder',
      'host:clean', 'host:runFullCheck', 'host:archiveNow', 'host:restore', 'host:stopRelays', 'host:stop',
      'archive:choose', 'archive:stop', 'archive:archiveNow', 'install', 'start',
    ];
    assert.deepEqual([...drives.byLabel.keys()].sort(), expected.sort());
    for (const [label, calls] of drives.byLabel) {
      // the install flow only BUILDS its terminal (-l -v and the running question, then the terminal's own `-d Debian --cd ~`, asserted above)
      const reached = label === 'install' ? calls.length > 0 : calls.some((c) => c.startsWith('-d Debian '));
      assert.ok(reached, `${label} never reached Debian: ${JSON.stringify(calls)}`);
    }
    // the daemon tails seen include every verb and root op the registers name — none of them only for the default
    const tails = new Set(seen.filter((c) => c.startsWith('-d Debian ')).map((c) => c.slice(c.indexOf(DAEMON) + DAEMON.length + 1)));
    assert.ok(tails.size >= VERB_NAMES.length + ROOT_OPS.length, `${tails.size} distinct tails`);
  });
});

// ---- (c) the 19 contributed commands ----

/** Each contributed command: a flow driven above with the picked target (`distro`), or one that has no `wsl.exe -d` road at all. */
const COMMAND_CLASS: Readonly<Record<string, 'distro' | 'windowsSide' | 'opener'>> = {
  'wslCare.openPanel': 'opener',
  'wslCare.refresh': 'distro',
  'wslCare.startWsl': 'distro',
  'wslCare.installDaemon': 'distro',
  'wslCare.startWindowsTime': 'windowsSide',
  'wslCare.installWindowsTimeGuard': 'windowsSide',
  'wslCare.removeWindowsTimeGuard': 'windowsSide',
  'wslCare.installWindowsWatchTask': 'windowsSide',
  'wslCare.installWindowsArchiveTask': 'windowsSide',
  'wslCare.removeWindowsTasks': 'windowsSide',
  'wslCare.runWindowsWatchTask': 'windowsSide',
  'wslCare.runWindowsArchiveTask': 'windowsSide',
  'wslCare.chooseArchiveFolder': 'distro',
  'wslCare.stopArchiving': 'distro',
  'wslCare.archiveNow': 'distro',
  'wslCare.enableGpu': 'windowsSide',
  'wslCare.openArchive': 'opener',
  'wslCare.stopRelays': 'distro',
  'wslCare.openLogs': 'opener',
};

/** The flow label each `distro` command is driven under in the test above. */
const DRIVEN_AS: Readonly<Record<string, string>> = {
  'wslCare.refresh': 'verb:status',
  'wslCare.startWsl': 'start',
  'wslCare.installDaemon': 'install',
  'wslCare.chooseArchiveFolder': 'archive:choose',
  'wslCare.stopArchiving': 'archive:stop',
  'wslCare.archiveNow': 'archive:archiveNow',
  'wslCare.stopRelays': 'host:stopRelays',
};

test('RED 20(c) · every one of the contributed commands is classified: a distro command names the flow driven above, the rest have no wsl.exe -d road — a 20th command fails here', () => {
  const buttons = Object.keys(COMMAND_BUTTONS).sort();
  const manifest = JSON.parse(fs.readFileSync(path.join(SOURCE_ROOT, '..', 'package.json'), 'utf8')) as { contributes: { commands: { command: string }[] } };
  assert.deepEqual(manifest.contributes.commands.map((c) => c.command).sort(), buttons, 'package.json and COMMAND_BUTTONS agree (commandButtons.test.ts)');
  assert.deepEqual(Object.keys(COMMAND_CLASS).sort(), buttons, 'every command is classified, and only commands are');
  assert.equal(buttons.length, 19);
  const distro = Object.entries(COMMAND_CLASS).filter(([, kind]) => kind === 'distro').map(([command]) => command).sort();
  assert.deepEqual(Object.keys(DRIVEN_AS).sort(), distro, 'each distro command is driven, and only those');
});

/** Modules whose commands are classified `windowsSide`: they run PowerShell / the Windows exe, never the WSL client. */
const WINDOWS_SIDE_FOLDERS = ['windowsTime', 'windowsTasks', 'gpu', 'scheduler', 'powershell'] as const;

test('RED 20(c) · the windowsSide commands have no wsl.exe -d road: their modules import (as values) neither the WSL client, the root controller, the cleanup flow nor the poller (a type-only import of the journal DurableStore is the one thing they share)', () => {
  for (const folder of WINDOWS_SIDE_FOLDERS) {
    for (const file of fs.readdirSync(path.join(SOURCE_ROOT, folder)).filter((f) => f.endsWith('.ts'))) {
      const text = fs.readFileSync(path.join(SOURCE_ROOT, folder, file), 'utf8');
      const imports = [...text.matchAll(/^import (?!type )[^;]*? from '([^']+)'/gms)].map((m) => m[1] ?? '');
      const offenders = imports.filter((m) => /(^|\/)client\/WslCareClient$|\/root\/|\/poll\/|\/cleanup\//.test(m));
      assert.deepEqual(offenders, [], `${folder}/${file} reaches the WSL road`);
    }
  }
});
