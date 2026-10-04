using System.Globalization;
using System.Text;
using System.Text.Json;

using WslCare.Core.Json;
using WslCare.Core.History;

namespace WslCare.Cli.Commands;

/// <summary>
/// <c>logs [--period …] [--action &lt;A#&gt;] [--json]</c> and <c>runs [--period …] [--json]</c> (plan §6, §7.4): READ-ONLY
/// answers over the run records — no lock, nothing written, any user may ask. <c>logs</c>: what was freed in total and per
/// action, runs with and without a cleanup (dry runs apart, with what they would have freed), the run that freed the most
/// and the least, each metric's max and min with its time, and every cleanup with every object it removed. <c>runs</c>:
/// every run of the period. The period is <c>today</c> (the default), <c>yesterday</c>, a UTC date or a UTC range — or,
/// since E6.S0, <c>--from</c> / <c>--to</c>, two RFC 3339 instants (plan §15j M7). <c>runs show &lt;runId&gt;</c> answers one
/// run: its state, its line and its full detail (§15j M3).
/// </summary>
/// <remarks>Exit codes: 0 answered (an empty period, an unknown run too); 2 a period, an instant or a run id that is not
/// one of the shapes; 4 the history could not be read.</remarks>
internal static class LogsCommand
{
    private const double BytesPerGigabyte = 1e9;

    public static int Logs(Request.Logs request, CliHost host, TextWriter stdout, TextWriter stderr)
    {
        if (Period(request.Period, request.From, request.To, host, stderr) is not { } period)
        {
            return (int)ExitCode.Usage;
        }

        var report = RunLogs.Logs(host.Paths, host.Files, period, request.Action, request.Detail);
        Output.Answer(stdout, request.Json ? JsonSerializer.Serialize(report, WslCareJsonContext.Default.LogsReport) : LogsText(report));
        return Exit(report.Problem, stderr);
    }

    public static int Runs(Request.Runs request, CliHost host, TextWriter stdout, TextWriter stderr)
    {
        if (Period(request.Period, request.From, request.To, host, stderr) is not { } period)
        {
            return (int)ExitCode.Usage;
        }

        var report = RunLogs.Runs(host.Paths, host.Files, period);
        Output.Answer(stdout, request.Json ? JsonSerializer.Serialize(report, WslCareJsonContext.Default.RunsReport) : RunsText(report));
        return Exit(report.Problem, stderr);
    }

    /// <summary><c>runs show &lt;runId&gt;</c> (plan §15j M3): read-only, exit 0 whatever the state (<c>unknown</c> included) —
    /// 4 only when the history exists and cannot be read (the answer then comes from the running state and requests alone).</summary>
    public static int Show(Request.RunsShow request, CliHost host, TextWriter stdout, TextWriter stderr)
    {
        var runId = Core.Records.RunId.TryParse(request.RunId) ?? throw new System.Diagnostics.UnreachableException("the parser admits a well-formed run id only");
        var report = RunShow.Read(host.Paths, host.Files, host.Processes, host.Clock.GetUtcNow(), Core.Actions.Engine.RunningReadRetry.Default, runId);
        Output.Answer(stdout, request.Json ? JsonSerializer.Serialize(report, WslCareJsonContext.Default.RunShowReport) : ShowText(report));
        return Exit(report.Problem, stderr);
    }

    /// <summary>The period of UTC days, or — with <c>--from</c> / <c>--to</c> (the parser admits both or neither) — the instant
    /// range; a refusal is one message and the usage code.</summary>
    private static LogPeriod? Period(string text, string from, string to, CliHost host, TextWriter stderr) =>
        Parsed(from.Length > 0 ? LogPeriod.ParseInstants(from, to) : LogPeriod.Parse(text, host.Clock.GetUtcNow()), stderr);

    private static LogPeriod? Parsed(PeriodParse parse, TextWriter stderr)
    {
        switch (parse)
        {
            case PeriodParse.Parsed parsed:
                return parsed.Period;
            case PeriodParse.Refused refused:
                Output.Note(stderr, CommandLine.Printable(refused.Reason));
                return null;
            default:
                throw new System.Diagnostics.UnreachableException("PeriodParse is a closed set");
        }
    }

    private static int Exit(string? problem, TextWriter stderr)
    {
        if (problem is null)
        {
            return (int)ExitCode.Ok;
        }

        Output.Note(stderr, problem);
        return (int)ExitCode.RecordsUnreadable;
    }

    private static string LogsText(LogsReport r)
    {
        var text = new StringBuilder()
            .AppendLine(Invariant($"wsl-care logs {r.Period.Label} ({r.Period.From}..{r.Period.To} UTC){OnlyAction(r)}: freed {Gb(r.FreedBytes)}, {r.ObjectsRemoved} objects"))
            .AppendLine(Invariant($"runs: {r.Runs.Total} ({r.Runs.WithCleanup} with a cleanup, {r.Runs.WithoutCleanup} without; {r.Runs.DryRun} dry, would have freed {Gb(r.Runs.WouldFreeBytes)}; timer {r.Runs.Timer}, button {r.Runs.Manual}, terminal {r.Runs.Cli})"));
        PerAction(text, r);
        Extremes(text, r);
        Cleanups(text, r);
        DetailsNotRead(text, r);
        return text.ToString().TrimEnd();
    }

