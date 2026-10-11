import { runWordsOf, type RemovedTable } from '../cleanup/runWords';
import { safeText } from '../panel/format';
import { sizeText } from '../text/format';
import { isJson, list, num, text, type Json } from '../panel/read';
import { blockOf, bytesOf, timeOf } from './logsBlocks';
import type { LogsBlock, LogsLine, LogsTable, ReadState } from './logsView';

/**
 * One run on the Logs page: its line in the run list (from `runs`), and — on expand, or for *This run* — what `runs show
 * <runId>` answered (plan §7.4, §15j M3): its state, every object removed and not removed with type, name, size and note,
 * and every command with its outcome and exit. The daemon's `ActionItem` is `{kind, name, bytes, note}`: an object's image
 * and age are in its note, as the daemon writes them. Read field by field, nothing computed.
 */

/** The run list's columns. */
export const RUN_HEADERS: readonly string[] = ['Time', 'Trigger', 'Dry run', 'Actions', 'Freed', 'Outcome'];

function yesNo(value: unknown): string {
  return typeof value === 'boolean' ? (value ? 'yes' : 'no') : '—';
}

/** The words a single-action run is known by (`cleanup/runWords.ts`: A13, A20, A21) — or undefined for a cleanup action. */
function wordsOf(id: string): string | undefined {
  return runWordsOf([id])?.logs;
}

/** "A4 ran (2)" — and, for an action the words table names (E10.S2c, D8), "A13 ran (3) — archive of AI sessions": the count stays the daemon's. */
function actionText(action: Json): string {
  const named = wordsOf(text(action, 'id'));

  return `${text(action, 'id')} ${text(action, 'status')} (${text(action, 'count')})${named === undefined ? '' : ` — ${named}`}`;
}

/** "A13 — archive of AI sessions" in *This run*'s actions table; a cleanup action by its id. */
function actionName(id: string): string {
  const named = wordsOf(id);

  return named === undefined ? id : `${id} — ${named}`;
}

/** Freed — and, for a dry run, what it would have freed. */
function freedText(run: Json): string {
  const freed = bytesOf(run, 'freedBytes');

  return num(run, 'wouldFreeBytes') === undefined ? freed : `${freed} (would free ${bytesOf(run, 'wouldFreeBytes')})`;
}

function outcomeText(run: Json): string {
  const reason = text(run, 'reason', '');

  return reason === '' ? text(run, 'outcome') : `${text(run, 'outcome')} — ${reason}`;
}

/** One history line as the run list shows it. */
export function runCells(run: Json): string[] {
  return [timeOf(run, 'startedAt'), text(run, 'trigger'), yesNo(run.dryRun), list(run, 'actions').map(actionText).join(', ') || 'none', freedText(run), outcomeText(run)];
}

function line(id: string, label: string, value: string): LogsLine {
  return { id, label, value };
}

function sizeOf(item: Json): string {
  return num(item, 'bytes') === undefined ? '—' : bytesOf(item, 'bytes');
}

function itemRows(items: readonly Json[]): string[][] {
  return items.map((item) => [text(item, 'kind'), text(item, 'name'), sizeOf(item), text(item, 'note', '')]);
}

/** A session's or an agent's size in the unit it reaches — 200 B, never 0.0 GB (the own review's #5, as `runWords.ts`'s done words). */
function smallSizeOf(item: Json): string {
  const bytes = num(item, 'bytes');

  return bytes === undefined ? '—' : sizeText(bytes);
}

/** The removed table of an action the words table names (E10.S2c, D8): its own title and headings over the daemon's name, size and note. */
function namedRemovedTable(id: string, index: number, items: readonly Json[], named: RemovedTable): LogsTable {
  return { id: `removed-${index}`, title: `${id} — ${named.title}`, headers: [...named.headers], rows: items.map((item) => [text(item, 'name'), smallSizeOf(item), text(item, 'note', '')]) };
}

/** The table's words when every item is of the kind they name — else none, so an unexpected item never sits under the wrong heading. */
function namedFor(id: string, items: readonly Json[]): RemovedTable | undefined {
  const named = runWordsOf([id])?.removed;

  return named !== undefined && items.every((item) => text(item, 'kind') === named.kind) ? named : undefined;
}

