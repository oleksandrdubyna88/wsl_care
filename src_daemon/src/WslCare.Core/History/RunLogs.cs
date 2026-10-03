using System.Text.Json;

using WslCare.Core.Actions;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Json;
using WslCare.Core.Records;

namespace WslCare.Core.History;

/// <summary>A detail file read only for its kind (<c>act</c>, or a full run's, which carries none).</summary>
public sealed record DetailKindView(string? Kind);

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

    /// <summary>The period's totals, run counts, extremes and every cleanup in detail; <paramref name="action"/> narrows the
    /// totals, the cleanups and the run counts to one action (the metrics are the machine's, whatever acted).</summary>
    public static LogsReport Logs(IHostPaths paths, IFileSystem files, LogPeriod period, ActionId? action)
    {
        var history = RunHistory.Read(paths, files);
        var runs = InPeriod(history, period).ToList();
        var lines = runs.Select(r => Line(Narrowed(r, action), StateOf(paths, files, r))).ToList();
        var cleanups = runs.SelectMany(r => Cleanups(paths, files, Narrowed(r, action))).ToList();
        var freed = lines.Where(l => l.FreedBytes > 0).ToList();
        return new LogsReport(
            SchemaVersion.Current,
            PeriodReport.Of(period),
            action?.Text,
            lines.Sum(l => l.FreedBytes),
            lines.SelectMany(l => l.Actions).Where(a => a.Status == ActionStatus.Ran).Sum(a => a.Count),
            Totals(lines),
            Counts(lines),
            freed.MaxBy(l => l.FreedBytes) is { } most ? new RunExtreme(most.RunId, most.StartedAt, most.FreedBytes) : null,
            freed.MinBy(l => l.FreedBytes) is { } least ? new RunExtreme(least.RunId, least.StartedAt, least.FreedBytes) : null,
            Metrics(runs),
            cleanups,
            history.Unparseable,
            Problem(history));
    }

    private static IEnumerable<RunRecord> InPeriod(HistoryRead history, LogPeriod period) =>
        history.Records.Where(r => period.Contains(r.StartedAt)).OrderBy(r => r.StartedAt);

    private static string? Problem(HistoryRead history) => history.Problem.Length > 0 ? history.Problem : null;

    /// <summary>The run with only <paramref name="action"/>'s line kept, or as it is.</summary>
    private static RunRecord Narrowed(RunRecord record, ActionId? action) =>
        action is null ? record : record with { Actions = [.. record.Actions.Where(a => a.Id == action.Text)] };

    private static RunLine Line(RunRecord r, DetailState detail)
    {
        var actions = r.Actions.Select(a => new RunActionLine(a.Id, a.Status, a.Count, a.FreedBytes, a.WouldFreeBytes)).ToList();
        var ran = actions.Where(a => a.Status == ActionStatus.Ran).ToList();
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
            ran.Sum(a => a.FreedBytes),
            dry.Count > 0 ? dry.Sum(a => a.WouldFreeBytes ?? 0) : null,
            ran.Any(a => a.Count > 0 || a.FreedBytes > 0),
            actions,
            r.Reason);
    }

    private static IReadOnlyList<ActionTotal> Totals(IReadOnlyList<RunLine> lines) =>
        [.. lines.SelectMany(l => l.Actions)
            .GroupBy(a => a.Id)
            .OrderBy(g => ExecutionIndex(g.Key))
            .Select(g => new ActionTotal(
                g.Key,
                g.Count(a => a.Status == ActionStatus.Ran),
                g.Where(a => a.Status == ActionStatus.Ran).Sum(a => a.Count),
                g.Where(a => a.Status == ActionStatus.Ran).Sum(a => a.FreedBytes),
                g.Count(a => a.Status == ActionStatus.DryRun),
                g.Where(a => a.Status == ActionStatus.DryRun).Sum(a => a.WouldFreeBytes ?? 0)))];

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

    /// <summary>Every action that RAN and removed (or freed) something in <paramref name="run"/>, with its objects from the detail.</summary>
    private static IEnumerable<CleanupDetail> Cleanups(IHostPaths paths, IFileSystem files, RunRecord run)
    {
        var ran = run.Actions.Where(a => a.Status == ActionStatus.Ran && (a.Count > 0 || a.FreedBytes > 0)).ToList();
        if (ran.Count == 0)
        {
            return [];
        }

        var (state, outcomes) = Outcomes(paths, files, run);
        // A detail written before a member existed reads it as null under the source generator, whatever the declaration
        // says (C# doctrine §4a): NotRemoved and Notes arrived with E3.S2, so they are normalised here, where they are read.
        return ran.Select(a => outcomes.FirstOrDefault(o => o.Id == a.Id) is { Run: { } done }
            ? new CleanupDetail(run.RunId.Text, run.StartedAt, Camel(run.Trigger.ToString()), a.Id, ActionStatus.Ran, a.Count, a.FreedBytes, done.FreedBasis, state, done.Removed ?? [], done.NotRemoved ?? [], done.Notes ?? [])
            : new CleanupDetail(run.RunId.Text, run.StartedAt, Camel(run.Trigger.ToString()), a.Id, ActionStatus.Ran, a.Count, a.FreedBytes, null, state == "present" ? "unreadable" : state, [], [], []));
    }

    /// <summary>The action outcomes the run's detail holds — an <c>act</c>'s, or a full run's timer pass — and the detail's state.</summary>
    private static (string State, IReadOnlyList<ActionOutcome> Outcomes) Outcomes(IHostPaths paths, IFileSystem files, RunRecord run)
    {
        if (run.DetailPath.Length == 0)
        {
            return ("none", []);
        }

        return files.ReadFile(RunDetailStore.Absolute(paths, run.DetailPath)) switch
        {
            FileReadResult.Content content => ("present", Parse(content.Bytes)),
            FileReadResult.Missing => ("lost", []),
            _ => ("unreadable", []),
        };
    }

    private static IReadOnlyList<ActionOutcome> Parse(byte[] json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, WslCareJsonContext.Default.DetailKindView)?.Kind == "act"
                ? JsonSerializer.Deserialize(json, WslCareJsonContext.Default.ActRunDetail)?.Actions ?? []
                : JsonSerializer.Deserialize(json, WslCareJsonContext.Default.TimerPassView)?.TimerPass?.Actions ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static DetailState StateOf(IHostPaths paths, IFileSystem files, RunRecord record) =>
        record.DetailPath.Length == 0
            ? DetailState.None
            : files.FileExists(RunDetailStore.Absolute(paths, record.DetailPath)) ? DetailState.Present : DetailState.Lost;

    private static int ExecutionIndex(string id) =>
        ActionId.ExecutionOrder.Select((a, i) => (a, i)).FirstOrDefault(x => x.a.Text == id) is { a: not null } found ? found.i : int.MaxValue;

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];
}
