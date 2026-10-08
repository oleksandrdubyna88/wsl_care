using System.Text.Json;

using WslCare.Core.Collect;
using WslCare.Core.Collectors;
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
    /// <summary>How often <c>running.json</c> is rewritten (plan §6, <c>running.heartbeatSeconds</c>; a test shortens it).</summary>
    public TimeSpan HeartbeatPeriod { get => field == TimeSpan.Zero ? RunningState.HeartbeatPeriod : field; init; }

    /// <summary>How A11 signals a process (by pid and start, never by name). Refuses unless the CLI wires the real one —
    /// which it does only outside a sandbox, on Linux.</summary>
    public IProcessSignals Signals { get; init; } = RefusingProcessSignals.NotWired;

    /// <summary>The wait an action may take (A11's CPU window); real time unless a test passes its own.</summary>
    public Func<TimeSpan, CancellationToken, Task> Wait { get; init; } = static (delay, token) => Task.Delay(delay, token);

    /// <summary>How an unreadable <c>running.json</c> is read again before any verdict (gate finding #7).</summary>
    public RunningReadRetry RunningRetry { get; init; } = RunningReadRetry.Default;

    /// <summary>What cancelled the run, in words, asked only once it was cancelled — the CLI names the signal (SIGHUP when
    /// the terminal or the <c>wsl.exe</c> that started it went away, §15j B2), so the <c>interrupted</c> record says which.</summary>
    public Func<string> InterruptCause { get; init; } = static () => "a signal";
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
        var context = Context(request, target, []);
        var outcomes = new List<ActionOutcome>();
        foreach (var action in c.Registry.InExecutionOrder(request.Ids))
        {
            outcomes.Add(await PreviewOneAsync(action, context, target, cancellationToken).ConfigureAwait(false));
        }

        return new ActResult.Previewed(outcomes, TargetUserReport.From(target));
    }

    /// <summary>The run: refused when another run holds the lock — at once, or after the request's bounded
    /// <see cref="ActRequest.LockWait"/> (an accepted detached run only).</summary>
    public async Task<ActResult> ExecuteAsync(ActRequest request, CancellationToken cancellationToken)
    {
        switch (await RunLock.TakeAsync(c.Paths, c.Files, request.LockWait, cancellationToken).ConfigureAwait(false))
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
    private ActResult WhileLocked(string lockReason) => RunningState.Read(c.Paths, c.Files, c.Processes, c.Clock.GetUtcNow(), c.RunningRetry) switch
    {
        RunningStatus.Wedged w => new ActResult.Wedged(RunningSweep.WedgedReason(w)),
        RunningStatus.Live live => new ActResult.Busy($"run {live.File.RunId} is acting ({live.File.Current}, pid {live.File.Pid}); try again when it ends"),
        _ => new ActResult.Busy($"another run holds the run lock - a full run (collect), most likely; try again when it ends ({lockReason})"),
    };

    private async Task<ActResult> UnderLockAsync(ActRequest request, CancellationToken cancellationToken)
    {
        var started = c.Clock.GetUtcNow();
        var notes = new List<string>();
        var runId = request.RunId ?? RunId.New(started, c.ProcessId);
        if (Sweep(notes, started, runId) is { } refusal)
        {
            return refusal;
        }

        Reconcile(notes, started);
        notes.AddRange(await request.UnderLock(runId, cancellationToken).ConfigureAwait(false));
        var pass = await PassAsync(runId, request, started, notes, cancellationToken).ConfigureAwait(false);
        var recorded = Record(Detail(runId, request.Trigger, started, pass.Dry, pass.Target, pass.Outcomes, notes, pass.Outcome));
        var result = pass.RunningWritten ? EndRunning(recorded) : recorded;
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    /// <summary>
    /// The TIMER PASS of a full run (E3.S3): <c>collect</c> calls it AFTER measuring, while it HOLDS the one run lock, for every
    /// action this build holds, as the timer — so the engine's own gates decide each action (side, observe-only, the
    /// <c>auto</c> switch, the live preview, the trigger, the target user, the refusal, the idle gate unless the preview is
    /// urgent, the dry-run week). Its outcomes go into the full run's OWN record; it writes no record of its own. The
    /// <c>running.json</c> sweep runs first (a live or wedged run, or an unreadable state, means no pass); the reconcile does
    /// not — the full run's housekeeping already did it. When <see cref="TimerPass.RunningWritten"/> the caller removes
    /// <c>running.json</c> with <see cref="EndTimerPass"/> once its record is written, the order an <c>act</c> keeps.
    /// </summary>
    public async Task<TimerPass> TimerPassAsync(RunId runId, DateTimeOffset started, CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        if (Sweep(notes, started, runId) is { } refusal)
        {
            return TimerPass.NotRun(refusal switch
            {
                ActResult.Busy busy => busy.Reason,
                ActResult.Wedged wedged => wedged.Reason,
                ActResult.StateUnreadable unreadable => unreadable.Reason,
                _ => "the running state does not allow a pass",
            }, notes);
        }

        RecordAgentCpu(notes);

        // A button-only action is never even selected by the timer (plan §15q E7.S2b); the auto gate refuses it as well.
        var request = new ActRequest([.. c.Registry.Actions.Select(a => a.Id).Where(id => !id.ButtonOnly)], RunTrigger.Timer, Execute: true) { Kind = RunKind.Collect };
        var pass = await PassAsync(runId, request, started, notes, cancellationToken).ConfigureAwait(false);
        return new TimerPass(true, string.Empty, pass.Dry.DryRun, pass.Dry.Reason, TargetUserReport.From(pass.Target), pass.Outcomes, notes, pass.Outcome)
        {
            RunningWritten = pass.RunningWritten,
        };
    }

    /// <summary>Plan §15q E7.S2b: every timer run records the AI-agent processes' CPU ticks by identity, so A18 can tell — by
    /// measurement — a process that used no CPU for hours. A failure is a note of the pass, never its end.</summary>
    private void RecordAgentCpu(List<string> notes)
    {
        if (c.Paths is not LinuxHostPaths linux || c.Probe.Sample(CancellationToken.None).Vm.Bind(vm => vm.Processes) is not Reading<ProcessSnapshot>.Available { Value: var snapshot })
        {
            return;
        }

        if (Suspects.AgentCpuHistory.Record(linux, c.Files, snapshot.All, Collectors.Procfs.SampleTime.Of(c.Clock), Mcp.McpSettings.From(c.Loaded.Config).Watched) is { Length: > 0 } failure)
        {
            notes.Add($"the AI-agent CPU history was not recorded: {failure}");
        }
    }

    /// <summary>The full run is recorded: the timer pass's <c>running.json</c> goes. Empty when it went; otherwise why not (the
    /// next run sweeps it without a second history line — the run already has one).</summary>
    public string EndTimerPass()
    {
        try
        {
            return RunningState.Remove(c.Paths, c.Files) is Files.Deletion.DeletionVerdict.Refused refused ? $"running.json was left behind: {refused.Reason}" : string.Empty;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"running.json was left behind: {e.Message}";
        }
    }

    /// <summary>What one pass over the actions produced.</summary>
    private sealed record Pass(TargetUserResult Target, DryRunDecision Dry, IReadOnlyList<ActionOutcome> Outcomes, RunOutcome Outcome, bool RunningWritten);

    /// <summary>The target user, the dry-run decision, <c>running.json</c>, then every action under a beating heart.</summary>
    private async Task<Pass> PassAsync(RunId runId, ActRequest request, DateTimeOffset started, List<string> notes, CancellationToken cancellationToken)
    {
        var target = DiscoverTarget();
        var dry = DryRunWindow.Decide(request.Trigger, c.Loaded.Config, c.Paths, c.Files, started);
        var running = RunningState.Identified(new RunningFile(Core.SchemaVersion.Current, runId, request.Trigger, [.. request.Ids.Select(i => i.Text)], string.Empty, c.ProcessId, OwnStart(), started, started, request.Kind), c.Processes);
        if (StartRunning(running) is { Length: > 0 } cannot)
        {
            notes.Add(cannot);
            return new Pass(target, dry, [], RunOutcome.Failed, RunningWritten: false);
        }

        request.OnRunningWritten();
        var outcomes = new List<ActionOutcome>();
        var run = new RunState(request.Trigger, target, dry, Context(request, target, outcomes)) { IdleSource = SampleIdle };
        var outcome = await ActAllAsync(request, run, running, notes, outcomes, cancellationToken).ConfigureAwait(false);
        return new Pass(target, dry, outcomes, outcome, RunningWritten: true);
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
    private async Task<RunOutcome> ActAllAsync(ActRequest request, RunState run, RunningFile running, List<string> notes, List<ActionOutcome> outcomes, CancellationToken cancellationToken)
    {
        var ordered = c.Registry.InExecutionOrder(request.Ids);
        var heartbeat = new Heartbeat(c.Paths, c.Files, c.Clock, c.Processes, running, c.HeartbeatPeriod);
        await using (heartbeat.ConfigureAwait(false))
        {
            await ActEachAsync(ordered, run, heartbeat, outcomes, cancellationToken).ConfigureAwait(false);
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            return Completed(heartbeat.Failure, notes);
        }

        RecordNotRun(ordered, outcomes);
        notes.Add($"interrupted by {c.InterruptCause()} before every action had run");
        return RunOutcome.Interrupted;
    }

    /// <summary>The actions one by one, until a cancellation: the action it cuts off is recorded <c>interrupted</c> — with what
    /// it had confirmed when it returned a partial run, without when it threw (E6.S0 review D2).</summary>
    private async Task ActEachAsync(IReadOnlyList<ICleanupAction> ordered, RunState run, Heartbeat heartbeat, List<ActionOutcome> outcomes, CancellationToken cancellationToken)
    {
        foreach (var action in ordered.TakeWhile(_ => !cancellationToken.IsCancellationRequested))
        {
            heartbeat.Current(action.Id.Text);
            try
            {
                outcomes.Add(await OneAsync(action, run, cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                outcomes.Add(Outcome(action, ActionStatus.Interrupted, $"cut off by {c.InterruptCause()} while it ran; what it did before the signal is not known", null, null));
                return;
            }
        }
    }

    /// <summary>Every requested action the cancellation came before: <c>interrupted</c>, not run — so the record says what the
    /// run was going to do, as the sweep of a dead run does.</summary>
    private void RecordNotRun(IReadOnlyList<ICleanupAction> ordered, List<ActionOutcome> outcomes) =>
        outcomes.AddRange([.. ordered.Where(a => outcomes.All(o => o.Id != a.Id.Text))
            .Select(a => Outcome(a, ActionStatus.Interrupted, $"not run: the run was interrupted by {c.InterruptCause()} before it", null, null))]);

    /// <summary>How a pass that reached its end ended: observe-only or completed — with the heartbeat's failure noted.</summary>
    private RunOutcome Completed(string heartbeatFailure, List<string> notes)
    {
        if (heartbeatFailure.Length > 0)
        {
            notes.Add(heartbeatFailure);
        }

        return c.Loaded.IsObserveOnly ? RunOutcome.ObserveOnly : RunOutcome.Completed;
    }

    /// <summary>One action through every gate; ANY failure of its own becomes a <c>failed</c> outcome and the run goes on.</summary>
    private async Task<ActionOutcome> OneAsync(ICleanupAction action, RunState run, CancellationToken cancellationToken)
    {
        try
        {
            return await GatedAsync(action, run, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (!(e is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            return Outcome(action, ActionStatus.Failed, $"{e.GetType().Name}: {e.Message}", null, null);
        }
    }

    /// <summary>The gates before the preview, the LIVE preview, the gates of the preview, then the run.</summary>
    private async Task<ActionOutcome> GatedAsync(ICleanupAction action, RunState run, CancellationToken cancellationToken)
    {
        if (Gate(action, run) is { } early)
        {
            return early;
        }

        var commands = Commands(action, run.Target);
        var preview = Guarded(action, run.Context, await action.PreviewAsync(run.Context, commands, cancellationToken).ConfigureAwait(false));
        if (Judge(action, preview, run) is { } held)
        {
            return held;
        }

        return Ran(action, preview, await action.RunAsync(run.Context, preview, commands, cancellationToken).ConfigureAwait(false));
    }

    private ActionOutcome Ran(ICleanupAction action, ActionPreview preview, ActionRun done) => done switch
    {
        { Interrupted: true } => Outcome(action, ActionStatus.Interrupted, $"cut off by {c.InterruptCause()} while it ran: {done.Count} confirmed before the signal, the command in flight and the rest are under notRemoved", preview, done),
        { Succeeded: true } => Outcome(action, ActionStatus.Ran, preview.Urgent.Length > 0 ? $"ran at once, without waiting for idle: {preview.Urgent}" : "ran", preview, done),
        _ => Outcome(action, ActionStatus.Failed, done.Failure, preview, done),
    };

    /// <summary>A gate's answer: the status and reason that stop the action here, or <c>null</c> to go on.</summary>
    private sealed record Stop(string Status, string Reason);

    /// <summary>The gates asked before any preview, in order: the side, observe-only, the timer's <c>auto</c> switch.</summary>
    private ActionOutcome? Gate(ICleanupAction action, RunState run) =>
        First(action, [() => SideStop(action), ObserveOnlyStop, () => AutoStop(action, run)], preview: null);

    private Stop? SideStop(ICleanupAction action) => action.Sides.Contains(c.Paths.Side) ? null : new Stop(ActionStatus.Skipped, NotThisSide(action));

    private Stop? ObserveOnlyStop() => c.Loaded.IsObserveOnly ? new Stop(ActionStatus.Skipped, ObserveOnlyReason) : null;

    private Stop? AutoStop(ICleanupAction action, RunState run) => run.Trigger != RunTrigger.Timer ? null : UnseenSwitchStop(action) ?? TimerStop(action);

    /// <summary>Retro gate over PR #7: with the user layer unread, any <c>auto</c> switch may be off where root cannot see it — the
    /// timer runs nothing rather than trust a default (plan §15a #1); the reason names how to end it.</summary>
    private Stop? UnseenSwitchStop(ICleanupAction action) =>
        !c.Loaded.UserLayerUnread
            ? null
            : new Stop(ActionStatus.Skipped, $"{c.Loaded.UserLayerSkipped}; the timer does not run {action.Id} while a switch the person turned off there cannot be seen");

    /// <summary>The timer's answer for one action: a button only never runs; an <c>auto</c> switch that is off skips it.</summary>
    private Stop? TimerStop(ICleanupAction action) =>
        action.Id.Timer switch
        {
            TimerSwitch.ButtonOnly button => new Stop(ActionStatus.Skipped, button.Why),
            TimerSwitch.Auto auto when !c.Loaded.Config.Bool(auto.Key) => new Stop(ActionStatus.Skipped, $"{auto.Key.Name} is off: the timer does not run {action.Id}"),
            _ => null,
        };

    /// <summary>The gates asked of the LIVE preview, in order; <c>null</c> when the action may run now.</summary>
    private ActionOutcome? Judge(ICleanupAction action, ActionPreview preview, RunState run) =>
        First(
            action,
            [
                () => UnreadStop(preview),
                () => SkipStop(preview),
                () => TriggerStop(action, preview, run),
                () => TargetUserStop(action, run),
                () => RefusalStop(preview),
                () => IdleStop(action, preview, run),
                () => DryRunStop(run),
            ],
            preview);

    /// <summary>Plan §15q R2 (review B2): a preview whose action's cleanup folder overlaps an AI agent's folder carries THAT refusal
    /// — before the action's own refusal AND its skip (E7.S1/S2 review R8: the safety reason is the one a person must read, and
    /// a skip would read as "nothing to do" where the truth is "refused").</summary>
    private static ActionPreview Guarded(ICleanupAction action, ActionContext context, ActionPreview preview) =>
        AgentFolderOverlap.Refusal(action, context) is { Length: > 0 } overlap ? preview with { Refusal = overlap, Skip = string.Empty } : preview;

    private static Stop? UnreadStop(ActionPreview preview) => preview.Available ? null : new Stop(ActionStatus.Refused, $"its preview could not be read: {preview.Reason}");

    private static Stop? SkipStop(ActionPreview preview) => preview.Skip.Length > 0 ? new Stop(ActionStatus.Skipped, preview.Skip) : null;

    private static Stop? TargetUserStop(ICleanupAction action, RunState run) =>
        action.Scope == CommandScope.User && run.Target is not TargetUserResult.Found ? new Stop(ActionStatus.Refused, run.Target.Refusal) : null;

    private static Stop? RefusalStop(ActionPreview preview) => preview.Refusal.Length > 0 ? new Stop(ActionStatus.Refused, preview.Refusal) : null;

    /// <summary>The idle gate: asked only when it applies and the preview is no urgent event; the sample is read once per run.</summary>
    private static Stop? IdleStop(ICleanupAction action, ActionPreview preview, RunState run) =>
        IdleGate.Applies(action.Idle, run.Trigger) && preview.Urgent.Length == 0 && run.Idle() is { Idle: false } busy ? new Stop(ActionStatus.Deferred, busy.Reason) : null;

    private static Stop? DryRunStop(RunState run) => run.Dry.DryRun ? new Stop(ActionStatus.DryRun, $"dry run: {run.Dry.Reason}") : null;

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
        try
        {
            return await PreviewedAsync(action, context, target, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (!(e is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            return Outcome(action, ActionStatus.Failed, $"its preview failed: {e.GetType().Name}: {e.Message}", null, null);
        }
    }

    private async Task<ActionOutcome> PreviewedAsync(ICleanupAction action, ActionContext context, TargetUserResult target, CancellationToken cancellationToken)
    {
        if (!action.Sides.Contains(c.Paths.Side))
        {
            return Outcome(action, ActionStatus.Skipped, NotThisSide(action), null, null);
        }

        var preview = Guarded(action, context, await action.PreviewAsync(context, Commands(action, target), cancellationToken).ConfigureAwait(false));
        var refusal = action.Scope == CommandScope.User ? target.Refusal : string.Empty;
        return Outcome(action, ActionStatus.Previewed, new[] { refusal, preview.Skip, preview.Refusal }.FirstOrDefault(r => r.Length > 0, string.Empty), preview, null) with
        {
            Shown = ShownOf(action, preview),
            ShownTruncated = ShownTruncatedOf(action, preview),
        };
    }

    /// <summary>coai E6 plan round #11: true when the preview selected more names than a shown list carries — <c>count</c>
    /// stays the total, <c>shown</c> the first <see cref="ShownList.MaxNames"/>; absent otherwise.</summary>
    private static bool? ShownTruncatedOf(ICleanupAction action, ActionPreview preview) =>
        action is IBoundToShownList && preview.Available && ShownList.Truncates(preview.Count) ? true : null;

    /// <summary>§15j B1: for an action bound to its shown list (A4), every name its available preview selected — what the
    /// panel sends back; absent (null) for every other action and for a preview that could not be read.</summary>
    private static IReadOnlyList<string>? ShownOf(ICleanupAction action, ActionPreview preview) =>
        action is IBoundToShownList bound && preview.Available ? bound.Shown(preview) : null;

    /// <summary>The <c>running.json</c> a previous run left (<see cref="RunningSweep"/>, shared with every full run's
    /// housekeeping): swept when its process is dead, a refusal when it is not; this run's own file (a full run's, met by its
    /// timer pass) is never in its way.</summary>
    private ActResult? Sweep(List<string> notes, DateTimeOffset now, RunId ownRunId) =>
        RunningSweep.Apply(c.Paths, c.Files, c.Processes, now, c.RunningRetry, ownRunId, c.ProcessId) switch
        {
            RunningSweep.Clear clear => Noted(notes, clear.Note),
            RunningSweep.Blocked blocked => blocked.Refusal,
            _ => throw new System.Diagnostics.UnreachableException("RunningSweep is a closed set"),
        };

    private static ActResult? Noted(List<string> notes, string note)
    {
        if (note.Length > 0)
        {
            notes.Add(note);
        }

        return null;
    }

    private void Reconcile(List<string> notes, DateTimeOffset now)
    {
        try
        {
            var report = RunReconcile.Apply(c.Paths, c.Files, now);
            notes.AddRange(report.Interrupted.Select(id => $"reconcile: run {id} had a detail and no history line (recorded as interrupted)"));
            if (report.Problem.Length > 0)
            {
                notes.Add(report.Problem);
            }
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

    /// <summary>Why <c>running.json</c> stays when the run's line could not be written (retro round over PR #11, O2).</summary>
    public const string KeptForTheSweep = "running.json is kept, the run's only trace: the next root run's sweep records it";

    /// <summary><c>running.json</c> goes only once the run's history line is written; with no line it is the run's only trace and
    /// stays for the next root run's sweep, which records the run <c>interrupted</c> (retro round over PR #11, O2).</summary>
    private ActResult.Done EndRunning(ActResult.Done recorded) =>
        recorded.LineWritten ? RemoveRunning(recorded) : recorded with { Reason = Joined(recorded.Reason, KeptForTheSweep) };

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
        return new ActResult.Done(detail, recorded.Recording, recorded.DetailFile, recorded.Reason) { LineWritten = recorded.LineWritten };
    }

    private static RunRecord Line(ActRunDetail d, string relative, string failure) =>
        new(Core.SchemaVersion.Current, d.RunId, d.Trigger, d.StartedAt, d.EndedAt, failure.Length == 0 ? d.Outcome : RunOutcome.Failed, [.. d.Actions.Select(ActionLine)], RunKind.Act)
        {
            DryRun = d.DryRun,
            Detail = relative.Length > 0 ? relative : null,
            Reason = LineReason(d, failure),
        };

    /// <summary>Why the run's line says what it says: the record's own failure, or the notes of a failed or interrupted run.</summary>
    private static string? LineReason(ActRunDetail d, string failure) =>
        failure.Length > 0 ? failure : d.Outcome is RunOutcome.Failed or RunOutcome.Interrupted ? string.Join("; ", d.Notes) : null;

    private static ActionRecord ActionLine(ActionOutcome o) => ActionRecords.Of(o);

    private ActRunDetail Detail(RunId runId, RunTrigger trigger, DateTimeOffset started, DryRunDecision dry, TargetUserResult target, IReadOnlyList<ActionOutcome> outcomes, IReadOnlyList<string> notes, RunOutcome outcome) =>
        new(Core.SchemaVersion.Current, runId, trigger, started, c.Clock.GetUtcNow(), dry.DryRun, "act", outcome, c.Paths.Side == HostSide.Wsl ? "wsl" : "windows", dry.Reason, TargetUserReport.From(target), outcomes, notes)
        {
            Config = ConfigValueReport.NotDefault(c.Loaded),
            ConfigNotices = ConfigNoticeReport.Of(c.Loaded),
        };

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

    /// <param name="soFar">The outcomes of this run so far — what <see cref="ActionContext.RanEarlier"/> answers from.</param>
    private ActionContext Context(ActRequest request, TargetUserResult target, IReadOnlyList<ActionOutcome> soFar) =>
        new(c.Paths, c.Files, c.Clock, c.Loaded.Config, request.Trigger, target)
        {
            Processes = SampleProcesses,
            Signals = c.Signals,
            ShownVolumes = request.ShownVolumes,
            ShownProcesses = request.ShownProcesses,
            Wait = c.Wait,
            RanEarlier = id => soFar.Any(o => o.Id == id.Text && o.Status == ActionStatus.Ran),
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
