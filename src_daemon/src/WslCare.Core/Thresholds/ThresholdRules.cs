using System.Globalization;

using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Health;
using WslCare.Core.Preview;
using WslCare.Core.Records;

namespace WslCare.Core.Thresholds;

/// <summary>Everything one full run's thresholds are evaluated over.</summary>
/// <param name="SinceLastRun">How long the "since the last run" counts cover — the clock-jump count is judged per 4 h.</param>
/// <param name="PreviousClock">The previous full run's measured Windows clock, for the second observation of
/// plan §15 #10; unavailable when there is none.</param>
/// <param name="BuildCacheBytes">The whole build cache (A7's trigger is its size, plan §5).</param>
/// <param name="NpmCacheBytes">The size of <c>~/.npm</c> the newest folder sample measured (A8's trigger).</param>
public sealed record ThresholdInputs(
    Reading<MemorySnapshot> Memory,
    Reading<VolumeUsage> Root,
    HealthSample Health,
    TimeSpan SinceLastRun,
    Reading<WindowsClockSample> PreviousClock,
    IReadOnlyList<CleanupRow> Rows,
    Reading<long> BuildCacheBytes,
    Reading<long> NpmCacheBytes);

/// <summary>
/// The thresholds of plan §4 evaluated over one full run — PURE, so every threshold is a unit test at its edge.
/// Where a threshold is a setting (<c>thresholds.*</c>, <c>volumes.*</c>, <c>images.unusedMaxGb</c>,
/// <c>buildCache.maxGb</c>, <c>npm.maxCacheGb</c>, <c>clock.maxDriftSeconds</c>) the setting is read; the other
/// numbers are plan §4's starting points, each one named constant below.
/// </summary>
/// <remarks>Units: memory in GiB (the kernel reports KiB), Docker and npm in decimal GB (Docker prints decimal GB, and
/// so did the 2026-10-02 record the defaults came from).</remarks>
public static class ThresholdRules
{
    /// <summary>Plan §4.1: page cache &gt; 15 GB warns.</summary>
    public static double PageCacheWarnGib => Tuning.Current.Int(ConfigKeys.Thresholds.PageCacheWarnGib);

    /// <summary>Plan §4.1, A1's second trigger: page cache &gt; 12 GB AND available &lt; 30 %.</summary>
    public static double PageCacheActGib => Tuning.Current.Int(ConfigKeys.Thresholds.PageCacheActGib);

    /// <summary>Plan §4.1, A1's second trigger: the available share it is paired with.</summary>
    public static double PageCacheActAvailablePercent => Tuning.Current.Int(ConfigKeys.Thresholds.PageCacheActAvailablePercent);

    /// <summary>Plan §4.1: inactive anonymous memory &gt; 15 GB warns.</summary>
    public static double InactiveAnonWarnGib => Tuning.Current.Int(ConfigKeys.Thresholds.InactiveAnonWarnGib);

    /// <summary>Plan §4.1: fewer than 32 free order-7 blocks in zone Normal warns; none is critical (A2 at once).</summary>
    public static long Order7WarnBlocks => Tuning.Current.Int(ConfigKeys.Thresholds.Order7WarnBlocks);

    /// <summary>Plan §4.1: PSI memory <c>some</c> avg60 (and avg300) above 10 warns.</summary>
    public static double PressureWarn => Tuning.Current.Int(ConfigKeys.Thresholds.MemoryPressureWarn);

    /// <summary>Plan §4.4: <c>/</c> above 80 % warns.</summary>
    public static double RootUsedWarnPercent => Tuning.Current.Int(ConfigKeys.Thresholds.RootUsedWarnPercent);

    /// <summary>Plan §5 A10: a journal above 1 GB is vacuumed.</summary>
    public static double JournalWarnGib => Tuning.Current.Int(ConfigKeys.Journal.MaxGb);

    /// <summary>Plan §4.5: a journal history shorter than 7 days warns.</summary>
    public static int JournalHistoryWarnDays => Tuning.Current.Int(ConfigKeys.Thresholds.JournalHistoryWarnDays);

    /// <summary>Plan §4.5: more than 100 clock jumps per 4 h warns.</summary>
    public static double ClockJumpsWarnPer4h => Tuning.Current.Int(ConfigKeys.Thresholds.ClockJumpsWarnPer4h);

