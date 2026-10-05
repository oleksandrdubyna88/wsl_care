import assert from 'node:assert/strict';
import { test } from 'node:test';

import { CleanupHost } from '../cleanup/cleanupHost';
import { newCleanRecorder, recordingCleanUi } from '../cleanup/cleanRecorder';
import { runIdOf, type RunId } from '../root/rootIds';
import type { HandOffOutcome, PreviewOutcome } from '../root/rootOutcome';
import { OutcomeStore } from '../state/outcomeStore';
import { noticeText, safeText } from '../text/safeText';
import { ManualTimers, MapStore } from './support/memento';
import { answered, headBody } from './support/outcomes';

/**
 * The cleanup host's own guarantees (E6.S3 review round): the ONE road every notification takes is sanitised for the
 * notification's markdown (a daemon string cannot become a clickable `command:` link), the host refuses a full check its
 * own controls grey, and a fault at the detached edge is told — sanitised — and logged.
 */

function run(text: string): RunId {
  const id = runIdOf(text);
  assert.ok(id !== undefined);
  return id;
}

const LINK = '[open me](command:workbench.action.reloadWindow)';
const BRACKET_PAREN = String.fromCharCode(93, 40);

interface Stub {
  preview?: () => Promise<PreviewOutcome>;
  confirm?: () => Promise<HandOffOutcome>;
  stop?: () => Promise<HandOffOutcome>;
  runFullCheck?: () => Promise<HandOffOutcome>;
}

function hostWith(stub: Stub, status: Record<string, unknown> = headBody('status')) {
  const recorder = newCleanRecorder();
  recorder.answer = true;
  const outcomes = new OutcomeStore();
  outcomes.set('status', answered('status', status));
  outcomes.set('preview', answered('preview', headBody('preview')));
  const logged: string[] = [];
  const never = (): Promise<never> => Promise.reject(new Error('not asked in this test'));
  const host = new CleanupHost({
    durable: new MapStore(),
    controller: { preview: stub.preview ?? never, confirm: stub.confirm ?? never, stop: stub.stop ?? never, runFullCheck: stub.runFullCheck ?? never },
    read: never, outcomes, askStatus: () => Promise.resolve(answered('status', status)), refreshPanel: () => Promise.resolve(),
    focused: () => true, ui: recordingCleanUi(recorder), timers: new ManualTimers(), now: () => 0, wallNow: () => Date.parse('2026-10-05T10:00:00.000Z'),
    log: (line) => { logged.push(line); },
  });
  return { host, recorder, logged };
}

test('A1: noticeText breaks markdown link syntax — a daemon string never keeps "](" — and keeps the words', () => {
  for (const text of [LINK, `before ${LINK} after`, '[a](b)[c](d)', `[x]${BRACKET_PAREN}y)`]) {
    const shown = noticeText(text);
    assert.equal(shown.includes(BRACKET_PAREN), false, shown);
    assert.ok(shown.includes('open me') || !text.includes('open me'));
  }
  assert.equal(noticeText('no link here'), 'no link here');
});

test('A1: safeText also makes line and paragraph separators, direction marks and zero-width characters visible', () => {
  const hidden = [0x2028, 0x2029, 0x200e, 0x200f, 0x061c, 0x200b, 0x200c, 0x200d, 0xfeff, 0x2060].map((c) => String.fromCharCode(c));
  const replacement = String.fromCharCode(0xfffd);
  for (const char of hidden) {
    assert.equal(safeText(`a${char}b`, 50), `a${replacement}b`, char.charCodeAt(0).toString(16));
  }
});

test('A1: a refusal whose daemon message carries a command link reaches the notification WITHOUT the link (the one road)', async () => {
  const { host, recorder } = hostWith({ runFullCheck: () => Promise.resolve({ kind: 'busy', messages: [`wsl-care: busy ${LINK}`], running: undefined }) });
  await host.runFullCheck();
  const sentences = recorder.notices.map((n) => n.sentence);
  assert.ok(sentences.length > 0);
  assert.ok(sentences.every((s) => !s.includes(BRACKET_PAREN)), JSON.stringify(sentences));
  assert.ok(sentences.some((s) => s.includes('open me')), 'the words are still there');
});

test('A4: Run full check now is refused host-side when the controls grey it — nothing reaches the controller', async () => {
  let asked = 0;
  const live = { ...headBody('status'), running: { state: 'live', reason: 'acting', runId: '20261005T100000Z-77', actions: ['A4'], current: 'A4', trigger: 'manual' } };
  const { host, recorder } = hostWith({ runFullCheck: () => { asked += 1; return Promise.resolve({ kind: 'accepted', runId: run('20261005T100000Z-77'), unit: 'x', productVersion: undefined }); } }, live);
  assert.equal(host.controls().fullCheck, false);
  await host.runFullCheck();
  assert.equal(asked, 0);
  assert.match(recorder.notices.at(-1)?.sentence ?? '', /a run is in flight/);
});

test('C11: a fault at the detached edge is told (sanitised) and logged — never swallowed', async () => {
  const { host, recorder, logged } = hostWith({ runFullCheck: () => Promise.reject(new Error(`boom ${LINK}`)) });
  await host.runFullCheck();
  assert.match(recorder.notices.at(-1)?.sentence ?? '', /boom/);
  assert.equal((recorder.notices.at(-1)?.sentence ?? '').includes(BRACKET_PAREN), false);
  assert.ok(logged.some((line) => line.includes('boom')), JSON.stringify(logged));
});
