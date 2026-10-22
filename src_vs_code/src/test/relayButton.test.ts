import assert from 'node:assert/strict';
import { test } from 'node:test';

import { ceilingMs } from '../client/ceilings';
import { CleanFlow, type CleanUi } from '../cleanup/cleanFlow';
import { deriveCleanup } from '../cleanup/cleanupView';
import { CleanupJournal, type JournalEntry } from '../cleanup/journal';
import { firstModal, secondModal, STOP_RELAYS_CONFIRM, type Modal } from '../cleanup/modalText';
import { handOffNotice, RELAY_RUN, resultNotice, type NoticeLevel } from '../cleanup/resultText';
import { parseRunShow } from '../cleanup/runAnswers';
import { parsePageMessage } from '../panel/messages';
import { deriveRelays, STOP_RELAYS_CAPABILITIES, stopRelaysBlocker } from '../relays/relaysView';
import { CleanupController, confirmNeeds, previewNeeds, type CleanupClient } from '../root/cleanupController';
import { MAX_PROCESS_ARGS, runIdOf, type RunId } from '../root/rootIds';
import type { HeldPreview } from '../root/rootOutcome';
import { DEFAULT_NUMBERS } from '../settings/numbers';
import type { Snapshot } from '../state/outcomeStore';
import { bodyAt, stringsAt } from './support/body';
import { MapStore } from './support/memento';
import { answered, headBody, type Body } from './support/outcomes';
import { goldenFile } from './support/paths';
import { exited, recordingRunner, type Scripted } from './support/recordingRunner';
import { relayKeys, relayPreviewBody } from './support/relayBodies';
import { DEFAULT_TARGET } from '../wsl/distroTarget';

/**
 * S7b.2b: *Stop client-gone relays* — A21 through the cleanup controller as it is, on *Archive now*'s road with a `--process`
 * tail. Its capabilities, its preview's ceiling, its own words (the modal, the notices, the in-flight state), the three preview
 * states, the transaction, and the button's conditions. The preview bodies are the daemon's shape (`support/relayBodies.ts`).
 */

const ROOT = '-d Ubuntu -u root --cd / --exec /opt/wsl-care/bin/wsl-care';
const RUN = '20000101T000000Z-1';
const ROOT_CHECK = `${ROOT} --version`;
const PREVIEW_A21 = `${ROOT} act A21 --preview --json`;

function confirmOf(keys: readonly string[]): string {
  return `${ROOT} act A21 --confirm --manual --detach ${keys.map((k) => `--process ${k}`).join(' ')} --json`;
}

