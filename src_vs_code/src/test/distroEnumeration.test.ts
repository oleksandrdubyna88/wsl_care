import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';
import * as ts from 'typescript';

import { ArchiveHost } from '../archive/archiveHost';
import { newArchiveRecorder, recordingArchiveUi } from '../archive/archiveRecorder';
import { judgedFolderOf } from '../archive/judgedFolder';
import type { FlowOutcome } from '../cleanup/cleanFlow';
import { CleanupHost } from '../cleanup/cleanupHost';
import { newCleanRecorder, recordingCleanUi } from '../cleanup/cleanRecorder';
import { RUN_READ_NAMES, runReadTail, VERB_NAMES, VERBS, type RunRead, type RunReadName } from '../client/verbs';
import { DAEMON_PATH, WslCareClient } from '../client/WslCareClient';
import { configRequest, type ConfigOp } from '../config/configCall';
import { installDaemon } from '../install/installDaemon';
import { COMMAND_BUTTONS } from '../panel/commandButtons';
import { Poller } from '../poll/poller';
import type { ProcessResult } from '../process/runner';
import { CleanupController } from '../root/cleanupController';
import { callRoot, ROOT_OPS, rootRequest, type RootOp, type RootOpName } from '../root/rootCall';
import { entryIdsOf, runIdOf, shownProcessOf, volumeNameOf, type ActionIds } from '../root/rootIds';
import { RUN_ID_SHAPE, UTC_INSTANT_SHAPE } from '../shared/shapes';
import { OutcomeStore } from '../state/outcomeStore';
import { DEFAULT_TARGET, listedNameOf, pickedTarget, type DistroTarget } from '../wsl/distroTarget';
import { fakeWorld, TEST_ENV, type FakeWorld } from './support/fakeWorld';
import { ManualClock } from './support/manualClock';
import { ManualTimers, MapStore } from './support/memento';
import { goldenFile, GOLDEN_ROOT, SOURCE_ROOT } from './support/paths';

/**
 * E18.W1a-3 (todo/PLAN_disk_health_E18.md, RED 20 — THE ENUMERATION, the cornerstone's proof): with the PICKED target `Debian` — the machine's
 * default being `Ubuntu`, both running, both WSL 2 — every verb, every run read, every root op, every config op, the terminal and every host
 * flow is driven through the REAL client, runner and the strict fake. What it asserts, and what it does not:
 *
 * - (a) EVERY recorded call is one of the two list questions of the picked road (`-l -v`, `--list --running --quiet` — never `--list --quiet`,
 *   the default road's) or starts with `-d Debian `; none is addressed to Ubuntu.
 * - (b) the SET of daemon-call SHAPES seen equals the union built from the closed registers — `VERBS`, `runReadTail` over every read,
 *   `rootRequest` over every root op (and the confirm's other two shapes), `configRequest` over both config ops — in both directions: a
 *   register row that reached the picked road with a shape no flow produced, or a flow that sent a shape no register builds, fails here.
 * - every flow's OUTCOME is asserted (a flow that ended in a fake refusal is not "reached Debian").
 * - (c) the 19 contributed commands are classified and extension.ts's wiring is scanned: every call it makes on a WSL road names
 *   `DEFAULT_TARGET` or forwards a `target`; and `startWindowsTime`'s default-only follow-up is pinned. It does NOT run the windows-side
 *   commands (they need VS Code); it proves nothing in the extension can name a distribution from the setting.
 */

const RUN = '20000101T000000Z-1';
/** The one archived entry the scenario's list holds as restorable (removed at its source, verified) — A20's preview offers it. */
const ENTRY = '00000000000000a1';
const WINDOWS = 'C:\\Windows\\System32\\wsl.exe';
const LIST_QUESTIONS: readonly string[] = ['-l -v', '--list --running --quiet'];

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

interface RootRows {
  readonly byName: { readonly [K in RootOpName]: RootOp };
  /** The confirm's other two shapes: no A4 list (A13), and A21 with its processes. */
  readonly confirmPlain: RootOp;
  readonly confirmProcesses: RootOp;
}

