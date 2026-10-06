/**
 * THE field map of the panel (plan §7.2, §15g B2): every row the panel shows, the section it sits in, the
 * daemon verb and JSON path it is read from, when that verb is asked again, and — for a row the four E5 verbs cannot
 * fill — the epic it arrives in.
 *
 * <p>One table, two readers: `viewModel.ts` renders the panel FROM it (the path in a row is the path the renderer
 * reads), and research/architecture.md § *The panel's field map* carries the same table generated from it
 * (`fieldMap.test.ts` holds the document equal to `fieldMapMarkdown()`; `npm run fieldmap:doc` rewrites it). A row
 * cannot be added, moved or re-pointed in one place only.</p>
 *
 * <p>Refresh triggers (§15g M1): `poll` rows come from `status`, which the FOCUSED window asks every
 * `wslCare.refreshSeconds` and once on focus; `panel` rows come from `preview` / `doctor`, asked only when the panel
 * opens or Refresh is pressed. A row that `arrives` is never asked for.</p>
 */

export type SectionId =
  | 'memory'
  | 'topHolders'
  | 'swap'
  | 'disk'
  | 'folders'
  | 'containers'
  | 'containerStarts'
  | 'cleanup'
  | 'health'
  | 'aiAgents'
  | 'lastCleanup';

/** The sections in panel order, with their titles. */
export const SECTIONS: readonly { readonly id: SectionId; readonly title: string }[] = [
  { id: 'memory', title: 'Memory' },
  { id: 'topHolders', title: 'Top holders' },
  { id: 'swap', title: 'Swap' },
  { id: 'disk', title: 'Disk' },
  { id: 'folders', title: 'Folders' },
  { id: 'containers', title: 'Containers' },
  { id: 'containerStarts', title: 'Container starts' },
  { id: 'cleanup', title: 'Cleanup' },
  { id: 'health', title: 'Health' },
  { id: 'aiAgents', title: 'AI agents' },
  { id: 'lastCleanup', title: 'Last cleanup' },
];

/** How a row's value is rendered — one renderer per kind (`rowRenderers.ts`). */
export type RowKind =
  | 'gib'
  | 'percent'
  | 'fragmentation'
  | 'unattributed'
  | 'processes'
  | 'families'
  | 'containerStats'
  | 'disk'
  | 'folders'
  | 'containersNow'
  | 'dockerEngine'
  | 'dockerTotals'
  | 'starts'
  | 'cleanupRows'
  | 'keptVolumes'
  | 'unboundedLogs'
  | 'builderGc'
  | 'buildkit'
  | 'productVersion'
  | 'healthy'
  | 'check'
  | 'configError'
  | 'checks'
  | 'versions'
  | 'verdicts'
  | 'verdict'
  | 'lastCleanup';

/** The three JSON verbs a row can be read from. */
export type RowVerb = 'status' | 'preview' | 'doctor';

/** A row filled in E5: read from `verb` at `path`. */
export interface ReadRow {
  readonly section: SectionId;
  readonly id: string;
  readonly label: string;
  readonly verb: RowVerb;
  readonly path: string;
  readonly kind: RowKind;
}

/** A row the four E5 verbs cannot fill: shown as "arrives in <epic>", never blank or 0. */
export interface ArrivingRow {
  readonly section: SectionId;
  readonly id: string;
  readonly label: string;
  readonly arrives: string;
  readonly why: string;
}

export type FieldRow = ReadRow | ArrivingRow;

export function isArriving(row: FieldRow): row is ArrivingRow {
  return 'arrives' in row;
}

/** When a verb is asked again — derived from the verb, so the table cannot claim otherwise. */
export function refreshOf(verb: RowVerb): 'poll' | 'panel' {
  return verb === 'status' ? 'poll' : 'panel';
}

const NEEDS_LOGS = 'needs the logs / runs verbs';
const NEEDS_PROBE = 'read by wsl-care.exe on the Windows side (E7.S3)';

function read(section: SectionId, id: string, label: string, verb: RowVerb, path: string, kind: RowKind): ReadRow {
  return { section, id, label, verb, path, kind };
}

function arriving(section: SectionId, id: string, label: string, arrives: string, why: string): ArrivingRow {
  return { section, id, label, arrives, why };
}

