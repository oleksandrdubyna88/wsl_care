import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

import { CleanFlow, PREVIEW_EXPIRY_MS, RETRY_LABEL, type CleanUi } from '../cleanup/cleanFlow';
import { CleanupJournal } from '../cleanup/journal';
import type { Modal } from '../cleanup/modalText';
import type { NoticeLevel } from '../cleanup/resultText';
import type { ProcessRequest } from '../process/runner';
import { CleanupController, type CleanupClient } from '../root/cleanupController';
import { runIdOf, type RunId } from '../root/rootIds';
import { MapStore } from './support/memento';
import { answered, headBody } from './support/outcomes';
import { GOLDEN_ROOT } from './support/paths';
import { exited, recordingRunner, type Scripted } from './support/recordingRunner';

/**
 * The cleanup buttons' host transaction (E6.S3, plan §7.3, §15j M8, m8, m9; §15k #12, #19): the REAL cleanup controller
 * over a recording runner that starts nothing (every root call asserted by its exact argv and stdin), a journal over a
 * `globalState`-shaped store, the modals and notifications recorded, the monotonic clock the test's — so "the preview's age
 * is re-checked after the LAST modal" is a frozen-clock assertion, and "persisted before the call" is read inside the call.
 */

const ROOT = '-d Ubuntu -u root --cd / --exec /opt/wsl-care/bin/wsl-care';
const RUN = '20000101T000000Z-1';
const ROOT_CHECK = `${ROOT} --version`;
const PREVIEW_A4 = `${ROOT} act A4 --preview --json`;
const CONFIRM_A4 = `${ROOT} act A4 --confirm --manual --detach --only - --json`;

function runId(text: string): RunId {
  const id = runIdOf(text);
  assert.ok(id !== undefined);
  return id;
}

function previewText(edit: (actions: Record<string, unknown>[]) => void = () => undefined): string {
  const body = JSON.parse(fs.readFileSync(path.join(GOLDEN_ROOT, 'head', 'act-a4-preview.json'), 'utf8')) as Record<string, unknown>;
  edit(body.actions as Record<string, unknown>[]);
  return JSON.stringify(body);
}

function action(id: string): Record<string, unknown> {
  return { id, summary: '', status: 'previewed', reason: '', preview: { what: `${id} things`, available: true, count: 2, bytes: 2e9, items: [] } };
}

function handOff(result = 'accepted'): string {
  return JSON.stringify({ schemaVersion: 1, result, kind: 'act', runId: RUN, unit: `wsl-care-act@${RUN}.service`, productVersion: '0.1.0' });
}

interface World {
  readonly flow: CleanFlow;
  readonly runner: ReturnType<typeof recordingRunner>;
  readonly store: MapStore;
  readonly journal: CleanupJournal;
  readonly modals: Modal[];
  readonly notices: { level: NoticeLevel; sentence: string; actions: readonly string[] }[];
  readonly started: string[];
  readonly clock: { now: number };
  answers: boolean[];
  noticeAnswer: string | undefined;
  onModal: (modal: Modal) => void;
  kicks: number;
}

function world(script: Readonly<Record<string, Scripted>> = {}, distro: () => string | undefined = () => 'Ubuntu', status: () => Record<string, unknown> = () => headBody('status')): World {
  const runner = recordingRunner({ [ROOT_CHECK]: exited(0, '0.1.0\n'), ...script });
  const clock = { now: 1_000_000 };
  const client: CleanupClient = { rootTarget: () => Promise.resolve({ wsl: 'C:\\Windows\\System32\\wsl.exe', distro: 'Ubuntu' }), run: () => Promise.resolve(answered('status', status())) };
  const controller = new CleanupController({ client, runner: runner.runner, now: () => clock.now, sleep: (ms) => { clock.now += ms; return Promise.resolve(); } });
  const store = new MapStore();
  const journal = new CleanupJournal(store);
  const w = { runner, store, journal, modals: [], notices: [], started: [], clock, answers: [], noticeAnswer: undefined, onModal: () => undefined, kicks: 0 } as unknown as World;
  const ui: CleanUi = {
    confirm: (modal) => { w.modals.push(modal); w.onModal(modal); return Promise.resolve(w.answers.length === 0 ? true : (w.answers.shift() ?? true)); },
    notify: (level, sentence, actions = []) => { w.notices.push({ level, sentence, actions }); return Promise.resolve(w.noticeAnswer); },
  };
  (w as { flow: CleanFlow }).flow = new CleanFlow({
    controller, journal, ui,
    follower: { started: (id) => { w.started.push(id); }, kick: () => { w.kicks += 1; } },
    now: () => clock.now, wallNow: () => Date.parse('2026-10-05T10:00:00.000Z'), distro, changed: () => undefined,
  });
  return w;
}

