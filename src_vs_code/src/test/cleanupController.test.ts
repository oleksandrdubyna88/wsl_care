import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

import { DEFAULT_NUMBERS, type Numbers } from '../settings/numbers';

import { MIN_DAEMON_FOR_ACTIONS } from '../client/handshake';
import type { Failure, VerbOutcome } from '../client/outcome';
import type { ProcessResult } from '../process/runner';
import { CleanupController, FOLLOW, type CleanupClient } from '../root/cleanupController';
import type { HeldPreview } from '../root/rootOutcome';
import { answered, failed, headBody, setAt, type Body } from './support/outcomes';
import { GOLDEN_ROOT } from './support/paths';
import { exited, recordingRunner, type Scripted } from './support/recordingRunner';

/**
 * The host-side cleanup controller (E6.S2) — the API E6.S3's buttons will call, and the ONLY importer of `rootCall.ts`.
 * Driven here against a fake client (status answers read through the client's own `parseAnswer`) and a recording runner
 * that starts nothing, so every ROOT call is asserted by its exact argv and its stdin, and a refused op is asserted to have
 * started NOTHING. The real client + runner + strict fake drive it in `scenarios/rootFlows.test.ts`.
 */

const TARGET = { wsl: 'C:\\Windows\\System32\\wsl.exe', distro: 'Ubuntu' } as const;
const ROOT = ['-d', 'Ubuntu', '-u', 'root', '--cd', '/', '--exec', '/opt/wsl-care/bin/wsl-care'];
const RUN_ID = '20000101T000000Z-1';

function argv(...tail: string[]): string {
  return [...ROOT, ...tail].join(' ');
}

const ROOT_CHECK = argv('--version');
const PREVIEW_A4 = argv('act', 'A4', '--preview', '--json');
const CONFIRM_A4 = argv('act', 'A4', '--confirm', '--manual', '--detach', '--only', '-', '--json');

function goldenText(name: string): string {
  return fs.readFileSync(path.join(GOLDEN_ROOT, 'head', name), 'utf8');
}

function previewBody(): Body {
  return JSON.parse(goldenText('act-a4-preview.json')) as Body;
}

function a4Of(body: Body): Body {
  return ((body.actions as Body[]).find((a) => a.id === 'A4') as Body);
}

function handOff(result: string, kind = 'act'): string {
  return JSON.stringify({ schemaVersion: 1, result, kind, runId: RUN_ID, unit: `wsl-care-act@${RUN_ID}.service`, productVersion: '0.1.0' });
}

interface World {
  readonly controller: CleanupController;
  readonly runner: ReturnType<typeof recordingRunner>;
  readonly statusCalls: () => number;
  readonly clock: { now: number };
}

interface WorldInput {
  readonly script?: Readonly<Record<string, Scripted>>;
  readonly status?: Body | (() => VerbOutcome);
  readonly numbers?: Numbers;
  readonly target?: typeof TARGET | Failure;
}

function world(input: WorldInput = {}): World {
  const runner = recordingRunner({ [ROOT_CHECK]: exited(0, '0.1.0\n'), ...input.script });
  let statusCalls = 0;
  const statusOf = input.status;
  const client: CleanupClient = {
    rootTarget: () => Promise.resolve(input.target ?? TARGET),
    run: () => {
      statusCalls += 1;
      return Promise.resolve(typeof statusOf === 'function' ? statusOf() : answered('status', statusOf ?? headBody('status')));
    },
  };
  const clock = { now: 1_000_000 };
  const controller = new CleanupController({ client, runner: runner.runner, now: () => clock.now, sleep: (ms) => { clock.now += ms; return Promise.resolve(); }, numbers: () => input.numbers ?? DEFAULT_NUMBERS });

  return { controller, runner, statusCalls: () => statusCalls, clock };
}

async function heldA4(w: World): Promise<HeldPreview> {
  const outcome = await w.controller.preview(['A4']);
  assert.equal(outcome.kind, 'previewed', JSON.stringify(outcome).slice(0, 400));
  assert.ok(outcome.kind === 'previewed');
  return outcome.preview;
}

// ---- preview: the ids, the gate, the root check, the held list ----

test('a preview of A4 checks root once, then runs exactly act A4 --preview --json — and holds every shown name', async () => {
  const w = world({ script: { [PREVIEW_A4]: exited(0, goldenText('act-a4-preview.json')) } });
  const preview = await heldA4(w);
  assert.deepEqual(w.runner.argvs(), [ROOT_CHECK, PREVIEW_A4]);
  assert.deepEqual(preview.ids, ['A4']);
  assert.equal(preview.distro, 'Ubuntu');
  assert.equal(preview.a4?.names.length, 387, 'every selected name (plan §15j B1), not the 20 items of the preview');
  assert.deepEqual(preview.a4?.names, a4Of(previewBody()).shown);
  assert.equal(preview.a4?.count, 387);
  assert.equal(preview.a4?.truncated, false);
});

