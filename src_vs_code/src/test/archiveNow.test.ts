import assert from 'node:assert/strict';
import { test } from 'node:test';

import { ARCHIVE_CAPABILITY, ARCHIVE_NOW_CAPABILITIES, deriveArchive, type ArchiveState } from '../archive/archiveView';
import { ceilingMs } from '../client/ceilings';
import { CleanFlow, type CleanUi } from '../cleanup/cleanFlow';
import { deriveCleanup } from '../cleanup/cleanupView';
import { CleanupJournal, type JournalEntry } from '../cleanup/journal';
import { parseRunShow } from '../cleanup/runAnswers';
import { ARCHIVE_LABEL, firstModal, secondModal, type Modal } from '../cleanup/modalText';
import { ARCHIVE_RUN, handOffNotice, resultNotice, type NoticeLevel } from '../cleanup/resultText';
import { parsePageMessage } from '../panel/messages';
import { CleanupController, confirmNeeds, previewNeeds, type CleanupClient } from '../root/cleanupController';
import { rootTimeoutMs } from '../root/rootCall';
import { runIdOf, type ActionIds, type RunId } from '../root/rootIds';
import type { HeldPreview } from '../root/rootOutcome';
import { DEFAULT_NUMBERS } from '../settings/numbers';
import type { Snapshot } from '../state/outcomeStore';
import { bodyAt, bodyOf, stringsAt } from './support/body';
import { MapStore } from './support/memento';
import { answered, headBody, type Body } from './support/outcomes';
import { goldenFile } from './support/paths';
import { exited, recordingRunner, type Scripted } from './support/recordingRunner';

/**
 * Plan §15s, E10.S1b: *Archive now* — A13 through the cleanup controller as it is. Its capabilities, its preview's ceiling, its
 * per-agent items, its own words (the modal, the notices, the in-flight state), the transaction, and the button's conditions.
 * The daemon's own A13 line (`contracts/golden/head/act-a13-preview-action.json`) is what every preview here answers.
 */

const ROOT = '-d Ubuntu -u root --cd / --exec /opt/wsl-care/bin/wsl-care';
const RUN = '20000101T000000Z-1';
const ROOT_CHECK = `${ROOT} --version`;
const PREVIEW_A13 = `${ROOT} act A13 --preview --json`;
const CONFIRM_A13 = `${ROOT} act A13 --confirm --manual --detach --json`;

/** `act A13 --preview --json` as the daemon answers it: the act envelope of the A4 golden, A13's own line inside. */
function a13PreviewText(): string {
  return JSON.stringify({ ...goldenFile('act-a4-preview.json'), actions: [goldenFile('act-a13-preview-action.json')] });
}

function handOff(): string {
  return JSON.stringify({ schemaVersion: 1, result: 'accepted', kind: 'act', runId: RUN, unit: `wsl-care-act@${RUN}.service`, productVersion: '0.1.0' });
}

function runId(text: string): RunId {
  const id = runIdOf(text);
  assert.ok(id !== undefined);
  return id;
}

function statusWithout(capability: string): Body {
  const head = headBody('status');
  return { ...head, capabilities: stringsAt(head, 'capabilities').filter((c) => c !== capability) };
}

/** The journal entry of a run of A13 alone — what every archive word is chosen by. */
function archiveEntry(): Extract<JournalEntry, { kind: 'run' }> {
  return { id: 'e1', kind: 'run', op: 'clean', distro: 'Ubuntu', actions: ['A13'], since: '2026-10-10T12:00:00.000Z', runId: runId(RUN) };
}

function controller(script: Readonly<Record<string, Scripted>>, status: () => Body = () => headBody('status')): { controller: CleanupController; runner: ReturnType<typeof recordingRunner> } {
  const runner = recordingRunner({ [ROOT_CHECK]: exited(0, '0.1.0\n'), ...script });
  const client: CleanupClient = { rootTarget: () => Promise.resolve({ wsl: 'C:\\Windows\\System32\\wsl.exe', distro: 'Ubuntu' }), run: () => Promise.resolve(answered('status', status())) };
  let now = 1_000_000;

  return { controller: new CleanupController({ client, runner: runner.runner, now: () => now, sleep: (ms) => { now += ms; return Promise.resolve(); } }), runner };
}

