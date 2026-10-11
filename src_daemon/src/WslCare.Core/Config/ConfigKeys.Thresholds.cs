namespace WslCare.Core.Config;

/// <summary>The <c>thresholds.*</c> number keys (E7.S2c), in their own file: one file per key group, split from
/// <c>ConfigKeys.Numbers.cs</c> (the doctrine caps a file at 800 lines). They join <c>NumberKeys()</c> like every other
/// number key. Its older keys stay in <c>ConfigKeys.cs</c>.</summary>
public static partial class ConfigKeys
{
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
}
