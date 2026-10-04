using System.Text.Json;

using Serilog;

using WslCare.Core.Actions;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Collect;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Json;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Records;
using WslCare.Core.Status;
using WslCare.Core.Systemd;

namespace WslCare.Cli.Commands;

/// <summary>
/// The detached runs of E6.S1 (plan §15j B2, M4, M9; §15k; the E6.S1 review round): <c>act … --confirm --detach</c> and
/// <c>collect --detach</c> write a REQUEST — the persisted queued state — and start the template unit
/// <c>wsl-care-act@&lt;runId&gt;.service</c>, answering <c>accepted</c> at once; the unit's <c>act --request &lt;runId&gt;</c> runs
/// it under that run id. All root; a detach never falls back to a synchronous run. <c>act --stop</c> is <see cref="RunStops"/>.
/// </summary>
/// <remarks>Exit codes: 0 accepted / unknown / recorded · 69 no systemd · 71 the unit would not start (the request removed) ·
/// 73 the request budget is full · 75 / 76 / 79 a run is live or queued / wedged / unreadable — at <c>--request</c> time
/// RECORDED as <c>refused</c> · 77 needs root · 80 no request names the run (a no-op) · 2 usage, or an unusable request (recorded
/// <c>refused</c>, removed).</remarks>
internal static class DetachedRuns
{
    private const string SystemdMarker = "/run/systemd/system";

    // ---------- --detach ----------

    public static int Detach(Request.Act request, RunTrigger trigger, ShownList shown, CliHost host, TextWriter stdout, TextWriter stderr, ILogger log) =>
        Accept(new Asked("act", [.. request.Ids.Select(i => i.Text)], trigger, [.. shown.Names.Order(StringComparer.Ordinal)], request.Json), host, stdout, stderr, log);

    public static int CollectDetach(Request.Collect request, CliHost host, TextWriter stdout, TextWriter stderr, ILogger log) =>
        ActCommand.NotRoot(host) is { } refused
            ? Refuse(stderr, log, refused.Code, $"collect --detach {refused.Message}")
            : Accept(new Asked("collect", ["collect"], RunTrigger.Manual, [], request.Json), host, stdout, stderr, log);

    /// <summary>What a detach asks for.</summary>
    private sealed record Asked(string Kind, IReadOnlyList<string> Actions, RunTrigger Trigger, IReadOnlyList<string> Shown, bool Json);

    /// <summary>systemd asked; then UNDER THE RUN LOCK the request folder swept (review D1: an orphaned request blocks nothing for
    /// longer than its grace), the budget and the running state asked, and the request written EXCLUSIVELY; the lock released
    /// BEFORE the unit is started (its run takes the lock itself); then the start — or the request removed again and the start's
    /// failure named (plan §15k #1).</summary>
    private static int Accept(Asked asked, CliHost host, TextWriter stdout, TextWriter stderr, ILogger log)
    {
        if (NoSystemd(host) is { } none)
        {
            return Refuse(stderr, log, none.Code, none.Message);
        }

        var (written, code, message) = WriteUnderLock(asked, host, log);
        return written is { } runId ? Start(runId, asked, host, stdout, stderr, log) : Refuse(stderr, log, code, message);
    }

    private static (RunId? Written, ExitCode Code, string Message) WriteUnderLock(Asked asked, CliHost host, ILogger log)
    {
        var now = host.Clock.GetUtcNow();
        switch (RunLock.TryTake(host.Paths, host.Files))
        {
            case ExclusiveLock.Busy busy:
                var (busyCode, busyMessage) = InFlight(Report(host, now)) ?? (ExitCode.Busy, $"busy: another run holds the lock ({busy.Reason}); nothing was written");
                return (null, busyCode, busyMessage);
            case ExclusiveLock.Held held:
                using (held.Handle)
                {
                    foreach (var note in RequestSweep.ApplyAsync(host.Paths, host.Files, host.Commands, host.Processes, now, own: null).GetAwaiter().GetResult())
                    {
                        log.Information("detach: {Note}", note);
                    }

                    return (Full(host) ?? InFlight(Report(host, now))) is { } refused ? (null, refused.Code, refused.Message) : Write(asked, host, now);
                }

            default:
                throw new System.Diagnostics.UnreachableException("ExclusiveLock is a closed set");
        }
    }

