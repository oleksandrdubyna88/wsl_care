using System.Text;
using System.Text.Json;

using WslCare.Core.Archive;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Json;
using WslCare.Core.Records;

namespace WslCare.Cli.Commands;

/// <summary>
/// <c>archive run</c>, <c>archive status</c> and <c>archive reconcile --scan</c> (plan §15r E9.S2b) — as the user whose sessions the
/// archive moves, never as root (D1). <c>archive run --json</c> streams one progress line per file done and a heartbeat at least every
/// <c>archive.progressSilenceSeconds</c>, then its answer — every line ONE JSON object, the answer the last; no line names a session.
/// </summary>
internal static class ArchiveRunCommand
{
    internal const string RootRefusal =
        "archive {0} runs as the user whose sessions the archive moves, not as uid 0 — that user's own process moves them (plan §15r D1): run it as that user. Nothing was touched.";

    public static int Run(Request.ArchiveRun request, CliHost host, ConfigLoadResult loaded, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        if (host.Privilege.IsRoot)
        {
            Output.Note(stderr, string.Format(System.Globalization.CultureInfo.InvariantCulture, RootRefusal, "run"));
            return (int)ExitCode.NotAsRoot;
        }

        var budget = TimeSpan.FromSeconds(request.BudgetSeconds > 0 ? request.BudgetSeconds : loaded.Config.Int(ConfigKeys.Archive.RunBudgetMinutes) * 60);
        var gate = new object();
        void Line(ArchiveProgressLine line)
        {
            if (request.Json)
            {
                lock (gate)
                {
                    Output.Progress(stdout, JsonSerializer.Serialize(line, WslCareJsonContext.Compact.ArchiveProgressLine));
                }
            }
        }

        var started = host.Clock.GetTimestamp();
        using var heartbeat = new Timer(_ => Line(new ArchiveProgressLine("heartbeat", 0, 0, host.Clock.GetElapsedTime(started).TotalSeconds)), null, Silence(loaded.Config), Silence(loaded.Config));
        var input = Input(host, loaded, request.Agent, budget, cancellationToken) with { Progress = Line };
        var report = ArchiveRun.Run(input);
        heartbeat.Change(Timeout.Infinite, Timeout.Infinite);
        lock (gate)
        {
            return Answered(stdout, request.Json ? JsonSerializer.Serialize(report, WslCareJsonContext.Compact.ArchiveRunReport) : Render(report), report);
        }
    }

    public static int ReconcileScan(Request.ArchiveReconcileScan request, CliHost host, ConfigLoadResult loaded, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        if (host.Privilege.IsRoot)
        {
            Output.Note(stderr, string.Format(System.Globalization.CultureInfo.InvariantCulture, RootRefusal, "reconcile --scan"));
            return (int)ExitCode.NotAsRoot;
        }

        var report = ArchiveRun.Run(Input(host, loaded, string.Empty, TimeSpan.MaxValue, cancellationToken) with { ScanOnly = true });
        return Answered(stdout, request.Json ? JsonSerializer.Serialize(report, WslCareJsonContext.Compact.ArchiveRunReport) : Render(report), report);
    }

    public static int Status(Request.ArchiveStatus request, CliHost host, ConfigLoadResult loaded, TextWriter stdout, TextWriter stderr)
    {
        if (host.Privilege.IsRoot)
        {
            Output.Note(stderr, string.Format(System.Globalization.CultureInfo.InvariantCulture, RootRefusal, "status"));
            return (int)ExitCode.NotAsRoot;
        }

        var report = ArchiveStatus.Of(host.Paths, host.Files, loaded.Config, host.Processes);
        return Output.Answer(stdout, request.Json ? JsonSerializer.Serialize(report, WslCareJsonContext.Default.ArchiveStatusReport) : RenderStatus(report));
    }

    private static TimeSpan Silence(EffectiveConfig config) => TimeSpan.FromSeconds(config.Int(ConfigKeys.Archive.ProgressSilenceSeconds));

