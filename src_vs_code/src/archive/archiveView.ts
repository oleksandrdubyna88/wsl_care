import type { ReadOutcome } from '../client/outcome';
import { failureText } from '../failureText';
import { gb, localMinuteOf } from '../text/format';
import { safeText } from '../text/safeText';

/**
 * The AI-session archive's part of the panel (E10.S1, plan §15s D2) — a pure derivation, outside `src/panel/`, of what the daemon
 * answered on the panel's own refresh (`archive status`, `archive preview`), the capabilities its newest `status` advertised,
 * and what the host's flow is doing now. Every state is the DAEMON's (the base folder in force, the run lock, the last run): the
 * flow's own text is only shown while it runs, and the folder it wrote is shown once the daemon reads it back.
 *
 * <p>The retention badge (§15s): an agent whose OWN cleanup deletes sessions sooner than the archive takes them (its retention
 * days below the effective age) loses sessions the archive never sees — said on its line, exactly when that holds.</p>
 */

export type ArchiveAction = 'chooseArchiveFolder' | 'stopArchiving';

export const ARCHIVE_LABELS: { readonly [K in ArchiveAction]: string } = {
  chooseArchiveFolder: 'Choose archive folder…',
  stopArchiving: 'Stop archiving',
};

export type ArchiveLevel = 'none' | 'ok' | 'warn' | 'critical' | 'unknown';

export interface ArchiveAgentLine {
  readonly name: string;
  /** "1 session · 2 files · 0.0 GB older than 14 d", or "nothing older than 14 d". */
  readonly due: string;
  /** "its own cleanup deletes after 30 d", or "no deletion of its own known". */
  readonly retention: string;
  /** '' — or why its own cleanup outruns the archive. */
  readonly badge: string;
}

export interface ArchiveControls {
  readonly line: string;
  readonly level: ArchiveLevel;
  readonly agents: readonly ArchiveAgentLine[];
  /** "No archive run in progress", "Archive run: held by run …". */
  readonly lock: string;
  /** "Last archive run: done — 1 copied, 0 removed, 0.0 GB, <time>", or ''. */
  readonly lastRun: string;
  readonly buttons: readonly { readonly id: ArchiveAction; readonly label: string; readonly enabled: boolean }[];
}

/** What the host holds: the two reads of the last refresh, the advertised capabilities (`undefined`: no status answered yet), the flow's text. */
export interface ArchiveState {
  readonly status: ReadOutcome | undefined;
  readonly preview: ReadOutcome | undefined;
  readonly capabilities: readonly string[] | undefined;
  /** '' — or what the flow is doing now ("Checking the folder…"). */
  readonly busy: string;
}

type Body = Readonly<Record<string, unknown>>;

const TEXT_MAX = 300;

/** The capability every archive button needs: a daemon that judges a base folder knows the key the writer sets. */
export const ARCHIVE_CAPABILITY = 'archive.checkBase';

