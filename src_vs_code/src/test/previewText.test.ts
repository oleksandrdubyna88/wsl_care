import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

import { previewResultOf, type TempPreviewAnswer } from '../client/windowsTasks';
import { MAX_GROUPS_SHOWN, MAX_PATHS_SHOWN, previewText } from '../windowsTasks/previewText';
import type { Body } from './support/body';
import { SOURCE_ROOT } from './support/paths';
import { stringLiteralsOf } from './support/sourceScan';
import { exited, stageFixture } from './support/windowsTasks';

/**
 * The text of a *Preview the Windows TEMP cleanup* (WR-T3 of PLAN_windows_runner.md §3.12): the extension's OWN rendering of the daemon's typed report, so
 * every path and word is made printable and bounded HERE, whatever the daemon printed, and the sentences claim only what the daemon measured —
 * logical bytes, never "freed"; a lower bound only when the scan ran out of its budget.
 */

/** The report of a fixture (with `change` applied to its body first), typed by the product's own reader. */
function answerOf(name = 'preview-cleanup.json', change: (body: Record<string, unknown>) => void = () => undefined): TempPreviewAnswer {
  const body = structuredClone(stageFixture(name)) as Record<string, unknown>;
  change(body);
  const result = previewResultOf(exited(0, body as Body));
  assert.equal(result.kind, 'answered', JSON.stringify(result).slice(0, 300));
  if (result.kind !== 'answered') {
    throw new Error('not answered');
  }

  return result.report;
}

type Group = Record<string, unknown> & { paths: string[] };

function groupsOf(body: Record<string, unknown>): Group[] {
  return body.groups as Group[];
}

const TEXT = previewText(answerOf()).text;