test('ids are the compiled registry ∩ status.actions: an id the daemon does not report is refused before any root call', async () => {
  // Since daemon E9.S4 the head golden reports A13: the gate is shown with an OLDER daemon's status that does not.
  const head = headBody('status');
  const w = world({ status: { ...head, actions: (head.actions as string[]).filter((id) => id !== 'A13') } });
  const outcome = await w.controller.preview(['A4', 'A13']);
  assert.equal(outcome.kind, 'idsRefused');
  assert.ok(outcome.kind === 'idsRefused');
  assert.deepEqual(outcome.refused, ['A13']);
  assert.deepEqual(w.runner.argvs(), []);
});

test('E7 (#17): A18 in status.actions is tolerated but never acted on by the E6 controller — its confirm needs --process, the button of E7.S4: refused before any root call, and A4 beside it still previews', async () => {
  assert.ok((headBody('status').actions as string[]).includes('A18'), 'the head golden reports A18 (daemon #17)');
  const w = world();
  const outcome = await w.controller.preview(['A18']);
  assert.ok(outcome.kind === 'idsRefused', JSON.stringify(outcome));
  assert.deepEqual(outcome.refused, ['A18']);
  assert.ok(!(outcome.allowed as readonly string[]).includes('A18'), 'not in what the gate allows');
  assert.deepEqual(w.runner.argvs(), []);
});

test('an id outside the compiled registry, or none at all, is refused before any root call', async () => {
  for (const ids of [['A99'], ['--timer'], ['A4,A5'], []]) {
    const w = world();
    const outcome = await w.controller.preview(ids);
    assert.equal(outcome.kind, 'idsRefused', JSON.stringify(ids));
    assert.deepEqual(w.runner.argvs(), [], JSON.stringify(ids));
  }
});

test('the ids go in the registry\'s order, each once, whatever order they were asked in', async () => {
  const preview = argv('act', 'A4,A5,A10', '--preview', '--json');
  const w = world({ script: { [preview]: exited(0, goldenText('act-a4-preview.json')) } });
  await w.controller.preview(['A10', 'A5', 'A4', 'A5']);
  assert.deepEqual(w.runner.argvs(), [ROOT_CHECK, preview]);
});

test('capabilities are the AUTHORITY: a daemon without them shows "Update daemon" naming the minimum, and starts NO root call', async () => {
  const status = setAt(headBody('status'), 'capabilities', ['running.block', 'runs.show']);
  setAt(status, 'productVersion', '0.1.0');
  const w = world({ status });
  const outcome = await w.controller.preview(['A4']);
  assert.deepEqual(outcome, { kind: 'actionsUnavailable', version: '0.1.0', minimum: MIN_DAEMON_FOR_ACTIONS, missing: ['act.shownList'] });
  assert.deepEqual(w.runner.argvs(), []);
});

test('a status with no capabilities at all (a daemon before E6.S0) is "Update daemon" with every capability missing', async () => {
  const status = headBody('status');
  delete status.capabilities;
  delete status.actions;
  const w = world({ status });
  const outcome = await w.controller.runFullCheck();
  assert.equal(outcome.kind, 'actionsUnavailable');
  assert.ok(outcome.kind === 'actionsUnavailable');
  assert.deepEqual(outcome.missing, ['running.block', 'runs.show', 'act.detach']);
  assert.deepEqual(w.runner.argvs(), []);
});

test('the version only supplies the message: an unstamped daemon that ADVERTISES the capabilities may act', async () => {
  const status = setAt(headBody('status'), 'productVersion', 'unknown');
  const w = world({ status, script: { [PREVIEW_A4]: exited(0, goldenText('act-a4-preview.json')) } });
  assert.equal((await w.controller.preview(['A4'])).kind, 'previewed');
});

test('a status that did not answer is the answer: no gate, no root call', async () => {
  const w = world({ status: () => failed('status', { kind: 'notInstalled', distro: 'Ubuntu' }) });
  assert.deepEqual(await w.controller.preview(['A4']), { kind: 'notInstalled', distro: 'Ubuntu' });
  assert.deepEqual(w.runner.argvs(), []);
});

test('a stopped distribution is never started by a root call: the client\'s answer, and nothing ran', async () => {
  const w = world({ target: { kind: 'stopped', distro: 'Ubuntu' } });
  assert.deepEqual(await w.controller.preview(['A4']), { kind: 'stopped', distro: 'Ubuntu' });
  assert.equal(w.statusCalls(), 0);
  assert.deepEqual(w.runner.argvs(), []);
});

// ---- the root check (plan §15j m4) ----

test('the root check is cached per session: two previews ask --version as root ONCE', async () => {
  const w = world({ script: { [PREVIEW_A4]: exited(0, goldenText('act-a4-preview.json')) } });
  await heldA4(w);
  await heldA4(w);
  assert.deepEqual(w.runner.argvs(), [ROOT_CHECK, PREVIEW_A4, PREVIEW_A4]);
});

