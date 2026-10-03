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
/// every run of the period. The period is <c>today</c> (the default), <c>yesterday</c>, a UTC date or a UTC range.
/// </summary>
/// <remarks>Exit codes: 0 answered (an empty period too); 2 a period that is not one of the shapes; 4 the history could
/// not be read.</remarks>
internal static class LogsCommand
{
    private const double BytesPerGigabyte = 1e9;

    public static int Logs(Request.Logs request, CliHost host, TextWriter stdout, TextWriter stderr)
    {
        if (Period(request.Period, host, stderr) is not { } period)
        {
            return (int)ExitCode.Usage;
        }

        var report = RunLogs.Logs(host.Paths, host.Files, period, request.Action);
        Output.Answer(stdout, request.Json ? JsonSerializer.Serialize(report, WslCareJsonContext.Default.LogsReport) : LogsText(report));
        return Exit(report.Problem, stderr);
    }

    public static int Runs(Request.Runs request, CliHost host, TextWriter stdout, TextWriter stderr)
    {
        if (Period(request.Period, host, stderr) is not { } period)
        {
            return (int)ExitCode.Usage;
        }

        var report = RunLogs.Runs(host.Paths, host.Files, period);
        Output.Answer(stdout, request.Json ? JsonSerializer.Serialize(report, WslCareJsonContext.Default.RunsReport) : RunsText(report));
        return Exit(report.Problem, stderr);
    }

    private static LogPeriod? Period(string text, CliHost host, TextWriter stderr)
    {
        switch (LogPeriod.Parse(text, host.Clock.GetUtcNow()))
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
            .AppendLine(Invariant($"wsl-care logs {r.Period.Label} ({r.Period.From}..{r.Period.To} UTC){(r.Action is null ? string.Empty : $", {r.Action} only")}: freed {Gb(r.FreedBytes)}, {r.ObjectsRemoved} objects"))
            .AppendLine(Invariant($"runs: {r.Runs.Total} ({r.Runs.WithCleanup} with a cleanup, {r.Runs.WithoutCleanup} without; {r.Runs.DryRun} dry, would have freed {Gb(r.Runs.WouldFreeBytes)}; timer {r.Runs.Timer}, button {r.Runs.Manual}, terminal {r.Runs.Cli})"));
        foreach (var total in r.PerAction.Where(t => t.Runs > 0 || t.DryRuns > 0))
        {
            text.AppendLine(Invariant($"  {total.Id,-17} ran {total.Runs}x, {total.Count} objects, freed {Gb(total.FreedBytes)}; dry {total.DryRuns}x, would free {Gb(total.WouldFreeBytes)}"));
        }

        if (r.MostFreed is { } most && r.LeastFreed is { } least)
        {
            text.AppendLine(Invariant($"most freed: {Gb(most.FreedBytes)} (run {most.RunId}); least (non-zero): {Gb(least.FreedBytes)} (run {least.RunId})"));
        }

        foreach (var cleanup in r.Cleanups)
        {
            text.AppendLine(Invariant($"  {cleanup.StartedAt.UtcDateTime:yyyy-MM-dd HH:mm}Z {cleanup.Action}: {cleanup.Count} removed, {Gb(cleanup.FreedBytes)} ({cleanup.Trigger}; detail {cleanup.DetailState})"));
            foreach (var item in cleanup.Removed.Take(20))
            {
                text.AppendLine(Invariant($"      {item.Kind} {CommandLine.Printable(item.Name)}{(item.Bytes is { } b ? $" {Gb(b)}" : string.Empty)}"));
            }
        }

        return text.ToString().TrimEnd();
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

    private static string Gb(long bytes) => Invariant($"{bytes / BytesPerGigabyte:0.00} GB");

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
