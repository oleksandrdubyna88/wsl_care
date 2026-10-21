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
/// <param name="LedgerNote">Why this read was not written to the ledger (<c>{state}/defender-cpu.jsonl</c>), or what could not be done to the
/// file (its old lines dropped); absent when the reading was recorded and the file is in order.</param>
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
    string? LedgerNote)
{
    /// <summary>In <c>status --json</c>: the full run the section was carried from, when it ended and how old it is; absent in the run
    /// detail, whose run IS the basis.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public VerdictBasis? Basis { get; init; }
}

/// <summary>The domain facts turned into their wire shape — the one place they become available + value or reason.</summary>
public static class DefenderReports
{
    /// <summary>The section of one full run: the read (or why there is none), the cores since the previous run, and the week the run's span
    /// joins — which is reported even when this run's read did not answer, because the ledger still holds the spans before it.</summary>
    public static DefenderReport From(DefenderSample sample, DefenderOutcome outcome)
    {
        var read = sample.Read is Reading<DefenderReading>.Available ? Answered(sample, outcome) : Unavailable(sample.Read.ReasonOrEmpty) with { SampledAt = sample.SampledAt };
        return WithWeek(read, outcome.Week) with { LedgerNote = outcome.LedgerNote.Length > 0 ? outcome.LedgerNote : null };
    }

    private static DefenderReport Answered(DefenderSample sample, DefenderOutcome outcome) =>
        new(true, null, sample.SampledAt, Figure(sample.Elapsed.Map(e => Math.Round(e.TotalSeconds, 3))), Figure(outcome.Usage.Map(u => Math.Round(u.Cores, 6))), Figure(outcome.Usage.Map(u => Math.Round(u.Covered.TotalSeconds, 3))), null, null, null, null);

    private static DefenderReport WithWeek(DefenderReport report, Reading<DefenderWeek> week) =>
        report with
        {
            CoreHoursPerWeek = Figure(week.Map(w => Math.Round(w.CoreHoursPerWeek, 4))),
            CoveredHours = Figure(week.Map(w => Math.Round(w.CoveredHours, 4))),
            Extrapolated = week is Reading<DefenderWeek>.Available { Value: var known } ? known.Extrapolated : null,
        };

    private static NumberFigure Figure(Reading<double> reading) => StatusReports.Number(reading);

    public static DefenderReport Unavailable(string reason) => new(false, reason, null, null, null, null, null, null, null, null);
}
