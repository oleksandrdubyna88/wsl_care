using WslCare.Core.Collectors;
using WslCare.Core.Events;
using WslCare.Core.Preview;
using WslCare.Core.Records;
using WslCare.Core.Status;
using WslCare.Core.Thresholds;

namespace WslCare.Core.Collect;

/// <summary>One folder of the daily walk, with its growth since the sample before.</summary>
public sealed record FolderReport(string Id, string Path, ByteFigure Size, long? Files, bool? Complete, ByteFigure GrowthSincePrevious);

/// <summary>The daily folder sizes (plan §4.4), from the newest full run that measured them, with their age.</summary>
/// <param name="MeasuredThisRun">Whether THIS run walked them (once a day), or carried an earlier run's.</param>
public sealed record FoldersReport(bool Available, string? Reason, string? RunId, DateTimeOffset? SampledAt, double? AgeSeconds, bool MeasuredThisRun, IReadOnlyList<FolderReport>? Folders);

/// <summary>What the run's housekeeping did before it measured: the reconcile, the retention sweeps.</summary>
public sealed record HousekeepingReport(ReconcileReport Reconcile, RetentionReport Retention, IReadOnlyList<string> ContainerStartsRetention)
{
    /// <summary>What the <c>running.json</c> sweep did (gate finding #8: every full run sweeps a dead run's file): swept, or the
    /// other run's state that stood in the way; empty when there was nothing to sweep.</summary>
    public string Running { get; init; } = string.Empty;

    /// <summary>What the request sweep did (E6.S1, plan §15k #15): every root full run sweeps the request folder; empty when
    /// nothing was swept.</summary>
    public IReadOnlyList<string> Requests { get; init; } = [];
}

/// <summary>
/// <c>runs/{yyyy-MM-dd}/{runId}.json</c> (plan §6): the full detail of one run — written FIRST of its three records
/// (plan §15b #1). Its head (<see cref="RunDetailHead"/>) is what the reconcile reads when the history line is
/// missing; actions (E3) add the removed objects and before/after numbers.
/// </summary>
public sealed record RunDetail(
    int SchemaVersion,
    RunId RunId,
    RunTrigger Trigger,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    RunOutcome Outcome,
    bool DryRun,
    bool ObserveOnly,
    string Side,
    StatusReport Sample,
    PreviewReport Docker,
    HealthReport Health,
    IReadOnlyList<Verdict> Thresholds,
    FoldersReport Folders,
    StartsWindow ContainerStarts,
    SlowParts Slow,
    HousekeepingReport Housekeeping,
    IReadOnlyList<ActionRecord> Actions)
{
    /// <summary>The timer pass (E3.S3): every action's outcome with its live preview and measured result — what was removed,
    /// in detail. Absent on a full run the timer did not start (and on every detail written before E3.S3).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Actions.Engine.TimerPass? TimerPass { get; init; }

    /// <summary>Plan §15q R1.4: every setting this run used whose value did NOT come from the embedded defaults, with the layer
    /// that set it — so a run the timer did under a user's value says so ("A5 ran with containers.stoppedOlderThanDays = 0, user
    /// layer"). Absent when every value is a default.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<Config.ConfigValueReport>? Config { get; init; }

    /// <summary>How long a TIMER run waited before it started — for the boot to settle, then while the machine was busy
    /// (PLAN_boot_settle.md). Absent on a run the timer did not start.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public RunSettledReport? Settled { get; init; }

    /// <summary>User values this run did not take (plan §15q); absent when none.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<Config.ConfigNoticeReport>? ConfigNotices { get; init; }
}

/// <summary>What became of the run's records: written, not written because this process may not (read-only), a
/// write that failed, or another run holding the lock.</summary>
public enum Recording
{
    Recorded,
    ReadOnly,
    Failed,
    Busy,
}

/// <summary>The answer of <c>collect --json</c>: how the run was recorded, and its detail.</summary>
/// <param name="Recording"><c>recorded</c>, <c>readOnly</c>, <c>failed</c> or <c>busy</c>.</param>
/// <param name="DetailFile">The detail's path relative to the state directory, when one was written.</param>
public sealed record CollectReport(int SchemaVersion, string Recording, string? Reason, string? DetailFile, RunDetail? Detail);

/// <summary>The folders part of a detail or a status answer, from the newest sample and the one before.</summary>
public static class FoldersReports
{
    public static FoldersReport From(Reading<AgedPart<FolderSizesSample>> newest, Reading<FolderSizesSample> previous, bool measuredThisRun) => newest switch
    {
        Reading<AgedPart<FolderSizesSample>>.Available { Value: var part } => new(
            true, null, part.RunId.Text, part.SampledAt, Math.Round(part.Age.TotalSeconds), measuredThisRun,
            [.. (part.Value.Folders ?? []).Select(f => Folder(f, previous))]),
        _ => new(false, newest.ReasonOrEmpty, null, null, null, measuredThisRun, null),
    };

    private static FolderReport Folder(FolderSize folder, Reading<FolderSizesSample> previous)
    {
        var size = folder.Measured ? Reading.Of(folder.Bytes) : Reading.Missing<long>(folder.Unavailable);
        var before = previous.Bind(p => p.Find(folder.Id) is { Measured: true } earlier
            ? Reading.Of(earlier.Bytes)
            : Reading.Missing<long>($"{folder.Id} was not measured by the sample before"));
        return new(folder.Id, folder.Path, StatusReports.Bytes(size), folder.Measured ? folder.Files : null, folder.Measured ? folder.Complete : null, StatusReports.Bytes(Reading.Combine(size, before, (now, then) => now - then)));
    }
}
