using WslCare.Core.Collect;
using WslCare.Core.Records;

namespace WslCare.Core.Actions.Engine;

/// <summary>What the engine is asked: these actions, started by this trigger, previewed or executed.</summary>
public sealed record ActRequest(IReadOnlyList<ActionId> Ids, RunTrigger Trigger, bool Execute);

/// <summary>What became of one action in a run — the closed set of <see cref="ActionStatus"/> names.</summary>
public static class ActionStatus
{
    /// <summary>Only a preview was asked (<c>act --preview</c>).</summary>
    public const string Previewed = "previewed";

    /// <summary>It ran and succeeded.</summary>
    public const string Ran = "ran";

    /// <summary>It ran and failed — recorded, and the run went on (plan §5).</summary>
    public const string Failed = "failed";

    /// <summary>The timer's dry run: previewed, recorded as "would have", nothing run.</summary>
    public const string DryRun = "dryRun";

    /// <summary>Not for this run: its <c>auto</c> switch is off, its trigger did not fire, another side's action, observe-only.</summary>
    public const string Skipped = "skipped";

    /// <summary>The machine is busy (plan §5 <i>Heavy actions wait for idle</i>): left to the next run.</summary>
    public const string Deferred = "deferred";

    /// <summary>It may not run: no target user, its preview could not be read, or it refuses on the live state.</summary>
    public const string Refused = "refused";
}

/// <summary>One action's line of an <c>act</c> run: its status and why, the LIVE preview, and — when it ran — what it did.</summary>
public sealed record ActionOutcome(string Id, string Summary, string Status, string Reason, ActionPreview? Preview, ActionRun? Run);

/// <summary>Who the run's user-scoped actions were for (plan §15c #2), as the run detail keeps it.</summary>
public sealed record TargetUserReport(bool Found, string? Name, string? Home, string Source)
{
    public static TargetUserReport From(TargetUserResult result) => result switch
    {
        TargetUserResult.Found f => new(true, f.User.Name, f.User.Home, f.Source),
        _ => new(false, null, null, result.Refusal),
    };
}

/// <summary>
/// <c>runs/{yyyy-MM-dd}/{runId}.json</c> of an <c>act</c> run (plan §6): every action's status, its live preview, what it
/// removed, its before / after and the commands it ran. Its head (<see cref="RunDetailHead"/>'s fields, same names) is
/// what the reconcile reads, exactly as for a full run's detail.
/// </summary>
/// <param name="Kind">Always <c>act</c> — a full run's detail is the other kind.</param>
/// <param name="Notes">What the engine did before the actions (a swept <c>running.json</c>, the reconcile).</param>
public sealed record ActRunDetail(
    int SchemaVersion,
    RunId RunId,
    RunTrigger Trigger,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    bool DryRun,
    string Kind,
    RunOutcome Outcome,
    string Side,
    string DryRunReason,
    TargetUserReport TargetUser,
    IReadOnlyList<ActionOutcome> Actions,
    IReadOnlyList<string> Notes);

/// <summary>How an <c>act</c> ended — a closed set.</summary>
public abstract record ActResult
{
    private ActResult()
    {
    }

    /// <summary>Previews only: nothing locked, nothing written.</summary>
    public sealed record Previewed(IReadOnlyList<ActionOutcome> Actions, TargetUserReport TargetUser) : ActResult;

    /// <summary>The run happened (or tried to): its detail, and how its records went.</summary>
    public sealed record Done(ActRunDetail Detail, Recording Recording, string DetailFile, string Reason) : ActResult;

    /// <summary>Another run holds the lock, or a LIVE run claims <c>running.json</c>: nothing was done.</summary>
    public sealed record Busy(string Reason) : ActResult;

    /// <summary>A live run has stopped beating, or the running state cannot be told: nothing was done, nothing killed.</summary>
    public sealed record Wedged(string Reason) : ActResult;
}

/// <summary>
/// The answer of <c>act … --json</c> (plan §6): per action <i>what</i>, its status and reason, its preview (count, bytes,
/// up to 20 items) and — when it ran — its measured result; plus how the run was recorded.
/// </summary>
/// <param name="Mode"><c>preview</c> or <c>run</c>.</param>
/// <param name="Result"><c>previewed</c>, <c>recorded</c>, <c>failed</c> (the records could not be written), <c>busy</c>,
/// <c>wedged</c>.</param>
public sealed record ActReport(
    int SchemaVersion,
    string Mode,
    string Result,
    string? Reason,
    string? RunId,
    string? DetailFile,
    bool? DryRun,
    string? DryRunReason,
    TargetUserReport? TargetUser,
    IReadOnlyList<ActionOutcome> Actions)
{
    public static ActReport From(ActResult result) => result switch
    {
        ActResult.Previewed p => new(Core.SchemaVersion.Current, "preview", "previewed", null, null, null, null, null, p.TargetUser, p.Actions),
        ActResult.Done d => new(
            Core.SchemaVersion.Current, "run", d.Recording == Recording.Recorded ? "recorded" : "failed", d.Reason.Length == 0 ? null : d.Reason, d.Detail.RunId.Text,
            d.DetailFile.Length == 0 ? null : d.DetailFile, d.Detail.DryRun, d.Detail.DryRunReason, d.Detail.TargetUser, d.Detail.Actions),
        ActResult.Busy b => new(Core.SchemaVersion.Current, "run", "busy", b.Reason, null, null, null, null, null, []),
        ActResult.Wedged w => new(Core.SchemaVersion.Current, "run", "wedged", w.Reason, null, null, null, null, null, []),
        _ => throw new System.Diagnostics.UnreachableException("ActResult is a closed set"),
    };
}
