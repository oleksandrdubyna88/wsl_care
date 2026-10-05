using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Records;

namespace WslCare.Core.Actions.Engine;

/// <summary>
/// The sweep of a <c>running.json</c> a previous run left (plan §15 #6, §15a #0) — ONE implementation for the action engine
/// (every <c>act</c>, the timer pass) and for every full run's housekeeping (gate finding #8: a <c>collect</c> from a terminal
/// or the panel sweeps a dead run too, timer or not). A dead or mismatched run gets its
/// <c>interrupted</c> history line, THEN its file goes; anything else that is not this run's own file is a state no run may
/// act over.
/// </summary>
public abstract record RunningSweep
{
    private RunningSweep()
    {
    }

    /// <summary>Nothing stands in the way: no file, this run's own, or a dead run swept (<paramref name="Note"/> says which).</summary>
    public sealed record Clear(string Note) : RunningSweep;

    /// <summary>Another run's state stands in the way — live, wedged, uninspectable, unreadable, or a sweep that failed.</summary>
    public sealed record Blocked(ActResult Refusal) : RunningSweep
    {
        public string Reason => Refusal switch
        {
            ActResult.Busy b => b.Reason,
            ActResult.Wedged w => w.Reason,
            ActResult.StateUnreadable u => u.Reason,
            _ => "the running state does not allow a run",
        };
    }

    /// <summary>Judges the file and sweeps a dead run's; <paramref name="ownRunId"/> with <paramref name="ownPid"/> names this
    /// run's own file (a full run's, met again by its timer pass), which is never in its own way.</summary>
    public static RunningSweep Apply(IHostPaths paths, IFileSystem files, IProcessTable processes, DateTimeOffset now, RunningReadRetry retry, RunId ownRunId, int ownPid) =>
        RunningState.Read(paths, files, processes, now, retry) switch
        {
            RunningStatus.None => new Clear(string.Empty),
            RunningStatus.Live live when live.File.RunId == ownRunId && live.File.Pid == ownPid => new Clear(string.Empty),
            RunningStatus.Dead dead => TrySweep(paths, files, dead),
            RunningStatus.Live live => new Blocked(new ActResult.Busy($"run {live.File.RunId} (pid {live.File.Pid}) is acting although the run lock was free - the lock file was replaced; nothing was done")),
            RunningStatus.Wedged wedged => new Blocked(new ActResult.Wedged(WedgedReason(wedged))),
            RunningStatus.Unknown unknown => new Blocked(new ActResult.Wedged($"the running state cannot be told: {unknown.Reason}; nothing was done, nothing was killed")),
            RunningStatus.Unreadable unreadable => new Blocked(new ActResult.StateUnreadable($"{unreadable.Reason}; nothing was done, nothing was killed")),
            _ => throw new System.Diagnostics.UnreachableException("RunningStatus is a closed set"),
        };

    public static string WedgedReason(RunningStatus.Wedged w) =>
        $"run {w.File.RunId} is wedged: pid {w.File.Pid} is alive but its heartbeat is {w.HeartbeatAge.TotalSeconds:0} s old ({w.File.Current}); nothing was killed - stop it by hand, then run again";

    /// <summary>A sweep that cannot write its record or remove the file blocks: the state is not one to act on.</summary>
    private static RunningSweep TrySweep(IHostPaths paths, IFileSystem files, RunningStatus.Dead dead)
    {
        try
        {
            return new Clear(SweepDead(paths, files, dead));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or TimeoutException)
        {
            return new Blocked(new ActResult.Wedged($"the running.json of the dead run {dead.File.RunId} could not be swept ({e.Message}); nothing was done"));
        }
    }

    /// <summary>Plan §15a #0: the dead run gets its <c>interrupted</c> history line, THEN its <c>running.json</c> goes.</summary>
    private static string SweepDead(IHostPaths paths, IFileSystem files, RunningStatus.Dead dead)
    {
        var file = dead.File;
        if (RunHistory.Read(paths, files).Records.Any(r => r.RunId == file.RunId))
        {
            // The run recorded itself and died before removing the file: its history already tells the truth.
            RunningState.Remove(paths, files);
            return $"removed the running.json of run {file.RunId}: {dead.Why}, and the run had already recorded itself";
        }

        // E6.S1 (plan §15k #18): a run act --stop asked systemd to stop and that was killed after TimeoutStopSec says so.
        var stopped = StopMarkers.StoppedReason(paths, files, file.RunId);
        // Plan §15o: per-action results only — a full check's own name (reserved, never an action id) is the run, not a row.
        // file.Actions is never null here: a Dead status comes only from RunningState.Read, which refuses a file without it
        // (`Actions: not null`), so an older or broken file is Unreadable and never reaches this sweep (§15o review G4).
        var actions = file.Actions.Where(a => a != RunKinds.FullCheckName).Select(a => new ActionRecord(a, 0, 0) { Status = ActionStatus.Interrupted });
        var line = new RunRecord(Core.SchemaVersion.Current, file.RunId, file.Trigger, file.StartedAt, file.HeartbeatAt, RunOutcome.Interrupted, [.. actions], file.KindOrMarker())
        {
            Reason = $"{(stopped.Length > 0 ? stopped + "; swept" : "swept")}: {dead.Why}; it was on {(file.Current.Length > 0 ? file.Current : "no action yet")}, last heartbeat {file.HeartbeatAt.UtcDateTime:yyyy-MM-dd HH:mm:ss}Z",
        };
        new RunRecordWriter(paths, files).Append(line);
        RunningState.Remove(paths, files);
        StopMarkers.Remove(paths, files, file.RunId);
        return $"swept the running.json of run {file.RunId}: {dead.Why} (recorded as interrupted)";
    }
}