test('Clean A4: preview, ONE modal, the confirm — persisted UNRESOLVED before the call goes out, then the run; followed; told', async () => {
  let seenBeforeCall: unknown;
  const w = world({ [PREVIEW_A4]: exited(0, previewText()), [CONFIRM_A4]: (_request: ProcessRequest) => { seenBeforeCall = new CleanupJournal(w.store).entries().map((e) => e.kind); return exited(0, handOff()); } });
  const outcome = await w.flow.clean(['A4'], false);
  assert.equal(outcome.kind, 'handedOff');
  assert.deepEqual(w.runner.argvs(), [ROOT_CHECK, PREVIEW_A4, CONFIRM_A4]);
  assert.deepEqual(seenBeforeCall, ['unresolved'], 'common.durable-status rule 1: written before the work starts');
  const entries = w.journal.entries();
  assert.equal(entries.length, 1);
  assert.ok(entries[0]?.kind === 'run' && entries[0].runId === runId(RUN), JSON.stringify(entries));
  assert.deepEqual(w.started, [entries[0]?.id]);
  assert.ok(w.kicks > 0, 'the follower was kicked');
  assert.equal(w.modals.length, 1, 'A4 needs no second confirmation');
  assert.equal(w.notices.at(-1)?.level, 'info');
  assert.match(w.notices.at(-1)?.sentence ?? '', /run 20000101T000000Z-1/);
  assert.equal(w.flow.busy(), false);
});

test('a 387-volume A4 pipes ALL 387 names on stdin, none on the command line', async () => {
  const w = world({ [PREVIEW_A4]: exited(0, previewText()), [CONFIRM_A4]: exited(0, handOff()) });
  await w.flow.clean(['A4'], false);
  const confirm = w.runner.requests.find((r) => r.args.includes('--confirm'));
  const lines = confirm?.stdin?.toString('utf8').split('\n').filter((l) => l.length > 0) ?? [];
  assert.equal(lines.length, 387);
  assert.ok(confirm !== undefined && confirm.args.join(' ').length < 200);
});

test('declined: nothing confirmed, nothing written, the in-flight flag back to idle', async () => {
  const w = world({ [PREVIEW_A4]: exited(0, previewText()) });
  w.answers = [false];
  assert.equal((await w.flow.clean(['A4'], false)).kind, 'declined');
  assert.deepEqual(w.runner.argvs(), [ROOT_CHECK, PREVIEW_A4]);
  assert.deepEqual(w.journal.entries(), []);
  assert.equal(w.store.writes, 0);
});

test('Clean selected = ONE act call holding every selected id; the second confirmation (A5, A6Unused) is asked, and declining it confirms nothing', async () => {
  const preview = `${ROOT} act A4,A5,A6Unused --preview --json`;
  const confirm = `${ROOT} act A4,A5,A6Unused --confirm --manual --detach --only - --json`;
  const script = { [preview]: exited(0, previewText((a) => a.push(action('A5'), action('A6Unused')))), [confirm]: exited(0, handOff()) };
  const declined = world(script);
  declined.answers = [true, false];
  assert.equal((await declined.flow.clean(['A6Unused', 'A5', 'A4'], true)).kind, 'declined');
  assert.equal(declined.modals.length, 2);
  assert.match(declined.modals[1]?.detail ?? '', /containers\.stoppedOlderThanDays/);
  assert.equal(declined.runner.argvs().filter((a) => a.includes('--confirm')).length, 0);
  const w = world(script);
  assert.equal((await w.flow.clean(['A6Unused', 'A5', 'A4'], true)).kind, 'handedOff');
  assert.deepEqual(w.runner.argvs().filter((a) => a.includes(' act ')), [preview, confirm], 'ONE preview, ONE confirm');
  assert.match(w.modals[0]?.message ?? '', /the 3 selected rows/);
});

