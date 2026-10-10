import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

import type { ReadOutcome, VerbOutcome } from '../../client/outcome';
import { runReadTail, type RunRead } from '../../client/verbs';
import { lastCleanupPeriod, LOGS_PERIOD_KEY, LogsController } from '../../logsPage/logsController';
import type { LogsView } from '../../logsPage/logsView';
import { answered, headBody } from '../support/outcomes';
import { MapStore } from '../support/memento';
import { GOLDEN_ROOT } from '../support/paths';
import { withZone } from '../support/zone';

/**
 * The Logs page's host side (plan §16 E6.S4 acceptance): what each period and each page message makes the host ASK — the
 * exact argv of every read — that the selection is persisted BEFORE it is read and survives a reload (a second controller
 * over the same `globalState`-shaped store), and that a malicious period, run id or extra field from the webview starts
 * no process at all. The reads are the client's (`WslCareClient.read`); here they are recorded and answered from the goldens.
 */

const RUN = '20261005T080000Z-4242';
/** 2026-10-05 12:00 in Kyiv. */
const NOW = Date.parse('2026-10-05T09:00:00Z');

function golden(name: string): Record<string, unknown> {
  return JSON.parse(fs.readFileSync(path.join(GOLDEN_ROOT, 'head', name), 'utf8')) as Record<string, unknown>;
}

const ANSWERS: { readonly [K in RunRead['read']]: string } = { logs: 'logs-local-day.json', runs: 'runs-local-day.json', runsShow: 'runs-show-done.json', archiveStatus: 'archive-status.json', archivePreview: 'archive-preview.json', archiveCheckBase: 'archive-check-base.json' };

/** A status answer naming `runId` as the last cleanup (or none), with the daemon's capabilities. */
function statusWith(runId: string | undefined, capabilities?: readonly string[]): VerbOutcome {
  const body = headBody('status');
  if (runId !== undefined) {
    body.lastCleanup = { runId, startedAt: '2026-10-05T08:00:00+00:00', trigger: 'manual', freedBytes: 1000, count: 1 };
  }
  if (capabilities !== undefined) {
    body.capabilities = [...capabilities];
  }
  return answered('status', body);
}

class World {
  readonly store: MapStore;
  readonly reads: RunRead[] = [];
  /** The order things happened in: `write <key>` and `read <tail>`. */
  readonly events: string[] = [];
  readonly views: LogsView[] = [];
  status: VerbOutcome | undefined = statusWith(RUN);
  now = NOW;
  answer: (request: RunRead) => ReadOutcome | Promise<ReadOutcome> = (request) => ({ kind: 'read', read: request.read, distro: 'Ubuntu', body: golden(ANSWERS[request.read]) });
  readonly controller: LogsController;

  constructor(store = new MapStore()) {
    this.store = store;
    const update = store.update.bind(store);
    store.update = (key, value) => update(key, value).then(() => { this.events.push(`write ${key}`); });
    this.controller = new LogsController({
      durable: store,
      read: (request) => {
        this.reads.push(request);
        this.events.push(`read ${runReadTail(request).join(' ')}`);
        return Promise.resolve(this.answer(request));
      },
      status: () => this.status,
      post: (view) => { this.views.push(view); },
      wallNow: () => this.now,
    });
  }

  tails(): string[] {
    return this.reads.map((r) => runReadTail(r).join(' '));
  }

  view(): LogsView {
    const last = this.views.at(-1);
    assert.ok(last !== undefined, 'a view was posted');
    return last;
  }
}

/** Every test runs in Kyiv, the zone held across its awaits (`withZone` restores it once the promise settles, review K1). */
const KYIV = 'Europe/Kyiv';

