import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

import { staged, WindowsCareClient } from '../client/WindowsCareClient';
import type { ProcessResult } from '../process/runner';
import { FAKE_EXIT, type FakeTasks } from './fake/fakeWsl';
import { fakeWorld, UBUNTU_RUNNING, type FakeWorld, type ScenarioInput } from './support/fakeWorld';
import { BUNDLED_EXE_FILE, EXTENSION, LOCAL_APP_DATA, renderAnswer, STAGED_DIGEST, STAGED_EXE, stageFixture, STAGING_ROOT, TASKS_SCENARIO } from './support/windowsTasks';

/**
 * WR-E1 (PLAN_windows_runner.md §6 E1: `the fake answers tasks and refuses unknown argv`): the strict fake learns the staged exe's
 * exact path and the `tasks` argv, and answers from the daemon's render golden and the stage fixtures. Like every other shape of
 * this fake it is STRICTER than the daemon, never more permissive: it answers exactly the argv the client builds and refuses every
 * other of that file — `tasks run`, a `--timer`, a flag the daemon would take, the right argv sent to the wrong exe.
 */

const WINDOWS: ScenarioInput = { ...UBUNTU_RUNNING, bundledExe: BUNDLED_EXE_FILE, stagedExe: STAGED_EXE, tasks: TASKS_SCENARIO };

async function within(input: ScenarioInput, body: (world: FakeWorld) => Promise<void>): Promise<void> {
  const world = fakeWorld(input);
  try {
    await body(world);
  } finally {
    world.dispose();
  }
}

function clientOn(world: FakeWorld): WindowsCareClient {
  return new WindowsCareClient({ runner: world.runner, platform: 'win32', extension: EXTENSION, timeoutMs: () => 20_000, localAppData: LOCAL_APP_DATA, digestOf: () => Promise.resolve(STAGED_DIGEST) });
}

function exitOf(result: ProcessResult): { code: number; stdout: string; stderr: string } {
  assert.ok(result.kind === 'exited', JSON.stringify(result));

  return { code: result.code, stdout: result.stdout.toString('utf8'), stderr: result.stderr.toString('utf8') };
}

async function codeOf(world: FakeWorld, file: string, args: readonly string[]): Promise<number> {
  return exitOf(await world.runner({ file, args, timeoutMs: 15_000 })).code;
}

test('the client\'s tasks calls reach the fake as EXACTLY the argv and the file the plan names, and read the daemon\'s own answers', async () => {
  await within(WINDOWS, async (world) => {
    const client = clientOn(world);
    const stage = await client.tasksStage();
    const render = await client.tasksRender('watch', STAGED_DIGEST);
    const archive = await client.tasksRender('archive', STAGED_DIGEST);
    const remove = await client.tasksStageRemove();
    assert.equal(stage.kind, 'staged');
    assert.equal(render.kind, 'registrable');
    assert.equal(archive.kind, 'registrable');
    assert.equal(remove.kind, 'removed');
    assert.deepEqual(world.calls(), ['tasks stage --json', 'tasks render watch --json', 'tasks render archive --json', 'tasks stage --remove --json']);
    assert.deepEqual(world.files(), [BUNDLED_EXE_FILE, STAGED_EXE, STAGED_EXE, BUNDLED_EXE_FILE]);
    assert.equal(render.kind === 'registrable' && render.answer.xml, renderAnswer('watch from the staged copy').xml, 'the golden, byte for byte');
  });
});

test('the render answers can be scripted by the golden\'s entry name — the not-registrable one included — and by an exit (2: wrong side, 78: configuration in error)', async () => {
  const notRegistrable = { ...TASKS_SCENARIO, render: { watch: 'watch from the extension folder' } };
  await within({ ...WINDOWS, tasks: notRegistrable }, async (world) => {
    assert.equal((await clientOn(world).tasksRender('watch', STAGED_DIGEST)).kind, 'notRegistrable');
  });
  const exits: readonly (readonly [number, string, string])[] = [
    [2, 'wrongSide', '"wsl-care tasks" is the Windows binary\'s: nothing was done\n'],
    [78, 'configurationInError', 'tasks render watch: the configuration is in error (dryRun: not a boolean); no task is rendered from it\n'],
  ];
  for (const [code, kind, stderr] of exits) {
    await within({ ...WINDOWS, tasks: { ...TASKS_SCENARIO, render: { exit: { code, stderr } } } }, async (world) => {
      const result = await clientOn(world).tasksRender('watch', STAGED_DIGEST);
      assert.equal(result.kind, kind, `exit ${code}`);
      assert.equal(result.kind !== 'registrable' && 'reason' in result && result.reason, stderr.trim());
    });
  }
});

test('the stage answers can be scripted by fixture and exit: reused, refused (1), busy (75), and the remove\'s busy', async () => {
  const cases: readonly (readonly [Partial<FakeTasks>, string])[] = [
    [{ stage: { file: 'stage-reused.json', code: 0 } }, 'reused'],
    [{ stage: { file: 'stage-refused.json', code: 1 } }, 'refused'],
    [{ stage: { file: 'stage-busy.json', code: 75 } }, 'busy'],
  ];
  for (const [patch, kind] of cases) {
    await within({ ...WINDOWS, tasks: { ...TASKS_SCENARIO, ...patch } }, async (world) => {
      assert.equal((await clientOn(world).tasksStage()).kind, kind);
    });
  }
  await within({ ...WINDOWS, tasks: { ...TASKS_SCENARIO, remove: { file: 'stage-remove-busy.json', code: 75 } } }, async (world) => {
    assert.equal((await clientOn(world).tasksStageRemove()).kind, 'busy');
  });
});

