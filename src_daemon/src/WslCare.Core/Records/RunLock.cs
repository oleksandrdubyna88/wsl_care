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
/// <para><b>Residual:</b> the lock is the OPEN of a path. Remove the file while it is held and the next run locks a new
/// file at the same path — two runs. <c>running.json</c> is the second fence: an <c>act</c> that took the lock still
/// refuses when a LIVE run claims to be acting.</para>
/// </remarks>
public static class RunLock
{
    public static ExclusiveLock TryTake(IHostPaths paths, IFileSystem files) => files.TryLockExclusive(paths.RunLockFile);
}
