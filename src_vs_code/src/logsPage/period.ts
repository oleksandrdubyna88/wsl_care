import type { RunRead } from '../client/verbs';
import { DAY_MS, RETENTION_DAYS, utcInstantOf } from '../shared/instants';
import { RUN_ID_SHAPE } from '../shared/shapes';

/**
 * The Logs page's periods (plan §7.4, §15j M7 / M8) — and the ONE place their argv is decided. The page names a period;
 * the HOST builds every read from it:
 *
 * - *This run* → `runs show <runId> --json`, the run id one the host read itself (`status.lastCleanup`, or the run list
 *   it holds), checked against the daemon's spelling;
 * - *Today*, *Yesterday*, a date, a range → `logs` and `runs --from <instant> --to <instant> --json`: the LOCAL days
 *   chosen, sent as the UTC instants of their local midnights (the first day's, and the one after the last day's), so a
 *   23- or 25-hour day under summer time is exactly the day the person meant (§15j M7, decided in E6.S0). A day whose
 *   midnight does not exist (a zone that springs forward AT midnight) starts at its first instant, which is what the
 *   machine's `Date` gives.
 *
 * A day is a CALENDAR day (`common.utc-timestamps` rule 3): `yyyy-MM-dd`, real (no 30 February), held as text and stamped
 * as UTC for every calendar step — only the two midnights are converted, at the edge, through the machine's zone. The days
 * are clamped to the history the daemon keeps (`RETENTION_DAYS`, today included): an older day is the oldest kept, a later
 * one is today, and the window says it was clamped.
 */

export interface Day {
  readonly year: number;
  readonly month: number;
  readonly day: number;
}

export type DayPeriod =
  | { readonly kind: 'today' }
  | { readonly kind: 'yesterday' }
  | { readonly kind: 'day'; readonly day: string }
  | { readonly kind: 'range'; readonly from: string; readonly to: string };

export type Period = DayPeriod | { readonly kind: 'thisRun'; readonly runId: string };

/** The instants a day period reaches the daemon as, the local days they span, and whether the retention clamped them. */
export interface PeriodWindow {
  readonly from: string;
  readonly to: string;
  readonly firstDay: string;
  readonly lastDay: string;
  readonly clamped: boolean;
}

const DAY_TEXT = /^\d{4}-\d{2}-\d{2}$/;

/** The UTC midnight of a `yyyy-MM-dd` text, or NaN for anything else. */
function stampOf(value: unknown): number {
  return typeof value === 'string' && DAY_TEXT.test(value) ? Date.parse(`${value}T00:00:00Z`) : Number.NaN;
}

/** A calendar day stamped as UTC, back as its text. */
function textOf(stamp: number): string {
  return new Date(stamp).toISOString().slice(0, 10);
}

/** The text, when it is a REAL calendar day: the engine rolls 2026-02-30 over to 2 March, so the round trip must hold. */
export function realDay(value: unknown): string | undefined {
  const stamp = stampOf(value);

  return Number.isFinite(stamp) && textOf(stamp) === value ? value : undefined;
}

/** A real calendar day of the shape `yyyy-MM-dd`, or `undefined`. */
export function dayOf(value: unknown): Day | undefined {
  const text = realDay(value);
  if (text === undefined) {
    return undefined;
  }
  const [year, month, day] = text.split('-').map(Number) as [number, number, number];

  return { year, month, day };
}

function addDays(day: string, days: number): string {
  return textOf(stampOf(day) + days * DAY_MS);
}

/** The machine's local calendar day at `now` — the presentation edge's zone, read once per question. */
function localToday(now: number): string {
  const at = new Date(now);
  const pad = (n: number): string => String(n).padStart(2, '0');

  return `${at.getFullYear()}-${pad(at.getMonth() + 1)}-${pad(at.getDate())}`;
}

/** The first instant of a local calendar day: its midnight in the machine's zone (or the first instant after it). */
function localMidnight(day: string): number {
  const { year, month, day: date } = dayOf(day) as Day;

  return new Date(year, month - 1, date).getTime();
}

/** The local days the daemon still holds: today and the `RETENTION_DAYS - 1` before it. */
export function retainedDays(now: number): { readonly oldest: string; readonly newest: string } {
  const newest = localToday(now);

  return { oldest: addDays(newest, 1 - RETENTION_DAYS), newest };
}