test('each period → its exact argv: Today and Yesterday as local midnights, a day, a range, This run as runs show of the LAST CLEANUP\'s id', async () => {
  await withZone(KYIV, async () => {
    const w = new World();
    await w.controller.receive({ type: 'today' });
    await w.controller.receive({ type: 'yesterday' });
    await w.controller.receive({ type: 'day', day: '2026-10-01' });
    await w.controller.receive({ type: 'range', from: '2026-09-30', to: '2026-10-02' });
    await w.controller.receive({ type: 'thisRun' });
    assert.deepEqual(w.tails(), [
      'logs --from 2026-10-04T21:00:00Z --to 2026-10-05T21:00:00Z --json', 'runs --from 2026-10-04T21:00:00Z --to 2026-10-05T21:00:00Z --json',
      'logs --from 2026-10-03T21:00:00Z --to 2026-10-04T21:00:00Z --json', 'runs --from 2026-10-03T21:00:00Z --to 2026-10-04T21:00:00Z --json',
      'logs --from 2026-09-30T21:00:00Z --to 2026-10-01T21:00:00Z --json', 'runs --from 2026-09-30T21:00:00Z --to 2026-10-01T21:00:00Z --json',
      'logs --from 2026-09-29T21:00:00Z --to 2026-10-02T21:00:00Z --json', 'runs --from 2026-09-29T21:00:00Z --to 2026-10-02T21:00:00Z --json',
      `runs show ${RUN} --json`,
    ]);
    assert.ok(w.reads.every((r) => !('detail' in r)), 'nothing but the typed reads');
  });
});

test('the selection is PERSISTED before it is read, and survives a reload: a new controller over the same store asks the same window', async () => {
  await withZone(KYIV, async () => {
    const store = new MapStore();
    const first = new World(store);
    await first.controller.receive({ type: 'day', day: '2026-10-01' });
    assert.deepEqual(first.events.slice(0, 2), [`write ${LOGS_PERIOD_KEY}`, 'read logs --from 2026-09-30T21:00:00Z --to 2026-10-01T21:00:00Z --json'], 'written first, then read');
    assert.deepEqual(store.get(LOGS_PERIOD_KEY), { kind: 'day', day: '2026-10-01' });

    const reloaded = new World(store);
    assert.deepEqual(reloaded.controller.period(), { kind: 'day', day: '2026-10-01' });
    await reloaded.controller.receive({ type: 'ready' });
    assert.deepEqual(reloaded.tails(), ['logs --from 2026-09-30T21:00:00Z --to 2026-10-01T21:00:00Z --json', 'runs --from 2026-09-30T21:00:00Z --to 2026-10-01T21:00:00Z --json']);
    assert.equal(reloaded.view().picker.dayPressed, true, 'the page shows the restored selection');
  });
});

test('This run survives a reload with ITS run id — the one the host read, even after status names a newer cleanup', async () => {
  await withZone(KYIV, async () => {
    const store = new MapStore();
    const first = new World(store);
    await first.controller.receive({ type: 'thisRun' });
    const reloaded = new World(store);
    reloaded.status = statusWith('20261005T090000Z-77');
    await reloaded.controller.receive({ type: 'ready' });
    assert.deepEqual(reloaded.tails(), [`runs show ${RUN} --json`]);
    assert.match(reloaded.view().periodLabel, new RegExp(RUN));
  });
});

test('a persisted value that is not a period (tampered, or from another build) is no period: the page opens on Today', async () => {
  await withZone(KYIV, async () => {
    for (const stored of [{ kind: 'thisRun', runId: `${RUN} --json` }, { kind: 'day', day: '2026-02-30' }, 'today', 7, { kind: 'week' }]) {
      const store = new MapStore();
      store.values.set(LOGS_PERIOD_KEY, stored);
      const w = new World(store);
      assert.deepEqual(w.controller.period(), { kind: 'today' }, JSON.stringify(stored));
    }
  });
});