async function heldA13(): Promise<HeldPreview> {
  const outcome = await controller({ [PREVIEW_A13]: exited(0, a13PreviewText()) }).controller.preview(['A13']);
  assert.equal(outcome.kind, 'previewed', JSON.stringify(outcome).slice(0, 300));
  assert.ok(outcome.kind === 'previewed');
  return outcome.preview;
}

// ---- 0. the fixtures: checked, never cast (the second code round, #0) ----

test('a golden read as a body is CHECKED — an array, a scalar or null is refused where it is read, by the fixture\'s name', () => {
  assert.deepEqual(bodyOf({ a: 1 }, 'x'), { a: 1 });
  for (const wrong of [[], 'text', null, 7]) {
    assert.throws(() => bodyOf(wrong, 'fixture.json'), /^Error: fixture\.json is not an object/, JSON.stringify(wrong));
  }
  assert.throws(() => bodyAt({ running: [] }, 'running'), /^Error: running is not an object/);
  assert.deepEqual(stringsAt({ capabilities: ['a'] }, 'capabilities'), ['a']);
  assert.throws(() => stringsAt({ capabilities: ['a', 1] }, 'capabilities'), /^Error: capabilities is not a list of strings/);
  assert.equal(goldenFile('act-a13-preview-action.json').id, 'A13');
});

// ---- 1. the capabilities ----

test('a preview of A13 without archive.preview advertised is refused before any root call', async () => {
  const c = controller({ [PREVIEW_A13]: exited(0, a13PreviewText()) }, () => statusWithout('archive.preview'));
  const outcome = await c.controller.preview(['A13']);
  assert.notEqual(outcome.kind, 'previewed');
  assert.deepEqual(c.runner.argvs(), [], 'nothing ran as root');
});

test('a confirm of A13 without archive.run advertised is refused before the confirm goes out', async () => {
  let status = headBody('status');
  const c = controller({ [PREVIEW_A13]: exited(0, a13PreviewText()), [CONFIRM_A13]: exited(0, handOff()) }, () => status);
  const previewed = await c.controller.preview(['A13']);
  assert.ok(previewed.kind === 'previewed');
  status = statusWithout('archive.run');
  const outcome = await c.controller.confirm(previewed.preview);
  assert.notEqual(outcome.kind, 'accepted');
  assert.equal(c.runner.argvs().includes(CONFIRM_A13), false);
});

// ---- 2. the ceiling ----

test('A13\'s preview ceiling is the archive preview setting\'s, not a Docker row\'s snapshot', () => {
  const ids: ActionIds = ['A13'];
  const ms = rootTimeoutMs({ op: 'preview', ids });
  assert.equal(ms, 1000 * (DEFAULT_NUMBERS.statusSeconds + DEFAULT_NUMBERS.archivePreviewSeconds));
  assert.ok(ms > 1000 * 600, 'above the daemon\'s archive.previewTimeoutSeconds at its range maximum');
  assert.equal(ceilingMs({ ...DEFAULT_NUMBERS, archivePreviewSeconds: 900 }, { call: 'rootPreview', ids: ['A13'] }), 1000 * (DEFAULT_NUMBERS.statusSeconds + 900), 'it follows the setting');
});

// ---- 3. the items ----

test('A13\'s preview keeps each agent\'s item whole: its name, bytes and note', async () => {
  const preview = await heldA13();
  assert.deepEqual(preview.actions[0]?.details, [{ name: 'claude-code', bytes: 200, note: '1 session(s) due, 2 file(s)' }]);
});

