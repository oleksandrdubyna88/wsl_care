namespace WslCare.Core.Actions.Engine;

/// <summary>
/// E7.S2b/S2c review C-H2: the steps a run makes — a command started or ended, a stretch of a folder walk, an action begun — so
/// a run whose heartbeat beats on a timer but that made no step for <c>running.noProgressMinutes</c> reads WEDGED (a hung 9p
/// read keeps a heartbeat fresh forever) and <c>act --stop</c> can end it. A run's own counter, in its own flow (an
/// <see cref="AsyncLocal{T}"/>: parallel runs in one test process never move each other's); a mark outside a run counts nothing.
/// </summary>
public sealed class RunProgress
{
    private static readonly AsyncLocal<RunProgress?> Scope = new();
    private long _steps;

    /// <summary>How many steps this run made so far.</summary>
    public long Steps => Interlocked.Read(ref _steps);

    /// <summary>A new counter for the run that calls it, from here on in its flow.</summary>
    public static RunProgress Begin()
    {
        var progress = new RunProgress();
        Scope.Value = progress;
        return progress;
    }

    /// <summary>One step of the run in this flow, if any.</summary>
    public static void Mark()
    {
        if (Scope.Value is { } progress)
        {
            Interlocked.Increment(ref progress._steps);
        }
    }
}
