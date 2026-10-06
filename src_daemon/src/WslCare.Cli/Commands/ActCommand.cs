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
/// <para>The trigger is <c>timer</c> with <c>--timer</c> (the timer unit's mark, as for <c>collect</c>; never <c>INVOCATION_ID</c>) — then the <c>auto</c>
/// switches, the triggers and the 7-day dry run apply — <c>manual</c> when the panel's button passes <c>--manual</c> (E3.S2;
/// the two marks are exclusive since E6.S0, plan §15j m2 — the parser refuses both), and <c>cli</c> otherwise. A4 on a
/// <c>manual</c> run removes only the volumes passed with <c>--volume</c> / <c>--only</c> — what its preview SHOWED, every one of
/// them in the preview's <c>shown</c> (§15j B1) — and refuses without them.</para>
/// <para>Every answer names <c>productVersion</c> (§15f #3). A run cut off by a signal records itself <c>interrupted</c> with the
/// signal named — SIGHUP included, which a terminal or the <c>wsl.exe</c> relay delivers when it goes away (§15j B2).</para>
/// </remarks>
internal static class ActCommand
{
    /// <summary>The largest <c>--only</c> file read: the same cap as the stdin list (<c>act.maxListBytes</c>).</summary>
    private static int MaxOnlyFileBytes => StdinList.MaxBytes;

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

        if (request.Detach)
        {
            return DetachedRuns.Detach(request, Trigger(request), shown.List, ShownProcesses(request), host, stdout, stderr, log);
        }

