using System.Text.Json;

using WslCare.Core.Collect;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Json;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Records;

namespace WslCare.Core.Actions.Engine;

/// <summary>Everything the engine reaches the machine through.</summary>
/// <param name="ProcessId">This process — what <c>running.json</c> records.</param>
public sealed record EngineContext(
    IHostPaths Paths,
    IFileSystem Files,
    ICommandRunner Commands,
    TimeProvider Clock,
    IHostProbe Probe,
    ConfigLoadResult Loaded,
    IProcessTable Processes,
    int ProcessId,
    ActionRegistry Registry)
{
    /// <summary>How often <c>running.json</c> is rewritten (plan §6: 5 s; a test shortens it).</summary>
    public TimeSpan HeartbeatPeriod { get; init; } = RunningState.HeartbeatPeriod;

    /// <summary>How A11 signals a process (by pid and start, never by name). Refuses unless the CLI wires the real one —
    /// which it does only outside a sandbox, on Linux.</summary>
    public IProcessSignals Signals { get; init; } = RefusingProcessSignals.NotWired;

    /// <summary>The wait an action may take (A11's CPU window); real time unless a test passes its own.</summary>
    public Func<TimeSpan, CancellationToken, Task> Wait { get; init; } = static (delay, token) => Task.Delay(delay, token);
}

/// <summary>
/// The action engine (plan §5, §6, §15 #6, §15a #0, §15c; E3.S1). Execution: the run lock → the
/// <c>running.json</c> sweep (dead or mismatched → swept with an <c>interrupted</c> record; live → busy; stale on a live
/// process → wedged, nothing killed) and the run-record reconcile → the target user → the dry-run decision → then, for
/// each action in <see cref="ActionId.ExecutionOrder"/>: side, observe-only, the <c>auto</c> switch (timer), the LIVE
/// preview, the trigger (timer), the target user (user-scoped), the preview's own refusal, the idle gate, dry run →
/// run → the measured result. A failing action is recorded and the run goes on. Then the detail, the history line, and
/// <c>running.json</c> removed.
/// </summary>
/// <remarks>
/// <para>The engine never trusts a persisted list across a crash (plan §15a #0): every action previews from live state at
/// run time, and a run that died is swept, never resumed.</para>
/// <para>Root is the caller's to check (<c>act</c> refuses whole before it gets here, plan §15c #0); the engine itself only
/// writes through <see cref="IFileSystem"/> and runs through <see cref="ICommandRunner"/>.</para>
/// </remarks>
public sealed class ActionEngine(EngineContext c)
{
    public const string ObserveOnlyReason = "observe-only: a configuration layer is invalid, so no action runs (plan §15a #1); \"wsl-care config get\" names it";

    /// <summary><c>act --preview</c>: each action's LIVE preview — no lock, no state touched, nothing run but reads.</summary>
    public async Task<ActResult> PreviewAsync(ActRequest request, CancellationToken cancellationToken)
    {
        var target = DiscoverTarget();
        var context = Context(request, target);
        var outcomes = new List<ActionOutcome>();
        foreach (var action in c.Registry.InExecutionOrder(request.Ids))
        {
            outcomes.Add(await PreviewOneAsync(action, context, target, cancellationToken).ConfigureAwait(false));
        }

        return new ActResult.Previewed(outcomes, TargetUserReport.From(target));
    }

    /// <summary>The run: refused at once (never waiting) when another run holds the lock.</summary>
    public async Task<ActResult> ExecuteAsync(ActRequest request, CancellationToken cancellationToken)
    {
        switch (RunLock.TryTake(c.Paths, c.Files))
        {
            case ExclusiveLock.Busy busy:
                return WhileLocked(busy.Reason);
            case ExclusiveLock.Held held:
                using (held.Handle)
                {
                    return await UnderLockAsync(request, cancellationToken).ConfigureAwait(false);
                }

            default:
                throw new System.Diagnostics.UnreachableException("ExclusiveLock is a closed set");
        }
    }

    /// <summary>The lock is held by someone else: what <c>running.json</c> says about them (read only).</summary>
    private ActResult WhileLocked(string lockReason) => RunningState.Read(c.Paths, c.Files, c.Processes, c.Clock.GetUtcNow()) switch
    {
        RunningStatus.Wedged w => new ActResult.Wedged(Wedged(w)),
        RunningStatus.Live live => new ActResult.Busy($"run {live.File.RunId} is acting ({live.File.Current}, pid {live.File.Pid}); try again when it ends"),
        _ => new ActResult.Busy($"another run holds the run lock - a full run (collect), most likely; try again when it ends ({lockReason})"),
    };

