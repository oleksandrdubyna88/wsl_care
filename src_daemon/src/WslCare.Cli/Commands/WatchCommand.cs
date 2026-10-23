using WslCare.Core.Processes.Redaction;
using System.Globalization;
using System.Text.Json;

using Serilog;

using WslCare.Core.Actions.Engine;
using WslCare.Core.Collect;
using WslCare.Core.Config;
using WslCare.Core.Hosting;
using WslCare.Core.Json;
using WslCare.Core.Records;
using WslCare.Core.Watch;

namespace WslCare.Cli.Commands;

/// <summary>
/// <c>watch [--timer] [--json]</c> (plan E14 S2b): the watch timer's run — <see cref="WatchRun"/>. On the distro's binary as root only
/// (it writes root's state); on the Windows binary (WR-D4 of PLAN_windows_runner.md) as the user only, NOT elevated (it writes the
/// user's own CPU ledger).
/// </summary>
/// <remarks>Exit codes: 0 sampled or acted — an action that fails is in the act's record and the log, and does not change the exit
/// (as a full run's pass); 1 the act ran and could not be recorded; 75 another run holds the lock (a normal answer: the next period
/// samples); 77 not root (the distro's binary); 81 elevated (the Windows binary). <c>--timer</c> is the watch unit's own mark in its
/// <c>ExecStart</c> — on Windows the <c>tasks run watch</c> supervisor's — never inferred.</remarks>
internal static class WatchCommand
{
    public const string NeedsRootText = "needs root: the watch writes root's CPU ledger and history under the state directory, and A19 is root's (plan 15c #0)";

    /// <summary>The Windows binary's refusal of an elevated process — the wording of <see cref="ArchiveRunCommand.ElevatedRefusal"/>.</summary>
    public const string ElevatedRefusal =
        "watch runs in this user's own, NOT elevated process — an elevated (administrator) one is refused (PLAN_windows_runner.md §3.2): the Windows watch writes this user's own CPU ledger; run it from a terminal that is not elevated. Nothing was touched.";

    public static int Run(Request.Watch request, CliHost host, ConfigLoadResult loaded, TextWriter stdout, TextWriter stderr, ILogger logger, CancellationToken cancellationToken)
    {
        var log = logger.ForContext(typeof(WatchCommand));
        if (Refusal(host) is { } refusal)
        {
            Output.Note(stderr, refusal.Text);
            return (int)refusal.Exit;
        }

        var context = new WatchContext(host.Paths, host.Files, host.Commands, host.Clock, host.Probe, loaded, host.Processes, Environment.ProcessId, host.Actions, request.Timer)
        {
            Signals = host.Signals,
            Wait = host.Wait,
            InterruptCause = host.InterruptCause,
            WindowsProcesses = host.WindowsProcesses,
            WindowsBoot = host.WindowsBoot,
            WindowsVolumes = host.WindowsVolumes,
        };
        // A console program has no synchronisation context; blocking here is the verb's whole job.
        var result = WatchRun.RunAsync(context, cancellationToken).GetAwaiter().GetResult();
        Log(log, result);
        Output.Answer(stdout, request.Json ? JsonSerializer.Serialize(WatchReport.From(result), WslCareJsonContext.Default.WatchReport) : Render(result));
        return Exit(result, stderr);
    }

    /// <summary>Why the watch does not run at all here — an elevated Windows process, or a distro process that is not root.</summary>
    private static (ExitCode Exit, string Text)? Refusal(CliHost host) => host switch
    {
        { Paths: WindowsHostPaths, Privilege.IsRoot: true } => (ExitCode.NotAsRoot, ElevatedRefusal),
        { Paths: WindowsHostPaths } => null,
        { Privilege.IsRoot: false } => (ExitCode.NeedsRoot, $"{NeedsRootText}; {host.Privilege.Basis}; nothing was done"),
        _ => null,
    };

