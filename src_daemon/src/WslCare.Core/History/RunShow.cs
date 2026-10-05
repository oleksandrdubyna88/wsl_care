using WslCare.Core.Actions.Engine;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Records;
using WslCare.Core.Status;

namespace WslCare.Core.History;

/// <summary>The closed set of <see cref="RunShowReport.State"/> names (plan §15j M3).</summary>
public static class RunShowState
{
    /// <summary>A request names the run (E6.S1's <c>--detach</c>) and it has not started.</summary>
    public const string Queued = "queued";

    /// <summary><c>running.json</c> names the run and its process is alive (live or wedged — the <c>running</c> block says which).</summary>
    public const string Running = "running";

    /// <summary>The history holds its line: it completed, failed or ran observe-only (the line's <c>outcome</c> says which).</summary>
    public const string Done = "done";

    /// <summary>The history holds its line with the outcome <c>refused</c> (E6.S1: a detached run that met the lock).</summary>
    public const string Refused = "refused";

    /// <summary>The history holds its line with the outcome <c>interrupted</c> — or <c>running.json</c> names it and its process
    /// is gone (dead, not swept yet): it will never finish, so it is never shown as running.</summary>
    public const string Interrupted = "interrupted";

    /// <summary>Nothing names the run: no history line, no running state, no request.</summary>
    public const string Unknown = "unknown";

    public static IReadOnlyList<string> All { get; } = [Queued, Running, Done, Refused, Interrupted, Unknown];
}

/// <summary>One run's detail as <c>runs show</c> answers it: every action with its status, its live preview and — when it
/// ran — every object it removed and did not remove, the commands it ran and their exits (§15j M3).</summary>
/// <param name="Kind"><c>act</c>, or <c>collect</c> (a full run: its timer pass's actions, none for a run from a terminal).</param>
public sealed record RunShowDetail(string Kind, bool? DryRun, string? DryRunReason, TargetUserReport? TargetUser, IReadOnlyList<ActionOutcome> Actions, IReadOnlyList<string> Notes);

/// <summary>A queued run's request as <c>runs show</c> answers it (the names it carries counted, not repeated).</summary>
public sealed record RunShowRequest(string Kind, IReadOnlyList<string> Actions, string Trigger, DateTimeOffset CreatedAt, int ShownCount);

/// <summary>The answer of <c>runs show &lt;runId&gt; --json</c> — its own <c>schemaVersion</c> (1). Every member but the four
/// positional ones is absent where the state does not have it.</summary>
/// <param name="State">One of <see cref="RunShowState"/>.</param>
public sealed record RunShowReport(int SchemaVersion, string RunId, string State, string? Reason)
{
    /// <summary>The run's history line, as <c>runs</c> answers it (done / refused / interrupted).</summary>
    public RunLine? Run { get; init; }

    /// <summary><c>present</c>, <c>lost</c> (the line names a detail that is gone), <c>none</c> or <c>unreadable</c>.</summary>
    public string? DetailState { get; init; }

    /// <summary>The run's full detail, when its file is present and parses.</summary>
    public RunShowDetail? Detail { get; init; }

    /// <summary>The <c>running</c> block of a run that holds <c>running.json</c> (running, or a dead one: interrupted).</summary>
    public RunningReport? Running { get; init; }

    /// <summary>The request of a queued run.</summary>
    public RunShowRequest? Request { get; init; }

    /// <summary>Why the history could not be read — the answer is then from the running state and the requests alone.</summary>
    public string? Problem { get; init; }
}

/// <summary>
/// <c>runs show &lt;runId&gt;</c> (plan §6, §15j M3): READ-ONLY — no lock, nothing written, never a sweep, so an unprivileged
/// process may ask (the state is 0644). The history decides first (a line is terminal), then <c>running.json</c>, then the
/// request folder; nothing naming the run is <c>unknown</c>.
/// </summary>
public static class RunShow
{
    public const string NothingNamesIt =
        "no history line, running state or request names this run - it never existed here, or it is older than the 90-day retention";

    private static readonly HistoryRead NoHistory = new([], 0, string.Empty);

    /// <summary>
    /// The three places are READ in the order a run moves through them — its request, then <c>running.json</c>, then its
    /// history line (E6.S0 review D4) — and the answer is the MOST advanced one found: a run that moves on between two reads
    /// is then caught by the later read, never answered "queued" from a request already gone, nor "unknown".
    /// </summary>
    public static RunShowReport Read(IHostPaths paths, IFileSystem files, IProcessTable processes, DateTimeOffset now, RunningReadRetry retry, RunId runId)
    {
        var request = RunRequests.Find(paths, files, runId);
        var holder = RunningReports.OfHolder(RunningState.Read(paths, files, processes, now, retry), NoHistory);
        var history = RunHistory.Read(paths, files);
        var report = history.Records.LastOrDefault(r => r.RunId == runId) is { } line
            ? Recorded(paths, files, line)
            : Holding(holder, runId) ?? Requested(request, runId, processes.Boot());
        return report with { Problem = history.Problem.Length > 0 ? history.Problem : null };
    }