test('the held preview\'s details are frozen like its items — immutable all the way down (own review #2)', async () => {
  const details = (await heldA13()).actions[0]?.details ?? [];
  assert.ok(Object.isFrozen(details), 'the list');
  assert.ok(details.length > 0 && details.every((d) => Object.isFrozen(d)), 'each item');
});

// ---- 4. the words ----

test('the modal of A13 names each agent\'s sessions and bytes, how the move is made, and confirms with Archive — no second modal', async () => {
  const modal = firstModal(await heldA13(), false);
  assert.equal(modal.message, 'Archive the aged AI sessions in "Ubuntu"?');
  assert.equal(modal.confirm, ARCHIVE_LABEL);
  // The code round's #2: 200 bytes read as 200 B, never as an empty-looking 0.0 GB.
  assert.match(modal.detail, /claude-code — 1 session\(s\) due, 2 file\(s\) · 200 B/);
  assert.match(modal.detail, /only after its archived copy was verified/);
  assert.equal(secondModal(['A13']), undefined);
});

test('the notices say "the archive run", never "Cleaning A13"', () => {
  const entry = archiveEntry();
  const taken = handOffNotice({ kind: 'accepted', runId: runId(RUN), unit: '', productVersion: '0.1.0' }, ARCHIVE_RUN, 'clean');
  assert.match(taken.sentence, /^The archive run: the daemon took it as run 20000101T000000Z-1/);
  assert.match(resultNotice({ kind: 'ceiling', entry }, 1_800_000).sentence, /^Run 20000101T000000Z-1 \(the archive run\): state unknown/);
  const { runId: _id, ...unresolved } = entry;
  assert.match(resultNotice({ kind: 'neverRan', entry: { ...unresolved, kind: 'unresolved' } }, 0).sentence, /^The archive run confirmed at .* never ran/);
});

test('a done archive run says what it MOVED, never what it freed', () => {
  const sentence = resultNotice({ kind: 'run', entry: archiveEntry(), show: parseRunShow(goldenFile('runs-show-done.json')) }, 0).sentence;
  assert.match(sentence, /\(the archive run\) is done: moved /);
  assert.match(sentence, / objects archived\.$/);
  assert.doesNotMatch(sentence, /freed/);
});

test('a small archive run reads its moved bytes in a unit it reaches — moved 200 B, never 0.0 GB (own review #3, the second code round #3)', () => {
  const done = goldenFile('runs-show-done.json');
  const show = parseRunShow({ ...done, run: { ...bodyAt(done, 'run'), freedBytes: 200 } });
  assert.match(resultNotice({ kind: 'run', entry: archiveEntry(), show }, 0).sentence, /\(the archive run\) is done: moved 200 B, /);
});

/** The `status` golden of `name` with its running block carrying A13 alone. */
function archivingStatus(name: string): Body {
  const status = goldenFile(name);
  return { ...status, running: { ...bodyAt(status, 'running'), actions: ['A13'], current: '' } };
}

/** What the cleanup controls say while the daemon reports that run. */
function inFlightState(name: string): string {
  const snapshot: Snapshot = { status: answered('status', archivingStatus(name)), preview: answered('preview', headBody('preview')), doctor: undefined, checking: false };
  return deriveCleanup(snapshot, { entries: [], results: [], flowBusy: false }).controls.state;
}

test('the in-flight state of an A13 run reads "Archiving…", not "Cleaning… A13"', () => {
  assert.equal(inFlightState('status-running-live.json'), 'Archiving… the aged AI sessions');
});

test('a queued or wedged archive run is named the archive run too, never "A13" (own review #7)', () => {
  assert.equal(inFlightState('status-running-queued.json'), 'Queued… the archive run');
  assert.match(inFlightState('status-running-wedged.json'), /^Wedged: run \S+ \(the archive run\) — /);
});

// ---- 5. the flow ----