    /// <summary>The request, stamped with the boot and the monotonic clock (review D3) so the sweep ages it by a clock no step moves.</summary>
    private static (RunId? Written, ExitCode Code, string Message) Write(Asked asked, CliHost host, DateTimeOffset now)
    {
        var runId = RunId.New(now, Environment.ProcessId);
        var boot = host.Processes.Boot();
        var request = new RunRequestFile(Core.SchemaVersion.Current, runId, asked.Kind, asked.Actions, asked.Trigger, now)
        {
            Shown = asked.Shown,
            BootId = boot.Known ? boot.BootId : string.Empty,
            CreatedMonotonicMs = boot.Known ? boot.MonotonicMilliseconds : 0,
        };
        return RunRequests.Create(host.Paths, host.Files, request) switch
        {
            ExclusiveCreate.Created => (runId, ExitCode.Ok, string.Empty),
            ExclusiveCreate.AlreadyExists => (null, ExitCode.Busy, $"a request of run {runId} exists already; try again"),
            ExclusiveCreate.Refused r => (null, ExitCode.RunFailed, $"the request could not be written: {r.Reason}; nothing was started"),
            _ => throw new System.Diagnostics.UnreachableException("ExclusiveCreate is a closed set"),
        };
    }

    private static RunningReport Report(CliHost host, DateTimeOffset now) =>
        RunningReports.Read(host.Paths, host.Files, host.Processes, now, RunningReadRetry.Default, RunHistory.Read(host.Paths, host.Files));

    private static (ExitCode Code, string Message)? NoSystemd(CliHost host) =>
        host.Paths is LinuxHostPaths linux && host.Files.DirectoryExists(linux.DistroPath(SystemdMarker))
            ? null
            : (ExitCode.DetachUnavailable, $"needs systemd: a detached run is started as its own unit and {SystemdMarker} does not exist here; there is no synchronous fallback (plan 15j B2) - nothing was written");

    /// <summary>One root operation at a time: a run acting or queued answers busy, a wedged or uninspectable one wedged, an
    /// unreadable state its own code (plan §15j B2).</summary>
    internal static (ExitCode Code, string Message)? InFlight(RunningReport running) => running.State switch
    {
        RunningStateName.Live or RunningStateName.Queued => (ExitCode.Busy, $"busy: {running.Reason}; nothing was written"),
        RunningStateName.Wedged or RunningStateName.Unknown => (ExitCode.Wedged, $"{running.Reason}; nothing was written"),
        RunningStateName.Unreadable => (ExitCode.StateUnreadable, $"{running.Reason}; nothing was written"),
        _ => null,
    };

    private static (ExitCode Code, string Message)? Full(CliHost host) =>
        RunRequests.Count(host.Paths, host.Files) >= RunRequests.MaxQueued
            ? (ExitCode.QueueFull, $"the request folder already holds {RunRequests.MaxQueued} requests (the budget, plan 15k #8); nothing was written")
            : null;

    /// <summary>The start. A refusal or a <c>systemctl</c> that never ran is certain: the request goes, 71. A start that TIMED OUT
    /// may have queued the job (review D6): the unit is asked — busy = accepted; done = 71; unreadable = the outcome is unknown,
    /// the request stays for the sweep and the answer says so (plan §15k #3: the panel follows <c>status.running</c>).</summary>
    private static int Start(RunId runId, Asked asked, CliHost host, TextWriter stdout, TextWriter stderr, ILogger log)
    {
        var started = host.Commands.RunAsync(UnitCommands.Start(runId).ToRequest(), CancellationToken.None).GetAwaiter().GetResult();
        return started switch
        {
            CommandOutcome.Exited { ExitCode: 0 } => Accepted(runId, asked, stdout, log, "accepted", string.Empty),
            CommandOutcome.TimedOut => AfterTimedOutStart(runId, asked, started, host, stdout, stderr, log),
            _ => StartFailed(runId, started, host, stderr, log),
        };
    }

