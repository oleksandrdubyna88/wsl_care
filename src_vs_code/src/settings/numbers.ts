import { MARGIN_S, WORST_CASE_S, WSL_LIST_MINIMUM_S } from '../client/worstCases';

/**
 * Every number of the extension's E6 work is a setting (the owner's standing rule, 2026-10-05): the call ceilings, the
 * durable poll's interval, ceiling and grace, the preview's expiry, the journal's budget and the Logs page's index bound —
 * application scope (a workspace's `.vscode/settings.json` cannot steer them), each with its range and default. ONE table:
 * `package.json`'s `contributes.configuration` is held EQUAL to it by `numbers.test.ts`, and a ceiling's MINIMUM is its
 * call's derived worst case plus the margin (`client/worstCases.ts`) — so no setting can put a ceiling at or below the
 * daemon's own worst case (`ceilings.test.ts`).
 */

export interface NumberSetting {
  /** The key under `wslCare.`. */
  readonly key: string;
  readonly default: number;
  readonly minimum: number;
  readonly maximum: number;
  readonly description: string;
}

/** A ceiling's minimum: strictly above the daemon's worst case for its call. */
function above(worstS: number): number {
  return worstS + MARGIN_S;
}

const TIMEOUTS = {
  statusSeconds: { key: 'timeouts.statusSeconds', default: 20, minimum: above(WORST_CASE_S.status), maximum: 600, description: 'How long `status --json` may take before `wsl.exe` is stopped and the call reads "timed out", in seconds.' },
  versionSeconds: { key: 'timeouts.versionSeconds', default: 20, minimum: above(WORST_CASE_S.version), maximum: 600, description: 'How long `--version` may take (also the privileged check before a cleanup), in seconds.' },
  doctorSeconds: { key: 'timeouts.doctorSeconds', default: 120, minimum: above(WORST_CASE_S.doctor), maximum: 3600, description: 'How long `doctor --json` may take, in seconds — at least its worst case: four `systemctl show`, `systemctl --version` and `docker version`, each to its ceiling.' },
  previewSeconds: { key: 'timeouts.previewSeconds', default: 350, minimum: above(WORST_CASE_S.preview), maximum: 7200, description: 'How long `preview --all --json` (one Docker snapshot, up to 100 containers) may take, in seconds.' },
  previewPerDockerRowSeconds: { key: 'timeouts.previewPerDockerRowSeconds', default: 350, minimum: above(WORST_CASE_S.preview), maximum: 7200, description: 'A cleanup\'s preview (`act <ids> --preview`) takes ONE Docker snapshot per Docker row (A4–A7): its ceiling is this many seconds per Docker row, plus A9\'s snap listing and a margin.' },
  archivePreviewSeconds: { key: 'timeouts.archivePreviewSeconds', default: 630, minimum: above(WORST_CASE_S.archivePreview), maximum: 7200, description: 'How long `archive preview --json` (what the AI-session archive would move) may take, in seconds — above the daemon ceiling `archive.previewTimeoutSeconds` at its maximum (600).' },
  runReadSeconds: { key: 'timeouts.runReadSeconds', default: 20, minimum: above(WORST_CASE_S.runRead), maximum: 600, description: 'How long `runs show`, `runs` and `logs` may take, in seconds.' },
  detachSeconds: { key: 'timeouts.detachSeconds', default: 690, minimum: above(WORST_CASE_S.detach), maximum: 7200, description: 'How long a cleanup\'s confirm or *Run full check now* may take to hand the run to its unit, in seconds — at least the daemon\'s worst case: the shown list, one `systemctl show` per queued request (up to 32), the start and one more `systemctl show`. A detach that outruns it is followed as "outcome unknown", never reported as failed.' },
  wslListSeconds: { key: 'timeouts.wslListSeconds', default: 15, minimum: WSL_LIST_MINIMUM_S, maximum: 300, description: 'How long each of the three `wsl.exe --list` questions asked before a daemon call may take, in seconds (measured: about 50 ms each, warm).' },
  stopSeconds: { key: 'timeouts.stopSeconds', default: 150, minimum: above(WORST_CASE_S.stop), maximum: 3600, description: 'How long *Stop* (`act --stop`, one `systemctl stop`) may take, in seconds.' },
  windowsTimeFixSeconds: { key: 'timeouts.windowsTimeFixSeconds', default: 180, minimum: 30, maximum: 3600, description: 'How long *Start Windows Time* may take, in seconds — it waits for YOU at the UAC prompt, then starts the Windows Time service and resyncs (up to three tries).' },
  windowsTimeGuardSeconds: { key: 'timeouts.windowsTimeGuardSeconds', default: 180, minimum: 30, maximum: 3600, description: 'How long installing or removing the Windows Time guard may take, in seconds — it waits for YOU at the UAC prompt, then registers or deletes one scheduled task.' },
  windowsTimeGuardQuerySeconds: { key: 'timeouts.windowsTimeGuardQuerySeconds', default: 30, minimum: 5, maximum: 300, description: 'How long the read-only Task Scheduler query behind the panel\'s *Windows Time guard* line may take, in seconds.' },
} as const satisfies Readonly<Record<string, NumberSetting>>;