test('§15k #12: the preview\'s age is re-checked AFTER the last modal — expired, it is taken again and the NEW count shown before any confirm', async () => {
  let previews = 0;
  const w = world({
    [PREVIEW_A4]: () => { previews += 1; return exited(0, previewText((a) => { if (previews > 1) { const a4 = a[0] as { preview: { count: number }; shown: string[] }; a4.preview.count = 380; a4.shown = a4.shown.slice(0, 380); } })); },
    [CONFIRM_A4]: exited(0, handOff()),
  });
  w.onModal = () => { if (w.modals.length === 1) { w.clock.now += PREVIEW_EXPIRY_MS + 1; } };
  assert.equal((await w.flow.clean(['A4'], false)).kind, 'handedOff');
  assert.equal(previews, 2);
  assert.equal(w.modals.length, 2, 'the modal shown again over the new preview');
  assert.match(w.modals[1]?.detail ?? '', /380 objects/);
  assert.ok(w.notices.some((n) => /older than 5 minutes/.test(n.sentence)));
  assert.deepEqual(w.runner.argvs().slice(-1), [CONFIRM_A4]);
});

test('an age of exactly 5 minutes is still fresh; a preview that keeps expiring ends the flow after three rounds, nothing confirmed', async () => {
  const fresh = world({ [PREVIEW_A4]: exited(0, previewText()), [CONFIRM_A4]: exited(0, handOff()) });
  fresh.onModal = () => { fresh.clock.now += PREVIEW_EXPIRY_MS; };
  assert.equal((await fresh.flow.clean(['A4'], false)).kind, 'handedOff');
  const stale = world({ [PREVIEW_A4]: exited(0, previewText()) });
  stale.onModal = () => { stale.clock.now += PREVIEW_EXPIRY_MS + 1; };
  assert.equal((await stale.flow.clean(['A4'], false)).kind, 'expired');
  assert.equal(stale.runner.argvs().filter((a) => a === PREVIEW_A4).length, 3);
  assert.deepEqual(stale.journal.entries(), []);
});

test('a refusal (75): its own words — with the running block the daemon reports — and the unresolved entry REMOVED', async () => {
  const w = world({ [PREVIEW_A4]: exited(0, previewText()), [CONFIRM_A4]: exited(75, '', 'wsl-care: busy: a run is acting\n') });
  const outcome = await w.flow.clean(['A4'], false);
  assert.equal(outcome.kind, 'handedOff');
  assert.deepEqual(w.journal.entries(), [], 'nothing was written by the daemon, nothing is followed');
  assert.equal(w.notices.at(-1)?.level, 'error');
  assert.match(w.notices.at(-1)?.sentence ?? '', /^Another run holds the daemon; nothing was written\./);
});

test('§15k #19: the daemon refusing the piped list (its 10 s stdin ceiling) is shown with a Retry — and Retry runs the whole flow again', async () => {
  let confirms = 0;
  const w = world({ [PREVIEW_A4]: exited(0, previewText()), [CONFIRM_A4]: () => { confirms += 1; return confirms === 1 ? exited(2, '', 'wsl-care: the --only list did not end within 10 s\n') : exited(0, handOff()); } });
  w.noticeAnswer = RETRY_LABEL;
  assert.equal((await w.flow.clean(['A4'], false)).kind, 'handedOff');
  const refusal = w.notices.find((n) => n.level === 'error');
  assert.ok(refusal !== undefined);
  assert.match(refusal.sentence, /refused the list of volumes/);
  assert.deepEqual(refusal.actions, [RETRY_LABEL]);
  assert.equal(confirms, 2);
  assert.equal(w.journal.entries()[0]?.kind, 'run');
});

test('§15k #3: a detach whose answer was not seen and whose run never showed stays UNRESOLVED in the journal — the follower resolves it', async () => {
  const w = world({ [PREVIEW_A4]: exited(0, previewText()), [CONFIRM_A4]: { kind: 'timedOut', timeoutMs: 90_000, stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) } });
  const outcome = await w.flow.clean(['A4'], false);
  assert.ok(outcome.kind === 'handedOff' && outcome.outcome.kind === 'outcomeUnknown');
  assert.deepEqual(w.journal.entries().map((e) => e.kind), ['unresolved']);
  assert.equal(w.notices.at(-1)?.level, 'warn');
  assert.ok(w.kicks > 0);
});

