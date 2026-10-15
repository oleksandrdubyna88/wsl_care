import { basePathRefusal } from '../shared/basePath';
import { RUN_ID_SHAPE, UTC_INSTANT_SHAPE } from '../shared/shapes';
import { DAEMON_EXIT } from './exitCodes';
import type { RunRead, RunReadName } from './verbs';

/**
 * What both clients check about a run read — the WSL client (`WslCareClient.read`) and the Windows one
 * (`WindowsCareClient.read`, WR-E1 of PLAN_windows_runner.md §3.8): the values BEFORE anything starts, and the exits that still
 * carry a readable answer. Extracted from `WslCareClient` (reuse-first move 2) so the two cannot drift: a read added to
 * `RunReadName` is checked here once, for both sides.
 */

/** The daemon's run-id spelling and the client's instant shape — `shared/shapes.ts`, the one place both are written. */
const RUN_ID = RUN_ID_SHAPE;
const INSTANT = UTC_INSTANT_SHAPE;

/** Why a run read may not be built, or `undefined` when it may. */
export function readRefusal(request: RunRead): string | undefined {
  return (READ_REFUSALS[request.read] as (r: RunRead) => string | undefined)(request);
}

/** One entry per read — a read added to `RunRead` without its check here does not compile. */
const READ_REFUSALS: { readonly [K in RunReadName]: (request: Extract<RunRead, { readonly read: K }>) => string | undefined } = {
  runsShow: (request) => (RUN_ID.test(request.runId) ? undefined : 'that run id is not one the daemon writes (yyyyMMddTHHmmssZ-<pid>)'),
  runs: (request) => instantsRefusal(request.from, request.to),
  logs: (request) => instantsRefusal(request.from, request.to),
  archiveStatus: () => undefined,
  archivePreview: () => undefined,
  archiveCheckBase: (request) => basePathRefusal(request.path),
  archiveList: () => undefined,
};

function instantsRefusal(from: string, to: string): string | undefined {
  if (!INSTANT.test(from) || !INSTANT.test(to)) {
    return 'a runs window takes two UTC instants of the shape yyyy-MM-ddTHH:mm:ssZ';
  }

  return Date.parse(to) > Date.parse(from) ? undefined : 'a runs window must end after it starts';
}

/** The exits a run read answers with: 0, and 4 — the history unreadable, the answer read from the rest. */
const READ_ANSWERS: ReadonlySet<number> = new Set([DAEMON_EXIT.ok, DAEMON_EXIT.recordsUnreadable]);

/**
 * `archive list` (E10.S2a, own review #1) answers with 1 too: every outcome but done and no-base exits 1 AFTER its JSON answer
 * (ArchiveRunCommand.List) — an unreachable share is the likeliest failure of the page, and its note is the daemon's own reason.
 */
const LIST_ANSWERS: ReadonlySet<number> = new Set([DAEMON_EXIT.ok, DAEMON_EXIT.runFailed]);

/** The exits after which `read`'s stdout is still an answer to read. */
export function answerExitsOf(read: RunReadName): ReadonlySet<number> {
  return read === 'archiveList' ? LIST_ANSWERS : READ_ANSWERS;
}