test('a refused root check greys the actions ("needs root"), runs no act — and is asked again next time, not cached', async () => {
  const w = world({ script: { [ROOT_CHECK]: exited(1, '', 'su: authentication failure\n') } });
  const first = await w.controller.preview(['A4']);
  assert.equal(first.kind, 'rootRefused');
  assert.equal((await w.controller.rootCheck()).kind, 'rootRefused');
  assert.deepEqual(w.runner.argvs(), [ROOT_CHECK, ROOT_CHECK]);
});

test('rootCheck() answers the daemon version root sees', async () => {
  const w = world();
  const outcome = await w.controller.rootCheck();
  assert.deepEqual(outcome, { kind: 'rootOk', distro: 'Ubuntu', version: { kind: 'release', text: '0.1.0', parts: [0, 1, 0] } });
});

// ---- the shown list is the held preview's, validated ----

test('a confirm pipes EXACTLY the held preview\'s names on stdin — all 387 — and answers accepted with the run id', async () => {
  const w = world({ script: { [PREVIEW_A4]: exited(0, goldenText('act-a4-preview.json')), [CONFIRM_A4]: exited(0, handOff('accepted')) } });
  const preview = await heldA4(w);
  const outcome = await w.controller.confirm(preview);
  assert.deepEqual(outcome, { kind: 'accepted', runId: RUN_ID, unit: `wsl-care-act@${RUN_ID}.service`, productVersion: '0.1.0' });
  const confirm = w.runner.requests.at(-1);
  assert.equal(confirm?.args.join(' '), CONFIRM_A4);
  assert.equal(confirm?.stdin?.toString('utf8'), `${(a4Of(previewBody()).shown as string[]).join('\n')}\n`);
});

test('a confirm of a preview this controller did not issue — a copy with the same shape — starts nothing', async () => {
  const w = world({ script: { [PREVIEW_A4]: exited(0, goldenText('act-a4-preview.json')) } });
  const real = await heldA4(w);
  const forged: HeldPreview = { ...real, a4: { names: [], count: 0, truncated: false, cap: 10_000 } };
  assert.deepEqual(await w.controller.confirm(forged), { kind: 'previewNotHeld' });
  assert.deepEqual(w.runner.argvs(), [ROOT_CHECK, PREVIEW_A4]);
});

test('a preview whose shown list holds a name that is not 64 hex digits is refused — never a partial list', async () => {
  const body = previewBody();
  (a4Of(body).shown as string[])[5] = 'ab'.repeat(31) + '-u';
  const w = world({ script: { [PREVIEW_A4]: exited(0, JSON.stringify(body)) } });
  const outcome = await w.controller.preview(['A4']);
  assert.equal(outcome.kind, 'shownListInvalid');
});

test('a preview whose shown list does not match its count is refused (shown.length == min(count, 10 000), plan §15k #11)', async () => {
  const body = previewBody();
  setAt(a4Of(body), 'preview.count', 388);
  const w = world({ script: { [PREVIEW_A4]: exited(0, JSON.stringify(body)) } });
  assert.equal((await w.controller.preview(['A4'])).kind, 'shownListInvalid');
});

test('a preview past the cap is honoured: count 12 000, 10 000 shown, shownTruncated — held, and a confirm pipes exactly those 650 000 bytes', async () => {
  const body = previewBody();
  const shown = Array.from({ length: 10_000 }, (_, i) => `${i.toString(16).padStart(8, '0')}${'ef'.repeat(28)}`);
  setAt(a4Of(body), 'shown', shown);
  setAt(a4Of(body), 'shownTruncated', true);
  setAt(a4Of(body), 'preview.count', 12_000);
  const w = world({ script: { [PREVIEW_A4]: exited(0, JSON.stringify(body)), [CONFIRM_A4]: exited(0, handOff('accepted')) } });
  const preview = await heldA4(w);
  assert.deepEqual([preview.a4?.names.length, preview.a4?.count, preview.a4?.truncated], [10_000, 12_000, true]);
  assert.equal((await w.controller.confirm(preview)).kind, 'accepted');
  assert.equal(w.runner.requests.at(-1)?.stdin?.length, 650_000, 'exactly the 10 000 shown names are piped, 65 bytes a line');
});

test('an A4 preview that carries no shown list cannot be confirmed: refused, nothing started', async () => {
  const body = previewBody();
  delete a4Of(body).shown;
  const w = world({ script: { [PREVIEW_A4]: exited(0, JSON.stringify(body)) } });
  const preview = await heldA4(w);
  assert.equal(preview.a4, undefined);
  assert.equal((await w.controller.confirm(preview)).kind, 'shownListInvalid');
  assert.deepEqual(w.runner.argvs(), [ROOT_CHECK, PREVIEW_A4]);
});

