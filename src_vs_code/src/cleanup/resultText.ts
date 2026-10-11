import { rootFailureText } from '../root/rootFailureText';
import type { HandOffOutcome, RootFailure } from '../root/rootOutcome';
import { gb, minuteOf, sizeText } from '../text/format';
import { safeText } from '../text/safeText';
import type { JournalEntry, JournalOp } from './journal';
import { isArchiveOnly, isRelayOnly } from './modalText';
import type { RunShow } from './runAnswers';
import type { RunResult } from './runFollower';

/**
 * The words the panel and its notifications use for a cleanup's hand-off and its end (E6.S3): one sentence and a level per
 * outcome, every daemon string through the one sanitiser. A refusal's words are `root/rootFailureText.ts`'s — one table,
 * one place.
 */

/** The label an archive run is told by (E10.S1b): never "Cleaning A13". */
export const ARCHIVE_RUN = 'the archive run';

/** The label a relay stop is told by (S7b.2b): never "Cleaning A21". */
export const RELAY_RUN = 'the relay stop';

/** What a run of these ids is told as: the archive run (A13 alone), the relay stop (A21 alone), or a cleanup of its ids. */
type RunWords = 'clean' | 'archive' | 'relays';

function wordsOf(actions: readonly string[]): RunWords {
  if (isRelayOnly(actions)) {
    return 'relays';
  }

  return isArchiveOnly(actions) ? 'archive' : 'clean';
}

export type NoticeLevel = 'info' | 'warn' | 'error';

export interface Notice {
  readonly level: NoticeLevel;
  readonly sentence: string;
}

const REASON_MAX = 200;

function reason(text: string): string {
  return safeText(text, REASON_MAX);
}

/** What a hand-off (a confirm, a full check, a stop) answered, as the person is told at once. */
type HandOffKind = 'accepted' | 'acceptedObserved' | 'outcomeUnknown' | 'stopping';
type HandOffWords = { readonly [K in HandOffKind]: (outcome: Extract<HandOffOutcome, { kind: K }>, label: string, op: JournalOp) => Notice };

const HAND_OFF: HandOffWords = {
  accepted: (o, label, op) => taken(o.runId, label, op),
  acceptedObserved: (o, label, op) => (o.runId === undefined ? unseen(label, 'it was seen in flight without its run id') : taken(o.runId, label, op)),
  outcomeUnknown: (o, label) => unseen(label, o.reason),
  stopping: (o) => ({ level: 'info', sentence: `Stopping run ${o.runId}: the daemon stops its unit; the run records itself interrupted.` }),
};

export function handOffNotice(outcome: HandOffOutcome, label: string, op: JournalOp = 'clean'): Notice {
  if (!Object.hasOwn(HAND_OFF, outcome.kind)) {
    return { level: 'error', sentence: rootFailureText(outcome as RootFailure).sentence };
  }
  const words = HAND_OFF[outcome.kind as HandOffKind] as (o: HandOffOutcome, label: string, op: JournalOp) => Notice;

  return words(outcome, label, op);
}

/** What a hand-off is called: the full check, the archive run (E10.S1b), or the cleanup of its ids. */
function doing(label: string, op: JournalOp): string {
  if (op === 'fullCheck') {
    return 'The full check';
  }

  if (label === RELAY_RUN) {
    return 'The relay stop';
  }

  return label === ARCHIVE_RUN ? 'The archive run' : `Cleaning ${label}`;
}

function taken(runId: string, label: string, op: JournalOp): Notice {
  const what = doing(label, op);

  return { level: 'info', sentence: `${what}: the daemon took it as run ${runId}; the panel follows it to its result.` };
}

function unseen(label: string, why: string): Notice {
  return { level: 'warn', sentence: `The daemon's answer to ${label} was not seen (${reason(why)}); the panel follows it from the daemon's own records.` };
}

/** "A4" — or the full check, or the archive run (E10.S1b: an entry of A13 alone), by name. */
function labelOf(entry: JournalEntry): string {
  if (entry.op === 'fullCheck') {
    return 'full check';
  }

  return { archive: ARCHIVE_RUN, relays: RELAY_RUN, clean: entry.actions.join(', ') }[wordsOf(entry.actions)];
}

/**
 * What a done run did, in its own words: a cleanup frees and removes, its figure in GB as `docker system df` prints it; the archive
 * run (E10.S1b) MOVES out of the agents' folders, its figure in the unit it reaches — a 200-byte backlog reads 200 B, never
 * 0.0 GB (own review #3, the second code round #3).
 */
const DID = {
  clean: { freed: 'freed', unknown: 'freed an unknown amount', objects: 'objects removed', size: gb },
  archive: { freed: 'moved', unknown: 'moved an unknown amount', objects: 'objects archived', size: sizeText },
} as const;

function freedText(bytes: number | undefined, did: (typeof DID)[keyof typeof DID]): string {
  return bytes === undefined ? did.unknown : `${did.freed} ${did.size(bytes)}`;
}