test('the text says the root, the cutoff, the ten counts, logical bytes (never freed), the dry line, the pass, the allow-list and the budgets', () => {
  const { headline, text } = previewText(answerOf());
  assert.match(text, /^Windows TEMP cleanup preview — C:\\Users\\user\\AppData\\Local\\Temp; cutoff 2026-09-24T21:00:00\.0000000Z \(temp\.olderThanDays 7\)/);
  assert.match(text, /eligible 2, would be eligible if allowed 3, young 2, excluded 1, unknown 0, changed 0, not allowed 3, deferred 0; 1300 logical bytes/);
  assert.match(text, /logical bytes \(an estimate of the files' sizes, not a measure of disk space\)/);
  assert.doesNotMatch(text, /freed/i, 'the word "freed" never appears beside a number');
  assert.doesNotMatch(headline, /freed/i);
  assert.match(text, /dry run \(A23's own week\): yes — the setting dryRun is on; the dry-run week has not started/);
  assert.match(text, /pass: started \(not yet\), 0 of 8 names decided, 0 completed; the next run starts after \(the first name\)/);
  assert.match(text, /allow-list \(temp\.allowGlobs\): coai-\*, wsl-care-test-\*, stage-\*, mindex-\*, git-commit-\*, docrag-\*, bge-seed-\*/);
  assert.match(text, /scan budget: 200000 nodes, 120 s; at most 5000 entries per run/);
  assert.match(text, /A preview deletes nothing, writes nothing and moves no cursor\. The task deletes only while the dry line says it acts\./);
  assert.match(headline, /^eligible 2, would be eligible if allowed 3, .*1300 logical bytes.* — opened in a read-only tab$/);
});

test('an interrupted deletion and the end of the dry week are said', () => {
  const text = previewText(answerOf('preview-cleanup.json', (b) => {
    (b.pass as Record<string, unknown>).inProgress = 'C:\\Users\\user\\AppData\\Local\\Temp\\coai-half';
    (b.pass as Record<string, unknown>).started = '2026-10-01T21:00:00.0000000Z';
    (b.pass as Record<string, unknown>).last = 'coai-a';
    (b.dry as Record<string, unknown>).until = '2026-10-08T21:00:00+00:00';
  })).text;
  assert.match(text, /an interrupted deletion of C:\\Users\\user\\AppData\\Local\\Temp\\coai-half is taken first/);
  assert.match(text, /the next run starts after coai-a/);
  assert.match(text, /until 2026-10-08T21:00:00\+00:00/);
});

test('groups: eligible then would-be-eligible, paths indented, "and N more", groups not shown, the LOWER BOUND sentence only when limited', () => {
  const plain = previewText(answerOf()).text;
  const limited = previewText(answerOf('preview-cleanup-limited.json')).text;
  assert.ok(plain.indexOf('eligible coai-*: 1 (1000 logical bytes), showing 1:') >= 0, plain);
  assert.match(plain, /eligible coai-\*: 1 \(1000 logical bytes\), showing 1:\n {4}C:\\Users\\user\\AppData\\Local\\Temp\\coai-old-a\n/);
  assert.ok(plain.indexOf('eligible wsl-care-test-*') < plain.indexOf('would be eligible if allowed — widen temp.allowGlobs'), 'eligible groups come first');
  assert.match(plain, /would be eligible if allowed — widen temp\.allowGlobs with the daemon's settings command \(see the README\):\nwouldBeEligible othertool-\*: 1 \(900 logical bytes\), showing 1:/);
  assert.doesNotMatch(plain, /LOWER BOUND/, 'an unlimited scan is not a lower bound');
  assert.doesNotMatch(plain, /more group\(s\) not shown/);
  assert.match(limited, /LOWER BOUND/);
  assert.match(limited, /\(temp\.scanMaxNodes \/ temp\.scanMaxSeconds\)/);
  assert.match(limited, /\.\.\. and 4 more\n/, 'the daemon\'s own "more" is said');
  assert.match(limited, /\.\.\. and 12 more group\(s\) not shown/);
});

test('every path and word is sanitised and clipped; the headline breaks markdown links', () => {
  const bidi = String.fromCharCode(0x202e);
  const bell = String.fromCharCode(7);
  const long = `C:\\Users\\user\\AppData\\Local\\Temp\\${'x'.repeat(2_000)}`;
  const report = answerOf('preview-cleanup.json', (b) => {
    b.root = `C:\\Users\\user\\${bidi}gnp.exe${bell}`;
    const group = groupsOf(b)[0] as Group;
    group.paths = [`C:\\Users\\user\\AppData\\Local\\Temp\\coai-${bidi}fdp.exe`, long, `C:\\x${bell}y`];
    group.pattern = `coai-${bidi}*`;
    (b.dry as Record<string, unknown>).reason = 'see [the docs](command:workbench.action.reloadWindow)';
    b.allowGlobs = [`evil${bell}*`];
  });
  const { headline, text } = previewText(report);
  assert.doesNotMatch(text, new RegExp(`[${bidi}${bell}]`), 'the bidi override reached the text');
  assert.ok(text.includes('\uFFFDgnp.exe\uFFFD'), 'the controls are visible replacements');
  assert.ok(text.includes(`${long.slice(0, 320)}…`), 'a 2 000-character name is clipped at the path bound and says so');
  assert.ok(!text.includes('x'.repeat(400)), 'nothing longer than the bound is shown');
  assert.doesNotMatch(headline, /\]\(/);
  assert.doesNotMatch(previewText(answerOf('preview-cleanup.json', (b) => { (b.dry as Record<string, unknown>).reason = 'a [b](command:x)'; })).headline, /\]\(/);
});

test('a refused root renders the refusal with zero counts; observeOnly renders the errors', () => {
  const refused = previewText(answerOf('preview-cleanup-refused.json'));
  assert.match(refused.text, /refused: the temp directory is the profile root C:\\Users\\user: a cleanup there would be refused/);
  assert.match(refused.text, /eligible 0, would be eligible if allowed 0, young 0/);
  assert.doesNotMatch(refused.text, /showing/);
  const broken = previewText(answerOf('preview-cleanup.json', (b) => { b.observeOnly = true; b.configurationErrors = ['temp.olderThanDays: not a number', 'dryRun: not a boolean']; })).text;
  assert.match(broken, /the configuration is in error \(temp\.olderThanDays: not a number; dryRun: not a boolean\): a run would be refused until it is fixed/);
  assert.doesNotMatch(TEXT, /the configuration is in error|refused:/, 'a clean report says neither');
});

test('the excluded reasons are said, one per line', () => {
  assert.match(TEXT, /excluded 1: the folder Claude Code keeps under the temp directory - never cleaned/);
});

test('the renderer re-bounds a report that lies about its caps', () => {
  const lines = previewText(answerOf('preview-cleanup.json', (b) => {
    const first = groupsOf(b)[0] as Group;
    first.paths = Array.from({ length: 500 }, (_, i) => `C:\\Users\\user\\AppData\\Local\\Temp\\coai-${i}`);
    first.count = 500;
  })).text.split('\n');
  const shown = lines.filter((line) => /^ {4}C:\\Users\\user\\AppData\\Local\\Temp\\coai-\d+$/.test(line));
  assert.equal(shown.length, MAX_PATHS_SHOWN, `${lines.length} lines rendered`);
  assert.ok(lines.includes('    ... and 400 more'), 'the rest is counted, not listed');
  const many = previewText(answerOf('preview-cleanup.json', (b) => {
    const template = groupsOf(b)[0] as Group;
    b.groups = Array.from({ length: 150 }, (_, i) => ({ ...template, pattern: `g${i}-*`, paths: ['C:\\Users\\user\\AppData\\Local\\Temp\\a'] }));
  })).text.split('\n');
  assert.equal(many.filter((line) => /^eligible g\d+-\*: /.test(line)).length, MAX_GROUPS_SHOWN);
  assert.ok(many.includes('... and 50 more group(s) not shown'), 'the groups left out are counted');
});

test('the renderer caps the other lists of a report that lies about its bounds: the allow-list, the configuration errors and the excluded reasons', () => {
  const text = previewText(answerOf('preview-cleanup.json', (b) => {
    b.allowGlobs = Array.from({ length: 150 }, (_, i) => `g${i}-*`);
    b.observeOnly = true;
    b.configurationErrors = Array.from({ length: 130 }, (_, i) => `error-${i}`);
    b.excludedByReason = Array.from({ length: 250 }, (_, i) => ({ reason: `reason-${i}`, count: i + 1 }));
  })).text.split('\n');
  const globs = text.find((line) => line.startsWith('allow-list (temp.allowGlobs): ')) ?? '';
  assert.ok(globs.includes('g99-*, ... and 50 more'), `150 globs rendered whole: ${globs.slice(-60)}`);
  assert.ok(!globs.includes('g100-*'), 'nothing past the cap is listed');
  const errors = text.find((line) => line.startsWith('the configuration is in error (')) ?? '';
  assert.ok(errors.includes('error-99; ... and 30 more)'), `130 errors rendered whole: ${errors.slice(-60)}`);
  assert.ok(!errors.includes('error-100'));
  const excluded = text.filter((line) => /^excluded \d+: reason-/.test(line));
  assert.equal(excluded.length, 100, `${excluded.length} excluded lines rendered`);
  assert.ok(text.includes('... and 150 more excluded reasons not shown'), 'the rest is counted');
});

test('the daemon\'s own "more" and the renderer\'s extra paths add up', () => {
  const lines = previewText(answerOf('preview-cleanup.json', (b) => {
    const first = groupsOf(b)[0] as Group;
    first.paths = Array.from({ length: 130 }, (_, i) => `C:\\Users\\user\\AppData\\Local\\Temp\\coai-${i}`);
    first.more = 20;
  })).text.split('\n');
  assert.ok(lines.includes('    ... and 50 more'), lines.filter((l) => l.includes('more')).join(' | '));
});

test('no literal of the renderer spells the daemon\'s settings verb, the act verb, --confirm or --timer — and no rendered text does', () => {
  const source = fs.readFileSync(path.join(SOURCE_ROOT, 'windowsTasks', 'previewText.ts'), 'utf8');
  const literals = stringLiteralsOf(source);
  assert.ok(literals.length >= 8, 'the scan reads the renderer\'s literals');
  const spelt = literals.filter((literal) => /\bconfig\b|\bact\b|--confirm|--timer/i.test(literal));
  assert.deepEqual(spelt, [], 'a hint that spells the settings command belongs in the README, not in the bundle');
  for (const name of ['preview-cleanup.json', 'preview-cleanup-limited.json', 'preview-cleanup-refused.json']) {
    assert.doesNotMatch(previewText(answerOf(name)).text, /\bconfig\b|--confirm|--timer|\bact A\d+/i, name);
  }
  assert.deepEqual(stringLiteralsOf("const hint = 'wsl-care.exe config set temp.allowGlobs';").filter((l) => /\bconfig\b/.test(l)), ["wsl-care.exe config set temp.allowGlobs"], 'the scan is alive: a planted hint is found');
});
