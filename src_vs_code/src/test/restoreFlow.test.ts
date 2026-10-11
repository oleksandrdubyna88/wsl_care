import assert from 'node:assert/strict';
import { test } from 'node:test';

import { CleanFlow, type CleanUi } from '../cleanup/cleanFlow';
import { CleanupHost } from '../cleanup/cleanupHost';
import { deriveCleanup } from '../cleanup/cleanupView';
import { CleanupJournal, type JournalEntry } from '../cleanup/journal';
import { ARCHIVE_LABEL, firstModal, RESTORE_LABEL, secondModal, type Modal } from '../cleanup/modalText';
import { ARCHIVE_RUN, handOffNotice, resultNotice, type NoticeLevel } from '../cleanup/resultText';
import { parseRunShow } from '../cleanup/runAnswers';
import { RUN_WORDS, runWordsByLabel, runWordsOf } from '../cleanup/runWords';
import { CleanupController, type CleanupClient } from '../root/cleanupController';
import { runIdOf, type RunId } from '../root/rootIds';
import type { HeldPreview } from '../root/rootOutcome';
import { OutcomeStore, type Snapshot } from '../state/outcomeStore';
import { bodyAt, bodyOf, stringsAt } from './support/body';
import { ManualTimers, MapStore } from './support/memento';
import { answered, headBody, type Body } from './support/outcomes';
import { goldenFile } from './support/paths';
import { exited, recordingRunner, type Scripted } from './support/recordingRunner';

/**
 * Plan §15s, E10.S2b story 2: *Restore* through the cleanup transaction as it is — its words (ONE table, `cleanup/runWords.ts`,
 * A13's words unchanged beside A20's), its modal, the flow (one at a time, the preview, the modal, the journal entry BEFORE the
 * confirm, the detached confirm piping the HELD preview's ids), a preview offering none still restorable, and the host's refusal
 * and its durable "a restore is in flight". The daemon's own A20 line (`contracts/golden/head/act-a20-preview-action.json`, made by
 * `RestoreAction`) is what every preview here answers.
 */

const ROOT = '-d Ubuntu -u root --cd / --exec /opt/wsl-care/bin/wsl-care';
const RUN = '20000101T000000Z-1';
const ROOT_CHECK = `${ROOT} --version`;
const ENTRY = '0123456789abcdef';
const PREVIEW_A20 = `${ROOT} act A20 --preview --entry - --json`;
const CONFIRM_A20 = `${ROOT} act A20 --confirm --manual --detach --entry - --json`;
const WALL = Date.parse('2026-10-11T12:00:00.000Z');

function previewText(edit: (line: Body) => Body = (line) => line): string {
  return JSON.stringify({ ...goldenFile('act-a4-preview.json'), actions: [edit(goldenFile('act-a20-preview-action.json'))] });
}

function handOff(): string {
  return JSON.stringify({ schemaVersion: 1, result: 'accepted', kind: 'act', runId: RUN, unit: `wsl-care-act@${RUN}.service`, productVersion: '0.1.0' });
}

function runId(text: string): RunId {
  const id = runIdOf(text);
  assert.ok(id !== undefined);
  return id;
}

function restoreEntry(): Extract<JournalEntry, { kind: 'run' }> {
  return { id: 'e1', kind: 'run', op: 'clean', distro: 'Ubuntu', actions: ['A20'], since: '2026-10-11T12:00:00.000Z', runId: runId(RUN) };
}

function controller(script: Readonly<Record<string, Scripted>>): { controller: CleanupController; runner: ReturnType<typeof recordingRunner> } {
  const runner = recordingRunner({ [ROOT_CHECK]: exited(0, '0.1.0\n'), ...script });
  const client: CleanupClient = { rootTarget: () => Promise.resolve({ wsl: 'C:\\Windows\\System32\\wsl.exe', distro: 'Ubuntu' }), run: () => Promise.resolve(answered('status', headBody('status'))) };
  let now = 1_000_000;

  return { controller: new CleanupController({ client, runner: runner.runner, now: () => now, sleep: (ms) => { now += ms; return Promise.resolve(); } }), runner };
}

async function heldA20(edit?: (line: Body) => Body): Promise<HeldPreview> {
  const outcome = await controller({ [PREVIEW_A20]: exited(0, previewText(edit)) }).controller.previewRestore([ENTRY]);
  assert.ok(outcome.kind === 'previewed', JSON.stringify(outcome).slice(0, 300));
  return outcome.preview;
}