    private static int Exit(WatchResult result, TextWriter stderr)
    {
        if (result.Outcome == WatchOutcome.Busy)
        {
            Output.Note(stderr, result.Reason);
            return (int)ExitCode.Busy;
        }

        if (result.Acts.OfType<ActResult.Done>().FirstOrDefault(d => d.Recording != Recording.Recorded) is { } unrecorded)
        {
            Output.Note(stderr, $"A19's run was not recorded: {unrecorded.Reason}");
            return (int)ExitCode.RunFailed;
        }

        return (int)ExitCode.Ok;
    }

    private static void Log(ILogger log, WatchResult result)
    {
        log.Information("watch {Outcome}: {Reason}", result.Outcome, result.Reason);
        if (!result.Ledger.Recorded)
        {
            log.Warning("the MCP CPU ledger was not recorded: {Reason}", result.Ledger.Reason);
        }

        if (result.History is { Length: > 0 } history && history != WatchRun.NoAgentHistory)
        {
            log.Warning("the agents' CPU history was not recorded: {Reason}", history);
        }

        LogRetention(log, result.Swept);
        LogVolumes(log, result.Volumes);
    }

    /// <summary>The Windows watch's volume sample (E19.S2): how many volumes it holds, or why it could not be written — a warning; the run went on.</summary>
    private static void LogVolumes(ILogger log, VolumeSampleOutcome volumes)
    {
        switch (volumes)
        {
            case VolumeSampleOutcome.Written written:
                log.Information("volume sample: {Volumes} volume(s) written to {File}", written.Volumes, written.File);
                break;
            case VolumeSampleOutcome.NotWritten notWritten:
                log.Warning("the volume sample was not written: {Reason}", notWritten.Reason);
                break;
        }
    }

    /// <summary>The run records' retention sweep (the Windows watch; none on the distro): what it removed, and every problem as a
    /// warning.</summary>
    private static void LogRetention(ILogger log, IReadOnlyList<RetentionReport> sweeps)
    {
        foreach (var swept in sweeps)
        {
            log.Information("run retention: {Lines} history line(s) and {Details} detail(s) removed", swept.HistoryLinesRemoved, swept.DetailsRemoved.Count);
            foreach (var problem in swept.Problems)
            {
                log.Warning("run retention: {Problem}", problem);
            }
        }
    }

    /// <summary>One line a person reads.</summary>
    private static string Render(WatchResult result)
    {
        var ledger = result.Ledger.Recorded ? "ledger recorded" : $"ledger not recorded ({result.Ledger.Reason})";
        var history = result.History.Length == 0 ? "history recorded" : $"history not recorded ({result.History})";
        var tried = result.Tried.Count == 0 ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"; A19 asked to stop {string.Join(", ", result.Tried)}");
        return ShownText.Printable($"watch {result.Outcome.ToString().ToLowerInvariant()}: {result.Reason}; {ledger}; {history}{tried}{Retention(result.Swept)}{Volumes(result.Volumes)}");
    }

    /// <summary>What the Windows watch did with the volume sample, for the one line (E19.S2) — nothing when it took none (the distro's watch, a busy lock).</summary>
    private static string Volumes(VolumeSampleOutcome volumes) => volumes switch
    {
        VolumeSampleOutcome.Written written => string.Create(CultureInfo.InvariantCulture, $"; volumes sampled: {written.Volumes}"),
        VolumeSampleOutcome.NotWritten notWritten => $"; volumes not sampled ({notWritten.Reason})",
        _ => string.Empty,
    };

    /// <summary>What the run records' retention sweep did, for the one line (the coai code round on WR-D4, finding 6) — nothing when
    /// no sweep ran (the distro's watch).</summary>
    private static string Retention(IReadOnlyList<RetentionReport> sweeps) =>
        string.Concat(sweeps.Select(static s => string.Create(CultureInfo.InvariantCulture,
            $"; retention: {s.HistoryLinesRemoved} history line(s) and {s.DetailsRemoved.Count} detail(s) removed{Problems(s.Problems)}")));

    private static string Problems(IReadOnlyList<string> problems) =>
        problems.Count == 0 ? string.Empty : $" ({string.Join("; ", problems)})";
}
