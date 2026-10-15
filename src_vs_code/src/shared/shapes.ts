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

/** An archived entry's id (`ArchiveIndex.IsEntryId`, E9.S3): 16 lowercase hex digits — A20's `--entry -` lines (E10.S2b). */
export const ENTRY_ID_SHAPE = /^[0-9a-f]{16}$/;

/**
 * The most entries ONE restore can take: `archive.maxRestoreEntries`' RANGE maximum (`ConfigKeys.Archive.MaxRestoreEntries`, 5000).
 * The daemon's effective ceiling is lower or equal and is the backstop; this bounds the root ops' stdin list (`root/rootIds.ts`)
 * and the Archive page's selection (`archivePage/archiveSelection.ts`) — one number, both sides.
 */
export const MAX_RESTORE_ENTRIES = 5000;

/**
 * A staged copy's folder name (`StagedCopies`, WR-D2 of PLAN_windows_runner.md): the SHA-256 of the exe as 64 LOWER-case hex digits —
 * the only spelling the daemon's staging root holds, and so the only digest the Windows client turns into a path
 * (`WindowsCareClient.exe`).
 */
export const STAGED_DIGEST_SHAPE = /^[0-9a-f]{64}$/;
