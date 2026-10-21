import assert from 'node:assert/strict';
import { test } from 'node:test';

import { parseRuns, type RunLine } from '../cleanup/runAnswers';
import type { JsonObject } from '../client/outcome';
import type { WindowsReadOutcome } from '../client/windowsTasks';
import type { WindowsRunRead } from '../client/verbs';
import type { LastRun } from '../scheduler/schedulerAnswer';
import { keptInUseCount, lastCleanupLine, pickRecord, readCleanupRecord, recordWindow, type CleanupRecordRead } from '../windowsTasks/cleanupRecord';
import { bodyOf } from './support/body';
import { stageFixture } from './support/windowsTasks';

/**
 * The last-cleanup line (WR-T3 of PLAN_windows_runner.md §3.12, R2): the newest A23 run record, read through the client's closed run reads, combined with
 * Task Scheduler's own last result. The words are the daemon's measured facts only: "N deleted" and "logical bytes" are typed fields of the history line;
 * "entries kept in use" is counted from the run's detail (`notRemoved[]`, notes that begin `kept in use:`) because no typed field carries it.
 */

const RAN = (result = 0, utc = '2026-10-01T21:00:00Z'): LastRun => ({ kind: 'ran', utc, result });
const FORMAT = (iso: string): string => `<${iso}>`;

function runs(change: (lines: Record<string, unknown>[]) => void = () => undefined): readonly RunLine[] {
  const body = structuredClone(stageFixture('runs-windows-a23.json')) as Record<string, unknown>;
  change(body.runs as Record<string, unknown>[]);

  return parseRuns(body as JsonObject);
}

function found(over: Record<string, unknown> = {}, kept: { readonly keptInUse: number | undefined } = { keptInUse: 3 }): CleanupRecordRead {
  const line = pickRecord(runs((lines) => Object.assign(lines[2] ?? {}, over)));
  assert.ok(line !== undefined, 'the fixture holds an A23 line');

  return { kind: 'found', line, keptInUse: kept.keptInUse };
}

test('the line from a completed record: N deleted, M entries kept in use, bytes logical, never freed', () => {
  const { line, level } = lastCleanupLine(RAN(), found(), FORMAT);
  assert.equal(line, 'last cleanup: <2026-10-01T21:00:00+00:00>, 12 deleted, 3 entries kept in use, 4096 logical bytes');
  assert.equal(level, 'ok');
  assert.doesNotMatch(line, /freed/i);
});

test('a hand-run record (not the timer\'s) says who started it', () => {
  const { line } = lastCleanupLine(RAN(), found({ trigger: 'cli' }), FORMAT);
  assert.match(line, /^last cleanup: <2026-10-01T21:00:00\+00:00> \(trigger: cli\), 12 deleted/);
});

test('an interrupted run reads interrupted, never completed; a dry run reads dry and claims no deletion', () => {
  const interrupted = lastCleanupLine(RAN(), found({ outcome: 'interrupted' }), FORMAT);
  assert.match(interrupted.line, /interrupted: the next run resumes the half-deleted entry first/);
  assert.doesNotMatch(interrupted.line, /completed/);
  assert.equal(interrupted.level, 'warn');
  const dry = lastCleanupLine(RAN(), found({ dryRun: true, outcome: 'completed' }), FORMAT);
  assert.match(dry.line, /dry run: previewed only, nothing deleted/);
  assert.doesNotMatch(dry.line, /12 deleted|completed/, 'a dry run deleted nothing');
  assert.equal(dry.level, 'ok');
  for (const outcome of ['failed', 'observeOnly', 'refused']) {
    const row = lastCleanupLine(RAN(), found({ outcome }), FORMAT);
    assert.ok(row.line.endsWith(` — ${outcome}`), row.line);
    assert.equal(row.level, 'warn');
  }
});

test('lastResult 75 reads busy; 81 reads elevated with the UAC sentence; 84 reads the deadline; any other code its words — with and without a record', () => {
  const rows: readonly (readonly [number, RegExp, string])[] = [
    [75, /busy: a watch was sampling under the run lock; the next run retries/, 'ok'],
    [81, /refused: it ran elevated; with UAC off or on the built-in Administrator account every process is elevated — run it from an account that is not an administrator, or turn UAC on/, 'warn'],
    [84, /ended at its deadline: its whole tree was ended \(exit 84\)/, 'warn'],
    [1, /the run failed \(exit 1\)/, 'warn'],
    [0x8007_0005, /result 0x80070005/, 'warn'],
  ];
  for (const [code, wanted, level] of rows) {
    for (const record of [found(), { kind: 'none' } as const]) {
      const row = lastCleanupLine(RAN(code), record, FORMAT);
      assert.match(row.line, wanted, `${code} / ${record.kind}`);
      assert.equal(row.level, level, `${code} / ${record.kind}`);
    }
  }
  assert.doesNotMatch(lastCleanupLine(RAN(0), found(), FORMAT).line, /succeeded|exit 0/, 'a clean result adds nothing');
});

