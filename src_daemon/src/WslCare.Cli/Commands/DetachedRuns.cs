using System.Text.Json;

using Serilog;

using WslCare.Core.Actions;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Collect;
using WslCare.Core.Collectors.Procfs;
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
/// The detached runs of E6.S1 (plan §15j B2, M4, M9; §15k): <c>act … --confirm --detach</c> and <c>collect --detach</c> write a
/// REQUEST — the persisted queued state — and start the template unit <c>wsl-care-act@&lt;runId&gt;.service</c>, answering
/// <c>accepted</c> at once; the unit's <c>act --request &lt;runId&gt;</c> runs it under that run id; <c>act --stop &lt;runId&gt;</c>
/// asks systemd to stop a WEDGED run hosted by one of the units. All root; nothing here kills by pid, and a detach never falls
/// back to a synchronous run.
/// </summary>
/// <remarks>Exit codes: 0 accepted / recorded / stopping · 69 no systemd · 71 the unit would not start (the request removed) ·
/// 73 the request budget is full · 75 / 76 / 79 a run is live or queued / wedged / unreadable — at <c>--request</c> time
/// RECORDED as <c>refused</c> · 77 needs root · 80 no request names the run (a no-op) · 2 usage, or nothing to stop.</remarks>
internal static class DetachedRuns
{
    private const string SystemdMarker = "/run/systemd/system";

    // ---------- --detach ----------

    public static int Detach(Request.Act request, RunTrigger trigger, ShownList shown, CliHost host, TextWriter stdout, TextWriter stderr, ILogger log) =>
        Accept("act", [.. request.Ids.Select(i => i.Text)], trigger, [.. shown.Names.Order(StringComparer.Ordinal)], request.Json, host, stdout, stderr, log);

    public static int CollectDetach(Request.Collect request, CliHost host, TextWriter stdout, TextWriter stderr, ILogger log) =>
        ActCommand.NotRoot(host) is { } refused
            ? Refuse(stderr, log, refused.Code, $"collect --detach {refused.Message}")
            : Accept("collect", ["collect"], RunTrigger.Manual, [], request.Json, host, stdout, stderr, log);

    /// <summary>systemd, the running state and the budget asked; the request written EXCLUSIVELY; the unit started — or the
    /// request removed again and the start's failure named (plan §15k #1).</summary>
    private static int Accept(string kind, IReadOnlyList<string> actions, RunTrigger trigger, IReadOnlyList<string> shown, bool json, CliHost host, TextWriter stdout, TextWriter stderr, ILogger log)
    {
        var now = host.Clock.GetUtcNow();
        if (Unavailable(host, now) is { } refused)
        {
            return Refuse(stderr, log, refused.Code, refused.Message);
        }

        var runId = RunId.New(now, Environment.ProcessId);
        var write = RunRequests.Create(host.Paths, host.Files, new RunRequestFile(Core.SchemaVersion.Current, runId, kind, actions, trigger, now) { Shown = shown });
        return write switch
        {
            ExclusiveCreate.Created => Start(runId, kind, json, host, stdout, stderr, log),
            ExclusiveCreate.AlreadyExists => Refuse(stderr, log, ExitCode.Busy, $"a request of run {runId} exists already; try again"),
            ExclusiveCreate.Refused r => Refuse(stderr, log, ExitCode.RunFailed, $"the request could not be written: {r.Reason}; nothing was started"),
            _ => throw new System.Diagnostics.UnreachableException("ExclusiveCreate is a closed set"),
        };
    }

    /// <summary>Why no request may be written now; <c>null</c> when one may.</summary>
    private static (ExitCode Code, string Message)? Unavailable(CliHost host, DateTimeOffset now) =>
        NoSystemd(host) ?? Full(host) ?? InFlight(RunningReports.Read(host.Paths, host.Files, host.Processes, now, RunningReadRetry.Default, RunHistory.Read(host.Paths, host.Files)));

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

