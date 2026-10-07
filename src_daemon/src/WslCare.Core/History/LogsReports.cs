using WslCare.Core.Actions;

namespace WslCare.Core.History;

// The wire shapes of `logs --json` and `runs --json` (plan §6, §7.4). Every instant is UTC; a figure that was not
// recorded is absent (null), never 0 (plan §15b #7).

/// <summary>The period an answer covers, as UTC dates — and, for an instant range (<c>--from</c> / <c>--to</c>, §15j M7), the
/// two instants themselves, in UTC: a run belongs to it when it STARTED at or after <see cref="FromInstant"/> and before
/// <see cref="ToInstant"/>; <see cref="From"/> / <see cref="To"/> are then the UTC days those instants touch.</summary>
public sealed record PeriodReport(string Label, string From, string To)
{
    public DateTimeOffset? FromInstant { get; init; }

    public DateTimeOffset? ToInstant { get; init; }

    public static PeriodReport Of(LogPeriod period) =>
        new(period.Label, period.FromText, period.ToText) { FromInstant = period.Instants?.From, ToInstant = period.Instants?.To };
}

/// <summary>One action's line of a run, as the history keeps it.</summary>
/// <param name="Failure">Why it failed, when it did — beside its figures, which still count (a failed action's measured
/// deletions are real).</param>
public sealed record RunActionLine(string Id, string? Status, int Count, long FreedBytes, long? WouldFreeBytes, string? Failure);

/// <summary>One run of the period.</summary>
/// <param name="DetailState"><c>present</c>, <c>lost</c> (the history names a detail that is gone, plan §15b #1) or <c>none</c>.</param>
/// <param name="Cleanup">At least one action ran and removed (or freed) something (plan §7.4: runs with a cleanup).</param>
public sealed record RunLine(
    string RunId,
    string Trigger,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    string Outcome,
    bool? DryRun,
    string? Detail,
    string DetailState,
    long FreedBytes,
    long? WouldFreeBytes,
    bool Cleanup,
    IReadOnlyList<RunActionLine> Actions,
    string? Reason)
{
    /// <summary>The headline figures the history line carries (<c>MemAvailable</c>, page cache, swap, <c>/</c>, Docker
    /// reclaimable, container starts) — what the Logs page's sparkline and trends read (§15j M7). Additive (E6.S0): absent on
    /// a line that recorded none (an <c>act</c>, a swept run, every line before E2.S3), never 0.</summary>
    public Records.RunMetrics? Metrics { get; init; }

    /// <summary>What the run was (plan §15o): <c>collect</c> — a full check, whatever started or ended it — or <c>act</c>; the ONE
    /// rule a reader tells a full check by. Additive: absent on a line that carries none (written before it existed, an unusable
    /// request's, an unreadable orphan's).</summary>
    public string? Kind { get; init; }
}

/// <summary>The answer of <c>runs [--period …] --json</c>: every run of the period, oldest first.</summary>
public sealed record RunsReport(int SchemaVersion, PeriodReport Period, int Count, IReadOnlyList<RunLine> Runs, int UnparseableLines, string? Problem);

/// <summary>One action's totals over the period.</summary>
/// <param name="Runs">The runs in which it RAN (succeeded).</param>
/// <param name="Count">The objects it removed — in the runs it ran AND in those it failed (what a failed action measurably
/// removed is gone all the same).</param>
/// <param name="FreedBytes">The bytes it freed, on the same footing as <paramref name="Count"/>.</param>
/// <param name="DryRuns">The runs in which the timer only previewed it (dry run).</param>
/// <param name="WouldFreeBytes">What those dry runs would have freed.</param>
/// <param name="Failed">The runs in which it FAILED.</param>
public sealed record ActionTotal(string Id, int Runs, int Count, long FreedBytes, int DryRuns, long WouldFreeBytes, int Failed);

/// <summary>The runs of the period, counted (plan §7.4).</summary>
public sealed record RunCounts(int Total, int WithCleanup, int WithoutCleanup, int DryRun, long WouldFreeBytes, int Timer, int Manual, int Cli, int Failed, int Interrupted);

/// <summary>A run that freed the most or the least (non-zero).</summary>
public sealed record RunExtreme(string RunId, DateTimeOffset StartedAt, long FreedBytes);

/// <summary>One recorded value of a metric, and when.</summary>
public sealed record MetricPoint(double Value, DateTimeOffset At, string RunId);

/// <summary>A metric's maximum and minimum over the period, with when they occurred; absent when no run recorded it.</summary>
public sealed record MetricExtremes(string Name, string Unit, int Samples, MetricPoint? Max, MetricPoint? Min);

/// <summary>
/// One cleanup in detail (plan §7.4 <i>what exactly was removed</i>): an action that ran in a run of the period, with every
/// object it removed — and those it did not, each with why — from the run's detail file.
/// </summary>
public sealed record CleanupDetail(
    string RunId,
    DateTimeOffset StartedAt,
    string Trigger,
    string Action,
    string Status,
    int Count,
    long FreedBytes,
    string? FreedBasis,
    string DetailState,
    IReadOnlyList<ActionItem> Removed,
    IReadOnlyList<ActionItem> NotRemoved,
    IReadOnlyList<string> Notes)
{
    /// <summary>Why the action failed, when it did — shown beside its figures.</summary>
    public string? Failure { get; init; }
}

/// <summary>The answer of <c>logs [--period …] [--action …] --json</c> (plan §7.4).</summary>
/// <param name="Action">The one action asked for, or absent for all.</param>
public sealed record LogsReport(
    int SchemaVersion,
    PeriodReport Period,
    string? Action,
    long FreedBytes,
    int ObjectsRemoved,
    IReadOnlyList<ActionTotal> PerAction,
    RunCounts Runs,
    RunExtreme? MostFreed,
    RunExtreme? LeastFreed,
    IReadOnlyList<MetricExtremes> Metrics,
    IReadOnlyList<CleanupDetail> Cleanups,
    int UnparseableLines,
    string? Problem)
{
    /// <summary>How many run details were looked up for the cleanups' objects (none unless asked: <c>--detail</c> or one
    /// <c>--action</c>, gate finding #10) — a detail opened but UNREADABLE (it does not parse, or is of a kind this build does not
    /// read) is not counted here (PR #44 gate round).</summary>
    public int DetailsRead { get; init; }

    /// <summary>How many runs with a cleanup are listed from their history line alone — not asked for, past the bound, or whose
    /// detail was opened and could not be read (its cleanups say <c>unreadable</c>).</summary>
    public int DetailsNotRead { get; init; }
}
