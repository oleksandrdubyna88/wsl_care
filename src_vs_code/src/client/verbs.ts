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
 * research/2026-10-03_wsl_exe_facts.md) and reports a timeout. Each is ABOVE the sum of the daemon's own ceilings on
 * that verb's path, so the extension never kills an answer the daemon would still have given:
 *
 * - `status` starts no child process at all (plan §15b #5, the < 2 s budget) — 20 s is ten times its budget plus the
 *   `wsl.exe` relay start (≈ 0.13 s measured).
 * - `--version` answers before the machine is read, with no log file — the same 20 s.
 * - `doctor` runs, one after another, `systemctl show` for four units (15 s ceiling each, `SystemdCommands.Ceiling`),
 *   `systemctl --version` (15 s) and `docker version` (10 s, `DockerCommands.ProbeCeiling`): 85 s → 100 s.
 * - `preview --all` runs `docker version` (10 s), `system df` and `system df -v` (2 min each,
 *   `DockerCommands.DiskUsageCeiling`), the dangling-volume listing (30 s) and one `container inspect` per 100
 *   containers (30 s each): 280 s + one batch = 310 s → 330 s. A machine with more than 100 containers can exceed it,
 *   and is then told the preview timed out rather than waited on forever.
 */
export const VERB_TIMEOUT_MS: { readonly [V in Verb]: number } = {
  status: 20_000,
  version: 20_000,
  doctor: 100_000,
  preview: 330_000,
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

export type RunRead =
  | { readonly read: 'runsShow'; readonly runId: string }
  | { readonly read: 'runs'; readonly from: string; readonly to: string }
  | { readonly read: 'logs'; readonly from: string; readonly to: string };

/** The daemon tail of a run read — appended after `--exec /opt/wsl-care/bin/wsl-care`. Pure; the values are checked by the caller. */
export function runReadTail(read: RunRead): readonly string[] {
  return read.read === 'runsShow' ? ['runs', 'show', read.runId, '--json'] : [read.read, '--from', read.from, '--to', read.to, '--json'];
}

/**
 * The ceilings of the run reads (plan §15k #19). None starts a child process: `runs show` reads the request folder, the
 * running state (at most four reads 100 ms apart, `RunningReadRetry.Default`), the history and ONE detail file; `runs` and
 * `logs` (without `--detail` / `--action`, so no detail file is opened) read the history of the window's days. All are the
 * file reads `status` already makes (it reads the history for `lastCleanup`), so `status`'s 20 s holds for each.
 */
export const RUN_READ_TIMEOUT_MS: { readonly [K in RunReadName]: number } = {
  runsShow: 20_000,
  runs: 20_000,
  logs: 20_000,
};
