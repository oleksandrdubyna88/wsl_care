import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

import { deriveArchive, type ArchiveState } from '../archive/archiveView';
import { ceilingMs } from '../client/ceilings';
import { CleanFlow, type CleanUi } from '../cleanup/cleanFlow';
import { deriveCleanup } from '../cleanup/cleanupView';
import { CleanupJournal, type JournalEntry } from '../cleanup/journal';
import { parseRunShow } from '../cleanup/runAnswers';
import { ARCHIVE_LABEL, firstModal, secondModal, type Modal } from '../cleanup/modalText';
import { ARCHIVE_RUN, handOffNotice, resultNotice, type NoticeLevel } from '../cleanup/resultText';
import { parsePageMessage } from '../panel/messages';
import { CleanupController, type CleanupClient } from '../root/cleanupController';
import { rootTimeoutMs } from '../root/rootCall';
import { runIdOf, type RunId } from '../root/rootIds';
import type { HeldPreview } from '../root/rootOutcome';
import { DEFAULT_NUMBERS } from '../settings/numbers';
import type { Snapshot } from '../state/outcomeStore';
import { MapStore } from './support/memento';
import { answered, headBody, type Body } from './support/outcomes';
import { GOLDEN_ROOT } from './support/paths';
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

function golden(name: string): Body {
  return JSON.parse(fs.readFileSync(path.join(GOLDEN_ROOT, 'head', name), 'utf8')) as Body;
}

/** `act A13 --preview --json` as the daemon answers it: the act envelope of the A4 golden, A13's own line inside. */
function a13PreviewText(): string {
  return JSON.stringify({ ...golden('act-a4-preview.json'), actions: [golden('act-a13-preview-action.json')] });
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
  return { ...head, capabilities: (head.capabilities as string[]).filter((c) => c !== capability) };
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
  const ms = rootTimeoutMs({ op: 'preview', ids: ['A13'] as never });
  assert.equal(ms, 1000 * (DEFAULT_NUMBERS.statusSeconds + DEFAULT_NUMBERS.archivePreviewSeconds));
  assert.ok(ms > 1000 * 600, 'above the daemon\'s archive.previewTimeoutSeconds at its range maximum');
  assert.equal(ceilingMs({ ...DEFAULT_NUMBERS, archivePreviewSeconds: 900 }, { call: 'rootPreview', ids: ['A13'] }), 1000 * (DEFAULT_NUMBERS.statusSeconds + 900), 'it follows the setting');
});

// ---- 3. the items ----

test('A13\'s preview keeps each agent\'s item whole: its name, bytes and note', async () => {
  const preview = await heldA13();
  assert.deepEqual(preview.actions[0]?.details, [{ name: 'claude-code', bytes: 200, note: '1 session(s) due, 2 file(s)' }]);
});

// ---- 4. the words ----

test('the modal of A13 names each agent\'s sessions and bytes, how the move is made, and confirms with Archive — no second modal', async () => {
  const modal = firstModal(await heldA13(), false);
  assert.equal(modal.message, 'Archive the aged AI sessions in "Ubuntu"?');
  assert.equal(modal.confirm, ARCHIVE_LABEL);
  assert.match(modal.detail, /claude-code — 1 session\(s\) due, 2 file\(s\) · 0\.0 GB/);
  assert.match(modal.detail, /only after its archived copy was verified/);
  assert.equal(secondModal(['A13']), undefined);
});

test('the notices say "the archive run", never "Cleaning A13"', () => {
  const entry: JournalEntry = { id: 'e1', kind: 'run', op: 'clean', distro: 'Ubuntu', actions: ['A13'], since: '2026-10-10T12:00:00.000Z', runId: runId(RUN) };
  const taken = handOffNotice({ kind: 'accepted', runId: runId(RUN), unit: '', productVersion: '0.1.0' } as never, ARCHIVE_RUN, 'clean');
  assert.match(taken.sentence, /^The archive run: the daemon took it as run 20000101T000000Z-1/);
  assert.match(resultNotice({ kind: 'ceiling', entry }, 1_800_000).sentence, /^Run 20000101T000000Z-1 \(the archive run\): state unknown/);
  const { runId: _id, ...unresolved } = entry;
  assert.match(resultNotice({ kind: 'neverRan', entry: { ...unresolved, kind: 'unresolved' } }, 0).sentence, /^The archive run confirmed at .* never ran/);
});

test('a done archive run says what it MOVED, never what it freed', () => {
  const entry: JournalEntry = { id: 'e1', kind: 'run', op: 'clean', distro: 'Ubuntu', actions: ['A13'], since: '2026-10-10T12:00:00.000Z', runId: runId(RUN) };
  const sentence = resultNotice({ kind: 'run', entry, show: parseRunShow(golden('runs-show-done.json')) }, 0).sentence;
  assert.match(sentence, /\(the archive run\) is done: moved /);
  assert.match(sentence, / objects archived\.$/);
  assert.doesNotMatch(sentence, /freed/);
});

test('the in-flight state of an A13 run reads "Archiving…", not "Cleaning… A13"', () => {
  const live = golden('status-running-live.json');
  const status = { ...live, running: { ...(live.running as Body), actions: ['A13'], current: '' } };
  const snapshot: Snapshot = { status: answered('status', status), preview: answered('preview', headBody('preview')), doctor: undefined, checking: false };
  assert.equal(deriveCleanup(snapshot, { entries: [], results: [], flowBusy: false }).controls.state, 'Archiving… the aged AI sessions');
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
  status: { kind: 'read', read: 'archiveStatus', distro: 'Ubuntu', body: golden('archive-status.json') },
  preview: undefined,
  capabilities: ['archive.checkBase', 'archive.preview', 'archive.run', 'act.detach'],
  busy: '', reading: false, asked: true, unavailable: '', cleanupFree: true, a13Offered: true,
};

function archiveNowEnabled(state: ArchiveState): boolean | undefined {
  return deriveArchive(state).buttons.find((b) => b.id === 'archiveNow')?.enabled;
}

test('Archive now is enabled exactly when every condition holds — each one missing greys it', () => {
  assert.equal(archiveNowEnabled(READY), true);
  const without = (capability: string): ArchiveState => ({ ...READY, capabilities: (READY.capabilities ?? []).filter((c) => c !== capability) });
  const greyed: Record<string, ArchiveState> = {
    'no archive.run': without('archive.run'),
    'no archive.preview (plan round #0)': without('archive.preview'),
    'no act.detach': without('act.detach'),
    'A13 not offered': { ...READY, a13Offered: false },
    'no base folder': { ...READY, status: { kind: 'read', read: 'archiveStatus', distro: 'Ubuntu', body: { ...golden('archive-status.json'), baseFolder: '' } } },
    'a flow busy': { ...READY, busy: 'Checking the folder…' },
    'a cleanup in flight': { ...READY, cleanupFree: false },
  };
  for (const [why, state] of Object.entries(greyed)) {
    assert.equal(archiveNowEnabled(state), false, why);
  }
});

test('Archive now is a BARE message — any payload is dropped', () => {
  assert.deepEqual(parsePageMessage({ type: 'archiveNow' }), { type: 'archiveNow' });
  assert.equal(parsePageMessage({ type: 'archiveNow', ids: ['A13'] }), undefined);
});