function removedTable(id: string, index: number, items: readonly Json[]): LogsTable {
  const named = namedFor(id, items);

  return named === undefined ? { id: `removed-${index}`, title: `${id} — removed`, headers: ['Type', 'Name', 'Size', 'Note'], rows: itemRows(items) } : namedRemovedTable(id, index, items, named);
}

function commandRows(commands: readonly Json[]): string[][] {
  return commands.map((command) => [text(command, 'display'), text(command, 'outcome'), text(command, 'exit'), text(command, 'detail', '')]);
}

/** One action of the detail: what it removed, what it did not, the commands it ran — tables named by the action's position. */
function actionTables(action: Json, index: number): LogsTable[] {
  const run = isJson(action.run) ? action.run : {};
  const id = text(action, 'id');

  return [
    removedTable(id, index, list(run, 'removed')),
    { id: `notRemoved-${index}`, title: `${id} — not removed`, headers: ['Type', 'Name', 'Size', 'Why'], rows: itemRows(list(run, 'notRemoved')) },
    { id: `commands-${index}`, title: `${id} — commands and exits`, headers: ['Command', 'Outcome', 'Exit', 'Detail'], rows: commandRows(list(run, 'commands')) },
  ];
}

function actionLine(action: Json, index: number): LogsLine {
  const run = isJson(action.run) ? action.run : {};
  const failure = text(run, 'failure', '');

  return line(`action-${index}`, text(action, 'id'), `${text(action, 'status')} — ${text(action, 'reason')}${failure === '' ? '' : `; failed: ${failure}`}`);
}

/** `runs show`'s detail: its state, and — when the file was read — every action's tables and the run's notes. */
function detailOf(body: Json): Omit<LogsBlock, 'id' | 'title' | 'state'> {
  const state = line('state', 'State', `${text(body, 'state')}${text(body, 'reason', '') === '' ? '' : ` — ${text(body, 'reason')}`}`);
  const detail = body.detail;
  if (!isJson(detail)) {
    return { lines: [state, line('detail', 'Objects', `no detail: ${text(body, 'detailState', 'not answered')}`)], tables: [], notes: [] };
  }
  const actions = list(detail, 'actions');

  return { lines: [state, ...actions.map(actionLine)], tables: actions.flatMap(actionTables), notes: stringsOf(detail.notes) };
}

/** A list of daemon sentences, each through the one sanitiser. */
function stringsOf(value: unknown): string[] {
  return Array.isArray(value) ? value.filter((v): v is string => typeof v === 'string').map((v) => safeText(v)) : [];
}

/** The detail of one expanded run (or of *This run*). */
export function detailBlock(read: ReadState): LogsBlock {
  return blockOf('detail', 'What exactly was removed', read, detailOf);
}

/** *This run*'s own block: `runs show`'s history line, field by field. */
function runOf(body: Json): Omit<LogsBlock, 'id' | 'title' | 'state'> {
  const run = isJson(body.run) ? body.run : {};

  return {
    lines: [
      line('runId', 'Run', text(body, 'runId')),
      line('state', 'State', text(body, 'state')),
      line('trigger', 'Trigger', text(run, 'trigger')),
      line('startedAt', 'Started', timeOf(run, 'startedAt')),
      line('endedAt', 'Ended', timeOf(run, 'endedAt')),
      line('outcome', 'Outcome', text(run, 'outcome')),
      line('dryRun', 'Dry run', yesNo(run.dryRun)),
      line('freed', 'Freed', bytesOf(run, 'freedBytes')),
    ],
    tables: [{ id: 'actions', title: 'Actions', headers: ['Action', 'Status', 'Objects', 'Freed'], rows: list(run, 'actions').map((a) => [actionName(text(a, 'id')), text(a, 'status'), text(a, 'count'), bytesOf(a, 'freedBytes')]) }],
    notes: [],
  };
}

export function thisRunBlocks(read: ReadState): LogsBlock[] {
  return [blockOf('run', 'This run', read, runOf), detailBlock(read)];
}
