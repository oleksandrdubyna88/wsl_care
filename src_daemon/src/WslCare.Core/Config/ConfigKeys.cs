namespace WslCare.Core.Config;

/// <summary>
/// The register of every setting (plan §5, §6, §7.5): the ONE list the loader validates against,
/// <c>config set</c> parses with, and the help derives from. The default of each key is in the
/// embedded <c>default.json</c>, and a test holds the two to the same set of names.
/// </summary>
/// <remarks>
/// <para>Ranges are generous bounds against typos, not tuning advice — a threshold's recommended
/// value carries its measurement in <c>research/</c>, not here.</para>
/// <para>Deviation from §7.5, recorded: <c>processes.killEnabled</c> is not a key. It would duplicate
/// <c>auto.A11</c> (A11 is the process-termination action), and two switches for one behaviour is
/// how one of them comes to be wrong. A5 and A6 each have two switches because plan §5 gives them two
/// different defaults: Testcontainers vs other containers, dangling vs unused images.</para>
/// </remarks>
public static partial class ConfigKeys
{
    private const int CountCeiling = 1_000_000;
    private const int GbCeiling = 100_000;
    private const int DaysCeiling = 3650;

    /// <summary>The longest <c>archive.windowsIdleDays</c>: a year (see the key).</summary>
    private const int WindowsIdleCeilingDays = 365;

    /// <summary>A day in minutes — the widest clock skew <c>archive.clockSkewMinutes</c> tolerates.</summary>
    private const int MinutesPerDay = 1440;
    private const int HoursCeiling = 8760;

    /// <summary>The distribution-name shape the extension's own setting uses (letters, digits, <c>.</c>, <c>_</c>, <c>-</c>).</summary>
    public const string DistroPattern = "^$|^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$";

    public static readonly ConfigKey.BoolKey DryRun = new("dryRun") { Trust = new(SafeDirection.On) };

    /// <summary>Read by no daemon code (§15q Q6): the extension's own setting, kept so a layer holding it stays valid.</summary>
    public static readonly ConfigKey.TextKey Distro = new("distro", new TextRule.Matching(DistroPattern, "a WSL distribution name (letters, digits, '.', '_', '-'; not starting with '-') or empty")) { Trust = KeyTrust.Unused };

    /// <summary>Read by no daemon code (§15q Q6).</summary>
    public static readonly ConfigKey.IntKey RefreshSeconds = new("refreshSeconds", 5, 3600) { Trust = KeyTrust.Unused };

    public static class Auto
    {
        public static readonly ConfigKey.BoolKey A1 = new("auto.A1") { Trust = KeyTrust.Off };
        public static readonly ConfigKey.BoolKey A2 = new("auto.A2") { Trust = KeyTrust.Off };
        public static readonly ConfigKey.BoolKey A3 = new("auto.A3") { Trust = KeyTrust.Off };
        public static readonly ConfigKey.BoolKey A4 = new("auto.A4") { Trust = KeyTrust.Off };
        public static readonly ConfigKey.BoolKey A5 = new("auto.A5") { Trust = KeyTrust.Off };
        public static readonly ConfigKey.BoolKey A5Testcontainers = new("auto.A5Testcontainers") { Trust = KeyTrust.Off };
        public static readonly ConfigKey.BoolKey A6 = new("auto.A6") { Trust = KeyTrust.Off };
        public static readonly ConfigKey.BoolKey A6Unused = new("auto.A6Unused") { Trust = KeyTrust.Off };
        public static readonly ConfigKey.BoolKey A7 = new("auto.A7") { Trust = KeyTrust.Off };
        public static readonly ConfigKey.BoolKey A8 = new("auto.A8") { Trust = KeyTrust.Off };
        public static readonly ConfigKey.BoolKey A9 = new("auto.A9") { Trust = KeyTrust.Off };
        public static readonly ConfigKey.BoolKey A10 = new("auto.A10") { Trust = KeyTrust.Off };
        /// <summary>A11, the suspects (plan §5): ON by default since E14 S3b — the owner's answer to Q15 of 2026-10-09; the
        /// daemon's dry-run rules (<c>dryRun</c>, the first week) still govern what the timer actually does.</summary>
        public static readonly ConfigKey.BoolKey A11 = new("auto.A11") { Trust = KeyTrust.Off };
        public static readonly ConfigKey.BoolKey A12 = new("auto.A12") { Trust = KeyTrust.Off };
        public static readonly ConfigKey.BoolKey A13 = new("auto.A13") { Trust = KeyTrust.Off };
        public static readonly ConfigKey.BoolKey A14 = new("auto.A14") { Trust = KeyTrust.Off };
        public static readonly ConfigKey.BoolKey A15 = new("auto.A15") { Trust = KeyTrust.Off };
        public static readonly ConfigKey.BoolKey A16 = new("auto.A16") { Trust = KeyTrust.Off };
        public static readonly ConfigKey.BoolKey A17 = new("auto.A17") { Trust = KeyTrust.Off };