test('a confirm without A4 sends no stdin and no --only', async () => {
  const preview = argv('act', 'A10', '--preview', '--json');
  const confirm = argv('act', 'A10', '--confirm', '--manual', '--detach', '--json');
  const body = previewBody();
  setAt(body, 'actions', [{ id: 'A10', summary: 'x', status: 'previewed', reason: '', preview: { available: true, count: 1, bytes: 0 } }]);
  const w = world({ script: { [preview]: exited(0, JSON.stringify(body)), [confirm]: exited(0, handOff('accepted')) } });
  const held = await w.controller.preview(['A10']);
  assert.ok(held.kind === 'previewed');
  assert.equal((await w.controller.confirm(held.preview)).kind, 'accepted');
  assert.equal(w.runner.requests.at(-1)?.stdin, undefined);
});

test('a confirm re-checks the gate: capabilities withdrawn since the preview → "Update daemon", nothing started', async () => {
  let statusBody = headBody('status');
  const w = world({ status: () => answered('status', statusBody), script: { [PREVIEW_A4]: exited(0, goldenText('act-a4-preview.json')) } });
  const preview = await heldA4(w);
  statusBody = setAt(headBody('status'), 'capabilities', ['act.shownList', 'running.block', 'runs.show']);
  assert.equal((await w.controller.confirm(preview)).kind, 'actionsUnavailable');
  assert.deepEqual(w.runner.argvs(), [ROOT_CHECK, PREVIEW_A4]);
});

test('a confirm in another distribution than the preview\'s is refused', async () => {
  let target: typeof TARGET | { wsl: string; distro: string } = TARGET;
  const runner = recordingRunner({ [ROOT_CHECK]: exited(0, '0.1.0\n'), [PREVIEW_A4]: exited(0, goldenText('act-a4-preview.json')) });
  const controller = new CleanupController({
    client: { rootTarget: () => Promise.resolve(target), run: () => Promise.resolve(answered('status', headBody('status'))) },
    runner: runner.runner, now: () => 0, sleep: () => Promise.resolve(),
  });
  const held = await controller.preview(['A4']);
  assert.ok(held.kind === 'previewed');
  target = { wsl: TARGET.wsl, distro: 'Ubuntu-24.04' };
  assert.deepEqual(await controller.confirm(held.preview), { kind: 'distroChanged', previewed: 'Ubuntu', now: 'Ubuntu-24.04' });
});

// ---- one root operation in flight per distribution (plan §15j m9) ----

test('a second root operation while one is in flight in the same distribution is refused at once, and starts nothing', async () => {
  let release: (r: ProcessResult) => void = () => undefined;
  const w = world({ script: { [PREVIEW_A4]: () => new Promise<ProcessResult>((resolve) => { release = resolve; }) } });
  const first = w.controller.preview(['A4']);
  await new Promise((r) => setImmediate(r));
  await new Promise((r) => setImmediate(r));
  const second = await w.controller.runFullCheck();
  assert.deepEqual(second, { kind: 'rootBusy', distro: 'Ubuntu' });
  release(exited(0, goldenText('act-a4-preview.json')));
  assert.equal((await first).kind, 'previewed');
  assert.deepEqual(w.runner.argvs(), [ROOT_CHECK, PREVIEW_A4]);
  assert.equal((await w.controller.rootCheck()).kind, 'rootOk', 'the slot is free again once the first answered');
});

// ---- a detach whose outcome is unknown is followed through status.running (plan §15k #3) ----

const FULL_CHECK = argv('collect', '--detach', '--json');
const CONFIRM_A10 = argv('act', 'A10', '--confirm', '--manual', '--detach', '--json');
const PREVIEW_A10 = argv('act', 'A10', '--preview', '--json');
const TIMED_OUT: ProcessResult = { kind: 'timedOut', timeoutMs: 90_000, stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) };

function running(state: string, runId = RUN_ID, trigger = 'manual', actions: readonly string[] = ['collect']): Body {
  return setAt(headBody('status'), 'running', { state, runId, actions, trigger, reason: `run ${runId} is ${state}` });
}

function a10PreviewBody(): string {
  const body = previewBody();
  setAt(body, 'actions', [{ id: 'A10', summary: 'x', status: 'previewed', reason: '', preview: { available: true, count: 1, bytes: 0 } }]);
  return JSON.stringify(body);
}

async function heldA10(w: World): Promise<HeldPreview> {
  const held = await w.controller.preview(['A10']);
  assert.ok(held.kind === 'previewed', JSON.stringify(held).slice(0, 300));
  return held.preview;
}

// ---- review round M3: a run id the daemon returned is handed back AT ONCE — E6.S3's poll follows it ----

test('M3: result "unknown" WITH a run id is handed back at once as outcome unknown with that run id — no follow, no wait', async () => {
  const w = world({ script: { [FULL_CHECK]: exited(0, handOff('unknown', 'collect')) } });
  const started = w.clock.now;
  const outcome = await w.controller.runFullCheck();
  assert.equal(outcome.kind, 'outcomeUnknown', JSON.stringify(outcome));
  assert.ok(outcome.kind === 'outcomeUnknown');
  assert.equal(outcome.runId, RUN_ID);
  assert.equal(outcome.followed, undefined, 'nothing was followed: the caller follows the run id');
  assert.equal(w.clock.now, started, 'no wait');
  assert.equal(w.statusCalls(), 1, 'the gate\'s status only');
});