    private static int AfterTimedOutStart(RunId runId, Asked asked, CommandOutcome started, CliHost host, TextWriter stdout, TextWriter stderr, ILogger log) =>
        RequestSweep.UnitStateAsync(host.Commands, runId).GetAwaiter().GetResult() switch
        {
            UnitState.Busy => Accepted(runId, asked, stdout, log, "accepted", $" (systemctl start {Describe(started)}, but systemd holds the job)"),
            UnitState.Unread unread => Accepted(runId, asked, stdout, log, "unknown", $" - but whether it started is UNKNOWN: systemctl start {Describe(started)} and its state could not be read ({unread.Why}); the request stays and the next root run settles it"),
            _ => StartFailed(runId, started, host, stderr, log),
        };

    private static int StartFailed(RunId runId, CommandOutcome started, CliHost host, TextWriter stderr, ILogger log)
    {
        var removed = RunRequests.Remove(host.Paths, host.Files, runId);
        return Refuse(stderr, log, ExitCode.DetachStartFailed, $"systemctl start --no-block {SlotKind.ActUnit.Of(runId)} did not succeed ({Describe(started)}); the request was removed{(removed.Length > 0 ? $" - but {removed}" : string.Empty)}");
    }

    private static int Accepted(RunId runId, Asked asked, TextWriter stdout, ILogger log, string result, string note)
    {
        var unit = SlotKind.ActUnit.Of(runId);
        log.Information("{Result} run {RunId} ({Kind}) in {Unit}{Note}", result, runId.Text, asked.Kind, unit, note);
        return Answer(stdout, new HandOffReport(Core.SchemaVersion.Current, result, asked.Kind, runId.Text, unit), asked.Json, $"wsl-care: {result} run {runId} ({asked.Kind}); it runs in {unit}{note} - follow it with \"wsl-care status\" or \"wsl-care runs show {runId}\"");
    }

    // ---------- act --request ----------

    public static int FromRequest(Request.ActFromRequest request, CliHost host, ConfigLoadResult loaded, TextWriter stdout, TextWriter stderr, ILogger log, CancellationToken cancellationToken)
    {
        if (ActCommand.NotRoot(host) is { } refused)
        {
            return Refuse(stderr, log, refused.Code, refused.Message);
        }

        var runId = RunId.TryParse(request.RunId) ?? throw new System.Diagnostics.UnreachableException("the parser admits a well-formed run id only");
        return RunRequests.Find(host.Paths, host.Files, runId) switch
        {
            null => Refuse(stderr, log, ExitCode.RequestGone, $"no request names run {runId} (swept, or a stray start): nothing to do"),
            RunRequestRead.Bad bad => Unusable(host, runId, bad, stderr, log),
            RunRequestRead.Parsed when AlreadyRecorded(host, runId) => LeftBehind(host, runId, stderr, log),
            RunRequestRead.Parsed parsed when parsed.File.Kind == "collect" => Collect(parsed.File, host, loaded, stdout, stderr, log, cancellationToken),
            RunRequestRead.Parsed parsed => Act(parsed.File, host, loaded, stdout, stderr, log, cancellationToken),
            _ => throw new System.Diagnostics.UnreachableException("RunRequestRead is a closed set"),
        };
    }

    /// <summary>A request the hardened reader refuses (review D5): recorded <c>refused</c> with why, then removed — it must not hold
    /// the state <c>unreadable</c> and refuse every detach forever. Nothing is run.</summary>
    private static int Unusable(CliHost host, RunId runId, RunRequestRead.Bad bad, TextWriter stderr, ILogger log)
    {
        var note = RequestSweep.Unusable(host.Paths, host.Files, host.Clock.GetUtcNow(), runId, bad.Why);
        return Refuse(stderr, log, ExitCode.Usage, $"the request {bad.Path} cannot be used: {bad.Why}; nothing was run ({note})");
    }

