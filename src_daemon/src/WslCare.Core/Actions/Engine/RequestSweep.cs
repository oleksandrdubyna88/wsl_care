using WslCare.Core.Config;
using System.Globalization;

using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Processes;
using WslCare.Core.Records;
using WslCare.Core.Systemd;

namespace WslCare.Core.Actions.Engine;

/// <summary>
/// The sweep of the request folder (E6.S1, plan §15j B2, §15k #2 / #15, the E6.S1 review round): run UNDER THE LOCK at the
/// start of every root operation — <c>collect</c> (timer or detached), <c>act --request</c>, and <c>--detach</c> itself (review
/// D1: an orphaned request must never block the panel's only remedy) — never by the unprivileged <c>status</c>.
/// OWNERSHIP-checked, history first:
/// <list type="bullet">
/// <item>a request whose run already has a history line only loses its file (the run recorded itself; no second line);</item>
/// <item>a request younger than <see cref="Grace"/> — aged by the MONOTONIC clock within its boot (review D3), by the wall
/// clock only when it carries no boot stamp — is left alone: it may not have reached <c>systemctl start</c> yet;</item>
/// <item>an older one, one of an earlier boot, or one stamped in the future (review S2) is still PENDING while its unit has a
/// queued job or is active / activating / deactivating / reloading (<c>systemctl show --property=ActiveState
/// --property=Job</c> — a queued start has no active state yet);</item>
/// <item>otherwise the run never recorded itself: the history is read AGAIN for that run (review D4 — its own run may have
/// recorded <c>refused</c> since the snapshot), and only when it is still silent does it get ONE <c>interrupted</c> line, then
/// its request goes;</item>
/// <item>a request that cannot be used (review D5) is recorded <c>refused</c> with the reason and removed — it would otherwise
/// hold the state <c>unreadable</c> and refuse every detach forever.</item>
/// </list>
/// A unit whose state cannot be read keeps its request (it may still run). The run's OWN request is never swept. The
/// <c>systemctl show</c> runs with no cancellation (it has its own ceiling): a signal arriving mid-sweep must not cut a run off
/// before it could record anything (review D2). Stop markers whose run has a line, or older than a day, go too.
/// </summary>
public static class RequestSweep
{
    /// <summary>How long a request may wait for its unit to take it (review D1: a short monotonic grace, not 15 wall-clock minutes
    /// — the window is detach's own, between writing the request and <c>systemctl start --no-block</c> returning).</summary>
    public static TimeSpan Grace => Tuning.Current.Seconds(ConfigKeys.Requests.GraceSeconds);

    /// <summary>How far ahead of the wall clock an unstamped request's creation may be before it counts as stale (review S2).</summary>
    public static TimeSpan FutureSkew => Tuning.Current.Seconds(ConfigKeys.Requests.FutureSkewSeconds);

    public static async Task<IReadOnlyList<string>> ApplyAsync(IHostPaths paths, IFileSystem files, ICommandRunner commands, IProcessTable processes, DateTimeOffset now, RunId? own)
    {
        if (HistoryProblem(paths, files) is { Length: > 0 } problem)
        {
            // Review C-H1: with an unreadable history no request can be told recorded or not — sweeping would write interrupted lines
            // for runs that DID record themselves. Every request is kept until the history reads again.
            return [$"the request sweep is skipped: {problem}; every request is kept"];
        }

        var recorded = Recorded(paths, files);
        var boot = processes.Boot();
        var notes = new List<string>();
        foreach (var read in RunRequests.List(paths, files))
        {
            notes.Add(read switch
            {
                RunRequestRead.Parsed parsed when parsed.File.RunId != own => await OneAsync(paths, files, commands, boot, now, parsed.File, recorded.Contains(parsed.File.RunId)).ConfigureAwait(false),
                RunRequestRead.Bad bad when RunRequests.FiledRunId(bad.Path) is { } filed && filed != own => Unusable(paths, files, now, filed, bad.Why),
                _ => string.Empty,
            });
        }

        notes.AddRange(OldStopMarkers(paths, files, now, recorded));
        return [.. notes.Where(n => n.Length > 0)];
    }