test('M3: a result this extension does not know reads "unknown (<value>)", and with its run id is handed back at once', async () => {
  const w = world({ script: { [FULL_CHECK]: exited(0, handOff('deferred', 'collect')) } });
  const outcome = await w.controller.runFullCheck();
  assert.ok(outcome.kind === 'outcomeUnknown', JSON.stringify(outcome));
  assert.equal(outcome.runId, RUN_ID);
  assert.match(outcome.reason, /unknown \(deferred\)/);
  assert.equal(w.statusCalls(), 1);
});

// ---- review round M2: with NO run id, only a run that is provably this hand-off is adopted ----

test('M2: a detach that TIMED OUT adopts a queued run only when it is the panel\'s (trigger manual) with exactly the asked actions', async () => {
  const statuses = [headBody('status'), running('none'), running('queued', RUN_ID, 'manual', ['collect'])];
  const w = world({ status: () => answered('status', statuses.shift() ?? running('live')), script: { [FULL_CHECK]: TIMED_OUT } });
  const started = w.clock.now;
  const outcome = await w.controller.runFullCheck();
  assert.equal(outcome.kind, 'acceptedObserved', JSON.stringify(outcome));
  assert.ok(outcome.kind === 'acceptedObserved');
  assert.equal(outcome.runId, RUN_ID);
  assert.equal(w.clock.now - started, 2 * FOLLOW.intervalMs, 'polled every 4 s');
});

test('M2: with no run id, the TIMER\'s run in flight is not adopted — reported unknown, naming the other run', async () => {
  const w = world({ status: () => answered('status', running('live', '20000101T000000Z-99', 'timer', ['collect'])), script: { [FULL_CHECK]: TIMED_OUT } });
  const outcome = await w.controller.runFullCheck();
  assert.equal(outcome.kind, 'outcomeUnknown', JSON.stringify(outcome));
  assert.ok(outcome.kind === 'outcomeUnknown');
  assert.equal(outcome.runId, undefined);
  assert.equal(outcome.otherRun?.runId, '20000101T000000Z-99');
});

test('M2: with no run id, a manual run of OTHER actions is not adopted — the confirm asked A10, the run holds A10 and A4', async () => {
  const w = world({
    status: () => answered('status', running('queued', RUN_ID, 'manual', ['A10', 'A4'])),
    script: { [PREVIEW_A10]: exited(0, a10PreviewBody()), [CONFIRM_A10]: TIMED_OUT },
  });
  const outcome = await w.controller.confirm(await heldA10(w));
  assert.equal(outcome.kind, 'outcomeUnknown', JSON.stringify(outcome));
});

test('M2: with no run id, a manual run of exactly the confirmed actions is adopted', async () => {
  const w = world({
    status: () => answered('status', running('live', RUN_ID, 'manual', ['A10'])),
    script: { [PREVIEW_A10]: exited(0, a10PreviewBody()), [CONFIRM_A10]: TIMED_OUT },
  });
  assert.equal((await w.controller.confirm(await heldA10(w))).kind, 'acceptedObserved');
});

test('§15q: the unknown-detach follow reads the settings — 10 s polls for 30 s are three polls', async () => {
  const w = world({ script: { [FULL_CHECK]: TIMED_OUT }, numbers: { ...DEFAULT_NUMBERS, followPollSeconds: 10, unknownDetachFollowSeconds: 30 } });
  const outcome = await w.controller.runFullCheck();
  assert.ok(outcome.kind === 'outcomeUnknown', JSON.stringify(outcome));
  assert.deepEqual(outcome.followed, { polls: 3, answered: 3 });
});

test('a detach that TIMED OUT and is never seen: outcome unknown after the bound, the polls counted, never a failure', async () => {
  const w = world({ script: { [FULL_CHECK]: TIMED_OUT } });
  const outcome = await w.controller.runFullCheck();
  assert.equal(outcome.kind, 'outcomeUnknown');
  assert.ok(outcome.kind === 'outcomeUnknown');
  assert.equal(outcome.runId, undefined);
  assert.deepEqual(outcome.followed, { polls: FOLLOW.boundMs / FOLLOW.intervalMs, answered: FOLLOW.boundMs / FOLLOW.intervalMs });
  assert.equal(w.statusCalls(), 1 + FOLLOW.boundMs / FOLLOW.intervalMs, 'the gate\'s status, then one per interval until the bound');
});

test('"accepted" with NO run id is not certain: it is followed like an unknown outcome, never reported accepted', async () => {
  const noRunId = JSON.stringify({ schemaVersion: 1, result: 'accepted', kind: 'collect', unit: 'wsl-care.service', productVersion: '0.1.0' });
  const w = world({ script: { [FULL_CHECK]: exited(0, noRunId) } });
  const outcome = await w.controller.runFullCheck();
  assert.equal(outcome.kind, 'outcomeUnknown');
  assert.ok(outcome.kind === 'outcomeUnknown');
  assert.match(outcome.reason, /accepted with no run id/);
  assert.notEqual(outcome.followed, undefined, 'no run id: followed');
});

