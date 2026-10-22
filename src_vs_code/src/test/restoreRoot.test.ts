import assert from 'node:assert/strict';
import { test } from 'node:test';

import { CleanupController, CAPABILITIES, confirmNeeds, previewNeeds, type CleanupClient } from '../root/cleanupController';
import { runIdOf } from '../root/rootIds';
import type { HeldPreview } from '../root/rootOutcome';
import { bodyOf, stringsAt } from './support/body';
import { answered, headBody, type Body } from './support/outcomes';
import { goldenFile } from './support/paths';
import { exited, recordingRunner, type Scripted } from './support/recordingRunner';
import { DEFAULT_TARGET } from '../wsl/distroTarget';

/**
 * Plan §15s, E10.S2b: *Restore* through the root road — A20's preview and its detached confirm through the cleanup controller, the
 * entry ids on stdin. The daemon's own A20 line (`contracts/golden/head/act-a20-preview-action.json`, made by `RestoreAction`) is what
 * every preview here answers, edited only where a test says why.
 */

const ROOT = '-d Ubuntu -u root --cd / --exec /opt/wsl-care/bin/wsl-care';
const RUN = '20000101T000000Z-1';
const ENTRY = '0123456789abcdef';
const OTHER = 'fedcba9876543210';
const PREVIEW_A20 = `${ROOT} act A20 --preview --entry - --json`;
const CONFIRM_A20 = `${ROOT} act A20 --confirm --manual --detach --entry - --json`;

/** `act A20 --preview --entry - --json` as the daemon answers it: the act envelope of the A4 golden, A20's own line inside (edited by `edit`). */
function previewText(edit: (line: Body) => Body = (line) => line): string {
  return JSON.stringify({ ...goldenFile('act-a4-preview.json'), actions: [edit(goldenFile('act-a20-preview-action.json'))] });
}

function handOff(): string {
  return JSON.stringify({ schemaVersion: 1, result: 'accepted', kind: 'act', runId: RUN, unit: `wsl-care-act@${RUN}.service`, productVersion: '0.1.0' });
}

function statusWithout(edit: (status: Body) => Body): () => Body {
  return () => edit(headBody('status'));
}

function controller(script: Readonly<Record<string, Scripted>>, status: () => Body = () => headBody('status')): { controller: CleanupController; runner: ReturnType<typeof recordingRunner> } {
  const runner = recordingRunner({ [`${ROOT} --version`]: exited(0, '0.1.0\n'), ...script });
  const client: CleanupClient = { rootTarget: () => Promise.resolve({ wsl: 'C:\\Windows\\System32\\wsl.exe', distro: 'Ubuntu' }), run: () => Promise.resolve(answered('status', status())) };
  let now = 1_000_000;

  return { controller: new CleanupController({ client, runner: runner.runner, now: () => now, sleep: (ms) => { now += ms; return Promise.resolve(); } }), runner };
}

async function held(script: Readonly<Record<string, Scripted>> = { [PREVIEW_A20]: exited(0, previewText()) }): Promise<{ preview: HeldPreview; c: ReturnType<typeof controller> }> {
  const c = controller(script);
  const outcome = await c.controller.previewRestore([ENTRY], DEFAULT_TARGET);
  assert.equal(outcome.kind, 'previewed', JSON.stringify(outcome).slice(0, 300));
  assert.ok(outcome.kind === 'previewed');
  return { preview: outcome.preview, c };
}

// ---- 1. the one road in ----

test('entry ids that are not a list the daemon takes start NOTHING — none, one twice, a bad id, more than one restore takes', async () => {
  const c = controller({});
  for (const bad of [[], [ENTRY, ENTRY], [ENTRY, 'NOT-AN-ID'], Array.from({ length: 5001 }, (_, i) => i.toString(16).padStart(16, '0'))]) {
    assert.equal((await c.controller.previewRestore(bad, DEFAULT_TARGET)).kind, 'entriesRefused', `${bad.length} ids`);
  }
  assert.deepEqual(c.runner.argvs(), [], 'not even the root check');
});

// ---- 2. the gate ----

test('a restore needs archive.restore, act.entryStdin and its op\'s own capabilities advertised — else "Update daemon", nothing started', async () => {
  for (const missing of ['archive.restore', 'act.entryStdin', 'act.shownList']) {
    const c = controller({}, statusWithout((s) => ({ ...s, capabilities: stringsAt(s, 'capabilities').filter((x) => x !== missing) })));
    const outcome = await c.controller.previewRestore([ENTRY], DEFAULT_TARGET);
    assert.equal(outcome.kind, 'actionsUnavailable', missing);
    assert.deepEqual(c.runner.argvs(), [], missing);
  }
  assert.deepEqual([...previewNeeds(['A20'])], [...CAPABILITIES.previewA20]);
  assert.ok(confirmNeeds(['A20']).includes('archive.restore') && confirmNeeds(['A20']).includes('act.entryStdin') && confirmNeeds(['A20']).includes('act.detach'));
});