interface FlowWorld {
  readonly flow: CleanFlow;
  readonly runner: ReturnType<typeof recordingRunner>;
  readonly journal: CleanupJournal;
  readonly modals: Modal[];
  readonly notices: { level: NoticeLevel; sentence: string }[];
  readonly seenBefore: string[];
}

function flowWorld(script: (store: MapStore, seen: string[]) => Readonly<Record<string, Scripted>>, answer = true): FlowWorld {
  const store = new MapStore();
  const seenBefore: string[] = [];
  const c = controller(script(store, seenBefore));
  const modals: Modal[] = [];
  const notices: { level: NoticeLevel; sentence: string }[] = [];
  const ui: CleanUi = { confirm: (m) => { modals.push(m); return Promise.resolve(answer); }, notify: (level, sentence) => { notices.push({ level, sentence }); return Promise.resolve(undefined); } };
  const journal = new CleanupJournal(store, () => WALL);
  const flow = new CleanFlow({ controller: c.controller, journal, ui, follower: { started: () => undefined, kick: () => undefined }, now: () => 1_000_000, wallNow: () => WALL, distro: () => 'Ubuntu', changed: () => undefined });

  return { flow, runner: c.runner, journal, modals, notices, seenBefore };
}

// ---- 1. the words: ONE table ----

test('the single-action words are ONE table: A13 keeps its archive words, A20 is told as the restore run — and only a run of ONE of them', () => {
  assert.deepEqual(RUN_WORDS.map((w) => w.id), ['A13', 'A20', 'A21'], 'the relay stop of S7b.2b is a row too, never a second copy');
  assert.equal(runWordsOf(['A21'])?.label, 'the relay stop');
  assert.equal(runWordsOf(['A21'])?.inFlight, 'Stopping… the client-gone relays');
  assert.equal(runWordsOf(['A13'])?.label, 'the archive run');
  assert.equal(runWordsOf(['A13'])?.inFlight, 'Archiving… the aged AI sessions');
  assert.equal(runWordsOf(['A20'])?.label, 'the restore run');
  assert.equal(runWordsOf(['A20'])?.inFlight, 'Restoring… archived sessions');
  for (const ids of [[], ['A4'], ['A13', 'A20'], ['A20', 'A4'], ['A21', 'A5'], ['toString'], ['__proto__']]) {
    assert.equal(runWordsOf(ids), undefined, JSON.stringify(ids));
  }
  assert.equal(runWordsByLabel('the restore run')?.id, 'A20');
  assert.equal(runWordsByLabel('A4'), undefined);
  assert.equal(ARCHIVE_RUN, 'the archive run');
  assert.equal(ARCHIVE_LABEL, 'Archive');
  assert.equal(RESTORE_LABEL, 'Restore');
});

test('the notices say "the restore run", never "Cleaning A20" — taken, unknown, never ran', () => {
  const entry = restoreEntry();
  const taken = handOffNotice({ kind: 'accepted', runId: runId(RUN), unit: '', productVersion: '0.1.0' }, 'the restore run', 'clean');
  assert.match(taken.sentence, /^The restore run: the daemon took it as run 20000101T000000Z-1/);
  assert.match(resultNotice({ kind: 'ceiling', entry }, 1_800_000).sentence, /^Run 20000101T000000Z-1 \(the restore run\): state unknown/);
  const { runId: _id, ...unresolved } = entry;
  assert.match(resultNotice({ kind: 'neverRan', entry: { ...unresolved, kind: 'unresolved' } }, 0).sentence, /^The restore run confirmed at .* never ran/);
});

test('a done restore run says how many sessions it RESTORED — never freed, never moved', () => {
  const done = goldenFile('runs-show-done.json');
  const run = bodyAt(done, 'run');
  const show = parseRunShow({ ...done, run: { ...run, freedBytes: 0, actions: [{ id: 'A20', status: 'completed', count: 2, bytes: 0, reason: '' }] } });
  const sentence = resultNotice({ kind: 'run', entry: restoreEntry(), show }, 0).sentence;
  assert.equal(sentence, 'Run 20000101T000000Z-1 (the restore run) is done: 2 archived sessions restored.');
  const one = parseRunShow({ ...done, run: { ...run, actions: [{ id: 'A20', status: 'completed', count: 1, bytes: 0, reason: '' }] } });
  assert.match(resultNotice({ kind: 'run', entry: restoreEntry(), show: one }, 0).sentence, /: 1 archived session restored\.$/);
});

function restoringState(name: string): string {
  const status = goldenFile(name);
  const snapshot: Snapshot = { status: answered('status', { ...status, running: { ...bodyAt(status, 'running'), actions: ['A20'], current: '' } }), preview: answered('preview', headBody('preview')), doctor: undefined, checking: false };
  return deriveCleanup(snapshot, { entries: [], results: [], flowBusy: false }).controls.state;
}