// ---- review round L3: the follow reads only THIS distribution's status, and says when status never answered ----

test('L3: a follow whose every status fails says so: polls counted, none answered — and the reason names it', async () => {
  let calls = 0;
  const w = world({ status: () => (calls++ === 0 ? answered('status', headBody('status')) : failed('status', { kind: 'timedOut', timeoutMs: 20_000 })), script: { [FULL_CHECK]: TIMED_OUT } });
  const outcome = await w.controller.runFullCheck();
  assert.ok(outcome.kind === 'outcomeUnknown', JSON.stringify(outcome));
  assert.deepEqual(outcome.followed, { polls: 15, answered: 0 });
  assert.match(outcome.reason, /status answered none of 15 polls/);
});

test('L3: a status of ANOTHER distribution during the follow is not this one\'s — its running run is not adopted', async () => {
  let calls = 0;
  const w = world({
    status: () => (calls++ === 0 ? answered('status', headBody('status')) : answered('status', running('queued', RUN_ID, 'manual', ['collect']), 'Debian')),
    script: { [FULL_CHECK]: TIMED_OUT },
  });
  const outcome = await w.controller.runFullCheck();
  assert.ok(outcome.kind === 'outcomeUnknown', JSON.stringify(outcome));
  assert.deepEqual(outcome.followed, { polls: 15, answered: 0 });
});

test('a detach that could not even start wsl.exe is a certain failure — nothing to follow', async () => {
  const w = world({ script: { [FULL_CHECK]: { kind: 'failedToStart', reason: 'ENOENT' } } });
  const outcome = await w.controller.runFullCheck();
  assert.equal(outcome.kind, 'wslFailed');
  assert.equal(w.statusCalls(), 1);
});

// ---- review round M1: only the exits the daemon guarantees "nothing written" are certain for a detach ----

const CERTAIN: readonly [number, string][] = [
  [1, 'unknownFailure'], [2, 'refused'], [69, 'detachUnavailable'], [71, 'detachStartFailed'], [73, 'queueFull'], [75, 'busy'], [76, 'wedged'], [77, 'needsRoot'], [78, 'observeOnly'], [79, 'stateUnreadable'], [80, 'requestGone'],
];

for (const [code, kind] of CERTAIN) {
  test(`M1: a full check answered exit ${code} is a CERTAIN ${kind} (nothing written), with the daemon's own wsl-care: line`, async () => {
    const w = world({ script: { [FULL_CHECK]: exited(code, '', `\u001b[31m[12:00:00 ERR]\u001b[0m noise\nwsl-care: refused for a reason\n`) } });
    const outcome = await w.controller.runFullCheck();
    assert.equal(outcome.kind, kind);
    if ('messages' in outcome) {
      assert.deepEqual(outcome.messages, ['wsl-care: refused for a reason']);
    }
  });
}

test('M1: wsl.exe refusing on its own account (-1) is certain for a detach', async () => {
  const w = world({ script: { [FULL_CHECK]: { kind: 'exited', code: -1, stdout: Buffer.from('There is no distribution with the supplied name.\r\n', 'utf16le'), stderr: Buffer.alloc(0) } } });
  assert.equal((await w.controller.runFullCheck()).kind, 'wslFailed');
});

for (const code of [70, 130, 137, 143, 3, 4]) {
  test(`M1: a detach that exited ${code} may have written its request — outcome UNKNOWN, followed, never a failure`, async () => {
    const w = world({ script: { [FULL_CHECK]: exited(code, '', 'wsl-care: cut off\n') } });
    const outcome = await w.controller.runFullCheck();
    assert.equal(outcome.kind, 'outcomeUnknown', JSON.stringify(outcome));
    assert.ok(outcome.kind === 'outcomeUnknown');
    assert.match(outcome.reason, new RegExp(`exited ${code}`));
    assert.notEqual(outcome.followed, undefined);
  });
}

test('M1: a STOP keeps reading its exits as they are — 130 is interrupted', async () => {
  const stop = argv('act', '--stop', RUN_ID, '--json');
  const w = world({ script: { [stop]: exited(130, '', '') } });
  assert.equal((await w.controller.stop(RUN_ID)).kind, 'interrupted');
});

test('exit 75 (busy) carries the running block the daemon reports, read from status right after', async () => {
  let calls = 0;
  const w = world({ status: () => answered('status', calls++ === 0 ? headBody('status') : running('live')), script: { [FULL_CHECK]: exited(75, '', 'wsl-care: busy: run x is acting\n') } });
  const outcome = await w.controller.runFullCheck();
  assert.ok(outcome.kind === 'busy');
  assert.equal(outcome.running?.runId, RUN_ID);
});

