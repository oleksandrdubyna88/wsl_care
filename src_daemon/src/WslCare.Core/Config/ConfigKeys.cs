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
public static class ConfigKeys
{
    private const int CountCeiling = 1_000_000;
    private const int GbCeiling = 100_000;
    private const int DaysCeiling = 3650;
    private const int HoursCeiling = 8760;

    public static readonly ConfigKey.BoolKey DryRun = new("dryRun");
    public static readonly ConfigKey.TextKey Distro = new("distro", []);
    public static readonly ConfigKey.IntKey RefreshSeconds = new("refreshSeconds", 5, 3600);

    public static class Auto
    {
        public static readonly ConfigKey.BoolKey A1 = new("auto.A1");
        public static readonly ConfigKey.BoolKey A2 = new("auto.A2");
        public static readonly ConfigKey.BoolKey A3 = new("auto.A3");
        public static readonly ConfigKey.BoolKey A4 = new("auto.A4");
        public static readonly ConfigKey.BoolKey A5 = new("auto.A5");
        public static readonly ConfigKey.BoolKey A5Testcontainers = new("auto.A5Testcontainers");
        public static readonly ConfigKey.BoolKey A6 = new("auto.A6");
        public static readonly ConfigKey.BoolKey A6Unused = new("auto.A6Unused");
        public static readonly ConfigKey.BoolKey A7 = new("auto.A7");
        public static readonly ConfigKey.BoolKey A8 = new("auto.A8");
        public static readonly ConfigKey.BoolKey A9 = new("auto.A9");
        public static readonly ConfigKey.BoolKey A10 = new("auto.A10");
        public static readonly ConfigKey.BoolKey A11 = new("auto.A11");
        public static readonly ConfigKey.BoolKey A12 = new("auto.A12");
        public static readonly ConfigKey.BoolKey A13 = new("auto.A13");
        public static readonly ConfigKey.BoolKey A14 = new("auto.A14");
        public static readonly ConfigKey.BoolKey A15 = new("auto.A15");
        public static readonly ConfigKey.BoolKey A16 = new("auto.A16");
        public static readonly ConfigKey.BoolKey A17 = new("auto.A17");
    }

    public static class Volumes
    {
        public static readonly ConfigKey.IntKey AnonymousMaxCount = new("volumes.anonymousMaxCount", 0, CountCeiling);
        public static readonly ConfigKey.IntKey AnonymousMaxGb = new("volumes.anonymousMaxGb", 0, GbCeiling);
        public static readonly ConfigKey.IntKey AnonymousOlderThanDays = new("volumes.anonymousOlderThanDays", 0, DaysCeiling);
    }

    public static class Containers
    {
        public static readonly ConfigKey.IntKey StoppedOlderThanDays = new("containers.stoppedOlderThanDays", 0, DaysCeiling);
        public static readonly ConfigKey.IntKey TestcontainersOlderThanHours = new("containers.testcontainersOlderThanHours", 0, HoursCeiling);
    }

    public static class Images
    {
        public static readonly ConfigKey.IntKey UnusedOlderThanDays = new("images.unusedOlderThanDays", 0, DaysCeiling);
        public static readonly ConfigKey.IntKey UnusedMaxGb = new("images.unusedMaxGb", 0, GbCeiling);
    }

    public static class BuildCache
    {
        public static readonly ConfigKey.IntKey MaxGb = new("buildCache.maxGb", 0, GbCeiling);
        public static readonly ConfigKey.IntKey OlderThanDays = new("buildCache.olderThanDays", 0, DaysCeiling);
    }

    public static class Npm
    {
        public static readonly ConfigKey.IntKey MaxCacheGb = new("npm.maxCacheGb", 0, GbCeiling);
    }

    public static class Journal
    {
        public static readonly ConfigKey.IntKey KeepDays = new("journal.keepDays", 1, DaysCeiling);
    }

    public static class BuildServers
    {
        public static readonly ConfigKey.IntKey IdleHours = new("buildServers.idleHours", 0, HoursCeiling);
    }

