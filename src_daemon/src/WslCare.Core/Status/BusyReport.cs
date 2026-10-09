using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Thresholds;

namespace WslCare.Core.Status;

// `wsl-care busy --json` (E14 S6): the "machine busy" signal agents poll before a heavy step. Nullable members only here, at the
// JSON edge: an unread figure is `available: false` with its reason, never 0.

/// <summary>The answer: <c>calm</c>, <c>busy</c> or <c>unknown</c>; every crossing; the three PSI files; the load (shown, never
/// judged); the pressures that could not be read; when.</summary>
public sealed record BusyReport(string State, PressureReport Pressure, LoadReport Load, DateTimeOffset EvaluatedAt)
{
    /// <summary>Every pressure over its key; empty when none (coai code round 2026-10-09, finding 6: never null).</summary>
    /// <remarks>The source generator sets an init property it did not find to null, so the getter answers empty for it.</remarks>
    public IReadOnlyList<BusyReasonReport> Reasons { get => field ?? []; init; } = [];

    /// <summary>Every pressure not read, as <c>resource: why</c>; empty when all three were.</summary>
    public IReadOnlyList<string> Unread { get => field ?? []; init; } = [];

    public static BusyReport From(BusyJudgement judgement, PressureSet pressure, Reading<LoadAverages> load, DateTimeOffset at) =>
        new(StateName(judgement.State), StatusReports.Pressure(pressure), LoadReport.From(load), at)
        {
            Reasons = [.. judgement.Reasons.Select(r => new BusyReasonReport(r.Resource, r.Window, r.Value, r.Limit, r.Key))],
            Unread = judgement.Unread,
        };

    /// <summary>The wire names — the contract agents compare against.</summary>
    public static string StateName(BusyState state) => state switch
    {
        BusyState.Busy => "busy",
        BusyState.Calm => "calm",
        _ => "unknown",
    };
}

/// <summary>One pressure over its key.</summary>
public sealed record BusyReasonReport(string Resource, string Window, double Value, double Limit, string Key);

/// <summary><c>/proc/loadavg</c>'s three averages, or why they were not read.</summary>
public sealed record LoadReport(bool Available, string? Reason, double? One, double? Five, double? Fifteen)
{
    public static LoadReport From(Reading<LoadAverages> load) => load switch
    {
        Reading<LoadAverages>.Available { Value: var l } => new(true, null, l.One, l.Five, l.Fifteen),
        _ => new(false, load.ReasonOrEmpty, null, null, null),
    };
}