function isBody(value: unknown): value is Body {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function str(body: unknown, key: string): string {
  const value = isBody(body) ? body[key] : undefined;

  return typeof value === 'string' ? safeText(value, TEXT_MAX) : '';
}

function count(body: unknown, key: string): number {
  const value = isBody(body) ? body[key] : undefined;

  return typeof value === 'number' && Number.isFinite(value) ? value : 0;
}

function bodyOf(outcome: ReadOutcome | undefined): Body | undefined {
  return outcome?.kind === 'read' ? outcome.body : undefined;
}

function plural(n: number, word: string): string {
  return `${n} ${word}${n === 1 ? '' : 's'}`;
}

// ---- the base folder line ----

function folderLine(status: ReadOutcome): { line: string; level: ArchiveLevel } {
  if (status.kind !== 'read') {
    return { line: `Archive: ${failureText(status).label}`, level: 'unknown' };
  }
  const base = str(status.body, 'baseFolder');

  return base === '' ? { line: 'Archive: off — no archive folder is chosen, so aged AI sessions are not archived', level: 'none' } : { line: `Archive folder: ${base}`, level: 'ok' };
}

function headLine(state: ArchiveState): { line: string; level: ArchiveLevel } {
  if (state.busy !== '') {
    return { line: state.busy, level: 'none' };
  }

  return state.status === undefined ? { line: 'Archive: checking…', level: 'none' } : folderLine(state.status);
}

function line(state: ArchiveState): { line: string; level: ArchiveLevel } {
  if (state.capabilities !== undefined && !state.capabilities.includes(ARCHIVE_CAPABILITY)) {
    return { line: 'Archive: this daemon has no AI-session archive — update the daemon', level: 'warn' };
  }

  return headLine(state);
}

// ---- per agent ----

function dueText(agent: Body): string {
  const age = `older than ${count(agent, 'effectiveAgeDays')} d`;
  const units = count(agent, 'dueUnits');

  return units === 0 ? `nothing ${age}` : `${plural(units, 'session')} · ${plural(count(agent, 'dueFiles'), 'file')} · ${gb(count(agent, 'dueBytes'))} ${age}`;
}

function retentionOf(agent: Body): { known: boolean; days: number } {
  const retention = agent.retention;

  return { known: isBody(retention) && retention.known === true, days: count(retention, 'days') };
}

function retentionText(agent: Body): string {
  const retention = retentionOf(agent);

  return retention.known ? `its own cleanup deletes after ${retention.days} d` : 'no deletion of its own known';
}

/** Exactly when the agent's own retention is below the archive's effective age. */
export function retentionBadge(agent: Body): string {
  const retention = retentionOf(agent);
  const age = count(agent, 'effectiveAgeDays');

  return retention.known && retention.days < age ? `its own cleanup deletes sessions after ${retention.days} d — before the archive takes them at ${age} d` : '';
}

function agentLine(agent: Body): ArchiveAgentLine {
  return { name: str(agent, 'name') || str(agent, 'id'), due: dueText(agent), retention: retentionText(agent), badge: retentionBadge(agent) };
}

function agents(preview: Body | undefined): ArchiveAgentLine[] {
  const list = isBody(preview) && Array.isArray(preview.agents) ? preview.agents.filter(isBody) : [];

  return list.filter((agent) => agent.enabled === true).map(agentLine);
}

// ---- the lock and the last run ----

const FREE: ReadonlySet<string> = new Set(['', 'free']);

function lockText(status: Body | undefined): string {
  const lock = status?.lock;
  const held = str(lock, 'state');

  return FREE.has(held) ? 'No archive run in progress' : `Archive run: ${held} (run ${str(lock, 'runId') || 'unknown'})`;
}

function lastRunText(status: Body | undefined): string {
  const last = status?.lastRun;
  if (!isBody(last)) {
    return '';
  }

  return `Last archive run: ${str(last, 'outcome') || 'unknown'} — ${count(last, 'copied')} copied, ${count(last, 'removed')} removed, ${gb(count(last, 'bytes'))}, ${localMinuteOf(str(last, 'endedUtc'))}`;
}

// ---- the buttons ----

function buttons(state: ArchiveState): ArchiveControls['buttons'] {
  const may = state.capabilities?.includes(ARCHIVE_CAPABILITY) === true && state.busy === '';
  const base = str(bodyOf(state.status), 'baseFolder');

  return [
    { id: 'chooseArchiveFolder', label: ARCHIVE_LABELS.chooseArchiveFolder, enabled: may },
    { id: 'stopArchiving', label: ARCHIVE_LABELS.stopArchiving, enabled: may && base !== '' },
  ];
}

/** The archive's controls. Pure. */
export function deriveArchive(state: ArchiveState): ArchiveControls {
  const status = bodyOf(state.status);

  return { ...line(state), agents: agents(bodyOf(state.preview)), lock: lockText(status), lastRun: lastRunText(status), buttons: buttons(state) };
}

/** The controls before the host derived any: every button greyed. */
export const NO_ARCHIVE: ArchiveControls = deriveArchive({ status: undefined, preview: undefined, capabilities: undefined, busy: '' });