test('a malicious period, run id or extra field from the webview starts NO process — and nothing is written', async () => {
  await withZone(KYIV, async () => {
    const bad: readonly unknown[] = [
      { type: 'thisRun', runId: '20261005T080000Z-1' },
      { type: 'day', day: '2026-10-01; rm -rf /' },
      { type: 'day', day: '2026-02-30' },
      { type: 'day', day: '2026-10-01', from: '2026-01-01T00:00:00Z' },
      { type: 'range', from: '2026-10-01', to: '2026-10-02 --detail' },
      { type: 'expand', index: 0, runId: RUN },
      { type: 'expand', index: -1 },
      { type: 'logs', argv: ['--period', 'today'] },
      { type: '__proto__' },
      null,
      'today',
    ];
    const w = new World();
    for (const message of bad) {
      await w.controller.receive(message);
    }
    assert.deepEqual(w.reads, []);
    assert.equal(w.store.writes, 0);
  });
});

test('a well-formed message the host cannot honour starts no process either — a range ending first, This run with no cleanup, a run index past the list', async () => {
  await withZone(KYIV, async () => {
    const w = new World();
    await w.controller.receive({ type: 'range', from: '2026-10-02', to: '2026-09-30' });
    assert.deepEqual(w.reads, []);
    assert.match(w.view().notice, /range must end on or after its first day/);
    w.status = statusWith(undefined);
    await w.controller.receive({ type: 'thisRun' });
    assert.deepEqual(w.reads, []);
    assert.match(w.view().notice, /no cleanup is recorded/);
    assert.equal(w.view().periods.find((p) => p.id === 'thisRun')?.enabled, false);
    await w.controller.receive({ type: 'today' });
    const before = w.reads.length;
    await w.controller.receive({ type: 'expand', index: 3 });
    assert.equal(w.reads.length, before, 'the runs golden has 3 lines: index 3 is not one of them');
  });
});

test('expanding a run reads runs show of the run id the HOST read at that index; collapsing drops it; a line with no daemon run id is not asked', async () => {
  await withZone(KYIV, async () => {
    const w = new World();
    await w.controller.receive({ type: 'today' });
    await w.controller.receive({ type: 'expand', index: 1 });
    assert.equal(w.tails().at(-1), 'runs show 20000101T000000Z-1 --json');
    assert.equal(w.view().runList.rows[1]?.expanded, true);
    assert.equal(w.view().runList.rows[1]?.detail[0]?.state, 'answered');
    await w.controller.receive({ type: 'collapse', index: 1 });
    assert.equal(w.view().runList.rows[1]?.expanded, false);

    const odd = new World();
    const runs = golden('runs-local-day.json');
    odd.answer = (request) => ({ kind: 'read', read: request.read, distro: 'Ubuntu', body: request.read === 'runs' ? { ...runs, runs: [{ ...(runs.runs as object[])[0], runId: '../../etc/passwd' }] } : golden(ANSWERS[request.read]) });
    await odd.controller.receive({ type: 'today' });
    const before = odd.reads.length;
    await odd.controller.receive({ type: 'expand', index: 0 });
    assert.equal(odd.reads.length, before);
    assert.match(odd.view().notice, /no run id the daemon writes/);
  });
});

test('a day older than the retention is clamped to the oldest kept day, and the page says so', async () => {
  await withZone(KYIV, async () => {
    const w = new World();
    await w.controller.receive({ type: 'day', day: '2026-01-01' });
    assert.equal(w.tails()[0], 'logs --from 2026-07-07T21:00:00Z --to 2026-07-08T21:00:00Z --json');
    assert.match(w.view().periodLabel, /clamped to the 90 days the daemon keeps/);
  });
});

test('a daemon that does not advertise logs.instantRange is not asked for a day; one without runs.show not for This run — the page says to update it', async () => {
  await withZone(KYIV, async () => {
    const w = new World();
    w.status = statusWith(RUN, ['act.shownList', 'running.block']);
    await w.controller.receive({ type: 'today' });
    await w.controller.receive({ type: 'thisRun' });
    assert.deepEqual(w.reads, []);
    assert.match(w.view().notice, /update the daemon/);
    w.status = undefined;
    await w.controller.receive({ type: 'today' });
    assert.equal(w.reads.length, 2, 'no status read yet: asked, and a refusal would be shown');
  });
});

