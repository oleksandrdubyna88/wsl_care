import assert from 'node:assert/strict';
import { test } from 'node:test';

import { VERB_TIMEOUT_MS } from '../client/verbs';
import { callRoot, DETACH_TIMEOUT_MS, ROOT_OPS, ROOT_TIMEOUT_MS, rootRequest, STOP_TIMEOUT_MS, type RootOp } from '../root/rootCall';
import { runIdOf, volumeNameOf, type RunId, type VolumeName } from '../root/rootIds';
import { exited, recordingRunner } from './support/recordingRunner';

/**
 * `root/rootCall.ts` — the ONE module that spells a root argv word (plan §15j M1): every root call is one of the closed
 * `ROOT_OPS`, and each op's argv is asserted here WORD FOR WORD, written out independently of the module (the oracle of
 * the plan's decision, as the fake's verb list is). Never `--timer`, `--user` or `config`; `-u root` only in its one
 * place; stdin only with `--only -`.
 */

const TARGET = { wsl: 'C:\\Windows\\System32\\wsl.exe', distro: 'Ubuntu' } as const;
const PREFIX = ['-d', 'Ubuntu', '-u', 'root', '--cd', '/', '--exec', '/opt/wsl-care/bin/wsl-care'];
const RUN = runIdOf('20261004T101500Z-4242') as RunId;

function names(count: number): VolumeName[] {
  return Array.from({ length: count }, (_, i) => volumeNameOf(`${i.toString(16).padStart(8, '0')}${'cd'.repeat(28)}`) as VolumeName);
}

const OPS: readonly { readonly op: RootOp; readonly tail: readonly string[] }[] = [
  { op: { op: 'preview', ids: ['A4'] }, tail: ['act', 'A4', '--preview', '--json'] },
  { op: { op: 'preview', ids: ['A5', 'A4', 'A10'] }, tail: ['act', 'A5,A4,A10', '--preview', '--json'] },
  { op: { op: 'confirm', ids: ['A10'], shown: undefined }, tail: ['act', 'A10', '--confirm', '--manual', '--detach', '--json'] },
  { op: { op: 'confirm', ids: ['A5', 'A4'], shown: names(2) }, tail: ['act', 'A5,A4', '--confirm', '--manual', '--detach', '--only', '-', '--json'] },
  { op: { op: 'stop', runId: RUN }, tail: ['act', '--stop', '20261004T101500Z-4242', '--json'] },
  { op: { op: 'fullCheck' }, tail: ['collect', '--detach', '--json'] },
  { op: { op: 'rootCheck' }, tail: ['--version'] },
];

for (const { op, tail } of OPS) {
  test(`root ${op.op} ${'ids' in op ? op.ids.join(',') : ''}: exactly -d <distro> -u root --cd / --exec <daemon> ${tail.join(' ')}`, () => {
    const request = rootRequest(TARGET, op);
    assert.ok(request !== undefined);
    assert.equal(request.file, TARGET.wsl, 'the absolute System32 launcher the client resolved');
    assert.deepEqual(request.args, [...PREFIX, ...tail]);
    assert.equal(request.timeoutMs, ROOT_TIMEOUT_MS[op.op]);
  });
}

test('every op of the closed union has its argv asserted above — an op added to ROOT_OPS without a row here fails', () => {
  assert.deepEqual([...ROOT_OPS], ['preview', 'confirm', 'stop', 'fullCheck', 'rootCheck']);
  assert.deepEqual([...new Set(OPS.map((o) => o.op.op))].sort(), [...ROOT_OPS].sort());
});

test('no op ever spells --timer, --user, config, or -u anywhere but its one place after -d', () => {
  for (const { op } of OPS) {
    const args = rootRequest(TARGET, op)?.args ?? [];
    assert.equal(args.some((a) => a === '--timer' || a === '--user' || a === 'config' || a.startsWith('--timer') || a.startsWith('--user')), false, args.join(' '));
    assert.deepEqual(args.map((a, i) => (a === '-u' ? i : -1)).filter((i) => i >= 0), [2]);
  }
});

test('A4\'s shown list travels on stdin as one name per line, ended — and ONLY a confirm with --only - carries stdin', () => {
  const shown = names(3);
  const request = rootRequest(TARGET, { op: 'confirm', ids: ['A4'], shown });
  assert.equal(request?.stdin?.toString('utf8'), `${shown.join('\n')}\n`);
  for (const { op } of OPS.filter((o) => !(o.op.op === 'confirm' && o.op.shown !== undefined))) {
    assert.equal(rootRequest(TARGET, op)?.stdin, undefined, op.op);
  }
});

test('A4 is ALWAYS bound to its shown list: an A4 confirm whose preview showed nothing still sends --only - with an empty stdin', () => {
  const request = rootRequest(TARGET, { op: 'confirm', ids: ['A4'], shown: [] });
  assert.deepEqual(request?.args.slice(-3), ['--only', '-', '--json']);
  assert.equal(request?.stdin?.length, 0);
});

test('a confirm whose shown list does not match its ids builds NOTHING: A4 without a list, a list without A4', () => {
  assert.equal(rootRequest(TARGET, { op: 'confirm', ids: ['A4'], shown: undefined }), undefined, 'A4 would re-select live — never');
  assert.equal(rootRequest(TARGET, { op: 'confirm', ids: ['A10'], shown: names(1) }), undefined, 'a list belongs to A4 alone');
});

test('10 000 shown names (the cap) make 650 000 stdin bytes — beside an argv that stays short (no 32 767-character limit)', () => {
  const request = rootRequest(TARGET, { op: 'confirm', ids: ['A4'], shown: names(10_000) });
  assert.equal(request?.stdin?.length, 650_000);
  assert.ok((request?.args.join(' ').length ?? 0) < 200);
});

test('the host\'s ceilings, stated (plan §15k #19): a detach answers well inside 90 s, a stop inside 150 s, a preview as preview --all', () => {
  assert.equal(DETACH_TIMEOUT_MS, 90_000);
  assert.equal(STOP_TIMEOUT_MS, 150_000);
  assert.deepEqual(ROOT_TIMEOUT_MS, { preview: VERB_TIMEOUT_MS.preview, confirm: DETACH_TIMEOUT_MS, stop: STOP_TIMEOUT_MS, fullCheck: DETACH_TIMEOUT_MS, rootCheck: VERB_TIMEOUT_MS.version });
});

test('callRoot spawns only through the runner seam it is handed — the one request, nothing else', async () => {
  const runner = recordingRunner({ [[...PREFIX, '--version'].join(' ')]: exited(0, '0.1.0\n') });
  const result = await callRoot(runner.runner, TARGET, { op: 'rootCheck' });
  assert.equal(result?.kind, 'exited');
  assert.deepEqual(runner.argvs(), [[...PREFIX, '--version'].join(' ')]);
});

test('callRoot of a confirm that cannot be built starts nothing', async () => {
  const runner = recordingRunner({});
  assert.equal(await callRoot(runner.runner, TARGET, { op: 'confirm', ids: ['A4'], shown: undefined }), undefined);
  assert.deepEqual(runner.argvs(), []);
});
