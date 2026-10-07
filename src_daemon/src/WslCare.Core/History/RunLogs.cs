using WslCare.Core.Config;
using System.Text.Json;

using WslCare.Core.Actions;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Json;
using WslCare.Core.Records;

namespace WslCare.Core.History;

/// <summary>A detail file read only for its <c>kind</c> member (<c>act</c>, absent in a full run's, anything else unknown).</summary>
public sealed record DetailKindView(RecordedKind Kind);

/// <summary>A full run's detail read only for its timer pass.</summary>
public sealed record TimerPassView(TimerPass? TimerPass);

/// <summary>
/// <c>logs</c> and <c>runs</c> (plan §6, §7.4): READ-ONLY answers over <c>history.jsonl</c> and the run detail files — no lock,
/// nothing written, so an unprivileged process may ask (the state is 0644, plan §15b #3). The history line gives every run,
/// its trigger, dry run, outcome, metrics and per-action counts; the detail gives what exactly was removed. The page
/// computes nothing itself (plan §7.4): every sum, count and extreme is computed here.
/// </summary>
/// <remarks>A line that does not parse is counted, never guessed; a detail the history names that is gone is
/// <c>lost</c> and its cleanup still counts from the history line (with no objects listed).</remarks>
public static class RunLogs
{
    /// <summary>The headline metrics of plan §7.4's max / min block, as the history line keeps them.</summary>
    private static readonly (string Name, string Unit, Func<RunMetrics, double?> Read)[] MetricReaders =
    [
        ("memAvailablePercent", "%", m => m.MemAvailablePercent),
        ("memAvailableBytes", "bytes", m => m.MemAvailableBytes),
        ("pageCacheBytes", "bytes", m => m.PageCacheBytes),
        ("swapUsedBytes", "bytes", m => m.SwapUsedBytes),
        ("rootUsedPercent", "%", m => m.RootUsedPercent),
        ("dockerReclaimableBytes", "bytes", m => m.DockerReclaimableBytes),
        ("containerStarts24h", "starts", m => m.ContainerStarts24h),
    ];

    /// <summary>Every run whose start lies in <paramref name="period"/>, oldest first.</summary>
    public static RunsReport Runs(IHostPaths paths, IFileSystem files, LogPeriod period)
    {
        var history = RunHistory.Read(paths, files);
        var lines = InPeriod(history, period).Select(r => Line(r, StateOf(paths, files, r))).ToList();
        return new RunsReport(SchemaVersion.Current, PeriodReport.Of(period), lines.Count, lines, history.Unparseable, Problem(history));
    }

    /// <summary>How many run details one <c>logs</c> answer opens at most — the newest runs with a cleanup; the older ones are
    /// listed from their history lines with their objects not read (gate finding #10).</summary>
    public static int MaxDetailsRead => Tuning.Current.Int(ConfigKeys.Logs.MaxDetailsRead);

    /// <summary>The period's totals, run counts, extremes and every cleanup; <paramref name="action"/> narrows the totals, the
    /// cleanups and the run counts to one action (the metrics are the machine's, whatever acted). Everything but the objects
    /// comes from the history lines ALONE (gate finding #10); the objects each cleanup removed are read from the run details
    /// only when asked — <paramref name="detail"/>, or one <paramref name="action"/> — and from at most
    /// <see cref="MaxDetailsRead"/> of them.</summary>
    public static LogsReport Logs(IHostPaths paths, IFileSystem files, LogPeriod period, ActionId? action, bool detail = false)
    {
        var history = RunHistory.Read(paths, files);
        var runs = InPeriod(history, period).ToList();
        var lines = runs.Select(r => Line(Narrowed(r, action), StateOf(paths, files, r))).ToList();
        var withCleanup = runs.Select(r => Narrowed(r, action)).Where(IsCleanup).ToList();
        var read = DetailsToRead(withCleanup, detail, action);
        var opened = withCleanup.Select(r => (Run: r, Detail: read.Contains(r.RunId) ? Outcomes(paths, files, r) : NotOpened)).ToList();
        var cleanups = opened.SelectMany(o => Cleanups(o.Run, o.Detail)).ToList();
        var detailsRead = opened.Count(o => o.Detail.State is not (NotRead or Unreadable));
        var freed = lines.Where(l => l.FreedBytes > 0).ToList();
        return new LogsReport(
            SchemaVersion.Current,
            PeriodReport.Of(period),
            action?.Text,
            lines.Sum(l => l.FreedBytes),
            lines.SelectMany(l => l.Actions).Where(Acted).Sum(a => a.Count),
            Totals(lines),
            Counts(lines),
            Extreme(freed.MaxBy(l => l.FreedBytes)),
            Extreme(freed.MinBy(l => l.FreedBytes)),
            Metrics(runs),
            cleanups,
            history.Unparseable,
            Problem(history))
        {
            DetailsRead = detailsRead,
            DetailsNotRead = withCleanup.Count - detailsRead,
        };
    }

