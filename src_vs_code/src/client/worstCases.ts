/**
 * The daemon's OWN worst case for each call the extension makes (plan §15q N-1–N-3, found by the E7 numbers inventory) —
 * derived from the daemon's per-command ceilings and what each call can do, in ONE place. Every host ceiling (the default
 * and the minimum of its setting, `settings/numbers.ts`) is held STRICTLY above these by `ceilings.test.ts`.
 *
 * <p>A command killed at its ceiling costs its ceiling plus the drain: `ProcessCommandRunner.DrainGrace` (2 s) is waited
 * twice — for the exit, then for the output readers (`Core/Processes/ProcessCommandRunner.cs:30`, `:234`, `:241`). Reading
 * stdin is not a command: no drain.</p>
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
  /** `UnitCommands.StopCeiling` (`systemctl stop`). */
  unitStop: 120,
  /** `HealthCommands.SnapCeiling` (`snap list --all`, A9's preview). */
  snapList: 30,
  /** `StdinList.Ceiling` (A4's shown list on stdin) — not a command. */
  stdin: 10,
} as const;

/** `ProcessCommandRunner.DrainGrace` (2 s), waited twice after a kill. */
export const DRAIN_S = 4;

/** `RunRequests.MaxQueued`: the most requests the detach's sweep can meet, each answered by one `systemctl show`. */
export const MAX_QUEUED_REQUESTS = 32;

/** One `container inspect` batch per 100 containers (`DockerCommands.InspectBatch`); the ceilings assume ONE batch (≤ 100). */
export const INSPECT_BATCHES = 1;

/** What the host adds above a worst case: the `wsl.exe` relay's start (≈ 0.13 s measured) and process spawns, with room. */
export const MARGIN_S = 10;

/** One command run to its ceiling and killed. */
function command(ceilingS: number): number {
  return ceilingS + DRAIN_S;
}

/** One Docker snapshot (`DockerCollector.CollectAsync`): the probe, `system df`, `system df -v`, the listing, the inspections. */
export const DOCKER_SNAPSHOT_S =
  command(DAEMON_CEILING_S.dockerProbe) + 2 * command(DAEMON_CEILING_S.dockerDiskUsage) + command(DAEMON_CEILING_S.dockerListing) + INSPECT_BATCHES * command(DAEMON_CEILING_S.dockerListing);

/** The worst case of each fixed-shape call, in seconds. */
export const WORST_CASE_S = {
  /** No child process (plan §15b #5) — its time is file reads and the relay. */
  status: 0,
  /** Answers before the machine is read. Also the root check. */
  version: 0,
  /** Four `systemctl show`, `systemctl --version`, `docker version` (`DoctorRun`). */
  doctor: 5 * command(DAEMON_CEILING_S.systemdRead) + command(DAEMON_CEILING_S.dockerProbe),
  /** `preview --all`: ONE Docker snapshot. */
  preview: DOCKER_SNAPSHOT_S,
  /** `runs show`, `runs`, `logs` without `--detail`: file reads only. */
  runRead: 0,
  /**
   * A detach (`act … --confirm --detach`, `collect --detach`, `DetachedRuns`): the shown list on stdin, the request sweep
   * under the lock — one `systemctl show` per queued request, at most `MAX_QUEUED_REQUESTS` — the unit's start and, when
   * that timed out, one more `systemctl show` (plan §15q N-3: two stale requests already outran the old 90 s).
   */
  detach: DAEMON_CEILING_S.stdin + MAX_QUEUED_REQUESTS * command(DAEMON_CEILING_S.systemdRead) + command(DAEMON_CEILING_S.unitStart) + command(DAEMON_CEILING_S.systemdRead),
  /** `act --stop`: one `systemctl stop` (`RunStops`). */
  stop: command(DAEMON_CEILING_S.unitStop),
} as const;

/**
 * What ONE row costs in `act <ids> --preview` (plan §15q N-2): each Docker action takes its OWN Docker snapshot
 * (`DockerLook.TakeAsync`), A8 reads a folder (no command), A9 lists the snaps.
 */
export const DOCKER_ROW_IDS: ReadonlySet<string> = new Set(['A4', 'A5', 'A5Testcontainers', 'A6', 'A6Unused', 'A7']);

export const OTHER_ROW_PREVIEW_S: Readonly<Record<string, number>> = { A8: 0, A9: command(DAEMON_CEILING_S.snapList) };

/**
 * One id's share of a preview — a Docker row's snapshot, A8's or A9's cost; an id outside the rows (the extension previews
 * only rows) is counted as a Docker snapshot, the dearest unit, an ASSUMPTION rather than a derivation.
 */
export function previewShareS(id: string): number {
  return Object.hasOwn(OTHER_ROW_PREVIEW_S, id) ? (OTHER_ROW_PREVIEW_S[id] as number) : DOCKER_SNAPSHOT_S;
}

/** `act <ids> --preview`'s worst case: the sum of its ids' shares. */
export function previewWorstCaseS(ids: readonly string[]): number {
  return ids.reduce((sum, id) => sum + previewShareS(id), 0);
}