        var engine = new ActionEngine(new EngineContext(host.Paths, host.Files, host.Commands, host.Clock, host.Probe, loaded, host.Processes, Environment.ProcessId, host.Actions)
        {
            Signals = host.Signals,
            InterruptCause = host.InterruptCause,
        });
        var act = new ActRequest(request.Ids, Trigger(request), request.Confirm) { ShownVolumes = shown.List, ShownProcesses = ShownProcesses(request) };
        // A console program has no synchronisation context; blocking here is the verb's whole job.
        var result = Dispatch(engine, act, cancellationToken).GetAwaiter().GetResult();
        Log(log, result);
        Output.Answer(stdout, Answer(result, request.Json));
        return Exit(result, stderr);
    }

    private static Task<ActResult> Dispatch(ActionEngine engine, ActRequest act, CancellationToken cancellationToken) =>
        act.Execute ? engine.ExecuteAsync(act, cancellationToken) : engine.PreviewAsync(act, cancellationToken);

    internal static string Answer(ActResult result, bool json) =>
        json ? JsonSerializer.Serialize(ActReport.From(result) with { ProductVersion = Program.VersionText }, WslCareJsonContext.Default.ActReport) : ActText.Render(result);

    /// <summary>A refusal of the whole request and its code; <c>null</c> when it may go on. Root FIRST: nothing else is
    /// asked of an unprivileged process (plan §15c #0).</summary>
    private static (ExitCode Code, string Message)? Refusal(Request.Act request, CliHost host, ConfigLoadResult loaded) =>
        NotRoot(host) ?? Unbuilt(request, host) ?? OtherSide(request, host) ?? ObserveOnly(request, loaded);

    internal static (ExitCode Code, string Message)? NotRoot(CliHost host) =>
        host.Privilege.IsRoot ? null : (ExitCode.NeedsRoot, $"needs root: every act runs as root (plan 15c #0) and {host.Privilege.Basis}; nothing was done");

    private static (ExitCode Code, string Message)? Unbuilt(Request.Act request, CliHost host)
    {
        var unbuilt = request.Ids.Where(id => host.Actions.Find(id) is null).ToList();
        return unbuilt.Count > 0
            ? (ExitCode.Usage, $"{string.Join(", ", unbuilt)} {(unbuilt.Count == 1 ? "is" : "are")} not built in this release; act holds: {string.Join(", ", host.Actions.Actions.Select(a => a.Id.Text))}")
            : null;
    }

    private static (ExitCode Code, string Message)? OtherSide(Request.Act request, CliHost host)
    {
        var otherSide = request.Ids.Select(id => host.Actions.Find(id)!).Where(a => !a.Sides.Contains(host.Paths.Side)).ToList();
        return otherSide.Count > 0
            ? (ExitCode.Usage, $"{string.Join(", ", otherSide.Select(a => a.Id))} {(otherSide.Count == 1 ? "runs" : "run")} inside the WSL distro, not on this side (the {SideName(host)} binary)")
            : null;
    }

    private static string SideName(CliHost host) => host.Paths.Side == Core.Hosting.HostSide.Wsl ? "WSL" : "Windows";

    private static (ExitCode Code, string Message)? ObserveOnly(Request.Act request, ConfigLoadResult loaded) =>
        request.Confirm && loaded.IsObserveOnly ? (ExitCode.ObserveOnly, ActionEngine.ObserveOnlyReason) : null;

    private static RunTrigger Trigger(Request.Act request) =>
        request.Timer ? RunTrigger.Timer
        : request.Manual ? RunTrigger.Manual
        : RunTrigger.Cli;

    /// <summary>The processes A18's preview showed (<c>--process</c>, E7.S2b review A-H1); none given = none.</summary>
    private static ShownList ShownProcesses(Request.Act request) => request.Processes.Count > 0 ? ShownList.Of(request.Processes) : ShownList.None;

    /// <summary>The volumes A4's preview showed, from <c>--volume</c> and the <c>--only</c> file together — or why the file
    /// cannot be used (it is read as root: its content is validated line by line and never echoed).</summary>
    private static (ShownList List, string Failure) ShownVolumes(Request.Act request, CliHost host)
    {
        if (!request.HasShownList)
        {
            return (ShownList.None, string.Empty);
        }

        return request.OnlyFile.Length == 0 ? (ShownList.Of(request.Volumes), string.Empty) : WithOnlyFile(request, host);
    }

    /// <summary>The <c>--volume</c> names with those of the <c>--only</c> file — read as root, validated line by line.</summary>
    private static (ShownList List, string Failure) WithOnlyFile(Request.Act request, CliHost host)
    {
        // Read as ROOT: a regular file only (a FIFO or a device is refused, never waited on) and never past the cap, whatever
        // its length claims (independent review of E3, 2026-10-03) — or, with "--only -", stdin under the same cap and a time
        // ceiling (E6.S1, §15j M2).
        var read = request.ShownOnStdin ? StdinList.Read(host.StandardInput(), MaxOnlyFileBytes, host.StdinCeiling) : host.Files.ReadRegularFile(request.OnlyFile, MaxOnlyFileBytes);
        if (read is not FileReadResult.Content content)
        {
            return (ShownList.None, $"act: {(request.ShownOnStdin ? "the shown list on stdin" : $"the --only file {CommandLine.Printable(request.OnlyFile)}")} {CommandLine.Printable(Unusable(read))}; nothing was done");
        }

        var (names, failure) = CommandLine.ShownVolumesFile(Encoding.UTF8.GetString(content.Bytes));
        return failure.Length > 0
            ? (ShownList.None, $"act: {failure}; nothing was done")
            : (ShownList.Of(names.Concat(request.Volumes)), string.Empty);
    }

    private static string Unusable(FileReadResult read) => read switch
    {
        FileReadResult.Unreadable unreadable => $"cannot be used: {unreadable.Reason}",
        _ => "is missing",
    };

    internal static int Exit(ActResult result, TextWriter stderr)
    {
        var (code, note) = Verdict(result);
        if (note.Length > 0)
        {
            Output.Note(stderr, note);
        }

        return (int)code;
    }

    /// <summary>The exit code of a result, and the line stderr says about it (none for a clean run).</summary>
    private static (ExitCode Code, string Note) Verdict(ActResult result) => result switch
    {
        ActResult.Busy busy => (ExitCode.Busy, $"busy: {busy.Reason}"),
        ActResult.Wedged wedged => (ExitCode.Wedged, wedged.Reason),
        ActResult.StateUnreadable unreadable => (ExitCode.StateUnreadable, unreadable.Reason),
        ActResult.Done { Recording: not Recording.Recorded } done => (ExitCode.RunFailed, $"the run was not recorded: {done.Reason}"),
        ActResult.Done done when done.Detail.Actions.Any(a => a.Status == ActionStatus.Failed) =>
            (ExitCode.ActionFailed, $"failed: {string.Join("; ", done.Detail.Actions.Where(a => a.Status == ActionStatus.Failed).Select(a => $"{a.Id}: {a.Reason}"))}"),
        _ => (ExitCode.Ok, string.Empty),
    };

    internal static void Log(ILogger log, ActResult result)
    {
        foreach (var outcome in Outcomes(result))
        {
            Logging.OutcomeLog.Log(log, outcome);
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
        ActResult.StateUnreadable u => $"wsl-care act: running state unreadable - {u.Reason}",
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