    /// <summary>The runs whose details are opened: the newest <see cref="MaxDetailsRead"/> with a cleanup, when asked — none otherwise.</summary>
    private static HashSet<RunId> DetailsToRead(IReadOnlyList<RunRecord> withCleanup, bool detail, ActionId? action) =>
        detail || action is not null ? [.. withCleanup.TakeLast(MaxDetailsRead).Select(r => r.RunId)] : [];

    private static RunExtreme? Extreme(RunLine? line) => line is null ? null : new RunExtreme(line.RunId, line.StartedAt, line.FreedBytes);

    private static IEnumerable<RunRecord> InPeriod(HistoryRead history, LogPeriod period) =>
        history.Records.Where(r => period.Contains(r.StartedAt)).OrderBy(r => r.StartedAt);

    private static string? Problem(HistoryRead history) => history.Problem.Length > 0 ? history.Problem : null;

    /// <summary>The run with only <paramref name="action"/>'s line kept, or as it is.</summary>
    private static RunRecord Narrowed(RunRecord record, ActionId? action) =>
        action is null ? record : record with { Actions = [.. record.Actions.Where(a => a.Id == action.Text)] };

    /// <summary>One history line as <c>runs</c> (and <c>runs show</c>) answer it.</summary>
    internal static RunLine Line(RunRecord r, DetailState detail)
    {
        var actions = r.Actions.Select(a => new RunActionLine(a.Id, a.Status, a.Count, a.FreedBytes, a.WouldFreeBytes, a.Failure)).ToList();
        var acted = actions.Where(Acted).ToList();
        var dry = actions.Where(a => a.Status == ActionStatus.DryRun).ToList();
        return new RunLine(
            r.RunId.Text,
            Camel(r.Trigger.ToString()),
            r.StartedAt,
            r.EndedAt,
            Camel(r.Outcome.ToString()),
            r.DryRun,
            r.Detail,
            Camel(detail.ToString()),
            acted.Sum(a => a.FreedBytes),
            dry.Count > 0 ? dry.Sum(a => a.WouldFreeBytes ?? 0) : null,
            acted.Any(a => a.Count > 0 || a.FreedBytes > 0),
            actions,
            r.Reason)
        {
            Metrics = r.Metrics,
            Kind = r.Kind.IsAbsent ? null : r.Kind.Text,
        };
    }

    private static IReadOnlyList<ActionTotal> Totals(IReadOnlyList<RunLine> lines) =>
        [.. lines.SelectMany(l => l.Actions)
            .GroupBy(a => a.Id)
            .OrderBy(g => ExecutionIndex(g.Key))
            .Select(g => new ActionTotal(
                g.Key,
                g.Count(a => a.Status == ActionStatus.Ran),
                g.Where(Acted).Sum(a => a.Count),
                g.Where(Acted).Sum(a => a.FreedBytes),
                g.Count(a => a.Status == ActionStatus.DryRun),
                g.Where(a => a.Status == ActionStatus.DryRun).Sum(a => a.WouldFreeBytes ?? 0),
                g.Count(a => a.Status == ActionStatus.Failed)))];

