import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

import { deriveCleanup, type CleanupState } from '../cleanup/cleanupView';
import type { JournalEntry } from '../cleanup/journal';
import { runIdOf } from '../root/rootIds';
import type { Snapshot } from '../state/outcomeStore';
import { answered, failed, headBody, type Body } from './support/outcomes';
import { GOLDEN_ROOT } from './support/paths';

/**
 * The cleanup controls (E6.S3, `common.durable-status`): derived from the daemon's goldens and the journal — so the
 * "Cleaning… A4" a reloaded panel shows is the daemon's `running` block, a dead run never sticks, and a stop is offered only
 * for a wedged run the daemon can stop.
 */

const IDLE: CleanupState = { entries: [], results: [], flowBusy: false };

function goldenStatus(name: string): Body {
  return JSON.parse(fs.readFileSync(path.join(GOLDEN_ROOT, 'head', name), 'utf8')) as Body;
}

function snapshot(status: Body, preview: Body | null = headBody('preview')): Snapshot {
  return { status: answered('status', status), preview: preview === null ? undefined : answered('preview', preview), doctor: undefined, checking: false };
}

test('idle (status.json, the head preview): every row with something to clean has an enabled Clean, nothing is in flight', () => {
  const { controls, stoppable } = deriveCleanup(snapshot(headBody('status')), IDLE);
  assert.equal(controls.enabled, true, controls.reason);
  assert.equal(controls.state, '');
  assert.deepEqual(controls.rows.map((r) => r.rowId), ['A4', 'A5', 'A5Testcontainers', 'A6', 'A6Unused', 'A7', 'A8', 'A9']);
  const a4 = controls.rows.find((r) => r.rowId === 'A4');
  assert.deepEqual(a4, { rowId: 'A4', label: 'Clean A4', enabled: true, note: '3 · 0.6 GB' });
  assert.ok(controls.rows.filter((r) => !r.enabled).every((r) => /nothing to clean|unavailable/.test(r.note)), JSON.stringify(controls.rows));
  assert.equal(controls.fullCheck, true);
  assert.equal(controls.stop, undefined);
  assert.deepEqual(stoppable, []);
});

test('the reload case: status.running live (A5, A4, on A4) reads "Cleaning… A4" and greys every button — from the daemon, not a flag', () => {
  const { controls } = deriveCleanup(snapshot(goldenStatus('status-running-live.json')), IDLE);
  assert.equal(controls.state, 'Cleaning… A4');
  assert.equal(controls.enabled, false);
  assert.ok(controls.rows.every((r) => !r.enabled));
  assert.equal(controls.fullCheck, false);
});

test('queued reads "Queued… A4"; unreadable says why and greys the buttons', () => {
  assert.equal(deriveCleanup(snapshot(goldenStatus('status-running-queued.json')), IDLE).controls.state, 'Queued… A4');
  const unreadable = deriveCleanup(snapshot(goldenStatus('status-running-unreadable.json')), IDLE).controls;
  assert.equal(unreadable.enabled, false);
  assert.match(unreadable.state, /does not parse/);
});

test('a DEAD run never sticks: it says it died and that the next run records it interrupted — and the buttons are enabled', () => {
  const { controls } = deriveCleanup(snapshot(goldenStatus('status-running-dead.json')), IDLE);
  assert.equal(controls.enabled, true);
  assert.match(controls.state, /^Run 20000101T000000Z-1 died/);
  assert.equal(controls.stateLevel, 'warn');
});

test('§15j M4: a wedged run of the daemon\'s units gets a Stop (index 0 into the host\'s list); one outside them gets text with its pid', () => {
  const wedged = goldenStatus('status-running-wedged.json');
  const { controls, stoppable } = deriveCleanup(snapshot(wedged), IDLE);
  assert.deepEqual(controls.stop, { index: 0, label: 'Stop run 20000101T000000Z-1' });
  assert.deepEqual(stoppable, [{ runId: '20000101T000000Z-1', actions: ['A5', 'A4'] }]);
  assert.equal(controls.enabled, false);
  assert.match(controls.state, /^Wedged: run 20000101T000000Z-1/);
  const cli = deriveCleanup(snapshot({ ...wedged, running: { ...(wedged.running as Body), trigger: 'cli' } }), IDLE);
  assert.equal(cli.controls.stop, undefined);
  assert.deepEqual(cli.stoppable, []);
  assert.match(cli.controls.stopText, /pid 4242/);
  const noStop = deriveCleanup(snapshot({ ...wedged, capabilities: (wedged.capabilities as string[]).filter((c) => c !== 'act.stop') }), IDLE);
  assert.equal(noStop.controls.stop, undefined, 'a daemon that cannot stop gets no button');
});