    /// <summary>The history holds the run: done, refused or interrupted — with its line and its detail.</summary>
    private static RunShowReport Recorded(IHostPaths paths, IFileSystem files, RunRecord line)
    {
        var (state, detail) = Detail(paths, files, line);
        return new RunShowReport(SchemaVersion.Current, line.RunId.Text, StateOf(line.Outcome), line.Reason)
        {
            Run = RunLogs.Line(line, DetailStateOf(state)),
            DetailState = state,
            Detail = detail,
        };
    }

    private static string StateOf(RunOutcome outcome) => outcome switch
    {
        RunOutcome.Interrupted => RunShowState.Interrupted,
        RunOutcome.Refused => RunShowState.Refused,
        _ => RunShowState.Done,
    };

    private static DetailState DetailStateOf(string state) => state switch
    {
        "present" or "unreadable" => DetailState.Present,
        "lost" => DetailState.Lost,
        _ => DetailState.None,
    };

    /// <summary>The run holds <c>running.json</c>: running (live, wedged, or a pid that cannot be inspected — review S2) — or
    /// interrupted when its process is gone (it will never finish).</summary>
    private static RunShowReport? Holding(RunningReport? running, RunId runId) =>
        running is not null && running.RunId == runId.Text
            ? new RunShowReport(SchemaVersion.Current, runId.Text, running.State == RunningStateName.Dead ? RunShowState.Interrupted : RunShowState.Running, running.Reason) { Running = running }
            : null;

    private static RunShowReport Requested(RunRequestRead? request, RunId runId, BootClock boot) => request switch
    {
        RunRequestRead.Parsed parsed when RunningReports.OfAnEarlierBoot(parsed.File, boot) =>
            new RunShowReport(SchemaVersion.Current, runId.Text, RunShowState.Interrupted, RunningReports.EarlierBootReason),
        RunRequestRead.Parsed parsed => Queued(parsed.File),
        RunRequestRead.Bad bad => new RunShowReport(SchemaVersion.Current, runId.Text, RunShowState.Unknown, $"its request {bad.Path} {bad.Why}"),
        _ => new RunShowReport(SchemaVersion.Current, runId.Text, RunShowState.Unknown, NothingNamesIt),
    };

    private static RunShowReport Queued(RunRequestFile request) =>
        new(SchemaVersion.Current, request.RunId.Text, RunShowState.Queued, $"accepted at {request.CreatedAt.UtcDateTime:yyyy-MM-dd HH:mm:ss}Z and not started yet")
        {
            Request = new RunShowRequest(request.Kind, request.Actions, RunningReports.TriggerName(request.Trigger), request.CreatedAt, request.Shown.Count),
        };

    /// <summary>The detail file the line names: its state, and its content when it is present and parses.</summary>
    private static (string State, RunShowDetail? Detail) Detail(IHostPaths paths, IFileSystem files, RunRecord line)
    {
        if (line.DetailPath.Length == 0)
        {
            return ("none", null);
        }

        return files.ReadFile(RunDetailStore.Absolute(paths, line.DetailPath)) switch
        {
            FileReadResult.Content content => Parsed(content.Bytes),
            FileReadResult.Missing => ("lost", null),
            _ => ("unreadable", null),
        };
    }

    private static (string State, RunShowDetail? Detail) Parsed(byte[] json)
    {
        try
        {
            var detail = RunLogs.IsAct(json) ? FromAct(json) : FromFullRun(json);
            return detail is null ? ("unreadable", null) : ("present", detail);
        }
        catch (System.Text.Json.JsonException)
        {
            return ("unreadable", null);
        }
    }

    private static RunShowDetail? FromAct(byte[] json) =>
        System.Text.Json.JsonSerializer.Deserialize(json, Json.WslCareJsonContext.Default.ActRunDetail) is { } act
            ? new RunShowDetail("act", act.DryRun, act.DryRunReason, act.TargetUser, Normalised(act.Actions), RunLogs.OrEmpty(act.Notes))
            : null;

    /// <summary>A full run's detail: its timer pass (none for a full run from a terminal or the panel — no action ran).</summary>
    private static RunShowDetail? FromFullRun(byte[] json) =>
        System.Text.Json.JsonSerializer.Deserialize(json, Json.WslCareJsonContext.Default.TimerPassView) is { } view
            ? view.TimerPass is { } pass
                ? new RunShowDetail("collect", pass.DryRun, pass.DryRunReason, pass.TargetUser, Normalised(pass.Actions), RunLogs.OrEmpty(pass.Notes))
                : new RunShowDetail("collect", null, null, null, [], [])
            : null;

    /// <summary>A detail written before a member existed reads it as null under the source generator (C# doctrine §4a):
    /// normalised here, where it is read.</summary>
    private static IReadOnlyList<ActionOutcome> Normalised(IReadOnlyList<ActionOutcome>? actions) =>
        [.. RunLogs.OrEmpty(actions).Select(o => o.Run is { } run
            ? o with { Run = run with { NotRemoved = RunLogs.OrEmpty(run.NotRemoved), Notes = RunLogs.OrEmpty(run.Notes), Removed = RunLogs.OrEmpty(run.Removed), Commands = RunLogs.OrEmpty(run.Commands) } }
            : o)];
}