    /// <summary>Plan §4.5: sysstat and atop are collecting when their last sample is younger than 30 minutes.</summary>
    public static TimeSpan CollectorFreshFor => Tuning.Current.Minutes(ConfigKeys.Thresholds.CollectorFreshMinutes);

    /// <summary>Plan §15 #10: the two observations of a drift are at least 5 minutes apart.</summary>
    public static TimeSpan DriftObservationsApart => Tuning.Current.Minutes(ConfigKeys.Clock.DriftObservationsApartMinutes);

    /// <summary>The owner's decision (2026-10-02): <c>.wslconfig</c> <c>memory=36GB</c> is RECOMMENDED — shown,
    /// never written — and the row is red when the VM uses more than 90 % of its ceiling.</summary>
    public static int RecommendedMemoryGb => Tuning.Current.Int(ConfigKeys.WslConfig.RecommendedMemoryGb);

    public static double WslMemoryCriticalPercent => Tuning.Current.Int(ConfigKeys.Thresholds.WslMemoryCriticalPercent);

    private const double Gib = 1024d * 1024 * 1024;
    private const double Gb = 1e9;

    /// <summary>Every threshold of one full run, in the order its detail records them: the ones a fast sample decides
    /// (<see cref="FromSample"/>), then the ones only a full run can (<see cref="UnreadFullRun"/> names them).</summary>
    public static IReadOnlyList<Verdict> Evaluate(ThresholdInputs inputs, EffectiveConfig config) =>
        [.. FromSample(inputs.Memory, inputs.Root, inputs.Health.WslConfig, config), .. FromFullRun(inputs, config)];

    /// <summary>
    /// The thresholds a FAST sample decides — memory, swap, fragmentation, pressure, the VM's ceiling and <c>/</c> — with
    /// the effective configuration. <c>collect</c> evaluates them inside <see cref="Evaluate"/>; <c>status</c> evaluates
    /// them over its own sample (plan §15g B1), so both answer with the same records and ids.
    /// </summary>
    /// <param name="wslConfig">The <c>.wslconfig</c> audit — read by a full run only; <c>status</c> passes the reason it
    /// has none, and the VM-ceiling verdict then says so in its value (its level is the memory's).</param>
    public static IReadOnlyList<Verdict> FromSample(Reading<MemorySnapshot> memory, Reading<VolumeUsage> root, Reading<WslConfigAudit> wslConfig, EffectiveConfig config) =>
    [
        MemoryAvailable(memory, config),
        Above("memory.pageCache", memory.Bind(m => m.PageCache), PageCacheWarnGib * Gib, Gib, "GiB", "page cache WSL does not hand back to Windows (A1 drops it)"),
        Above("memory.inactiveAnon", memory.Bind(m => m.InactiveAnon), InactiveAnonWarnGib * Gib, Gib, "GiB", "anonymous memory nobody touched lately (the top holders name the processes)"),
        Above("memory.swap", memory.Bind(m => m.SwapUsed), config.Int(ConfigKeys.Thresholds.SwapWarnGb) * Gib, Gib, "GiB", "swap in use (thresholds.swapWarnGb)"),
        Fragmentation(memory),
        Pressure(memory),
        .. MachineBusy.Verdicts(memory, config),
        WslConfigMemory(memory, wslConfig),
        RootDisk(root),
    ];

    /// <summary>
    /// Every threshold only a full run can judge, evaluated over inputs that were NOT read — so the ids, their order and
    /// the limits in force under <paramref name="config"/> come from the rules themselves, never from a second list.
    /// The LEVELS of this list mean nothing (an unread collector is a warning in a full run): a reader takes the ids and
    /// limits from it.
    /// </summary>
    public static IReadOnlyList<Verdict> UnreadFullRun(EffectiveConfig config) => FromFullRun(UnreadInputs(), config);

