import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

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
  const controller = new CleanupController({ client, runner: runner.runner, now: () => clock.now, sleep: (ms) => { clock.now += ms; return Promise.resolve(); } });

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
  const w = world();
  assert.ok(!(headBody('status').actions as string[]).includes('A13'), 'the head golden reports no A13 (a Windows-side action)');
  const outcome = await w.controller.preview(['A4', 'A13']);
  assert.equal(outcome.kind, 'idsRefused');
  assert.ok(outcome.kind === 'idsRefused');
  assert.deepEqual(outcome.refused, ['A13']);
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
  const forged: HeldPreview = { ...real, a4: { names: [], count: 0, truncated: false } };
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

test('a preview past the cap is honoured: count 12 000, 10 000 shown, shownTruncated — the held list says so', async () => {
  const body = previewBody();
  const shown = Array.from({ length: 10_000 }, (_, i) => `${i.toString(16).padStart(8, '0')}${'ef'.repeat(28)}`);
  setAt(a4Of(body), 'shown', shown);
  setAt(a4Of(body), 'shownTruncated', true);
  setAt(a4Of(body), 'preview.count', 12_000);
  const w = world({ script: { [PREVIEW_A4]: exited(0, JSON.stringify(body)) } });
  const preview = await heldA4(w);
  assert.deepEqual([preview.a4?.names.length, preview.a4?.count, preview.a4?.truncated], [10_000, 12_000, true]);
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

function running(state: string, runId = RUN_ID): Body {
  return setAt(headBody('status'), 'running', { state, runId, actions: ['collect'], reason: `run ${runId} is ${state}` });
}

test('result "unknown" is followed: the run seen queued under its run id → accepted (observed), polling every 4 s', async () => {
  const statuses = [headBody('status'), running('none'), running('queued')];
  const w = world({ status: () => answered('status', statuses.shift() ?? running('live')), script: { [FULL_CHECK]: exited(0, handOff('unknown', 'collect')) } });
  const started = w.clock.now;
  const outcome = await w.controller.runFullCheck();
  assert.equal(outcome.kind, 'acceptedObserved', JSON.stringify(outcome));
  assert.ok(outcome.kind === 'acceptedObserved');
  assert.equal(outcome.runId, RUN_ID);
  assert.equal(outcome.running.state.kind === 'known' ? outcome.running.state.value : '', 'queued');
  assert.equal(w.clock.now - started, 2 * FOLLOW.intervalMs);
});

test('a detach that TIMED OUT is outcome unknown, followed for the bound; nothing seen → "outcome unknown", never a failure', async () => {
  const w = world({ script: { [FULL_CHECK]: { kind: 'timedOut', timeoutMs: 90_000, stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) } } });
  const outcome = await w.controller.runFullCheck();
  assert.equal(outcome.kind, 'outcomeUnknown');
  assert.ok(outcome.kind === 'outcomeUnknown');
  assert.equal(outcome.runId, undefined);
  assert.equal(w.statusCalls(), 1 + FOLLOW.boundMs / FOLLOW.intervalMs, 'the gate\'s status, then one per interval until the bound');
});

test('a result this extension does not know reads "unknown (<value>)" and is followed like unknown', async () => {
  const w = world({ script: { [FULL_CHECK]: exited(0, handOff('deferred', 'collect')) } });
  const outcome = await w.controller.runFullCheck();
  assert.equal(outcome.kind, 'outcomeUnknown');
  assert.ok(outcome.kind === 'outcomeUnknown');
  assert.equal(outcome.runId, RUN_ID);
  assert.match(outcome.reason, /unknown \(deferred\)/);
});

test('a run seen under ANOTHER run id does not count as ours while our run id is known', async () => {
  const w = world({ status: () => answered('status', running('live', '20000101T000000Z-99')), script: { [FULL_CHECK]: exited(0, handOff('unknown', 'collect')) } });
  assert.equal((await w.controller.runFullCheck()).kind, 'outcomeUnknown');
});

test('a detach that could not even start wsl.exe is a certain failure — nothing to follow', async () => {
  const w = world({ script: { [FULL_CHECK]: { kind: 'failedToStart', reason: 'ENOENT' } } });
  const outcome = await w.controller.runFullCheck();
  assert.equal(outcome.kind, 'wslFailed');
  assert.equal(w.statusCalls(), 1);
});

// ---- the daemon's refusals, each its own kind ----

const REFUSALS: readonly [number, string][] = [
  [69, 'detachUnavailable'], [71, 'detachStartFailed'], [73, 'queueFull'], [77, 'needsRoot'], [78, 'observeOnly'], [79, 'stateUnreadable'], [80, 'requestGone'], [70, 'internalDefect'], [130, 'interrupted'],
];

for (const [code, kind] of REFUSALS) {
  test(`a full check answered exit ${code} reads as ${kind}, with the daemon's own wsl-care: line`, async () => {
    const w = world({ script: { [FULL_CHECK]: exited(code, '', `\u001b[31m[12:00:00 ERR]\u001b[0m noise\nwsl-care: refused for a reason\n`) } });
    const outcome = await w.controller.runFullCheck();
    assert.equal(outcome.kind, kind);
    if ('messages' in outcome) {
      assert.deepEqual(outcome.messages, ['wsl-care: refused for a reason']);
    }
  });
}

test('exit 75 (busy) carries the running block the daemon reports, read from status right after', async () => {
  let calls = 0;
  const w = world({ status: () => answered('status', calls++ === 0 ? headBody('status') : running('live')), script: { [FULL_CHECK]: exited(75, '', 'wsl-care: busy: run x is acting\n') } });
  const outcome = await w.controller.runFullCheck();
  assert.equal(outcome.kind, 'busy');
  assert.ok(outcome.kind === 'busy');
  assert.equal(outcome.running?.runId, RUN_ID);
});

test('exit 2 on a confirm that piped a shown list: refused, nothing started, a retry offered (the daemon\'s 10 s stdin ceiling, §15k #19)', async () => {
  const w = world({ script: { [PREVIEW_A4]: exited(0, goldenText('act-a4-preview.json')), [CONFIRM_A4]: exited(2, '', 'wsl-care: act: the shown list on stdin had no end within 10 s (the list must be followed by the end of input); nothing was done\n') } });
  const outcome = await w.controller.confirm(await heldA4(w));
  assert.deepEqual(outcome, { kind: 'shownListRefused', messages: ['wsl-care: act: the shown list on stdin had no end within 10 s (the list must be followed by the end of input); nothing was done'] });
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

test('"accepted" with NO run id is not certain: it is followed like an unknown outcome, never reported accepted', async () => {
  const noRunId = JSON.stringify({ schemaVersion: 1, result: 'accepted', kind: 'collect', unit: 'wsl-care.service', productVersion: '0.1.0' });
  const w = world({ script: { [FULL_CHECK]: exited(0, noRunId) } });
  const outcome = await w.controller.runFullCheck();
  assert.equal(outcome.kind, 'outcomeUnknown');
  assert.ok(outcome.kind === 'outcomeUnknown');
  assert.match(outcome.reason, /accepted with no run id/);
});

test('the daemon\'s capped golden (10 001 volumes, plan §15k #11): held as 10 000 names, truncated — and a confirm pipes exactly those 650 000 bytes', async () => {
  const capped = goldenText('act-a4-preview-capped.json');
  const w = world({ script: { [PREVIEW_A4]: exited(0, capped), [CONFIRM_A4]: exited(0, handOff('accepted')) } });
  const preview = await heldA4(w);
  assert.deepEqual([preview.a4?.names.length, preview.a4?.count, preview.a4?.truncated], [10_000, 10_001, true]);
  assert.equal((await w.controller.confirm(preview)).kind, 'accepted');
  assert.equal(w.runner.requests.at(-1)?.stdin?.length, 650_000);
});
