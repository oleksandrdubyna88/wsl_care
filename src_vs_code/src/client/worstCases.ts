import { FALLBACK_LIMITS, type DaemonLimits } from '../shared/daemonLimits';

/**
 * The daemon's OWN worst case for each call the extension makes (plan §15q N-1–N-3, found by the E7 numbers inventory) —
 * derived from the daemon's per-command ceilings and what each call can do, in ONE place. Every host ceiling (the default
 * and the minimum of its setting, `settings/numbers.ts`) is held STRICTLY above these by `ceilings.test.ts`.
 *
 * <p>A command killed at its ceiling costs its ceiling plus the drain, waited twice — for the exit, then for the output
 * readers (`ProcessCommandRunner`). The drain and `systemctl stop`'s ceiling are the daemon's PUBLISHED values
 * (`status.limits`, daemon #17; 2 s and 120 s until a daemon says). Reading stdin is not a command: no drain.</p>
 */

/** The daemon's per-command ceilings, in seconds — each mirrors the C# constant named. */
export const DAEMON_CEILING_S = {
  /** `DockerCommands.ProbeCeiling` (`docker version`). */
  dockerProbe: 10,
  /** `DockerCommands.DiskUsageCeiling` (`system df`, `system df -v`). */
  dockerDiskUsage: 120,
  /** `DockerCommands.ListingCeiling` (the dangling-volume listing, ONE `container inspect` batch). */
  dockerListing: 30,
  /** `SystemdCommands.Ceiling` (`systemctl show`, `systemctl --version`). */
  systemdRead: 15,
  /** `UnitCommands.StartCeiling` (`systemctl start --no-block`). */
  unitStart: 30,
  /** `HealthCommands.SnapCeiling` (`snap list --all`, A9's preview). */
  snapList: 30,
  /** `StdinList.Ceiling` (A4's shown list on stdin) — not a command. */
  stdin: 10,
} as const;

/** `RunRequests.MaxQueued`: the most requests the detach's sweep can meet, each answered by one `systemctl show`. */
export const MAX_QUEUED_REQUESTS = 32;

/** One `container inspect` batch per 100 containers (`DockerCommands.InspectBatch`); the ceilings assume ONE batch (≤ 100). */
export const INSPECT_BATCHES = 1;

/** What the host adds above a worst case: the `wsl.exe` relay's start (≈ 0.13 s measured) and process spawns, with room. */
export const MARGIN_S = 10;

/**
 * Every call's worst case under the daemon's PUBLISHED limits (daemon #17, `status.limits`): the drain after a killed
 * command (`commands.drainGraceMilliseconds`, waited twice — `ProcessCommandRunner`) and `systemctl stop`'s ceiling
 * (`systemd.unitStopTimeoutSeconds`). The other per-command ceilings are the constants above.
 */
