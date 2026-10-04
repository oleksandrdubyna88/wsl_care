import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

import type { VerbOutcome } from '../../client/outcome';
import { VERB_NAMES, VERB_TIMEOUT_MS, VERBS, type Verb } from '../../client/verbs';
import { WslCareClient } from '../../client/WslCareClient';
import { fakeWorld, TEST_ENV, UBUNTU_RUNNING, type FakeWorld, type ScenarioInput } from '../support/fakeWorld';
import { golden, GOLDEN_ROOT, goldenSets } from '../support/paths';

/**
 * The extension's scenario harness, client tier (plan §15g m6; research/module_tests.md § The extension): the REAL
 * `WslCareClient` over the REAL runner seam (`nodeScriptRunner` → `spawnRunner` → a child process) against the strict
 * fake `wsl.exe` — the one collaborator CI cannot have. The flow list is DERIVED from `VERB_NAMES`: a verb added to the
 * client without a flow here is a missing test, not a silent pass. The extension-host tier (`@vscode/test-electron`:
 * activation, status bar, panel) is E5.S2's.
 */

async function flow(input: ScenarioInput, distro: string, body: (client: WslCareClient, world: FakeWorld) => Promise<void>): Promise<void> {
  const world = fakeWorld(input);
  try {
    await body(new WslCareClient({ runner: world.runner, platform: 'win32', env: TEST_ENV, distroSetting: () => distro }), world);
  } finally {
    world.dispose();
  }
}

function daemonCalls(world: FakeWorld): string[] {
  return world.calls().filter((c) => c.startsWith('-d '));
}

function daemonCall(verb: Verb): string {
  return ['-d', 'Ubuntu', '--cd', '/', '--exec', '/opt/wsl-care/bin/wsl-care', ...VERBS[verb]].join(' ');
}

/** What one `run(verb)` on a fresh client sends to the daemon: the verb, and — for the two verbs whose answers carry no
 * version — the one `--version` that tells the handshake which daemon answered. */
function expectedDaemonCalls(verb: Verb): string[] {
  return verb === 'preview' || verb === 'doctor' ? [daemonCall(verb), daemonCall('version')] : [daemonCall(verb)];
}

function editedAnswers(world: FakeWorld, verb: 'status' | 'preview' | 'doctor', edit: (body: Record<string, unknown>) => void): string {
  const folder = path.join(world.folder, 'answers');
  fs.mkdirSync(folder, { recursive: true });
  for (const name of ['status', 'preview', 'doctor'] as const) {
    const body = golden('head', name);
    if (name === verb) {
      edit(body);
    }
    fs.writeFileSync(path.join(folder, `${name}.json`), JSON.stringify(body));
  }

  return folder;
}

for (const set of goldenSets()) {
  for (const verb of VERB_NAMES) {
    test(`flow · ${verb} over the ${set} goldens: answered through the fake, exactly the daemon calls it needs, the System32 launcher`, async () => {
      await flow({ ...UBUNTU_RUNNING, answers: path.join(GOLDEN_ROOT, set), version: '0.1.0' }, '', async (client, world) => {
        const outcome = await client.run(verb);
        assert.equal(outcome.kind, 'answered', JSON.stringify(outcome));
        assert.deepEqual(daemonCalls(world), expectedDaemonCalls(verb), 'the verb itself, plus ONE --version only for preview / doctor with no status read (status carries productVersion)');
        assert.ok(world.files().every((f) => f === 'C:\\Windows\\System32\\wsl.exe'));
      });
    });
  }
}

for (const verb of VERB_NAMES) {
  test(`flow · ${verb} with the distribution stopped: "stopped", and the fake saw no -d call`, async () => {
    await flow({ distros: [{ name: 'Ubuntu', running: false }], defaultDistro: 'Ubuntu', binary: 'present' }, '', async (client, world) => {
      const outcome: VerbOutcome = await client.run(verb);
      assert.deepEqual(outcome, { kind: 'stopped', verb, distro: 'Ubuntu' });
      assert.deepEqual(daemonCalls(world), []);
    });
  });

  test(`flow · ${verb} with the daemon not installed: the measured signature reads as "not installed"`, async () => {
    await flow({ ...UBUNTU_RUNNING, binary: 'missing' }, 'Ubuntu', async (client) => {
      assert.deepEqual(await client.run(verb), { kind: 'notInstalled', verb, distro: 'Ubuntu' });
    });
  });

  test(`flow · ${verb} on a distribution with an old glibc: "unsupported distribution"`, async () => {
    await flow({ ...UBUNTU_RUNNING, binary: 'oldGlibc' }, 'Ubuntu', async (client) => {
      const outcome = await client.run(verb);
      assert.equal(outcome.kind, 'unsupportedDistro', JSON.stringify(outcome));
    });
  });
}

test('flow · a configured distribution that WSL does not list: refused, and the fake saw only the list question', async () => {
  await flow(UBUNTU_RUNNING, 'Debian', async (client, world) => {
    assert.equal((await client.run('status')).kind, 'distroRefused');
    assert.deepEqual(world.calls(), ['--list --quiet']);
  });
});

test('flow · an out-of-pattern distribution: refused, and the fake was never started', async () => {
  await flow(UBUNTU_RUNNING, '-u', async (client, world) => {
    assert.equal((await client.run('status')).kind, 'distroRefused');
    assert.deepEqual(world.calls(), []);
  });
});

test('flow · a daemon refusal under Serilog noise and colour: only the wsl-care: line reaches the caller', async () => {
  const stderr = '\u001b[90m[19:02:11 \u001b[33mINF\u001b[0m] WslCare.Cli: request doctor --json\n\u001b[31mwsl-care: the configuration could not be read\u001b[0m\n';
  await flow({ ...UBUNTU_RUNNING, daemonExit: { code: 2, stderr } }, 'Ubuntu', async (client) => {
    assert.deepEqual(await client.run('doctor'), { kind: 'refused', verb: 'doctor', messages: ['wsl-care: the configuration could not be read'] });
  });
});

test('flow · an unknown schemaVersion in preview blanks preview only — status still answers', async () => {
  const world = fakeWorld(UBUNTU_RUNNING);
  try {
    const answers = editedAnswers(world, 'preview', (b) => { b.schemaVersion = 3; });
    await flow({ ...UBUNTU_RUNNING, answers, version: '0.1.0' }, 'Ubuntu', async (client) => {
      assert.deepEqual(await client.run('preview'), { kind: 'needsNewerExtension', verb: 'preview', schemaVersion: 3 });
      assert.equal((await client.run('status')).kind, 'answered');
    });
  } finally {
    world.dispose();
  }
});

test('flow · a hung daemon: the runner kills wsl.exe at the ceiling and the caller hears "timed out"', async () => {
  // The client's real ceilings are tens of seconds; this flow drives the same path through a client whose runner
  // shortens the request's ceiling, so the fake's 30-second hang ends in under a second.
  const world = fakeWorld({ ...UBUNTU_RUNNING, delayMs: 30_000 });
  try {
    const short = new WslCareClient({ runner: (r) => world.runner({ ...r, timeoutMs: Math.min(r.timeoutMs, 900) }), platform: 'win32', env: TEST_ENV, distroSetting: () => 'Ubuntu' });
    const started = Date.now();
    const outcome = await short.run('status');
    assert.deepEqual(outcome, { kind: 'timedOut', verb: 'status', timeoutMs: VERB_TIMEOUT_MS.status });
    assert.ok(Date.now() - started < 10_000);
  } finally {
    world.dispose();
  }
});