        /// <summary>A19, the idle MCP servers' watchdog (plan E14 S2a): ON by default — the owner's decision of 2026-10-08; the
        /// daemon's dry-run rules (<c>dryRun</c>, the first week) still govern what the timer actually does.</summary>
        public static readonly ConfigKey.BoolKey A19 = new("auto.A19") { Trust = KeyTrust.Off };

        /// <summary>A21, the client-gone interop relays (plan E14 S7b.2): ON by default — the owner's D3 for the MCP stops; the
        /// daemon's dry-run rules (<c>dryRun</c>, the first week) still govern what the timer actually does.</summary>
        public static readonly ConfigKey.BoolKey A21 = new("auto.A21") { Trust = KeyTrust.Off };

        /// <summary>A23, the Windows <c>%TEMP%</c> cleanup (PLAN_windows_runner.md §3.12, WR-T1): ON by default like the other timer
        /// actions — installing the cleanup task is the opt-in, and the daemon's dry-run rules (<c>dryRun</c>, the first week) still
        /// govern what the timer actually deletes.</summary>
        public static readonly ConfigKey.BoolKey A23 = new("auto.A23") { Trust = KeyTrust.Off };
    }

    public static class Volumes
    {
        public static readonly ConfigKey.IntKey AnonymousMaxCount = new("volumes.anonymousMaxCount", 0, CountCeiling) { Trust = KeyTrust.Higher };
        public static readonly ConfigKey.IntKey AnonymousMaxGb = new("volumes.anonymousMaxGb", 0, GbCeiling) { Trust = KeyTrust.Higher };
        public static readonly ConfigKey.IntKey AnonymousOlderThanDays = new("volumes.anonymousOlderThanDays", 0, DaysCeiling) { Trust = KeyTrust.Higher };
    }

    public static class Containers
    {
        public static readonly ConfigKey.IntKey StoppedOlderThanDays = new("containers.stoppedOlderThanDays", 0, DaysCeiling) { Trust = KeyTrust.Higher };
        public static readonly ConfigKey.IntKey TestcontainersOlderThanHours = new("containers.testcontainersOlderThanHours", 0, HoursCeiling) { Trust = KeyTrust.Higher };
    }

    public static class Images
    {
        public static readonly ConfigKey.IntKey UnusedOlderThanDays = new("images.unusedOlderThanDays", 0, DaysCeiling) { Trust = KeyTrust.Higher };
        public static readonly ConfigKey.IntKey UnusedMaxGb = new("images.unusedMaxGb", 0, GbCeiling) { Trust = KeyTrust.Higher };
    }

    public static class BuildCache
    {
        public static readonly ConfigKey.IntKey MaxGb = new("buildCache.maxGb", 0, GbCeiling) { Trust = KeyTrust.Higher };
        public static readonly ConfigKey.IntKey OlderThanDays = new("buildCache.olderThanDays", 0, DaysCeiling) { Trust = KeyTrust.Higher };
    }

    public static class AiAgents
    {
        public static readonly ConfigKey.IntKey WarnGb = new("aiAgents.warnGb", 0, GbCeiling);
        public static readonly ConfigKey.IntKey SessionWarnMb = new("aiAgents.sessionWarnMb", 0, CountCeiling);

