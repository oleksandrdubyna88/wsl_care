using System.Globalization;
using System.Text;
using System.Text.Json;

using WslCare.Core.Config;
using WslCare.Core.Json;
using WslCare.Core.Preview;

namespace WslCare.Cli.Commands;

/// <summary>
/// <c>preview --all [--json]</c> (plan §6): every cleanup row of plan §4.3 with its count and reclaimable bytes,
/// the kept named volumes, Docker's own totals and the hygiene audit. It runs Docker READ commands only
/// (<c>DockerCommands</c>) through the host's command runner; it removes nothing. Docker being unavailable is an
/// answer (exit 0, every Docker figure <c>available: false</c> with the reason), not an error.
/// </summary>
internal static class PreviewCommand
{
    public static int Run(Request.Preview request, CliHost host, ConfigLoadResult loaded, TextWriter stdout, CancellationToken cancellationToken)
    {
        // A console program has no synchronisation context; blocking here is the verb's whole job.
        var report = PreviewRun.RunAsync(host.Paths, host.Files, host.Commands, host.Clock, loaded, cancellationToken).GetAwaiter().GetResult();
        return Output.Answer(stdout, request.Json ? JsonSerializer.Serialize(report, WslCareJsonContext.Default.PreviewReport) : PreviewText.Render(report));
    }
}

/// <summary>The human form: the cleanup table as a person reads it at a terminal. The extension reads the JSON.</summary>
internal static class PreviewText
{
    private const double BytesPerGigabyte = 1e9;

    public static string Render(PreviewReport report)
    {
        var text = new StringBuilder()
            .AppendLine(Invariant($"wsl-care preview ({report.Side}), sampled {report.SampledAt.UtcDateTime:yyyy-MM-dd HH:mm:ss}Z in {report.SampleMilliseconds} ms"));
        if (report.ObserveOnly)
        {
            text.AppendLine("observe-only: a configuration layer is invalid (\"wsl-care config get\" names it)");
        }

        text.AppendLine(report.Docker.Available ? $"docker: {report.Docker.ServerVersion} {report.Docker.Platform}".TrimEnd() : $"docker: unavailable ({report.Docker.Kind}: {report.Docker.Reason})");
        foreach (var row in report.Rows)
        {
            text.AppendLine(Row(row));
        }

        text.AppendLine(Kept(report.Kept));
        text.Append($"first sightings: {(report.VolumeSeen.Recorded ? "recorded" : report.VolumeSeen.Reason)} ({report.VolumeSeen.File})");
        return text.ToString();
    }

    private static string Row(PreviewRowReport row)
    {
        var figure = row.Available ? Invariant($"{row.Count} objects, {Gb(row.ReclaimableBytes ?? 0)}") : $"unavailable ({row.Reason})";
        var refusal = row.Refusal is null ? string.Empty : $" - refuses: {row.Refusal}";
        return $"{row.Id,-17} {figure}  {row.What} [{row.AutoSwitch}={(row.Auto ? "on" : "off")}]{refusal}";
    }

    private static string Kept(KeptReport kept) =>
        kept.Available ? Invariant($"kept named volumes: {kept.Count}, {Gb(kept.Bytes ?? 0)} (report only)") : $"kept named volumes: unavailable ({kept.Reason})";

    private static string Gb(long bytes) => Invariant($"{bytes / BytesPerGigabyte:0.00} GB");

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