test('no record in the window says so; a detail that is absent says "kept in use: not recorded"; a failed history read says so and levels unknown', () => {
  const none = lastCleanupLine(RAN(), { kind: 'none' }, FORMAT);
  assert.equal(none.line, 'last cleanup: <2026-10-01T21:00:00Z> — no run record in the daemon\'s history');
  const unrecorded = lastCleanupLine(RAN(), found({}, { keptInUse: undefined }), FORMAT);
  assert.match(unrecorded.line, /12 deleted, kept in use: not recorded, 4096 logical bytes/);
  const failed = lastCleanupLine(RAN(), { kind: 'unreadable', reason: 'it did not answer within 20 s' }, FORMAT);
  assert.equal(failed.line, 'last cleanup: <2026-10-01T21:00:00Z> — the run history could not be read (it did not answer within 20 s)');
  assert.equal(failed.level, 'unknown');
  const reading = lastCleanupLine(RAN(), undefined, FORMAT);
  assert.equal(reading.line, 'last cleanup: <2026-10-01T21:00:00Z>', 'before the record is read the line says only what Task Scheduler said');
});

test('a task that never ran says so; a task that is not there has no line', () => {
  assert.deepEqual(lastCleanupLine({ kind: 'never' }, undefined, FORMAT), { line: 'last cleanup: never (the task has not run yet)', level: 'none' });
  assert.deepEqual(lastCleanupLine(undefined, undefined, FORMAT), { line: '', level: 'none' });
});

test('every word the daemon or Task Scheduler supplied is made printable and unlinked', () => {
  const bidi = String.fromCharCode(0x202e);
  const record = found({ outcome: `failed${bidi}[x](command:y)`, trigger: `cli${bidi}` });
  const { line } = lastCleanupLine(RAN(), record, (iso) => `${iso}${bidi}`);
  assert.doesNotMatch(line, new RegExp(bidi));
  assert.doesNotMatch(line, /\]\(/);
  const reason = lastCleanupLine(RAN(), { kind: 'unreadable', reason: `a [b](command:c)${bidi}` }, FORMAT).line;
  assert.doesNotMatch(reason, new RegExp(`${bidi}|\\]\\(`));
});

// ---- the window, the pick, the count ----

test('the window is two checked instants, whole seconds, UTC; it starts before Task Scheduler\'s truncated second and never ends before it starts', () => {
  const now = Date.parse('2026-10-01T22:00:00.789Z');
  assert.deepEqual(recordWindow('2026-10-01T21:00:00Z', now), { from: '2026-10-01T20:59:55Z', to: '2026-10-01T22:00:00Z' });
  const future = recordWindow('2026-10-02T00:00:00Z', now);
  assert.ok(future !== undefined && Date.parse(future.to) > Date.parse(future.from), 'a last run in the future (a clock set back) still gives a window that ends after it starts');
  assert.equal(recordWindow('not a time', now), undefined);
  assert.equal(recordWindow('', now), undefined);
});

test('the newest A23 line wins; lines of other actions are not the cleanup', () => {
  const picked = pickRecord(runs());
  assert.equal(picked?.runId, '20261001T210000Z-4242');
  assert.equal(picked?.actions.find((a) => a.id === 'A23')?.count, 12);
  assert.equal(pickRecord(runs((lines) => { lines.splice(2, 1); lines.splice(0, 1); })), undefined, 'no A23 line');
  const older = pickRecord(runs((lines) => { lines.splice(2, 1); }));
  assert.equal(older?.runId, '20261001T100000Z-100', 'the newer of the A23 lines that are there');
});

test('kept in use is counted from the notes that begin "kept in use:" — not from every entry not removed; anything missing is not recorded', () => {
  const show = stageFixture('runs-show-a23-done.json');
  assert.equal(keptInUseCount(show as JsonObject), 3, 'expected 3: five entries were not removed, three of them kept in use');
  assert.equal(keptInUseCount(stageFixture('runs-show-a23-dry.json') as JsonObject), 0);
  const noDetail = { ...show } as Record<string, unknown>;
  delete noDetail.detail;
  assert.equal(keptInUseCount(noDetail as JsonObject), undefined);
  const noA23 = structuredClone(show) as Record<string, unknown>;
  ((noA23.detail as Record<string, unknown>).actions as Record<string, unknown>[])[0]!.id = 'A10';
  assert.equal(keptInUseCount(noA23 as JsonObject), undefined);
  const noNotRemoved = structuredClone(show) as Record<string, unknown>;
  delete (((noNotRemoved.detail as Record<string, unknown>).actions as Record<string, unknown>[])[0]!.run as Record<string, unknown>).notRemoved;
  assert.equal(keptInUseCount(noNotRemoved as JsonObject), undefined);
});

