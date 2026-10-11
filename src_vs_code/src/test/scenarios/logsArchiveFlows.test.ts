import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

import { WslCareClient } from '../../client/WslCareClient';
import type { VerbOutcome } from '../../client/outcome';
import { LogsController } from '../../logsPage/logsController';
import type { LogsView } from '../../logsPage/logsView';
import { fakeWorld, TEST_ENV, UBUNTU_RUNNING, type FakeWorld } from '../support/fakeWorld';
import { MapStore } from '../support/memento';
import { answered, headBody } from '../support/outcomes';
import { Element, runPageScript } from '../support/pageHarness';
import { EXTENSION_ROOT } from '../support/paths';

/**
 * Plan §15s, E10.S2c (D8; the plan round's #0): the Logs page over the daemon's OWN archive run goldens — the REAL client asks the
 * strict fake `wsl.exe` for `runs show <runId> --json`, the fake answers with `runs-show-archive.json` / `runs-show-restore.json`
 * (each made by the daemon's engine and read back by its CLI), the REAL Logs controller builds the view and the page script
 * (`media/logs.js`) renders it in the strict harness. What a person sees on *This run* is checked: the run named, its table titled.
 */

const RUN = '20000101T000000Z-1';

function statusNaming(runId: string): VerbOutcome {
  const body = headBody('status');
  body.lastCleanup = { runId, startedAt: '2000-01-20T12:00:00+00:00', trigger: 'manual', freedBytes: 0, count: 1 };
  return answered('status', body);
}

async function thisRun(world: FakeWorld): Promise<Element> {
  const client = new WslCareClient({ runner: world.runner, platform: 'win32', env: TEST_ENV, distroSetting: () => '' });
  const views: LogsView[] = [];
  const controller = new LogsController({ durable: new MapStore(), read: (request) => client.read(request), status: () => statusNaming(RUN), post: (view) => { views.push(view); }, wallNow: () => Date.parse('2000-01-21T09:00:00Z') });
  await controller.receive({ type: 'thisRun' });
  const view = views.at(-1);
  assert.ok(view !== undefined);
  const root = new Element('MAIN');
  runPageScript(fs.readFileSync(path.join(EXTENSION_ROOT, 'media', 'logs.js'), 'utf8'), { logs: root }).message({ type: 'view', view: structuredClone(view) });
  assert.ok(world.calls().some((call) => call.endsWith(`runs show ${RUN} --json`)), 'the one read: runs show of the last run, unprivileged');
  return root;
}

function title(root: Element, table: string): string {
  return root.one(`[data-table-of="${table}"]`).one('[data-title]').textContent;
}

test('flow · This run of an ARCHIVE run: the daemon-made run renders as "A13 — archive of AI sessions" with its per-agent table', async () => {
  const world = fakeWorld(UBUNTU_RUNNING);
  try {
    world.rewrite({ runsShow: 'runs-show-archive.json' });
    const root = await thisRun(world);
    assert.equal(root.one('table[data-table="actions"]').one('tr[data-row]').all('[data-cell]')[0]?.textContent, 'A13 — archive of AI sessions');
    assert.equal(title(root, 'removed-0'), 'A13 — AI sessions archived, per agent');
    assert.match(root.one('table[data-table="removed-0"]').textContent, /claude-code/);
  } finally {
    world.dispose();
  }
});

test('flow · This run of a RESTORE run: the daemon-made run renders as "A20 — restore of archived sessions", its sessions by entry id', async () => {
  const world = fakeWorld(UBUNTU_RUNNING);
  try {
    world.rewrite({ runsShow: 'runs-show-restore.json' });
    const root = await thisRun(world);
    assert.equal(root.one('table[data-table="actions"]').one('tr[data-row]').all('[data-cell]')[0]?.textContent, 'A20 — restore of archived sessions');
    assert.equal(title(root, 'removed-0'), 'A20 — archived sessions restored');
    assert.match(root.one('table[data-table="removed-0"]').one('tr[data-row]').all('[data-cell]')[0]?.textContent ?? '', /^[0-9a-f]{16}$/);
  } finally {
    world.dispose();
  }
});
