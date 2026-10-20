import { engineChangedSentence, type Engine } from './engineTypes';

/**
 * Where a flow gets its engine, and what a read of it says (moved out of `engineHost.ts` by the coai code round of WR-E3, finding 5): the flows —
 * the elevated ones and the Windows tasks' unelevated ones — consume an {@link EngineSource}; only the host (`engineHost.ts`) reads Windows to make one.
 */

/** One read: the engine it chose, and why PowerShell 7 could not be checked ('' when the read answered). */
export interface EngineRead {
  readonly engine: Engine;
  readonly failure: string;
}

/** Where an elevated flow gets its engine — the host, or a fixed one in a unit test. */
export interface EngineSource {
  /** The last read's engine, reading first when there is none — asked BEFORE the modal. */
  readonly engine: () => Promise<EngineRead>;
  /** A NEW read — asked AFTER the modal, right before the launch. */
  readonly reread: () => Promise<EngineRead>;
  /** The last read, or `undefined` before the first one answered — what the panel names. */
  readonly last: () => EngineRead | undefined;
}

/**
 * Why the launch must not go ahead with `before` (the engine the modal named) after the read taken right before it, or
 * `undefined` when it may: a read that did not answer says so (§11 R7) — never "the program changed" — and a read that chose
 * another program says that (G1's sentence). Both reads failing name the same 5.1, which runs: nothing is refused for a probe.
 */
export function engineProblem(before: Engine, now: EngineRead): string | undefined {
  if (now.engine.file === before.file) {
    return undefined;
  }

  return now.failure === '' ? engineChangedSentence(before, now.engine) : `Nothing ran: the read of the elevated program right before the launch did not answer (${now.failure}). Press the button again.`;
}
