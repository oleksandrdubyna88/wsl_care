import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

import { isTerminal, parseRuns, parseRunShow, RUN_SHOW_STATES } from '../cleanup/runAnswers';
import { readEnum } from '../client/enumValue';
import type { ReadOutcome } from '../client/outcome';
import { RUN_READ_NAMES, RUN_READ_TIMEOUT_MS, runReadTail, type RunRead } from '../client/verbs';
import { WslCareClient } from '../client/WslCareClient';
import { TEST_ENV } from './support/fakeWorld';
import { GOLDEN_ROOT } from './support/paths';
import { daemonArgv, exited, exitedUtf16, LIST_QUIET, LIST_RUNNING, LIST_VERBOSE, recordingRunner, verboseTable, type Scripted } from './support/recordingRunner';

/**
 * The two run reads E6.S3 adds to the client (plan §15j M3 / M7, §15k #19): `runs show <runId> --json` and
 * `runs --from <instant> --to <instant> --json` — unprivileged (never `-u`), after the same three WSL questions as every
 * read-only verb, never to a stopped distribution, their values checked BEFORE anything starts — and their answers read
 * over the daemon's goldens.
 */

const RUN = '20000101T000000Z-1';
const FROM = '2026-10-05T10:00:00Z';
const TO = '2026-10-05T10:05:00Z';

function goldenText(name: string): string {
  return fs.readFileSync(path.join(GOLDEN_ROOT, 'head', name), 'utf8');
}

function wsl(running: boolean): Record<string, Scripted> {
  return {
    [LIST_QUIET]: exitedUtf16(0, 'Ubuntu\r\n'),
    [LIST_VERBOSE]: exitedUtf16(0, verboseTable([{ name: 'Ubuntu', running }], 'Ubuntu')),
    [LIST_RUNNING]: exitedUtf16(0, running ? 'Ubuntu\r\n' : ''),
  };
}

function client(script: Record<string, Scripted>): { c: WslCareClient; rec: ReturnType<typeof recordingRunner> } {
  const rec = recordingRunner(script);
  return { c: new WslCareClient({ runner: rec.runner, platform: 'win32', env: TEST_ENV, distroSetting: () => '' }), rec };
}

const SHOW = daemonArgv('Ubuntu', ['runs', 'show', RUN, '--json']);
const RUNS = daemonArgv('Ubuntu', ['runs', '--from', FROM, '--to', TO, '--json']);

function bodyOf(outcome: ReadOutcome): Record<string, unknown> {
  assert.equal(outcome.kind, 'read', JSON.stringify(outcome));
  assert.ok(outcome.kind === 'read');
  return outcome.body;
}

test('runs show: after the three WSL questions, exactly -d <distro> --cd / --exec <daemon> runs show <runId> --json — no -u, 20 s', async () => {
  const { c, rec } = client({ ...wsl(true), [SHOW]: exited(0, goldenText('runs-show-done.json')) });
  const outcome = await c.read({ read: 'runsShow', runId: RUN });
  assert.equal(bodyOf(outcome).state, 'done');
  assert.ok(outcome.kind === 'read' && outcome.distro === 'Ubuntu' && outcome.read === 'runsShow');
  assert.deepEqual(rec.argvs(), [LIST_QUIET, LIST_VERBOSE, LIST_RUNNING, SHOW]);
  assert.equal(rec.requests[3]?.timeoutMs, RUN_READ_TIMEOUT_MS.runsShow);
  assert.ok(rec.requests.every((r) => !r.args.includes('-u')), 'a run read is unprivileged');
});

test('runs --from --to: the instant window, exactly as built, 20 s', async () => {
  const { c, rec } = client({ ...wsl(true), [RUNS]: exited(0, goldenText('runs-local-day.json')) });
  const outcome = await c.read({ read: 'runs', from: FROM, to: TO });
  assert.equal(bodyOf(outcome).count, 3);
  assert.equal(rec.argvs().at(-1), RUNS);
  assert.equal(rec.requests.at(-1)?.timeoutMs, RUN_READ_TIMEOUT_MS.runs);
});