export function worstCasesOf(limits: DaemonLimits) {
  const drainS = (2 * limits.drainGraceMs) / 1000;
  const command = (ceilingS: number): number => ceilingS + drainS;
  const snapshot =
    command(DAEMON_CEILING_S.dockerProbe) + 2 * command(DAEMON_CEILING_S.dockerDiskUsage) + command(DAEMON_CEILING_S.dockerListing) + INSPECT_BATCHES * command(DAEMON_CEILING_S.dockerListing);

  return {
    /**
     * No child process (plan §15b #5) — file reads and the relay, plus, since daemon #38 (E7.S2d), the MCP servers: their
     * CPU sampled across the published window and their run logs listed within the published budget.
     */
    status: (limits.mcpCpuWindowMs + limits.mcpLogListMs) / 1000,
    /** Answers before the machine is read. Also the root check. */
    version: 0,
    /** Four `systemctl show`, `systemctl --version`, `docker version` (`DoctorRun`). */
    doctor: 5 * command(DAEMON_CEILING_S.systemdRead) + command(DAEMON_CEILING_S.dockerProbe),
    /** `preview --all`: ONE Docker snapshot. */
    preview: snapshot,
    /** `runs show`, `runs`, `logs` without `--detail`: file reads only. */
    runRead: 0,
    /**
     * A detach (`act … --confirm --detach`, `collect --detach`, `DetachedRuns`): the shown list on stdin, the request sweep
     * under the lock — one `systemctl show` per queued request, at most `MAX_QUEUED_REQUESTS` — the unit's start and, when
     * that timed out, one more `systemctl show` (plan §15q N-3: two stale requests already outran the old 90 s).
     */
    detach: DAEMON_CEILING_S.stdin + MAX_QUEUED_REQUESTS * command(DAEMON_CEILING_S.systemdRead) + command(DAEMON_CEILING_S.unitStart) + command(DAEMON_CEILING_S.systemdRead),
    /** `act --stop`: one `systemctl stop` (`RunStops`), under the published stop ceiling. */
    stop: command(limits.unitStopSeconds),
    /** One Docker snapshot (`DockerCollector.CollectAsync`) — what each Docker row of a cleanup's preview takes. */
    snapshot,
    /** A9's preview: `snap list --all`. */
    snapList: command(DAEMON_CEILING_S.snapList),
  } as const;
}

export type WorstCases = ReturnType<typeof worstCasesOf>;

/** The worst cases under today's limits (every published field at its default) — what the settings' minimums are built on. */
const TODAY = worstCasesOf(FALLBACK_LIMITS);

export const DOCKER_SNAPSHOT_S = TODAY.snapshot;

/** The fixed-shape calls' worst cases under today's limits. */
export const WORST_CASE_S = { status: TODAY.status, version: TODAY.version, doctor: TODAY.doctor, preview: TODAY.preview, runRead: TODAY.runRead, detach: TODAY.detach, stop: TODAY.stop } as const;

/**
 * What ONE row costs in `act <ids> --preview` (plan §15q N-2): each Docker action takes its OWN Docker snapshot
 * (`DockerLook.TakeAsync`), A8 reads a folder (no command), A9 lists the snaps.
 */
export const DOCKER_ROW_IDS: ReadonlySet<string> = new Set(['A4', 'A5', 'A5Testcontainers', 'A6', 'A6Unused', 'A7']);

/** The rows that take no Docker snapshot — their share, under `worst`. */
export function otherRowShareS(id: string, worst: WorstCases = TODAY): number | undefined {
  const shares: Readonly<Record<string, number>> = { A8: 0, A9: worst.snapList };

  return Object.hasOwn(shares, id) && !DOCKER_ROW_IDS.has(id) ? shares[id] : undefined;
}

/**
 * One id's share of a preview — a Docker row's snapshot, A8's or A9's cost; an id outside the rows (the extension previews
 * only rows) is counted as a Docker snapshot, the dearest unit, an ASSUMPTION rather than a derivation.
 */
export function previewShareS(id: string, worst: WorstCases = TODAY): number {
  return otherRowShareS(id, worst) ?? worst.snapshot;
}

/** `act <ids> --preview`'s worst case: the sum of its ids' shares, under the daemon's limits. */
export function previewWorstCaseS(ids: readonly string[], limits: DaemonLimits = FALLBACK_LIMITS): number {
  const worst = worstCasesOf(limits);

  return ids.reduce((sum, id) => sum + previewShareS(id, worst), 0);
}

/**
 * `wsl.exe --list`'s questions (`--list --quiet`, `-l -v`, `--list --running --quiet`) are not the daemon's: measured at about
 * 50 ms each on a warm WSL (research/2026-10-03_wsl_exe_facts.md, `WslCareClient.ts`). A cold WSL service start is NOT
 * measured — the E5 live gate's. The setting's minimum is a hundred times the measured time.
 */
export const WSL_LIST_MEASURED_S = 0.05;

export const WSL_LIST_MINIMUM_S = 5;