    private static string OnlyAction(LogsReport r) => r.Action is null ? string.Empty : $", {r.Action} only";

    private static void PerAction(StringBuilder text, LogsReport r)
    {
        foreach (var total in r.PerAction.Where(t => t.Runs > 0 || t.DryRuns > 0 || t.Failed > 0))
        {
            text.AppendLine(Invariant($"  {total.Id,-17} ran {total.Runs}x{FailedTimes(total)}, {total.Count} objects, freed {Gb(total.FreedBytes)}; dry {total.DryRuns}x, would free {Gb(total.WouldFreeBytes)}"));
        }
    }

    private static string FailedTimes(ActionTotal total) => total.Failed > 0 ? Invariant($" (failed {total.Failed}x)") : string.Empty;

    private static void Extremes(StringBuilder text, LogsReport r)
    {
        if (r.MostFreed is { } most && r.LeastFreed is { } least)
        {
            text.AppendLine(Invariant($"most freed: {Gb(most.FreedBytes)} (run {most.RunId}); least (non-zero): {Gb(least.FreedBytes)} (run {least.RunId})"));
        }
    }

    private static void Cleanups(StringBuilder text, LogsReport r)
    {
        foreach (var cleanup in r.Cleanups)
        {
            text.AppendLine(Invariant($"  {cleanup.StartedAt.UtcDateTime:yyyy-MM-dd HH:mm}Z {cleanup.Action}: {cleanup.Count} removed, {Gb(cleanup.FreedBytes)} ({cleanup.Trigger}; detail {cleanup.DetailState}){FailedBeside(cleanup)}"));
            foreach (var item in cleanup.Removed.Take(20))
            {
                text.AppendLine(Invariant($"      {item.Kind} {CommandLine.Printable(item.Name)}{SizeOf(item)}"));
            }
        }
    }

    private static string FailedBeside(CleanupDetail cleanup) =>
        cleanup.Failure is { Length: > 0 } failure ? $" - FAILED: {CommandLine.Printable(failure)}" : string.Empty;

    private static string SizeOf(Core.Actions.ActionItem item) => item.Bytes is { } b ? $" {Gb(b)}" : string.Empty;

    private static void DetailsNotRead(StringBuilder text, LogsReport r)
    {
        if (r.DetailsNotRead > 0)
        {
            text.AppendLine(Invariant($"objects not read for {r.DetailsNotRead} run(s): {(r.DetailsRead == 0 ? "--detail or one --action lists them" : $"only the newest {r.DetailsRead} run details are read")}"));
        }
    }

    private static string RunsText(RunsReport r)
    {
        var text = new StringBuilder().AppendLine(Invariant($"wsl-care runs {r.Period.Label} ({r.Period.From}..{r.Period.To} UTC): {r.Count}"));
        foreach (var run in r.Runs)
        {
            text.AppendLine(Invariant($"  {run.StartedAt.UtcDateTime:yyyy-MM-dd HH:mm:ss}Z {run.RunId} {run.Trigger,-6} {run.Outcome}{(run.DryRun == true ? " dry" : string.Empty)} freed {Gb(run.FreedBytes)}; {string.Join(", ", run.Actions.Where(a => a.Status is not ("skipped" or null)).Select(a => $"{a.Id}:{a.Status}"))}"));
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>The human form of <c>runs show</c>: the state and why, then — when the detail was read — one line per action
    /// with what it removed and every command with its exit.</summary>
    private static string ShowText(RunShowReport r)
    {
        var text = new StringBuilder().AppendLine($"wsl-care runs show {r.RunId}: {r.State}{(r.Reason is { Length: > 0 } reason ? $" - {CommandLine.Printable(reason)}" : string.Empty)}");
        foreach (var action in r.Detail?.Actions ?? [])
        {
            text.AppendLine(Invariant($"  {action.Id,-17} {action.Status,-9} {(action.Run is { } run ? $"{run.Count} removed, {run.NotRemoved.Count} not removed, freed {Gb(run.FreedBytes ?? 0)}" : CommandLine.Printable(action.Reason))}"));
            foreach (var command in action.Run?.Commands ?? [])
            {
                text.AppendLine(Invariant($"      {CommandLine.Printable(command.Display)} -> {command.Outcome}{(command.Exit is { } exit ? $" {exit}" : string.Empty)}"));
            }
        }

        return text.ToString().TrimEnd();
    }

    private static string Gb(long bytes) => Invariant($"{bytes / BytesPerGigabyte:0.00} GB");

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