test('the fake refuses every other argv of the bundled exe — tasks run, a --timer anywhere, a missing --json, an unknown task, extra words, the reads it is not asked', async () => {
  await within(WINDOWS, async (world) => {
    const refused: readonly (readonly string[])[] = [
      ['tasks', 'run', 'watch', '--json'],
      ['tasks', 'run', 'watch', '--timer', '--json'],
      ['tasks', 'run', 'archive'],
      ['tasks', 'stage'],
      ['tasks', 'stage', '--json', '--timer'],
      ['tasks', 'stage', '--remove'],
      ['tasks', 'stage', '--json', '--remove'],
      ['tasks', 'stage', '--remove', '--json', 'extra'],
      ['tasks', 'render', 'watch', '--json'],
      ['tasks', 'render', 'both', '--json'],
      ['tasks'],
      ['status', '--json'],
      ['preview', '--all', '--json'],
      ['archive', 'preview', '--json'],
      ['archive', 'check-base', 'C:\\x', '--json'],
      ['logs', '--from', '2026-10-05T10:00:00Z', '--to', '2026-10-05T11:00:00Z', '--detail', '--json'],
      ['config', 'set', 'dryRun', 'false'],
      ['act', 'A4', '--confirm'],
      ['--version', '--timer'],
      [],
    ];
    for (const args of refused) {
      assert.equal(await codeOf(world, BUNDLED_EXE_FILE, args), FAKE_EXIT.refused, JSON.stringify(args));
    }
  });
});

test('the fake refuses every other argv of the staged exe — it renders and reads, and does not stage, remove, run or print a version', async () => {
  await within(WINDOWS, async (world) => {
    const refused: readonly (readonly string[])[] = [
      ['tasks', 'stage', '--json'],
      ['tasks', 'stage', '--remove', '--json'],
      ['tasks', 'run', 'watch', '--json'],
      ['tasks', 'render', 'watch'],
      ['tasks', 'render', 'watch', '--json', '--timer'],
      ['tasks', 'render', 'cleanup', '--json'],
      ['--version'],
      ['status', '--json'],
      ['--timer'],
    ];
    for (const args of refused) {
      assert.equal(await codeOf(world, STAGED_EXE, args), FAKE_EXIT.refused, JSON.stringify(args));
    }
    assert.equal(await codeOf(world, STAGED_EXE, ['tasks', 'render', 'watch', '--json']), 0, 'and the shape it IS asked still answers');
  });
});

test('a scenario without tasks refuses every tasks argv; a staged path other than the scenario\'s stays an unknown launcher', async () => {
  await within({ ...UBUNTU_RUNNING, bundledExe: BUNDLED_EXE_FILE, stagedExe: STAGED_EXE }, async (world) => {
    assert.equal(await codeOf(world, BUNDLED_EXE_FILE, ['tasks', 'stage', '--json']), FAKE_EXIT.refused);
    assert.equal(await codeOf(world, STAGED_EXE, ['tasks', 'render', 'watch', '--json']), FAKE_EXIT.refused);
  });
  await within(WINDOWS, async (world) => {
    const another = `${STAGING_ROOT}\\${'f'.repeat(64)}\\wsl-care.exe`;
    assert.equal(await codeOf(world, another, ['tasks', 'render', 'watch', '--json']), FAKE_EXIT.wrongFile, 'another digest is not the scenario\'s staged copy');
    assert.equal(await codeOf(world, `${STAGING_ROOT}\\..\\other\\wsl-care.exe`, ['tasks', 'render', 'watch', '--json']), FAKE_EXIT.wrongFile);
  });
  await within({ ...UBUNTU_RUNNING, tasks: TASKS_SCENARIO }, async (world) => {
    assert.equal(await codeOf(world, STAGED_EXE, ['tasks', 'render', 'watch', '--json']), FAKE_EXIT.wrongFile, 'no stagedExe in the scenario: the file is unknown');
  });
});

test('the run reads answer on both Windows exes from the scenario\'s answer files, and the bundled --version is unchanged', async () => {
  await within({ ...WINDOWS, bundledVersion: '0.4.0+abc' }, async (world) => {
    const client = clientOn(world);
    const status = await client.read({ read: 'archiveStatus' });
    const list = await client.read({ read: 'archiveList' }, staged(STAGED_DIGEST));
    const runs = await client.read({ read: 'runs', from: '2026-10-05T10:00:00Z', to: '2026-10-05T11:00:00Z' });
    assert.deepEqual([status.kind, list.kind, runs.kind], ['read', 'read', 'read']);
    assert.deepEqual(await client.version(), { kind: 'version', text: '0.4.0+abc' });
    assert.deepEqual(world.files(), [BUNDLED_EXE_FILE, STAGED_EXE, BUNDLED_EXE_FILE, BUNDLED_EXE_FILE]);
  });
});

test('every call is logged with its argv and the file it was started as — the stage fixtures are plain data files', async () => {
  await within(WINDOWS, async (world) => {
    await clientOn(world).tasksStageRemove();
    const log = fs.readFileSync(path.join(world.folder, 'calls.jsonl'), 'utf8').trim().split('\n').map((line) => JSON.parse(line) as { argv: string[]; file: string });
    assert.deepEqual(log, [{ argv: ['tasks', 'stage', '--remove', '--json'], file: BUNDLED_EXE_FILE }]);
    assert.equal(stageFixture('stage-remove-removed.json').outcome, 'removed');
  });
});