    /// <summary>History first for its OWN request too (plan §15k #2): a run that recorded itself and died before removing its
    /// request has a terminal line already — it is never run a second time under the same id.</summary>
    private static bool AlreadyRecorded(CliHost host, RunId runId) =>
        RunHistory.Read(host.Paths, host.Files).Records.Any(r => r.RunId == runId);

    private static int LeftBehind(CliHost host, RunId runId, TextWriter stderr, ILogger log)
    {
        var removed = RunRequests.Remove(host.Paths, host.Files, runId);
        return Refuse(stderr, log, ExitCode.RequestGone, $"run {runId} has already recorded itself; the request it left behind was removed{(removed.Length > 0 ? $" - but {removed}" : string.Empty)}: nothing to do");
    }

    private static int Act(RunRequestFile file, CliHost host, ConfigLoadResult loaded, TextWriter stdout, TextWriter stderr, ILogger log, CancellationToken cancellationToken)
    {
        var ids = file.Actions.Select(a => ActionId.Find(a)!).ToList();
        if (loaded.IsObserveOnly)
        {
            return Refused(file, host, stderr, log, ExitCode.ObserveOnly, ActionEngine.ObserveOnlyReason);
        }

        var engine = new ActionEngine(new EngineContext(host.Paths, host.Files, host.Commands, host.Clock, host.Probe, loaded, host.Processes, Environment.ProcessId, host.Actions)
        {
            Signals = host.Signals,
            InterruptCause = host.InterruptCause,
        });
        var act = new ActRequest(ids, file.Trigger, Execute: true)
        {
            RunId = file.RunId,
            ShownVolumes = file.Shown.Count > 0 ? ShownList.Of(file.Shown) : ShownList.None,
            OnRunningWritten = () => RunRequests.Remove(host.Paths, host.Files, file.RunId),
            UnderLock = (own, _) => RequestSweep.ApplyAsync(host.Paths, host.Files, host.Commands, host.Processes, host.Clock.GetUtcNow(), own),
        };
        var result = Executed(() => engine.ExecuteAsync(act, cancellationToken).GetAwaiter().GetResult(), file, host, cancellationToken);
        ActCommand.Log(log, result);
        return result switch
        {
            ActResult.Busy busy => Refused(file, host, stderr, log, ExitCode.Busy, $"busy: {busy.Reason}"),
            ActResult.Wedged wedged => Refused(file, host, stderr, log, ExitCode.Wedged, wedged.Reason),
            ActResult.StateUnreadable unreadable => Refused(file, host, stderr, log, ExitCode.StateUnreadable, unreadable.Reason),
            _ => Finished(result, host, file, stdout, stderr),
        };
    }

    /// <summary>The run — and when a cancellation cuts it off, the run's record kept (review D2): a run that recorded itself only
    /// loses its request; one cut off before it recorded anything gets ONE <c>interrupted</c> line first. The request was the
    /// accepted run's only trace, and removing it bare made <c>runs show</c> answer <c>unknown</c> for a run the panel had been
    /// told was accepted.</summary>
    private static T Executed<T>(Func<T> run, RunRequestFile file, CliHost host, CancellationToken cancellationToken)
    {
        try
        {
            return run();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CutOff(file, host);
            throw;
        }
    }