function rootRows(): RootRows {
  const a4 = ['A4'] as unknown as ActionIds;
  const volume = volumeNameOf('a'.repeat(64));
  const process = shownProcessOf('1234:5678');
  const run = runIdOf(RUN);
  const entries = entryIdsOf([ENTRY]);
  assert.ok(volume !== undefined && process !== undefined && run !== undefined && entries !== undefined, 'the typed ids are mintable');

  return {
    byName: {
      preview: { op: 'preview', ids: a4 },
      confirm: { op: 'confirm', ids: a4, shown: [volume] },
      stop: { op: 'stop', runId: run },
      fullCheck: { op: 'fullCheck' },
      rootCheck: { op: 'rootCheck' },
      restorePreview: { op: 'restorePreview', entries },
      restoreConfirm: { op: 'restoreConfirm', entries },
    },
    confirmPlain: { op: 'confirm', ids: ['A13'] as unknown as ActionIds, shown: undefined },
    confirmProcesses: { op: 'confirm', ids: ['A21'] as unknown as ActionIds, shown: undefined, processes: [process] },
  };
}

// ---- the shape of a daemon call: its tail with the values replaced by what they are ----

const IDS = /^A\d+[A-Za-z0-9]*(,A\d+[A-Za-z0-9]*)*$/;

function placeholder(word: string, previous: string | undefined): string {
  if (previous === 'check-base') {
    return '<path>';
  }
  if (previous === 'archive.baseFolder') {
    return word === '' ? '' : '<folder>';
  }
  const known: readonly [RegExp, string][] = [[IDS, '<ids>'], [RUN_ID_SHAPE, '<run>'], [UTC_INSTANT_SHAPE, '<instant>'], [/^\d+:\d+$/, '<process>']];

  return known.find(([shape]) => shape.test(word))?.[1] ?? word;
}

/** `root|` or `user|`, then the tail's words with values replaced, and repeated `--process <process>` pairs collapsed to one. */
function shapeOf(argv: readonly string[]): string {
  const at = argv.indexOf(DAEMON_PATH);
  const tail = argv.slice(at + 1);
  const words = tail.map((word, i) => placeholder(word, tail[i - 1]));
  const collapsed = words.join(' ').replace(/(--process <process> ?)+/g, '--process <process> ');

  return `${argv.includes('-u') ? 'root' : 'user'}| ${collapsed}`.trimEnd();
}

function shapeOfCall(call: string): string {
  return shapeOf(call.split(' '));
}

/** The set of shapes the closed registers build for the picked target — the oracle of (b). */
function registerShapes(root: { readonly wsl: string; readonly distro: string }, rows: RootRows, configOps: readonly ConfigOp[]): Set<string> {
  const shapes = new Set<string>();
  for (const verb of VERB_NAMES) {
    shapes.add(shapeOf(['-d', root.distro, '--cd', '/', '--exec', DAEMON_PATH, ...VERBS[verb]]));
  }
  for (const name of RUN_READ_NAMES) {
    shapes.add(shapeOf(['-d', root.distro, '--cd', '/', '--exec', DAEMON_PATH, ...runReadTail(VALID_READS[name])]));
  }
  for (const op of [...ROOT_OPS.map((name) => rows.byName[name]), rows.confirmPlain, rows.confirmProcesses]) {
    const request = rootRequest(root, op);
    assert.ok(request !== undefined, op.op);
    shapes.add(shapeOf(request.args));
  }
  for (const op of configOps) {
    shapes.add(shapeOf(configRequest(root, op).args));
  }

  return shapes;
}

// ---- the world ----