    private async Task<ActResult> UnderLockAsync(ActRequest request, CancellationToken cancellationToken)
    {
        var started = c.Clock.GetUtcNow();
        var notes = new List<string>();
        if (Sweep(notes, started) is { } refusal)
        {
            return refusal;
        }

        Reconcile(notes, started);
        var runId = RunId.New(started, c.ProcessId);
        var target = DiscoverTarget();
        var dry = DryRunWindow.Decide(request.Trigger, c.Loaded.Config, c.Paths, c.Files, started);
        var running = new RunningFile(Core.SchemaVersion.Current, runId, request.Trigger, [.. request.Ids.Select(i => i.Text)], string.Empty, c.ProcessId, OwnStart(), started, started);
        if (StartRunning(running) is { Length: > 0 } cannot)
        {
            return Record(Detail(runId, request.Trigger, started, dry, target, [], [.. notes, cannot], RunOutcome.Failed));
        }

        var run = new RunState(request.Trigger, target, dry, Context(request, target)) { IdleSource = SampleIdle };
        var (outcomes, outcome) = await ActAllAsync(request, run, running, notes, cancellationToken).ConfigureAwait(false);
        var result = RemoveRunning(Record(Detail(runId, request.Trigger, started, dry, target, outcomes, notes, outcome)));
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    /// <summary>The idle check of plan §5, read once per run and only when an action needs it.</summary>
    private IdleVerdict SampleIdle()
    {
        if (c.Paths is not LinuxHostPaths linux)
        {
            return new IdleVerdict(false, "deferred: CPU use is unknown on the Windows binary");
        }

        // CancellationToken.None: the fast probe reads files only (no process, no wait), and a cancellation is observed
        // by the action loop right after.
        var processes = c.Probe.Sample(CancellationToken.None).Vm.Bind(vm => vm.Processes);
        return IdleGate.Judge(IdleGate.Sample(linux, c.Files, processes, c.Loaded.Config.Int(ConfigKeys.Idle.Minutes)), c.Loaded.Config);
    }

    /// <summary>Every action, under a beating heart; a cancellation ends the loop as <c>interrupted</c> (recorded, then rethrown by the caller).</summary>
    private async Task<(IReadOnlyList<ActionOutcome> Outcomes, RunOutcome Outcome)> ActAllAsync(ActRequest request, RunState run, RunningFile running, List<string> notes, CancellationToken cancellationToken)
    {
        var outcomes = new List<ActionOutcome>();
        var heartbeat = new Heartbeat(c.Paths, c.Files, c.Clock, running, c.HeartbeatPeriod);
        await using (heartbeat.ConfigureAwait(false))
        {
            try
            {
                foreach (var action in c.Registry.InExecutionOrder(request.Ids))
                {
                    heartbeat.Current(action.Id.Text);
                    outcomes.Add(await OneAsync(action, run, cancellationToken).ConfigureAwait(false));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                notes.Add("interrupted by a signal before every action had run");
                return (outcomes, RunOutcome.Interrupted);
            }
        }

        if (heartbeat.Failure.Length > 0)
        {
            notes.Add(heartbeat.Failure);
        }

        return (outcomes, c.Loaded.IsObserveOnly ? RunOutcome.ObserveOnly : RunOutcome.Completed);
    }

    /// <summary>One action through every gate; ANY failure of its own becomes a <c>failed</c> outcome and the run goes on.</summary>
    private async Task<ActionOutcome> OneAsync(ICleanupAction action, RunState run, CancellationToken cancellationToken)
    {
        try
        {
            if (Gate(action, run) is { } early)
            {
                return early;
            }

            var commands = Commands(action, run.Target);
            var preview = await action.PreviewAsync(run.Context, commands, cancellationToken).ConfigureAwait(false);
            if (Judge(action, preview, run) is { } held)
            {
                return held;
            }

            var done = await action.RunAsync(run.Context, preview, commands, cancellationToken).ConfigureAwait(false);
            return Outcome(action, done.Succeeded ? ActionStatus.Ran : ActionStatus.Failed, done.Succeeded ? "ran" : done.Failure, preview, done);
        }
        catch (Exception e) when (!(e is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            return Outcome(action, ActionStatus.Failed, $"{e.GetType().Name}: {e.Message}", null, null);
        }
    }

    /// <summary>A gate's answer: the status and reason that stop the action here, or <c>null</c> to go on.</summary>
    private sealed record Stop(string Status, string Reason);

    /// <summary>The gates asked before any preview, in order: the side, observe-only, the timer's <c>auto</c> switch.</summary>
    private ActionOutcome? Gate(ICleanupAction action, RunState run)
    {
        Func<Stop?>[] gates =
        [
            () => action.Sides.Contains(c.Paths.Side) ? null : new Stop(ActionStatus.Skipped, NotThisSide(action)),
            () => c.Loaded.IsObserveOnly ? new Stop(ActionStatus.Skipped, ObserveOnlyReason) : null,
            () => run.Trigger != RunTrigger.Timer || c.Loaded.Config.Bool(action.Id.AutoSwitch) ? null : new Stop(ActionStatus.Skipped, $"{action.Id.AutoSwitch.Name} is off: the timer does not run {action.Id}"),
        ];
        return First(action, gates, preview: null);
    }

    /// <summary>The gates asked of the LIVE preview, in order; <c>null</c> when the action may run now.</summary>
    private ActionOutcome? Judge(ICleanupAction action, ActionPreview preview, RunState run)
    {
        Func<Stop?>[] gates =
        [
            () => preview.Available ? null : new Stop(ActionStatus.Refused, $"its preview could not be read: {preview.Reason}"),
            () => preview.Skip.Length > 0 ? new Stop(ActionStatus.Skipped, preview.Skip) : null,
            () => TriggerStop(action, preview, run),
            () => action.Scope == CommandScope.User && run.Target is not TargetUserResult.Found ? new Stop(ActionStatus.Refused, run.Target.Refusal) : null,
            () => preview.Refusal.Length > 0 ? new Stop(ActionStatus.Refused, preview.Refusal) : null,
            () => IdleGate.Applies(action.Idle, run.Trigger) && run.Idle() is { Idle: false } busy ? new Stop(ActionStatus.Deferred, busy.Reason) : null,
            () => run.Dry.DryRun ? new Stop(ActionStatus.DryRun, $"dry run: {run.Dry.Reason}") : null,
        ];
        return First(action, gates, preview);
    }

    /// <summary>The timer runs an action only when its trigger fired; a button runs it regardless (plan §5).</summary>
    private Stop? TriggerStop(ICleanupAction action, ActionPreview preview, RunState run)
    {
        if (run.Trigger != RunTrigger.Timer)
        {
            return null;
        }

        var decision = action.Trigger(preview, c.Loaded.Config);
        return decision.Fired ? null : new Stop(ActionStatus.Skipped, $"trigger not reached: {decision.Reason}");
    }

    private static ActionOutcome? First(ICleanupAction action, IEnumerable<Func<Stop?>> gates, ActionPreview? preview) =>
        gates.Select(gate => gate()).FirstOrDefault(stop => stop is not null) is { } stop ? Outcome(action, stop.Status, stop.Reason, preview, null) : null;

    private async Task<ActionOutcome> PreviewOneAsync(ICleanupAction action, ActionContext context, TargetUserResult target, CancellationToken cancellationToken)
    {
        if (!action.Sides.Contains(c.Paths.Side))
        {
            return Outcome(action, ActionStatus.Skipped, NotThisSide(action), null, null);
        }

        try
        {
            var preview = await action.PreviewAsync(context, Commands(action, target), cancellationToken).ConfigureAwait(false);
            var refusal = action.Scope == CommandScope.User ? target.Refusal : string.Empty;
            return Outcome(action, ActionStatus.Previewed, new[] { refusal, preview.Skip, preview.Refusal }.FirstOrDefault(r => r.Length > 0, string.Empty), preview, null);
        }
        catch (Exception e) when (!(e is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            return Outcome(action, ActionStatus.Failed, $"its preview failed: {e.GetType().Name}: {e.Message}", null, null);
        }
    }

    /// <summary>The <c>running.json</c> a previous run left: swept when its process is dead, a refusal when it is not.</summary>
    private ActResult? Sweep(List<string> notes, DateTimeOffset now)
    {
        switch (RunningState.Read(c.Paths, c.Files, c.Processes, now))
        {
            case RunningStatus.None:
                return null;
            case RunningStatus.Dead dead:
                return TrySweep(dead, notes);
            case RunningStatus.Live live:
                return new ActResult.Busy($"run {live.File.RunId} (pid {live.File.Pid}) is acting although the run lock was free - the lock file was replaced; nothing was done");
            case RunningStatus.Wedged wedged:
                return new ActResult.Wedged(Wedged(wedged));
            case RunningStatus.Unknown unknown:
                return new ActResult.Wedged($"the running state cannot be told: {unknown.Reason}; nothing was done, nothing was killed");
            default:
                throw new System.Diagnostics.UnreachableException("RunningStatus is a closed set");
        }
    }

    /// <summary>A sweep that cannot write its record or remove the file refuses the run: the state is not one to act on.</summary>
    private ActResult? TrySweep(RunningStatus.Dead dead, List<string> notes)
    {
        try
        {
            notes.Add(SweepDead(dead));
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or TimeoutException)
        {
            return new ActResult.Wedged($"the running.json of the dead run {dead.File.RunId} could not be swept ({e.Message}); nothing was done");
        }
    }

    /// <summary>Plan §15a #0: the dead run gets its <c>interrupted</c> history line, THEN its <c>running.json</c> goes.</summary>
    private string SweepDead(RunningStatus.Dead dead)
    {
        var file = dead.File;
        if (RunHistory.Read(c.Paths, c.Files).Records.Any(r => r.RunId == file.RunId))
        {
            // The run recorded itself and died before removing the file: its history already tells the truth.
            RunningState.Remove(c.Paths, c.Files);
            return $"removed the running.json of run {file.RunId}: {dead.Why}, and the run had already recorded itself";
        }

        var line = new RunRecord(Core.SchemaVersion.Current, file.RunId, file.Trigger, file.StartedAt, file.HeartbeatAt, RunOutcome.Interrupted, [.. file.Actions.Select(a => new ActionRecord(a, 0, 0) { Status = "interrupted" })])
        {
            Reason = $"swept: {dead.Why}; it was on {(file.Current.Length > 0 ? file.Current : "no action yet")}, last heartbeat {file.HeartbeatAt.UtcDateTime:yyyy-MM-dd HH:mm:ss}Z",
        };
        new RunRecordWriter(c.Paths, c.Files).Append(line);
        RunningState.Remove(c.Paths, c.Files);
        return $"swept the running.json of run {file.RunId}: {dead.Why} (recorded as interrupted)";
    }

    private static string Wedged(RunningStatus.Wedged w) =>
        $"run {w.File.RunId} is wedged: pid {w.File.Pid} is alive but its heartbeat is {w.HeartbeatAge.TotalSeconds:0} s old ({w.File.Current}); nothing was killed - stop it by hand, then run again";

    private void Reconcile(List<string> notes, DateTimeOffset now)
    {
        try
        {
            var report = RunReconcile.Apply(c.Paths, c.Files, now);
            notes.AddRange(report.Interrupted.Select(id => $"reconcile: run {id} had a detail and no history line (recorded as interrupted)"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or TimeoutException)
        {
            notes.Add($"reconcile failed: {e.Message}");
        }
    }

    /// <summary>Empty when <c>running.json</c> was written; otherwise why the run cannot start.</summary>
    private string StartRunning(RunningFile running)
    {
        try
        {
            return RunningState.Write(c.Paths, c.Files, running) is Files.Deletion.DeletionVerdict.Refused refused
                ? $"running.json could not be written, so no action ran: {refused.Reason}"
                : string.Empty;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"running.json could not be written, so no action ran: {e.Message}";
        }
    }

    /// <summary>The run is recorded: <c>running.json</c> goes. A removal that fails is said in the result — the next run sweeps
    /// it (its pid will be gone) without a second history line, because the run already has one.</summary>
    private ActResult.Done RemoveRunning(ActResult.Done result)
    {
        try
        {
            return RunningState.Remove(c.Paths, c.Files) is Files.Deletion.DeletionVerdict.Refused refused
                ? result with { Reason = Joined(result.Reason, $"running.json was left behind: {refused.Reason}") }
                : result;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return result with { Reason = Joined(result.Reason, $"running.json was left behind: {e.Message}") };
        }
    }

    private static string Joined(string first, string second) => first.Length == 0 ? second : $"{first}; {second}";

    private ActResult.Done Record(ActRunDetail detail)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(detail, WslCareJsonContext.Default.ActRunDetail);
        var recorded = RunRecorder.Record(c.Paths, c.Files, detail.RunId, json, (relative, failure) => Line(detail, relative, failure));
        return new ActResult.Done(detail, recorded.Recording, recorded.DetailFile, recorded.Reason);
    }

    private static RunRecord Line(ActRunDetail d, string relative, string failure) =>
        new(Core.SchemaVersion.Current, d.RunId, d.Trigger, d.StartedAt, d.EndedAt, failure.Length == 0 ? d.Outcome : RunOutcome.Failed, [.. d.Actions.Select(ActionLine)])
        {
            DryRun = d.DryRun,
            Detail = relative.Length > 0 ? relative : null,
            Reason = failure.Length > 0 ? failure : d.Outcome is RunOutcome.Failed or RunOutcome.Interrupted ? string.Join("; ", d.Notes) : null,
        };

    private static ActionRecord ActionLine(ActionOutcome o) => o.Status switch
    {
        ActionStatus.Ran or ActionStatus.Failed => new ActionRecord(o.Id, o.Run?.Count ?? 0, o.Run?.FreedBytes ?? 0) { Status = o.Status },
        ActionStatus.DryRun => new ActionRecord(o.Id, o.Preview?.Count ?? 0, 0) { Status = o.Status, WouldFreeBytes = o.Preview?.Bytes },
        _ => new ActionRecord(o.Id, 0, 0) { Status = o.Status },
    };

    private ActRunDetail Detail(RunId runId, RunTrigger trigger, DateTimeOffset started, DryRunDecision dry, TargetUserResult target, IReadOnlyList<ActionOutcome> outcomes, IReadOnlyList<string> notes, RunOutcome outcome) =>
        new(Core.SchemaVersion.Current, runId, trigger, started, c.Clock.GetUtcNow(), dry.DryRun, "act", outcome, c.Paths.Side == HostSide.Wsl ? "wsl" : "windows", dry.Reason, TargetUserReport.From(target), outcomes, notes);

    private static ActionOutcome Outcome(ICleanupAction action, string status, string reason, ActionPreview? preview, ActionRun? run) =>
        new(action.Id.Text, action.Summary, status, reason, preview, run);

    private string NotThisSide(ICleanupAction action) =>
        $"{action.Id} runs on the {string.Join(" / ", action.Sides.Select(s => s == HostSide.Wsl ? "WSL distro" : "Windows host"))} side; this is the {(c.Paths.Side == HostSide.Wsl ? "WSL" : "Windows")} binary";

    private TargetUserResult DiscoverTarget() =>
        c.Paths is LinuxHostPaths linux
            ? TargetUserDiscovery.Discover(c.Files, linux)
            : new TargetUserResult.None("the Windows binary has no target user (its actions are E12's)");

    private ActionCommands Commands(ICleanupAction action, TargetUserResult target) =>
        new(action, c.Commands, target, action.Scope == CommandScope.User && target is TargetUserResult.Found found && c.Paths is LinuxHostPaths linux ? TargetUserCommands.BinFolders(found.User, linux, c.Files) : []);

    private ActionContext Context(ActRequest request, TargetUserResult target) =>
        new(c.Paths, c.Files, c.Clock, c.Loaded.Config, request.Trigger, target)
        {
            Processes = SampleProcesses,
            Signals = c.Signals,
            ShownVolumes = request.ShownVolumes,
            Wait = c.Wait,
        };

    /// <summary>The distro's process table, read fresh (the fast probe reads files only, starts nothing).</summary>
    private Collectors.Reading<Collectors.ProcessSnapshot> SampleProcesses(CancellationToken cancellationToken) =>
        c.Probe.Sample(cancellationToken).Vm.Bind(vm => vm.Processes);

    /// <summary>This process's start as the operating system reports it — the same reading a later run compares with.</summary>
    private DateTimeOffset OwnStart() =>
        c.Processes.Lookup(c.ProcessId) is ProcessLookup.Alive alive ? alive.StartUtc : DateTimeOffset.MinValue;

    /// <summary>One execution's fixed facts and the idle sample, read at most once and only when an action needs it.</summary>
    private sealed class RunState(RunTrigger trigger, TargetUserResult target, DryRunDecision dry, ActionContext context)
    {
        private IdleVerdict? _idle;

        public RunTrigger Trigger => trigger;

        public TargetUserResult Target => target;

        public DryRunDecision Dry => dry;

        public ActionContext Context => context;

        public Func<IdleVerdict> IdleSource { get; init; } = static () => new IdleVerdict(false, "deferred: no idle sample");

        public IdleVerdict Idle() => _idle ??= IdleSource();
    }
}