    /// <summary>An action that ACTED — ran, or ran and failed: a failed action's measured deletions are real (A4 removing 386
    /// of 387 volumes and failing on one removed 386), so its count and freed bytes count wherever a successful one's do.</summary>
    private static bool Acted(RunActionLine action) => action.Status is ActionStatus.Ran or ActionStatus.Failed or ActionStatus.Interrupted;

    private static bool Acted(ActionRecord action) => action.Status is ActionStatus.Ran or ActionStatus.Failed or ActionStatus.Interrupted;

    /// <summary>An action that acted AND removed (or freed) something: a cleanup.</summary>
    private static bool Removed(ActionRecord action) => Acted(action) && (action.Count > 0 || action.FreedBytes > 0);

    /// <summary>A run with a cleanup: at least one action acted AND removed (or freed) something — the ONE definition
    /// <c>logs</c> counts by and <c>status</c>'s <c>lastCleanup</c> picks by (plan §7.4, §15j M7).</summary>
    public static bool IsCleanup(RunRecord run) => run.Actions.Any(Removed);

    /// <summary>What a run's acting actions removed and measurably freed (a failed action's real deletions included).</summary>
    public static (int Count, long FreedBytes) Freed(RunRecord run) =>
        (run.Actions.Where(Acted).Sum(a => a.Count), run.Actions.Where(Acted).Sum(a => a.FreedBytes));

    private static RunCounts Counts(IReadOnlyList<RunLine> lines)
    {
        var dry = lines.Where(l => l.DryRun == true && l.Actions.Any(a => a.Status == ActionStatus.DryRun)).ToList();
        return new RunCounts(
            lines.Count,
            lines.Count(l => l.Cleanup),
            lines.Count(l => !l.Cleanup),
            dry.Count,
            dry.Sum(l => l.WouldFreeBytes ?? 0),
            lines.Count(l => l.Trigger == "timer"),
            lines.Count(l => l.Trigger == "manual"),
            lines.Count(l => l.Trigger == "cli"),
            lines.Count(l => l.Outcome == "failed"),
            lines.Count(l => l.Outcome == "interrupted"));
    }

    private static IReadOnlyList<MetricExtremes> Metrics(IReadOnlyList<RunRecord> runs) =>
        [.. MetricReaders.Select(reader =>
        {
            var points = runs.Where(r => r.Metrics is not null)
                .SelectMany(r => reader.Read(r.Metrics!) is { } value ? [new MetricPoint(value, r.StartedAt, r.RunId.Text)] : Array.Empty<MetricPoint>())
                .ToList();
            return new MetricExtremes(reader.Name, reader.Unit, points.Count, points.MaxBy(p => p.Value), points.MinBy(p => p.Value));
        })];

    /// <summary>A run's detail as <c>logs</c> read it: its state — <c>present</c> only when it was READ (it parsed and is of a kind
    /// this build reads), <c>unreadable</c> when it was opened and could not be (an I/O error, JSON that does not parse, a kind this
    /// build does not read), <c>lost</c>, <c>none</c>, or <see cref="NotRead"/> when it was not opened — and its outcomes. An
    /// unreadable detail is never a detail read with nothing in it, and counts as NOT read (PR #44 gate round).</summary>
    private sealed record DetailOutcomes(string State, IReadOnlyList<ActionOutcome> Outcomes);

    private const string Present = "present";

    private static readonly DetailOutcomes NotOpened = new(NotRead, []);

    private const string Unreadable = "unreadable";

    private static readonly DetailOutcomes UnreadableDetail = new(Unreadable, []);

    /// <summary>Every action that ACTED (ran, or failed) and removed (or freed) something in <paramref name="run"/>, its failure
    /// beside its figures — and its objects from the run's detail when that was read.</summary>
    private static IEnumerable<CleanupDetail> Cleanups(RunRecord run, DetailOutcomes detail) =>
        run.Actions.Where(Removed).Select(a => Cleanup(run, a, detail.State, detail.Outcomes.FirstOrDefault(o => o.Id == a.Id)));

