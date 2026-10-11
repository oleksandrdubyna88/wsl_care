/**
 * The strict fake's A20 — the restore through the root road (plan §15s, E10.S2b) — its OWN copy of the two shapes, held to the ONE
 * shared request `contracts/requests/act-a20-entry-stdin.json` word for word (the request both halves are tested against, §15s D7),
 * and stricter than the daemon, never more permissive:
 *
 * - exactly `act A20 --preview --entry - --json` or `act A20 --confirm --manual --detach --entry - --json` — A20 alone, never beside
 *   another id, never through the cleanup shapes;
 * - stdin: not empty, every line 16 lowercase hex digits and ended, none twice, at most the scenario list's `restoreCeiling`;
 * - the preview answers the daemon's OWN A20 line (`act-a20-preview-action.json`, made by `RestoreAction` in `GoldenContracts`) with
 *   `shown`, the items, the count and the bytes narrowed to the piped ids the scenario's `archive list` holds as verified and removed at
 *   their source — what `RestoreAction.Bound` does. `fakeWsl.test.ts` holds the line over the golden's own entry EQUAL to the golden.
 */
import * as fs from 'node:fs';
import * as path from 'node:path';

/** The shared request, read from the repository the compiled fake lives in. */
const REQUEST = path.resolve(__dirname, '..', '..', '..', '..', 'contracts', 'requests', 'act-a20-entry-stdin.json');

/** The daemon's own A20 line, which every A20 preview answers from. */
export const A20_PREVIEW_ACTION = 'act-a20-preview-action.json';

const ENTRY_ID = /^[0-9a-f]{16}$/;

/** What `RestoreAction.Restorable` takes: an entry whose source is gone, whole or in part. */
const RESTORABLE: ReadonlySet<string> = new Set(['sourceRemoved', 'split']);

type Json = Record<string, unknown>;

function readJson(file: string): Json {
  return JSON.parse(fs.readFileSync(file, 'utf8')) as Json;
}

function words(name: 'preview' | 'confirm'): readonly string[] {
  const value = readJson(REQUEST)[name];

  return Array.isArray(value) ? value.filter((w): w is string => typeof w === 'string') : [];
}

function same(a: readonly string[], b: readonly string[]): boolean {
  return a.length === b.length && a.every((w, i) => w === b[i]);
}

/** The restore shape `tail` is — `preview` or `confirm` — or `undefined` when it is not one (the caller refuses A20 then). */
export function restoreOpOf(tail: readonly string[]): 'preview' | 'confirm' | undefined {
  if (same(tail, words('preview'))) {
    return 'preview';
  }

  return same(tail, words('confirm')) ? 'confirm' : undefined;
}

/** The capabilities each restore shape needs advertised — what `cleanupController.ts` gates on. */
export const RESTORE_CAPABILITIES = {
  preview: ['act.shownList', 'archive.restore', 'act.entryStdin'],
  confirm: ['act.detach', 'archive.restore', 'act.entryStdin'],
} as const;

/** The piped ids, or why the stdin is not a list the daemon takes. */
export function pipedEntries(stdin: Buffer, ceiling: number): readonly string[] | string {
  const text = stdin.toString('utf8');
  if (text.length === 0 || !text.endsWith('\n')) {
    return 'the piped entry list is empty or does not end with a newline';
  }
  const ids = text.split('\n').slice(0, -1);
  const bad = ids.findIndex((line) => !ENTRY_ID.test(line));
  if (bad >= 0) {
    return `line ${bad + 1} of the piped entry list is not an archived entry's id`;
  }

  return new Set(ids).size !== ids.length ? 'the piped entry list names an entry twice' : ids.length > ceiling ? `the piped entry list holds ${ids.length} entries, more than restoreCeiling (${ceiling})` : ids;
}

/** The scenario's `archive list` answer: its entries and its ceiling. */
export function scenarioList(answers: string, file: string): { readonly entries: readonly Json[]; readonly ceiling: number } {
  const list = readJson(path.join(answers, file));
  const entries = Array.isArray(list.entries) ? list.entries.filter((e): e is Json => typeof e === 'object' && e !== null) : [];

  return { entries, ceiling: typeof list.restoreCeiling === 'number' ? list.restoreCeiling : 0 };
}

function restorable(entry: Json): boolean {
  return entry.verified === true && RESTORABLE.has(String(entry.status));
}

/** A20's preview line over the piped ids: the golden line, narrowed as `RestoreAction.Bound` narrows. */
export function restorePreviewLine(answers: string, listFile: string, piped: readonly string[]): Json {
  const golden = readJson(path.join(answers, A20_PREVIEW_ACTION));
  const preview = (golden.preview ?? {}) as Json;
  const template = (Array.isArray(preview.items) ? preview.items[0] : {}) as Json;
  const kept = scenarioList(answers, listFile).entries.filter((e) => piped.includes(String(e.entryId)) && restorable(e));
  const items = kept.map((e) => ({ ...template, name: e.entryId, bytes: e.bytes, note: `${String(e.agent)} ${String(e.month)}, ${String(e.files)} file(s)` }));
  const bytes = kept.reduce((sum, e) => sum + Number(e.bytes ?? 0), 0);
  const what = `restore archived AI-agent sessions as user: ${kept.length} removed at their source and verified; of the ${piped.length} entries the panel showed, the ${kept.length} still restorable`;

  return { ...golden, preview: { ...preview, what, count: kept.length, bytes, facts: { ...((preview.facts ?? {}) as Json), count: kept.length, bytes }, items }, shown: kept.map((e) => e.entryId) };
}