test('exit 76 (wedged) carries the running block too', async () => {
  let calls = 0;
  const w = world({ status: () => answered('status', calls++ === 0 ? headBody('status') : running('wedged')), script: { [FULL_CHECK]: exited(76, '', 'wsl-care: wedged\n') } });
  const outcome = await w.controller.runFullCheck();
  assert.ok(outcome.kind === 'wedged', JSON.stringify(outcome));
  assert.equal(outcome.running?.state.kind === 'known' ? outcome.running.state.value : '', 'wedged');
});

test('exit 2 on a confirm that piped a shown list: refused, nothing started, a retry offered (the daemon\'s 10 s stdin ceiling, §15k #19)', async () => {
  const w = world({ script: { [PREVIEW_A4]: exited(0, goldenText('act-a4-preview.json')), [CONFIRM_A4]: exited(2, '', 'wsl-care: act: the shown list on stdin had no end within 10 s (the list must be followed by the end of input); nothing was done\n') } });
  const outcome = await w.controller.confirm(await heldA4(w));
  assert.deepEqual(outcome, { kind: 'shownListRefused', messages: ['wsl-care: act: the shown list on stdin had no end within 10 s (the list must be followed by the end of input); nothing was done'] });
});

// ---- review round M4: a held preview is confirmed ONCE ----

test('M4: a preview whose confirm went out cannot be confirmed again', async () => {
  const w = world({ script: { [PREVIEW_A4]: exited(0, goldenText('act-a4-preview.json')), [CONFIRM_A4]: exited(0, handOff('accepted')) } });
  const preview = await heldA4(w);
  assert.equal((await w.controller.confirm(preview)).kind, 'accepted');
  assert.deepEqual(await w.controller.confirm(preview), { kind: 'previewNotHeld' });
  assert.equal(w.runner.argvs().filter((a) => a === CONFIRM_A4).length, 1);
});

test('M4: a confirm whose outcome is unknown still consumed its preview (the request may have been written)', async () => {
  const w = world({ script: { [PREVIEW_A4]: exited(0, goldenText('act-a4-preview.json')), [CONFIRM_A4]: exited(0, handOff('unknown')) } });
  const preview = await heldA4(w);
  assert.equal((await w.controller.confirm(preview)).kind, 'outcomeUnknown');
  assert.deepEqual(await w.controller.confirm(preview), { kind: 'previewNotHeld' });
});

test('M4: refusals where nothing started keep the preview — a gate refusal, a piped-list refusal, a launcher that never started', async () => {
  let statusBody = setAt(headBody('status'), 'capabilities', ['act.shownList', 'running.block', 'runs.show']);
  let confirmAnswer: ProcessResult = exited(2, '', 'wsl-care: act: line 3 of the shown list is not a name; nothing was done\n');
  const w = world({ status: () => answered('status', statusBody), script: { [PREVIEW_A4]: exited(0, goldenText('act-a4-preview.json')), [CONFIRM_A4]: () => confirmAnswer } });
  const preview = await heldA4(w);
  assert.equal((await w.controller.confirm(preview)).kind, 'actionsUnavailable', 'refused at the gate');
  statusBody = headBody('status');
  assert.equal((await w.controller.confirm(preview)).kind, 'shownListRefused');
  confirmAnswer = { kind: 'failedToStart', reason: 'ENOENT' };
  assert.equal((await w.controller.confirm(preview)).kind, 'wslFailed');
  confirmAnswer = exited(0, handOff('accepted'));
  assert.equal((await w.controller.confirm(preview)).kind, 'accepted', 'still held after three refusals');
});

// ---- review round L2: only an EXIT of the root check is "needs root" ----

test('L2: a root check that timed out is its own failure — not "needs root" — and is not cached', async () => {
  let answer: ProcessResult = { kind: 'timedOut', timeoutMs: 20_000, stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) };
  const w = world({ script: { [ROOT_CHECK]: () => answer } });
  assert.deepEqual(await w.controller.rootCheck(), { kind: 'timedOut', timeoutMs: 20_000 });
  answer = { kind: 'failedToStart', reason: 'ENOENT' };
  assert.equal((await w.controller.rootCheck()).kind, 'wslFailed');
  answer = { kind: 'exited', code: -1, stdout: Buffer.from('There is no distribution with the supplied name.\r\n', 'utf16le'), stderr: Buffer.alloc(0) };
  assert.equal((await w.controller.rootCheck()).kind, 'wslFailed', 'wsl.exe refusing on its own account is not a root refusal');
  answer = exited(0, '0.1.0\n');
  assert.equal((await w.controller.rootCheck()).kind, 'rootOk', 'asked again: none of them was cached');
});

// ---- the in-flight slot is freed whatever happens ----

test('the in-flight slot is freed even when the runner REJECTS — the next op is not refused as busy', async () => {
  const w = world({ script: { [PREVIEW_A4]: () => Promise.reject(new Error('runner exploded')) } });
  await assert.rejects(w.controller.preview(['A4']), /runner exploded/);
  assert.equal((await w.controller.rootCheck()).kind, 'rootOk');
});