/**
 * What a done relay stop did (S7b.2b): how many relays it ended — their Windows programs end with them — and, when A21 itself
 * failed, that some did not end on SIGTERM and keep their Windows programs running. Never "freed": A21 frees no disk.
 */
function relaysDone(name: string, show: RunShow): string {
  const action = show.line?.actions.find((a) => a.id === 'A21');
  const ended = action === undefined ? 0 : action.count;

  return `${name} is done: ended ${ended} relay${ended === 1 ? '' : 's'}; their Windows programs end with them.${survivorsOf(action)}`;
}

function survivorsOf(action: { readonly status: string } | undefined): string {
  return action !== undefined && action.status === 'failed' ? ' Some relays did not end on SIGTERM; their Windows programs keep running (runs show names them).' : '';
}

/** A done run's sentence in its own words: the relay stop's (S7b.2b), or the cleanup's / the archive run's. */
function sentenceOf(name: string, show: RunShow, words: RunWords): string {
  return words === 'relays' ? relaysDone(name, show) : doneSentence(name, show, words === 'archive');
}

function doneSentence(name: string, show: RunShow, archive: boolean): string {
  const line = show.line;
  if (line === undefined) {
    return `${name} is done.`;
  }
  const did = DID[archive ? 'archive' : 'clean'];
  const removed = line.actions.reduce((sum, a) => sum + a.count, 0);
  const freed = freedText(line.freedBytes, did);

  return `${name} ${line.outcome === 'completed' ? 'is done' : `ended ${reason(line.outcome)}`}: ${freed}, ${removed} ${did.objects}.`;
}

type ByState = { readonly [K in 'done' | 'refused' | 'interrupted' | 'unknown' | 'queued' | 'running']: (name: string, show: RunShow, runId: string, words: RunWords) => Notice };

const BY_STATE: ByState = {
  done: (name, show, _runId, words) => ({ level: show.line === undefined || show.line.outcome === 'completed' ? 'info' : 'warn', sentence: sentenceOf(name, show, words) }),
  refused: (name, show) => ({ level: 'warn', sentence: `${name} was refused: ${reason(show.reason || (show.line?.reason ?? ''))}` }),
  interrupted: (name, show) => ({ level: 'warn', sentence: `${name} was interrupted: ${reason(show.reason || (show.line?.reason ?? ''))}` }),
  unknown: (_name, show, runId) => ({ level: 'warn', sentence: `The daemon does not know run ${runId}: ${reason(show.reason)}` }),
  queued: (name) => ({ level: 'info', sentence: `${name} is queued.` }),
  running: (name) => ({ level: 'info', sentence: `${name} is running.` }),
};

function runNotice(entry: JournalEntry, show: RunShow): Notice {
  const runId = entry.kind === 'run' ? entry.runId : (show.runId ?? '');
  const name = `Run ${runId} (${labelOf(entry)})`;

  return show.state.kind === 'known' ? BY_STATE[show.state.value](name, show, runId, wordsOf(entry.actions)) : { level: 'warn', sentence: `${name} is in a state this extension does not know: ${show.state.label}` };
}

const CONFIRMED_AS: { readonly [K in 'fullCheck' | RunWords]: (entry: JournalEntry) => string } = {
  fullCheck: () => 'The full check',
  archive: () => 'The archive run',
  relays: () => 'The relay stop',
  clean: (entry) => `The cleanup of ${labelOf(entry)}`,
};

function confirmed(entry: JournalEntry): string {
  const kind = entry.op === 'fullCheck' ? 'fullCheck' : wordsOf(entry.actions);

  return `${CONFIRMED_AS[kind](entry)} confirmed at ${minuteOf(entry.since)}`;
}

type Results = { readonly [K in RunResult['kind']]: (result: Extract<RunResult, { kind: K }>, ceilingMs: number) => Notice };

const RESULTS: Results = {
  run: (r) => runNotice(r.entry, r.show),
  ceiling: (r, ceilingMs) => ({ level: 'warn', sentence: `Run ${r.entry.kind === 'run' ? r.entry.runId : '(no run id)'} (${labelOf(r.entry)}): state unknown — no answer that it ended within ${Math.round(ceilingMs / 60_000)} minutes; the daemon's runs show says where it is.` }),
  unreadable: (r) => ({ level: 'warn', sentence: `Run ${r.entry.kind === 'run' ? r.entry.runId : '(no run id)'} (${labelOf(r.entry)}): state unknown — the record could not be read (${reason(r.reason)}); the daemon's runs show says where it is.` }),
  neverRan: (r) => ({ level: 'info', sentence: `${confirmed(r.entry)} never ran: the daemon recorded no run of it.` }),
  ambiguous: (r) => ({ level: 'warn', sentence: `${confirmed(r.entry)} cannot be told apart: runs ${r.candidates.join(', ')} each match; see them under Last cleanup.` }),
};

/** A followed run's terminal answer, as it is shown — once. */
export function resultNotice(result: RunResult, ceilingMs: number): Notice {
  const words = RESULTS[result.kind] as (r: RunResult, ceilingMs: number) => Notice;

  return words(result, ceilingMs);
}