    /// <summary>One cleanup: its history line's figures and failure, and — when its detail was read and holds its outcome — the
    /// objects it removed and kept (a detail read without its outcome is <c>unreadable</c>).</summary>
    private static CleanupDetail Cleanup(RunRecord run, ActionRecord action, string state, ActionOutcome? outcome) => outcome is { Run: { } done }
        ? Head(run, action, state) with { FreedBasis = done.FreedBasis, Removed = OrEmpty(done.Removed), NotRemoved = OrEmpty(done.NotRemoved), Notes = OrEmpty(done.Notes) }
        : Head(run, action, state == Present ? "unreadable" : state);

    private static CleanupDetail Head(RunRecord run, ActionRecord action, string state) =>
        new(run.RunId.Text, run.StartedAt, Camel(run.Trigger.ToString()), action.Id, action.Status ?? string.Empty, action.Count, action.FreedBytes, null, state, [], [], []) { Failure = action.Failure };

    /// <summary>A detail written before a member existed reads it as null under the source generator, whatever the declaration
    /// says (C# doctrine §4a): NotRemoved and Notes arrived with E3.S2, so they are normalised here, where they are read.</summary>
    internal static IReadOnlyList<T> OrEmpty<T>(IReadOnlyList<T>? list) => list ?? [];

    /// <summary>The action outcomes the run's detail holds — an <c>act</c>'s, or a full run's timer pass — and the detail's state.</summary>
    private static DetailOutcomes Outcomes(IHostPaths paths, IFileSystem files, RunRecord run)
    {
        if (run.DetailPath.Length == 0)
        {
            return new("none", []);
        }

        return files.ReadFile(RunDetailStore.Absolute(paths, run.DetailPath), RootFileCaps.History) switch
        {
            FileReadResult.Content content => Parse(content.Bytes),
            FileReadResult.Missing => new("lost", []),
            _ => UnreadableDetail,
        };
    }

    private static DetailOutcomes Parse(byte[] json)
    {
        try
        {
            return KindOf(json) switch
            {
                DetailKind.Act => new(Present, ActOutcomes(json)),
                DetailKind.FullRun => new(Present, TimerPassOutcomes(json)),
                _ => UnreadableDetail,
            };
        }
        catch (JsonException)
        {
            return UnreadableDetail;
        }
    }

    /// <summary>A detail's <c>kind</c> member as written; unknown for a detail that is no JSON object at all.</summary>
    internal static RecordedKind DetailMember(byte[] json) =>
        JsonSerializer.Deserialize(json, WslCareJsonContext.Default.DetailKindView) is { } view ? view.Kind : RecordedKind.Unknown("null");

    /// <summary>What a detail is — read by explicit kind, never guessed: a detail of a kind this build does not know yields no
    /// outcomes (its objects are not read from a timer pass it may not have; PR #16 retro round O1).</summary>
    internal static DetailKind KindOf(byte[] json) => RunKinds.OfDetail(DetailMember(json));

    private static IReadOnlyList<ActionOutcome> ActOutcomes(byte[] json) => JsonSerializer.Deserialize(json, WslCareJsonContext.Default.ActRunDetail)?.Actions ?? [];

    private static IReadOnlyList<ActionOutcome> TimerPassOutcomes(byte[] json) =>
        JsonSerializer.Deserialize(json, WslCareJsonContext.Default.TimerPassView)?.TimerPass?.Actions ?? [];

    /// <summary>The detail state of a cleanup whose objects were not asked for (or fell past <see cref="MaxDetailsRead"/>).</summary>
    public const string NotRead = "notRead";

    private static DetailState StateOf(IHostPaths paths, IFileSystem files, RunRecord record) =>
        record.DetailPath.Length == 0
            ? DetailState.None
            : files.FileExists(RunDetailStore.Absolute(paths, record.DetailPath)) ? DetailState.Present : DetailState.Lost;

    private static int ExecutionIndex(string id) =>
        ActionId.ExecutionOrder.Select((a, i) => (a, i)).FirstOrDefault(x => x.a.Text == id) is { a: not null } found ? found.i : int.MaxValue;

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];
}