test('the in-flight state of an A20 run reads "Restoring…", and a queued or wedged one is named the restore run', () => {
  assert.equal(restoringState('status-running-live.json'), 'Restoring… archived sessions');
  assert.equal(restoringState('status-running-queued.json'), 'Queued… the restore run');
  assert.match(restoringState('status-running-wedged.json'), /^Wedged: run \S+ \(the restore run\) — /);
});

// ---- 2. the modal ----

test('the restore modal counts the HELD preview\'s sessions, lists each with its note and size, says how — and confirms with Restore, no second modal', async () => {
  const modal = firstModal(await heldA20(), false);
  assert.equal(modal.message, 'Restore 1 archived session in "Ubuntu"?');
  assert.equal(modal.confirm, RESTORE_LABEL);
  assert.match(modal.detail, /0123456789abcdef — claude-code 1999\/12, 2 file\(s\) · 200 B/);
  assert.match(modal.detail, /files are only created, never replaced — a session already back is skipped/);
  assert.doesNotMatch(modal.detail, /Clean|A20 —/);
  assert.equal(secondModal(['A20']), undefined);
});

test('a restore of many lists the first ten sessions and counts the rest', async () => {
  const ids = Array.from({ length: 12 }, (_, i) => `00000000000000${i.toString(16).padStart(2, '0')}`);
  const items = ids.map((id) => ({ kind: 'archived session', name: id, bytes: 10, note: 'codex 2026/01, 1 file(s)' }));
  const many = previewText((line) => ({ ...line, shown: ids, preview: { ...bodyOf(line.preview, 'preview'), count: 12, bytes: 120, items } }));
  const outcome = await controller({ [PREVIEW_A20]: exited(0, many) }).controller.previewRestore(ids);
  assert.ok(outcome.kind === 'previewed', JSON.stringify(outcome).slice(0, 300));
  const modal = firstModal(outcome.preview, false);
  assert.equal(modal.message, 'Restore 12 archived sessions in "Ubuntu"?');
  assert.equal(modal.detail.split('\n').filter((l) => l.startsWith('  • ')).length, 10);
  assert.match(modal.detail, /… and 2 more/);
});

// ---- 3. the flow ----

test('Restore: the preview of the page\'s ids, its modal, the journal entry BEFORE the confirm, ONE detached confirm of the shown ids, told as the restore run', async () => {
  const w = flowWorld((store, seen) => ({ [PREVIEW_A20]: exited(0, previewText()), [CONFIRM_A20]: () => { seen.push(...new CleanupJournal(store).entries().map((e) => e.actions.join(','))); return exited(0, handOff()); } }));
  const outcome = await w.flow.restore([ENTRY, 'fedcba9876543210']);
  assert.equal(outcome.kind, 'handedOff');
  assert.deepEqual(w.runner.argvs(), [ROOT_CHECK, PREVIEW_A20, CONFIRM_A20]);
  assert.deepEqual(w.seenBefore, ['A20'], 'persisted before the call');
  assert.deepEqual(w.runner.requests.filter((r) => r.stdin !== undefined).map((r) => r.stdin?.toString('utf8')), [`${ENTRY}\nfedcba9876543210\n`, `${ENTRY}\n`]);
  assert.deepEqual(w.modals.map((m) => m.confirm), [RESTORE_LABEL]);
  assert.match(w.notices.at(-1)?.sentence ?? '', /^The restore run: the daemon took it as run/);
  const [entry] = w.journal.entries();
  assert.ok(entry?.kind === 'run' && entry.runId === runId(RUN));
});

test('declined: nothing confirmed, nothing written', async () => {
  const w = flowWorld(() => ({ [PREVIEW_A20]: exited(0, previewText()) }), false);
  assert.equal((await w.flow.restore([ENTRY])).kind, 'declined');
  assert.deepEqual(w.runner.argvs(), [ROOT_CHECK, PREVIEW_A20]);
  assert.deepEqual(w.journal.entries(), []);
});

test('a preview offering none still restorable opens NO modal and confirms nothing — it says so, in the restore\'s words', async () => {
  const none = previewText((line) => ({ ...line, shown: [], preview: { ...bodyOf(line.preview, 'preview'), count: 0, bytes: 0, items: [] } }));
  const w = flowWorld(() => ({ [PREVIEW_A20]: exited(0, none) }));
  assert.equal((await w.flow.restore([ENTRY])).kind, 'nothing', 'the relay stop\'s outcome, reused');
  assert.deepEqual(w.modals, []);
  assert.deepEqual(w.journal.entries(), []);
  assert.match(w.notices.at(-1)?.sentence ?? '', /^None of the selected archived sessions can be restored now/);
});