function answersFolder(world: FakeWorld, name: string, status: string): string {
  const folder = path.join(world.folder, name);
  fs.mkdirSync(folder, { recursive: true });
  for (const file of fs.readdirSync(path.join(GOLDEN_ROOT, 'head'))) {
    fs.copyFileSync(path.join(GOLDEN_ROOT, 'head', file), path.join(folder, file));
  }
  fs.copyFileSync(path.join(GOLDEN_ROOT, 'head', status), path.join(folder, 'status.json'));
  const list = goldenFile('archive-list.json');
  const entry = (list.entries as Record<string, unknown>[])[0] ?? {};
  fs.writeFileSync(path.join(folder, 'archive-list.json'), JSON.stringify({ ...list, entries: [{ ...entry, entryId: ENTRY, key: `projects/p/${ENTRY}.jsonl`, status: 'sourceRemoved', verified: true }] }));

  return folder;
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

function pickedDebian(names: readonly string[]): Extract<DistroTarget, { kind: 'picked' }> {
  const minted = listedNameOf('Debian', names);
  assert.ok(minted !== undefined);
  const target = pickedTarget(minted);
  assert.ok(target.kind === 'picked');

  return target;
}

/** One road driven: the `wsl.exe` calls it made. */
interface Driven {
  readonly calls: string[];
}

async function drive(world: FakeWorld, drives: Map<string, Driven>, label: string, road: () => Promise<unknown>): Promise<unknown> {
  const before = world.calls().length;
  const result = await road();
  drives.set(label, { calls: world.calls().slice(before) });

  return result;
}

function handedOff(result: unknown): string {
  const flow = result as FlowOutcome | undefined;

  return flow?.kind === 'handedOff' ? `handedOff:${flow.outcome.kind}` : String(flow?.kind);
}

function exitedOk(result: unknown): string {
  const process = result as ProcessResult;

  return process.kind === 'exited' && process.code === 0 ? 'exit0' : `refused:${JSON.stringify(process).slice(0, 120)}`;
}

function cleanupWindow(world: FakeWorld, client: WslCareClient, store: OutcomeStore): CleanupHost {
  let mono = 0;
  const controller = new CleanupController({ client, runner: world.runner, now: () => mono, sleep: (ms) => { mono += ms; return Promise.resolve(); } });
  const recorder = newCleanRecorder();
  recorder.answer = true;

  return new CleanupHost({
    durable: new MapStore(), controller, read: (request, target) => client.read(request, target), outcomes: store,
    askStatus: (target) => client.run('status', target), refreshPanel: () => Promise.resolve(),
    focused: () => true, ui: recordingCleanUi(recorder), timers: new ManualTimers(), now: () => mono, wallNow: () => Date.parse('2026-10-05T10:00:00.000Z'), log: () => undefined,
  });
}

/** A run handed off stays in the journal until a poll ends it — and it greys the next flow of its distribution (correctly): this test ends each one by hand. */
async function settled(host: CleanupHost): Promise<void> {
  for (const entry of host.journal.entries()) {
    await host.journal.remove(entry.id);
  }
}

/** The host flows, typed by their names: a flow added to the list below without a row here does not compile. */
const HOST_FLOWS = ['clean', 'runFullCheck', 'archiveNow', 'restore', 'stopRelays', 'stop'] as const;

type HostFlow = (typeof HOST_FLOWS)[number];

const HOST_FLOW_ANSWERS: { readonly [K in HostFlow]: string } = {
  clean: 'handedOff:accepted', runFullCheck: 'handedOff:accepted', archiveNow: 'handedOff:accepted', restore: 'handedOff:accepted', stopRelays: 'handedOff:accepted', stop: 'handedOff:stopping',
};

test('RED 20 · every verb, run read, root op, config op, the terminal and every host flow reaches Debian — and nothing is addressed to Ubuntu', async () => {
  await within(async (world, client) => {
    world.rewrite({ answers: answersFolder(world, 'base', 'status.json') });
    const list = await client.listDistros();
    assert.ok('distros' in list, JSON.stringify(list));
    const names = list.distros.map((d) => d.name);
    const picked = pickedDebian(names);
    const store = new OutcomeStore();
    store.dropUnlisted(names);
    store.set('status', await client.run('status', DEFAULT_TARGET));
    const host = cleanupWindow(world, client, store);
    const poller = new Poller({ run: (verb, target, options) => client.run(verb, target, options), listDistros: () => client.listDistros(), store, focused: () => true, refreshSeconds: () => undefined, timers: new ManualClock(), target: () => '' });
    const archiveRecorder = newArchiveRecorder();
    archiveRecorder.picked = 'V:\\ai-archive';
    archiveRecorder.answer = true;
    const archivedNow: unknown[] = [];
    const archive = new ArchiveHost({
      read: (request, target) => client.read(request, target), target: (target) => client.rootTarget(target), runner: world.runner, outcomes: store,
      ui: recordingArchiveUi(archiveRecorder), log: () => undefined, cleanupFree: () => host.controls().enabled,
      archiveNow: async (target) => { const outcome = await host.archiveNow(target); archivedNow.push(outcome); return outcome; },
    });
    await archive.refresh();
    const drives = new Map<string, Driven>();
    const start = world.calls().length;
    const answers = new Map<string, string>();

    for (const verb of VERB_NAMES) {
      answers.set(`verb:${verb}`, ((await drive(world, drives, `verb:${verb}`, () => client.run(verb, picked))) as { kind: string }).kind);
    }
    for (const name of RUN_READ_NAMES) {
      answers.set(`read:${name}`, ((await drive(world, drives, `read:${name}`, () => client.read(VALID_READS[name], picked))) as { kind: string }).kind);
    }
    const terminal = await client.terminalTarget(picked);
    assert.ok(terminal.kind === 'terminal', JSON.stringify(terminal));
    assert.deepEqual([terminal.shellPath, ...terminal.shellArgs], [WINDOWS, '-d', 'Debian', '--cd', '~']);

    const root = await client.rootTarget(picked);
    assert.ok('wsl' in root, JSON.stringify(root));
    const rows = rootRows();
    const rootCases: readonly (readonly [string, RootOp])[] = [
      ...ROOT_OPS.map((name) => [`root:${name}`, rows.byName[name]] as const),
      ['root:confirm(plain ids)', rows.confirmPlain], ['root:confirm(A21, one process)', rows.confirmProcesses],
    ];
    for (const [label, op] of rootCases) {
      const request = rootRequest(root, op);
      assert.ok(request !== undefined, label);
      assert.deepEqual(request.args.slice(0, 2), ['-d', 'Debian'], `${label}: the built argv`);
      answers.set(label, exitedOk(await drive(world, drives, label, () => callRoot(world.runner, root, op))));
    }
    const accepted = judgedFolderOf(goldenFile('archive-check-base.json'));
    assert.ok(accepted !== undefined, 'the golden check-base answer is an accepted folder');
    const configOps: readonly ConfigOp[] = [{ op: 'setBaseFolder', folder: accepted }, { op: 'clearBaseFolder' }];
    for (const op of configOps) {
      answers.set(`config:${op.op}`, exitedOk(await drive(world, drives, `config:${op.op}`, () => world.runner(configRequest(root, op)))));
    }

    // every HOST FLOW — the typed table below is keyed by HOST_FLOWS
    store.setPicked(picked.distro, 'status', await client.run('status', picked));
    const flows: { readonly [K in HostFlow]: () => Promise<unknown> } = {
      clean: () => host.clean(['A4'], false, picked),
      runFullCheck: () => host.runFullCheck(picked),
      archiveNow: () => host.archiveNow(picked),
      restore: () => host.restore([ENTRY], picked),
      stopRelays: () => host.stopRelays(picked),
      stop: async () => {
        world.rewrite({ answers: answersFolder(world, 'wedged', 'status-running-wedged.json') });
        store.setPicked(picked.distro, 'status', await client.run('status', picked));
        const outcome = await host.stop(0, picked);
        world.rewrite({ answers: answersFolder(world, 'base', 'status.json') });
        store.setPicked(picked.distro, 'status', await client.run('status', picked));

        return outcome;
      },
    };
    for (const flow of HOST_FLOWS) {
      answers.set(`host:${flow}`, handedOff(await drive(world, drives, `host:${flow}`, flows[flow])));
      await settled(host);
    }
    answers.set('archive:choose', String(await drive(world, drives, 'archive:choose', () => archive.choose(picked))));
    answers.set('archive:stop', String(await drive(world, drives, 'archive:stop', () => archive.stop(picked))));
    await drive(world, drives, 'archive:archiveNow', () => archive.archiveNow(picked));
    await settled(host);
    answers.set('archive:archiveNow', handedOff(archivedNow.at(-1)));
    const opened: string[] = [];
    answers.set('install', ((await drive(world, drives, 'install', () => installDaemon({
      target: () => client.terminalTarget(picked), confirm: () => Promise.resolve(true), report: () => undefined,
      openTerminal: (spec) => { opened.push(spec.shellArgs.join(' ')); return { show: () => undefined, sendText: () => undefined }; },
    }))) as { kind: string }).kind);
    assert.deepEqual(opened, ['-d Debian --cd ~']);
    await drive(world, drives, 'start', () => poller.readPicked(picked.distro, { startIfStopped: true }));
    answers.set('start', store.snapshotOf(picked.distro).status?.kind ?? 'nothing stored');

    // every road ANSWERED — a flow that ended in a refusal did not "reach Debian"
    const expectedAnswers = new Map<string, string>([
      ...VERB_NAMES.map((v) => [`verb:${v}`, 'answered'] as const), ...RUN_READ_NAMES.map((r) => [`read:${r}`, 'read'] as const),
      ...rootCases.map(([label]) => [label, 'exit0'] as const), ['config:setBaseFolder', 'exit0'], ['config:clearBaseFolder', 'exit0'],
      ...HOST_FLOWS.map((f) => [`host:${f}`, HOST_FLOW_ANSWERS[f]] as const),
      ['archive:choose', 'written'], ['archive:stop', 'written'], ['archive:archiveNow', 'handedOff:accepted'], ['install', 'typed'], ['start', 'answered'],
    ]);
    assert.deepEqual(Object.fromEntries([...answers].sort()), Object.fromEntries([...expectedAnswers].sort()));
    assert.deepEqual([...drives.keys()].sort(), [...expectedAnswers.keys()].sort(), 'every row was driven');

    // (a) EVERY recorded call: a list question of the picked road, or addressed to Debian
    const seen = world.calls().slice(start);
    const strays = seen.filter((c) => !LIST_QUESTIONS.includes(c) && !c.startsWith('-d Debian '));
    assert.deepEqual(strays, [], 'a call that is neither a picked-road list question nor addressed to Debian');
    for (const [label, driven] of drives) {
      if (label !== 'install' && label !== 'archive:archiveNow') {
        assert.ok(driven.calls.some((c) => c.startsWith('-d Debian ')), `${label} never reached Debian: ${JSON.stringify(driven.calls)}`);
      }
    }
    // (b) the shapes seen equal the shapes the closed registers build
    const seenShapes = new Set(seen.filter((c) => c.startsWith('-d Debian ')).map(shapeOfCall));
    const wanted = registerShapes(root, rows, configOps);
    assert.deepEqual([...seenShapes].sort(), [...wanted].sort());
  });
});

// ---- (c) the contributed commands ----

type Class = 'distro' | 'windowsSide' | 'opener' | 'defaultOnly';

/** Each contributed command: a flow driven above with the picked target, one that has no `wsl.exe -d` road, or a default-only follow-up. */
const COMMAND_CLASS: Readonly<Record<string, Class>> = {
  'wslCare.openPanel': 'opener',
  'wslCare.refresh': 'distro',
  'wslCare.startWsl': 'distro',
  'wslCare.installDaemon': 'distro',
  // Not windowsSide: after the elevated fix, `afterDone` runs *Run full check now* — for the DEFAULT distribution only (extension.ts).
  'wslCare.startWindowsTime': 'defaultOnly',
  'wslCare.installWindowsTimeGuard': 'windowsSide',
  'wslCare.removeWindowsTimeGuard': 'windowsSide',
  'wslCare.installWindowsWatchTask': 'windowsSide',
  'wslCare.installWindowsArchiveTask': 'windowsSide',
  'wslCare.removeWindowsTasks': 'windowsSide',
  'wslCare.runWindowsWatchTask': 'windowsSide',
  'wslCare.runWindowsArchiveTask': 'windowsSide',
  'wslCare.installWindowsCleanupTask': 'windowsSide',
  'wslCare.previewWindowsTempCleanup': 'windowsSide',
  'wslCare.runWindowsCleanupTask': 'windowsSide',
  'wslCare.chooseArchiveFolder': 'distro',
  'wslCare.stopArchiving': 'distro',
  'wslCare.archiveNow': 'distro',
  'wslCare.enableGpu': 'windowsSide',
  'wslCare.openArchive': 'opener',
  'wslCare.stopRelays': 'distro',
  'wslCare.openLogs': 'opener',
};

/** The flow each `distro` command is driven as in the enumeration above. */
const DRIVEN_AS: Readonly<Record<string, string>> = {
  'wslCare.refresh': 'verb:status',
  'wslCare.startWsl': 'start',
  'wslCare.installDaemon': 'install',
  'wslCare.chooseArchiveFolder': 'archive:choose',
  'wslCare.stopArchiving': 'archive:stop',
  'wslCare.archiveNow': 'archive:archiveNow',
  'wslCare.stopRelays': 'host:stopRelays',
};

test('RED 20(c) · all 22 commands are classified; a distro command names the flow driven above — a 23rd command fails here', () => {
  const buttons = Object.keys(COMMAND_BUTTONS).sort();
  const manifest = JSON.parse(fs.readFileSync(path.join(SOURCE_ROOT, '..', 'package.json'), 'utf8')) as { contributes: { commands: { command: string }[] } };
  assert.deepEqual(manifest.contributes.commands.map((c) => c.command).sort(), buttons, 'package.json and COMMAND_BUTTONS agree (commandButtons.test.ts)');
  assert.deepEqual(Object.keys(COMMAND_CLASS).sort(), buttons, 'every command is classified, and only commands are');
  assert.equal(buttons.length, 22);
  const distro = Object.entries(COMMAND_CLASS).filter(([, kind]) => kind === 'distro').map(([command]) => command).sort();
  assert.deepEqual(Object.keys(DRIVEN_AS).sort(), distro, 'each distro command is driven, and only those');
});

/** The members of the WSL roads the extension's own objects expose — a call on one of them names its distribution. */
const ROAD_METHODS: ReadonlySet<string> = new Set(['clean', 'runFullCheck', 'stop', 'archiveNow', 'restore', 'stopRelays', 'choose', 'rootTarget', 'terminalTarget', 'run', 'read', 'askStatus', 'readPicked']);
const ROAD_RECEIVERS = /(^|\.)(host|archive|client|poller)$/;
/** An argument that cannot name a distribution from the setting: the default token, or a target forwarded by a caller that holds one. */
const TARGET_ARGUMENTS: ReadonlySet<string> = new Set(['DEFAULT_TARGET', 'target']);

/** The calls of `text` on a WSL road whose arguments carry no `DEFAULT_TARGET` / forwarded `target` — read with the parser. */
function unnamedRoadCalls(text: string): string[] {
  const source = ts.createSourceFile('scan.ts', text, ts.ScriptTarget.ES2022, true, ts.ScriptKind.TS);
  const found: string[] = [];
  const visit = (node: ts.Node): void => {
    if (ts.isCallExpression(node) && ts.isPropertyAccessExpression(node.expression) && ROAD_METHODS.has(node.expression.name.text) && ROAD_RECEIVERS.test(node.expression.expression.getText())) {
      const named = node.arguments.some((arg) => ts.isIdentifier(arg) && TARGET_ARGUMENTS.has(arg.text));
      if (!named) {
        found.push(node.getText().slice(0, 80));
      }
    }
    ts.forEachChild(node, visit);
  };
  visit(source);

  return found;
}

test('RED 20(c) · extension.ts names a distribution on EVERY WSL road only through DEFAULT_TARGET or a forwarded target — never from the setting', () => {
  const text = fs.readFileSync(path.join(SOURCE_ROOT, 'extension.ts'), 'utf8');
  assert.deepEqual(unnamedRoadCalls(text), []);
  const roadCalls = text.match(/\b(host|archive|client|poller)\.(clean|runFullCheck|stop|archiveNow|restore|stopRelays|choose|rootTarget|terminalTarget|run|read|askStatus)\(/g) ?? [];
  assert.ok(roadCalls.length >= 12, `the scan sees the extension's road calls (${roadCalls.length})`);
});

test('the road scan finds a planted call that names no target, a setting-derived one, and ignores the Windows-side objects', () => {
  const planted = [
    'void host.clean(rowIds, selected);',
    'void parts.archive.choose(distroFromSetting());',
    'void client.read(request, distroSetting());',
    'void parts.guard.run(op); void parts.tasks.run(op);',
    'void host.runFullCheck(DEFAULT_TARGET); void client.run(verb, target, options);',
  ].join('\n');
  assert.deepEqual(unnamedRoadCalls(planted), ['host.clean(rowIds, selected)', 'parts.archive.choose(distroFromSetting())', 'client.read(request, distroSetting())']);
});

test('RED 20(c) · startWindowsTime is default-only: its afterDone follow-up is *Run full check now* for DEFAULT_TARGET, written in extension.ts', () => {
  const text = fs.readFileSync(path.join(SOURCE_ROOT, 'extension.ts'), 'utf8');
  assert.match(text, /afterDone: \(\) => \{ void parts\.host\.runFullCheck\(DEFAULT_TARGET\); \}/);
  assert.equal(COMMAND_CLASS['wslCare.startWindowsTime'], 'defaultOnly');
});
