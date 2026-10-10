import type { Failure, JsonObject, ReadOutcome } from '../client/outcome';
import type { RunRead } from '../client/verbs';
import type { Modal } from '../cleanup/modalText';
import type { NoticeLevel } from '../cleanup/resultText';
import { callConfig, type ConfigOp, type ConfigOutcome, type ConfigTarget } from '../config/configCall';
import { failureText } from '../failureText';
import type { Runner } from '../process/runner';
import type { Numbers } from '../settings/numbers';
import type { DaemonLimits } from '../shared/daemonLimits';
import { safeText } from '../text/safeText';
import { judgedFolderOf, type JudgedFolder } from './judgedFolder';

/**
 * The archive's two flows (E10.S1, plan §15s), and the ONE importer of the user-layer writer (`structure.test.ts`):
 *
 * - *Choose archive folder…* — the person picks a folder; the daemon JUDGES it (`archive check-base <path>`, unprivileged, by the
 *   process that will write the archive); a refusal is told with its rule and nothing is written; an accepted folder is shown in
 *   a modal with the daemon's warnings, and only *Use this folder* writes it — the folder AS THE DAEMON SPELT IT in its report
 *   (`JudgedFolder`), never the picked text.
 * - *Stop archiving* — a modal, then the empty value. Nothing already archived is touched.
 *
 * Neither starts anything else: no archive run, no move. The panel shows the daemon's own reading of the key after each write
 * (`archiveHost.ts` reads `archive status` again), never the value this flow believes it wrote.
 */

/** The writer's target, re-exported: nothing but this flow imports `config/configCall.ts` (`structure.test.ts`). */
export type { ConfigTarget } from '../config/configCall';

export interface ArchiveUi {
  /** The folder the person picked (a Windows path), or `undefined` when the dialog was dismissed. */
  readonly pickFolder: () => Promise<string | undefined>;
  readonly confirm: (modal: Modal) => Promise<boolean>;
  readonly notify: (level: NoticeLevel, sentence: string) => Promise<void>;
}

export interface ArchiveFlowDeps {
  readonly ui: ArchiveUi;
  readonly read: (request: RunRead) => Promise<ReadOutcome>;
  /** The validated, listed, RUNNING distribution — or why not (`WslCareClient.rootTarget()`: the target checks, no `-u`). */
  readonly target: () => Promise<ConfigTarget | Failure>;
  readonly runner: Runner;
  readonly numbers: () => Numbers;
  readonly limits: () => DaemonLimits;
  /** What the flow is doing now ('' when nothing), for the panel's line. */
  readonly busy: (text: string) => void;
}

/** `unknown`: the write's call timed out — the daemon may have written the key before it was stopped (code round #4). */
export type ArchiveFlowOutcome = 'cancelled' | 'refused' | 'failed' | 'unknown' | 'written';

export const USE_FOLDER_LABEL = 'Use this folder';
export const STOP_LABEL = 'Stop archiving';

const TEXT_MAX = 200;

function said(report: JsonObject, key: string): string {
  const value = report[key];

  return typeof value === 'string' ? safeText(value, TEXT_MAX) : '';
}

function saidList(report: JsonObject, key: string): string[] {
  const value = report[key];

  return Array.isArray(value) ? value.filter((v): v is string => typeof v === 'string').map((v) => safeText(v, TEXT_MAX)) : [];
}

/** `showOpenDialog`'s `fsPath` writes the drive letter in lower case (`v:\…`); the folder is asked about as Windows spells it. Pure. */
export function pickedPath(fsPath: string): string {
  return /^[a-z]:/.test(fsPath) ? fsPath.charAt(0).toUpperCase() + fsPath.slice(1) : fsPath;
}

/** The modal of an ACCEPTED folder: where it is, how the daemon will write it, and its warnings. Pure. */
export function useFolderModal(report: JsonObject, folder: JudgedFolder): Modal {
  const warnings = saidList(report, 'warnings').map((w) => `Warning: ${w}`);
  const notes = saidList(report, 'notes');

  return {
    message: `Archive aged AI sessions into ${said(report, 'given') || safeText(folder, TEXT_MAX)}?`,
    detail: [`The daemon accepted it and will write to ${safeText(folder, TEXT_MAX)}.`, ...warnings, ...notes, 'Nothing is moved now: a session is archived only once it is older than its agent\'s effective age.'].join('\n'),
    confirm: USE_FOLDER_LABEL,
  };
}

