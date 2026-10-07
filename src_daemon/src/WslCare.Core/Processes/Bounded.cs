namespace WslCare.Core.Processes;

/// <summary>
/// A file-system lookup under a ceiling (the ONE place this is done): its answer, or <paramref name="timedOut"/> when it does
/// not answer in time. A read blocked in the kernel (a 9p share the host stopped serving) cannot be cancelled, so the lookup
/// runs on a thread of its own (never the pool's) and is left to finish on its own — its answer ignored and any fault observed
/// — rather than holding the caller. A lookup that throws surfaces its own exception, never an <see cref="AggregateException"/>
/// around it. Used by the Windows system-drive lookup and by the agent discovery (E7.S1/S2 review R1).
/// </summary>
public static class Bounded
{
    public static T Run<T>(Func<T> lookup, TimeSpan ceiling, T timedOut, CancellationToken cancellationToken = default)
    {
        // Its own thread, never the pool's: a read the host stopped answering may never return, and an abandoned lookup must
        // not hold a thread-pool thread for the life of the process (measured: blocked pool threads delayed unrelated work).
        var running = Task.Factory.StartNew(lookup, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _ = running.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        // WaitAny, not Wait: Wait throws an AggregateException for a lookup that threw, and a caller handling the lookup's own
        // failure would never meet its type; GetResult rethrows the lookup's exception itself (PR #10 retro round).
        return Task.WaitAny([running], Milliseconds(ceiling), cancellationToken) == 0 ? running.GetAwaiter().GetResult() : timedOut;
    }

    /// <summary>The ceiling in whole milliseconds, within what a wait accepts.</summary>
    private static int Milliseconds(TimeSpan ceiling) => (int)Math.Clamp(ceiling.TotalMilliseconds, 0, int.MaxValue);
}