    private static ThresholdInputs UnreadInputs()
    {
        const string reason = "not read";
        return new(
            Reading.Missing<MemorySnapshot>(reason),
            Reading.Missing<VolumeUsage>(reason),
            HealthSample.Unavailable(DateTimeOffset.UnixEpoch, reason, new WindowsClockSample(DateTimeOffset.UnixEpoch, 0, 0, reason), Reading.Missing<string>(reason), Reading.Missing<WslConfigAudit>(reason)),
            TimeSpan.Zero,
            Reading.Missing<WindowsClockSample>(reason),
            [],
            Reading.Missing<long>(reason),
            Reading.Missing<long>(reason));
    }

    private static IReadOnlyList<Verdict> FromFullRun(ThresholdInputs inputs, EffectiveConfig config) =>
    [
        AtLeastOne("kernel.allocationFailures", inputs.Health.Kernel.Map(k => k.AllocationFailures), Level.Critical, "page allocation failures since the last run (the VMBus order-7 signature, plan §4.1)"),
        AtLeastOne("kernel.oomKills", inputs.Health.Kernel.Map(k => k.OomKills), Level.Critical, "OOM kills since the last run"),
        Above("journal.size", inputs.Health.JournalBytes, JournalWarnGib * Gib, Gib, "GiB", "journal on disk (A10 vacuums it)"),
        JournalHistory(inputs.Health),
        ClockJumps(inputs.Health.ClockJumps, inputs.SinceLastRun),
        ClockDrift(inputs.Health.WindowsClock, inputs.PreviousClock, inputs.Health.TimeSync, config),
        .. ClockVerdicts.Evaluate(inputs.Health, config),
        AtLeastOne("systemd.failedUnits", inputs.Health.FailedUnits.Map(u => u.Count), Level.Warn, "failed units"),
        WslPro(inputs.Health.WslPro),
        Collector("collectors.sysstat", inputs.Health.Sysstat, inputs.Health.Since),
        Collector("collectors.atop", inputs.Health.Atop, inputs.Health.Since),
        Discard(inputs.Health),
        DockerVolumes(inputs.Rows, config),
        Above("docker.A6Unused", RowBytes(inputs.Rows, "A6Unused"), config.Int(ConfigKeys.Images.UnusedMaxGb) * Gb, Gb, "GB", "unused tagged images reclaimable (images.unusedMaxGb)"),
        Above("docker.buildCache", inputs.BuildCacheBytes, config.Int(ConfigKeys.BuildCache.MaxGb) * Gb, Gb, "GB", "build cache (buildCache.maxGb, A7's cap)"),
        Above("npm.cache", inputs.NpmCacheBytes, config.Int(ConfigKeys.Npm.MaxCacheGb) * Gb, Gb, "GB", "the npm cache, ~/.npm (npm.maxCacheGb, A8)"),
    ];

    private static Verdict MemoryAvailable(Reading<MemorySnapshot> memory, EffectiveConfig config)
    {
        var warn = config.Int(ConfigKeys.Thresholds.MemAvailableWarnPercent);
        var act = config.Int(ConfigKeys.Thresholds.MemAvailableActPercent);
        var limit = $"warn < {warn} %, critical < {act} %";
        return memory.Bind(m => m.AvailablePercent) switch
        {
            Reading<double>.Available { Value: var p } when p < act => new("memory.available", Level.Critical, Percent(p), limit, "MemAvailable is below the act threshold (A1 + A2)"),
            Reading<double>.Available { Value: var p } when p < warn => new("memory.available", Level.Warn, Percent(p), limit, "MemAvailable is below the warn threshold"),
            Reading<double>.Available { Value: var p } => new("memory.available", Level.Ok, Percent(p), limit, "MemAvailable is above both thresholds"),
            var unknown => Unknown("memory.available", limit, unknown.ReasonOrEmpty),
        };
    }

    private static Verdict Fragmentation(Reading<MemorySnapshot> memory)
    {
        var limit = Invariant($"warn < {Order7WarnBlocks} free order-7 blocks, critical = 0 (zone Normal)");
        return memory.Bind(m => m.Fragmentation) switch
        {
            Reading<Collectors.Procfs.Fragmentation>.Available { Value: var f } => new(
                "memory.fragmentation",
                f.BlocksOrder7Plus == 0 ? Level.Critical : f.BlocksOrder7Plus < Order7WarnBlocks ? Level.Warn : Level.Ok,
                $"{f.BlocksOrder7Plus} order-7 blocks, {f.BlocksOrder4Plus} order-4",
                limit,
                f.BlocksOrder7Plus == 0 ? "no free 512 KiB block: the next VMBus allocation fails (A2 at once)" : "free high-order blocks in zone Normal"),
            var unknown => Unknown("memory.fragmentation", limit, unknown.ReasonOrEmpty),
        };
    }