test('a failed read is shown with its reason; an answer to a period no longer selected is dropped (the newest selection wins)', async () => {
  await withZone(KYIV, async () => {
    const w = new World();
    w.answer = (request) => ({ kind: 'timedOut', timeoutMs: 20_000, read: request.read });
    await w.controller.receive({ type: 'today' });
    assert.equal(w.view().blocks[0]?.state, 'failed');
    assert.match(w.view().blocks[0]?.notes[0] ?? '', /did not answer within 20 s/);

    const slow = new World();
    let release: () => void = () => undefined;
    const gate = new Promise<void>((resolve) => { release = resolve; });
    const answers = slow.answer;
    slow.answer = (request) => (request.read === 'logs' && request.from === '2026-10-04T21:00:00Z'
      ? gate.then(() => ({ kind: 'read' as const, read: 'logs' as const, distro: 'Ubuntu', body: { ...golden('logs-local-day.json'), freedBytes: 1 } }))
      : answers(request));
    const today = slow.controller.receive({ type: 'today' });
    await slow.controller.receive({ type: 'yesterday' });
    release();
    await today;
    assert.equal(slow.view().periods.find((p) => p.pressed)?.id, 'yesterday');
    assert.notEqual(slow.view().blocks[0]?.lines[0]?.value, '0.0 GB', 'the late answer for Today did not overwrite Yesterday');
  });
});

test('failure text from the daemon reaches the page sanitised', async () => {
  await withZone(KYIV, async () => {
    const w = new World();
    w.answer = (request) => ({ kind: 'refused', messages: [`wsl-care: bad ${String.fromCharCode(0x202e)}argument`], read: request.read });
    await w.controller.receive({ type: 'today' });
    assert.ok((w.view().blocks[0]?.notes[0] ?? '').includes(String.fromCharCode(0xfffd)));
    assert.equal((w.view().blocks[0]?.notes[0] ?? '').includes(String.fromCharCode(0x202e)), false);
  });
});

test('the page\'s rendered count is kept for the extension-host scenarios', async () => {
  const w = new World();
  await w.controller.receive({ type: 'rendered', blocks: 5 });
  assert.equal(w.controller.lastRendered(), 5);
  assert.deepEqual(w.reads, []);
});

test('choose (a NEW page about to load): the period is persisted and nothing is read — the page\'s ready reads it, once', async () => {
  await withZone(KYIV, async () => {
    const w = new World();
    const period = lastCleanupPeriod(w.status);
    assert.deepEqual(period, { kind: 'thisRun', runId: RUN }, 'the panel\'s Logs opens THE last cleanup');
    await w.controller.choose(period ?? { kind: 'today' });
    assert.deepEqual(w.reads, []);
    assert.deepEqual(w.store.get(LOGS_PERIOD_KEY), { kind: 'thisRun', runId: RUN });
    await w.controller.receive({ type: 'ready' });
    assert.deepEqual(w.tails(), [`runs show ${RUN} --json`]);
    assert.equal(lastCleanupPeriod(statusWith(undefined)), undefined, 'no cleanup recorded: no run to open');
  });
});

// ---- the E6.S4 review round ----

/** A read held until the returned function is called — to observe what the host shows while it is out. */
function gated(w: World, holds: (request: RunRead) => boolean): () => void {
  let release: () => void = () => undefined;
  const gate = new Promise<void>((resolve) => { release = resolve; });
  const answers = w.answer;
  w.answer = (request) => (holds(request) ? gate.then(() => answers(request)) : answers(request));
  return () => release();
}