test('A20 is acted on only when the daemon REPORTS it in status.actions — and the cleanup ops still never act on it', async () => {
  const c = controller({}, statusWithout((s) => ({ ...s, actions: stringsAt(s, 'actions').filter((a) => a !== 'A20') })));
  assert.equal((await c.controller.previewRestore([ENTRY], DEFAULT_TARGET)).kind, 'idsRefused');
  const cleanup = controller({});
  const outcome = await cleanup.controller.preview(['A20'], DEFAULT_TARGET);
  assert.equal(outcome.kind, 'idsRefused', 'a button-only id is never a cleanup row');
  assert.ok(cleanup.runner.argvs().every((a) => !a.includes('A20')));
});

// ---- 3. the held preview and its confirm ----

test('the preview pipes the page\'s ids; the CONFIRM pipes the ids the preview SHOWED — never the selection it was asked for', async () => {
  const c = controller({ [PREVIEW_A20]: exited(0, previewText()), [CONFIRM_A20]: exited(0, handOff()) });
  const outcome = await c.controller.previewRestore([ENTRY, OTHER], DEFAULT_TARGET);
  assert.ok(outcome.kind === 'previewed', JSON.stringify(outcome).slice(0, 200));
  assert.deepEqual(outcome.preview.restore?.names, [ENTRY], 'the daemon showed one of the two (the other is no longer restorable)');
  assert.equal((await c.controller.confirm(outcome.preview)).kind, 'accepted');
  const stdins = c.runner.requests.filter((r) => r.stdin !== undefined).map((r) => r.stdin?.toString('utf8'));
  assert.deepEqual(stdins, [`${ENTRY}\n${OTHER}\n`, `${ENTRY}\n`]);
  assert.ok(c.runner.requests.every((r) => r.args.every((a) => a !== ENTRY && a !== OTHER)), 'no id travels as argv');
});

test('a shown entry that is not 16 lowercase hex, or one shown twice, refuses the preview — nothing can be confirmed', async () => {
  for (const shown of [['NOT-AN-ENTRY-ID!'], [ENTRY, ENTRY]]) {
    const c = controller({ [PREVIEW_A20]: exited(0, previewText((line) => ({ ...line, shown, preview: { ...bodyOf(line.preview, 'preview'), count: shown.length } }))) });
    assert.equal((await c.controller.previewRestore([ENTRY], DEFAULT_TARGET)).kind, 'restoreListInvalid', JSON.stringify(shown));
  }
});

test('the plan round\'s #0: a list the daemon CUT (shownTruncated) is never confirmed as the whole — the confirm is refused, nothing piped', async () => {
  const cut = previewText((line) => ({ ...line, shownTruncated: true, preview: { ...bodyOf(line.preview, 'preview'), count: 3 } }));
  const c = controller({ [PREVIEW_A20]: exited(0, cut) });
  const status = headBody('status');
  const capped = controller({ [PREVIEW_A20]: exited(0, cut) }, () => ({ ...status, limits: { ...bodyOf(status.limits, 'limits'), maxShownNames: 1 } }));
  assert.equal((await c.controller.previewRestore([ENTRY], DEFAULT_TARGET)).kind, 'restoreListInvalid', 'a cut list under the default cap does not add up');
  assert.equal((await capped.controller.previewRestore([ENTRY, OTHER], DEFAULT_TARGET)).kind, 'entriesRefused', 'more ids than the cap in force: refused before the preview, not cut');
  const outcome = await capped.controller.previewRestore([ENTRY], DEFAULT_TARGET);
  assert.ok(outcome.kind === 'previewed', JSON.stringify(outcome).slice(0, 300));
  assert.equal(outcome.preview.restore?.truncated, true);
  const confirm = await capped.controller.confirm(outcome.preview);
  assert.equal(confirm.kind, 'restoreListInvalid');
  assert.ok(capped.runner.argvs().every((a) => a !== CONFIRM_A20), 'no confirm went out');
});

test('a preview offering no entry still restorable is held, and its confirm is refused — never an empty --entry - list', async () => {
  const none = previewText((line) => ({ ...line, shown: [], preview: { ...bodyOf(line.preview, 'preview'), count: 0, bytes: 0, items: [] } }));
  const { preview, c } = await held({ [PREVIEW_A20]: exited(0, none) });
  assert.deepEqual(preview.restore?.names, []);
  assert.equal((await c.controller.confirm(preview)).kind, 'restoreListInvalid');
  assert.ok(c.runner.argvs().every((a) => a !== CONFIRM_A20));
});