test('every run read\'s tail is built from its typed parts, and each has a stated ceiling', () => {
  const reads: { readonly [K in RunRead['read']]: RunRead } = { runsShow: { read: 'runsShow', runId: RUN }, runs: { read: 'runs', from: FROM, to: TO } };
  assert.deepEqual(RUN_READ_NAMES.map((name) => runReadTail(reads[name])), [['runs', 'show', RUN, '--json'], ['runs', '--from', FROM, '--to', TO, '--json']]);
  for (const name of RUN_READ_NAMES) {
    assert.ok(RUN_READ_TIMEOUT_MS[name] > 0, name);
  }
});

test('a stopped distribution: no -d call at all, the read answers "stopped"', async () => {
  const { c, rec } = client(wsl(false));
  const outcome = await c.read({ read: 'runsShow', runId: RUN });
  assert.equal(outcome.kind, 'stopped');
  assert.deepEqual(rec.argvs().filter((a) => a.startsWith('-d ')), []);
});

test('a value the daemon would never write is refused BEFORE anything starts — run ids, instants, an argv word smuggled in', async () => {
  const refused: readonly RunRead[] = [
    { read: 'runsShow', runId: '20000101T000000Z-01' },
    { read: 'runsShow', runId: `${RUN} --json` },
    { read: 'runsShow', runId: '-u' },
    { read: 'runs', from: '2026-10-05', to: TO },
    { read: 'runs', from: FROM, to: '2026-10-05T10:05:00+02:00' },
    { read: 'runs', from: '--period', to: 'today' },
    { read: 'runs', from: TO, to: FROM },
  ];
  for (const request of refused) {
    const { c, rec } = client(wsl(true));
    const outcome = await c.read(request);
    assert.equal(outcome.kind, 'readRefused', JSON.stringify(request));
    assert.deepEqual(rec.argvs(), [], `nothing started for ${JSON.stringify(request)}`);
  }
});

test('exit 4 WITH a readable answer is an answer (the history unreadable, the rest read); exit 4 without one is a failure', async () => {
  const withBody = client({ ...wsl(true), [SHOW]: exited(4, goldenText('runs-show-unknown.json'), 'wsl-care: the history cannot be read\n') });
  assert.equal(bodyOf(await withBody.c.read({ read: 'runsShow', runId: RUN })).state, 'unknown');
  const without = client({ ...wsl(true), [SHOW]: exited(4, '', 'wsl-care: the history cannot be read\n') });
  const outcome = await without.c.read({ read: 'runsShow', runId: RUN });
  assert.equal(outcome.kind, 'unknownFailure');
});

test('a timeout reads as a timeout of the read\'s own ceiling; a refusal (2) as refused', async () => {
  const slow = client({ ...wsl(true), [SHOW]: { kind: 'timedOut', timeoutMs: RUN_READ_TIMEOUT_MS.runsShow, stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) } });
  const timedOut = await slow.c.read({ read: 'runsShow', runId: RUN });
  assert.ok(timedOut.kind === 'timedOut' && timedOut.timeoutMs === RUN_READ_TIMEOUT_MS.runsShow, JSON.stringify(timedOut));
  const usage = client({ ...wsl(true), [SHOW]: exited(2, '', 'wsl-care: "runs show" takes a run id\n') });
  assert.equal((await usage.c.read({ read: 'runsShow', runId: RUN })).kind, 'refused');
});

test('two reads of the same run in flight share ONE call', async () => {
  const { c, rec } = client({ ...wsl(true), [SHOW]: exited(0, goldenText('runs-show-done.json')) });
  await Promise.all([c.read({ read: 'runsShow', runId: RUN }), c.read({ read: 'runsShow', runId: RUN })]);
  assert.equal(rec.argvs().filter((a) => a === SHOW).length, 1);
});

// ---- the answers, read over the goldens ----

test('runs show over the done golden: its state, run id, the line with its outcome, freed bytes and actions', () => {
  const show = parseRunShow(JSON.parse(goldenText('runs-show-done.json')) as Record<string, unknown>);
  assert.deepEqual(show.state, { kind: 'known', value: 'done' });
  assert.equal(show.runId, RUN);
  assert.equal(show.line?.outcome, 'completed');
  assert.equal(show.line?.trigger, 'cli');
  assert.equal(show.line?.freedBytes, 0);
  assert.deepEqual(show.line?.actions, [{ id: 'A10', status: 'ran', count: 0, freedBytes: 0 }]);
});

