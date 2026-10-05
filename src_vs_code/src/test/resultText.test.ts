import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

import type { JournalEntry } from '../cleanup/journal';
import { handOffNotice, resultNotice } from '../cleanup/resultText';
import { parseRunShow } from '../cleanup/runAnswers';
import { DAEMON_EXIT, WSL_EXE_FAILED } from '../client/exitCodes';
import { exitFailure } from '../root/rootFailures';
import { rootFailureText } from '../root/rootFailureText';
import { runIdOf, type RunId } from '../root/rootIds';
import { GOLDEN_ROOT } from './support/paths';

/**
 * What a person is told (E6.S3, plan §15j m3, §15k #3 / #4): the hand-off at once, the terminal answer once, every exit
 * code of the plan's list in words of its own.
 */

function runId(text: string): RunId {
  const id = runIdOf(text);
  assert.ok(id !== undefined);
  return id;
}

const ENTRY: JournalEntry = { id: 'e', kind: 'run', op: 'clean', distro: 'Ubuntu', actions: ['A4'], since: '2026-10-05T10:00:00.000Z', runId: runId('20261005T100000Z-77') };

function show(name: string): ReturnType<typeof parseRunShow> {
  return parseRunShow(JSON.parse(fs.readFileSync(path.join(GOLDEN_ROOT, 'head', name), 'utf8')) as Record<string, unknown>);
}

test('m3: every exit code the plan lists has words of its own — 1 (not the relay), 2, 3, 4, 69, 70, 71, 73, 75–80, 130 and wsl.exe\'s -1', () => {
  const codes = [DAEMON_EXIT.runFailed, DAEMON_EXIT.usage, DAEMON_EXIT.actionFailed, DAEMON_EXIT.recordsUnreadable, DAEMON_EXIT.detachUnavailable, DAEMON_EXIT.internal, DAEMON_EXIT.detachStartFailed, DAEMON_EXIT.queueFull, DAEMON_EXIT.busy, DAEMON_EXIT.wedged, DAEMON_EXIT.needsRoot, DAEMON_EXIT.observeOnly, DAEMON_EXIT.stateUnreadable, DAEMON_EXIT.requestGone, DAEMON_EXIT.interrupted, WSL_EXE_FAILED];
  const words = codes.map((code) => rootFailureText(exitFailure({ kind: 'exited', code, stdout: Buffer.alloc(0), stderr: Buffer.from('wsl-care: a reason\n', 'utf8') }, 'Ubuntu', false)));
  assert.equal(new Set(words.map((w) => w.label)).size, codes.length, JSON.stringify(words.map((w) => w.label)));
  assert.equal(new Set(words.map((w) => w.sentence)).size, codes.length);
});

test('the hand-off: accepted names its run id and says the panel follows it; unknown says it is followed from the daemon\'s records', () => {
  const accepted = handOffNotice({ kind: 'accepted', runId: runId('20261005T100000Z-77'), unit: 'wsl-care-act@20261005T100000Z-77.service', productVersion: '0.1.0' }, 'A4');
  assert.deepEqual(accepted, { level: 'info', sentence: 'Cleaning A4: the daemon took it as run 20261005T100000Z-77; the panel follows it to its result.' });
  const unknown = handOffNotice({ kind: 'outcomeUnknown', runId: undefined, reason: 'the call did not answer in time\u202E', followed: { polls: 15, answered: 15 }, otherRun: undefined }, 'A4');
  assert.equal(unknown.level, 'warn');
  assert.match(unknown.sentence, /^The daemon's answer to A4 was not seen \(the call did not answer in time\uFFFD\); the panel follows it from the daemon's own records\.$/);
  const stopping = handOffNotice({ kind: 'stopping', runId: runId('20261005T100000Z-77'), unit: 'wsl-care-act@20261005T100000Z-77.service' }, 'the wedged run');
  assert.match(stopping.sentence, /^Stopping run 20261005T100000Z-77/);
  const refused = handOffNotice({ kind: 'busy', messages: ['wsl-care: busy'], running: undefined }, 'A4');
  assert.deepEqual(refused, { level: 'error', sentence: rootFailureText({ kind: 'busy', messages: ['wsl-care: busy'], running: undefined }).sentence });
});

test('a terminal answer: done with what it freed, refused and interrupted with the daemon\'s reason, unknown, the ceiling, never-ran, ambiguous', () => {
  const done = resultNotice({ kind: 'run', entry: ENTRY, show: { ...show('runs-show-done.json'), line: { runId: ENTRY.kind === 'run' ? ENTRY.runId : undefined, trigger: 'manual', startedAt: '', outcome: 'completed', freedBytes: 59_598_000_000, actions: [{ id: 'A4', status: 'ran', count: 387, freedBytes: 59_598_000_000 }], reason: '' } } });
  assert.deepEqual(done, { level: 'info', sentence: 'Run 20261005T100000Z-77 (A4) is done: freed 59.6 GB, 387 objects removed.' });
  const interrupted = resultNotice({ kind: 'run', entry: ENTRY, show: show('runs-show-interrupted.json') });
  assert.equal(interrupted.level, 'warn');
  assert.match(interrupted.sentence, /^Run 20261005T100000Z-77 \(A4\) was interrupted: swept: pid 4242 is gone/);
  const refused = resultNotice({ kind: 'run', entry: ENTRY, show: { ...show('runs-show-unknown.json'), state: { kind: 'known', value: 'refused' }, reason: 'busy: run X holds the lock' } });
  assert.match(refused.sentence, /was refused: busy: run X holds the lock$/);
  assert.match(resultNotice({ kind: 'run', entry: ENTRY, show: show('runs-show-unknown.json') }).sentence, /^The daemon does not know run 20261005T100000Z-77: no history line/);
  assert.match(resultNotice({ kind: 'run', entry: ENTRY, show: { ...show('runs-show-unknown.json'), state: { kind: 'unknown', label: 'unknown (paused)' } } }).sentence, /a state this extension does not know: unknown \(paused\)/);
  const ceiling = resultNotice({ kind: 'ceiling', entry: ENTRY });
  assert.equal(ceiling.level, 'warn');
  assert.match(ceiling.sentence, /^Run 20261005T100000Z-77 \(A4\): state unknown — no answer that it ended within 30 minutes/);
  const unresolved: JournalEntry = { id: 'u', kind: 'unresolved', op: 'clean', distro: 'Ubuntu', actions: ['A4', 'A5'], since: '2026-10-05T10:00:00.000Z' };
  assert.match(resultNotice({ kind: 'neverRan', entry: unresolved }).sentence, /^The cleanup of A4, A5 confirmed at 2026-10-05 10:00 UTC never ran: the daemon recorded no run of it\.$/);
  assert.match(resultNotice({ kind: 'ambiguous', entry: unresolved, candidates: [runId('20261005T100000Z-80'), runId('20261005T100001Z-81')] }).sentence, /runs 20261005T100000Z-80, 20261005T100001Z-81 each match/);
  assert.match(resultNotice({ kind: 'neverRan', entry: { ...unresolved, op: 'fullCheck', actions: ['collect'] } }).sentence, /^The full check confirmed at/);
});
