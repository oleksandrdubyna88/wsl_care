using System.Globalization;

using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Processes;
using WslCare.Core.Records;
using WslCare.Core.Systemd;

namespace WslCare.Core.Actions.Engine;

/// <summary>
/// The sweep of the request folder (E6.S1, plan §15j B2, §15k #2 / #15): run at the start of every ROOT run — <c>collect</c>
/// (timer or detached) and <c>act --request</c> — never by the unprivileged <c>status</c>. OWNERSHIP-checked, history first:
/// <list type="bullet">
/// <item>a request whose run already has a history line only loses its file (the run recorded itself; no second line);</item>
/// <item>a request younger than <see cref="StaleAfter"/> (15 minutes) is left alone;</item>
/// <item>an older one is still PENDING while its unit has a queued job or is active / activating / deactivating / reloading
/// (<c>systemctl show --property=ActiveState --property=Job</c> — a queued start has no active state yet);</item>
/// <item>otherwise the run never recorded itself: it gets ONE <c>interrupted</c> history line naming why, then its request goes.</item>
/// </list>
/// A unit whose state cannot be read keeps its request (it may still run). The run's OWN request is never swept. Stop
/// markers whose run has a line, or older than a day, go too.
/// </summary>
public static class RequestSweep
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);

    public static async Task<IReadOnlyList<string>> ApplyAsync(IHostPaths paths, IFileSystem files, ICommandRunner commands, DateTimeOffset now, RunId? own, CancellationToken cancellationToken)
    {
        var history = RunHistory.Read(paths, files);
        var recorded = history.Records.Select(r => r.RunId).ToHashSet();
        var notes = new List<string>();
        foreach (var request in RunRequests.List(paths, files).OfType<RunRequestRead.Parsed>().Select(p => p.File).Where(r => r.RunId != own))
        {
            notes.Add(await OneAsync(paths, files, commands, now, request, recorded.Contains(request.RunId), cancellationToken).ConfigureAwait(false));
        }

        notes.AddRange(OldStopMarkers(paths, files, now, recorded));
        return [.. notes.Where(n => n.Length > 0)];
    }

    private static async Task<string> OneAsync(IHostPaths paths, IFileSystem files, ICommandRunner commands, DateTimeOffset now, RunRequestFile request, bool recorded, CancellationToken cancellationToken)
    {
        if (recorded)
        {
            return Joined($"removed the request of run {request.RunId}: the run recorded itself", RunRequests.Remove(paths, files, request.RunId));
        }

        if (now - request.CreatedAt < StaleAfter)
        {
            return string.Empty;
        }

        return await UnitStateAsync(commands, request.RunId, cancellationToken).ConfigureAwait(false) switch
        {
            UnitState.Unread unread => $"kept the request of run {request.RunId}: its unit's state could not be read ({unread.Why})",
            UnitState.Busy => string.Empty,
            UnitState.Done done => Interrupted(paths, files, now, request, done.ActiveState),
            _ => throw new System.Diagnostics.UnreachableException("UnitState is a closed set"),
        };
    }

    /// <summary>The run never recorded itself and its unit is not busy: ONE interrupted line, then the request goes.</summary>
    private static string Interrupted(IHostPaths paths, IFileSystem files, DateTimeOffset now, RunRequestFile request, string activeState)
    {
        var minutes = (now - request.CreatedAt).TotalMinutes;
        var line = new RunRecord(Core.SchemaVersion.Current, request.RunId, request.Trigger, request.CreatedAt, now, RunOutcome.Interrupted, [.. request.Actions.Select(a => new ActionRecord(a, 0, 0) { Status = ActionStatus.Interrupted })])
        {
            Reason = string.Create(CultureInfo.InvariantCulture, $"swept: the detached run never recorded itself - its unit {Processes.Policy.SlotKind.ActUnit.Of(request.RunId)} is {(activeState.Length > 0 ? activeState : "unknown to systemd")} with no queued job, and its request is {minutes:0} min old"),
        };
        new RunRecordWriter(paths, files).Append(line);
        return Joined($"swept the request of run {request.RunId}: its unit is not running (recorded as interrupted)", RunRequests.Remove(paths, files, request.RunId));
    }

    private abstract record UnitState
    {
        private UnitState()
        {
        }

        public sealed record Busy : UnitState;

        public sealed record Done(string ActiveState) : UnitState;

        public sealed record Unread(string Why) : UnitState;
    }

    private static async Task<UnitState> UnitStateAsync(ICommandRunner commands, RunId runId, CancellationToken cancellationToken) =>
        await commands.RunAsync(UnitCommands.Show(runId).ToRequest(), cancellationToken).ConfigureAwait(false) switch
        {
            CommandOutcome.Exited { ExitCode: 0 } exited when UnitCommands.Busy(exited.Stdout.Text) => new UnitState.Busy(),
            CommandOutcome.Exited { ExitCode: 0 } exited => new UnitState.Done(UnitCommands.ActiveState(exited.Stdout.Text)),
            CommandOutcome.Exited other => new UnitState.Unread($"systemctl show exited {other.ExitCode}: {other.Stderr.Text.Trim()}"),
            CommandOutcome.TimedOut => new UnitState.Unread("systemctl show timed out"),
            CommandOutcome.FailedToStart failed => new UnitState.Unread(failed.Reason),
            CommandOutcome.Refused refused => new UnitState.Unread(refused.Reason),
            _ => throw new System.Diagnostics.UnreachableException("CommandOutcome is a closed set"),
        };

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
