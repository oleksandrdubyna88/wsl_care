using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Core.Records;

/// <summary>
/// THE run lock (plan §5 <i>Global guards</i>: one run at a time, <c>flock</c> on <c>/run/wsl-care.lock</c>, shared by the
/// timer and the buttons): ONE file for <c>collect</c> and <c>act</c> (E3.S1 superseded E2.S3's <c>{state}/run.lock</c>).
/// </summary>
/// <remarks>
/// <para><b>The atomic operation</b> is an exclusive open (<see cref="IFileSystem.TryLockExclusive"/>: <c>FileShare.None</c>,
/// which .NET takes as <c>flock(LOCK_EX | LOCK_NB)</c> on Linux and a sharing violation on Windows), released by the
/// operating system when the holder dies — so the lock is never stale and nothing sweeps it.</para>
/// <para><b>The rule between collect and act: the second one REFUSES, it never waits.</b> Exit 75 (<c>EX_TEMPFAIL</c>),
/// nothing measured, nothing acted on, nothing written. The timer simply runs again at its next tick; a button shows
/// <i>busy</i> and can be pressed again — a wait would be a run the person cannot see.</para>
/// <para><b>The one exception — an ACCEPTED detached run</b> (<c>act --request</c>, retro round over PR #11, O1): a
/// <c>--detach</c> CHECK holds this lock while it sweeps the request folder, counts and reads the running state, and the run an
/// EARLIER detach accepted must not be recorded refused because a second click landed in that window. That run waits for the
/// lock up to <see cref="AcceptedRunWait"/> (<c>requests.lockWaitSeconds</c>), retrying with the lock jitter; it stays visible
/// meanwhile — its request stands, so the running block reads <c>queued</c>. Past the wait it is refused as before.</para>
/// <para><b>Residual:</b> the lock is the OPEN of a path. Remove the file while it is held and the next run locks a new
/// file at the same path — two runs. <c>running.json</c> is the second fence: an <c>act</c> that took the lock still
/// refuses when a LIVE run claims to be acting. A detach check slower than the wait (many stale requests, each asked of
/// systemd under its own ceiling) still refuses the accepted run — recorded <c>refused</c>, never lost.</para>
/// </remarks>
public static class RunLock
{
    /// <summary>How long an accepted detached run waits for the lock (<c>requests.lockWaitSeconds</c>).</summary>
    public static TimeSpan AcceptedRunWait => Tuning.Current.Seconds(ConfigKeys.Requests.LockWaitSeconds);

    public static ExclusiveLock TryTake(IHostPaths paths, IFileSystem files) => files.TryLockExclusive(paths.RunLockFile);

    /// <summary>The lock, tried again for up to <paramref name="wait"/> (measured on the monotonic clock) while another holds it;
    /// a zero wait is one try, exactly <see cref="TryTake"/>. A cancellation ends the wait (the caller records the run cut off).</summary>
    public static async Task<ExclusiveLock> TakeAsync(IHostPaths paths, IFileSystem files, TimeSpan wait, CancellationToken cancellationToken)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        var taken = TryTake(paths, files);
        while (taken is ExclusiveLock.Busy && waited.Elapsed < wait)
        {
            await Task.Delay(Pause(), cancellationToken).ConfigureAwait(false);
            taken = TryTake(paths, files);
        }

        return taken;
    }

    /// <summary>The random pause between two tries — the same jitter as every other lock of this product.</summary>
    private static TimeSpan Pause() =>
        TimeSpan.FromMilliseconds(Random.Shared.Next(Tuning.Current.Int(ConfigKeys.FileLocks.LockJitterMinMilliseconds), Tuning.Current.Int(ConfigKeys.FileLocks.LockJitterMaxMilliseconds)));
}
