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

    /// <summary>The refusal of a privileged process, in the words of its side (E9.S3 own review round C-9).</summary>
    internal static string RootRefusalFor(Core.Hosting.HostSide side, string verb) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, side == Core.Hosting.HostSide.Windows ? ElevatedRefusal : RootRefusal, verb);

    /// <summary>On Windows a privileged process is an ELEVATED one — the same user, so "as that user" would tell them nothing.</summary>
    internal const string ElevatedRefusal =
        "archive {0} runs in this user's own, NOT elevated process — an elevated (administrator) one is refused (plan §15r D1): run it from a terminal that is not elevated. Nothing was touched.";

    public static int Run(Request.ArchiveRun request, CliHost host, ConfigLoadResult loaded, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        if (host.Privilege.IsRoot)
        {
            Output.Note(stderr, RootRefusalFor(host.Paths.Side, "run"));
            return (int)ExitCode.NotAsRoot;
        }

        var budget = TimeSpan.FromSeconds(request.BudgetSeconds > 0 ? request.BudgetSeconds : loaded.Config.Int(ConfigKeys.Archive.RunBudgetMinutes) * 60);
        using var progress = new ArchiveProgress(stdout, stderr, request.Json, host.Clock, Silence(loaded.Config));
        var input = Input(host, loaded, request.Agent, budget, cancellationToken);
        var report = ArchiveRun.Run(input with { Progress = progress.Line, RunId = request.RunId.Length > 0 ? request.RunId : input.RunId });
        return progress.Answer(() => Answered(stdout, request.Json ? JsonSerializer.Serialize(report, WslCareJsonContext.Compact.ArchiveRunReport) : Render(report), report));
    }

    /// <summary><c>archive reach</c> (plan §15r D1, E9.S4): the side's lock and the base within <c>archive.reachabilitySeconds</c>;
    /// exit 0 when it answered (or no base is set: nothing to reach), <see cref="ExitCode.RunFailed"/> when it did not, was refused or the
    /// lock is held.</summary>
    public static int Reach(Request.ArchiveReach request, CliHost host, ConfigLoadResult loaded, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        if (host.Privilege.IsRoot)
        {
            Output.Note(stderr, RootRefusalFor(host.Paths.Side, "reach"));
            return (int)ExitCode.NotAsRoot;
        }

        var report = ArchiveRun.Run(Input(host, loaded, string.Empty, TimeSpan.MaxValue, cancellationToken) with { ReachOnly = true });
        var exit = Answered(stdout, request.Json ? JsonSerializer.Serialize(report, WslCareJsonContext.Compact.ArchiveRunReport) : Render(report), report);
        return exit == (int)ExitCode.Ok && report.Outcome is not (RunOutcomes.Done or RunOutcomes.NoBase) ? (int)ExitCode.RunFailed : exit;
    }

    public static int ReconcileScan(Request.ArchiveReconcileScan request, CliHost host, ConfigLoadResult loaded, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        if (host.Privilege.IsRoot)
        {
            Output.Note(stderr, RootRefusalFor(host.Paths.Side, "reconcile --scan"));
            return (int)ExitCode.NotAsRoot;
        }

        var report = ArchiveRun.Run(Input(host, loaded, string.Empty, TimeSpan.MaxValue, cancellationToken) with { ScanOnly = true });
        return Answered(stdout, request.Json ? JsonSerializer.Serialize(report, WslCareJsonContext.Compact.ArchiveRunReport) : Render(report), report);
    }

    /// <summary><c>archive restore</c> (plan §15r D6, E9.S3): under the side's lock and lease like a run; exit 0 when every session
    /// asked for was restored or already there, <see cref="ExitCode.RunFailed"/> when one was refused.</summary>
    public static int Restore(Request.ArchiveRestore request, CliHost host, ConfigLoadResult loaded, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        if (host.Privilege.IsRoot)
        {
            Output.Note(stderr, RootRefusalFor(host.Paths.Side, "restore"));
            return (int)ExitCode.NotAsRoot;
        }

        var asked = new RestoreRequest(request.EntryIds, request.Agent, request.Month, request.Session, request.AcceptUnverified);
        var budget = TimeSpan.FromMinutes(loaded.Config.Int(ConfigKeys.Archive.RestoreLimitMinutes));
        using var progress = new ArchiveProgress(stdout, stderr, request.Json, host.Clock, Silence(loaded.Config));
        var report = ArchiveRun.Run(Input(host, loaded, request.Agent, budget, cancellationToken) with { Restore = asked, Progress = progress.Line });
        var text = request.Json ? JsonSerializer.Serialize(report, WslCareJsonContext.Compact.ArchiveRunReport) : RenderRestore(report);
        var exit = progress.Answer(() => Answered(stdout, text, report));
        return exit == (int)ExitCode.Ok && report.Restore.Refused > 0 ? (int)ExitCode.RunFailed : exit;
    }

    /// <summary><c>archive list</c> (plan §15r E9.S3): read-only — no lock, no lease, no key made; its <c>--json</c> answer ONE line, as
    /// every archive child's (the S4 own review round C-8).</summary>
    public static int List(Request.ArchiveList request, CliHost host, ConfigLoadResult loaded, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        if (host.Privilege.IsRoot)
        {
            Output.Note(stderr, RootRefusalFor(host.Paths.Side, "list"));
            return (int)ExitCode.NotAsRoot;
        }

        var report = ArchiveList.List(Input(host, loaded, request.Agent, TimeSpan.MaxValue, cancellationToken), new ArchiveListRequest(request.Agent, request.Month, request.RunId) { Restorable = request.Restorable });
        _ = Output.Answer(stdout, request.Json ? JsonSerializer.Serialize(report, WslCareJsonContext.Compact.ArchiveListReport) : RenderList(report));
        return report.Outcome is RunOutcomes.Done or RunOutcomes.NoBase ? (int)ExitCode.Ok : (int)ExitCode.RunFailed;
    }

    private static string RenderRestore(ArchiveRunReport report)
    {
        var text = new StringBuilder().AppendLine(CommandLine.Printable($"wsl-care archive restore {report.RunId} ({report.SideFolder}): {report.Outcome}{(report.Stop.Length > 0 ? " - " + report.Stop : string.Empty)}; restored {report.Restore.Restored}, already there {report.Restore.AlreadyThere}, refused {report.Restore.Refused}"));
        foreach (var session in report.Restore.Sessions)
        {
            text.AppendLine(CommandLine.Printable($"  {session.Outcome,-13} {session.Agent} {session.Key} ({session.EntryId}): {session.Note}"));
        }

        foreach (var note in report.Reconcile.Notes)
        {
            text.AppendLine(CommandLine.Printable("  " + note));
        }

        return text.ToString().TrimEnd();
    }

    private static string RenderList(ArchiveListReport report)
    {
        var text = new StringBuilder().AppendLine(CommandLine.Printable($"wsl-care archive list ({report.SideFolder}): {report.Outcome}{(report.Note.Length > 0 ? " - " + report.Note : string.Empty)}; {report.Entries.Count} entries, {report.SkippedLines} index lines skipped"));
        foreach (var entry in report.Entries)
        {
            text.AppendLine(CommandLine.Printable(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"  {entry.EntryId} {entry.Agent,-12} {entry.Month} {entry.Status,-13}{(entry.Verified ? string.Empty : " UNVERIFIED")} {entry.Files} file(s), {entry.Bytes} bytes  {entry.Key}")));
        }

        foreach (var note in report.Notes)
        {
            text.AppendLine(CommandLine.Printable("  " + note));
        }

        return text.ToString().TrimEnd();
    }

    public static int Status(Request.ArchiveStatus request, CliHost host, ConfigLoadResult loaded, TextWriter stdout, TextWriter stderr)
    {
        if (host.Privilege.IsRoot)
        {
            Output.Note(stderr, RootRefusalFor(host.Paths.Side, "status"));
            return (int)ExitCode.NotAsRoot;
        }

        var report = ArchiveStatus.Of(host.Paths, host.Files, loaded.Config, host.Processes);
        return Output.Answer(stdout, request.Json ? JsonSerializer.Serialize(report, WslCareJsonContext.Default.ArchiveStatusReport) : RenderStatus(report));
    }

    private static TimeSpan Silence(EffectiveConfig config) => TimeSpan.FromSeconds(config.Int(ConfigKeys.Archive.ProgressSilenceSeconds));

    private static ArchiveRunInput Input(CliHost host, ConfigLoadResult loaded, string agent, TimeSpan budget, CancellationToken cancellationToken)
    {
        // The S4 own review round S-M1: the base is judged LATE — inside the bounded window, after the side's lock for a reach — never
        // here, before it: a share that stops answering would hold this child in the kernel, unbounded and before its lock.
        var configured = loaded.Config.Text(ConfigKeys.Archive.BaseFolder);
        var judging = configured.Length == 0 ? BaseJudging.Already : BaseJudging.Within(() => OnDisk(host, ArchiveCommand.Judge(host, configured)));
        var now = host.Clock.GetUtcNow();
        var boot = host.Processes.Boot();
        var me = host.Processes.Lookup(Environment.ProcessId) is Core.Actions.Engine.ProcessLookup.Alive alive
            ? new LeaseRecord(1, Environment.MachineName, boot.BootId, Environment.ProcessId, alive.StartTicks ?? 0, alive.StartUtc, string.Empty, now)
            : new LeaseRecord(1, Environment.MachineName, boot.BootId, Environment.ProcessId, 0, now, string.Empty, now);
        return new ArchiveRunInput(host.Paths, host.Files, host.ArchiveFiles(), loaded.Config, host.Clock, host.Processes, TimeZoneInfo.Local, judging.Late ? BaseFolderRules.NotYetJudged : BaseFolderRules.Unconfigured, RunId.New(now, Environment.ProcessId).Text, budget, Environment.GetEnvironmentVariable, cancellationToken)
        {
            Judging = judging,
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
            RunOutcomes.Stopped when StopKinds.IsFault(report.StopKind) => (int)ExitCode.RunFailed,
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

    internal static string RenderStatus(ArchiveStatusReport report)
    {
        var text = new StringBuilder().AppendLine(CommandLine.Printable($"wsl-care archive status ({report.SideFolder}): lock {report.Lock.State}{(report.Lock.Pid > 0 ? $" (pid {report.Lock.Pid}, run {report.Lock.RunId})" : string.Empty)}; {report.Inflight.Count} on the way"));
        foreach (var entry in report.Inflight)
        {
            text.AppendLine(CommandLine.Printable($"  {entry.State,-9} {entry.Agent} {entry.Key} ({entry.Files} file(s), {entry.Month})"));
        }

        if (report.LastRun is { } last)
        {
            text.AppendLine(CommandLine.Printable($"  last run {last.RunId}: {last.Outcome}, copied {last.Copied}, removed {last.Removed}"));
        }

        return text.ToString().TrimEnd('\r', '\n');
    }
}
