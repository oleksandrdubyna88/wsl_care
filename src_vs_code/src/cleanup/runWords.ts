import { sizeText } from '../text/format';

/**
 * The words of a run of ONE named action (E10.S2b) — *Archive now*'s A13, *Restore*'s A20 and *Stop client-gone relays*' A21 — in ONE table, so a single-action run
 * is told in its own words everywhere a cleanup would be: the modal's confirm label, the hand-off and terminal notices, the in-flight
 * state of the cleanup controls, and the Logs page's run rows (E10.S2c). Before this table the A13 words were chosen by an
 * `isArchiveOnly` check at three sites (`modalText.ts`, `resultText.ts`, `cleanupView.ts`), and S7b.2b had added an `isRelayOnly` beside
 * it at each; A20 and A21 are rows here, never more copies of that decision (reuse-first: widen the existing thing). Root-free: it names ids as text and imports nothing under `root/`.
 */

export interface RunWords {
  /** The one action id the run holds. */
  readonly id: 'A13' | 'A20' | 'A21';
  /** "the archive run" — inside a sentence. */
  readonly label: string;
  /** "The archive run" — at a sentence's start. */
  readonly name: string;
  /** What a live run of it reads in the cleanup controls. */
  readonly inFlight: string;
  /** The modal's one confirming button. */
  readonly confirm: string;
  /** What a done run did, from its history line: the bytes the line answered (if any) and the count of objects. */
  readonly done: (bytes: number | undefined, count: number) => string;
  /** E10.S2c (D8): what the Logs page calls the run beside its id — "A13 ran (3) — AI sessions archived". */
  readonly logs: string;
  /** E10.S2c (D8): the run detail's *removed* table when it is not a list of removed objects — its title after the id, and its
   *  headings over the daemon's name, size and note. Absent: the generic table. */
  readonly removed?: RemovedTable;
}

/** A run detail's *removed* table, named for what its items are. */
export interface RemovedTable {
  readonly title: string;
  readonly headers: readonly [string, string, string];
}

function plural(n: number, word: string): string {
  return `${n} ${word}${n === 1 ? '' : 's'}`;
}

const WORDS: { readonly [K in RunWords['id']]: RunWords } = {
  A13: {
    id: 'A13',
    label: 'the archive run',
    name: 'The archive run',
    inFlight: 'Archiving… the aged AI sessions',
    confirm: 'Archive',
    // A 200-byte backlog reads 200 B, never 0.0 GB (E10.S1b own review #3).
    done: (bytes, count) => `${bytes === undefined ? 'moved an unknown amount' : `moved ${sizeText(bytes)}`}, ${count} objects archived`,
    logs: 'AI sessions archived',
    // The run's removed items are AGENTS (`ArchiveAction`: per agent the sessions copied and removed, the bytes moved) — §15q R9.
    removed: { title: 'AI sessions archived, per agent', headers: ['Agent', 'Moved', 'What the archive did'] },
  },
  A20: {
    id: 'A20',
    label: 'the restore run',
    name: 'The restore run',
    inFlight: 'Restoring… archived sessions',
    confirm: 'Restore',
    // A restore frees nothing (`RestoreAction`: "a restore frees nothing: it puts archived sessions back") — its figure is the count.
    done: (_bytes, count) => `${plural(count, 'archived session')} restored`,
    logs: 'archived sessions restored',
    // The run's removed items are the sessions put back, by entry id (`RestoreAction`).
    removed: { title: 'archived sessions restored', headers: ['Archived session', 'Size', 'Agent, month and outcome'] },
  },
  A21: {
    id: 'A21',
    label: 'the relay stop',
    name: 'The relay stop',
    inFlight: 'Stopping… the client-gone relays',
    confirm: 'Stop relays',
    // A21 frees no disk (S7b.2b): its figure is the relays it ended; the relay stop's own sentence adds what follows from it.
    done: (_bytes, count) => `ended ${plural(count, 'relay')}`,
    logs: 'client-gone relays stopped',
  },
};

/** The words of a run of exactly one of the table's ids — or `undefined` for every other run (a cleanup, a full check). */
export function runWordsOf(ids: readonly string[]): RunWords | undefined {
  const [only] = ids;

  return ids.length === 1 && only !== undefined && Object.hasOwn(WORDS, only) ? WORDS[only as RunWords['id']] : undefined;
}

/** The words whose `label` is `label` — how a hand-off told by its label finds its name. */
export function runWordsByLabel(label: string): RunWords | undefined {
  return Object.values(WORDS).find((words) => words.label === label);
}

/** The table's rows — what the tests walk. */
export const RUN_WORDS: readonly RunWords[] = Object.values(WORDS);