    private static Verdict Pressure(Reading<MemorySnapshot> memory)
    {
        var limit = Invariant($"warn: memory some avg60 or avg300 > {PressureWarn:0.##}");
        return memory.Bind(m => m.Pressure.Memory) switch
        {
            Reading<Collectors.Procfs.Pressure>.Available { Value.Some: var s } => new(
                "memory.pressure",
                s.Avg60 > PressureWarn || s.Avg300 > PressureWarn ? Level.Warn : Level.Ok,
                Invariant($"some avg60 {s.Avg60:0.##}, avg300 {s.Avg300:0.##}"),
                limit,
                "memory pressure (PSI), the primary \"VM struggles\" signal"),
            var unknown => Unknown("memory.pressure", limit, unknown.ReasonOrEmpty),
        };
    }

    /// <summary>The VM's use of its ceiling against the 36 GB recommendation (shown, never applied). Inside the VM the
    /// kernel's <c>MemTotal</c> IS the ceiling <c>.wslconfig</c> sets, so the share used is 100 − MemAvailable %.</summary>
    private static Verdict WslConfigMemory(Reading<MemorySnapshot> memory, Reading<WslConfigAudit> audit)
    {
        var limit = Invariant($"critical > {WslMemoryCriticalPercent:0} % of the VM's ceiling; recommended .wslconfig memory={RecommendedMemoryGb}GB (shown, never written)");
        var configured = audit switch
        {
            Reading<WslConfigAudit>.Available { Value: { Present: true, Settings.Memory.Length: > 0 } a } => $".wslconfig memory={a.Settings.Memory}",
            Reading<WslConfigAudit>.Available { Value.Present: true } => ".wslconfig sets no memory= (WSL takes half the host's RAM)",
            Reading<WslConfigAudit>.Available => "no .wslconfig (WSL takes half the host's RAM)",
            var unread => $".wslconfig not read: {unread.ReasonOrEmpty}",
        };
        return Reading.Combine(memory.Bind(m => m.Total), memory.Bind(m => m.AvailablePercent), (total, available) => (total, used: 100 - available)) switch
        {
            Reading<(long Total, double Used)>.Available { Value: var v } => new(
                "wslconfig.memory",
                v.Used > WslMemoryCriticalPercent ? Level.Critical : Level.Ok,
                Invariant($"{v.Used:0.0} % of {v.Total / Gib:0.0} GiB used; {configured}"),
                limit,
                $"the VM's ceiling against the recommendation of memory={RecommendedMemoryGb}GB"),
            var unknown => Unknown("wslconfig.memory", limit, $"{unknown.ReasonOrEmpty}; {configured}"),
        };
    }

    private static Verdict RootDisk(Reading<VolumeUsage> root) => root switch
    {
        Reading<VolumeUsage>.Available { Value: var v } => new(
            "disk.root", v.UsedPercent > RootUsedWarnPercent ? Level.Warn : Level.Ok, Percent(v.UsedPercent), RootDiskLimit, "df / of the distro"),
        var unknown => Unknown("disk.root", RootDiskLimit, unknown.ReasonOrEmpty),
    };

    private static string RootDiskLimit => Invariant($"warn > {RootUsedWarnPercent:0} %");

    private static Verdict JournalHistory(HealthSample health)
    {
        var limit = Invariant($"warn when the oldest entry is less than {JournalHistoryWarnDays} days old");
        return health.JournalOldestEntry switch
        {
            Reading<DateTimeOffset>.Available { Value: var oldest } => (health.Since - oldest) switch
            {
                var span => new(
                    "journal.history",
                    span < TimeSpan.FromDays(JournalHistoryWarnDays) ? Level.Warn : Level.Ok,
                    Invariant($"{span.TotalDays:0.0} days (oldest entry {oldest.UtcDateTime:yyyy-MM-dd HH:mm}Z)"),
                    limit,
                    "how far back the journal reaches (clock jumps erase it, plan finding 4)"),
            },
            var unknown => Unknown("journal.history", limit, unknown.ReasonOrEmpty),
        };
    }

