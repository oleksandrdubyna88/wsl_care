using WslCare.Core.Collectors;
using WslCare.Core.Health;
using WslCare.Core.Records;
using WslCare.Core.Status;
using WslCare.Core.Systemd;

namespace WslCare.Core.Collect;

// The wire shape of the health part of a run detail (and of `collect --json`). As in `status`, a figure is
// `"available": true` with its value, or `"available": false` with a `reason` and NO value key (plan §15b #7);
// the nullable members exist only here, at the JSON edge.

/// <summary>An instant, or why it is unknown.</summary>
public sealed record InstantFigure(bool Available, DateTimeOffset? Value, string? Reason);

/// <summary>A count, or why it is unknown.</summary>
public sealed record CountFigure(bool Available, int? Value, string? Reason);

/// <summary>A yes/no, or why it is unknown.</summary>
public sealed record FlagFigure(bool Available, bool? Value, string? Reason);

public sealed record FailedUnitsReport(bool Available, string? Reason, int? Count, IReadOnlyList<FailedUnit>? Units);

public sealed record KernelReport(bool Available, string? Reason, int? AllocationFailures, int? OomKills, IReadOnlyList<string>? Lines);

public sealed record TimeSyncReport(bool Available, string? Reason, bool? Ntp, bool? Synchronized);

public sealed record UnitReport(bool Available, string? Reason, string? Unit, string? LoadState, string? ActiveState, string? UnitFileState);

public sealed record FreshnessReport(bool Available, string? Reason, string? File, DateTimeOffset? LastWrite);

/// <param name="OffsetSeconds">Windows' clock minus the distro's, the probe's launch latency subtracted.</param>
public sealed record ClockReport(bool Available, string? Reason, DateTimeOffset? SampledAt, double? OffsetSeconds, double? LaunchLatencySeconds);

/// <param name="RecommendedMemory">The owner's recommendation, SHOWN and never written: <c>memory=36GB</c>.</param>
public sealed record WslConfigReport(bool Available, string? Reason, string? File, bool? Present, string? Memory, string? Swap, string? AutoMemoryReclaim, string? SparseVhd, IReadOnlyList<string>? Warnings, string RecommendedMemory);

/// <summary>The health of plan §4.5 as one full run read it.</summary>
public sealed record HealthReport(
    DateTimeOffset Since,
    FailedUnitsReport FailedUnits,
    ByteFigure JournalBytes,
    InstantFigure JournalOldestEntry,
    CountFigure ClockJumps,
    KernelReport Kernel,
    TimeSyncReport TimeSync,
    UnitReport WslPro,
    UnitReport FstrimTimer,
    FlagFigure RootDiscard,
    NumberFigure UptimeSeconds,
    FreshnessReport Sysstat,
    FreshnessReport Atop,
    FlagFigure OomDaemon,
    ClockReport WindowsClock,
    TextFigure WindowsProfile,
    WslConfigReport WslConfig);

/// <summary>The domain health sample turned into its wire shape — the one place it becomes available + value or reason.</summary>
public static class HealthReports
{
    public static HealthReport From(HealthSample h) =>
        new(
            h.Since,
            h.FailedUnits is Reading<IReadOnlyList<FailedUnit>>.Available { Value: var units } ? new(true, null, units.Count, units) : new(false, h.FailedUnits.ReasonOrEmpty, null, null),
            StatusReports.Bytes(h.JournalBytes),
            h.JournalOldestEntry is Reading<DateTimeOffset>.Available { Value: var oldest } ? new(true, oldest, null) : new(false, null, h.JournalOldestEntry.ReasonOrEmpty),
            h.ClockJumps is Reading<int>.Available { Value: var jumps } ? new(true, jumps, null) : new(false, null, h.ClockJumps.ReasonOrEmpty),
            h.Kernel is Reading<KernelSignals>.Available { Value: var k } ? new(true, null, k.AllocationFailures, k.OomKills, k.Lines) : new(false, h.Kernel.ReasonOrEmpty, null, null, null),
            h.TimeSync is Reading<TimeSync>.Available { Value: var t } ? new(true, null, t.Ntp, t.Synchronized) : new(false, h.TimeSync.ReasonOrEmpty, null, null),
            Unit(h.WslPro),
            Unit(h.FstrimTimer),
            Flag(h.RootDiscard),
            StatusReports.Number(h.Uptime.Map(u => Math.Round(u.TotalSeconds))),
            Freshness(h.Sysstat),
            Freshness(h.Atop),
            Flag(h.OomDaemon),
            Clock(h.WindowsClock),
            h.WindowsProfile is Reading<string>.Available { Value: var p } ? new(true, p, null) : new(false, null, h.WindowsProfile.ReasonOrEmpty),
            WslConfig(h.WslConfig));

    public static ClockReport Clock(WindowsClockSample c) =>
        c.Measured ? new(true, null, c.SampledAt, Math.Round(c.OffsetSeconds, 3), Math.Round(c.LaunchLatencySeconds, 3)) : new(false, c.Unavailable, c.SampledAt, null, null);

    private static UnitReport Unit(Reading<SystemdUnit> unit) => unit switch
    {
        Reading<SystemdUnit>.Available { Value: var u } => new(true, null, u.Id, u.LoadState, u.ActiveState, u.UnitFileState.Length == 0 ? null : u.UnitFileState),
        _ => new(false, unit.ReasonOrEmpty, null, null, null, null),
    };

    private static FlagFigure Flag(Reading<bool> flag) =>
        flag is Reading<bool>.Available { Value: var v } ? new(true, v, null) : new(false, null, flag.ReasonOrEmpty);

    private static FreshnessReport Freshness(Reading<CollectorFreshness> f) =>
        f is Reading<CollectorFreshness>.Available { Value: var v } ? new(true, null, v.File, v.LastWrite) : new(false, f.ReasonOrEmpty, null, null);

    private static WslConfigReport WslConfig(Reading<WslConfigAudit> audit)
    {
        var recommended = $"memory={Thresholds.ThresholdRules.RecommendedMemoryGb}GB";
        return audit is Reading<WslConfigAudit>.Available { Value: var a }
            ? new(true, null, a.File, a.Present, Text(a.Settings.Memory), Text(a.Settings.Swap), Text(a.Settings.AutoMemoryReclaim), Text(a.Settings.SparseVhd), a.Warnings, recommended)
            : new(false, audit.ReasonOrEmpty, null, null, null, null, null, null, null, recommended);
    }

    private static string? Text(string value) => value.Length == 0 ? null : value;
}
