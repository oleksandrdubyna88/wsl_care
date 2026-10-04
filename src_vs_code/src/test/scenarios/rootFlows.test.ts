import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

import { WslCareClient } from '../../client/WslCareClient';
import { CleanupController } from '../../root/cleanupController';
import { ROOT_OPS, type RootOpName } from '../../root/rootCall';
import { fakeWorld, TEST_ENV, UBUNTU_RUNNING, type FakeWorld, type ScenarioInput } from '../support/fakeWorld';
import { GOLDEN_ROOT } from '../support/paths';

/**
 * The extension's scenario harness, ROOT tier (E6.S2; research/module_tests.md § The extension): the REAL cleanup
 * controller over the REAL `WslCareClient` and the REAL runner seam (`nodeScriptRunner` → a child process) against the
 * strict fake `wsl.exe`, whose root shapes are its own copy of the closed union. The flow list is DERIVED from
 * `ROOT_OPS` — `FLOWS` is typed by the op, so an op added without a flow does not compile. Only the controller's clock is
 * a test's (following an unknown detach would otherwise take a real minute).
 */

const RUN = '20000101T000000Z-1';
const ROOT = '-d Ubuntu -u root --cd / --exec /opt/wsl-care/bin/wsl-care';

interface Flow {
  readonly world: FakeWorld;
  readonly controller: CleanupController;
}

async function flow(input: ScenarioInput, body: (f: Flow) => Promise<void>): Promise<void> {
  const world = fakeWorld(input);
  try {
    const client = new WslCareClient({ runner: world.runner, platform: 'win32', env: TEST_ENV, distroSetting: () => '' });
    let now = 0;
    const controller = new CleanupController({ client, runner: world.runner, now: () => now, sleep: (ms) => { now += ms; return Promise.resolve(); } });
    await body({ world, controller });
  } finally {
    world.dispose();
  }
}

function rootCalls(world: FakeWorld): string[] {
  return world.calls().filter((c) => c.startsWith(ROOT));
}

function goldenShown(): string[] {
  const preview = JSON.parse(fs.readFileSync(path.join(GOLDEN_ROOT, 'head', 'act-a4-preview.json'), 'utf8')) as { actions: { id: string; shown: string[] }[] };
  return preview.actions.find((a) => a.id === 'A4')?.shown ?? [];
}

type Expected = { readonly kind: string; readonly root: readonly string[] };

/** One flow per root op: what the controller answers through the fake, and the exact root calls the fake received. */
const FLOWS: { readonly [K in RootOpName]: (c: CleanupController) => Promise<Expected & { readonly got: string }> } = {
  rootCheck: async (c) => ({ got: (await c.rootCheck()).kind, kind: 'rootOk', root: [`${ROOT} --version`] }),
  preview: async (c) => ({ got: (await c.preview(['A4'])).kind, kind: 'previewed', root: [`${ROOT} --version`, `${ROOT} act A4 --preview --json`] }),
  confirm: async (c) => {
    const held = await c.preview(['A4']);
    assert.ok(held.kind === 'previewed', JSON.stringify(held).slice(0, 300));
    return { got: (await c.confirm(held.preview)).kind, kind: 'accepted', root: [`${ROOT} --version`, `${ROOT} act A4 --preview --json`, `${ROOT} act A4 --confirm --manual --detach --only - --json`] };
  },
  stop: async (c) => ({ got: (await c.stop(RUN)).kind, kind: 'stopping', root: [`${ROOT} --version`, `${ROOT} act --stop ${RUN} --json`] }),
  fullCheck: async (c) => ({ got: (await c.runFullCheck()).kind, kind: 'accepted', root: [`${ROOT} --version`, `${ROOT} collect --detach --json`] }),
};

for (const op of ROOT_OPS) {
  test(`root flow · ${op}: answered through the fake, exactly its root calls, the System32 launcher`, async () => {
    await flow(UBUNTU_RUNNING, async ({ world, controller }) => {
      const outcome = await FLOWS[op](controller);
      assert.equal(outcome.got, outcome.kind);
      assert.deepEqual(rootCalls(world), outcome.root);
      assert.ok(world.files().every((f) => f === 'C:\\Windows\\System32\\wsl.exe'));
    });
  });
}

