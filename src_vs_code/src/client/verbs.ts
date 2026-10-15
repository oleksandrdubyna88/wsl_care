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
export const RUN_READ_NAMES = ['runsShow', 'runs', 'logs', 'archiveStatus', 'archivePreview', 'archiveCheckBase', 'archiveList'] as const;

export type RunReadName = (typeof RUN_READ_NAMES)[number];

/**
 * Each run read's CLI verb words (review K2) — an explicit table, so a read added to `RUN_READ_NAMES` does not compile until
 * its verb is written here, and its union tag can never reach argv by accident.
 */
export const RUN_READ_VERBS: { readonly [K in RunReadName]: readonly string[] } = {
  runsShow: ['runs', 'show'],
  runs: ['runs'],
  logs: ['logs'],
  archiveStatus: ['archive', 'status'],
  archivePreview: ['archive', 'preview'],
  archiveCheckBase: ['archive', 'check-base'],
  archiveList: ['archive', 'list'],
};

export type RunRead =
  | { readonly read: 'runsShow'; readonly runId: string }
  | { readonly read: 'runs'; readonly from: string; readonly to: string }
  | { readonly read: 'logs'; readonly from: string; readonly to: string }
  | { readonly read: 'archiveStatus' }
  | { readonly read: 'archivePreview' }
  | { readonly read: 'archiveCheckBase'; readonly path: string }
  /** E10.S2a, the Archive page: `archive list --json` — never `--agent`, `--month`, `--run` or `--restorable`. */
  | { readonly read: 'archiveList' };

/** The reads about runs and their history (E6.S3, E6.S4) — what the Logs page and the durable poll ask; the archive reads are E10's. */
export type HistoryRead = Extract<RunRead, { readonly read: 'runsShow' | 'runs' | 'logs' }>;

/** The daemon tail of a run read — appended after `--exec /opt/wsl-care/bin/wsl-care`. Pure; the values are checked by the caller. */
export function runReadTail(read: RunRead): readonly string[] {
  return [...RUN_READ_VERBS[read.read], ...valuesOf(read), '--json'];
}

/** The values a read carries between its verb and `--json`, in the order the daemon takes them. */
function valuesOf(read: RunRead): readonly string[] {
  return (READ_VALUES[read.read] as (r: RunRead) => readonly string[])(read);
}

/** One entry per read — a read added to `RunRead` without its values here does not compile. */
const READ_VALUES: { readonly [K in RunReadName]: (read: Extract<RunRead, { readonly read: K }>) => readonly string[] } = {
  runsShow: (read) => [read.runId],
  runs: (read) => ['--from', read.from, '--to', read.to],
  logs: (read) => ['--from', read.from, '--to', read.to],
  archiveStatus: () => [],
  archivePreview: () => [],
  archiveCheckBase: (read) => [read.path],
  archiveList: () => [],
};

/**
 * The ceilings of the run reads (plan §15k #19). None starts a child process: `runs show` reads the request folder, the
 * running state (at most four reads 100 ms apart, `RunningReadRetry.Default`), the history and ONE detail file; `runs` and
 * `logs` (without `--detail` / `--action`, so no detail file is opened) read the history of the window's days. All are the
 * file reads `status` already makes (it reads the history for `lastCleanup`), so `status`'s 20 s holds for each. The archive
 * reads that walk or read the base (`archive preview`, `archive list`) take the archive preview's setting instead.
 */
export const RUN_READ_CALLS: { readonly [K in RunReadName]: 'runRead' | 'archivePreview' } = {
  runsShow: 'runRead',
  runs: 'runRead',
  logs: 'runRead',
  archiveStatus: 'runRead',
  archivePreview: 'archivePreview',
  archiveCheckBase: 'runRead',
  // E10.S2a (own review #6): the daemon bounds the USER's own `archive list` only by its reachability window
  // (`archive.reachabilitySeconds`, `BaseWindow`) — reading the month indexes after it has no daemon limit. The ceiling is the one the
  // daemon gives its OWN list child (`ArchiveChildren.List`, `archive.previewTimeoutSeconds`, at most 600 s): the archive preview's setting.
  archiveList: 'archivePreview',
};

/** Each run read's DEFAULT ceiling (the client reads the settings at every call through `RUN_READ_CALLS`). */
export const RUN_READ_TIMEOUT_MS: { readonly [K in RunReadName]: number } = Object.fromEntries(
  RUN_READ_NAMES.map((name) => [name, ceilingMs(DEFAULT_NUMBERS, { call: RUN_READ_CALLS[name] })]),
) as { readonly [K in RunReadName]: number };

/**
 * The Windows runner's `tasks` verbs (WR-E1 of PLAN_windows_runner.md §3.8) — run by the Windows daemon exe, never through
 * `wsl.exe`, and spelt HERE and nowhere else like every other verb word:
 *
 * - `tasks render <task> --json` — read-only: the XML a task would be registered with, its summary and whether it may be
 *   registered; run on the STAGED exe;
 * - `tasks stage --json` — the bundled exe copies itself to the staging folder and verifies the copy;
 * - `tasks stage --remove --json` — every staged copy removed.
 *
 * `tasks run` is the task's own action (the supervisor, which spells the timer's mark): the extension never runs it, so it is not here.
 */
export const TASK_NAMES = ['watch', 'archive'] as const;

export type TaskName = (typeof TASK_NAMES)[number];

/** The daemon tail of `tasks render <task>` — the task is a closed word, checked again at runtime (a cast cannot widen it). */
export function tasksRenderTail(task: TaskName): readonly string[] {
  if (!TASK_NAMES.includes(task)) {
    throw new RangeError(`"${String(task)}" is not one of the two Windows tasks`);
  }

  return ['tasks', 'render', task, '--json'];
}

/** The daemon tail of `tasks stage`. */
export const TASKS_STAGE_TAIL: readonly string[] = ['tasks', 'stage', '--json'];

/** The daemon tail of `tasks stage --remove`. */
export const TASKS_REMOVE_TAIL: readonly string[] = ['tasks', 'stage', '--remove', '--json'];

/**
 * How long the Windows client waits for `tasks stage` / `tasks render` before it ends the process: the DEFAULT of the setting
 * WR-E2 adds (`wslCare.timeouts.windowsTasksStageSeconds`, 60 s, range 10–600). A stage copies 13.7 MB and reads it back twice; a
 * render reads one small file and the configuration.
 */
export const TASKS_DEFAULT_TIMEOUT_MS = 60_000;

/**
 * The five run reads the Windows side asks (WR-E1 of PLAN_windows_runner.md §3.8): `runs show`, `runs`, `logs`, `archive status`,
 * `archive list`. `archive preview` and `archive check-base` are the WSL side's; the Windows client refuses them by type and at runtime.
 */
export const WINDOWS_READ_NAMES = ['runsShow', 'runs', 'logs', 'archiveStatus', 'archiveList'] as const satisfies readonly RunReadName[];

export type WindowsRunRead = Extract<RunRead, { readonly read: (typeof WINDOWS_READ_NAMES)[number] }>;