export const FIELD_MAP: readonly FieldRow[] = [
  read('memory', 'memory.total', 'VM ceiling (MemTotal)', 'status', 'vm.memory.total', 'gib'),
  read('memory', 'memory.available', 'Available (MemAvailable)', 'status', 'vm.memory.memAvailable', 'gib'),
  read('memory', 'memory.availablePercent', 'Available, share of the ceiling', 'status', 'vm.memory.availablePercent', 'percent'),
  read('memory', 'memory.free', 'Free', 'status', 'vm.memory.free', 'gib'),
  read('memory', 'memory.pageCache', 'Page cache', 'status', 'vm.memory.pageCache', 'gib'),
  read('memory', 'memory.inactiveAnon', 'Inactive anonymous', 'status', 'vm.memory.inactiveAnon', 'gib'),
  read('memory', 'memory.anon', 'Anonymous', 'status', 'vm.memory.anonPages', 'gib'),
  read('memory', 'memory.shmem', 'Shared memory (shmem)', 'status', 'vm.memory.shmem', 'gib'),
  read('memory', 'memory.fragmentation', 'Fragmentation', 'status', 'vm.memory.fragmentation', 'fragmentation'),
  read('memory', 'memory.unattributed', 'Unattributed', 'status', 'vm.unattributed', 'unattributed'),
  arriving('memory', 'memory.sparkline', 'MemAvailable today (sparkline)', 'E6', NEEDS_LOGS),
  arriving('memory', 'memory.vmmem', 'vmmemWSL', 'E7', NEEDS_PROBE),
  arriving('memory', 'memory.hostRam', 'Host RAM', 'E7', NEEDS_PROBE),
  read('topHolders', 'holders.processes', 'Top processes by held memory', 'status', 'vm.processes.top', 'processes'),
  read('topHolders', 'holders.families', 'Process families', 'status', 'vm.processes.families', 'families'),
  read('topHolders', 'holders.containers', 'Containers by memory (docker stats)', 'status', 'slow.containerStats', 'containerStats'),
  read('topHolders', 'holders.mnt', 'Processes working under /mnt', 'status', 'vm.processes.mntWalkers', 'processes'),
  read('swap', 'swap.used', 'Swap used', 'status', 'vm.memory.swapUsed', 'gib'),
  read('swap', 'swap.total', 'Swap total', 'status', 'vm.memory.swapTotal', 'gib'),
  arriving('swap', 'swap.trend', 'Swap trend today', 'E6', NEEDS_LOGS),
  read('disk', 'disk.distro', 'The distribution\'s / file system', 'status', 'vm.disk', 'disk'),
  arriving('disk', 'disk.hostC', 'C: free', 'E7', NEEDS_PROBE),
  arriving('disk', 'disk.vhdx', '.vhdx sizes', 'E11', 'read by the Windows collectors (E11)'),
  read('folders', 'folders.sizes', 'Big folders (the daily walk)', 'status', 'folders', 'folders'),
  read('containers', 'containers.now', 'Running now', 'status', 'vm.containers', 'containersNow'),
  read('containers', 'containers.engine', 'Docker engine', 'preview', 'docker', 'dockerEngine'),
  read('containers', 'containers.totals', 'Docker totals (docker system df)', 'preview', 'totals', 'dockerTotals'),
  read('containerStarts', 'starts.last24h', 'Started in the last 24 h', 'status', 'containerStarts', 'starts'),
  read('cleanup', 'cleanup.rows', 'Cleanup candidates', 'preview', 'rows', 'cleanupRows'),
  read('cleanup', 'cleanup.kept', 'Kept named volumes (never cleaned)', 'preview', 'kept', 'keptVolumes'),
  read('cleanup', 'cleanup.logs', 'Containers logging without max-size', 'preview', 'hygiene.unboundedLogs', 'unboundedLogs'),
  read('cleanup', 'cleanup.builderGc', 'Builder garbage collection (daemon.json)', 'preview', 'hygiene.builderGc', 'builderGc'),
  read('cleanup', 'cleanup.buildkit', 'Forgotten buildx builders', 'preview', 'hygiene.buildkit', 'buildkit'),
  read('health', 'health.version', 'Daemon version', 'status', 'productVersion', 'productVersion'),
  read('health', 'health.healthy', 'Healthy', 'doctor', 'healthy', 'healthy'),
  read('health', 'health.lastRun', 'Last full run', 'doctor', 'checks[id=lastRun]', 'check'),
  read('health', 'health.configError', 'Configuration error', 'doctor', 'configError', 'configError'),
  read('health', 'health.checks', 'Checks', 'doctor', 'checks', 'checks'),
  read('health', 'health.versions', 'Versions', 'doctor', 'versions', 'versions'),
  read('health', 'health.verdicts', 'Verdicts', 'status', 'verdicts', 'verdicts'),
  read('health', 'health.clockJumps', 'Clock jumps', 'status', 'verdicts[id=clock.jumps]', 'verdict'),
  read('health', 'health.journalSpan', 'Journal history', 'status', 'verdicts[id=journal.history]', 'verdict'),
  arriving('health', 'health.warningsSince', 'Warnings since the last full run', 'E6', NEEDS_LOGS),
  arriving('aiAgents', 'agents.all', 'AI agents (both sides, Add CLI path)', 'E7', 'needs the agents list verb (E7)'),
  read('lastCleanup', 'lastCleanup.run', 'Last cleanup', 'status', 'lastCleanup', 'lastCleanup'),
];