test('review C1: an answer with `problem` (the history could not be read, exit 4) is its own state — no figures, the reason shown', async () => {
  await withZone(KYIV, async () => {
    const w = new World();
    const problem = `history.jsonl could not be read: permission denied${String.fromCharCode(0x202e)}`;
    w.answer = (request) => ({ kind: 'read', read: request.read, distro: 'Ubuntu', body: { ...golden(ANSWERS[request.read]), freedBytes: 0, problem } });
    await w.controller.receive({ type: 'today' });
    const view = w.view();
    const expected = `the run history could not be read: history.jsonl could not be read: permission denied${String.fromCharCode(0xfffd)} — no figures`;
    for (const block of view.blocks) {
      assert.equal(block.state, 'failed', block.id);
      assert.deepEqual(block.lines, [], `${block.id}: no figure shown as fact`);
      assert.deepEqual(block.tables, [], `${block.id}: no table shown as fact`);
      assert.equal(block.notes[0], expected);
    }
    assert.equal(view.runList.state, 'failed');
    assert.deepEqual(view.runList.rows, []);
    assert.deepEqual(view.runList.notes, [expected]);
    await w.controller.receive({ type: 'thisRun' });
    assert.ok(w.view().blocks.every((b) => b.state === 'failed' && b.lines.length === 0), 'This run too');
  });
});

test('review C1: an expanded run whose runs show answered with `problem` shows the problem, not "unknown — it never existed"', async () => {
  await withZone(KYIV, async () => {
    const w = new World();
    await w.controller.receive({ type: 'today' });
    w.answer = (request) => ({ kind: 'read', read: request.read, distro: 'Ubuntu', body: { ...golden('runs-show-unknown.json'), problem: 'the history could not be read' } });
    await w.controller.receive({ type: 'expand', index: 0 });
    const detail = w.view().runList.rows[0]?.detail[0];
    assert.equal(detail?.state, 'failed');
    assert.deepEqual(detail?.notes, ['the run history could not be read: the history could not be read — no figures']);
  });
});

test('review C2: the window is the one the read was BUILT for — past local midnight an expand does not relabel day D\'s answers as D+1', async () => {
  await withZone(KYIV, async () => {
    const w = new World();
    w.now = Date.parse('2026-10-05T20:59:00Z'); // 23:59 in Kyiv
    await w.controller.receive({ type: 'today' });
    const label = w.view().periodLabel;
    assert.match(label, /2026-10-05/);
    w.now = Date.parse('2026-10-05T21:01:00Z'); // 00:01 on the 6th
    await w.controller.receive({ type: 'expand', index: 1 });
    assert.equal(w.view().periodLabel, label, 'the answers are still the 5th\'s');
    assert.equal(w.view().picker.day, '2026-10-05');
    await w.controller.receive({ type: 'refresh' });
    assert.match(w.view().periodLabel, /2026-10-06/, 'a NEW read is the new day');
  });
});

test('review C3: status not answered yet, or failed, is not "no cleanup is recorded" — and the page is re-posted when status changes', async () => {
  await withZone(KYIV, async () => {
    const w = new World();
    w.status = undefined;
    await w.controller.receive({ type: 'today' });
    const thisRun = () => w.view().periods.find((p) => p.id === 'thisRun');
    assert.equal(thisRun()?.enabled, false);
    assert.match(thisRun()?.reason ?? '', /status has not been read yet/);
    await w.controller.receive({ type: 'thisRun' });
    assert.match(w.view().notice, /status has not been read yet/);
    w.status = { kind: 'timedOut', timeoutMs: 20_000, verb: 'status' };
    w.controller.statusChanged();
    assert.match(thisRun()?.reason ?? '', /status did not answer: timed out/);
    assert.doesNotMatch(thisRun()?.reason ?? '', /no cleanup/);
    const posted = w.views.length;
    w.status = statusWith(RUN);
    w.controller.statusChanged();
    assert.equal(w.views.length, posted + 1, 'a status change re-posts the view');
    assert.equal(thisRun()?.enabled, true);
    w.status = statusWith(undefined);
    w.controller.statusChanged();
    assert.equal(thisRun()?.reason, 'no cleanup is recorded yet', 'answered, with no lastCleanup');
  });
});