test('the in-flight slot is freed even when the status read REJECTS', async () => {
  let reject = true;
  const w = world({ status: () => { if (reject) { throw new Error('status exploded'); } return answered('status', headBody('status')); } });
  await assert.rejects(w.controller.runFullCheck(), /status exploded/);
  reject = false;
  assert.notEqual((await w.controller.runFullCheck()).kind, 'rootBusy');
});

// ---- stop and full check ----

test('stop: exactly act --stop <runId> --json, answered stopping', async () => {
  const stop = argv('act', '--stop', RUN_ID, '--json');
  const w = world({ script: { [stop]: exited(0, handOff('stopping')) } });
  assert.deepEqual(await w.controller.stop(RUN_ID), { kind: 'stopping', runId: RUN_ID, unit: `wsl-care-act@${RUN_ID}.service` });
  assert.deepEqual(w.runner.argvs(), [ROOT_CHECK, stop]);
});

test('stop with a run id the daemon would never write starts nothing', async () => {
  for (const bad of ['20000101T000000Z-01', '--timer', `${RUN_ID} --user x`, '']) {
    const w = world();
    assert.deepEqual(await w.controller.stop(bad), { kind: 'runIdRefused' });
    assert.deepEqual(w.runner.argvs(), []);
  }
});

test('stop needs act.stop: a daemon without it is "Update daemon"', async () => {
  const w = world({ status: setAt(headBody('status'), 'capabilities', ['running.block', 'runs.show', 'act.detach']) });
  const outcome = await w.controller.stop(RUN_ID);
  assert.equal(outcome.kind, 'actionsUnavailable');
  assert.deepEqual(w.runner.argvs(), []);
});

test('Run full check now: exactly collect --detach --json — never --timer — answered accepted', async () => {
  const w = world({ script: { [FULL_CHECK]: exited(0, handOff('accepted', 'collect')) } });
  assert.equal((await w.controller.runFullCheck()).kind, 'accepted');
  assert.deepEqual(w.runner.argvs(), [ROOT_CHECK, FULL_CHECK]);
});

test('#17: a root call is sized under the limits of the fresh status — a daemon whose drain is 10 s gets a detach ceiling above its 1 215 s', async () => {
  const status = { ...headBody('status'), limits: { ...(headBody('status').limits as Record<string, unknown>), drainGraceMilliseconds: 10_000 } };
  const w = world({ status, script: { [FULL_CHECK]: exited(0, handOff('accepted', 'collect')) } });
  await w.controller.runFullCheck();
  const detach = w.runner.requests.find((r) => r.args.includes('collect'));
  assert.equal(detach?.timeoutMs, (1215 + 10) * 1000);
});

// ---- #17: A4's shown-list cap is the daemon's published maxShownNames (never above the compiled MAX_SHOWN_VOLUMES) ----

/** The head A4 preview, cut the way a daemon whose maxShownNames is `cap` writes it: count 387, the first `cap` names, truncated. */
function cappedPreview(cap: number): string {
  const body = JSON.parse(goldenText('act-a4-preview.json')) as { actions: Record<string, unknown>[] };
  const a4 = body.actions.find((a) => a.id === 'A4') as { shown: string[]; shownTruncated?: boolean };
  a4.shown = a4.shown.slice(0, cap);
  a4.shownTruncated = true;
  return JSON.stringify(body);
}

function statusCapped(cap: number): Record<string, unknown> {
  const status = headBody('status');
  return { ...status, limits: { ...(status.limits as Record<string, unknown>), maxShownNames: cap } };
}

test('#17: a preview is held to the cap IN FORCE — 300 names of 387 with maxShownNames 300 is consistent; the same list under 10 000 is not', async () => {
  const capped = world({ status: statusCapped(300), script: { [PREVIEW_A4]: exited(0, cappedPreview(300)) } });
  const held = await capped.controller.preview(['A4']);
  assert.ok(held.kind === 'previewed', JSON.stringify(held).slice(0, 300));
  assert.deepEqual([held.preview.a4?.names.length, held.preview.a4?.count, held.preview.a4?.truncated, held.preview.a4?.cap], [300, 387, true, 300]);
  const uncapped = world({ script: { [PREVIEW_A4]: exited(0, cappedPreview(300)) } });
  assert.equal((await uncapped.controller.preview(['A4'])).kind, 'shownListInvalid', 'under the default cap 300 of 387 names is a cut list');
});

test('#17: the confirm checks the cap in force AT THE CONFIRM — a 387-name list held under 10 000 is refused when the daemon now takes 300', async () => {
  let cap = 10_000;
  const w = world({ status: () => answered('status', statusCapped(cap)), script: { [PREVIEW_A4]: exited(0, goldenText('act-a4-preview.json')), [CONFIRM_A4]: exited(0, handOff('accepted')) } });
  const preview = await heldA4(w);
  cap = 300;
  const outcome = await w.controller.confirm(preview);
  assert.equal(outcome.kind, 'shownListInvalid', JSON.stringify(outcome).slice(0, 300));
  assert.deepEqual(w.runner.argvs().filter((a) => a === CONFIRM_A4), [], 'nothing confirmed');
});