test('m9: a second cleanup in this window while the first is still at its modal is refused at once, starting nothing', async () => {
  const w = world({ [PREVIEW_A4]: exited(0, previewText()), [CONFIRM_A4]: exited(0, handOff()) });
  let second: Promise<unknown> | undefined;
  w.onModal = () => { second ??= w.flow.clean(['A4'], false); };
  await w.flow.clean(['A4'], false);
  assert.deepEqual(await second, { kind: 'busy' });
  assert.equal(w.runner.argvs().filter((a) => a === PREVIEW_A4).length, 1);
  assert.ok(w.notices.some((n) => /already/.test(n.sentence)));
});

test('a preview refused (the ids not offered) is told in its own words; nothing is written', async () => {
  const w = world({}, () => 'Ubuntu', () => ({ ...headBody('status'), actions: ['A4'] }));
  const outcome = await w.flow.clean(['A9'], false);
  assert.equal(outcome.kind, 'refused');
  assert.match(w.notices.at(-1)?.sentence ?? '', /Not offered by this daemon/);
  assert.equal(w.store.writes, 0);
});

test('Stop: a modal first, then act --stop as one call; a run already followed is not followed twice; declining starts nothing', async () => {
  const stop = `${ROOT} act --stop ${RUN} --json`;
  const stopping = JSON.stringify({ schemaVersion: 1, result: 'stopping', kind: 'act', runId: RUN, unit: `wsl-care-act@${RUN}.service`, productVersion: '0.1.0' });
  const declined = world({ [stop]: exited(0, stopping) });
  declined.answers = [false];
  assert.equal((await declined.flow.stop({ runId: runId(RUN), actions: ['A4'] })).kind, 'declined');
  assert.deepEqual(declined.runner.argvs(), []);
  const w = world({ [stop]: exited(0, stopping) });
  await w.journal.add({ kind: 'run', op: 'clean', distro: 'Ubuntu', actions: ['A4'], since: '2026-10-05T09:00:00.000Z', runId: runId(RUN) });
  assert.equal((await w.flow.stop({ runId: runId(RUN), actions: ['A4'] })).kind, 'handedOff');
  assert.match(w.modals[0]?.message ?? '', /Stop run 20000101T000000Z-1/);
  assert.deepEqual(w.runner.argvs(), [ROOT_CHECK, stop]);
  assert.equal(w.journal.entries().length, 1, 'still the one entry');
  assert.match(w.notices.at(-1)?.sentence ?? '', /^Stopping run/);
});

test('Run full check now: no modal (it measures, it does not act), persisted before the call as a full check, then its run', async () => {
  const full = `${ROOT} collect --detach --json`;
  let before: unknown;
  const w = world({ [full]: () => { before = new CleanupJournal(w.store).entries().map((e) => [e.kind, e.op, e.actions]); return exited(0, JSON.stringify({ schemaVersion: 1, result: 'accepted', kind: 'collect', runId: RUN, unit: `wsl-care-act@${RUN}.service` })); } });
  assert.equal((await w.flow.fullCheck()).kind, 'handedOff');
  assert.deepEqual(before, [['unresolved', 'fullCheck', ['collect']]]);
  assert.equal(w.modals.length, 0);
  assert.equal(w.journal.entries()[0]?.kind, 'run');
});

test('a full check or a stop before any status answered says so and starts nothing', async () => {
  const w = world({}, () => undefined);
  assert.equal((await w.flow.fullCheck()).kind, 'noStatus');
  assert.deepEqual(w.runner.argvs(), []);
});

test('E6.S3 review A3: a preview that does not describe every asked id is NOT confirmed — told, nothing written', async () => {
  const preview = `${ROOT} act A4,A5 --preview --json`;
  const w = world({ [preview]: exited(0, previewText()) });
  const outcome = await w.flow.clean(['A5', 'A4'], true);
  assert.equal(outcome.kind, 'incomplete');
  assert.equal(w.modals.length, 0, 'no modal over a preview that does not describe A5');
  assert.equal(w.runner.argvs().filter((a) => a.includes('--confirm')).length, 0);
  assert.equal(w.store.writes, 0);
  assert.match(w.notices.at(-1)?.sentence ?? '', /did not describe A5/);
});