    private static Verdict ClockJumps(Reading<int> jumps, TimeSpan since)
    {
        var limit = Invariant($"warn > {ClockJumpsWarnPer4h:0} per 4 h");
        var hours = Math.Max(since.TotalHours, 1.0 / 60);
        return jumps switch
        {
            Reading<int>.Available { Value: var n } => new(
                "clock.jumps",
                n * 4 / hours > ClockJumpsWarnPer4h ? Level.Warn : Level.Ok,
                Invariant($"{n} in {since.TotalHours:0.0} h ({n * 4 / hours:0} per 4 h)"),
                limit,
                "systemd-resolved's \"Clock change detected\" since the last run"),
            var unknown => Unknown("clock.jumps", limit, unknown.ReasonOrEmpty),
        };
    }

    /// <summary>Plan §4.5 with §15 #10: a drift is reported only on TWO observations above the threshold at least
    /// 5 minutes apart — this run's and the previous full run's — and never as more than a warning (A16 corrects it,
    /// and skips a clock that timesyncd/chrony reports synchronised).</summary>
    /// <summary>Plan §15 #10, the ONE drift rule (this report and A16 share it): both observations measured, both above
    /// <paramref name="maxSeconds"/>, at least <see cref="DriftObservationsApart"/> apart.</summary>
    public static bool IsDrift(WindowsClockSample current, Reading<WindowsClockSample> previous, int maxSeconds) =>
        IsOff(current, maxSeconds)
        && previous is Reading<WindowsClockSample>.Available { Value: var p }
        && IsOff(p, maxSeconds) && current.SampledAt - p.SampledAt >= DriftObservationsApart;

    /// <summary>One observation, measured and above the limit.</summary>
    private static bool IsOff(WindowsClockSample sample, int maxSeconds) => sample.Measured && Math.Abs(sample.OffsetSeconds) > maxSeconds;

    private static Verdict ClockDrift(WindowsClockSample current, Reading<WindowsClockSample> previous, Reading<TimeSync> sync, EffectiveConfig config)
    {
        var max = config.Int(ConfigKeys.Clock.MaxDriftSeconds);
        var limit = Invariant($"warn: |offset| > {max} s on two observations at least {ApartText} apart (clock.maxDriftSeconds)");
        if (!current.Measured)
        {
            return Unknown("clock.drift", limit, current.Unavailable);
        }

        var value = Invariant($"{current.OffsetSeconds:+0.00;-0.00} s (launch latency {current.LaunchLatencySeconds:0.00} s subtracted)");
        if (Math.Abs(current.OffsetSeconds) <= max)
        {
            return new("clock.drift", Level.Ok, value, limit, "the distro's clock agrees with Windows'");
        }

        var second = IsDrift(current, previous, max);
        var synced = sync is Reading<TimeSync>.Available { Value.Synchronized: true } ? "; timesyncd reports the clock synchronised, so A16 would not step it" : string.Empty;
        return second
            ? new("clock.drift", Level.Warn, value, limit, $"drift on two observations at least {ApartText} apart{synced}")
            : new("clock.drift", Level.Ok, value, limit, $"one observation above the threshold; a second, at least {ApartText} later, is needed before a drift is reported");
    }

    /// <summary>How far apart two drift observations must be, in words (<c>clock.driftObservationsApartMinutes</c>).</summary>
    public static string ApartText => Invariant($"{DriftObservationsApart.TotalMinutes:0} minutes");

    private static Verdict WslPro(Reading<Systemd.SystemdUnit> unit)
    {
        const string limit = "warn when wsl-pro.service is not masked and runs (plan 0.4)";
        return unit switch
        {
            Reading<Systemd.SystemdUnit>.Available { Value: var u } => new(
                "systemd.wslPro",
                u.Exists && u.UnitFileState != "masked" && u.ActiveState == "active" ? Level.Warn : Level.Ok,
                !u.Exists ? "not installed" : $"{u.UnitFileState}, {u.ActiveState}",
                limit,
                "an agent that is not installed reconnects every ~20 s (plan finding 5)"),
            var unknown => Unknown("systemd.wslPro", limit, unknown.ReasonOrEmpty),
        };
    }

