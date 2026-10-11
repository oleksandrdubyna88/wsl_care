namespace WslCare.Core.Config;

/// <summary>The <c>mcpServers.*</c> number keys (E7.S2c), in their own file: one file per key group, split from
/// <c>ConfigKeys.Numbers.cs</c> (the doctrine caps a file at 800 lines). They join <c>NumberKeys()</c> like every other
/// number key. Its older keys stay in <c>ConfigKeys.cs</c>.</summary>
public static partial class ConfigKeys
{
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
}
