import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

import { VERB_NAMES } from '../client/verbs';
import { EXTENSION_ROOT, REPOSITORY_ROOT } from './support/paths';

/**
 * The extension's flow catalogue is DERIVED (`common.scenario-tests` rule 4, plan §15g m6): every client verb, every
 * contributed command and every contributed view must have a row in research/module_tests.md's extension section —
 * a flow added without a row is a red build, not a stale document. Since E5.S2 / E5.S3 the manifest contributes four
 * commands and one view, each with its row; the planted companion shows the check bites for the next one added.
 */

const MODULE_TESTS = path.join(REPOSITORY_ROOT, 'research', 'module_tests.md');
/** A backtick, spelled without one (TypeScript doctrine §2). */
const BACKTICK = String.fromCharCode(96);
const SECTION = '## The extension (`src_vs_code/`)';

interface Contributes {
  readonly commands?: readonly { readonly command: string }[];
  readonly views?: Readonly<Record<string, readonly { readonly id: string }[]>>;
}

/** The flow ids the catalogue must name, from the product's own sources of truth. */
function flowIds(contributes: Contributes): string[] {
  return [
    ...VERB_NAMES.map((verb) => `client ${verb}`),
    ...(contributes.commands ?? []).map((c) => `command ${c.command}`),
    ...Object.values(contributes.views ?? {}).flat().map((v) => `view ${v.id}`),
  ];
}

/** The first cell of every table row inside the extension section. */
function firstCells(markdown: string): string[] {
  const lines = markdown.split(/\r?\n/);
  const start = lines.indexOf(SECTION);
  assert.ok(start >= 0, `research/module_tests.md has no "${SECTION}" heading`);
  const end = lines.findIndex((line, i) => i > start && line.startsWith('## '));

  return lines.slice(start + 1, end < 0 ? undefined : end).filter((l) => l.trimStart().startsWith('|')).map((l) => (l.split('|')[1] ?? '').trim());
}

function missing(ids: readonly string[], cells: readonly string[]): string[] {
  return ids.filter((id) => !cells.some((cell) => cell.startsWith(BACKTICK + id + BACKTICK)));
}

function contributes(): Contributes {
  return (JSON.parse(fs.readFileSync(path.join(EXTENSION_ROOT, 'package.json'), 'utf8')) as { contributes: Contributes }).contributes;
}

test('every client verb, contributed command and view has a row in the extension flow catalogue', () => {
  const cells = firstCells(fs.readFileSync(MODULE_TESTS, 'utf8'));
  assert.deepEqual(missing(flowIds(contributes()), cells), []);
});

test('the derivation sees the verbs (its known instances) and a planted command or view without a row is reported', () => {
  const cells = firstCells(fs.readFileSync(MODULE_TESTS, 'utf8'));
  assert.ok(flowIds({}).includes('client status'));
  const planted: Contributes = { commands: [{ command: 'wslCare.plantedRefresh' }], views: { explorer: [{ id: 'wslCare.plantedPanel' }] } };
  assert.deepEqual(missing(flowIds(planted), cells), ['command wslCare.plantedRefresh', 'view wslCare.plantedPanel']);
});
