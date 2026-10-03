using System.Globalization;
using System.Text;
using System.Text.Json;

using Serilog;

using WslCare.Core.Actions;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Collect;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Json;
using WslCare.Core.Records;

namespace WslCare.Cli.Commands;

/// <summary>
/// <c>act &lt;A#&gt;[,&lt;A#&gt;…] (--preview or --confirm) [--manual] [--volume &lt;name&gt;]... [--only &lt;file&gt;] [--json]</c> (plan §6, §7.3, §15c #0): every act runs as ROOT — started by
/// anyone else it refuses the whole run (<i>needs root</i>, exit 77) before the lock or any state is touched.
/// <c>--preview</c> prints each action's LIVE preview and writes nothing; <c>--confirm</c> runs them through the
/// <see cref="ActionEngine"/>: the run lock, the <c>running.json</c> sweep, each action's gates, the measured result, the
/// run detail and the history line.
/// </summary>
/// <remarks>
/// <para>Exit codes: 0 previewed / run recorded (an action that was skipped, deferred, refused or dry-run is still 0 — the
/// answer says which and why); 1 the run could not be recorded; 2 usage (an unknown id, an action this build does not
/// hold, an action of the other side); 3 an action failed (the run was recorded and the rest ran); 75 another run holds
/// the lock (collect or act — the second refuses, it never waits) or a live run is acting; 76 a run is wedged (alive,
/// heartbeat stale) or the running state cannot be told — nothing was killed; 77 needs root; 78 observe-only (a
/// configuration layer is invalid); 130 interrupted.</para>
/// <para>The trigger is <c>timer</c> under systemd (<c>INVOCATION_ID</c>, as for <c>collect</c>) — then the <c>auto</c>
/// switches, the triggers and the 7-day dry run apply — <c>manual</c> when the panel's button passes <c>--manual</c> (E3.S2;
/// the timer wins if both are true: more gates, not fewer), and <c>cli</c> otherwise. A4 on a <c>manual</c> run removes
/// only the volumes passed with <c>--volume</c> / <c>--only</c> — what its preview SHOWED — and refuses without them.</para>
/// </remarks>
internal static class ActCommand
{
    /// <summary>The largest <c>--only</c> file read: 10 000 names of 65 bytes, with room to spare.</summary>
    private const int MaxOnlyFileBytes = 1024 * 1024;

    public static int Run(Request.Act request, CliHost host, ConfigLoadResult loaded, TextWriter stdout, TextWriter stderr, ILogger logger, CancellationToken cancellationToken)
    {
        var log = logger.ForContext(typeof(ActCommand));
        if (Refusal(request, host, loaded) is { } refused)
        {
            log.Warning("act refused: {Reason}", refused.Message);
            Output.Note(stderr, refused.Message);
            return (int)refused.Code;
        }

        var shown = ShownVolumes(request, host);
        if (shown.Failure.Length > 0)
        {
            log.Warning("act refused: {Reason}", shown.Failure);
            Output.Note(stderr, shown.Failure);
            return (int)ExitCode.Usage;
        }

        var engine = new ActionEngine(new EngineContext(host.Paths, host.Files, host.Commands, host.Clock, host.Probe, loaded, host.Processes, Environment.ProcessId, host.Actions) { Signals = host.Signals });
        var act = new ActRequest(request.Ids, Trigger(request), request.Confirm) { ShownVolumes = shown.List };
        // A console program has no synchronisation context; blocking here is the verb's whole job.
        var result = (request.Confirm ? engine.ExecuteAsync(act, cancellationToken) : engine.PreviewAsync(act, cancellationToken)).GetAwaiter().GetResult();
        Log(log, result);
        Output.Answer(stdout, request.Json ? JsonSerializer.Serialize(ActReport.From(result), WslCareJsonContext.Default.ActReport) : ActText.Render(result));
        return Exit(result, stderr);
    }

    /// <summary>A refusal of the whole request and its code; <c>null</c> when it may go on. Root FIRST: nothing else is
    /// asked of an unprivileged process (plan §15c #0).</summary>
    private static (ExitCode Code, string Message)? Refusal(Request.Act request, CliHost host, ConfigLoadResult loaded)
    {
        if (!host.Privilege.IsRoot)
        {
            return (ExitCode.NeedsRoot, $"needs root: every act runs as root (plan 15c #0) and {host.Privilege.Basis}; nothing was done");
        }

        var unbuilt = request.Ids.Where(id => host.Actions.Find(id) is null).ToList();
        if (unbuilt.Count > 0)
        {
            return (ExitCode.Usage, $"{string.Join(", ", unbuilt)} {(unbuilt.Count == 1 ? "is" : "are")} not built in this release; act holds: {string.Join(", ", host.Actions.Actions.Select(a => a.Id.Text))}");
        }

        var otherSide = request.Ids.Select(id => host.Actions.Find(id)!).Where(a => !a.Sides.Contains(host.Paths.Side)).ToList();
        if (otherSide.Count > 0)
        {
            return (ExitCode.Usage, $"{string.Join(", ", otherSide.Select(a => a.Id))} {(otherSide.Count == 1 ? "runs" : "run")} inside the WSL distro, not on this side (the {(host.Paths.Side == Core.Hosting.HostSide.Wsl ? "WSL" : "Windows")} binary)");
        }

        return request.Confirm && loaded.IsObserveOnly ? (ExitCode.ObserveOnly, ActionEngine.ObserveOnlyReason) : null;
    }

    private static RunTrigger Trigger(Request.Act request) =>
        Environment.GetEnvironmentVariable("INVOCATION_ID") is { Length: > 0 } ? RunTrigger.Timer
        : request.Manual ? RunTrigger.Manual
        : RunTrigger.Cli;