test('root flow · confirm of A4 pipes EVERY name its preview showed — all 387, on stdin, never on the command line', async () => {
  await flow(UBUNTU_RUNNING, async ({ world, controller }) => {
    const held = await controller.preview(['A4']);
    assert.ok(held.kind === 'previewed');
    assert.equal((await controller.confirm(held.preview)).kind, 'accepted');
    const shown = goldenShown();
    assert.equal(shown.length, 387);
    assert.deepEqual(world.stdins(), [shown.map((n) => `${n}\n`).join('')]);
    assert.ok(world.calls().every((c) => c.length < 300), 'no name travels as argv');
  });
});

test('root flow · a stopped distribution: refused as "stopped", and the fake saw no -d call at all', async () => {
  await flow({ distros: [{ name: 'Ubuntu', running: false }], defaultDistro: 'Ubuntu', binary: 'present' }, async ({ world, controller }) => {
    for (const run of [() => controller.rootCheck(), () => controller.preview(['A4']), () => controller.runFullCheck(), () => controller.stop(RUN)]) {
      assert.equal((await run()).kind, 'stopped');
    }
    assert.deepEqual(world.calls().filter((c) => c.startsWith('-d ')), []);
  });
});

test('root flow · a daemon that does not advertise the capabilities: "Update daemon", and NO root call reached the fake', async () => {
  await flow(UBUNTU_RUNNING, async ({ world, controller }) => {
    const answers = path.join(world.folder, 'answers-old');
    fs.mkdirSync(answers);
    for (const file of fs.readdirSync(path.join(GOLDEN_ROOT, 'head'))) {
      fs.copyFileSync(path.join(GOLDEN_ROOT, 'head', file), path.join(answers, file));
    }
    const status = JSON.parse(fs.readFileSync(path.join(answers, 'status.json'), 'utf8')) as Record<string, unknown>;
    delete status.capabilities;
    fs.writeFileSync(path.join(answers, 'status.json'), JSON.stringify(status));
    world.rewrite({ answers });
    for (const run of [() => controller.preview(['A4']), () => controller.runFullCheck(), () => controller.stop(RUN)]) {
      assert.equal((await run()).kind, 'actionsUnavailable');
    }
    assert.deepEqual(rootCalls(world), []);
  });
});

test('root flow · a detach answered "unknown" WITH its run id is handed back at once — E6.S3 follows the run id (review M3)', async () => {
  await flow({ ...UBUNTU_RUNNING, root: { detach: 'unknown' } }, async ({ world, controller }) => {
    const outcome = await controller.runFullCheck();
    assert.equal(outcome.kind, 'outcomeUnknown');
    assert.ok(outcome.kind === 'outcomeUnknown');
    assert.equal(outcome.runId, RUN);
    assert.equal(outcome.followed, undefined);
    assert.equal(world.calls().filter((c) => c.endsWith('status --json')).length, 1, 'the gate\x27s status only: nothing followed here');
  });
});

test('root flow · the daemon busy (75): the refusal, with the running block status reports', async () => {
  await flow({ ...UBUNTU_RUNNING, root: { exit: { code: 75, stderr: 'wsl-care: busy: a run is acting\n' } } }, async ({ controller }) => {
    const outcome = await controller.runFullCheck();
    assert.equal(outcome.kind, 'busy');
    assert.ok(outcome.kind === 'busy');
    assert.deepEqual(outcome.messages, ['wsl-care: busy: a run is acting']);
    assert.notEqual(outcome.running, undefined);
  });
});

test('root flow · the root check refused: "needs root", and no act reached the fake', async () => {
  await flow({ ...UBUNTU_RUNNING, root: { checkExit: { code: 1, stderr: 'refused\n' } } }, async ({ world, controller }) => {
    assert.equal((await controller.preview(['A4'])).kind, 'rootRefused');
    assert.deepEqual(rootCalls(world), [`${ROOT} --version`]);
  });
});

test('root flow · S2: with WSLENV set in the extension host, every root call reaches the fake WITHOUT it (the fake refuses one that has it)', async () => {
  const before = process.env.WSLENV;
  process.env.WSLENV = 'PATH/l:USERPROFILE/p';
  try {
    await flow(UBUNTU_RUNNING, async ({ world, controller }) => {
      assert.equal((await controller.runFullCheck()).kind, 'accepted');
      assert.equal(rootCalls(world).length, 2, 'the root check and the full check, both answered');
    });
  } finally {
    if (before === undefined) {
      delete process.env.WSLENV;
    } else {
      process.env.WSLENV = before;
    }
  }
});