const CLEANUP = {
  followPollSeconds: { key: 'cleanup.followPollSeconds', default: 4, minimum: 2, maximum: 60, description: 'While a cleanup is in flight, how often the focused window asks `status`, in seconds.' },
  unknownDetachFollowSeconds: { key: 'cleanup.unknownDetachFollowSeconds', default: 60, minimum: 10, maximum: 600, description: 'A detach whose outcome is unknown and named no run id: how long the panel looks for its run in `status.running` before it hands the question to the durable poll, in seconds.' },
  followCeilingMinutes: { key: 'cleanup.followCeilingMinutes', default: 30, minimum: 5, maximum: 1440, description: 'How long a cleanup is followed before it reads "state unknown" with its run id, in minutes (after one last read of its record).' },
  // The daemon sweeps a request after its own grace, `RequestSweep.Grace` (60 s): the host waits past it before it resolves
  // an unresolved confirm from the history.
  requestGraceSeconds: { key: 'cleanup.requestGraceSeconds', default: 90, minimum: 60 + MARGIN_S, maximum: 900, description: 'How long a confirm whose run id was never seen waits before it is resolved from the run history, in seconds (above the daemon\'s own 60 s request grace).' },
  previewRounds: { key: 'cleanup.previewRounds', default: 3, minimum: 1, maximum: 10, description: 'How many times a preview that expired before the last confirmation is taken again before the cleanup is given up, saying so.' },
  previewExpiryMinutes: { key: 'cleanup.previewExpiryMinutes', default: 5, minimum: 1, maximum: 60, description: 'A preview older than this when you confirm is taken again first, in minutes.' },
  recordReadTries: { key: 'cleanup.recordReadTries', default: 3, minimum: 1, maximum: 10, description: 'How many times the record of a finished cleanup is read (`runs show`, `runs`) before it reads "the record could not be read".' },
  recordReadBackoffSeconds: { key: 'cleanup.recordReadBackoffSeconds', default: 8, minimum: 1, maximum: 300, description: 'How much longer each new try of a failed record read waits than the one before, in seconds (the first try at once).' },
  settleAtOnce: { key: 'cleanup.settleAtOnce', default: 4, minimum: 1, maximum: 32, description: 'How many followed cleanups are settled at the same time in one poll.' },
  tombstoneMinutes: { key: 'cleanup.tombstoneMinutes', default: 10, minimum: 1, maximum: 1440, description: 'How long a cleanup another window removed from the shared journal is remembered as removed, so a stale list in a second window does not write it back, in minutes.' },
  resultsKept: { key: 'cleanup.resultsKept', default: 5, minimum: 1, maximum: 50, description: 'How many past cleanup results *Last cleanup* in the panel lists (newest first; the daemon keeps them all).' },
  journalEntries: { key: 'cleanup.journalEntries', default: 32, minimum: 4, maximum: 256, description: 'How many started cleanups whose result has not appeared yet the extension keeps following; past it a new cleanup is refused, none dropped.' },
} as const satisfies Readonly<Record<string, NumberSetting>>;

/**
 * The Windows Time guard's task (PLAN_windows_time_task.md D6): baked into the task when it is installed — a changed value
 * makes the panel say "install it again to update it".
 */
const GUARD = {
  guardEveryHours: { key: 'windowsTime.guard.everyHours', default: 4, minimum: 1, maximum: 168, description: 'The Windows Time guard runs every this many hours (besides at startup, at logon, when the Windows Time service logs that it is stopping and when its start type is changed). Applies when the guard is installed (again).' },
  guardMinMinutesBetweenStarts: { key: 'windowsTime.guard.minMinutesBetweenStarts', default: 10, minimum: 1, maximum: 1440, description: 'The Windows Time guard starts the service at most once in this many minutes, so it never loops against software that stops it again. Applies when the guard is installed (again).' },
  guardDelaySeconds: { key: 'windowsTime.guard.delaySeconds', default: 60, minimum: 0, maximum: 3600, description: 'How long after startup, a logon, the service\'s stop event or a change of its start type the Windows Time guard waits before it runs, in seconds (0 = at once). Applies when the guard is installed (again).' },
  guardTimeLimitMinutes: { key: 'windowsTime.guard.timeLimitMinutes', default: 5, minimum: 1, maximum: 60, description: 'How long one run of the Windows Time guard may take before Task Scheduler stops it, in minutes. Applies when the guard is installed (again).' },
} as const satisfies Readonly<Record<string, NumberSetting>>;

const LOGS = {
  maxRunIndex: { key: 'logs.maxRunIndex', default: 9_999, minimum: 100, maximum: 100_000, description: 'The largest run-list index the Logs page may name (a bound on its messages; the host still checks the index against the list it read).' },
} as const satisfies Readonly<Record<string, NumberSetting>>;

/** Every number setting, by the name the code uses. */
export const NUMBER_SETTINGS = { ...TIMEOUTS, ...CLEANUP, ...GUARD, ...LOGS } as const;

export type NumberName = keyof typeof NUMBER_SETTINGS;

export type Numbers = { readonly [K in NumberName]: number };

export const NUMBER_NAMES = Object.keys(NUMBER_SETTINGS) as readonly NumberName[];

/** The defaults — what the extension uses when nothing is set, and what every test that sets nothing sees. */
export const DEFAULT_NUMBERS: Numbers = Object.fromEntries(NUMBER_NAMES.map((name) => [name, NUMBER_SETTINGS[name].default])) as Numbers;

/** One setting's value as read: an integer kept inside its range (a hand-edited value outside it is clamped), else the default. */
export function numberOf(setting: NumberSetting, value: unknown): number {
  if (typeof value !== 'number' || !Number.isInteger(value)) {
    return setting.default;
  }

  return Math.min(setting.maximum, Math.max(setting.minimum, value));
}

/** Every number, read through `get` (`getConfiguration('wslCare').get`) — at each use, so a changed setting applies at once. */
export function readNumbers(get: (key: string) => unknown): Numbers {
  return Object.fromEntries(NUMBER_NAMES.map((name) => [name, numberOf(NUMBER_SETTINGS[name], get(NUMBER_SETTINGS[name].key))])) as Numbers;
}