    /// <summary>A request that cannot be used (review D5): ONE <c>refused</c> line naming why — unless its run has a line — then it
    /// goes. Shared with <c>act --request</c>, which meets its own. Its kind cannot be known (the request did not read), so the line
    /// carries none, and its reason begins with <see cref="HistoryReasons.UnusableRequestPrefix"/> (plan §15o).</summary>
    public static string Unusable(IHostPaths paths, IFileSystem files, DateTimeOffset now, RunId runId, string why)
    {
        if (HistoryProblem(paths, files) is { Length: > 0 } problem)
        {
            return $"kept the unusable request of run {runId} ({why}): {problem}";
        }

        if (!Recorded(paths, files).Contains(runId))
        {
            new RunRecordWriter(paths, files).Append(new RunRecord(Core.SchemaVersion.Current, runId, RunTrigger.Manual, now, now, RunOutcome.Refused, [], RecordedKind.Absent)
            {
                Reason = $"{HistoryReasons.UnusableRequestPrefix} ({why}); nothing was run",
            });
        }

        return Joined($"removed the unusable request of run {runId} ({why})", RunRequests.Remove(paths, files, runId));
    }

    /// <summary>The state of a run's unit, as <c>systemctl show</c> tells it (also asked by <c>--detach</c> when its start's outcome
    /// is unknown, review D6). Never cancelled: the command has its own ceiling.</summary>
    public static async Task<UnitState> UnitStateAsync(ICommandRunner commands, RunId runId) =>
        await commands.RunAsync(UnitCommands.Show(runId).ToRequest(), CancellationToken.None).ConfigureAwait(false) switch
        {
            CommandOutcome.Exited { ExitCode: 0 } exited when UnitCommands.Busy(exited.Stdout.Text) => new UnitState.Busy(),
            CommandOutcome.Exited { ExitCode: 0 } exited => new UnitState.Done(UnitCommands.ActiveState(exited.Stdout.Text)),
            CommandOutcome.Exited other => new UnitState.Unread($"systemctl show exited {other.ExitCode}: {other.Stderr.Text.Trim()}"),
            CommandOutcome.TimedOut => new UnitState.Unread("systemctl show timed out"),
            CommandOutcome.FailedToStart failed => new UnitState.Unread(failed.Reason),
            CommandOutcome.Refused refused => new UnitState.Unread(refused.Reason),
            _ => throw new System.Diagnostics.UnreachableException("CommandOutcome is a closed set"),
        };

    /// <summary>Why the request is past its grace, in words; empty while it is within it. The monotonic clock decides within one
    /// boot; another boot is stale at once; only a request with no boot stamp (or a side that cannot tell) falls back to the
    /// wall clock — and one stamped ahead of either clock is stale, never held forever (review S2, D3).</summary>
    public static string Staleness(RunRequestFile request, BootClock boot, DateTimeOffset now)
    {
        if (request.BootId.Length > 0 && boot.Known)
        {
            return request.BootId != boot.BootId ? "its request was written in an earlier boot" : MonotonicStaleness(boot.MonotonicMilliseconds - request.CreatedMonotonicMs);
        }

        var wall = now - request.CreatedAt;
        return request.CreatedAt > now + FutureSkew ? $"its request claims a creation {Age(request.CreatedAt - now)} in the future"
            : wall < Grace ? string.Empty
            : $"its request is {Age(wall)} old";
    }

    private static string MonotonicStaleness(long milliseconds)
    {
        var age = TimeSpan.FromMilliseconds(milliseconds);
        return age < TimeSpan.Zero ? "its request is stamped ahead of this boot's monotonic clock"
            : age < Grace ? string.Empty
            : $"its request is {Age(age)} old";
    }

