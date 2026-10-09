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
/// <c>watch [--timer] [--json]</c> (plan E14 S2b): the watch timer's run — <see cref="WatchRun"/>. As root only (it writes root's
/// state), the distro's binary only.
/// </summary>
/// <remarks>Exit codes: 0 sampled or acted — an action that fails is in the act's record and the log, and does not change the exit
/// (as a full run's pass); 1 the act ran and could not be recorded; 2 the Windows binary (the watch is the distro's); 75 another run
/// holds the lock; 77 not root. <c>--timer</c> is the watch unit's own mark in its <c>ExecStart</c>, never inferred.</remarks>
internal static class WatchCommand
{
    public const string NeedsRootText = "needs root: the watch writes root's CPU ledger and history under the state directory, and A19 is root's (plan 15c #0)";

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
        };
        // A console program has no synchronisation context; blocking here is the verb's whole job.
        var result = WatchRun.RunAsync(context, cancellationToken).GetAwaiter().GetResult();
        Log(log, result);
        Output.Answer(stdout, request.Json ? JsonSerializer.Serialize(WatchReport.From(result), WslCareJsonContext.Default.WatchReport) : Render(result));
        return Exit(result, stderr);
    }

    /// <summary>Why the watch does not run at all here — the Windows binary, or not root.</summary>
    private static (ExitCode Exit, string Text)? Refusal(CliHost host) => host switch
    {
        { Paths: not LinuxHostPaths } => (ExitCode.Usage, $"\"{CommandLine.BinaryName} watch\" is the distro's: {WatchRun.NotTheDistro}"),
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
            log.Warning("root's MCP CPU ledger was not recorded: {Reason}", result.Ledger.Reason);
        }

        if (result.History.Length > 0)
        {
            log.Warning("the agents' CPU history was not recorded: {Reason}", result.History);
        }
    }

    /// <summary>One line a person reads.</summary>
    private static string Render(WatchResult result)
    {
        var ledger = result.Ledger.Recorded ? "ledger recorded" : $"ledger not recorded ({result.Ledger.Reason})";
        var history = result.History.Length == 0 ? "history recorded" : $"history not recorded ({result.History})";
        var tried = result.Tried.Count == 0 ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"; A19 asked to stop {string.Join(", ", result.Tried)}");
        return CommandLine.Printable($"watch {result.Outcome.ToString().ToLowerInvariant()}: {result.Reason}; {ledger}; {history}{tried}");
    }
}