function handOff(): string {
  return JSON.stringify({ schemaVersion: 1, result: 'accepted', kind: 'act', runId: RUN, unit: `wsl-care-act@${RUN}.service`, productVersion: '0.4.0' });
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

function controller(script: Readonly<Record<string, Scripted>>, status: () => Body = () => headBody('status')): { controller: CleanupController; runner: ReturnType<typeof recordingRunner> } {
  const runner = recordingRunner({ [ROOT_CHECK]: exited(0, '0.4.0\n'), ...script });
  const client: CleanupClient = { rootTarget: () => Promise.resolve({ wsl: 'C:\\Windows\\System32\\wsl.exe', distro: 'Ubuntu' }), run: () => Promise.resolve(answered('status', status())) };
  let now = 1_000_000;

  return { controller: new CleanupController({ client, runner: runner.runner, now: () => now, sleep: (ms) => { now += ms; return Promise.resolve(); } }), runner };
}

async function held(keys: readonly string[], options: Parameters<typeof relayPreviewBody>[1] = {}): Promise<HeldPreview> {
  const outcome = await controller({ [PREVIEW_A21]: exited(0, JSON.stringify(relayPreviewBody(keys, options))) }).controller.preview(['A21'], DEFAULT_TARGET);
  assert.equal(outcome.kind, 'previewed', JSON.stringify(outcome).slice(0, 300));
  assert.ok(outcome.kind === 'previewed');
  return outcome.preview;
}

/** The journal entry of a run of A21 alone — what every relay word is chosen by. */
function relayEntry(): Extract<JournalEntry, { kind: 'run' }> {
  return { id: 'e1', kind: 'run', op: 'clean', distro: 'Ubuntu', actions: ['A21'], since: '2026-10-11T12:00:00.000Z', runId: runId(RUN) };
}

interface Recorded {
  readonly modals: Modal[];
  readonly notices: { level: NoticeLevel; sentence: string }[];
}

function flowOver(c: { controller: CleanupController }, store = new MapStore(), answer = true): { flow: CleanFlow; recorded: Recorded; journal: CleanupJournal } {
  const recorded: Recorded = { modals: [], notices: [] };
  const ui: CleanUi = { confirm: (m) => { recorded.modals.push(m); return Promise.resolve(answer); }, notify: (level, sentence) => { recorded.notices.push({ level, sentence }); return Promise.resolve(undefined); } };
  const journal = new CleanupJournal(store, () => Date.parse('2026-10-11T12:00:00.000Z'));
  const flow = new CleanFlow({ controller: c.controller, journal, ui, follower: { started: () => undefined, kick: () => undefined }, now: () => 1_000_000, wallNow: () => Date.parse('2026-10-11T12:00:00.000Z'), distro: () => 'Ubuntu', changed: () => undefined });

  return { flow, recorded, journal };
}

// ---- 1. the capabilities and the ceiling ----

test('a preview of A21 without act.processList advertised is refused before any root call', async () => {
  const c = controller({ [PREVIEW_A21]: exited(0, JSON.stringify(relayPreviewBody(relayKeys(1)))) }, () => statusWithout('act.processList'));
  const outcome = await c.controller.preview(['A21'], DEFAULT_TARGET);
  assert.notEqual(outcome.kind, 'previewed');
  assert.deepEqual(c.runner.argvs(), [], 'nothing ran as root');
});

test('A21\'s preview ceiling is the fd scan\'s range maximum, not a Docker row\'s snapshot', () => {
  assert.equal(ceilingMs(DEFAULT_NUMBERS, { call: 'rootPreview', ids: ['A21'] }), ceilingMs(DEFAULT_NUMBERS, { call: 'rootPreview', ids: ['A8'] }) + 30_000);
});

// ---- 2. the words ----

test('the modal of A21 says SIGTERM asks, names every listed relay, binds the shown list and confirms with Stop relays — no second modal', async () => {
  const modal = firstModal(await held(relayKeys(3)), false);
  assert.equal(modal.message, 'Stop 3 client-gone relays in "Ubuntu"?');
  assert.equal(modal.confirm, STOP_RELAYS_CONFIRM);
  assert.match(modal.detail, /Stops exactly the 3 relays this preview showed/);
  assert.match(modal.detail, /• 12000 creds-mcp — the creds-mcp\.exe relay, pid 12000/);
  assert.match(modal.detail, /SIGTERM asks each relay to end, and its Windows program ends with it\. A relay that does not end is reported/);
  assert.match(modal.detail, /never sent SIGKILL/);
  assert.match(modal.detail, /\/mcp/);
  assert.match(modal.detail, /a smaller count is not a failure/, 'the timer\'s own watch may end some first');
  assert.equal(secondModal(['A21']), undefined);
});

test('a long list: all are stopped, the first 20 listed — and past the command line\'s cap the modal says the timer takes the rest', async () => {
  const many = firstModal(await held(relayKeys(30)), false);
  assert.match(many.detail, /… all 30 are stopped; the first 20 are listed/);
  const cut = firstModal(await held(relayKeys(MAX_PROCESS_ARGS + 7)), false);
  assert.match(cut.detail, new RegExp(`Only the first ${MAX_PROCESS_ARGS} shown are stopped; ${MAX_PROCESS_ARGS + 7} were selected — the timer takes the rest\\.`));
});

test('the notices say "the relay stop", never "Cleaning A21"', () => {
  const entry = relayEntry();
  assert.match(handOffNotice({ kind: 'accepted', runId: runId(RUN), unit: '', productVersion: '0.4.0' }, RELAY_RUN, 'clean').sentence, /^The relay stop: the daemon took it as run 20000101T000000Z-1/);
  assert.match(resultNotice({ kind: 'ceiling', entry }, 1_800_000).sentence, /^Run 20000101T000000Z-1 \(the relay stop\): state unknown/);
});

/** `runs show` done, with its line's actions replaced by A21's. */
function relayDone(status: string, count: number): ReturnType<typeof parseRunShow> {
  const done = goldenFile('runs-show-done.json');
  const run = bodyAt(done, 'run');
  return parseRunShow({ ...done, run: { ...run, actions: [{ id: 'A21', status, count, freedBytes: 0 }] } });
}

test('a done relay stop says how many relays it ENDED and that their Windows programs end with them — never what it freed', () => {
  const sentence = resultNotice({ kind: 'run', entry: relayEntry(), show: relayDone('ran', 2) }, 0).sentence;
  assert.match(sentence, /\(the relay stop\) is done: ended 2 relays; their Windows programs end with them\.$/);
  assert.doesNotMatch(sentence, /freed|GB/);
});

test('a relay stop whose A21 failed reports the relays that did not end on SIGTERM — their Windows programs keep running', () => {
  const sentence = resultNotice({ kind: 'run', entry: relayEntry(), show: relayDone('failed', 1) }, 0).sentence;
  assert.match(sentence, /ended 1 relay; .* Some relays did not end on SIGTERM; their Windows programs keep running/);
});

/** What the cleanup controls say while the daemon reports a run of A21 alone. */
function inFlightState(name: string): string {
  const status = goldenFile(name);
  const snapshot: Snapshot = { status: answered('status', { ...status, running: { ...bodyAt(status, 'running'), actions: ['A21'], current: '' } }), preview: answered('preview', headBody('preview')), doctor: undefined, checking: false };
  return deriveCleanup(snapshot, { entries: [], results: [], flowBusy: false }).controls.state;
}

test('the in-flight state of an A21 run reads "Stopping… the client-gone relays", and a queued or wedged one is named the relay stop', () => {
  assert.equal(inFlightState('status-running-live.json'), 'Stopping… the client-gone relays');
  assert.equal(inFlightState('status-running-queued.json'), 'Queued… the relay stop');
  assert.match(inFlightState('status-running-wedged.json'), /^Wedged: run \S+ \(the relay stop\) — /);
});

// ---- 3. the flow and its three preview states ----

test('Stop client-gone relays: the preview of A21, its modal, the journal entry BEFORE the confirm, ONE detached confirm with exactly the shown pairs', async () => {
  const keys = relayKeys(2);
  const store = new MapStore();
  let seenBefore: string[] = [];
  const c = controller({ [PREVIEW_A21]: exited(0, JSON.stringify(relayPreviewBody(keys))), [confirmOf(keys)]: () => { seenBefore = new CleanupJournal(store, () => Date.parse('2026-10-11T12:00:00.000Z')).entries().map((e) => e.actions.join(',')); return exited(0, handOff()); } });
  const { flow, recorded } = flowOver(c, store);
  const outcome = await flow.relays(DEFAULT_TARGET);
  assert.equal(outcome.kind, 'handedOff', JSON.stringify(outcome).slice(0, 300));
  assert.deepEqual(c.runner.argvs(), [ROOT_CHECK, PREVIEW_A21, confirmOf(keys)]);
  assert.deepEqual(seenBefore, ['A21'], 'persisted before the call');
  assert.deepEqual(recorded.modals.map((m) => m.confirm), [STOP_RELAYS_CONFIRM]);
  assert.match(recorded.notices.at(-1)?.sentence ?? '', /^The relay stop: the daemon took it as run/);
});

test('an EMPTY preview opens no modal and confirms nothing — it tells the daemon\'s basis, every kept relay and why', async () => {
  const c = controller({ [PREVIEW_A21]: exited(0, JSON.stringify(relayPreviewBody([], { basis: '2 relay(s) of user kept: 2 because its stdio is a socket' }))) });
  const { flow, recorded, journal } = flowOver(c);
  const outcome = await flow.relays(DEFAULT_TARGET);
  assert.equal(outcome.kind, 'nothing');
  assert.deepEqual(recorded.modals, []);
  assert.match(recorded.notices.at(-1)?.sentence ?? '', /^No relay's client is shown gone, so nothing was stopped\. The daemon says: 2 relay\(s\) of user kept: 2 because its stdio is a socket/);
  assert.equal(c.runner.argvs().some((a) => a.includes('--confirm')), false);
  assert.deepEqual(journal.entries(), []);
});

test('an UNAVAILABLE preview opens no modal and confirms nothing — it tells the daemon\'s reason', async () => {
  const c = controller({ [PREVIEW_A21]: exited(0, JSON.stringify(relayPreviewBody([], { available: false, reason: 'the boot id cannot be read' }))) });
  const { flow, recorded } = flowOver(c);
  const outcome = await flow.relays(DEFAULT_TARGET);
  assert.equal(outcome.kind, 'nothing');
  assert.deepEqual(recorded.modals, []);
  assert.match(recorded.notices.at(-1)?.sentence ?? '', /^The daemon could not read the relays: the boot id cannot be read\. Nothing was stopped\./);
});

test('a declined modal starts nothing', async () => {
  const c = controller({ [PREVIEW_A21]: exited(0, JSON.stringify(relayPreviewBody(relayKeys(1)))) });
  const { flow, journal } = flowOver(c, new MapStore(), false);
  assert.equal((await flow.relays(DEFAULT_TARGET)).kind, 'declined');
  assert.equal(c.runner.argvs().some((a) => a.includes('--confirm')), false);
  assert.deepEqual(journal.entries(), []);
});

// ---- 4. the button and the line ----

function snapshotWith(status: Body): Snapshot {
  return { status: answered('status', status), preview: undefined, doctor: undefined, checking: false };
}

const FREE = { enabled: true, reason: '' } as const;

test('Stop client-gone relays requires exactly what the controller\'s preview and confirm of A21 need — ONE definition, the controller\'s', () => {
  assert.deepEqual([...STOP_RELAYS_CAPABILITIES].sort(), [...new Set([...previewNeeds(['A21']), ...confirmNeeds(['A21'])])].sort());
});

test('the line reads the daemon\'s counts and its switch with the command that flips it; the button is enabled when every condition holds', () => {
  const head = headBody('status');
  const relays = deriveRelays(snapshotWith({ ...head, interopRelays: { available: true, count: 3, reparented: 1, autoStop: true, command: 'wsl-care config set auto.A21 false' } }), FREE);
  assert.equal(relays.line, 'Windows MCP relays: 3 (1 re-parented) · the timer stops client-gone relays: on — to turn it off: wsl-care config set auto.A21 false');
  assert.equal(relays.button?.enabled, true);
  assert.equal(relays.why, '');
});

test('an unavailable block says so with its reason — never "0 relays"', () => {
  const head = headBody('status');
  const relays = deriveRelays(snapshotWith({ ...head, interopRelays: { available: false, reason: 'the process table could not be read', autoStop: false, command: 'wsl-care config set auto.A21 true' } }), FREE);
  assert.equal(relays.line, 'Windows MCP relays: unavailable — the process table could not be read · the timer stops client-gone relays: off — to turn it on: wsl-care config set auto.A21 true');
  assert.equal(relays.level, 'warn');
});

test('an older daemon (no interopRelays) shows no line and no button, and the host refuses the bare request', () => {
  const head = headBody('status');
  const { interopRelays: _gone, ...older } = head;
  const relays = deriveRelays(snapshotWith(older), FREE);
  assert.equal(relays.line, '');
  assert.equal(relays.button, undefined);
  assert.match(stopRelaysBlocker(snapshotWith(older), FREE), /publishes no interop relays/);
});

test('the button is greyed with each blocker\'s sentence: a cleanup in flight, A21 not offered, a capability missing', () => {
  const head = headBody('status');
  const greyed: Record<string, [Body, { enabled: boolean; reason: string }, RegExp]> = {
    'a cleanup in flight': [head, { enabled: false, reason: 'a run is in flight' }, /^a run is in flight$/],
    'A21 not offered': [{ ...head, actions: stringsAt(head, 'actions').filter((a) => a !== 'A21') }, FREE, /does not offer A21/],
    ...Object.fromEntries(STOP_RELAYS_CAPABILITIES.map((c) => [`no ${c}`, [statusWithout(c), FREE, new RegExp(`does not advertise ${c.replace('.', '\\.')}`)]])),
  };
  for (const [why, [status, cleanup, words]] of Object.entries(greyed)) {
    const relays = deriveRelays(snapshotWith(status), cleanup);
    assert.equal(relays.button?.enabled, false, why);
    assert.match(relays.why, words, why);
    assert.equal(stopRelaysBlocker(snapshotWith(status), cleanup), relays.why, `${why}: the host refuses on the same sentence`);
  }
});

test('Stop client-gone relays is a BARE message — any payload is dropped', () => {
  assert.deepEqual(parsePageMessage({ type: 'stopRelays' }), { type: 'stopRelays' });
  assert.equal(parsePageMessage({ type: 'stopRelays', processes: ['12000:36500'] }), undefined);
});

test('an available preview WITHOUT a shown list is no empty selection — it is told as a preview that cannot be acted on (code round)', async () => {
  const body = relayPreviewBody(relayKeys(1));
  const actions = body.actions as Record<string, unknown>[];
  const { shown: _dropped, ...withoutShown } = actions[0] ?? {};
  const c = controller({ [PREVIEW_A21]: exited(0, JSON.stringify({ ...body, actions: [withoutShown] })) });
  const { flow, recorded } = flowOver(c);
  const outcome = await flow.relays(DEFAULT_TARGET);
  assert.equal(outcome.kind, 'nothing');
  assert.deepEqual(recorded.modals, []);
  assert.match(recorded.notices.at(-1)?.sentence ?? '', /^The daemon's preview carried no relay list/);
  assert.doesNotMatch(recorded.notices.at(-1)?.sentence ?? '', /No relay's client is shown gone/);
});

test('own review #3: a Stop of the daemon\'s OWN A21 run (the watch, trigger timer) never adopts the panel\'s unresolved A21 entry', async () => {
  const store = new MapStore();
  const journal = new CleanupJournal(store, () => Date.parse('2026-10-11T12:00:00.000Z'));
  await journal.add({ kind: 'unresolved', op: 'clean', distro: 'Ubuntu', actions: ['A21'], since: '2026-10-11T11:59:00.000Z' });
  const stop = `${ROOT} act --stop ${RUN} --json`;
  const c = controller({ [stop]: exited(0, JSON.stringify({ schemaVersion: 1, result: 'stopping', kind: 'act', runId: RUN, unit: `wsl-care-act@${RUN}.service`, productVersion: '0.4.0' })) });
  const { flow } = flowOver(c, store);
  await flow.stop({ runId: runId(RUN), actions: ['A21'], trigger: 'timer' }, DEFAULT_TARGET);
  assert.ok(journal.entries().some((e) => e.kind === 'unresolved' && e.actions.join() === 'A21'), 'the panel\'s own entry stays unresolved, for its own run');
});

test('own review #4: a confirm of a preview that carried no process list says so — not "nothing to stop"', async () => {
  const c = controller({ [PREVIEW_A21]: exited(0, JSON.stringify(relayPreviewBody([], { available: false, reason: 'the boot id cannot be read' }))) });
  const previewed = await c.controller.preview(['A21'], DEFAULT_TARGET);
  assert.ok(previewed.kind === 'previewed');
  const outcome = await c.controller.confirm(previewed.preview);
  assert.equal(outcome.kind, 'shownListInvalid');
  assert.match(JSON.stringify(outcome), /carried no process list/);
});

test('own review #5: a done relay stop whose runs show has no line says "is done." — never "ended 0 relays"', () => {
  const done = goldenFile('runs-show-done.json');
  const show = parseRunShow({ ...done, run: undefined });
  assert.equal(resultNotice({ kind: 'run', entry: relayEntry(), show }, 0).sentence, 'Run 20000101T000000Z-1 (the relay stop) is done.');
});

test('own review #6: a non-object interopRelays shows no button AND refuses the command — the two agree', () => {
  for (const odd of [null, 'yes', 3]) {
    const snapshot = snapshotWith({ ...headBody('status'), interopRelays: odd });
    assert.equal(deriveRelays(snapshot, FREE).button, undefined, JSON.stringify(odd));
    assert.match(stopRelaysBlocker(snapshot, FREE), /publishes no interop relays/, JSON.stringify(odd));
  }
});

test('own review #8: a shown list that names a relay twice refuses the preview', async () => {
  const keys = relayKeys(1);
  const c = controller({ [PREVIEW_A21]: exited(0, JSON.stringify(relayPreviewBody([...keys, ...keys]))) });
  const outcome = await c.controller.preview(['A21'], DEFAULT_TARGET);
  assert.equal(outcome.kind, 'shownListInvalid');
  assert.match(JSON.stringify(outcome), /names an entry twice/);
});