    private static async Task<string> OneAsync(IHostPaths paths, IFileSystem files, ICommandRunner commands, BootClock boot, DateTimeOffset now, RunRequestFile request, bool recorded)
    {
        if (recorded)
        {
            return Joined($"removed the request of run {request.RunId}: the run recorded itself", RunRequests.Remove(paths, files, request.RunId));
        }

        if (Staleness(request, boot, now) is not { Length: > 0 } stale)
        {
            return string.Empty;
        }

        return await UnitStateAsync(commands, request.RunId).ConfigureAwait(false) switch
        {
            UnitState.Unread unread => $"kept the request of run {request.RunId}: its unit's state could not be read ({unread.Why})",
            UnitState.Busy => string.Empty,
            UnitState.Done done => Interrupted(paths, files, now, request, done.ActiveState, stale),
            _ => throw new System.Diagnostics.UnreachableException("UnitState is a closed set"),
        };
    }

    /// <summary>The run never recorded itself and its unit is not busy. The history is read again first (review D4): a run that
    /// recorded itself since the snapshot only loses its request. Otherwise ONE interrupted line, then the request goes.</summary>
    private static string Interrupted(IHostPaths paths, IFileSystem files, DateTimeOffset now, RunRequestFile request, string activeState, string stale)
    {
        if (HistoryProblem(paths, files) is { Length: > 0 } problem)
        {
            return $"kept the request of run {request.RunId}: {problem}";
        }

        if (Recorded(paths, files).Contains(request.RunId))
        {
            return Joined($"removed the request of run {request.RunId}: the run recorded itself while the sweep looked", RunRequests.Remove(paths, files, request.RunId));
        }

        var reason = $"swept: the detached run never recorded itself - its unit {Processes.Policy.SlotKind.ActUnit.Of(request.RunId)} is {(activeState.Length > 0 ? activeState : "unknown to systemd")} with no queued job, and {stale}";
        new RunRecordWriter(paths, files).Append(request.TerminalLine(request.CreatedAt, now, RunOutcome.Interrupted, ActionStatus.Interrupted, reason));
        return Joined($"swept the request of run {request.RunId}: its unit is not running (recorded as interrupted)", RunRequests.Remove(paths, files, request.RunId));
    }

    private static HashSet<RunId> Recorded(IHostPaths paths, IFileSystem files) => [.. RunHistory.Read(paths, files).Records.Select(r => r.RunId)];

    /// <summary>Why the history cannot be read now; empty when it can.</summary>
    private static string HistoryProblem(IHostPaths paths, IFileSystem files) => RunHistory.Read(paths, files).Problem;

    private static string Age(TimeSpan age) =>
        age >= TimeSpan.FromMinutes(1)
            ? string.Create(CultureInfo.InvariantCulture, $"{age.TotalMinutes:0} min")
            : string.Create(CultureInfo.InvariantCulture, $"{age.TotalSeconds:0} s");

    private static IEnumerable<string> OldStopMarkers(IHostPaths paths, IFileSystem files, DateTimeOffset now, IReadOnlySet<RunId> recorded) =>
        StopMarkers.List(paths, files)
            .Where(id => recorded.Contains(id) || now - StartOf(id) > StopMarkers.KeptFor)
            .Select(id =>
            {
                StopMarkers.Remove(paths, files, id);
                return $"removed the stop marker of run {id}";
            })
            .ToList();

    private static DateTimeOffset StartOf(RunId id) =>
        DateTimeOffset.ParseExact(id.Text[..16], "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static string Joined(string note, string problem) => problem.Length == 0 ? note : $"{note}; {problem}";
}

/// <summary>A run's unit as <c>systemctl show</c> tells it — a closed set.</summary>
public abstract record UnitState
{
    private UnitState()
    {
    }

    /// <summary>A queued job, or an active / activating / deactivating / reloading state.</summary>
    public sealed record Busy : UnitState;

    /// <summary>Neither: the unit ended, failed, or systemd does not know it (<paramref name="ActiveState"/> as printed).</summary>
    public sealed record Done(string ActiveState) : UnitState;

    /// <summary>The state could not be read.</summary>
    public sealed record Unread(string Why) : UnitState;
}
