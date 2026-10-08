namespace WslCare.Core.Config;

/// <summary>
/// E7.S2c, the owner's rule (2026-10-05): every behavioural number is configuration. Group A — behaviour — are ordinary keys;
/// group B — a limit on what ROOT reads, does or waits for — are machine-layer-only keys whose range maximum is the hard limit
/// (lowered freely, never raised past it; a raise-only key has today's value as its minimum instead). Every default is today's
/// value in <c>default.json</c>; coupled limits are checked at load (<see cref="NumberRules"/>); a number reaches its call site
/// through <see cref="Tuning"/>. The inventory and the group-C formats are plan §15q *E7.S2c*.
/// </summary>
public static partial class ConfigKeys
{
    public static partial class Walk
    {
        /// <summary>The folders are walked again when the newest walk is older than this. Default 20.</summary>
        public static readonly ConfigKey.IntKey IntervalHours = new("walk.intervalHours", 4, 168) { Trust = KeyTrust.Higher };

        /// <summary>How many folders a walk left out are named; past it the answer says how many more. Default 20.</summary>
        public static readonly ConfigKey.IntKey MaxExclusionsNamed = new("walk.maxExclusionsNamed", 0, 100) { Trust = KeyTrust.Display };

        /// <summary>Entries one folder walk may see. Default 2000000.</summary>
        public static readonly ConfigKey.IntKey MaxEntries = new("walk.maxEntries", 1000, 2000000) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>Time one folder walk may take. Default 120.</summary>
        public static readonly ConfigKey.IntKey MaxSeconds = new("walk.maxSeconds", 5, 120) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }

    public static partial class Agents
    {
        /// <summary>agents list --measure and agents probe: the whole walk; below every host call ceiling. Default 60.</summary>
        public static readonly ConfigKey.IntKey MeasureBudgetSeconds = new("agents.measureBudgetSeconds", 5, 60) { Trust = KeyTrust.Lower };

        /// <summary>The whole agent walk inside a root collect. Default 180.</summary>
        public static readonly ConfigKey.IntKey WalkBudgetSeconds = new("agents.walkBudgetSeconds", 10, 180) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>Entries one session listing may see. Default 500000.</summary>
        public static readonly ConfigKey.IntKey SessionMaxEntries = new("agents.sessionMaxEntries", 1000, 500000) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>Bytes of a package.json read for a version. Default 1048576.</summary>
        public static readonly ConfigKey.IntKey MaxPackageJsonBytes = new("agents.maxPackageJsonBytes", 65536, 1048576) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>The whole binary lookup of one discovery. Default 10.</summary>
        public static readonly ConfigKey.IntKey LookupCeilingSeconds = new("agents.lookupCeilingSeconds", 1, 10) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }

    public static partial class Runs
    {
        /// <summary>How long a stop marker is kept. Default 24.</summary>
        public static readonly ConfigKey.IntKey StopMarkerRetentionHours = new("runs.stopMarkerRetentionHours", 1, 168) { Trust = KeyTrust.Higher };

        /// <summary>History lines and run details are kept this long. Default 90.</summary>
        public static readonly ConfigKey.IntKey HistoryRetentionDays = new("runs.historyRetentionDays", 7, 366) { Trust = new(SafeDirection.Higher, TightenOnlyForRoot: true) };
    }

    public static partial class Preview
    {
        /// <summary>How many objects a preview names (the confirmation modal). Default 20.</summary>
        public static readonly ConfigKey.IntKey MaxItems = new("preview.maxItems", 1, 100) { Trust = KeyTrust.Display };
    }

    public static partial class Clock
    {
        /// <summary>A16 corrects the clock at most once in this time. Default 60.</summary>
        public static readonly ConfigKey.IntKey MinimumGapMinutes = new("clock.minimumGapMinutes", 60, 1440) { Trust = KeyTrust.Higher };

        /// <summary>A16 acts only on two drift observations at least this far apart. Default 5.</summary>
        public static readonly ConfigKey.IntKey DriftObservationsApartMinutes = new("clock.driftObservationsApartMinutes", 1, 1440) { Trust = KeyTrust.Higher };

