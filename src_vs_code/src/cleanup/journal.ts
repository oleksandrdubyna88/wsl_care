import { randomUUID } from 'node:crypto';

import { FULL_CHECK_ACTIONS } from '../root/cleanupController';
import { actionIdOf, runIdOf, type RunId } from '../root/rootIds';

/**
 * The host's DURABLE memory of the cleanups this extension started (E6.S3, plan §15k #4, `common.durable-status` rules 1
 * and 4): one entry per run that has not yet had a terminal answer SHOWN, kept in VS Code's `globalState` — so a reload, a
 * crash of the extension host or a closed window loses nothing, and the next load follows the run to its end.
 *
 * <p>Two kinds of entry:</p>
 * <ul>
 *   <li><b>run</b> — the daemon named the run id (accepted, observed, or unknown WITH a run id): followed through
 *       `status.running`, then ONE `runs show` once it is no longer in flight;</li>
 *   <li><b>unresolved</b> — written BEFORE the confirm's call goes out (rule 1: the in-flight state is persisted before the
 *       work starts), and left as it is when the controller hands back `outcomeUnknown` with NO run id: the next load
 *       resolves it from the daemon's own records (the coai E6.S2 plan round #3 contract).</li>
 * </ul>
 *
 * <p>Read as untrusted on every load: `globalState` is a file another build may have written. An entry that does not
 * validate — an unknown kind, a run id the daemon would never write, an action this build does not know, an instant that
 * does not parse — is dropped, never a crash. <b>Growth:</b> at most `MAX_ENTRIES` (32, the daemon's own request budget);
 * an entry leaves on its terminal answer, on the 30-minute poll ceiling (`runFollower.ts`) or, past the cap, oldest first.</p>
 */

/** The `globalState` key. The `.v1` is the shape: a later shape gets a new key, and this one is then read as nothing. */
export const JOURNAL_KEY = 'wslCare.cleanup.journal.v1';

/** The daemon accepts at most 32 waiting requests (plan §15k #8); more entries than that cannot all be real. */
export const MAX_ENTRIES = 32;

/** What started the run: a cleanup's confirm, *Run full check now*, or a stop. */
export const JOURNAL_OPS = ['clean', 'fullCheck', 'stop'] as const;

export type JournalOp = (typeof JOURNAL_OPS)[number];

interface EntryBase {
  /** This entry's own id — what `replace` and `remove` name. */
  readonly id: string;
  readonly op: JournalOp;
  readonly distro: string;
  /** The confirmed action ids (FULL_CHECK_ACTIONS for a full check, as `status.running` names it). */
  readonly actions: readonly string[];
  /** When the call went out, UTC, ISO 8601 — the start of the window a resolution reads, and of the poll ceiling. */
  readonly since: string;
}

export type JournalEntry = (EntryBase & { readonly kind: 'unresolved' }) | (EntryBase & { readonly kind: 'run'; readonly runId: RunId });

/** The part of `vscode.Memento` the journal uses. */
export interface DurableStore {
  get(key: string): unknown;
  update(key: string, value: unknown): PromiseLike<void>;
}

/** An entry as a caller hands it in: everything but its id. */
export type NewEntry = Omit<EntryBase, 'id'> & ({ readonly kind: 'unresolved' } | { readonly kind: 'run'; readonly runId: RunId });

export class CleanupJournal {
  private readonly listeners = new Set<() => void>();

  constructor(private readonly store: DurableStore) {}

  /** Every valid entry, oldest first, read from the store each time (another window may have written it). */
  entries(): readonly JournalEntry[] {
    const stored = this.store.get(JOURNAL_KEY);

    return Array.isArray(stored) ? stored.flatMap(entryOf) : [];
  }

  /** Persist a new entry; resolves once the store has it — the caller starts the work only then. */
  async add(entry: NewEntry): Promise<JournalEntry> {
    const added: JournalEntry = { ...entry, id: randomUUID() };
    await this.write([...this.entries(), added].slice(-MAX_ENTRIES));

    return added;
  }

  /** Replace the entry `id` with `next` (keeping the id), e.g. an unresolved confirm that got its run id. */
  async replace(id: string, next: NewEntry): Promise<void> {
    const entries = this.entries();
    if (entries.some((e) => e.id === id)) {
      await this.write(entries.map((e) => (e.id === id ? { ...next, id } : e)));
    }
  }

  /** Remove the entry `id` — its terminal answer was shown. */
  async remove(id: string): Promise<void> {
    const entries = this.entries();
    if (entries.some((e) => e.id === id)) {
      await this.write(entries.filter((e) => e.id !== id));
    }
  }

  private async write(entries: readonly JournalEntry[]): Promise<void> {
    await this.store.update(JOURNAL_KEY, entries);
    this.listeners.forEach((listener) => listener());
  }

  /** Called after every change this journal made; the returned function unsubscribes. */
  onChange(listener: () => void): () => void {
    this.listeners.add(listener);
    return () => { this.listeners.delete(listener); };
  }
}

type Raw = Readonly<Record<string, unknown>>;

function isRaw(value: unknown): value is Raw {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** An instant as the journal writes it — `Date.toISOString()` — and nothing looser. */
const ISO = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/;

function isAction(value: unknown): value is string {
  return actionIdOf(value) !== undefined || FULL_CHECK_ACTIONS.includes(value as string);
}

function isActionList(value: unknown): value is readonly string[] {
  return Array.isArray(value) && [value.length > 0, value.length <= MAX_ENTRIES, value.every(isAction)].every(Boolean);
}

function isText(value: unknown, max: number): value is string {
  return typeof value === 'string' && value.length > 0 && value.length <= max;
}

/** The checks every stored entry must pass, one per field. */
const FIELD_CHECKS: readonly ((raw: Raw) => boolean)[] = [
  (raw) => isText(raw.id, 64),
  (raw) => isText(raw.distro, 64),
  (raw) => isText(raw.since, 32) && ISO.test(raw.since),
  (raw) => JOURNAL_OPS.some((op) => op === raw.op),
  (raw) => isActionList(raw.actions),
];

/** The fields every entry carries, validated — or undefined. */
function baseOf(raw: Raw): EntryBase | undefined {
  return FIELD_CHECKS.every((check) => check(raw)) ? { id: raw.id as string, op: raw.op as JournalOp, distro: raw.distro as string, actions: raw.actions as readonly string[], since: raw.since as string } : undefined;
}

type Kinds = { readonly [K in JournalEntry['kind']]: (base: EntryBase, raw: Raw) => JournalEntry[] };

const KINDS: Kinds = {
  unresolved: (base) => [{ ...base, kind: 'unresolved' }],
  run: (base, raw) => {
    const runId = runIdOf(raw.runId);
    return runId === undefined ? [] : [{ ...base, kind: 'run', runId }];
  },
};

/** One stored entry, validated — or nothing. */
function entryOf(value: unknown): JournalEntry[] {
  return isRaw(value) ? rawEntryOf(value) : [];
}

function rawEntryOf(raw: Raw): JournalEntry[] {
  const base = baseOf(raw);
  const kind = Object.hasOwn(KINDS, String(raw.kind)) ? KINDS[raw.kind as JournalEntry['kind']] : undefined;

  return base === undefined || kind === undefined ? [] : kind(base, raw);
}