    /// <summary>The volumes A4's preview showed, from <c>--volume</c> and the <c>--only</c> file together — or why the file
    /// cannot be used (it is read as root: its content is validated line by line and never echoed).</summary>
    private static (ShownList List, string Failure) ShownVolumes(Request.Act request, CliHost host)
    {
        if (!request.HasShownList)
        {
            return (ShownList.None, string.Empty);
        }

        if (request.OnlyFile.Length == 0)
        {
            return (ShownList.Of(request.Volumes), string.Empty);
        }

        if (host.Files.FileSize(request.OnlyFile) is not FileSizeResult.Measured { Bytes: <= MaxOnlyFileBytes } || host.Files.ReadFile(request.OnlyFile) is not FileReadResult.Content content)
        {
            return (ShownList.None, $"act: the --only file {CommandLine.Printable(request.OnlyFile)} is missing, unreadable, or larger than {MaxOnlyFileBytes} bytes; nothing was done");
        }

        var (names, failure) = CommandLine.ShownVolumesFile(Encoding.UTF8.GetString(content.Bytes));
        return failure.Length > 0
            ? (ShownList.None, $"act: {failure}; nothing was done")
            : (ShownList.Of(names.Concat(request.Volumes)), string.Empty);
    }

    private static int Exit(ActResult result, TextWriter stderr)
    {
        switch (result)
        {
            case ActResult.Busy busy:
                Output.Note(stderr, $"busy: {busy.Reason}");
                return (int)ExitCode.Busy;
            case ActResult.Wedged wedged:
                Output.Note(stderr, wedged.Reason);
                return (int)ExitCode.Wedged;
            case ActResult.Done { Recording: not Recording.Recorded } done:
                Output.Note(stderr, $"the run was not recorded: {done.Reason}");
                return (int)ExitCode.RunFailed;
            case ActResult.Done done when done.Detail.Actions.Any(a => a.Status == ActionStatus.Failed):
                Output.Note(stderr, $"failed: {string.Join("; ", done.Detail.Actions.Where(a => a.Status == ActionStatus.Failed).Select(a => $"{a.Id}: {a.Reason}"))}");
                return (int)ExitCode.ActionFailed;
            default:
                return (int)ExitCode.Ok;
        }
    }

    private static void Log(ILogger log, ActResult result)
    {
        foreach (var outcome in Outcomes(result))
        {
            switch (outcome.Status)
            {
                case ActionStatus.Failed:
                    log.Error("{Action} {Status}: {Reason}", outcome.Id, outcome.Status, outcome.Reason);
                    break;
                case ActionStatus.Deferred or ActionStatus.Refused:
                    log.Warning("{Action} {Status}: {Reason}", outcome.Id, outcome.Status, outcome.Reason);
                    break;
                default:
                    log.Information("{Action} {Status}: {Reason} (count {Count}, freed {FreedBytes} bytes)", outcome.Id, outcome.Status, outcome.Reason, outcome.Run?.Count ?? outcome.Preview?.Count, outcome.Run?.FreedBytes);
                    break;
            }
        }

        if (result is ActResult.Done done)
        {
            log.Information("act run {RunId}: {Recording} {DetailFile}; dry run {DryRun} ({DryRunReason}); {Notes}", done.Detail.RunId.Text, done.Recording, done.DetailFile, done.Detail.DryRun, done.Detail.DryRunReason, string.Join("; ", done.Detail.Notes));
        }
    }

    internal static IReadOnlyList<ActionOutcome> Outcomes(ActResult result) => result switch
    {
        ActResult.Previewed p => p.Actions,
        ActResult.Done d => d.Detail.Actions,
        _ => [],
    };
}

/// <summary>The human form: one line per action. The extension reads the JSON.</summary>
internal static class ActText
{
    private const double BytesPerGigabyte = 1e9;

    public static string Render(ActResult result)
    {
        var text = new StringBuilder().AppendLine(Head(result));
        foreach (var outcome in ActCommand.Outcomes(result))
        {
            text.AppendLine(Line(outcome));
        }

        return text.ToString().TrimEnd();
    }

    private static string Head(ActResult result) => result switch
    {
        ActResult.Previewed p => $"wsl-care act --preview (nothing run, nothing written); target user: {User(p.TargetUser)}",
        ActResult.Done d => $"wsl-care act, run {d.Detail.RunId.Text} ({d.Detail.Trigger.ToString().ToLowerInvariant()}), {(d.Recording == Recording.Recorded ? $"recorded ({d.DetailFile})" : "NOT recorded")}; dry run: {(d.Detail.DryRun ? "yes" : "no")} - {d.Detail.DryRunReason}",
        ActResult.Busy b => $"wsl-care act: busy - {b.Reason}",
        ActResult.Wedged w => $"wsl-care act: wedged - {w.Reason}",
        _ => throw new System.Diagnostics.UnreachableException("ActResult is a closed set"),
    };

    private static string Line(ActionOutcome o)
    {
        var figures = o.Run is { } run
            ? Invariant($"{run.Count} removed, freed {Gb(run.FreedBytes)} (measured: {run.FreedBasis})")
            : o.Preview is { Available: true } p ? Invariant($"{p.Count} objects, {Gb(p.Bytes)}") : string.Empty;
        return $"  {o.Id,-17} {o.Status,-9} {(figures.Length > 0 ? figures + " - " : string.Empty)}{o.Reason}";
    }

    private static string User(TargetUserReport user) => user.Found ? $"{user.Name} ({user.Source})" : user.Source;

    private static string Gb(long? bytes) => bytes is { } b ? Invariant($"{b / BytesPerGigabyte:0.00} GB") : "size unknown";

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