test('runs show over the interrupted and unknown goldens: the state and the daemon\'s reason', () => {
  const interrupted = parseRunShow(JSON.parse(goldenText('runs-show-interrupted.json')) as Record<string, unknown>);
  assert.deepEqual(interrupted.state, { kind: 'known', value: 'interrupted' });
  assert.match(interrupted.reason, /^swept: pid 4242 is gone/);
  assert.deepEqual(interrupted.line?.actions.map((a) => a.id), ['A5', 'A4']);
  const unknown = parseRunShow(JSON.parse(goldenText('runs-show-unknown.json')) as Record<string, unknown>);
  assert.deepEqual(unknown.state, { kind: 'known', value: 'unknown' });
  assert.equal(unknown.line, undefined);
});

test('a running run carries its running block; a value this build does not know reads "unknown (<value>)", sanitised', () => {
  const live = JSON.parse(goldenText('status-running-live.json')) as Record<string, unknown>;
  const running = parseRunShow({ schemaVersion: 1, runId: RUN, state: 'running', running: live.running });
  assert.equal(running.running?.runId, RUN);
  assert.deepEqual(running.running?.actions, ['A5', 'A4']);
  const odd = parseRunShow({ schemaVersion: 1, runId: RUN, state: 'paused\u202Eevil' });
  assert.deepEqual(odd.state, { kind: 'unknown', label: 'unknown (paused\uFFFDevil)' });
  assert.equal(parseRunShow({ schemaVersion: 1, runId: 'not a run' }).runId, undefined);
});

test('terminal for polling: done, refused, interrupted, unknown — and a state this build does not know; queued and running are not', () => {
  const terminal = RUN_SHOW_STATES.filter((state) => isTerminal({ kind: 'known', value: state }));
  assert.deepEqual(terminal, ['done', 'refused', 'interrupted', 'unknown']);
  assert.equal(isTerminal(readEnum('paused', RUN_SHOW_STATES)), true, 'never stuck on a value a later daemon adds');
});

test('runs over the local-day golden: every line, in order, with its trigger, start, outcome and action ids', () => {
  const lines = parseRuns(JSON.parse(goldenText('runs-local-day.json')) as Record<string, unknown>);
  assert.deepEqual(lines.map((l) => [l.trigger, l.startedAt, l.outcome, l.actions.map((a) => a.id)]), [
    ['timer', '2026-10-01T21:00:00+00:00', 'completed', ['A10']],
    ['manual', '2026-10-01T23:59:59+00:00', 'completed', ['A4']],
    ['timer', '2026-10-02T00:00:00+00:00', 'completed', ['A10']],
  ]);
  assert.equal(lines[1]?.freedBytes, 308000000);
  assert.deepEqual(parseRuns({ schemaVersion: 1, runs: [7, null, { runId: 'x' }] }).map((l) => l.runId), [undefined], 'non-objects dropped, a bad run id read as none');
});
test('§15o: a line\'s kind is read STRICTLY — "collect" or "act"; any other value, a case variant or a non-string reads as absent', () => {
  const kinds = parseRuns({ schemaVersion: 1, runs: ['collect', 'act', 'Collect', 'sweep', 7, null, undefined, `collect${String.fromCharCode(0x202e)}`].map((kind) => ({ runId: RUN, kind })) }).map((l) => l.kind);
  assert.deepEqual(kinds, ['collect', 'act', undefined, undefined, undefined, undefined, undefined, undefined]);
  assert.equal(parseRunShow({ schemaVersion: 1, runId: RUN, state: 'done', run: { runId: RUN, kind: 'act' } }).line?.kind, 'act', 'runs show\'s line too');
  assert.equal(parseRuns(JSON.parse(goldenText('runs-local-day.json')) as Record<string, unknown>)[0]?.kind, undefined, 'a line from a daemon older than §15o has none');
});