/** The day inside the kept history: `yyyy-MM-dd` texts order as their days. */
function clamp(day: string, kept: { readonly oldest: string; readonly newest: string }): string {
  const atLeast = day < kept.oldest ? kept.oldest : day;

  return atLeast > kept.newest ? kept.newest : atLeast;
}

/** The first and last local day of a day period, before the clamp. */
function daysOf(period: DayPeriod, today: string): readonly [string, string] {
  const days: { readonly [K in DayPeriod['kind']]: (p: Extract<DayPeriod, { kind: K }>) => readonly [string, string] } = {
    today: () => [today, today],
    yesterday: () => [addDays(today, -1), addDays(today, -1)],
    day: (p) => [p.day, p.day],
    range: (p) => [p.from, p.to],
  };

  return (days[period.kind] as (p: DayPeriod) => readonly [string, string])(period);
}

/** The instant window of a day period: from its first day's local midnight to the midnight after its last day. */
export function windowOf(period: DayPeriod, now: number): PeriodWindow {
  const kept = retainedDays(now);
  const [first, last] = daysOf(period, kept.newest);
  const firstDay = clamp(first, kept);
  const lastDay = clamp(last, kept);

  return {
    from: utcInstantOf(localMidnight(firstDay)),
    to: utcInstantOf(localMidnight(addDays(lastDay, 1))),
    firstDay,
    lastDay,
    clamped: firstDay !== first || lastDay !== last,
  };
}

/** What the host asks the daemon for a period — `runs show` for one run; `logs` and `runs` over the window otherwise. */
export function readsOf(period: Period, now: number): RunRead[] {
  return readsFor(period, period.kind === 'thisRun' ? undefined : windowOf(period, now));
}

/** The reads of a period over the window ALREADY built for it (review C2: the view shows that same window). */
export function readsFor(period: Period, window: PeriodWindow | undefined): RunRead[] {
  if (period.kind === 'thisRun') {
    return [{ read: 'runsShow', runId: period.runId }];
  }

  return window === undefined ? [] : [{ read: 'logs', from: window.from, to: window.to }, { read: 'runs', from: window.from, to: window.to }];
}

// ---- a period read back: from the memento, or built from a page message ----

type Raw = Readonly<Record<string, unknown>>;

function isRecord(value: unknown): value is Raw {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function keysAre(raw: Raw, keys: readonly string[]): boolean {
  const held = Object.keys(raw);

  return held.length === keys.length && keys.every((key) => held.includes(key));
}

/** Two real days, the first not after the second. */
function rangeValid(from: unknown, to: unknown): boolean {
  const first = realDay(from);
  const last = realDay(to);

  return first !== undefined && last !== undefined && first <= last;
}

interface KindCheck {
  readonly keys: readonly string[];
  readonly valid: (raw: Raw) => boolean;
}

/** Each kind's exact keys and the check of its values — a kind outside this table is no period. */
const KINDS: Readonly<Record<Period['kind'], KindCheck>> = {
  today: { keys: ['kind'], valid: () => true },
  yesterday: { keys: ['kind'], valid: () => true },
  day: { keys: ['kind', 'day'], valid: (raw) => realDay(raw.day) !== undefined },
  range: { keys: ['kind', 'from', 'to'], valid: (raw) => rangeValid(raw.from, raw.to) },
  thisRun: { keys: ['kind', 'runId'], valid: (raw) => typeof raw.runId === 'string' && RUN_ID_SHAPE.test(raw.runId) },
};

function kindOf(raw: Raw): KindCheck | undefined {
  return typeof raw.kind === 'string' && Object.hasOwn(KINDS, raw.kind) ? KINDS[raw.kind as Period['kind']] : undefined;
}

function checked(raw: Raw): Period | undefined {
  const kind = kindOf(raw);

  return kind !== undefined && keysAre(raw, kind.keys) && kind.valid(raw) ? ({ ...raw } as Period) : undefined;
}

/** The period, when `value` is EXACTLY one of the closed shapes with valid values — anything else is `undefined`. */
export function periodOf(value: unknown): Period | undefined {
  return isRecord(value) ? checked(value) : undefined;
}