    private static ArchiveRunInput Input(CliHost host, ConfigLoadResult loaded, string agent, TimeSpan budget, CancellationToken cancellationToken)
    {
        var configured = loaded.Config.Text(ConfigKeys.Archive.BaseFolder);
        var judged = OnDisk(host, configured.Length == 0 ? BaseFolderRules.Unconfigured : ArchiveCommand.Judge(host, configured));
        var now = host.Clock.GetUtcNow();
        var boot = host.Processes.Boot();
        var me = host.Processes.Lookup(Environment.ProcessId) is Core.Actions.Engine.ProcessLookup.Alive alive
            ? new LeaseRecord(1, Environment.MachineName, boot.BootId, Environment.ProcessId, alive.StartTicks ?? 0, alive.StartUtc, string.Empty, now)
            : new LeaseRecord(1, Environment.MachineName, boot.BootId, Environment.ProcessId, 0, now, string.Empty, now);
        return new ArchiveRunInput(host.Paths, host.Files, host.ArchiveFiles(), loaded.Config, host.Clock, host.Processes, TimeZoneInfo.Local, judged, RunId.New(now, Environment.ProcessId).Text, budget, Environment.GetEnvironmentVariable, cancellationToken)
        {
            OnlyAgent = agent,
            Me = me,
            Step = host.ArchiveFault,
        };
    }

    /// <summary>The judged base as THIS process reaches it on disk: the distribution's spelling of it under a sandbox root
    /// (<c>WSL_CARE_ROOT</c> — never the real <c>/mnt/v</c> of the machine a scenario runs on), the folder itself everywhere else.</summary>
    private static BaseFolderReport OnDisk(CliHost host, BaseFolderReport judged) =>
        judged.Folder.Length > 0 && host.Paths is Core.Hosting.LinuxHostPaths linux ? judged with { Folder = linux.DistroPath(judged.Folder) } : judged;

    /// <summary>The answer, and the exit: 0 when the run did its work or stopped by a limit; <see cref="ExitCode.Busy"/> when another
    /// run holds the side; <see cref="ExitCode.RunFailed"/> when it was refused, could not reach the base, or stopped on a fault.</summary>
    private static int Answered(TextWriter stdout, string text, ArchiveRunReport report)
    {
        _ = Output.Answer(stdout, text);
        return report.Outcome switch
        {
            RunOutcomes.Done or RunOutcomes.NoBase => (int)ExitCode.Ok,
            RunOutcomes.Busy => (int)ExitCode.Busy,
            RunOutcomes.Stopped when report.Stop.Contains("read back different", StringComparison.Ordinal) => (int)ExitCode.RunFailed,
            RunOutcomes.Stopped => (int)ExitCode.Ok,
            _ => (int)ExitCode.RunFailed,
        };
    }

    private static string Render(ArchiveRunReport report)
    {
        var text = new StringBuilder().AppendLine(CommandLine.Printable($"wsl-care archive run {report.RunId} ({report.SideFolder}): {report.Outcome}{(report.Stop.Length > 0 ? " — " + report.Stop : string.Empty)}"));
        foreach (var agent in report.Agents)
        {
            text.AppendLine(CommandLine.Printable(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"  {agent.Id,-14} copied {agent.Copied} ({agent.CopiedBytes / 1048576.0:0.0} MiB), removed {agent.Removed}, waiting {agent.Waiting}, gone {agent.GoneAtSource}, superseded {agent.Superseded}, damaged {agent.Damaged}")));
        }

        if (report.Scan.SideFolders > 0)
        {
            text.AppendLine(CommandLine.Printable($"  scan: {report.Scan.SideFolders} side folders, {report.Scan.Files} files, {report.Scan.Recovered} re-indexed as recovered"));
        }

        foreach (var note in report.Reconcile.Notes)
        {
            text.AppendLine(CommandLine.Printable($"  note: {note}"));
        }

        return text.ToString().TrimEnd('\r', '\n');
    }

    private static string RenderStatus(ArchiveStatusReport report)
    {
        var text = new StringBuilder().AppendLine(CommandLine.Printable($"wsl-care archive status ({report.SideFolder}): lock {report.Lock.State}{(report.Lock.Pid > 0 ? $" (pid {report.Lock.Pid}, run {report.Lock.RunId})" : string.Empty)}; {report.Inflight.Count} on the way"));
        if (report.LastRun is { } last)
        {
            text.AppendLine(CommandLine.Printable($"  last run {last.RunId}: {last.Outcome}, copied {last.Copied}, removed {last.Removed}"));
        }

        return text.ToString().TrimEnd('\r', '\n');
    }
}
