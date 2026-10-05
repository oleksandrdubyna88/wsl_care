import { DEFAULT_NUMBERS, type Numbers } from '../settings/numbers';
import { FALLBACK_LIMITS } from '../shared/daemonLimits';
import { randomUUID } from 'node:crypto';

import { FULL_CHECK_ACTIONS } from '../root/cleanupController';
import { ACTION_IDS, actionIdOf, runIdOf, type RunId } from '../root/rootIds';

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
 * does not exist or lies more than `FUTURE_SKEW_MS` ahead (review A5: it would never age) — is dropped, never a crash.</p>
 *
 * <p><b>Several windows share ONE `globalState`</b> (review C6). `vscode.Memento` has only `update(key, value)` — checked in
 * `@types/vscode` 1.85: there is no compare-and-swap and no callback form — so this is ADVISORY coordination, and its
 * residual race is stated here, where a caller reads it. What it does: no copy is ever cached (every operation reads the
 * store afresh and applies itself to what is there); this host's writes are serialised; a removal leaves a TOMBSTONE for
 * `wslCare.cleanup.tombstoneMinutes`, merged into every write, so an entry another window removed is not written back by a stale list;
 * after each write the store is read again one turn later and the operation re-applied if a concurrent write took it away.
 * The residual: a write by another window landing later than that turn, from a list read before ours, can still drop an
 * entry we added (a lost entry is a run that is not followed — its result still reaches *Last cleanup* through
 * `status.lastCleanup`) or, past the tombstone's life, bring a removed one back (the follower re-reads the journal before it
 * shows a result, so a result is shown once per window at most).</p>
 *
 * <p><b>Growth:</b> at most `wslCare.cleanup.journalEntries` (32 by default, the daemon's own request budget); past it a new entry is REFUSED and the
 * flow says to wait (review C12) — an unshown entry is never evicted; at most two tombstones per entry of the budget, each for 10 minutes.
 * An entry leaves on its terminal answer or on the 30-minute poll ceiling (`runFollower.ts`).</p>
 */

/** The `globalState` key. The `.v2` is the shape `{ entries, removed }`: the `.v1` array of E6.S3's first build is read as nothing. */
export const JOURNAL_KEY = 'wslCare.cleanup.journal.v2';

/** The daemon accepts at most 32 waiting requests (plan §15k #8); more entries than that cannot all be real. */
/** The DEFAULT budget (`wslCare.cleanup.journalEntries`); a journal reads the setting at each add. */
export const MAX_ENTRIES = DEFAULT_NUMBERS.journalEntries;

/** The most actions one entry may name: every id of the compiled registry once (review C9). */
export const MAX_ACTIONS_PER_ENTRY = ACTION_IDS.length;

/** How far ahead of this window's clock an instant may lie and still be read — the daemon's `RequestSweep.FutureSkew`. */
/** The clock-skew allowance's FALLBACK — the daemon's own value (`shared/daemonLimits.ts`) wins when it answers one. */
export const FUTURE_SKEW_MS = FALLBACK_LIMITS.futureSkewMs;

/** How long a removal's tombstone is kept and merged. */
/** The DEFAULT of `wslCare.cleanup.tombstoneMinutes`. */
export const TOMBSTONE_TTL_MS = DEFAULT_NUMBERS.tombstoneMinutes * 60_000;

/** Tombstones kept per entry of the budget. */
const TOMBSTONES_PER_ENTRY = 2;

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

/** An entry as a caller hands it in: everything but its id. */
export type NewEntry = Omit<EntryBase, 'id'> & ({ readonly kind: 'unresolved' } | { readonly kind: 'run'; readonly runId: RunId });

/** The part of `vscode.Memento` the journal uses. */
export interface DurableStore {
  get(key: string): unknown;
  update(key: string, value: unknown): PromiseLike<void>;
}

interface Tombstone {
  readonly id: string;
  readonly at: number;
  /** The journal (window) that removed it — whose claim stands when two remove it at once. */
  readonly by: string;
}

/** What the key holds. */
interface Stored {
  readonly entries: readonly JournalEntry[];
  readonly removed: readonly Tombstone[];
}

/** One operation, applied to what the store holds NOW; `undefined` when there is nothing to change. */
type Operation = (current: Stored) => Stored | undefined;

/** Whether the operation's effect is still in what the store holds. */
type Effect = (current: Stored) => boolean;

/** One turn of the event loop, so a concurrent write already queued lands before the check (not a microtask). */
function nextTurn(): Promise<void> {
  return new Promise((resolve) => { setImmediate(resolve); });
}

export class CleanupJournal {
  private readonly listeners = new Set<() => void>();
  private queue: Promise<void> = Promise.resolve();
  /** This journal's own mark on the tombstones it writes (one per window). */
  private readonly token = randomUUID();

  constructor(
    private readonly store: DurableStore,
    private readonly wallNow: () => number = Date.now,
    private readonly numbers: () => Numbers = () => DEFAULT_NUMBERS,
    private readonly futureSkew: () => number = () => FUTURE_SKEW_MS,
  ) {}

  /** Every valid entry, oldest first, read from the store each time (another window may have written it). */
  entries(): readonly JournalEntry[] {
    return this.read().entries;
  }

  /** Whether `id` is still an open entry — asked again right before a result is shown (review C6). */
  has(id: string): boolean {
    return this.entries().some((e) => e.id === id);
  }

  /** Persist a new entry; resolves once the store has it — or `undefined` when the journal is full (nothing written). */
  async add(entry: NewEntry): Promise<JournalEntry | undefined> {
    const added: JournalEntry = { ...entry, id: randomUUID() };
    let full = false;
    await this.serialised((current) => {
      full = current.entries.length >= this.numbers().journalEntries;
      return full ? undefined : { ...current, entries: [...current.entries, added] };
    }, (current) => full || current.entries.some((e) => e.id === added.id));

    return full ? undefined : added;
  }

  /** Replace the entry `id` with `next` (keeping the id), e.g. an unresolved confirm that got its run id. */
  replace(id: string, next: NewEntry): Promise<void> {
    const entry: JournalEntry = { ...next, id };

    return this.serialised(
      (current) => (current.entries.some((e) => e.id === id) ? { ...current, entries: current.entries.map((e) => (e.id === id ? entry : e)) } : undefined),
      (current) => !current.entries.some((e) => e.id === id) || current.entries.some((e) => e.id === id && e.kind === entry.kind),
    );
  }

  /** Remove the entry `id` — its terminal answer was shown — leaving a tombstone. */
  remove(id: string): Promise<void> {
    return this.serialised((current) => this.removing(current, id), (current) => !current.entries.some((e) => e.id === id));
  }

  /**
   * Remove the entry `id` and say whether THIS window's removal stands (review C6): when two windows end one entry at once,
   * the store keeps one tombstone — the last write — and only its writer shows the result. False when the entry was gone.
   */
  async claim(id: string): Promise<boolean> {
    if (!this.has(id)) {
      return false;
    }
    await this.remove(id);

    return this.read().removed.some((t) => t.id === id && t.by === this.token);
  }

  private removing(current: Stored, id: string): Stored | undefined {
    return current.entries.some((e) => e.id === id) ? { entries: current.entries.filter((e) => e.id !== id), removed: [...current.removed, { id, at: this.wallNow(), by: this.token }] } : undefined;
  }

  /** Called after every change this journal made; the returned function unsubscribes. */
  onChange(listener: () => void): () => void {
    this.listeners.add(listener);
    return () => { this.listeners.delete(listener); };
  }

  /** One write at a time in this host, each applied to what the store holds when it runs. */
  private serialised(operation: Operation, effect: Effect): Promise<void> {
    const run = this.queue.then(() => this.applied(operation, effect));
    this.queue = run.catch(() => undefined);

    return run;
  }

  /** Write, wait one turn, read again: when a concurrent write took the effect away, apply it once more. */
  private async applied(operation: Operation, effect: Effect): Promise<void> {
    const wrote = await this.write(operation);
    if (!wrote) {
      return;
    }
    await nextTurn();
    if (!effect(this.read())) {
      await this.write(operation);
    }
    this.listeners.forEach((listener) => listener());
  }

  private async write(operation: Operation): Promise<boolean> {
    const next = operation(this.read());
    if (next !== undefined) {
      await this.store.update(JOURNAL_KEY, this.pruned(next));
    }

    return next !== undefined;
  }

  /** The tombstones still alive, and no entry they name. */
  private pruned(stored: Stored): Stored {
    const now = this.wallNow();
    const removed = stored.removed.filter((t) => now - t.at < this.numbers().tombstoneMinutes * 60_000).slice(-TOMBSTONES_PER_ENTRY * this.numbers().journalEntries);
    const gone = new Set(removed.map((t) => t.id));

    return { entries: stored.entries.filter((e) => !gone.has(e.id)), removed };
  }

  /** The store, read as untrusted — validated entries, no tombstoned id. */
  private read(): Stored {
    const raw = this.store.get(JOURNAL_KEY);
    const removed = listOf(raw, 'removed').flatMap(tombstoneOf);
    const entries = listOf(raw, 'entries').flatMap((e) => entryOf(e, this.wallNow() + this.futureSkew()));
    const gone = new Set(removed.map((t) => t.id));

    return { entries: entries.filter((e) => !gone.has(e.id)), removed };
  }
}

type Raw = Readonly<Record<string, unknown>>;

function isRaw(value: unknown): value is Raw {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** The list under `key` of a stored value, or none. */
function listOf(raw: unknown, key: 'entries' | 'removed'): readonly unknown[] {
  const list = isRaw(raw) ? raw[key] : undefined;

  return Array.isArray(list) ? list : [];
}

function isTime(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value);
}

function tombstoneOf(value: unknown): Tombstone[] {
  return isRaw(value) && typeof value.id === 'string' && isTime(value.at) ? [{ id: value.id, at: value.at, by: textOr(value.by) }] : [];
}

function textOr(value: unknown): string {
  return typeof value === 'string' ? value : '';
}

/** An instant as the journal writes it — `Date.toISOString()` — and nothing looser. */
const ISO = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/;

function isAction(value: unknown): value is string {
  return actionIdOf(value) !== undefined || FULL_CHECK_ACTIONS.includes(value as string);
}

function isActionList(value: unknown): value is readonly string[] {
  return Array.isArray(value) && [value.length > 0, value.length <= MAX_ACTIONS_PER_ENTRY, value.every(isAction)].every(Boolean);
}

function isText(value: unknown, max: number): value is string {
  return typeof value === 'string' && value.length > 0 && value.length <= max;
}

/** An instant that EXISTS (the shape alone lets 2026-13-01 through, review A5) and is not later than `latest` (now plus the skew allowance). */
function isReadableInstant(value: unknown, latest: number): boolean {
  const ms = isoMs(value);

  return isTime(ms) && roundTrips(ms, value) && ms <= latest;
}

function isoMs(value: unknown): number {
  return isText(value, 32) && ISO.test(value) ? Date.parse(value) : Number.NaN;
}

/** 2026-02-30 parses (to 2 March) — only an instant that prints back as itself exists. */
function roundTrips(ms: number, value: unknown): boolean {
  return new Date(ms).toISOString() === value;
}

/** The checks every stored entry must pass, one per field. */
const FIELD_CHECKS: readonly ((raw: Raw, now: number) => boolean)[] = [
  (raw) => isText(raw.id, 64),
  (raw) => isText(raw.distro, 64),
  (raw, now) => isReadableInstant(raw.since, now),
  (raw) => JOURNAL_OPS.some((op) => op === raw.op),
  (raw) => isActionList(raw.actions),
];

/** The fields every entry carries, validated — or undefined. */
function baseOf(raw: Raw, now: number): EntryBase | undefined {
  return FIELD_CHECKS.every((check) => check(raw, now)) ? { id: raw.id as string, op: raw.op as JournalOp, distro: raw.distro as string, actions: raw.actions as readonly string[], since: raw.since as string } : undefined;
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
function entryOf(value: unknown, now: number): JournalEntry[] {
  return isRaw(value) ? rawEntryOf(value, now) : [];
}

function rawEntryOf(raw: Raw, now: number): JournalEntry[] {
  const base = baseOf(raw, now);
  const kind = Object.hasOwn(KINDS, String(raw.kind)) ? KINDS[raw.kind as JournalEntry['kind']] : undefined;

  return base === undefined || kind === undefined ? [] : kind(base, raw);
}