test('Archive now: the preview of A13, its modal, the journal entry BEFORE the confirm, ONE detached confirm, told as the archive run', async () => {
  const store = new MapStore();
  let seenBefore: string[] = [];
  const c = controller({ [PREVIEW_A13]: exited(0, a13PreviewText()), [CONFIRM_A13]: () => { seenBefore = new CleanupJournal(store).entries().map((e) => e.actions.join(',')); return exited(0, handOff()); } });
  const modals: Modal[] = [];
  const notices: { level: NoticeLevel; sentence: string }[] = [];
  const ui: CleanUi = { confirm: (m) => { modals.push(m); return Promise.resolve(true); }, notify: (level, sentence) => { notices.push({ level, sentence }); return Promise.resolve(undefined); } };
  const journal = new CleanupJournal(store, () => Date.parse('2026-10-10T12:00:00.000Z'));
  const flow = new CleanFlow({ controller: c.controller, journal, ui, follower: { started: () => undefined, kick: () => undefined }, now: () => 1_000_000, wallNow: () => Date.parse('2026-10-10T12:00:00.000Z'), distro: () => 'Ubuntu', changed: () => undefined });
  const outcome = await flow.archive();
  assert.equal(outcome.kind, 'handedOff');
  assert.deepEqual(c.runner.argvs(), [ROOT_CHECK, PREVIEW_A13, CONFIRM_A13]);
  assert.deepEqual(seenBefore, ['A13'], 'persisted before the call');
  assert.deepEqual(modals.map((m) => m.confirm), [ARCHIVE_LABEL]);
  assert.match(notices.at(-1)?.sentence ?? '', /^The archive run: the daemon took it as run/);
});

// ---- 6. the button ----

const READY: ArchiveState = {
  status: { kind: 'read', read: 'archiveStatus', distro: 'Ubuntu', body: goldenFile('archive-status.json') },
  preview: undefined,
  capabilities: [ARCHIVE_CAPABILITY, ...ARCHIVE_NOW_CAPABILITIES],
  busy: '', reading: false, asked: true, unavailable: '', cleanupFree: true, a13Offered: true,
};

function archiveNowEnabled(state: ArchiveState): boolean | undefined {
  return deriveArchive(state).buttons.find((b) => b.id === 'archiveNow')?.enabled;
}

test('Archive now requires exactly what the controller\'s preview and confirm of A13 need — ONE definition, the controller\'s (the second code round #2)', () => {
  assert.deepEqual([...ARCHIVE_NOW_CAPABILITIES].sort(), [...new Set([...previewNeeds(['A13']), ...confirmNeeds(['A13'])])].sort());
});

test('Archive now is enabled exactly when every condition holds — each one missing greys it', () => {
  assert.equal(archiveNowEnabled(READY), true);
  const without = (capability: string): ArchiveState => ({ ...READY, capabilities: (READY.capabilities ?? []).filter((c) => c !== capability) });
  const greyed: Record<string, ArchiveState> = {
    ...Object.fromEntries([ARCHIVE_CAPABILITY, ...ARCHIVE_NOW_CAPABILITIES].map((c) => [`no ${c}`, without(c)])),
    'A13 not offered': { ...READY, a13Offered: false },
    'no base folder': { ...READY, status: { kind: 'read', read: 'archiveStatus', distro: 'Ubuntu', body: { ...goldenFile('archive-status.json'), baseFolder: '' } } },
    'a flow busy': { ...READY, busy: 'Checking the folder…' },
    'a cleanup in flight': { ...READY, cleanupFree: false },
  };
  assert.ok(Object.keys(greyed).some((why) => why === 'no archive.preview'), 'the plan round\'s #0 is among them');
  for (const [why, state] of Object.entries(greyed)) {
    assert.equal(archiveNowEnabled(state), false, why);
  }
});

test('Archive now is a BARE message — any payload is dropped', () => {
  assert.deepEqual(parsePageMessage({ type: 'archiveNow' }), { type: 'archiveNow' });
  assert.equal(parsePageMessage({ type: 'archiveNow', ids: ['A13'] }), undefined);
});
