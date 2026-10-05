/**
 * The shapes two sides of this repository must spell the same way (E6.S3 review, coai #0 / #5) — held ONCE, here, and
 * imported by the client (`WslCareClient.read`), the root ids (`root/rootIds.ts`, the unit names of `root/rootAnswers.ts`)
 * and the strict fake. `sharedShapes.test.ts` fails when any other source spells the run-id pattern.
 */

/**
 * A run id as the daemon spells it (`RunId.New` / `RunId.TryParse`): the UTC stamp, a dash, the pid with NO leading zero (two
 * spellings would name one run twice — E6.S0 review S4). Stricter than the daemon in one place: pid 0 is refused, no run has it.
 */
export const RUN_ID_SHAPE = /^[0-9]{8}T[0-9]{6}Z-[1-9][0-9]{0,9}$/;

/** The one instant shape the client sends to `runs --from --to`: UTC, whole seconds (`LogPeriod.ParseInstants` takes it). */
export const UTC_INSTANT_SHAPE = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$/;

/** The run-id pattern without its anchors — for a shape that embeds a run id (a unit name). */
export const RUN_ID_BODY = RUN_ID_SHAPE.source.slice(1, -1);