    private static void CutOff(RunRequestFile file, CliHost host)
    {
        try
        {
            if (!AlreadyRecorded(host, file.RunId))
            {
                var now = host.Clock.GetUtcNow();
                new RunRecordWriter(host.Paths, host.Files).Append(new RunRecord(Core.SchemaVersion.Current, file.RunId, file.Trigger, now, now, RunOutcome.Interrupted, [.. file.Actions.Select(a => new ActionRecord(a, 0, 0) { Status = ActionStatus.Interrupted })])
                {
                    Reason = $"interrupted by {host.InterruptCause()} before it recorded anything (cut off before it started)",
                });
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or TimeoutException)
        {
            // The cancellation still flies; the request is left for the sweep, which records the run interrupted.
            return;
        }

        RunRequests.Remove(host.Paths, host.Files, file.RunId);
    }

    private static int Finished(ActResult result, CliHost host, RunRequestFile file, TextWriter stdout, TextWriter stderr)
    {
        RunRequests.Remove(host.Paths, host.Files, file.RunId);
        Output.Answer(stdout, ActCommand.Answer(result, json: true));
        return ActCommand.Exit(result, stderr);
    }

    private static int Collect(RunRequestFile file, CliHost host, ConfigLoadResult loaded, TextWriter stdout, TextWriter stderr, ILogger log, CancellationToken cancellationToken)
    {
        var context = new CollectContext(host.Paths, host.Files, host.Commands, host.Clock, host.Probe, loaded, Environment.ProcessId, file.Trigger)
        {
            Actions = host.Actions,
            Processes = host.Processes,
            Signals = host.Signals,
            RunId = file.RunId,
            OnRunningWritten = () => RunRequests.Remove(host.Paths, host.Files, file.RunId),
            InterruptCause = host.InterruptCause,
        };
        var result = Executed(() => CollectRun.RunAsync(context, cancellationToken).GetAwaiter().GetResult(), file, host, cancellationToken);
        RunRequests.Remove(host.Paths, host.Files, file.RunId);
        if (result.Recording == Recording.Busy)
        {
            return Refused(file, host, stderr, log, ExitCode.Busy, result.Reason);
        }

        Output.Answer(stdout, JsonSerializer.Serialize(CollectCommand.Report(result), WslCareJsonContext.Default.CollectReport));
        return result.Recording == Recording.Failed ? (int)ExitCode.RunFailed : (int)ExitCode.Ok;
    }

    /// <summary>A detached run that may not start now: ONE terminal history line with the outcome <c>refused</c> and why (never
    /// a silent busy, plan §15j B2), then its request goes; the exit code says which refusal it was.</summary>
    private static int Refused(RunRequestFile file, CliHost host, TextWriter stderr, ILogger log, ExitCode code, string reason)
    {
        var now = host.Clock.GetUtcNow();
        try
        {
            new RunRecordWriter(host.Paths, host.Files).Append(new RunRecord(Core.SchemaVersion.Current, file.RunId, file.Trigger, now, now, RunOutcome.Refused, [.. file.Actions.Select(a => new ActionRecord(a, 0, 0) { Status = ActionStatus.Refused })])
            {
                Reason = reason,
            });
        }
        finally
        {
            RunRequests.Remove(host.Paths, host.Files, file.RunId);
        }

        return Refuse(stderr, log, code, $"run {file.RunId} refused (recorded): {reason}");
    }

    // ---------- shared with RunStops ----------

    internal static int Answer(TextWriter stdout, HandOffReport report, bool json, string text) =>
        Output.Answer(stdout, json ? JsonSerializer.Serialize(report with { ProductVersion = Program.VersionText }, WslCareJsonContext.Default.HandOffReport) : text);

    internal static int Refuse(TextWriter stderr, ILogger log, ExitCode code, string message)
    {
        log.Warning("refused: {Reason}", message);
        Output.Note(stderr, message);
        return (int)code;
    }

    internal static string Describe(CommandOutcome outcome) => outcome switch
    {
        CommandOutcome.Exited exited => $"exit {exited.ExitCode}: {exited.Stderr.Text.Trim()}",
        CommandOutcome.TimedOut timedOut => $"timed out after {timedOut.Timeout.TotalSeconds:0} s",
        CommandOutcome.FailedToStart failed => failed.Reason,
        CommandOutcome.Refused refused => $"refused by the command policy: {refused.Reason}",
        _ => throw new System.Diagnostics.UnreachableException("CommandOutcome is a closed set"),
    };
}