        /// <summary>The manual AI agents (plan §15q D4, R2). Root takes it as DATA — re-judged against the disk at every root read
        /// — and it can only ADD protection (a failing entry leaves the walk, never the protected roots, review B2), so it has no
        /// loosening direction; <c>config set</c> reads it from stdin only.</summary>
        public static readonly ConfigKey.AgentListKey Extra = new("aiAgents.extra");
    }

    public static class Idle
    {
        public static readonly ConfigKey.IntKey CpuPercent = new("idle.cpuPercent", 0, 100) { Trust = KeyTrust.Lower };
        public static readonly ConfigKey.IntKey Minutes = new("idle.minutes", 0, 1440) { Trust = KeyTrust.Higher };
    }

    public static class Logging
    {
        /// <summary>Serilog's level names; levels come from configuration, never from call sites.</summary>
        /// <summary>Root's own audit log is not the user's to steer (§15q R1.6): a root run takes a user value only when it is no
        /// higher than the layers below.</summary>
        public static readonly ConfigKey.TextKey MinimumLevel = new("logging.minimumLevel", new TextRule.OneOf(["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"]))
        {
            Trust = new(SafeDirection.Lower, TightenOnlyForRoot: true),
        };

        /// <summary>Day folders older than this are pruned at startup; 0 disables the sweep.</summary>
        public static readonly ConfigKey.IntKey RetentionDays = new("logging.retentionDays", 0, DaysCeiling) { Trust = new(SafeDirection.Higher, TightenOnlyForRoot: true, ZeroIsUnbounded: true) };
    }

    /// <summary>Every key, in the order <c>config get</c> lists them.</summary>
    public static readonly IReadOnlyList<ConfigKey> All =
    [
        DryRun, Distro, RefreshSeconds,
        Auto.A1, Auto.A2, Auto.A3, Auto.A4, Auto.A5, Auto.A5Testcontainers, Auto.A6, Auto.A6Unused, Auto.A7, Auto.A8,
        Auto.A9, Auto.A10, Auto.A11, Auto.A12, Auto.A13, Auto.A14, Auto.A15, Auto.A16, Auto.A17, Auto.A19, Auto.A21, Auto.A23,
        Volumes.AnonymousMaxCount, Volumes.AnonymousMaxGb, Volumes.AnonymousOlderThanDays,
        Containers.StoppedOlderThanDays, Containers.TestcontainersOlderThanHours,
        Images.UnusedOlderThanDays, Images.UnusedMaxGb,
        BuildCache.MaxGb, BuildCache.OlderThanDays,
        Npm.MaxCacheGb,
        Journal.KeepDays,
        BuildServers.IdleHours,
        Processes.IdleOlderThanHours, Processes.Families, Processes.AiAgentsIdleHours,
        Thresholds.MemAvailableWarnPercent, Thresholds.MemAvailableActPercent, Thresholds.SwapWarnGb,
        AiAgents.WarnGb, AiAgents.SessionWarnMb, AiAgents.Extra,
        McpServers.Watched, McpServers.Programs,
        Temp.AllowGlobs,
        Systemd.IgnoredFailedUnits,
        Archive.OlderThanDays, Archive.WindowsIdleDays, Archive.ClockSkewMinutes, Archive.BaseFolder, Archive.Agents,
        Idle.CpuPercent, Idle.Minutes,
        Clock.ReferenceUrl, Clock.ReferenceToleranceSeconds, Clock.ManualStartWarns,
        Clock.MaxDriftSeconds,
        Logging.MinimumLevel, Logging.RetentionDays,
        .. NumberKeys(),
    ];

    private static readonly Dictionary<string, ConfigKey> ByName = All.ToDictionary(k => k.Name, StringComparer.Ordinal);

    /// <summary>The key of that name, or <c>null</c> — a name is user input and an unknown one is an expected answer.</summary>
    public static ConfigKey? Find(string name) => ByName.GetValueOrDefault(name);
}
