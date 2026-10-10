import assert from 'node:assert/strict';
import { test } from 'node:test';

import { ArchiveHost } from '../../archive/archiveHost';
import { newArchiveRecorder, recordingArchiveUi, type ArchiveRecorder } from '../../archive/archiveRecorder';
import { WslCareClient } from '../../client/WslCareClient';
import { OutcomeStore } from '../../state/outcomeStore';
import { fakeWorld, TEST_ENV, UBUNTU_RUNNING, type FakeWorld, type ScenarioInput } from '../support/fakeWorld';

/**
 * The extension's scenario harness, ARCHIVE tier (E10.S1a; research/module_tests.md § The extension; the coai code round's finding
 * 1): the REAL archive host — its reads, its flows, the user-layer writer — over the REAL `WslCareClient` and the REAL runner seam
 * against the strict fake `wsl.exe`. The panel's `status` comes from the client too, so the capabilities that decide what is asked
 * are the golden daemon's own. Only the dialog and the modals answer from a recorder.
 */

const DAEMON = '-d Ubuntu --cd / --exec /opt/wsl-care/bin/wsl-care';
const READS = [`${DAEMON} archive status --json`, `${DAEMON} archive preview --json`];

/** The two reads run at once, so their order in the log is the race of two processes: each adjacent pair is put in READS' order. */
function ordered(calls: readonly string[]): string[] {
  const out = [...calls];
  for (let i = 0; i + 1 < out.length; i += 1) {
    if (READS.includes(out[i] ?? '') && READS.includes(out[i + 1] ?? '')) {
      out.splice(i, 2, ...READS);
      i += 1;
    }
  }
  return out;
}

interface Scene {
  readonly world: FakeWorld;
  readonly host: ArchiveHost;
  readonly recorder: ArchiveRecorder;
  /** The daemon calls since the panel's status, in order — the two parallel reads of a refresh in READS' order. */
  readonly archiveCalls: () => string[];
}

async function scene(input: ScenarioInput, body: (s: Scene) => Promise<void>): Promise<void> {
  const world = fakeWorld(input);
  try {
    const client = new WslCareClient({ runner: world.runner, platform: 'win32', env: TEST_ENV, distroSetting: () => '' });
    const store = new OutcomeStore();
    store.set('status', await client.run('status'));
    const before = world.calls().length;
    const recorder = newArchiveRecorder();
    const host = new ArchiveHost({ read: (r) => client.read(r), target: () => client.rootTarget(), runner: world.runner, outcomes: store, ui: recordingArchiveUi(recorder), log: () => undefined });
    await body({ world, host, recorder, archiveCalls: () => ordered(world.calls().slice(before).filter((c) => c.startsWith('-d '))) });
  } finally {
    world.dispose();
  }
}

test('scenario · choose: the panel reads, the daemon judges the picked folder, Use this folder writes ITS spelling, the panel reads again', async () => {
  await scene(UBUNTU_RUNNING, async ({ host, recorder, archiveCalls }) => {
    await host.refresh();
    assert.equal(host.view().line, 'Archive folder: /mnt/v/ai-archive');
    recorder.picked = 'v:\\ai-archive';
    recorder.answer = true;
    assert.equal(await host.choose(), 'written');
    assert.deepEqual(archiveCalls(), [
      ...READS,
      `${DAEMON} archive check-base V:\\ai-archive --json`,
      `${DAEMON} config set archive.baseFolder /mnt/v/ai-archive`,
      ...READS,
    ]);
    assert.ok(archiveCalls().every((c) => !c.includes(' -u ')));
  });
});

test('scenario · choose: a folder the daemon REFUSES is told with its rule, and no config call is made', async () => {
  await scene({ ...UBUNTU_RUNNING, checkBase: 'archive-check-base-refused.json' }, async ({ host, recorder, archiveCalls }) => {
    recorder.picked = 'C:\\Users\\someone\\.claude\\archive';
    recorder.answer = true;
    assert.equal(await host.choose(), 'refused');
    assert.equal(archiveCalls().some((c) => c.includes(' config ')), false);
    assert.match(recorder.notices.at(-1)?.sentence ?? '', /cannot hold the archive \(overlap\)/);
  });
});

test('scenario · stop: its modal, then config set archive.baseFolder with the EMPTY value, then the reads again', async () => {
  await scene(UBUNTU_RUNNING, async ({ host, recorder, archiveCalls }) => {
    recorder.answer = true;
    assert.equal(await host.stop(), 'written');
    assert.deepEqual(archiveCalls(), [`${DAEMON} config set archive.baseFolder `, ...READS]);
  });
});

test('scenario · the daemon refusing the write (2) is told with its own line, and the panel is read again', async () => {
  await scene({ ...UBUNTU_RUNNING, configExit: { code: 2, stderr: 'wsl-care: archive.baseFolder: refused (gone): x. Nothing was written.\n' } }, async ({ host, recorder, archiveCalls }) => {
    recorder.answer = true;
    assert.equal(await host.stop(), 'failed');
    assert.match(recorder.notices.at(-1)?.sentence ?? '', /refused \(gone\): x\. Nothing was written\./);
    assert.deepEqual(archiveCalls().slice(-2), READS);
  });
});

test('scenario · a STOPPED distribution: no archive call reaches it, and the line says why', async () => {
  await scene({ ...UBUNTU_RUNNING, distros: [{ name: 'Ubuntu', running: false }] }, async ({ host, archiveCalls }) => {
    await host.refresh();
    assert.deepEqual(archiveCalls(), []);
    assert.equal(host.view().line, 'Archive: unavailable — WSL stopped');
  });
});