    private static Verdict Collector(string id, Reading<CollectorFreshness> freshness, DateTimeOffset now) => freshness switch
    {
        Reading<CollectorFreshness>.Available { Value: var f } => new(
            id,
            now - f.LastWrite > CollectorFreshFor ? Level.Warn : Level.Ok,
            Invariant($"last sample {(now - f.LastWrite).TotalMinutes:0} min ago ({f.File})"),
            FreshLimit,
            "a history collector that stopped is a gap nobody can fill later"),
        _ => new(id, Level.Warn, string.Empty, FreshLimit, freshness.ReasonOrEmpty),
    };

    private static string FreshLimit => Invariant($"warn when the last sample is {CollectorFreshFor.TotalMinutes:0} minutes old or older");

    private static Verdict Discard(HealthSample health)
    {
        const string limit = "warn when / is mounted without discard and fstrim.timer is not enabled";
        return Reading.Combine(health.RootDiscard, health.FstrimTimer, (discard, timer) => (discard, timer)) switch
        {
            Reading<(bool Discard, Systemd.SystemdUnit Timer)>.Available { Value: var v } => new(
                "disk.discard",
                v.Discard || v.Timer.UnitFileState == "enabled" ? Level.Ok : Level.Warn,
                v.Discard ? "mounted with discard" : $"no discard; fstrim.timer {v.Timer.UnitFileState}",
                limit,
                "freed blocks reach the VHDX only through discard or fstrim, and only then can compaction shrink it"),
            var unknown => Unknown("disk.discard", limit, unknown.ReasonOrEmpty),
        };
    }

    private static Verdict DockerVolumes(IReadOnlyList<CleanupRow> rows, EffectiveConfig config)
    {
        var maxCount = config.Int(ConfigKeys.Volumes.AnonymousMaxCount);
        var maxGb = config.Int(ConfigKeys.Volumes.AnonymousMaxGb);
        var limit = $"warn > {maxCount} volumes or > {maxGb} GB (volumes.anonymousMaxCount / anonymousMaxGb, A4's trigger)";
        return Row(rows, "A4") switch
        {
            Reading<RowFigures>.Available { Value: var f } => new(
                "docker.A4",
                f.Count > maxCount || f.Bytes > maxGb * Gb ? Level.Warn : Level.Ok,
                Invariant($"{f.Count} volumes, {f.Bytes / Gb:0.00} GB"),
                limit,
                "unattached anonymous volumes old enough for A4"),
            var unknown => Unknown("docker.A4", limit, unknown.ReasonOrEmpty),
        };
    }

    private static Verdict Above(string id, Reading<long> figure, double warnAbove, double unit, string unitName, string what) =>
        figure switch
        {
            Reading<long>.Available { Value: var v } => new(id, v > warnAbove ? Level.Warn : Level.Ok, Invariant($"{v / unit:0.00} {unitName}"), Invariant($"warn > {warnAbove / unit:0.##} {unitName}"), what),
            var unknown => Unknown(id, Invariant($"warn > {warnAbove / unit:0.##} {unitName}"), unknown.ReasonOrEmpty),
        };

    private static Verdict AtLeastOne(string id, Reading<int> count, Level level, string what) => count switch
    {
        Reading<int>.Available { Value: var n } => new(id, n >= 1 ? level : Level.Ok, n.ToString(CultureInfo.InvariantCulture), $"{(level == Level.Critical ? "critical" : "warn")} at 1 or more", what),
        var unknown => Unknown(id, $"{(level == Level.Critical ? "critical" : "warn")} at 1 or more", unknown.ReasonOrEmpty),
    };

    private static Reading<RowFigures> Row(IReadOnlyList<CleanupRow> rows, string id) =>
        rows.FirstOrDefault(r => r.Id == id)?.Figures ?? Reading.Missing<RowFigures>($"no {id} row in this run");

    private static Reading<long> RowBytes(IReadOnlyList<CleanupRow> rows, string id) => Row(rows, id).Map(f => f.Bytes);

    private static Verdict Unknown(string id, string limit, string reason) => new(id, Level.Unknown, string.Empty, limit, reason);

    private static string Percent(double value) => Invariant($"{value:0.0} %");

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
