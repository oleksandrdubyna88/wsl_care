import type { VerbOutcome } from '../client/outcome';
import { failureText } from '../failureText';
import type { Snapshot } from '../state/outcomeStore';
import { FIELD_MAP, isArriving, SECTIONS, type FieldRow, type ReadRow } from './fieldMap';
import { at, unavailableAncestor } from './jsonPath';
import { isUnavailable, unavailableText } from './read';
import { RENDERERS, type Rendered } from './rowRenderers';
import type { CleanupControls, PanelView, ViewLevel, ViewRow, ViewSection } from './view';

/**
 * The panel's view, a pure function of the store's snapshot and `FIELD_MAP` — so what the page shows is exactly what
 * the table (and the document generated from it) says. For each row, in this order:
 *
 * 1. it `arrives` in a later epic → "arrives in E# — why";
 * 2. its verb was not asked yet → "checking…";
 * 3. its verb failed (stopped, not installed, a newer schema …) → that failure's short state — per verb, so an unknown
 *    `preview` major blanks only the rows read from `preview` (plan §6);
 * 4. its path is absent from the answer → "update the daemon to see this" (the compatibility rule: never 0) — unless an
 *    ANCESTOR on the path was answered `available: false` (`vm`, `vm.memory`), which then reads as 5 with ITS reason;
 * 5. the figure is `available: false` → "unavailable — <reason>";
 * 6. otherwise its kind's renderer.
 */

const MISSING = 'update the daemon to see this';

const PLAIN: Omit<ViewRow, 'id' | 'label' | 'value'> = { state: 'value', level: 'none', headers: [], items: [] };

function row(field: FieldRow, rendered: Rendered): ViewRow {
  return { ...PLAIN, ...rendered, id: field.id, label: field.label };
}

/** A path with no value: 'unavailable — <reason>' when an ancestor was answered unavailable (it then has no children),
 * else 'update the daemon to see this' — the field is genuinely absent, an older daemon. */
function absent(body: unknown, path: string): Rendered {
  const parent = unavailableAncestor(body, path);

  return parent === undefined ? { value: MISSING, state: 'missing' } : { value: unavailableText(parent), state: 'unavailable' };
}

function fromAnswer(field: ReadRow, outcome: Extract<VerbOutcome, { kind: 'answered' }>): Rendered {
  const body = 'body' in outcome.answer ? outcome.answer.body : undefined;
  const value = at(body, field.path);
  if (value === undefined) {
    return absent(body, field.path);
  }

  return isUnavailable(value) ? { value: unavailableText(value), state: 'unavailable' } : RENDERERS[field.kind](value);
}

function fromOutcome(field: ReadRow, outcome: VerbOutcome | undefined): Rendered {
  if (outcome === undefined) {
    return { value: 'checking…', state: 'checking' };
  }

  return outcome.kind === 'answered' ? fromAnswer(field, outcome) : { value: failureText(outcome).label, state: 'blocked' };
}

function rowFor(field: FieldRow, snapshot: Snapshot): ViewRow {
  if (isArriving(field)) {
    return row(field, { value: `arrives in ${field.arrives} — ${field.why}`, state: 'arrives' });
  }

  return row(field, fromOutcome(field, snapshot[field.verb]));
}

function sections(snapshot: Snapshot): ViewSection[] {
  return SECTIONS.map((section) => ({ id: section.id, title: section.title, rows: FIELD_MAP.filter((f) => f.section === section.id).map((f) => rowFor(f, snapshot)) }));
}

function distroOf(snapshot: Snapshot): string {
  const status = snapshot.status;
  return status !== undefined && status.kind === 'answered' ? ` — ${status.distro}` : '';
}

function failureNotice(status: Exclude<VerbOutcome, { kind: 'answered' }>): { notice: string; noticeLevel: ViewLevel } {
  return { notice: failureText(status).sentence, noticeLevel: status.kind === 'stopped' ? 'warn' : 'critical' };
}

/** The sentence above the sections: the status failure's, or "asking", or nothing. */
function notice(snapshot: Snapshot): { notice: string; noticeLevel: ViewLevel } {
  const status = snapshot.status;
  if (status !== undefined && status.kind !== 'answered') {
    return failureNotice(status);
  }

  return { notice: snapshot.checking ? 'Asking the daemon…' : '', noticeLevel: 'none' };
}

/** The button a status failure offers first: start a stopped distribution, or install a missing daemon (E5.S3). */
const FIRST_ACTION: Partial<Record<VerbOutcome['kind'], PanelView['actions'][number]>> = {
  stopped: { id: 'startWsl', label: 'Start WSL and check' },
  notInstalled: { id: 'installDaemon', label: 'Install daemon' },
};

function actions(snapshot: Snapshot): PanelView['actions'] {
  const first = snapshot.status === undefined ? undefined : FIRST_ACTION[snapshot.status.kind];

  return [...(first === undefined ? [] : [first]), { id: 'refresh', label: 'Refresh' }, { id: 'openSettings', label: 'Settings' }];
}

/** The controls before the host has derived any (the page greys every cleanup button). */
export const NO_CLEANUP: CleanupControls = { enabled: false, state: '', stateLevel: 'none', reason: 'checking…', rows: [], fullCheck: false, stop: undefined, stopText: '', results: [], dockerAfter: '' };

/** `cleanup` is the host's derivation (`cleanup/cleanupView.ts`) — this module reads nothing of the root paths. */
export function buildPanelView(snapshot: Snapshot, cleanup: CleanupControls = NO_CLEANUP): PanelView {
  return { heading: `AI OS Care${distroOf(snapshot)}`, ...notice(snapshot), actions: actions(snapshot), sections: sections(snapshot), cleanup };
}
