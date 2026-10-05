import { DEFAULT_NUMBERS } from '../settings/numbers';
import { ceilingMs } from './ceilings';

/**
 * The CLOSED set of daemon verbs this extension may run — the one module that holds it (plan §15f #5, §15g m1).
 *
 * <p>E5 is read-only and root-free: it calls `status --json`, `preview --all --json`, `doctor --json` and `--version`,
 * and nothing else. Every argv the client sends ends in exactly one of these tails; `structure.test.ts` fails when an
 * argv-shaped daemon word appears anywhere else in the shipped source, and `bundleScan.test.ts` fails when the bundle
 * carries a root, timer, confirm, manual or config word at all.</p>
 */

/** The verbs, by the name the client and its callers use. */
export const VERB_NAMES = ['status', 'preview', 'doctor', 'version'] as const;

export type Verb = (typeof VERB_NAMES)[number];

/** The daemon's argv tail for each verb — appended after `--exec /opt/wsl-care/bin/wsl-care`, never anything else. */
export const VERBS: { readonly [V in Verb]: readonly string[] } = {
  status: ['status', '--json'],
  preview: ['preview', '--all', '--json'],
  doctor: ['doctor', '--json'],
  version: ['--version'],
};

/**
 * How long the client waits for each verb before it kills `wsl.exe` (which ends the Linux process — measured,
 * research/2026-10-03_wsl_exe_facts.md) and reports a timeout — the DEFAULTS of the `wslCare.timeouts.*` settings. Each
 * is derived and held STRICTLY above the daemon's own worst case on that verb's path, every killed command counted with
 * its drain (`client/worstCases.ts`, `ceilings.test.ts`; plan §15q N-1 found `doctor`'s old 100 s below its 109 s):
 *
 * - `status` starts no child process at all (plan §15b #5, the < 2 s budget) — 20 s.
 * - `--version` answers before the machine is read, with no log file — the same 20 s.
 * - `doctor` runs four `systemctl show`, `systemctl --version` (15 s each) and `docker version` (10 s): 109 s → 120 s.
 * - `preview --all` runs ONE Docker snapshot — `docker version`, `system df`, `system df -v`, the dangling-volume
 *   listing and one `container inspect` per 100 containers: 330 s with the drains → 350 s. A machine with more than
 *   100 containers can exceed it, and is then told the preview timed out rather than waited on forever.
 *
 * The client reads the settings at every call (`ClientOptions.numbers`); these are what a test that sets nothing sees.
 */
export const VERB_TIMEOUT_MS: { readonly [V in Verb]: number } = {
  status: ceilingMs(DEFAULT_NUMBERS, { call: 'status' }),
  version: ceilingMs(DEFAULT_NUMBERS, { call: 'version' }),
  doctor: ceilingMs(DEFAULT_NUMBERS, { call: 'doctor' }),
  preview: ceilingMs(DEFAULT_NUMBERS, { call: 'preview' }),
};

/**
 * How many containers `preview`'s ceiling assumes: ONE `container inspect` batch (`DockerCommands.InspectBatch` = 100).
 * Above it the preview may outrun `VERB_TIMEOUT_MS.preview`, and a timeout then says "too many containers for a quick
 * preview (<n>)" rather than a bare timeout (§15h #1); the real duration at the real count is measured at the E5 live
 * gate (`POST_DEPLOY.md`).
 */
export const PREVIEW_CONTAINER_ASSUMPTION = 100;

/**
 * The three read verbs about RUNS (E6.S3, E6.S4, plan §15j M3 / M7): read-only and unprivileged like the four above — never
 * `-u`, never a write — but they take a value, so each is built here from typed parts rather than held as a fixed tail:
 *
 * - `runs show <runId> --json` — one run's state (queued / running / done / refused / interrupted / unknown), what the
 *   panel's durable poll asks ONCE when a run it follows is no longer in flight (§15j M6);
 * - `runs --from <instant> --to <instant> --json` — the runs of a window, what resolves a confirm whose run id was never
 *   seen (the coai E6.S2 plan round #3 contract) and the Logs page's run list (E6.S4): two RFC 3339 instants, UTC, whole
 *   seconds;
 * - `logs --from <instant> --to <instant> --json` — the Logs page's totals, run counts and max / min of a window (E6.S4,
 *   §7.4): the same two instants — the local-midnight instants of the days chosen (`logsPage/period.ts`). Never `--detail` and
 *   never `--action`: the objects of a run are read lazily through `runs show` when its line is expanded (§15j M7).
 *
 * The values are checked by the client before a spawn (`WslCareClient.read`): a run id of the daemon's one spelling, an
 * instant of exactly `yyyy-MM-ddTHH:mm:ssZ`.
 */
export const RUN_READ_NAMES = ['runsShow', 'runs', 'logs'] as const;

export type RunReadName = (typeof RUN_READ_NAMES)[number];

/**
 * Each run read's CLI verb words (review K2) — an explicit table, so a read added to `RUN_READ_NAMES` does not compile until
 * its verb is written here, and its union tag can never reach argv by accident.
 */
export const RUN_READ_VERBS: { readonly [K in RunReadName]: readonly string[] } = {
  runsShow: ['runs', 'show'],
  runs: ['runs'],
  logs: ['logs'],
};

export type RunRead =
  | { readonly read: 'runsShow'; readonly runId: string }
  | { readonly read: 'runs'; readonly from: string; readonly to: string }
  | { readonly read: 'logs'; readonly from: string; readonly to: string };

/** The daemon tail of a run read — appended after `--exec /opt/wsl-care/bin/wsl-care`. Pure; the values are checked by the caller. */
export function runReadTail(read: RunRead): readonly string[] {
  return read.read === 'runsShow' ? [...RUN_READ_VERBS.runsShow, read.runId, '--json'] : [...RUN_READ_VERBS[read.read], '--from', read.from, '--to', read.to, '--json'];
}

/**
 * The ceilings of the run reads (plan §15k #19). None starts a child process: `runs show` reads the request folder, the
 * running state (at most four reads 100 ms apart, `RunningReadRetry.Default`), the history and ONE detail file; `runs` and
 * `logs` (without `--detail` / `--action`, so no detail file is opened) read the history of the window's days. All are the
 * file reads `status` already makes (it reads the history for `lastCleanup`), so `status`'s 20 s holds for each.
 */
export const RUN_READ_TIMEOUT_MS: { readonly [K in RunReadName]: number } = {
  runsShow: ceilingMs(DEFAULT_NUMBERS, { call: 'runRead' }),
  runs: ceilingMs(DEFAULT_NUMBERS, { call: 'runRead' }),
  logs: ceilingMs(DEFAULT_NUMBERS, { call: 'runRead' }),
};