        /// <summary>hwclock -s / chronyc makestep. Default 30.</summary>
        public static readonly ConfigKey.IntKey StepTimeoutSeconds = new("clock.stepTimeoutSeconds", 1, 30) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>The clock reference's HEAD (curl's <c>--max-time</c> and its kill ceiling). Default 10.</summary>
        public static readonly ConfigKey.IntKey ReferenceTimeoutSeconds = new("clock.referenceTimeoutSeconds", 1, 30) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }

    public static partial class Trim
    {
        /// <summary>A15 trims the filesystems once in this many days. Default 7.</summary>
        public static readonly ConfigKey.IntKey PeriodDays = new("trim.periodDays", 1, 90) { Trust = KeyTrust.Higher };

        /// <summary>fstrim -av. Default 600.</summary>
        public static readonly ConfigKey.IntKey TimeoutSeconds = new("trim.timeoutSeconds", 60, 600) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }

    public static partial class AptCache
    {
        /// <summary>A9 cleans the apt cache above this size. Default 200.</summary>
        public static readonly ConfigKey.IntKey TriggerMb = new("aptCache.triggerMb", 0, 100000) { Trust = KeyTrust.Higher };

        /// <summary>apt-get clean. Default 300.</summary>
        public static readonly ConfigKey.IntKey CleanTimeoutSeconds = new("aptCache.cleanTimeoutSeconds", 30, 300) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }

    public static partial class ToolCaches
    {
        /// <summary>A17 trims when one tool cache is above this size. Default 5.</summary>
        public static readonly ConfigKey.IntKey TriggerGb = new("toolCaches.triggerGb", 0, 100000) { Trust = KeyTrust.Higher };

        /// <summary>pnpm store prune / uv cache prune / pip cache purge. Default 600.</summary>
        public static readonly ConfigKey.IntKey TrimTimeoutSeconds = new("toolCaches.trimTimeoutSeconds", 60, 600) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>A tool asked where its cache is. Default 30.</summary>
        public static readonly ConfigKey.IntKey WhereTimeoutSeconds = new("toolCaches.whereTimeoutSeconds", 5, 30) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>Output kept of a tool's where-is-your-cache answer. Default 65536.</summary>
        public static readonly ConfigKey.IntKey WhereOutputCapBytes = new("toolCaches.whereOutputCapBytes", 4096, 65536) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }

    public static partial class EditorServers
    {
        /// <summary>A14 keeps this many newest builds of each editor server. Default 2.</summary>
        public static readonly ConfigKey.IntKey KeepNewest = new("editorServers.keepNewest", 1, 20) { Trust = KeyTrust.Higher };
    }

    public static partial class Processes
    {
        /// <summary>A11: a suspect must use no CPU in this window. Default 5.</summary>
        public static readonly ConfigKey.IntKey CpuWindowSeconds = new("processes.cpuWindowSeconds", 5, 60) { Trust = KeyTrust.Higher };

        /// <summary>A11 and A18: SIGKILL this long after SIGTERM (raise-only: plan section 5 fixes 10 s as the least). Default 10.</summary>
        public static readonly ConfigKey.IntKey TermGraceSeconds = new("processes.termGraceSeconds", 10, 120) { Trust = KeyTrust.Higher };

        /// <summary>How many processes a full run names. Default 30.</summary>
        public static readonly ConfigKey.IntKey TopCount = new("processes.topCount", 0, 200) { Trust = KeyTrust.Display };

        /// <summary>The wait after SIGKILL before "still running". Default 5.</summary>
        public static readonly ConfigKey.IntKey KillWaitSeconds = new("processes.killWaitSeconds", 1, 30) { Trust = new(SafeDirection.None, MachineOnly: true) };

        /// <summary>Characters of a command line a record keeps. Default 200.</summary>
        public static readonly ConfigKey.IntKey ShownCommandChars = new("processes.shownCommandChars", 0, 200) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>The poll slice while waiting for processes to end. Default 200.</summary>
        public static readonly ConfigKey.IntKey SignalSliceMilliseconds = new("processes.signalSliceMilliseconds", 50, 1000) { Trust = new(SafeDirection.None, MachineOnly: true) };
    }

    public static partial class Events
    {
        /// <summary>Container-start lines are kept this long. Default 14.</summary>
        public static readonly ConfigKey.IntKey StartsRetentionDays = new("events.startsRetentionDays", 1, 90) { Trust = new(SafeDirection.Higher, MachineOnly: true) };

        /// <summary>The first wait for the Docker socket. Default 5.</summary>
        public static readonly ConfigKey.IntKey RetryFirstSeconds = new("events.retryFirstSeconds", 1, 60) { Trust = KeyTrust.Higher };

        /// <summary>The longest wait for the Docker socket. Default 300.</summary>
        public static readonly ConfigKey.IntKey RetryMaxSeconds = new("events.retryMaxSeconds", 5, 3600) { Trust = KeyTrust.Higher };

        /// <summary>Each wait for the Docker socket is this many times the last. Default 2.</summary>
        public static readonly ConfigKey.IntKey RetryFactor = new("events.retryFactor", 1, 10) { Trust = KeyTrust.Higher };

        /// <summary>The follower counts as not running this long after its segment should have ended. Default 5.</summary>
        public static readonly ConfigKey.IntKey StalenessSlackMinutes = new("events.stalenessSlackMinutes", 1, 60) { Trust = KeyTrust.Display };

        /// <summary>How many images the starts summary names. Default 5.</summary>
        public static readonly ConfigKey.IntKey TopImages = new("events.topImages", 0, 50) { Trust = KeyTrust.Display };

        /// <summary>One docker events segment. Default 10.</summary>
        public static readonly ConfigKey.IntKey SegmentMinutes = new("events.segmentMinutes", 1, 50) { Trust = KeyTrust.Display };

        /// <summary>A segment's ceiling beyond its length. Default 60.</summary>
        public static readonly ConfigKey.IntKey SegmentSlackSeconds = new("events.segmentSlackSeconds", 10, 60) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>A segment that ends this much early is abnormal. Default 2.</summary>
        public static readonly ConfigKey.IntKey EarlyEndSeconds = new("events.earlyEndSeconds", 1, 10) { Trust = new(SafeDirection.None, MachineOnly: true) };
    }

    public static partial class Thresholds
    {
        /// <summary>The page cache warns above this. Default 15.</summary>
        public static readonly ConfigKey.IntKey PageCacheWarnGib = new("thresholds.pageCacheWarnGib", 0, 100000) { Trust = KeyTrust.Display };

        /// <summary>A1 acts when the page cache is above this and available memory below the next key. Default 12.</summary>
        public static readonly ConfigKey.IntKey PageCacheActGib = new("thresholds.pageCacheActGib", 0, 100000) { Trust = KeyTrust.Higher };

        /// <summary>A1 acts when available memory is below this and the page cache above the last key. Default 30.</summary>
        public static readonly ConfigKey.IntKey PageCacheActAvailablePercent = new("thresholds.pageCacheActAvailablePercent", 0, 100) { Trust = KeyTrust.Lower };

        /// <summary>Inactive anonymous memory warns above this. Default 15.</summary>
        public static readonly ConfigKey.IntKey InactiveAnonWarnGib = new("thresholds.inactiveAnonWarnGib", 0, 100000) { Trust = KeyTrust.Display };

        /// <summary>Order-7 free blocks warn below this. Default 32.</summary>
        public static readonly ConfigKey.IntKey Order7WarnBlocks = new("thresholds.order7WarnBlocks", 0, 10000) { Trust = KeyTrust.Display };

        /// <summary>Memory pressure (PSI some avg10) warns above this. Default 10.</summary>
        public static readonly ConfigKey.IntKey MemoryPressureWarn = new("thresholds.memoryPressureWarn", 0, 100) { Trust = KeyTrust.Display };

        /// <summary>E14 S6: cpu pressure (PSI some avg60, %) above this makes the machine BUSY — the <c>pressure.cpu</c> verdict
        /// and <c>wsl-care busy</c>. Default 20 (the 2026-10-07 evening read 31 % avg10; a calm machine reads about 4).</summary>
        public static readonly ConfigKey.IntKey CpuPressureWarnPercent = new("thresholds.cpuPressureWarnPercent", 0, 100) { Trust = KeyTrust.Display };

        /// <summary>E14 S6: io pressure (PSI some avg60, %) above this makes the machine BUSY — <c>pressure.io</c> and
        /// <c>wsl-care busy</c>. Default 10.</summary>
        public static readonly ConfigKey.IntKey IoPressureWarnPercent = new("thresholds.ioPressureWarnPercent", 0, 100) { Trust = KeyTrust.Display };

        /// <summary>E14 S5: swap LEFT (<c>SwapFree</c>, GB) under this warns — <c>memory.swapFree</c>; a swap smaller than the key is
        /// judged by <c>memory.swap</c> only. Default 4 (the 2026-10-07 evening had 2.4 GB of 12 left).</summary>
        public static readonly ConfigKey.IntKey SwapFreeWarnGb = new("thresholds.swapFreeWarnGb", 0, GbCeiling) { Trust = KeyTrust.Display };

        /// <summary>E14 S5: what the kernel PROMISED (<c>Committed_AS</c>) above this share of <c>MemTotal</c> (%) warns —
        /// <c>memory.committed</c>. Default 80 (the evening read 104 %, the calm 2026-10-02 tree 46 %).</summary>
        public static readonly ConfigKey.IntKey CommittedWarnPercent = new("thresholds.committedWarnPercent", 1, 1000) { Trust = KeyTrust.Display };

        /// <summary>The root volume warns above this use. Default 80.</summary>
        public static readonly ConfigKey.IntKey RootUsedWarnPercent = new("thresholds.rootUsedWarnPercent", 0, 100) { Trust = KeyTrust.Display };

        /// <summary>The journal warns when it holds less history than this. Default 7.</summary>
        public static readonly ConfigKey.IntKey JournalHistoryWarnDays = new("thresholds.journalHistoryWarnDays", 0, 3650) { Trust = KeyTrust.Display };

        /// <summary>Clock jumps warn above this many in 4 hours. Default 100.</summary>
        public static readonly ConfigKey.IntKey ClockJumpsWarnPer4h = new("thresholds.clockJumpsWarnPer4h", 0, 1000000) { Trust = KeyTrust.Display };

        /// <summary>journald's "Time jumped backwards" warn above this many in the last 4 hours of this boot (the clock fight,
        /// PLAN_windows_time_guard.md D4; ≈ 436 per 4 h in the incident of 2026-10-08, none on a quiet day). Default 10.</summary>
        public static readonly ConfigKey.IntKey TimeJumpsBackWarnPer4h = new("thresholds.timeJumpsBackWarnPer4h", 0, 1000000) { Trust = KeyTrust.Display };

        /// <summary>A sysstat or atop sample older than this means the collector stopped. Default 30.</summary>
        public static readonly ConfigKey.IntKey CollectorFreshMinutes = new("thresholds.collectorFreshMinutes", 1, 1440) { Trust = KeyTrust.Display };

        /// <summary>The VM using this much of its ceiling is critical. Default 90.</summary>
        public static readonly ConfigKey.IntKey WslMemoryCriticalPercent = new("thresholds.wslMemoryCriticalPercent", 0, 100) { Trust = KeyTrust.Display };
    }

    public static partial class Journal
    {
        /// <summary>The journal warns, and A10 vacuums, above this size. Default 1.</summary>
        public static readonly ConfigKey.IntKey MaxGb = new("journal.maxGb", 0, 100000) { Trust = KeyTrust.Higher };

        /// <summary>journalctl --vacuum-time. Default 300.</summary>
        public static readonly ConfigKey.IntKey VacuumTimeoutSeconds = new("journal.vacuumTimeoutSeconds", 30, 300) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }

    public static partial class WslConfig
    {
        /// <summary>The .wslconfig memory the audit recommends (shown, never applied). Default 36.</summary>
        public static readonly ConfigKey.IntKey RecommendedMemoryGb = new("wslConfig.recommendedMemoryGb", 1, 1024) { Trust = KeyTrust.Display };

        /// <summary>E14 S5: the <c>.wslconfig</c> swap the advice recommends (shown, never written); 0 advises no swap line. Default
        /// 16 (the 2026-10-07 evening used 9.9 of 12 GB).</summary>
        public static readonly ConfigKey.IntKey RecommendedSwapGb = new("wslConfig.recommendedSwapGb", 0, 1024) { Trust = KeyTrust.Display };

        /// <summary>E14 S7a: above this many GiB held by <c>vmmemWSL</c>, the Windows binary's <c>status</c> shows the reclaim advice
        /// (text, never acted on). Default 24.</summary>
        public static readonly ConfigKey.IntKey VmmemAdviceGb = new("wslConfig.vmmemAdviceGb", 1, 1024) { Trust = KeyTrust.Display };
    }

    public static partial class Requests
    {
        /// <summary>Request files one reader opens (at least requests.maxQueued). Default 64.</summary>
        public static readonly ConfigKey.IntKey MaxRead = new("requests.maxRead", 1, 64) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>Requests waiting at once; one more is refused (exit 73). Default 32.</summary>
        public static readonly ConfigKey.IntKey MaxQueued = new("requests.maxQueued", 1, 32) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>The largest request file root reads. Default 1048576.</summary>
        public static readonly ConfigKey.IntKey MaxBytes = new("requests.maxBytes", 65536, 1048576) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>How long a request waits for its unit before it is swept (raise-only). Default 60.</summary>
        public static readonly ConfigKey.IntKey GraceSeconds = new("requests.graceSeconds", 60, 600) { Trust = new(SafeDirection.Higher, MachineOnly: true) };

        /// <summary>How far a request may be ahead of the clock before it is stale (published in status --json limits). Default 300.</summary>
        public static readonly ConfigKey.IntKey FutureSkewSeconds = new("requests.futureSkewSeconds", 60, 3600) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>How long an ACCEPTED detached run waits for THE run lock before it is recorded refused — a --detach check holds
        /// the lock for its sweep and count (retro round over PR #11, O1); 0 is the old refuse-at-once. Default 30.</summary>
        public static readonly ConfigKey.IntKey LockWaitSeconds = new("requests.lockWaitSeconds", 0, 120) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }

    public static partial class Act
    {
        /// <summary>Names in one shown list (--volume, --only, a request). Default 10000.</summary>
        public static readonly ConfigKey.IntKey MaxShownNames = new("act.maxShownNames", 1, 10000) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>Bytes of a list on stdin, an --only file or a request file (at least 67 bytes a shown name). Default 1048576.</summary>
        public static readonly ConfigKey.IntKey MaxListBytes = new("act.maxListBytes", 65536, 1048576) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>The wait for the end of a list on stdin. Default 10.</summary>
        public static readonly ConfigKey.IntKey StdinTimeoutSeconds = new("act.stdinTimeoutSeconds", 1, 10) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }

    public static partial class Stops
    {
        /// <summary>Bytes of a stop marker read. Default 4096.</summary>
        public static readonly ConfigKey.IntKey MaxMarkerBytes = new("stops.maxMarkerBytes", 256, 4096) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }

    public static partial class Timer
    {
        /// <summary>The timer runs dry this many days after its first run (raise-only). Default 7.</summary>
        public static readonly ConfigKey.IntKey FirstDryWindowDays = new("timer.firstDryWindowDays", 7, 3650) { Trust = new(SafeDirection.Higher, MachineOnly: true) };

        /// <summary>The timer runs a full check this often (installed as the timer drop-in). Default 4.</summary>
        public static readonly ConfigKey.IntKey PeriodHours = new("timer.periodHours", 1, 24) { Trust = new(SafeDirection.Higher, MachineOnly: true) };

        /// <summary>doctor counts the timer as not running this long after a period. Default 60.</summary>
        public static readonly ConfigKey.IntKey LateSlackMinutes = new("timer.lateSlackMinutes", 10, 1440) { Trust = new(SafeDirection.Higher, MachineOnly: true) };

        /// <summary>The timer drop-in RandomizedDelaySec. Default 5.</summary>
        public static readonly ConfigKey.IntKey RandomizedDelayMinutes = new("timer.randomizedDelayMinutes", 0, 60) { Trust = new(SafeDirection.None, MachineOnly: true) };

        /// <summary>The timer drop-in AccuracySec. Default 1.</summary>
        public static readonly ConfigKey.IntKey AccuracyMinutes = new("timer.accuracyMinutes", 1, 60) { Trust = new(SafeDirection.None, MachineOnly: true) };

        /// <summary>wsl-care.service's TimeoutStartSec: the backstop that ends a timer run as a whole (E7.S2b/S2c review C-H2; at least
        /// the derived worst case of a timer run). Default 240.</summary>
        public static readonly ConfigKey.IntKey RunLimitMinutes = new("timer.runLimitMinutes", 60, 1440) { Trust = new(SafeDirection.Higher, MachineOnly: true) };
    }

    public static partial class Units
    {
        /// <summary>The services drop-in Nice. Default 19.</summary>
        public static readonly ConfigKey.IntKey Nice = new("units.nice", 0, 19) { Trust = new(SafeDirection.Higher, MachineOnly: true) };

        /// <summary>The services drop-in MemoryMax. Default 1024.</summary>
        public static readonly ConfigKey.IntKey MemoryMaxMb = new("units.memoryMaxMb", 256, 8192) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>The services drop-in TimeoutStopSec. Default 90.</summary>
        public static readonly ConfigKey.IntKey StopTimeoutSeconds = new("units.stopTimeoutSeconds", 30, 300) { Trust = new(SafeDirection.None, MachineOnly: true) };

        /// <summary>The follower service drop-in RestartSec. Default 30.</summary>
        public static readonly ConfigKey.IntKey EventsRestartSeconds = new("units.eventsRestartSeconds", 5, 600) { Trust = new(SafeDirection.Higher, MachineOnly: true) };
    }

    public static partial class Docker
    {
        /// <summary>docker version / network inspect / help. Default 10.</summary>
        public static readonly ConfigKey.IntKey ProbeTimeoutSeconds = new("docker.probeTimeoutSeconds", 1, 10) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>docker volume ls / ps / stats / inspect. Default 30.</summary>
        public static readonly ConfigKey.IntKey ListTimeoutSeconds = new("docker.listTimeoutSeconds", 5, 30) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>docker system df. Default 120.</summary>
        public static readonly ConfigKey.IntKey DiskUsageTimeoutSeconds = new("docker.diskUsageTimeoutSeconds", 10, 120) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>docker volume rm / rm -v (one batch). Default 300.</summary>
        public static readonly ConfigKey.IntKey RemoveTimeoutSeconds = new("docker.removeTimeoutSeconds", 30, 300) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>docker image / builder prune. Default 900.</summary>
        public static readonly ConfigKey.IntKey PruneTimeoutSeconds = new("docker.pruneTimeoutSeconds", 60, 900) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>Ids in one inspect or rm command. Default 100.</summary>
        public static readonly ConfigKey.IntKey BatchSize = new("docker.batchSize", 1, 100) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>Output kept of docker version, system df, network inspect, help. Default 1048576.</summary>
        public static readonly ConfigKey.IntKey SmallOutputCapBytes = new("docker.smallOutputCapBytes", 65536, 1048576) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>Output kept of docker system df -v, volume ls, ps -a, stats. Default 67108864.</summary>
        public static readonly ConfigKey.IntKey LargeOutputCapBytes = new("docker.largeOutputCapBytes", 1048576, 67108864) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>Output kept of a Docker cleanup command. Default 4194304.</summary>
        public static readonly ConfigKey.IntKey ActionOutputCapBytes = new("docker.actionOutputCapBytes", 65536, 4194304) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>Bytes of Docker Desktop's daemon.json read. Default 1048576.</summary>
        public static readonly ConfigKey.IntKey MaxDaemonJsonBytes = new("docker.maxDaemonJsonBytes", 65536, 1048576) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }

    public static partial class Systemd
    {
        /// <summary>systemctl / journalctl / timedatectl reads. Default 15.</summary>
        public static readonly ConfigKey.IntKey TimeoutSeconds = new("systemd.timeoutSeconds", 1, 15) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>A journal search. Default 30.</summary>
        public static readonly ConfigKey.IntKey SearchTimeoutSeconds = new("systemd.searchTimeoutSeconds", 5, 30) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>Output kept of a systemctl / journalctl read. Default 1048576.</summary>
        public static readonly ConfigKey.IntKey OutputCapBytes = new("systemd.outputCapBytes", 65536, 1048576) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>Output kept of a journal search. Default 4194304.</summary>
        public static readonly ConfigKey.IntKey SearchOutputCapBytes = new("systemd.searchOutputCapBytes", 262144, 4194304) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>Output kept of systemctl start / stop. Default 65536.</summary>
        public static readonly ConfigKey.IntKey UnitOutputCapBytes = new("systemd.unitOutputCapBytes", 4096, 65536) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>systemctl start --no-block. Default 30.</summary>
        public static readonly ConfigKey.IntKey UnitStartTimeoutSeconds = new("systemd.unitStartTimeoutSeconds", 5, 30) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>systemctl stop: at least units.stopTimeoutSeconds + 30 (raise-only). Default 120.</summary>
        public static readonly ConfigKey.IntKey UnitStopTimeoutSeconds = new("systemd.unitStopTimeoutSeconds", 120, 600) { Trust = new(SafeDirection.Higher, MachineOnly: true) };
    }

    public static partial class Memory
    {
        /// <summary>sync before dropping caches. Default 120.</summary>
        public static readonly ConfigKey.IntKey SyncTimeoutSeconds = new("memory.syncTimeoutSeconds", 10, 120) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>sysctl vm.drop_caches. Default 30.</summary>
        public static readonly ConfigKey.IntKey DropCachesTimeoutSeconds = new("memory.dropCachesTimeoutSeconds", 5, 30) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>sysctl vm.compact_memory. Default 120.</summary>
        public static readonly ConfigKey.IntKey CompactTimeoutSeconds = new("memory.compactTimeoutSeconds", 10, 120) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }

    public static partial class Snap
    {
        /// <summary>snap remove --revision. Default 300.</summary>
        public static readonly ConfigKey.IntKey RemoveTimeoutSeconds = new("snap.removeTimeoutSeconds", 30, 300) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }

    public static partial class BuildServers
    {
        /// <summary>dotnet build-server shutdown. Default 120.</summary>
        public static readonly ConfigKey.IntKey ShutdownTimeoutSeconds = new("buildServers.shutdownTimeoutSeconds", 10, 120) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>E14 S3: A3's timer runs <c>dotnet build-server shutdown</c> — which stops EVERY server of the user — only when
        /// every one used no CPU for this many minutes, measured by identity over the timer's CPU history. A longer window stops
        /// less, so higher is safer. Default 60; on the 4-hour timer it is a floor.</summary>
        public static readonly ConfigKey.IntKey IdleMinutes = new("buildServers.idleMinutes", 10, 10080) { Trust = KeyTrust.Higher };
    }

    public static partial class Npm
    {
        /// <summary>npm cache clean. Default 600.</summary>
        public static readonly ConfigKey.IntKey CleanTimeoutSeconds = new("npm.cleanTimeoutSeconds", 60, 600) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }

    public static partial class Nuget
    {
        /// <summary>dotnet nuget locals http-cache --clear. Default 600.</summary>
        public static readonly ConfigKey.IntKey ClearTimeoutSeconds = new("nuget.clearTimeoutSeconds", 60, 600) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }

    public static partial class Health
    {
        /// <summary>The Windows clock probe. Default 20.</summary>
        public static readonly ConfigKey.IntKey WindowsClockTimeoutSeconds = new("health.windowsClockTimeoutSeconds", 5, 20) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>snap list --all. Default 30.</summary>
        public static readonly ConfigKey.IntKey SnapTimeoutSeconds = new("health.snapTimeoutSeconds", 5, 30) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>Output kept of the health probes. Default 1048576.</summary>
        public static readonly ConfigKey.IntKey OutputCapBytes = new("health.outputCapBytes", 65536, 1048576) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>Kernel log lines copied into a report. Default 5.</summary>
        public static readonly ConfigKey.IntKey KernelLinesKept = new("health.kernelLinesKept", 0, 5) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>Characters of each copied kernel line. Default 200.</summary>
        public static readonly ConfigKey.IntKey KernelLineChars = new("health.kernelLineChars", 0, 200) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>Bytes of .wslconfig read. Default 1048576.</summary>
        public static readonly ConfigKey.IntKey MaxWslConfigBytes = new("health.maxWslConfigBytes", 65536, 1048576) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }

    public static partial class Commands
    {
        /// <summary>Output kept of a command without its own cap. Default 1048576.</summary>
        public static readonly ConfigKey.IntKey OutputCapBytes = new("commands.outputCapBytes", 65536, 1048576) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>The whole lookup of a program on the Windows system drive. Default 5.</summary>
        public static readonly ConfigKey.IntKey SystemDriveLookupSeconds = new("commands.systemDriveLookupSeconds", 1, 30) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>No command may run longer; every timeout key is checked against it. Default 24.</summary>
        public static readonly ConfigKey.IntKey MaxTimeoutHours = new("commands.maxTimeoutHours", 1, 24) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>The wait for a command's output streams after it exits or is killed. Default 2000.</summary>
        public static readonly ConfigKey.IntKey DrainGraceMilliseconds = new("commands.drainGraceMilliseconds", 500, 10000) { Trust = new(SafeDirection.None, MachineOnly: true) };
    }

    public static partial class UserFiles
    {
        /// <summary>Bytes of a user's small file read by root (nvm's alias). Default 4096.</summary>
        public static readonly ConfigKey.IntKey MaxSmallFileBytes = new("userFiles.maxSmallFileBytes", 256, 4096) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>Bytes of a user's JSON file read by root (browsers.json, .obsolete). Default 1048576.</summary>
        public static readonly ConfigKey.IntKey MaxJsonBytes = new("userFiles.maxJsonBytes", 65536, 1048576) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>Bytes of a Playwright link file read by root. Default 65536.</summary>
        public static readonly ConfigKey.IntKey MaxLinkBytes = new("userFiles.maxLinkBytes", 4096, 65536) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }

    public static partial class Records
    {
        /// <summary>Characters of a tool's message a record keeps. Default 300.</summary>
        public static readonly ConfigKey.IntKey MaxReasonChars = new("records.maxReasonChars", 0, 300) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>The wait for a record file lock. Default 5.</summary>
        public static readonly ConfigKey.IntKey LockTimeoutSeconds = new("records.lockTimeoutSeconds", 1, 30) { Trust = new(SafeDirection.None, MachineOnly: true) };

        /// <summary>Bytes of one of root's small state files read (running.json, a run detail, the seen volumes). Default 1048576.</summary>
        public static readonly ConfigKey.IntKey MaxStateFileBytes = new("records.maxStateFileBytes", 65536, 16777216) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>Bytes of history.jsonl or a container-starts file read. Default 268435456.</summary>
        public static readonly ConfigKey.IntKey MaxHistoryBytes = new("records.maxHistoryBytes", 67108864, 1073741824) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }

    public static partial class Logs
    {
        /// <summary>Run details one logs answer opens. Default 50.</summary>
        public static readonly ConfigKey.IntKey MaxDetailsRead = new("logs.maxDetailsRead", 1, 50) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>The longest logs / runs range (at least runs.historyRetentionDays). Default 366.</summary>
        public static readonly ConfigKey.IntKey MaxRangeDays = new("logs.maxRangeDays", 90, 366) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }

    public static partial class Running
    {
        /// <summary>running.json heartbeat period (wedged-after must be at least 3 times this). Default 5.</summary>
        public static readonly ConfigKey.IntKey HeartbeatSeconds = new("running.heartbeatSeconds", 1, 60) { Trust = new(SafeDirection.None, MachineOnly: true) };

        /// <summary>A heartbeat older than this means wedged. Default 30.</summary>
        public static readonly ConfigKey.IntKey WedgedAfterSeconds = new("running.wedgedAfterSeconds", 10, 600) { Trust = new(SafeDirection.Higher, MachineOnly: true) };

        /// <summary>Reads of running.json before a verdict. Default 3.</summary>
        public static readonly ConfigKey.IntKey ReadRetries = new("running.readRetries", 1, 10) { Trust = new(SafeDirection.None, MachineOnly: true) };

        /// <summary>The pause between those reads. Default 100.</summary>
        public static readonly ConfigKey.IntKey ReadRetryMilliseconds = new("running.readRetryMilliseconds", 10, 1000) { Trust = new(SafeDirection.None, MachineOnly: true) };

        /// <summary>A run whose heartbeat is fresh but that made no step of progress (a command started or ended, a folder walked,
        /// an action begun) for this long reads WEDGED, so act --stop can end it (E7.S2b/S2c review C-H2; at least the longest single
        /// command ceiling plus its drain and a margin). Default 20.</summary>
        public static readonly ConfigKey.IntKey NoProgressMinutes = new("running.noProgressMinutes", 5, 1440) { Trust = new(SafeDirection.Higher, MachineOnly: true) };
    }

    public static partial class ConfigLayerLimits
    {
        /// <summary>Bytes of the user layer read (the machine layer is read with this range maximum). Default 262144.</summary>
        public static readonly ConfigKey.IntKey MaxLayerBytes = new("config.maxLayerBytes", 4096, 262144) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }

    public static partial class Patterns
    {
        /// <summary>The match ceiling of every fixed pattern (a ReDoS guard). Default 250.</summary>
        public static readonly ConfigKey.IntKey MatchTimeoutMilliseconds = new("patterns.matchTimeoutMilliseconds", 50, 1000) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }

    public static partial class FileLocks
    {
        /// <summary>Windows: how long a busy rename is retried. Default 2000.</summary>
        public static readonly ConfigKey.IntKey RenameRetryMilliseconds = new("files.renameRetryMilliseconds", 100, 10000) { Trust = new(SafeDirection.None, MachineOnly: true) };

        /// <summary>Windows: the pause between those tries. Default 10.</summary>
        public static readonly ConfigKey.IntKey RenameRetrySleepMilliseconds = new("files.renameRetrySleepMilliseconds", 1, 1000) { Trust = new(SafeDirection.None, MachineOnly: true) };

        /// <summary>The least random pause between lock tries. Default 5.</summary>
        public static readonly ConfigKey.IntKey LockJitterMinMilliseconds = new("files.lockJitterMinMilliseconds", 1, 100) { Trust = new(SafeDirection.None, MachineOnly: true) };

        /// <summary>The most random pause between lock tries. Default 25.</summary>
        public static readonly ConfigKey.IntKey LockJitterMaxMilliseconds = new("files.lockJitterMaxMilliseconds", 2, 1000) { Trust = new(SafeDirection.None, MachineOnly: true) };
    }

    public static partial class AgentCpu
    {
        /// <summary>The timer's CPU history (A18, A19, A3's timer): the most process identities kept. Default 512.</summary>
        public static readonly ConfigKey.IntKey MaxEntries = new("agentCpu.maxEntries", 16, 512) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>The timer's CPU history (A18, A19, A3's timer): the most bytes read (a full 512-entry history with both clocks is ~157 KiB, review A-M4). Default 262144.</summary>
        public static readonly ConfigKey.IntKey MaxBytes = new("agentCpu.maxBytes", 16384, 262144) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }

    public static partial class McpServers
    {
        /// <summary>The window an MCP server's CPU is measured across: two /proc reads this far apart (plan §15q E7.S2d). Root waits on
        /// it inside a full run, so machine-only; at 100 ticks/s one tick is 1 % of a core over 1 s. Default 1000.</summary>
        public static readonly ConfigKey.IntKey CpuWindowMilliseconds = new("mcpServers.cpuWindowMilliseconds", 200, 5000) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>Below this CPU % of one core an instance is idle (or starting). Since E14 S2b it also decides what is BUSY — A19's
        /// busy evidence — so a user layer may only raise it when root reads it. Default 2.</summary>
        public static readonly ConfigKey.IntKey IdleCpuPercent = new("mcpServers.idleCpuPercent", 0, 100) { Trust = KeyTrust.Higher };

        /// <summary>An instance younger than this is starting, not idle. Default 10.</summary>
        public static readonly ConfigKey.IntKey IdleMinAgeMinutes = new("mcpServers.idleMinAgeMinutes", 0, 1440) { Trust = KeyTrust.Display };

        /// <summary>A busy instance whose newest log write is older than this is busy without activity — A19's busy evidence since E14
        /// S2b, so a user layer may only lengthen it when root reads it. Default 10.</summary>
        public static readonly ConfigKey.IntKey ActivityWindowMinutes = new("mcpServers.activityWindowMinutes", 1, 1440) { Trust = KeyTrust.Higher };

        /// <summary>The starts of each server are counted over this window (at most a day: today's and yesterday's log folders cover
        /// it). Default 10.</summary>
        public static readonly ConfigKey.IntKey StartsWindowMinutes = new("mcpServers.startsWindowMinutes", 1, 1440) { Trust = KeyTrust.Display };

        /// <summary><c>mcp.instances</c> warns above this many instances. Default 12.</summary>
        public static readonly ConfigKey.IntKey WarnInstances = new("mcpServers.warnInstances", 0, 10000) { Trust = KeyTrust.Display };

        /// <summary><c>mcp.cpu</c> warns above this total, in % of one core. Default 100.</summary>
        public static readonly ConfigKey.IntKey WarnCpuPercent = new("mcpServers.warnCpuPercent", 1, 100000) { Trust = KeyTrust.Display };

        /// <summary><c>mcp.starts</c> warns when a server started more often than this within the starts window. Default 10.</summary>
        public static readonly ConfigKey.IntKey WarnStarts = new("mcpServers.warnStarts", 0, 100000) { Trust = KeyTrust.Display };

        /// <summary>The most instances whose CPU is sampled and that are listed — it bounds root's /proc reads. Default 256.</summary>
        public static readonly ConfigKey.IntKey MaxInstances = new("mcpServers.maxInstances", 1, 1024) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>The most entries one log listing sees. Default 20000.</summary>
        public static readonly ConfigKey.IntKey MaxLogEntries = new("mcpServers.maxLogEntries", 100, 100000) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>The most starts one server lists with their times (newest first); the count is never capped. Default 50.</summary>
        public static readonly ConfigKey.IntKey MaxStartsListed = new("mcpServers.maxStartsListed", 0, 1000) { Trust = KeyTrust.Display };

        /// <summary>The deadline of one server's log listing. Default 1000.</summary>
        public static readonly ConfigKey.IntKey LogListMilliseconds = new("mcpServers.logListMilliseconds", 100, 5000) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>The shortest interval an instance's CPU is measured over from the ledger (plan E14 S1): a recorded point younger
        /// than this is no baseline. Two periods of a burst near a minute (research/2026-10-07_evening_overload.md M1). Its range
        /// ends where <see cref="CpuIntervalMaxMinutes"/>' begins, so the two cannot contradict. A19's busy evidence since E14 S2b: a user layer
        /// may only lengthen it when root reads it. Default 120.</summary>
        public static readonly ConfigKey.IntKey CpuIntervalMinSeconds = new("mcpServers.cpuIntervalMinSeconds", 10, 600) { Trust = KeyTrust.Higher };

        /// <summary>The longest interval an instance's CPU is measured over from the ledger: an older point is no baseline and the
        /// window answers — an average over hours would judge the kind on the past (plan E14 S1, review finding 3). Two activity
        /// windows. A19's busy evidence since E14 S2b (a streak is a chain of readings no further apart): a user layer may only
        /// shorten it when root reads it. Default 20.</summary>
        public static readonly ConfigKey.IntKey CpuIntervalMaxMinutes = new("mcpServers.cpuIntervalMaxMinutes", 10, 1440) { Trust = KeyTrust.Lower };
    }

    public static partial class McpWatchdog
    {
        /// <summary>A19 (plan E14 S2a): an MCP server with NO CPU for at least this long, measured by identity over the timer's CPU
        /// history, is stopped — the owner's "idle > 60 min" of 2026-10-08. It decides what ends, so a user layer may only lengthen
        /// it. Default 60.</summary>
        public static readonly ConfigKey.IntKey IdleMinutes = new("mcpWatchdog.idleMinutes", 10, 10080) { Trust = KeyTrust.Higher };

        /// <summary>A19: the same for a server whose agent died (re-parented to init) — nobody can talk to it any more (the owner's
        /// Q-M3). Default 10.</summary>
        public static readonly ConfigKey.IntKey OrphanIdleMinutes = new("mcpWatchdog.orphanIdleMinutes", 1, 10080) { Trust = KeyTrust.Higher };

        /// <summary>A19's busy half (plan E14 S2b): an MCP server busy without a log write for at least this long — a chain of interval
        /// readings in root's CPU ledger — is stopped. It decides what ends, so a user layer may only lengthen it. Default 30 (an
        /// assumption, the coordinator's; the owner may change it).</summary>
        public static readonly ConfigKey.IntKey BusyMinutes = new("mcpWatchdog.busyMinutes", 10, 10080) { Trust = KeyTrust.Higher };

        /// <summary>The watch timer's period (<c>wsl-care-watch.timer</c>, plan E14 S2b): how often root samples the MCP servers into
        /// its ledger and lets A19 act. It must stay under <c>mcpServers.cpuIntervalMaxMinutes</c>, or no sample finds a baseline.
        /// Machine-only (it is a unit's value). Default 5.</summary>
        public static readonly ConfigKey.IntKey PeriodMinutes = new("mcpWatchdog.periodMinutes", 2, 15) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>The longest one watch run may take (<c>wsl-care-watch.service</c>'s <c>TimeoutStartSec</c>): a sample, the CPU
        /// window, and A19's signals with their grace. Machine-only. Default 10.</summary>
        public static readonly ConfigKey.IntKey RunLimitMinutes = new("mcpWatchdog.runLimitMinutes", 2, 60) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }

    /// <summary>The AI-session archive (plan §15r, E9.S0). Group A: the ages and the base's free space, the user's to choose;
    /// group B: what bounds a run root starts and waits on — the budget, the ceilings, the caps of what root reads back.</summary>
    public static partial class Archive
    {
        /// <summary>Phase 2 (§15r D2) removes a source only this long after its copy was indexed, once the copy is read again. Default 24.</summary>
        public static readonly ConfigKey.IntKey RemoveAfterHours = new("archive.removeAfterHours", 1, 720) { Trust = KeyTrust.Higher };

        /// <summary>Days kept between a source's removal and the agent's own deletion (§15r D10). Default 7.</summary>
        public static readonly ConfigKey.IntKey MarginDays = new("archive.marginDays", 1, 365) { Trust = KeyTrust.Higher };

        /// <summary>The shortest own-retention of an archived agent — Claude Code's cleanupPeriodDays default; raise it with that
        /// setting (§15r D10). Default 30.</summary>
        public static readonly ConfigKey.IntKey AgentRetentionDays = new("archive.agentRetentionDays", 3, 3650) { Trust = KeyTrust.Lower };

        /// <summary>Within this many days of an agent's retention the timer does not wait for an idle machine (§15r D8). Default 7.</summary>
        public static readonly ConfigKey.IntKey UrgentWithinDays = new("archive.urgentWithinDays", 0, 365) { Trust = KeyTrust.Higher };

        /// <summary>The base keeps this much free; a session that would go below it is not started (§15r D7). Default 5.</summary>
        public static readonly ConfigKey.IntKey MinFreeGb = new("archive.minFreeGb", 0, GbCeiling) { Trust = KeyTrust.Higher };

        /// <summary>The copy's read size. Default 1024.</summary>
        public static readonly ConfigKey.IntKey CopyBufferKib = new("archive.copyBufferKib", 64, 16384) { Trust = KeyTrust.Display };

        /// <summary>The most the archive child takes of one timer run — from the run limit's slack, never a term of its sum (§15r D8).
        /// Default 30.</summary>
        public static readonly ConfigKey.IntKey RunBudgetMinutes = new("archive.runBudgetMinutes", 1, 55) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>The session in flight may finish this long past the budget. Default 5.</summary>
        public static readonly ConfigKey.IntKey FinishGraceMinutes = new("archive.finishGraceMinutes", 1, 30) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>Below this much slack in a timer run A13 skips "no time left in this run". Default 2.</summary>
        public static readonly ConfigKey.IntKey MinRunMinutes = new("archive.minRunMinutes", 1, 60) { Trust = new(SafeDirection.Higher, MachineOnly: true) };

        /// <summary>The preview child's ceiling — counted in a timer run's worst case. Default 120.</summary>
        public static readonly ConfigKey.IntKey PreviewTimeoutSeconds = new("archive.previewTimeoutSeconds", 10, 600) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>The one-<c>stat</c> child that asks whether the base answers at all, before the long one (§15r D1, review M11). Default 10.</summary>
        public static readonly ConfigKey.IntKey ReachabilitySeconds = new("archive.reachabilitySeconds", 1, 60) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>The archive child prints a line at least this often, mid-file too — the progress watchdog counts it (§15r D8). Default 60.</summary>
        public static readonly ConfigKey.IntKey ProgressSilenceSeconds = new("archive.progressSilenceSeconds", 10, 600) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>A20's restore child's ceiling (the restore button; A19 is the idle MCP servers' since E14 S2a) — a button, never in a timer run; within the least maximum a request accepts
        /// (<c>commands.maxTimeoutHours</c>' minimum, 1 h) WITH the 60 s a command's ceiling keeps — so at most 59 (E9.S0 review
        /// round C5). Default 59.</summary>
        public static readonly ConfigKey.IntKey RestoreLimitMinutes = new("archive.restoreLimitMinutes", 1, 59) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>The most sessions one run takes — bounds the in-flight file and the answer root reads (the owner's backlog of
        /// ~4 500 sessions drains in five runs). Default 1000.</summary>
        public static readonly ConfigKey.IntKey MaxSessionsPerRun = new("archive.maxSessionsPerRun", 1, 100000) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>The most entries one A20 press restores (plan §15r E9.S4 plan round, finding 0): the ids reach the restore child as
        /// ONE argument, so its ceiling keeps that argument far below the kernel's per-argument limit (128 KiB; 5 000 ids of 16 hex and a
        /// comma are 85 000 bytes). Default 1000.</summary>
        public static readonly ConfigKey.IntKey MaxRestoreEntries = new("archive.maxRestoreEntries", 1, 5000) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>One month index read. Default 67108864.</summary>
        public static readonly ConfigKey.IntKey MaxIndexBytes = new("archive.maxIndexBytes", 1048576, 268435456) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>The archive's local state files (the in-flight file, the summary, the restored map) — the in-flight file holds
        /// the sessions of every run still waiting for its removal (E9.S0 review round C3); 16 MiB so the hourly timer (25 waiting
        /// runs × 1000 × 600 B) stays valid under the defaults. Default 16777216.</summary>
        public static readonly ConfigKey.IntKey MaxStateFileBytes = new("archive.maxStateFileBytes", 65536, 67108864) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>The archive child's answer root reads. Default 1048576.</summary>
        public static readonly ConfigKey.IntKey ChildOutputCapBytes = new("archive.childOutputCapBytes", 65536, 16777216) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>One progress line root reads (it carries no name). Default 256.</summary>
        public static readonly ConfigKey.IntKey ProgressLineMaxBytes = new("archive.progressLineMaxBytes", 64, 4096) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>The open-file scan (<c>/proc/*/fd</c>) or the Restart Manager query of one run. Default 20.</summary>
        public static readonly ConfigKey.IntKey InUseScanSeconds = new("archive.inUseScanSeconds", 1, 120) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>An EMPTY side lease is read again after this wait: a creator that is alive writes it right after its exclusive
        /// create, so one still empty is a run that died in between and is taken over (plan §15r D4, E9.S2b). Longer is safer.
        /// Default 2000.</summary>
        public static readonly ConfigKey.IntKey LeaseSettleMilliseconds = new("archive.leaseSettleMilliseconds", 100, 30000) { Trust = new(SafeDirection.Higher, MachineOnly: true) };

        /// <summary>An in-flight entry phase 2 keeps waiting (its index unreadable, a line unverified, its folder behind a link) this many
        /// days after it was archived is let go: dropped, its quarantined files renamed back, its source left where it is (correctness
        /// review M5). Default 14.</summary>
        public static readonly ConfigKey.IntKey KeptEntryDays = new("archive.keptEntryDays", 1, 365) { Trust = KeyTrust.Display };

        /// <summary>A restored entry leaves <c>restored.json</c> this many days after its restore when another is added (E9.S3 own review
        /// round C-6): past it the session is either archived again (it left already) or gone. Default 180.</summary>
        public static readonly ConfigKey.IntKey RestoredKeepDays = new("archive.restoredKeepDays", 1, 3650) { Trust = KeyTrust.Display };
    }

    /// <summary>Every E7.S2c number key, in the order <c>config get</c> lists them (after the older keys).</summary>
    private static IReadOnlyList<ConfigKey> NumberKeys() =>
    [
        Walk.IntervalHours,
        Walk.MaxExclusionsNamed,
        Agents.MeasureBudgetSeconds,
        Runs.StopMarkerRetentionHours,
        Preview.MaxItems,
        Clock.MinimumGapMinutes,
        Clock.DriftObservationsApartMinutes,
        Trim.PeriodDays,
        AptCache.TriggerMb,
        ToolCaches.TriggerGb,
        EditorServers.KeepNewest,
        Processes.CpuWindowSeconds,
        Processes.TermGraceSeconds,
        Runs.HistoryRetentionDays,
        Events.StartsRetentionDays,
        Events.RetryFirstSeconds,
        Events.RetryMaxSeconds,
        Events.RetryFactor,
        Events.StalenessSlackMinutes,
        Events.TopImages,
        Events.SegmentMinutes,
        Thresholds.PageCacheWarnGib,
        Thresholds.PageCacheActGib,
        Thresholds.PageCacheActAvailablePercent,
        Thresholds.InactiveAnonWarnGib,
        Thresholds.Order7WarnBlocks,
        Thresholds.MemoryPressureWarn,
        Thresholds.CpuPressureWarnPercent,
        Thresholds.IoPressureWarnPercent,
        Thresholds.SwapFreeWarnGb,
        Thresholds.CommittedWarnPercent,
        Thresholds.RootUsedWarnPercent,
        Journal.MaxGb,
        Thresholds.JournalHistoryWarnDays,
        Thresholds.ClockJumpsWarnPer4h,
        Thresholds.TimeJumpsBackWarnPer4h,
        Thresholds.CollectorFreshMinutes,
        WslConfig.RecommendedMemoryGb,
        WslConfig.RecommendedSwapGb,
        WslConfig.VmmemAdviceGb,
        Thresholds.WslMemoryCriticalPercent,
        Processes.TopCount,
        Walk.MaxEntries,
        Walk.MaxSeconds,
        Agents.WalkBudgetSeconds,
        Agents.SessionMaxEntries,
        Agents.MaxPackageJsonBytes,
        Agents.LookupCeilingSeconds,
        Requests.MaxRead,
        Requests.MaxQueued,
        Requests.MaxBytes,
        Requests.GraceSeconds,
        Requests.FutureSkewSeconds,
        Requests.LockWaitSeconds,
        Act.MaxShownNames,
        Act.MaxListBytes,
        Act.StdinTimeoutSeconds,
        Stops.MaxMarkerBytes,
        Timer.FirstDryWindowDays,
        Timer.PeriodHours,
        Timer.LateSlackMinutes,
        Timer.RandomizedDelayMinutes,
        Timer.AccuracyMinutes,
        Timer.RunLimitMinutes,
        Units.Nice,
        Units.MemoryMaxMb,
        Units.StopTimeoutSeconds,
        Units.EventsRestartSeconds,
        Docker.ProbeTimeoutSeconds,
        Docker.ListTimeoutSeconds,
        Docker.DiskUsageTimeoutSeconds,
        Docker.RemoveTimeoutSeconds,
        Docker.PruneTimeoutSeconds,
        Docker.BatchSize,
        Docker.SmallOutputCapBytes,
        Docker.LargeOutputCapBytes,
        Docker.ActionOutputCapBytes,
        Docker.MaxDaemonJsonBytes,
        Systemd.TimeoutSeconds,
        Systemd.SearchTimeoutSeconds,
        Systemd.OutputCapBytes,
        Systemd.SearchOutputCapBytes,
        Systemd.UnitOutputCapBytes,
        Systemd.UnitStartTimeoutSeconds,
        Systemd.UnitStopTimeoutSeconds,
        Journal.VacuumTimeoutSeconds,
        Memory.SyncTimeoutSeconds,
        Memory.DropCachesTimeoutSeconds,
        Memory.CompactTimeoutSeconds,
        AptCache.CleanTimeoutSeconds,
        Snap.RemoveTimeoutSeconds,
        BuildServers.ShutdownTimeoutSeconds,
        BuildServers.IdleMinutes,
        Trim.TimeoutSeconds,
        Npm.CleanTimeoutSeconds,
        Nuget.ClearTimeoutSeconds,
        ToolCaches.TrimTimeoutSeconds,
        ToolCaches.WhereTimeoutSeconds,
        ToolCaches.WhereOutputCapBytes,
        Clock.StepTimeoutSeconds,
        Clock.ReferenceTimeoutSeconds,
        Health.WindowsClockTimeoutSeconds,
        Health.SnapTimeoutSeconds,
        Health.OutputCapBytes,
        Health.KernelLinesKept,
        Health.KernelLineChars,
        Health.MaxWslConfigBytes,
        Commands.OutputCapBytes,
        Commands.SystemDriveLookupSeconds,
        Commands.MaxTimeoutHours,
        Commands.DrainGraceMilliseconds,
        UserFiles.MaxSmallFileBytes,
        UserFiles.MaxJsonBytes,
        UserFiles.MaxLinkBytes,
        Records.MaxReasonChars,
        Records.LockTimeoutSeconds,
        Records.MaxStateFileBytes,
        Records.MaxHistoryBytes,
        Logs.MaxDetailsRead,
        Logs.MaxRangeDays,
        Processes.KillWaitSeconds,
        Processes.ShownCommandChars,
        Processes.SignalSliceMilliseconds,
        Events.SegmentSlackSeconds,
        Events.EarlyEndSeconds,
        Running.HeartbeatSeconds,
        Running.WedgedAfterSeconds,
        Running.ReadRetries,
        Running.ReadRetryMilliseconds,
        Running.NoProgressMinutes,
        ConfigLayerLimits.MaxLayerBytes,
        Patterns.MatchTimeoutMilliseconds,
        FileLocks.RenameRetryMilliseconds,
        FileLocks.RenameRetrySleepMilliseconds,
        FileLocks.LockJitterMinMilliseconds,
        FileLocks.LockJitterMaxMilliseconds,
        AgentCpu.MaxEntries,
        AgentCpu.MaxBytes,
        McpServers.CpuWindowMilliseconds,
        McpServers.IdleCpuPercent,
        McpServers.IdleMinAgeMinutes,
        McpServers.ActivityWindowMinutes,
        McpServers.StartsWindowMinutes,
        McpServers.WarnInstances,
        McpServers.WarnCpuPercent,
        McpServers.WarnStarts,
        McpServers.MaxInstances,
        McpServers.MaxLogEntries,
        McpServers.LogListMilliseconds,
        McpServers.MaxStartsListed,
        McpServers.CpuIntervalMinSeconds,
        McpServers.CpuIntervalMaxMinutes,
        McpWatchdog.IdleMinutes,
        McpWatchdog.OrphanIdleMinutes,
        McpWatchdog.BusyMinutes,
        McpWatchdog.PeriodMinutes,
        McpWatchdog.RunLimitMinutes,
        Archive.RemoveAfterHours,
        Archive.MarginDays,
        Archive.AgentRetentionDays,
        Archive.UrgentWithinDays,
        Archive.MinFreeGb,
        Archive.CopyBufferKib,
        Archive.RunBudgetMinutes,
        Archive.FinishGraceMinutes,
        Archive.MinRunMinutes,
        Archive.PreviewTimeoutSeconds,
        Archive.ReachabilitySeconds,
        Archive.ProgressSilenceSeconds,
        Archive.RestoreLimitMinutes,
        Archive.MaxSessionsPerRun,
        Archive.MaxRestoreEntries,
        Archive.MaxIndexBytes,
        Archive.MaxStateFileBytes,
        Archive.ChildOutputCapBytes,
        Archive.ProgressLineMaxBytes,
        Archive.InUseScanSeconds,
        Archive.LeaseSettleMilliseconds,
        Archive.KeptEntryDays,
        Archive.RestoredKeepDays,
    ];
}