    public static class Processes
    {
        public static readonly ConfigKey.IntKey IdleOlderThanHours = new("processes.idleOlderThanHours", 0, HoursCeiling);
        public static readonly ConfigKey.TextListKey Families = new("processes.families");
    }

    public static class Thresholds
    {
        public static readonly ConfigKey.IntKey MemAvailableWarnPercent = new("thresholds.memAvailableWarnPercent", 0, 100);
        public static readonly ConfigKey.IntKey MemAvailableActPercent = new("thresholds.memAvailableActPercent", 0, 100);
        public static readonly ConfigKey.IntKey SwapWarnGb = new("thresholds.swapWarnGb", 0, GbCeiling);
    }

    public static class AiAgents
    {
        public static readonly ConfigKey.IntKey WarnGb = new("aiAgents.warnGb", 0, GbCeiling);
        public static readonly ConfigKey.IntKey SessionWarnMb = new("aiAgents.sessionWarnMb", 0, CountCeiling);
    }

    public static class Archive
    {
        public static readonly ConfigKey.IntKey OlderThanDays = new("archive.olderThanDays", 1, DaysCeiling);

        /// <summary>Empty means no archive is configured and A13 does not run.</summary>
        public static readonly ConfigKey.TextKey BaseFolder = new("archive.baseFolder", []);
    }

    public static class Idle
    {
        public static readonly ConfigKey.IntKey CpuPercent = new("idle.cpuPercent", 0, 100);
        public static readonly ConfigKey.IntKey Minutes = new("idle.minutes", 0, 1440);
    }

    public static class Clock
    {
        public static readonly ConfigKey.IntKey MaxDriftSeconds = new("clock.maxDriftSeconds", 1, 3600);
    }

    public static class Logging
    {
        /// <summary>Serilog's level names; levels come from configuration, never from call sites.</summary>
        public static readonly ConfigKey.TextKey MinimumLevel = new("logging.minimumLevel", ["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"]);

        /// <summary>Day folders older than this are pruned at startup; 0 disables the sweep.</summary>
        public static readonly ConfigKey.IntKey RetentionDays = new("logging.retentionDays", 0, DaysCeiling);
    }

    /// <summary>Every key, in the order <c>config get</c> lists them.</summary>
    public static readonly IReadOnlyList<ConfigKey> All =
    [
        DryRun, Distro, RefreshSeconds,
        Auto.A1, Auto.A2, Auto.A3, Auto.A4, Auto.A5, Auto.A5Testcontainers, Auto.A6, Auto.A6Unused, Auto.A7, Auto.A8,
        Auto.A9, Auto.A10, Auto.A11, Auto.A12, Auto.A13, Auto.A14, Auto.A15, Auto.A16, Auto.A17,
        Volumes.AnonymousMaxCount, Volumes.AnonymousMaxGb, Volumes.AnonymousOlderThanDays,
        Containers.StoppedOlderThanDays, Containers.TestcontainersOlderThanHours,
        Images.UnusedOlderThanDays, Images.UnusedMaxGb,
        BuildCache.MaxGb, BuildCache.OlderThanDays,
        Npm.MaxCacheGb,
        Journal.KeepDays,
        BuildServers.IdleHours,
        Processes.IdleOlderThanHours, Processes.Families,
        Thresholds.MemAvailableWarnPercent, Thresholds.MemAvailableActPercent, Thresholds.SwapWarnGb,
        AiAgents.WarnGb, AiAgents.SessionWarnMb,
        Archive.OlderThanDays, Archive.BaseFolder,
        Idle.CpuPercent, Idle.Minutes,
        Clock.MaxDriftSeconds,
        Logging.MinimumLevel, Logging.RetentionDays,
    ];

    private static readonly Dictionary<string, ConfigKey> ByName = All.ToDictionary(k => k.Name, StringComparer.Ordinal);

    /// <summary>The key of that name, or <c>null</c> — a name is user input and an unknown one is an expected answer.</summary>
    public static ConfigKey? Find(string name) => ByName.GetValueOrDefault(name);
}