    private static int Start(RunId runId, string kind, bool json, CliHost host, TextWriter stdout, TextWriter stderr, ILogger log)
    {
        var started = host.Commands.RunAsync(UnitCommands.Start(runId).ToRequest(), CancellationToken.None).GetAwaiter().GetResult();
        if (started is not CommandOutcome.Exited { ExitCode: 0 })
        {
            var removed = RunRequests.Remove(host.Paths, host.Files, runId);
            return Refuse(stderr, log, ExitCode.DetachStartFailed, $"systemctl start --no-block {SlotKind.ActUnit.Of(runId)} did not succeed ({Describe(started)}); the request was removed{(removed.Length > 0 ? $" - but {removed}" : string.Empty)}");
        }

        log.Information("accepted run {RunId} ({Kind}) in {Unit}", runId.Text, kind, SlotKind.ActUnit.Of(runId));
        return Answer(stdout, new HandOffReport(Core.SchemaVersion.Current, "accepted", kind, runId.Text, SlotKind.ActUnit.Of(runId)), json, $"wsl-care: accepted run {runId} ({kind}); it runs in {SlotKind.ActUnit.Of(runId)} - follow it with \"wsl-care status\" or \"wsl-care runs show {runId}\"");
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
            RunRequestRead.Bad bad => Refuse(stderr, log, ExitCode.Usage, $"the request {bad.Path} cannot be used: {bad.Why}; nothing was run"),
            RunRequestRead.Parsed when AlreadyRecorded(host, runId) => LeftBehind(host, runId, stderr, log),
            RunRequestRead.Parsed parsed when parsed.File.Kind == "collect" => Collect(parsed.File, host, loaded, stdout, stderr, log, cancellationToken),
            RunRequestRead.Parsed parsed => Act(parsed.File, host, loaded, stdout, stderr, log, cancellationToken),
            _ => throw new System.Diagnostics.UnreachableException("RunRequestRead is a closed set"),
        };
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
            UnderLock = (own, token) => RequestSweep.ApplyAsync(host.Paths, host.Files, host.Commands, host.Clock.GetUtcNow(), own, token),
        };
        var result = Executed(engine, act, file, host, cancellationToken);
        ActCommand.Log(log, result);
        return result switch
        {
            ActResult.Busy busy => Refused(file, host, stderr, log, ExitCode.Busy, $"busy: {busy.Reason}"),
            ActResult.Wedged wedged => Refused(file, host, stderr, log, ExitCode.Wedged, wedged.Reason),
            ActResult.StateUnreadable unreadable => Refused(file, host, stderr, log, ExitCode.StateUnreadable, unreadable.Reason),
            _ => Finished(result, host, file, stdout, stderr),
        };
    }

    /// <summary>The engine's run — and the request removed on EVERY way out, a cancellation included (a terminal path).</summary>
    private static ActResult Executed(ActionEngine engine, ActRequest act, RunRequestFile file, CliHost host, CancellationToken cancellationToken)
    {
        try
        {
            return engine.ExecuteAsync(act, cancellationToken).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            RunRequests.Remove(host.Paths, host.Files, file.RunId);
            throw;
        }
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
        CollectResult result;
        try
        {
            result = CollectRun.RunAsync(context, cancellationToken).GetAwaiter().GetResult();
        }
        finally
        {
            RunRequests.Remove(host.Paths, host.Files, file.RunId);
        }

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

    // ---------- act --stop ----------

    public static int Stop(Request.ActStop request, CliHost host, TextWriter stdout, TextWriter stderr, ILogger log, CancellationToken cancellationToken)
    {
        if (ActCommand.NotRoot(host) is { } refused)
        {
            return Refuse(stderr, log, refused.Code, refused.Message);
        }

        var runId = RunId.TryParse(request.RunId) ?? throw new System.Diagnostics.UnreachableException("the parser admits a well-formed run id only");
        return RunningState.Read(host.Paths, host.Files, host.Processes, host.Clock.GetUtcNow()) switch
        {
            RunningStatus.Wedged wedged when wedged.File.RunId == runId => StopWedged(wedged.File, request.Json, host, stdout, stderr, log, cancellationToken),
            RunningStatus.Live live when live.File.RunId == runId => Refuse(stderr, log, ExitCode.Busy, $"run {runId} is acting, not wedged: its heartbeat is fresh - let it finish"),
            var other => Refuse(stderr, log, ExitCode.Usage, $"run {runId} is not wedged here ({Describe(other)}): nothing to stop"),
        };
    }

    /// <summary>Only a process living in <c>wsl-care.service</c> or THIS run's own unit is stopped, and only through systemd
    /// (SIGTERM, then SIGKILL after <c>TimeoutStopSec=90</c>); anything else is text with its pid — never a kill by pid.</summary>
    private static int StopWedged(RunningFile file, bool json, CliHost host, TextWriter stdout, TextWriter stderr, ILogger log, CancellationToken cancellationToken)
    {
        var unit = HostingUnit(host, file.Pid);
        if (unit != UnitCommands.TimerService && unit != SlotKind.ActUnit.Of(file.RunId))
        {
            return Refuse(stderr, log, ExitCode.Usage, $"run {file.RunId} is wedged, but its process (pid {file.Pid}) is not in wsl-care.service nor in {SlotKind.ActUnit.Of(file.RunId)} ({(unit.Length > 0 ? unit : "its cgroup could not be read")}): nothing was stopped - stop pid {file.Pid} by hand if that is safe");
        }

        StopMarkers.Mark(host.Paths, host.Files, file.RunId, unit, host.Clock.GetUtcNow());
        var stopped = host.Commands.RunAsync(UnitCommands.Stop(unit).ToRequest(), cancellationToken).GetAwaiter().GetResult();
        if (stopped is not CommandOutcome.Exited { ExitCode: 0 })
        {
            StopMarkers.Remove(host.Paths, host.Files, file.RunId);
            return Refuse(stderr, log, ExitCode.RunFailed, $"systemctl stop {unit} did not succeed ({Describe(stopped)})");
        }

        log.Information("asked systemd to stop {Unit} for the wedged run {RunId}", unit, file.RunId.Text);
        return Answer(stdout, new HandOffReport(Core.SchemaVersion.Current, "stopping", "stop", file.RunId.Text, unit), json, $"wsl-care: asked systemd to stop {unit}; run {file.RunId} records itself interrupted, or the next root run records why it could not");
    }

    /// <summary>The unit whose cgroup holds <paramref name="pid"/> — the last part of its <c>/proc/[pid]/cgroup</c> path; empty when unread.</summary>
    private static string HostingUnit(CliHost host, int pid)
    {
        if (host.Paths is not LinuxHostPaths linux)
        {
            return string.Empty;
        }

        var cgroup = host.Files.ReadFile(linux.Rules.Join(linux.ProcRoot, pid.ToString(System.Globalization.CultureInfo.InvariantCulture), "cgroup")) is FileReadResult.Content content
            ? ProcCgroup.Parse(System.Text.Encoding.UTF8.GetString(content.Bytes))
            : string.Empty;
        return cgroup.Length > 0 ? cgroup[(cgroup.LastIndexOf('/') + 1)..] : string.Empty;
    }

    // ---------- shared ----------

    private static int Answer(TextWriter stdout, HandOffReport report, bool json, string text) =>
        Output.Answer(stdout, json ? JsonSerializer.Serialize(report with { ProductVersion = Program.VersionText }, WslCareJsonContext.Default.HandOffReport) : text);

    private static int Refuse(TextWriter stderr, ILogger log, ExitCode code, string message)
    {
        log.Warning("refused: {Reason}", message);
        Output.Note(stderr, message);
        return (int)code;
    }

    private static string Describe(CommandOutcome outcome) => outcome switch
    {
        CommandOutcome.Exited exited => $"exit {exited.ExitCode}: {exited.Stderr.Text.Trim()}",
        CommandOutcome.TimedOut timedOut => $"timed out after {timedOut.Timeout.TotalSeconds:0} s",
        CommandOutcome.FailedToStart failed => failed.Reason,
        CommandOutcome.Refused refused => $"refused by the command policy: {refused.Reason}",
        _ => throw new System.Diagnostics.UnreachableException("CommandOutcome is a closed set"),
    };

    private static string Describe(RunningStatus status) => status switch
    {
        RunningStatus.None => "no run holds running.json",
        RunningStatus.Live live => $"run {live.File.RunId} is acting",
        RunningStatus.Wedged wedged => $"run {wedged.File.RunId} is the wedged one",
        RunningStatus.Dead dead => $"run {dead.File.RunId} is dead: {dead.Why}",
        RunningStatus.Unknown unknown => unknown.Reason,
        RunningStatus.Unreadable unreadable => unreadable.Reason,
        _ => throw new System.Diagnostics.UnreachableException("RunningStatus is a closed set"),
    };
}
