import { readEnum, type EnumRead } from '../client/enumValue';
import type { JsonObject } from '../client/outcome';
import { runningOf } from '../root/rootAnswers';
import { runIdOf, type RunId } from '../root/rootIds';
import type { RunningBlock } from '../root/rootOutcome';

/**
 * The two run reads' answers (E6.S3, plan §15j M3 / M7), read as untrusted input: every enum through `readEnum` (an unknown
 * value is "unknown (<value>)", never a crash, §15j m1), every run id through `runIdOf`, every other key ignored. Only what
 * the panel's durable poll and its *Last cleanup* need is read — the full detail (every object removed, the commands)
 * stays the daemon's to show.
 */

/** `runs show`'s states (the daemon's `RunShowState.All`). */
export const RUN_SHOW_STATES = ['queued', 'running', 'done', 'refused', 'interrupted', 'unknown'] as const;

export type RunShowState = (typeof RUN_SHOW_STATES)[number];

/** One action of a run line: what it did, what it removed, what it freed. */
export interface RunLineAction {
  readonly id: string;
  readonly status: string;
  readonly count: number;
  readonly freedBytes: number;
}

/** One history line, as `runs` and `runs show` answer it. */
export interface RunLine {
  readonly runId: RunId | undefined;
  readonly trigger: string;
  readonly startedAt: string;
  readonly outcome: string;
  readonly freedBytes: number | undefined;
  readonly actions: readonly RunLineAction[];
  readonly reason: string;
}

/** `runs show <runId> --json`. */
export interface RunShow {
  readonly runId: RunId | undefined;
  readonly state: EnumRead<RunShowState>;
  readonly reason: string;
  readonly line: RunLine | undefined;
  readonly running: RunningBlock | undefined;
}

function isObject(value: unknown): value is JsonObject {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function stringOr(value: unknown, fallback = ''): string {
  return typeof value === 'string' ? value : fallback;
}

function count(value: unknown): number {
  return typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value : 0;
}

function optionalCount(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value : undefined;
}

function actionOf(value: unknown): RunLineAction[] {
  return isObject(value) && typeof value.id === 'string' ? [{ id: value.id, status: stringOr(value.status), count: count(value.count), freedBytes: count(value.freedBytes) }] : [];
}

/** One history line, or nothing when it is not an object. */
function lineOf(value: unknown): RunLine[] {
  if (!isObject(value)) {
    return [];
  }
  const actions = Array.isArray(value.actions) ? value.actions.flatMap(actionOf) : [];

  return [{ runId: runIdOf(value.runId), trigger: stringOr(value.trigger), startedAt: stringOr(value.startedAt), outcome: stringOr(value.outcome), freedBytes: optionalCount(value.freedBytes), actions, reason: stringOr(value.reason) }];
}

/** `runs show <runId> --json`, read. */
export function parseRunShow(body: JsonObject): RunShow {
  const running = isObject(body.running) ? runningOf({ running: body.running }) : undefined;

  return { runId: runIdOf(body.runId), state: readEnum(body.state, RUN_SHOW_STATES), reason: stringOr(body.reason), line: lineOf(body.run)[0], running };
}

/** `runs --json`: every line of the window, in the daemon's order. */
export function parseRuns(body: JsonObject): readonly RunLine[] {
  return Array.isArray(body.runs) ? body.runs.flatMap(lineOf) : [];
}

/** The states a run never leaves: done, refused, interrupted — and unknown, which ends a poll too (plan §15k #4). */
const TERMINAL: ReadonlySet<RunShowState> = new Set<RunShowState>(['done', 'refused', 'interrupted', 'unknown']);

/**
 * Whether polling may stop at this state: done, refused, interrupted, unknown (plan §15k #4) — and a value this build does
 * not know, which a later daemon may add: a poll that waited on it would wait for a state it cannot recognise, so it ends,
 * shown as "unknown (<value>)".
 */
export function isTerminal(state: EnumRead<RunShowState>): boolean {
  return state.kind === 'unknown' || TERMINAL.has(state.value);
}
