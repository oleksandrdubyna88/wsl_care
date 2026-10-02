using System.Globalization;
using System.Text;
using System.Text.Json;

using Serilog;

using WslCare.Core;
using WslCare.Core.Collect;
using WslCare.Core.Config;
using WslCare.Core.Json;
using WslCare.Core.Records;
using WslCare.Core.Thresholds;

namespace WslCare.Cli.Commands;

/// <summary>
/// <c>collect [--json]</c> (plan §6): the full run — the timer's target and the panel's <i>Run full check now</i>.
/// Run as root it records the run (detail, then history line; the run log closes last, in <c>Program</c>); run as
/// anyone else it measures, prints, writes nothing and says <i>read-only: run as root to record</i> (plan §15b #3).
/// </summary>
/// <remarks>Exit codes: 0 recorded or read-only; 1 the run could not be recorded (detail or history line); 75 another
/// run holds the lock. The trigger is <c>timer</c> under systemd (which sets <c>INVOCATION_ID</c> for every unit it
/// runs), <c>cli</c> otherwise; the panel's button (E6) passes through the root allowlist as <c>collect</c>.</remarks>
internal static class CollectCommand
{
    public static int Run(Request.Collect request, CliHost host, ConfigLoadResult loaded, TextWriter stdout, TextWriter stderr, ILogger logger, CancellationToken cancellationToken)
    {
        var log = logger.ForContext(typeof(CollectCommand));
        var context = new CollectContext(host.Paths, host.Files, host.Commands, host.Clock, host.Probe, loaded, Environment.ProcessId, Trigger());
        // A console program has no synchronisation context; blocking here is the verb's whole job.
        var result = CollectRun.RunAsync(context, cancellationToken).GetAwaiter().GetResult();
        Log(log, result);
        if (result.Recording == Recording.Busy)
        {
            Output.Note(stderr, result.Reason);
            return (int)ExitCode.Busy;
        }

        var answer = request.Json
            ? JsonSerializer.Serialize(Report(result), WslCareJsonContext.Default.CollectReport)
            : CollectText.Render(result);
        Output.Answer(stdout, answer);
        if (result.Recording == Recording.ReadOnly)
        {
            Output.Note(stderr, CollectRun.ReadOnlyNote);
        }

        if (result.Recording == Recording.Failed)
        {
            Output.Note(stderr, $"the run was not recorded: {result.Reason}");
            return (int)ExitCode.RunFailed;
        }

        return (int)ExitCode.Ok;
    }

    internal static CollectReport Report(CollectResult result) =>
        new(SchemaVersion.Current, Camel(result.Recording.ToString()), result.Reason.Length == 0 ? null : result.Reason, result.DetailFile.Length == 0 ? null : result.DetailFile, result.Detail);

    private static RunTrigger Trigger() =>
        Environment.GetEnvironmentVariable("INVOCATION_ID") is { Length: > 0 } ? RunTrigger.Timer : RunTrigger.Cli;

    private static void Log(ILogger log, CollectResult result)
    {
        switch (result.Recording)
        {
            case Recording.Recorded:
                log.Information("run {RunId} recorded: detail {Detail}, then its history line", result.Detail?.RunId.Text, result.DetailFile);
                break;
            case Recording.Failed:
                log.Error("run {RunId} could not be recorded: {Reason}", result.Detail?.RunId.Text, result.Reason);
                break;
            default:
                log.Warning("run {Recording}: {Reason}", result.Recording, result.Reason);
                break;
        }

        foreach (var verdict in result.Detail?.Thresholds.Where(v => v.Level is Level.Warn or Level.Critical) ?? [])
        {
            log.Warning("{Threshold} {Level}: {Value} ({Reason})", verdict.Id, verdict.Level, verdict.Value, verdict.Reason);
        }
    }

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];
}

/// <summary>The human form: what the run found that is not ok, in a few lines. The extension reads the JSON.</summary>
internal static class CollectText
{
    public static string Render(CollectResult result)
    {
        var detail = result.Detail!;
        var text = new StringBuilder()
            .AppendLine(Invariant($"wsl-care collect ({detail.Side}), run {detail.RunId.Text}, {(detail.EndedAt - detail.StartedAt).TotalSeconds:0.0} s, {Recorded(result)}"));
        var notOk = detail.Thresholds.Where(v => v.Level != Level.Ok).ToList();
        text.AppendLine(Invariant($"thresholds: {detail.Thresholds.Count - notOk.Count} ok, {notOk.Count(v => v.Level == Level.Warn)} warn, {notOk.Count(v => v.Level == Level.Critical)} critical, {notOk.Count(v => v.Level == Level.Unknown)} unknown"));
        foreach (var verdict in notOk)
        {
            text.AppendLine($"  {verdict.Level.ToString().ToLowerInvariant(),-8} {verdict.Id}: {(verdict.Value.Length > 0 ? verdict.Value + " - " : string.Empty)}{verdict.Reason}");
        }

        var starts = detail.ContainerStarts;
        return text.Append(Invariant($"container starts, last 24 h: {starts.Starts}{(starts.Complete ? string.Empty : $" (partial: {starts.Gaps.Count} gap(s))")}")).ToString();
    }

    private static string Recorded(CollectResult result) => result.Recording switch
    {
        Recording.Recorded => $"recorded ({result.DetailFile})",
        Recording.ReadOnly => "not recorded (read-only)",
        _ => "NOT recorded",
    };

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
