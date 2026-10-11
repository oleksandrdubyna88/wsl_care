import type { ProcessResult } from './runner';

/** A process result that is not an exit: the query did not answer. */
export type NotAnswered = Exclude<ProcessResult, { kind: 'exited' }>;

type Words = { readonly [K in NotAnswered['kind']]: (r: Extract<NotAnswered, { kind: K }>, what: string, timeoutKey: string) => string };

const WORDS: Words = {
  timedOut: (r, what, timeoutKey) => `${what} did not answer within ${Math.round(r.timeoutMs / 1000)} s (${timeoutKey})`,
  failedToStart: (r, what) => `${what} could not start: ${r.reason}`,
  signalled: (r, what) => `${what} was stopped (${r.signal})`,
  tooMuchOutput: (_r, what) => `${what} printed more than the extension reads`,
};

/**
 * Why an unelevated query did not answer, in ONE wording (shared since PLAN_pwsh_engine_windows_time.md's own code review: the
 * GPU query, the guard's Task Scheduler query and the engine read had three copies of this table). `what` names the query
 * ("the display-adapter query"), `timeoutKey` the setting that bounds it.
 */
export function notAnsweredWords(result: NotAnswered, what: string, timeoutKey: string): string {
  return (WORDS[result.kind] as (r: NotAnswered, what: string, timeoutKey: string) => string)(result, what, timeoutKey);
}