test('a daemon that does not advertise the capabilities: every button greyed, the reason is "Update daemon"\'s sentence', () => {
  const body = headBody('status');
  delete body.capabilities;
  const { controls } = deriveCleanup(snapshot(body), IDLE);
  assert.equal(controls.enabled, false);
  assert.match(controls.reason, /Update daemon/);
  assert.equal(controls.fullCheck, false);
});

test('a status that did not answer greys everything with its own label; before any answer, "checking…"', () => {
  const stopped: Snapshot = { status: failed('status', { kind: 'stopped', distro: 'Ubuntu' }), preview: undefined, doctor: undefined, checking: false };
  assert.equal(deriveCleanup(stopped, IDLE).controls.reason, 'WSL stopped');
  assert.equal(deriveCleanup({ status: undefined, preview: undefined, doctor: undefined, checking: true }, IDLE).controls.reason, 'checking…');
});

test('the persisted journal greys the buttons while a run of this distribution awaits its answer — even when status says none yet', () => {
  const runId = runIdOf('20261005T100000Z-77');
  assert.ok(runId !== undefined);
  const entry: JournalEntry = { id: 'e', kind: 'run', op: 'clean', distro: 'Ubuntu', actions: ['A4'], since: '2026-10-05T10:00:00.000Z', runId };
  const { controls } = deriveCleanup(snapshot(headBody('status')), { ...IDLE, entries: [entry] });
  assert.equal(controls.enabled, false);
  assert.equal(controls.state, 'Waiting for the result of run 20261005T100000Z-77 (A4)…');
  const other = deriveCleanup(snapshot(headBody('status')), { ...IDLE, entries: [{ ...entry, distro: 'Debian' }] });
  assert.equal(other.controls.enabled, true, 'another distribution\'s entry does not grey this one');
  assert.equal(deriveCleanup(snapshot(headBody('status')), { ...IDLE, flowBusy: true }).controls.state, 'Confirming…');
});

test('a row the daemon does not report in status.actions has no enabled button', () => {
  const body = headBody('status');
  body.actions = (body.actions as string[]).filter((a) => a !== 'A4');
  assert.equal(deriveCleanup(snapshot(body), IDLE).controls.rows.find((r) => r.rowId === 'A4')?.enabled, false);
});

test('Last cleanup: the results this window showed, newest first, with their level; "Docker after" labelled with the time it was read', () => {
  const results = [{ level: 'warn' as const, sentence: 'Run X was refused: busy' }, { level: 'info' as const, sentence: 'Run Y is done' }];
  const { controls } = deriveCleanup(snapshot(headBody('status')), { ...IDLE, results });
  assert.deepEqual(controls.results, [{ sentence: 'Run X was refused: busy', level: 'warn' }, { sentence: 'Run Y is done', level: 'ok' }]);
  assert.match(controls.dockerAfter, /^Docker now: \d+\.\d GB reclaimable \(docker system df, read at \d{4}-\d{2}-\d{2} \d{2}:\d{2} UTC\)/);
  const status = headBody('status');
  status.lastCleanup = { available: true, runId: '19990101T000000Z-1', startedAt: '1999-01-01T00:00:00+00:00', trigger: 'manual', freedBytes: 5, count: 1 };
  assert.match(deriveCleanup(snapshot(status), IDLE).controls.dockerAfter, /^Docker after the last cleanup: /);
  assert.equal(deriveCleanup(snapshot(headBody('status'), null), IDLE).controls.dockerAfter, '');
});

test('E6.S3 review C2: absent is not zero — an unread count reads "? objects" (no button), unread bytes "? GB"; a real 0 is "nothing to clean"', () => {
  const preview = headBody('preview');
  const rows = preview.rows as Body[];
  rows[0] = { ...rows[0], count: undefined, reclaimableBytes: 5e9 };
  rows[1] = { ...rows[1], count: 3, reclaimableBytes: undefined };
  rows[2] = { ...rows[2], count: 0, reclaimableBytes: 0 };
  const { controls } = deriveCleanup(snapshot(headBody('status'), preview), IDLE);
  const [first, second, third] = controls.rows;
  assert.deepEqual([first?.note, first?.enabled], ['? objects · 5.0 GB', false]);
  assert.deepEqual([second?.note, second?.enabled], ['3 · ? GB', true]);
  assert.deepEqual([third?.note, third?.enabled], ['nothing to clean', false]);
});

test('E6.S3 review C2: "Docker after" with a type Docker could not size says "at least" and how many were not read — never a smaller total as the whole', () => {
  const preview = headBody('preview');
  const types = (preview.totals as { types: Body[] }).types;
  types[0] = { ...types[0], reclaimable: { available: false, reason: 'not read' } };
  assert.match(deriveCleanup(snapshot(headBody('status'), preview), IDLE).controls.dockerAfter, /: at least \d+\.\d GB reclaimable, 1 type not read \(docker system df, read at /);
});