test('the held restore selection is frozen with the preview — the ids a confirm pipes cannot be edited after the modal', async () => {
  const { preview } = await held();
  assert.ok(Object.isFrozen(preview.restore), 'the selection');
  assert.ok(Object.isFrozen(preview.restore?.names), 'its ids');
});

test('a restore detach answered without a run id never ADOPTS a manual full check (["collect"]) — it stays unknown, the other run named', async () => {
  const unknownNoId = JSON.stringify({ schemaVersion: 1, result: 'unknown', kind: 'act', productVersion: '0.1.0' });
  const fullCheck = { ...headBody('status'), running: { state: 'live', runId: '20000101T000000Z-7', trigger: 'manual', actions: ['collect'], reason: '', current: '' } };
  let statuses = 0;
  const c = controller({ [PREVIEW_A20]: exited(0, previewText()), [CONFIRM_A20]: exited(0, unknownNoId) }, () => {
    statuses += 1;
    return statuses <= 2 ? headBody('status') : fullCheck;
  });
  const outcome = await c.controller.previewRestore([ENTRY], DEFAULT_TARGET);
  assert.ok(outcome.kind === 'previewed');
  const handed = await c.controller.confirm(outcome.preview);
  assert.equal(handed.kind, 'outcomeUnknown', 'a full check in flight is not this restore');
  assert.ok(handed.kind === 'outcomeUnknown');
  assert.equal(handed.otherRun?.runId, runIdOf('20000101T000000Z-7'));
});
test('the restore\'s root requests: the preview a root preview of A20, the confirm a DETACH, both without WSLENV', async () => {
  const { c } = await held({ [PREVIEW_A20]: exited(0, previewText()), [CONFIRM_A20]: exited(0, handOff()) });
  const preview = c.runner.requests.find((r) => r.args.join(' ') === PREVIEW_A20);
  assert.ok(preview !== undefined);
  assert.ok(preview.timeoutMs > 600_000, 'above archive.previewTimeoutSeconds at its maximum');
  assert.deepEqual(preview.withoutEnv, ['WSLENV']);
});

// ---- the own review on Fable ----

test('own review #1: an UNAVAILABLE A20 line (its archive child failed, no shown list) is refused in the daemon\'s OWN words', async () => {
  const unread = previewText((line) => {
    const { shown: _shown, ...rest } = line;
    return { ...rest, preview: { ...bodyOf(line.preview, 'preview'), available: false, reason: 'the archive list child timed out after 600 s', count: 0, bytes: 0, items: [] } };
  });
  const c = controller({ [PREVIEW_A20]: exited(0, unread) });
  const outcome = await c.controller.previewRestore([ENTRY], DEFAULT_TARGET);
  assert.ok(outcome.kind === 'restoreListInvalid', JSON.stringify(outcome).slice(0, 300));
  assert.match(outcome.reason, /the archive list child timed out after 600 s/);
});

test('own review #2: a shown entry the panel did NOT ask for refuses the preview — the confirm can only pipe ids the page handed', async () => {
  const c = controller({ [PREVIEW_A20]: exited(0, previewText((line) => ({ ...line, shown: [OTHER] }))) });
  const outcome = await c.controller.previewRestore([ENTRY], DEFAULT_TARGET);
  assert.ok(outcome.kind === 'restoreListInvalid', JSON.stringify(outcome).slice(0, 300));
  assert.match(outcome.reason, /did not ask for/);
});

test('own review #7: the restore confirm\'s exit 2 is a refusal of the piped list — shownListRefused, the retry road', async () => {
  const c = controller({ [PREVIEW_A20]: exited(0, previewText()), [CONFIRM_A20]: exited(2, '', 'wsl-care: act: the entry list on stdin had no end within 10 s; nothing was done\n') });
  const outcome = await c.controller.previewRestore([ENTRY], DEFAULT_TARGET);
  assert.ok(outcome.kind === 'previewed');
  const confirm = await c.controller.confirm(outcome.preview);
  assert.equal(confirm.kind, 'shownListRefused');
});

test('own review #7: A20 dropped from status.actions between the preview and the confirm refuses the confirm — nothing piped', async () => {
  let reported = true;
  const status = headBody('status');
  const c = controller({ [PREVIEW_A20]: exited(0, previewText()), [CONFIRM_A20]: exited(0, handOff()) }, () => (reported ? status : { ...status, actions: stringsAt(status, 'actions').filter((a) => a !== 'A20') }));
  const outcome = await c.controller.previewRestore([ENTRY], DEFAULT_TARGET);
  assert.ok(outcome.kind === 'previewed');
  reported = false;
  assert.equal((await c.controller.confirm(outcome.preview)).kind, 'idsRefused');
  assert.ok(c.runner.argvs().every((a) => a !== CONFIRM_A20));
});
