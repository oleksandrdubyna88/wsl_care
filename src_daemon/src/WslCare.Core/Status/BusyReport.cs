using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Thresholds;

namespace WslCare.Core.Status;

// `wsl-care busy --json` (E14 S6): the "machine busy" signal agents poll before a heavy step. Nullable members only here, at the
// JSON edge: an unread figure is `available: false` with its reason, never 0.

/// <summary>The answer: <c>calm</c>, <c>busy</c> or <c>unknown</c>; every crossing; the three PSI files; the load (shown, never
/// judged); the pressures that could not be read; when.</summary>
public sealed record BusyReport(
    string State,
    IReadOnlyList<BusyReasonReport> Reasons,
    IReadOnlyList<string> Unread,
    PressureReport Pressure,
    LoadReport Load,
    DateTimeOffset EvaluatedAt)
{
    public static BusyReport From(BusyJudgement judgement, PressureSet pressure, Reading<LoadAverages> load, DateTimeOffset at) => new(
        StateName(judgement.State),
        [.. judgement.Reasons.Select(r => new BusyReasonReport(r.Resource, r.Window, r.Value, r.Limit, r.Key))],
        judgement.Unread,
        StatusReports.Pressure(pressure),
        LoadReport.From(load),
        at);

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