test('ids the daemon does not take start nothing: refused before the root check, told in the restore\'s words', async () => {
  const w = flowWorld(() => ({}));
  const outcome = await w.flow.restore(['not-an-id']);
  assert.ok(outcome.kind === 'refused' && outcome.failure.kind === 'entriesRefused');
  assert.deepEqual(w.runner.argvs(), []);
  assert.equal(w.notices.at(-1)?.level, 'error');
});

// ---- 4. the host: its refusal and the durable "in flight" ----

async function hostWith(status: Body, entries: readonly JournalEntry[] = []): Promise<{ host: CleanupHost; notices: string[]; asked: string[] }> {
  const outcomes = new OutcomeStore();
  outcomes.set('status', answered('status', status));
  outcomes.set('preview', answered('preview', headBody('preview')));
  const durable = new MapStore();
  const notices: string[] = [];
  const asked: string[] = [];
  const never = (): Promise<never> => Promise.reject(new Error('not asked in this test'));
  const host = new CleanupHost({
    durable,
    controller: { preview: never, previewRestore: (texts) => { asked.push(...texts); return never(); }, confirm: never, stop: never, runFullCheck: never },
    read: never, outcomes, askStatus: () => Promise.resolve(answered('status', status)), refreshPanel: () => Promise.resolve(),
    focused: () => true, ui: { confirm: () => Promise.resolve(false), notify: (_l, sentence) => { notices.push(sentence); return Promise.resolve(undefined); } },
    timers: new ManualTimers(), now: () => 0, wallNow: () => WALL, log: () => undefined,
  });
  for (const entry of entries) {
    const { id: _id, ...rest } = entry;
    await host.journal.add(rest);
  }
  return { host, notices, asked };
}

function runningStatus(name: string, actions: readonly string[]): Body {
  const status = goldenFile(name);
  return { ...status, running: { ...bodyAt(status, 'running'), actions, current: '' } };
}

test('Restore is refused by the HOST while a run is in flight — whatever the page sent; nothing reaches the controller', async () => {
  const busy = await hostWith(runningStatus('status-running-live.json', ['A4']));
  assert.notEqual(busy.host.restoreBlocker(), '');
  assert.equal(await busy.host.restore([ENTRY]), undefined);
  assert.deepEqual(busy.asked, []);
  assert.match(busy.notices.at(-1) ?? '', /^Restore is not available: /);
  assert.equal((await hostWith(headBody('status'))).host.restoreBlocker(), '');
});

test('a restore is IN FLIGHT while the daemon runs A20 alone or this window\'s journal follows one — not for a cleanup, an archive run or a dead run', async () => {
  assert.equal((await hostWith(runningStatus('status-running-live.json', ['A20']))).host.restoreInFlight(), true);
  assert.equal((await hostWith(runningStatus('status-running-queued.json', ['A20']))).host.restoreInFlight(), true);
  assert.equal((await hostWith(runningStatus('status-running-live.json', ['A13']))).host.restoreInFlight(), false);
  assert.equal((await hostWith(runningStatus('status-running-dead.json', ['A20']))).host.restoreInFlight(), false);
  assert.equal((await hostWith(headBody('status'))).host.restoreInFlight(), false);
  const followed = await hostWith(headBody('status'), [restoreEntry()]);
  assert.equal(followed.host.restoreInFlight(), true);
  const archive = await hostWith(headBody('status'), [{ ...restoreEntry(), actions: ['A13'] }]);
  assert.equal(archive.host.restoreInFlight(), false);
});

test('Restore is greyed by the HOST when the daemon does not offer it — a capability of its preview or its confirm missing, or A20 not reported', async () => {
  const head = headBody('status');
  for (const missing of ['archive.restore', 'act.entryStdin', 'act.shownList', 'act.detach']) {
    const without = await hostWith({ ...head, capabilities: stringsAt(head, 'capabilities').filter((c) => c !== missing) });
    assert.match(without.host.restoreBlocker(), new RegExp(`does not offer .*${missing.split('.').join('[.]')}.* update the daemon`), missing);
    assert.equal(await without.host.restore([ENTRY]), undefined);
    assert.deepEqual(without.asked, [], missing);
  }
  const unreported = await hostWith({ ...head, actions: stringsAt(head, 'actions').filter((a) => a !== 'A20') });
  assert.match(unreported.host.restoreBlocker(), /does not report A20/);
});