test('review C5: ready with an answer for the current period posts it — no second read, the expanded runs kept; refresh still reads', async () => {
  await withZone(KYIV, async () => {
    const w = new World();
    await w.controller.receive({ type: 'today' });
    await w.controller.receive({ type: 'expand', index: 1 });
    const reads = w.reads.length;
    await w.controller.receive({ type: 'ready' });
    assert.equal(w.reads.length, reads, 'a tab returning asks nothing');
    assert.equal(w.view().runList.rows[1]?.expanded, true, 'and keeps what was open');
    await w.controller.receive({ type: 'refresh' });
    assert.equal(w.reads.length, reads + 2);
  });
});

test('review C6: a new selection never shows the old period\'s answer under its own label — even while its choice is being written', async () => {
  await withZone(KYIV, async () => {
    const w = new World();
    await w.controller.receive({ type: 'today' });
    assert.equal(w.controller.view().blocks[0]?.state, 'answered');
    const yesterday = w.controller.receive({ type: 'yesterday' });
    const during = w.controller.view();
    assert.match(during.periodLabel, /^Yesterday/);
    assert.notEqual(during.blocks[0]?.state, 'answered', 'Today\'s totals are not Yesterday\'s');
    await yesterday;
    assert.equal(w.view().blocks[0]?.state, 'answered');
  });
});

test('review: a detail that comes back after its run was collapsed, or after the period changed, is dropped (the stale-detail guard)', async () => {
  await withZone(KYIV, async () => {
    const w = new World();
    await w.controller.receive({ type: 'today' });
    const release = gated(w, (request) => request.read === 'runsShow');
    const expanding = w.controller.receive({ type: 'expand', index: 1 });
    await w.controller.receive({ type: 'collapse', index: 1 });
    release();
    await expanding;
    assert.equal(w.view().runList.rows[1]?.expanded, false, 'collapsed stays collapsed');

    const again = gated(w, (request) => request.read === 'runsShow');
    const late = w.controller.receive({ type: 'expand', index: 1 });
    await w.controller.receive({ type: 'yesterday' });
    again();
    await late;
    assert.ok(w.view().runList.rows.every((row) => !row.expanded), 'a detail of Today does not open in Yesterday\'s list');

    // The list read again (Refresh) and the same line opened again: the FIRST read's late answer is not the line's detail.
    const first = gated(w, (request) => request.read === 'runsShow');
    const old = w.controller.receive({ type: 'expand', index: 1 });
    await w.controller.receive({ type: 'refresh' });
    const second = gated(w, (request) => request.read === 'runsShow');
    const fresh = w.controller.receive({ type: 'expand', index: 1 });
    first();
    await old;
    assert.equal(w.view().runList.rows[1]?.detail[0]?.state, 'reading', 'the old list\'s answer is dropped; the new read is still out');
    second();
    await fresh;
    assert.equal(w.view().runList.rows[1]?.detail[0]?.state, 'answered');
  });
});

test('§15p: a daemon that answers `limits.historyRetentionDays` 30 — the picker, the clamp and its words use the daemon\'s 30, not a constant', async () => {
  await withZone(KYIV, async () => {
    const w = new World();
    const status = statusWith(RUN);
    w.status = status.kind === 'answered' && status.answer.verb === 'status' ? { ...status, answer: { ...status.answer, body: { ...status.answer.body, limits: { historyRetentionDays: 30 } } } } : status;
    await w.controller.receive({ type: 'day', day: '2026-01-01' });
    assert.equal(w.view().picker.min, '2026-09-06');
    assert.equal(w.tails()[0], 'logs --from 2026-09-05T21:00:00Z --to 2026-09-06T21:00:00Z --json');
    assert.match(w.view().periodLabel, /clamped to the 30 days the daemon keeps/);
  });
});
