using WslCare.Core.Collectors;
using WslCare.Core.Health;
using WslCare.Core.Status;
using WslCare.Core.Thresholds;

namespace WslCare.Core.Collect;

// The wire shape of E15.S10's Windows Defender CPU figures (PLAN_disk_health.md): in the run detail (`defender`) and, with its basis,
// in `status --json` (`defender`). As everywhere at the JSON edge, a figure is "available": true with its value or "available": false
// with a reason and no value that could be read as 0. Its own records, so a change to the domain cannot change a saved detail.

/// <summary>The Defender section of a run detail and of <c>status --json</c>: how many cores the engine averaged since the previous
/// full run, and the week it joins.</summary>
/// <param name="Available">The full run's script READ MsMpEng's counters. False — the script did not start, ran past its ceiling,
/// found no MsMpEng, or the Windows binary, which never runs it — carries only the reason.</param>
/// <param name="SampledAt">When the script read the counters (the daemon's clock).</param>
/// <param name="ElapsedSeconds">How long the script ran — present only for a read that answered.</param>
/// <param name="CoresAverage">The engine's CPU summed over all cores per second of wall time, between this read and the previous full
/// run's — or why there is none (the first read, a restart, a counter that went down).</param>
/// <param name="CoveredSeconds">The wall time <paramref name="CoresAverage"/> covers.</param>
/// <param name="CoreHoursPerWeek">The last 7 days' spans summed, per 168 hours; extrapolated when they cover fewer
/// (<paramref name="Extrapolated"/>).</param>
/// <param name="CoveredHours">How many hours of those 7 days the spans cover; a restart's gap adds none.</param>
/// <param name="Extrapolated">The week is extrapolated from fewer than 168 covered hours.</param>
/// <param name="NotRecorded">Why this read was not written to the ledger (<c>{state}/defender-cpu.jsonl</c>); absent when it was.</param>
public sealed record DefenderReport(
    bool Available,
    string? Reason,
    DateTimeOffset? SampledAt,
    NumberFigure? ElapsedSeconds,
    NumberFigure? CoresAverage,
    NumberFigure? CoveredSeconds,
    NumberFigure? CoreHoursPerWeek,
    NumberFigure? CoveredHours,
    bool? Extrapolated,
    string? NotRecorded)
{
    /// <summary>In <c>status --json</c>: the full run the section was carried from, when it ended and how old it is; absent in the run
    /// detail, whose run IS the basis.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public VerdictBasis? Basis { get; init; }
}

/// <summary>The domain facts turned into their wire shape — the one place they become available + value or reason.</summary>
public static class DefenderReports
{
    public static DefenderReport From(DefenderSample sample, DefenderOutcome outcome) => Unavailable("stub");

    public static DefenderReport Unavailable(string reason) => new(false, reason, null, null, null, null, null, null, null, null);
}