export const STOP_MODAL: Modal = {
  message: 'Stop archiving AI sessions?',
  detail: 'The archive folder setting is cleared. Nothing already archived is touched or moved back.',
  confirm: STOP_LABEL,
};

/** A refused folder's sentence: the daemon's rule and its words. Pure. */
export function refusedSentence(report: JsonObject): string {
  return `That folder cannot hold the archive (${said(report, 'rule') || 'refused'}): ${said(report, 'refusal') || 'the daemon gave no reason'}. Nothing was written.`;
}

const WRITTEN: { readonly [K in ConfigOp['op']]: (op: Extract<ConfigOp, { op: K }>) => string } = {
  setBaseFolder: (op) => `Aged AI sessions will be archived into ${safeText(op.folder, TEXT_MAX)}.`,
  clearBaseFolder: () => 'Archiving stopped: no archive folder is set. Nothing already archived was touched.',
};

async function told(deps: ArchiveFlowDeps, op: ConfigOp, outcome: ConfigOutcome): Promise<ArchiveFlowOutcome> {
  if (outcome.kind === 'written') {
    await deps.ui.notify('info', (WRITTEN[op.op] as (op: ConfigOp) => string)(op));
    return 'written';
  }
  if (outcome.kind === 'timedOut') {
    await deps.ui.notify('warn', `The archive setting may or may not have been written: the daemon did not answer within ${Math.round(outcome.timeoutMs / 1000)} s. The panel shows the folder the daemon reads now.`);
    return 'unknown';
  }
  await deps.ui.notify('error', `The archive setting was not written: ${failureText(outcome).sentence}`);

  return 'failed';
}

async function writeOnce(deps: ArchiveFlowDeps, op: ConfigOp): Promise<ConfigOutcome> {
  const target = await deps.target();

  return 'kind' in target ? target : callConfig(deps.runner, target, op, deps.numbers(), deps.limits());
}

async function write(deps: ArchiveFlowDeps, op: ConfigOp): Promise<ArchiveFlowOutcome> {
  deps.busy('Writing the archive setting…');
  try {
    return await told(deps, op, await writeOnce(deps, op));
  } finally {
    deps.busy('');
  }
}

async function judged(deps: ArchiveFlowDeps, report: JsonObject): Promise<ArchiveFlowOutcome> {
  const folder = judgedFolderOf(report);
  if (folder === undefined) {
    await deps.ui.notify('warn', refusedSentence(report));
    return 'refused';
  }

  return (await deps.ui.confirm(useFolderModal(report, folder))) ? write(deps, { op: 'setBaseFolder', folder }) : 'cancelled';
}

async function checkOnce(deps: ArchiveFlowDeps, path: string): Promise<ReadOutcome> {
  deps.busy('Checking the folder…');
  try {
    return await deps.read({ read: 'archiveCheckBase', path });
  } finally {
    deps.busy('');
  }
}

async function checked(deps: ArchiveFlowDeps, path: string): Promise<ArchiveFlowOutcome> {
  const outcome = await checkOnce(deps, path);
  if (outcome.kind === 'read') {
    return judged(deps, outcome.body);
  }
  await deps.ui.notify('error', `The folder could not be checked: ${failureText(outcome).sentence}`);

  return 'failed';
}

/** *Choose archive folder…*. */
export async function chooseArchiveFolder(deps: ArchiveFlowDeps): Promise<ArchiveFlowOutcome> {
  const picked = await deps.ui.pickFolder();

  return picked === undefined ? 'cancelled' : checked(deps, pickedPath(picked));
}

/** *Stop archiving*. */
export async function stopArchiving(deps: ArchiveFlowDeps): Promise<ArchiveFlowOutcome> {
  return (await deps.ui.confirm(STOP_MODAL)) ? write(deps, { op: 'clearBaseFolder' }) : 'cancelled';
}
