using WslCare.Core.Config;

namespace WslCare.Core.Archive;

/// <summary>How the base of a run, a restore or a list is judged (plan §15r E9.S4 own review round S-M1): ALREADY, by the caller
/// (a test hands its judged base), or LATE — inside the bounded reachability window (<see cref="BaseWindow"/>), after the side's lock
/// for <c>archive reach</c>. A share that stops answering blocks its reader in the kernel; judged late, it blocks a task the child
/// abandons at the window's end, never the child itself before its lock.</summary>
public sealed record BaseJudging(bool Late, Func<BaseFolderReport> Judge)
{
    public static BaseJudging Already { get; } = new(false, static () => BaseFolderRules.Unconfigured);

    public static BaseJudging Within(Func<BaseFolderReport> judge) => new(true, judge);
}

/// <summary>An input whose base was judged, and what stopped it there (<see cref="EarlyStop.None"/> when nothing did).</summary>
public sealed record BaseWithin(ArchiveRunInput Input, EarlyStop Stop);

/// <summary>
/// The bounded window of the base (plan §15r D1, E9.S4 own review round S-M1): a LATE base is judged — and the checks that read it
/// are run — in ONE task the child waits for at most <c>archive.reachabilitySeconds</c>; past it the child answers
/// <c>unreachable</c> and leaves the task to the kernel. Nothing the base does can hold the child longer.
/// </summary>
public static class BaseWindow
{
    /// <summary>The base judged (late ones within the window) and <paramref name="then"/> asked of it in the same window.</summary>
    public static BaseWithin Judged(ArchiveRunInput input, Func<ArchiveRunInput, EarlyStop> then) =>
        input.Judging.Late ? Bounded(input, then) : new BaseWithin(input, then(input));

    /// <summary>Whether the judged base answers within <c>archive.reachabilitySeconds</c>; <see cref="EarlyStop.None"/> when it does.</summary>
    public static EarlyStop Reachability(ArchiveRunInput input) =>
        input.Reachable(input.JudgedBase.Folder, Ceiling(input)) ? EarlyStop.None : Unanswered;

    /// <summary>The base that did not answer in time: nothing was touched.</summary>
    public static EarlyStop Unanswered { get; } =
        new(RunOutcomes.Unreachable, $"the base did not answer within {ConfigKeys.Archive.ReachabilitySeconds.Name}; nothing was touched (the reconcile waits too)");

    private static BaseWithin Bounded(ArchiveRunInput input, Func<ArchiveRunInput, EarlyStop> then)
    {
        // Not the run's token: a cancellation before the start would read as a failure of the base, and the window bounds the wait anyway.
        var work = Task.Run(() => Checked(input with { JudgedBase = input.Judging.Judge() }, then), CancellationToken.None);
        try
        {
            return work.Wait(Ceiling(input), CancellationToken.None) ? work.Result : new BaseWithin(input, Unanswered);
        }
        catch (AggregateException e)
        {
            return new BaseWithin(input, new EarlyStop(RunOutcomes.Refused, $"the base could not be judged: {e.InnerException?.Message ?? e.Message}"));
        }
    }

    private static BaseWithin Checked(ArchiveRunInput judged, Func<ArchiveRunInput, EarlyStop> then) => new(judged, then(judged));

    private static TimeSpan Ceiling(ArchiveRunInput input) => TimeSpan.FromSeconds(input.Config.Int(ConfigKeys.Archive.ReachabilitySeconds));
}