// ---- the read ----

interface Seen { readonly requests: WindowsRunRead[] }

/** A client whose run reads answer from fixtures (`runs`, `runsShow`) and remember what was asked; `fail` makes a read not answer. */
function clientOf(answers: { readonly runs?: string; readonly show?: string; readonly failRuns?: string; readonly failShow?: string }): { readonly client: Parameters<typeof readCleanupRecord>[0]; readonly seen: Seen } {
  const seen: Seen = { requests: [] };
  const client = {
    read: (request: WindowsRunRead): Promise<WindowsReadOutcome> => {
      seen.requests.push(request);
      const failure = request.read === 'runs' ? answers.failRuns : answers.failShow;
      if (failure !== undefined) {
        return Promise.resolve({ kind: 'failed', read: request.read, reason: failure });
      }
      const name = request.read === 'runs' ? answers.runs : answers.show;

      return Promise.resolve({ kind: 'read', read: request.read, body: bodyOf(stageFixture(name ?? 'runs-windows-a23.json'), 'fixture') });
    },
  };

  return { client, seen };
}

test('the read asks `runs` with the checked window, then `runs show` ONCE for the picked run when its detail is there', async () => {
  const { client, seen } = clientOf({ runs: 'runs-windows-a23.json', show: 'runs-show-a23-done.json' });
  const record = await readCleanupRecord(client, '2026-10-01T21:00:00Z', Date.parse('2026-10-01T22:00:00Z'));
  assert.deepEqual(seen.requests, [
    { read: 'runs', from: '2026-10-01T20:59:55Z', to: '2026-10-01T22:00:00Z' },
    { read: 'runsShow', runId: '20261001T210000Z-4242' },
  ]);
  assert.equal(record.kind, 'found');
  assert.equal(record.kind === 'found' ? record.keptInUse : -1, 3);
});

test('a picked run with no detail file asks no `runs show`; no A23 line is "none"; a failed read of either is told, never guessed', async () => {
  const noDetail = clientOf({ runs: 'runs-windows-a23.json' });
  // the older A23 line has detailState "none": drop the newest so it is the pick
  const older = {
    read: async (request: WindowsRunRead): Promise<WindowsReadOutcome> => {
      noDetail.seen.requests.push(request);
      const body = structuredClone(stageFixture('runs-windows-a23.json')) as Record<string, unknown>;
      (body.runs as unknown[]).splice(2, 1);

      return { kind: 'read', read: 'runs', body: body as JsonObject };
    },
  };
  const record = await readCleanupRecord(older, '2026-10-01T10:00:00Z', Date.parse('2026-10-01T22:00:00Z'));
  assert.equal(record.kind === 'found' ? record.keptInUse : 'not found', undefined, 'detailState none: kept in use is not recorded');
  assert.deepEqual(noDetail.seen.requests.map((r) => r.read), ['runs']);
  const empty = { read: (): Promise<WindowsReadOutcome> => Promise.resolve({ kind: 'read', read: 'runs', body: { runs: [] } as JsonObject }) };
  assert.deepEqual(await readCleanupRecord(empty, '2026-10-01T21:00:00Z', Date.parse('2026-10-01T22:00:00Z')), { kind: 'none' });
  const failedRuns = await readCleanupRecord(clientOf({ failRuns: 'it did not answer within 20 s' }).client, '2026-10-01T21:00:00Z', Date.parse('2026-10-01T22:00:00Z'));
  assert.deepEqual(failedRuns, { kind: 'unreadable', reason: 'it did not answer within 20 s' });
  const failedShow = await readCleanupRecord(clientOf({ failShow: 'it exited 4' }).client, '2026-10-01T21:00:00Z', Date.parse('2026-10-01T22:00:00Z'));
  assert.equal(failedShow.kind === 'found' ? failedShow.keptInUse : 'not found', undefined, 'a detail that cannot be read is "not recorded", the counts of the history line stand');
  const refused = { read: (): Promise<WindowsReadOutcome> => Promise.resolve({ kind: 'readRefused', read: 'runs', detail: 'a runs window must end after it starts' }) };
  assert.deepEqual(await readCleanupRecord(refused, '2026-10-01T21:00:00Z', Date.parse('2026-10-01T22:00:00Z')), { kind: 'unreadable', reason: 'a runs window must end after it starts' });
  const unreadableInstant = await readCleanupRecord(clientOf({}).client, 'never', 0);
  assert.equal(unreadableInstant.kind, 'unreadable');
});
