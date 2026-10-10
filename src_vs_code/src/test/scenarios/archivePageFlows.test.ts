import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

import { ArchiveController } from '../../archivePage/archiveController';
import type { ArchivePageView } from '../../archivePage/archiveView';
import { WslCareClient } from '../../client/WslCareClient';
import type { VerbOutcome } from '../../client/outcome';
import { stringsAt } from '../support/body';
import { fakeWorld, TEST_ENV, UBUNTU_RUNNING } from '../support/fakeWorld';
import { Element, runPageScript } from '../support/pageHarness';
import { EXTENSION_ROOT, goldenFile, GOLDEN_ROOT } from '../support/paths';

/**
 * The extension's scenario harness, ARCHIVE PAGE tier (plan §15s D6, E10.S2a; research/module_tests.md § The extension): the REAL
 * Archive page controller over the REAL `WslCareClient` and runner seam against the strict fake `wsl.exe`, and the REAL page script
 * (`media/archive.js`) rendering what the controller posted, in the strict harness. Browse only: one unprivileged `archive list
 * --json`, nothing else.
 */

const LIST = '-d Ubuntu --cd / --exec /opt/wsl-care/bin/wsl-care archive list --json';

function page(views: readonly ArchivePageView[]): { root: Element; posted: readonly unknown[]; click: (e: Element) => void } {
  const root = new Element('MAIN');
  const script = runPageScript(fs.readFileSync(path.join(EXTENSION_ROOT, 'media', 'archive.js'), 'utf8'), { archive: root });
  const view = views.at(-1);
  assert.ok(view !== undefined, 'the controller posted a view');
  script.message({ type: 'view', view: structuredClone(view) });
  return { root, posted: script.posted, click: (e) => script.click(e) };
}

async function statusOf(client: WslCareClient): Promise<VerbOutcome> {
  const status = await client.run('status');
  assert.equal(status.kind, 'answered');
  return status;
}

test('flow · open the Archive page: ONE unprivileged archive list --json, the page renders the golden\'s agent and — opened — its entry', async () => {
  const world = fakeWorld(UBUNTU_RUNNING);
  try {
    const client = new WslCareClient({ runner: world.runner, platform: 'win32', env: TEST_ENV, distroSetting: () => '' });
    const status = await statusOf(client);
    const views: ArchivePageView[] = [];
    const controller = new ArchiveController({ read: (request) => client.read(request), status: () => status, post: (view) => { views.push(view); } });
    await controller.receive({ type: 'ready' });
    const lists = world.calls().filter((call) => call.includes(' archive '));
    assert.deepEqual(lists, [LIST], 'one list read, exactly that argv');
    assert.ok(world.calls().every((call) => !call.includes(' -u ')), 'nothing as another user');
    const shown = page(views);
    assert.deepEqual(shown.root.all('section[data-agent]').map((s) => s.dataset.agent), ['claude-code']);

    const month = shown.root.one('button[data-month]');
    shown.click(month);
    await controller.receive(shown.posted.at(-1));
    const opened = page(views);
    const entry = goldenFile('archive-list.json').entries;
    assert.ok(Array.isArray(entry));
    assert.equal(opened.root.all('tr[data-entry]').length, entry.length);
    assert.equal(world.calls().filter((call) => call.includes(' archive ')).length, 1, 'opening a month reads nothing');
  } finally {
    world.dispose();
  }
});

test('flow · a daemon that does not advertise archive.list: the page asks nothing and says to update the daemon', async () => {
  const world = fakeWorld(UBUNTU_RUNNING);
  const folder = path.join(world.folder, 'no-list');
  fs.mkdirSync(folder);
  for (const file of fs.readdirSync(path.join(GOLDEN_ROOT, 'head'))) {
    fs.copyFileSync(path.join(GOLDEN_ROOT, 'head', file), path.join(folder, file));
  }
  const golden = goldenFile('status.json');
  fs.writeFileSync(path.join(folder, 'status.json'), JSON.stringify({ ...golden, capabilities: stringsAt(golden, 'capabilities').filter((c) => c !== 'archive.list') }));
  world.rewrite({ answers: folder });
  try {
    const client = new WslCareClient({ runner: world.runner, platform: 'win32', env: TEST_ENV, distroSetting: () => '' });
    const status = await statusOf(client);
    const views: ArchivePageView[] = [];
    const controller = new ArchiveController({ read: (request) => client.read(request), status: () => status, post: (view) => { views.push(view); } });
    await controller.receive({ type: 'ready' });
    assert.deepEqual(world.calls().filter((call) => call.includes(' archive ')), []);
    assert.match(page(views).root.textContent, /does not list its archive \(archive\.list\) — update the daemon/);
  } finally {
    world.dispose();
  }
});
