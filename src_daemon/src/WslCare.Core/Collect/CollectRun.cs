using System.Text.Json;

using WslCare.Core.Actions;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Docker;
using WslCare.Core.Events;
using WslCare.Core.Files;
using WslCare.Core.Folders;
using WslCare.Core.Health;
using WslCare.Core.Hosting;
using WslCare.Core.Json;
using WslCare.Core.Preview;
using WslCare.Core.Processes;
using WslCare.Core.Records;
using WslCare.Core.Status;
using WslCare.Core.Thresholds;

namespace WslCare.Core.Collect;

/// <summary>Everything a full run reaches the machine through.</summary>
/// <remarks>The init-only members are what the TIMER PASS needs (E3.S3) — the actions, the process table
/// <c>running.json</c> is judged against, the signal sender — and default to the SAFE answer: no action at all, a table that
/// can tell no pid, a sender that refuses. The CLI sets them from its host.</remarks>
public sealed record CollectContext(
    IHostPaths Paths,
    IFileSystem Files,
    ICommandRunner Commands,
    TimeProvider Clock,
    IHostProbe Probe,
    ConfigLoadResult Loaded,
    int ProcessId,
    RunTrigger Trigger)
{
    /// <summary>The actions the timer pass runs over; none by default.</summary>
    public ActionRegistry Actions { get; init; } = new([]);

    /// <summary>The operating system's process table (the <c>running.json</c> sweep); one that can tell no pid by default.</summary>
    public IProcessTable Processes { get; init; } = UnknownProcessTable.Instance;

    /// <summary>How A11 signals a process; refuses by default.</summary>
    public IProcessSignals Signals { get; init; } = RefusingProcessSignals.NotWired;
}

/// <summary>How a full run ended for its records, and its detail (none when another run held the lock).</summary>
public sealed record CollectResult(Recording Recording, string Reason, string DetailFile, RunDetail? Detail);

/// <summary>
/// <c>collect</c> (plan §6): the FULL run — the fast sample, Docker's full numbers and the cleanup rows, the slow
/// parts (<c>docker stats</c>, the Windows clock), the health collectors, the daily folder walk, the container
/// starts of the last 24 h, the thresholds — recorded in the order plan §15b #1 fixes.
/// </summary>
/// <remarks>
/// <para><b>Who records (plan §15b #3).</b> The state directory is root's. Whether this process may write it is the
/// operating system's answer to a write probe (<see cref="IFileSystem.ProbeWriteAccess"/>): when it may not, the
/// run measures and reports exactly as a privileged one would and writes NOTHING — no reconcile, no retention, no
/// detail, no history line, no first sightings — and says <i>read-only: run as root to record</i>.</para>
/// <para><b>One run at a time.</b> A privileged run holds THE run lock (<see cref="RunLock"/>, <c>/run/wsl-care.lock</c> —
/// one file for <c>collect</c> and <c>act</c> since E3.S1; an exclusive open, released by the OS when the holder dies) for its
/// whole length, so the reconcile never mistakes a running run's detail for an orphan; a second run — a full run or an
/// act — is <see cref="Recording.Busy"/>, waits for nothing and measures nothing.</para>
/// <para><b>Order.</b> Reconcile → retention → measure → (the timer only) the action pass, under the same lock (E3.S3) → the
/// detail (atomic) → the history line naming it → the pass's <c>running.json</c> removed; the caller closes the run log last. A failed write makes the run <c>failed</c> with the reason — on its history line when the
/// detail was what failed, in the result (and the log) when the line itself could not be written.</para>
/// </remarks>
public static class CollectRun
{
    public const string ReadOnlyNote = "read-only: run as root to record";

    /// <summary>What a full run's <c>running.json</c> names as its action and its current step (gate finding #11).</summary>
    public const string RunningAction = "collect";

    /// <summary>The "since the last run" window when there is no last run: the timer's period (plan §8).</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(4);

    public static async Task<CollectResult> RunAsync(CollectContext c, CancellationToken cancellationToken)
    {
        var started = c.Clock.GetUtcNow();
        var runId = RunId.New(started, c.ProcessId);
        if (c.Files.ProbeWriteAccess(c.Paths.StateDirectory) is WriteAccess.NotWritable denied)
        {
            var measured = await MeasureAsync(c, runId, started, NoHousekeeping(), mayRecord: false, cancellationToken).ConfigureAwait(false);
            return new CollectResult(Recording.ReadOnly, $"{ReadOnlyNote} ({denied.Reason})", string.Empty, measured);
        }

        switch (RunLock.TryTake(c.Paths, c.Files))
        {
            case ExclusiveLock.Busy busy:
                return new CollectResult(Recording.Busy, $"another run is in progress (a full run or an act): {busy.Reason}", string.Empty, null);
            case ExclusiveLock.Held held:
                using (held.Handle)
                {
                    return await UnderLockAsync(c, runId, started, cancellationToken).ConfigureAwait(false);
                }

            default:
                throw new System.Diagnostics.UnreachableException("ExclusiveLock is a closed set");
        }
    }

    /// <summary>
    /// The run under the lock: the <c>running.json</c> sweep (gate finding #8 — every full run, whatever started it), then THIS
    /// run's own <c>running.json</c> (action <c>collect</c>, pid, start, a heartbeat every 5 s — gate finding #11, so a window
    /// reloaded mid-run still shows the truth), the housekeeping, the measurement, the timer pass, the records — and the
    /// file removed in a <c>finally</c>, whatever ended the run. When another run's state stands in the way (live, wedged,
    /// unreadable) the run still measures and records, writes no <c>running.json</c> of its own and never removes theirs.
    /// </summary>
    private static async Task<CollectResult> UnderLockAsync(CollectContext c, RunId runId, DateTimeOffset started, CancellationToken cancellationToken)
    {
        var sweep = RunningSweep.Apply(c.Paths, c.Files, c.Processes, started, RunningReadRetry.Default, runId, c.ProcessId);
        var running = new RunningFile(Core.SchemaVersion.Current, runId, c.Trigger, [RunningAction], RunningAction, c.ProcessId, OwnStart(c), started, started);
        var owned = sweep is RunningSweep.Clear && StartRunning(c, running);
        try
        {
            var housekeeping = Housekeep(c, started) with { Running = SweepNote(sweep, owned) };
            var measured = await MeasureBeatingAsync(c, running, owned, housekeeping, cancellationToken).ConfigureAwait(false);
            var (detail, engine) = await TimerPassAsync(c, measured, runId, started, cancellationToken).ConfigureAwait(false);
            var result = Record(c, detail);
            var left = EndPass(engine);
            cancellationToken.ThrowIfCancellationRequested();
            return left.Length == 0 ? result : result with { Reason = Joined(result.Reason, left) };
        }
        finally
        {
            EndRunning(c, runId, owned);
        }
    }

    /// <summary>The timer pass's <c>running.json</c> removed once the run is recorded; empty when there was no pass or it went.</summary>
    private static string EndPass(ActionEngine? engine) => engine?.EndTimerPass() ?? string.Empty;

    /// <summary>The measurement under this run's heartbeat (when it owns <c>running.json</c>).</summary>
    private static async Task<RunDetail> MeasureBeatingAsync(CollectContext c, RunningFile running, bool owned, HousekeepingReport housekeeping, CancellationToken cancellationToken)
    {
        IAsyncDisposable heartbeat = owned ? new Heartbeat(c.Paths, c.Files, c.Clock, running, RunningState.HeartbeatPeriod) : NoHeartbeat.Instance;
        await using (heartbeat.ConfigureAwait(false))
        {
            return await MeasureAsync(c, running.RunId, running.StartedAt, housekeeping, mayRecord: true, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The heartbeat of a run that does not own <c>running.json</c>: nothing to beat.</summary>
    private sealed class NoHeartbeat : IAsyncDisposable
    {
        public static readonly NoHeartbeat Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Writes this run's <c>running.json</c>; whether it was written (a refusal leaves the run without one — it still
    /// measures and records, and says so).</summary>
    private static bool StartRunning(CollectContext c, RunningFile running)
    {
        try
        {
            return RunningState.Write(c.Paths, c.Files, running) is not Files.Deletion.DeletionVerdict.Refused;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Removes this run's <c>running.json</c> — only its OWN (the timer pass may have removed it already).</summary>
    private static void EndRunning(CollectContext c, RunId runId, bool owned)
    {
        if (owned && RunningState.RunIdIn(c.Paths, c.Files) == runId)
        {
            try
            {
                RunningState.Remove(c.Paths, c.Files);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The next run's sweep finds a dead pid and removes it without a second history line: this run has one.
            }
        }
    }

    private static string SweepNote(RunningSweep sweep, bool owned) => sweep switch
    {
        RunningSweep.Blocked blocked => $"running.json of another run stands: {blocked.Reason}",
        RunningSweep.Clear clear when !owned => Joined(clear.Note, "this run's running.json could not be written"),
        RunningSweep.Clear clear => clear.Note,
        _ => throw new System.Diagnostics.UnreachableException("RunningSweep is a closed set"),
    };

    private static string Joined(string first, string second) => first.Length == 0 ? second : $"{first}; {second}";

    /// <summary>This process's start as the operating system reports it — what a later reader compares with.</summary>
    private static DateTimeOffset OwnStart(CollectContext c) =>
        c.Processes.Lookup(c.ProcessId) is ProcessLookup.Alive alive ? alive.StartUtc : DateTimeOffset.MinValue;

    /// <summary>
    /// The TIMER PASS (E3.S3): a full run started by the timer runs the action engine AFTER measuring, under the lock it
    /// already holds, over every action this build holds — the engine's own gates decide each one (the <c>auto</c> switch, the
    /// trigger, the idle gate, the dry-run week). Its outcomes become this run's action lines; no second record is written.
    /// A full run started any other way (a terminal, the panel's <i>Run full check now</i>) does not act — a button's
    /// <c>act</c> stays a separate run. Returns the engine when it wrote <c>running.json</c>, for the caller to end the pass
    /// once the run is recorded.
    /// </summary>
    private static async Task<(RunDetail Detail, ActionEngine? Engine)> TimerPassAsync(CollectContext c, RunDetail measured, RunId runId, DateTimeOffset started, CancellationToken cancellationToken)
    {
        if (c.Trigger != RunTrigger.Timer)
        {
            return (measured, null);
        }

        var engine = new ActionEngine(new EngineContext(c.Paths, c.Files, c.Commands, c.Clock, c.Probe, c.Loaded, c.Processes, c.ProcessId, c.Actions) { Signals = c.Signals });
        var pass = await engine.TimerPassAsync(runId, started, cancellationToken).ConfigureAwait(false);
        var detail = measured with
        {
            EndedAt = c.Clock.GetUtcNow(),
            DryRun = pass.Ran ? pass.DryRun : measured.DryRun,
            Actions = pass.Records(),
            TimerPass = pass,
        };
        return (detail, pass.RunningWritten ? engine : null);
    }

    /// <summary>The detail first (atomic), then the history line that names it (<see cref="RunRecorder"/>, shared with <c>act</c>).</summary>
    private static CollectResult Record(CollectContext c, RunDetail detail)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(detail, WslCareJsonContext.Default.RunDetail);
        var recorded = RunRecorder.Record(c.Paths, c.Files, detail.RunId, json, (relative, failure) =>
            failure.Length == 0 ? Line(detail, detail.Outcome) with { Detail = relative } : Line(detail, RunOutcome.Failed) with { Reason = failure });
        return recorded switch
        {
            { Recording: Recording.Recorded } => new CollectResult(Recording.Recorded, string.Empty, recorded.DetailFile, detail),
            { LineWritten: false } => new CollectResult(Recording.Failed, recorded.Reason, recorded.DetailFile, detail),
            _ => new CollectResult(Recording.Failed, recorded.Reason, string.Empty, detail with { Outcome = RunOutcome.Failed }),
        };
    }

    private static RunRecord Line(RunDetail d, RunOutcome outcome) =>
        new(Core.SchemaVersion.Current, d.RunId, d.Trigger, d.StartedAt, d.EndedAt, outcome, d.Actions)
        {
            Slow = d.Slow,
            DryRun = d.DryRun,
            Warnings = [.. d.Thresholds.Where(v => v.Level is not Level.Ok).Select(v => new WarningRecord(v.Id, Camel(v.Level), v.Reason))],
            Metrics = Metrics(d),
        };

    /// <summary>The reconcile, the retention of the run records and of the container-start files — each step guarded
    /// on its own, so a sweep that fails is reported and the run still measures (plan §5).</summary>
    private static HousekeepingReport Housekeep(CollectContext c, DateTimeOffset now)
    {
        var problems = new List<string>();
        var reconcile = Guard(() => RunReconcile.Apply(c.Paths, c.Files, now), new ReconcileReport([], []), problems, "reconcile");
        var retention = Guard(() => RunRetention.Sweep(c.Paths, c.Files, now), new RetentionReport(0, [], []), problems, "run retention");
        var starts = Guard(() => new ContainerStartsStore(c.Paths, c.Files).Prune(now), [], problems, "container-start retention");
        return new HousekeepingReport(reconcile, retention with { Problems = [.. retention.Problems, .. problems] }, starts);
    }

    private static T Guard<T>(Func<T> step, T fallback, List<string> problems, string what)
    {
        try
        {
            return step();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or TimeoutException)
        {
            problems.Add($"{what} failed: {e.Message}");
            return fallback;
        }
    }

    private static HousekeepingReport NoHousekeeping() =>
        new(new ReconcileReport([], []), new RetentionReport(0, [], [ReadOnlyNote]), []);

    private static async Task<RunDetail> MeasureAsync(CollectContext c, RunId runId, DateTimeOffset started, HousekeepingReport housekeeping, bool mayRecord, CancellationToken cancellationToken)
    {
        var newestFirst = RunHistory.Read(c.Paths, c.Files).Records.Reverse().ToList();
        var since = newestFirst.FirstOrDefault()?.StartedAt ?? started - DefaultWindow;
        var last = LastFullRun.FromRecords(newestFirst, started);
        var sample = c.Probe.Sample(cancellationToken);
        var health = await new HealthCollector(c.Commands, c.Files, c.Paths, c.Clock).CollectAsync(since, cancellationToken).ConfigureAwait(false);
        var folders = c.Paths is LinuxHostPaths linux && FolderSizes.Due(last.Folders, started)
            ? await new FolderSizes(c.Files, c.Commands, c.Clock).MeasureAsync(linux, cancellationToken).ConfigureAwait(false)
            : null;
        var foldersNow = folders is null ? last.Folders : Reading.Of(new AgedPart<FolderSizesSample>(folders, runId, folders.SampledAt, TimeSpan.Zero));
        var foldersBefore = folders is null ? last.PreviousFolders : last.Folders.Map(a => a.Value);
        var profile = health.WindowsClock.Measured ? health.WindowsClock.Profile : last.WindowsClock.Map(a => a.Value.Profile).ValueOr(string.Empty);
        var docker = await PreviewRun.CollectAsync(c.Paths, c.Files, c.Commands, c.Clock, c.Loaded, new PreviewExtras(foldersNow, WindowsProfiles.DockerDesktopConfig(c.Paths, c.Files, profile)) { MayRecord = mayRecord }, cancellationToken).ConfigureAwait(false);
        var stats = await DockerStats.SampleAsync(new DockerCli(c.Commands), c.Clock, cancellationToken).ConfigureAwait(false);
        var starts = Coverage.Last24h(new ContainerStartsStore(c.Paths, c.Files).ReadAll(), c.Clock.GetUtcNow());
        var slow = new SlowParts { ContainerStats = stats, WindowsClock = health.WindowsClock, Folders = folders };
        var verdicts = ThresholdRules.Evaluate(Inputs(sample, health, started - since, last, docker, foldersNow), c.Loaded.Config);
        var ended = c.Clock.GetUtcNow();
        var thisRun = LastFullRun.FromRecords([new RunRecord(Core.SchemaVersion.Current, runId, c.Trigger, started, ended, RunOutcome.Completed, []) { Slow = slow }, .. newestFirst], ended);
        var folderReport = FoldersReports.From(foldersNow, foldersBefore, folders is not null);
        return new RunDetail(
            Core.SchemaVersion.Current,
            runId,
            c.Trigger,
            started,
            ended,
            c.Loaded.IsObserveOnly ? RunOutcome.ObserveOnly : RunOutcome.Completed,
            c.Loaded.Config.Bool(ConfigKeys.DryRun),
            c.Loaded.IsObserveOnly,
            c.Paths.Side == HostSide.Wsl ? "wsl" : "windows",
            StatusReports.From(sample, thisRun, c.Loaded) with { ContainerStarts = starts, Folders = folderReport },
            docker.Report,
            HealthReports.From(health),
            verdicts,
            folderReport,
            starts,
            slow,
            housekeeping,
            []);
    }

    private static ThresholdInputs Inputs(ProbeSample sample, HealthSample health, TimeSpan sinceLastRun, LastSlowParts last, PreviewResult docker, Reading<AgedPart<FolderSizesSample>> folders) =>
        new(
            sample.Vm.Bind(vm => vm.Memory),
            sample.Vm.Bind(vm => vm.RootVolume),
            health,
            sinceLastRun,
            last.WindowsClock.Map(a => a.Value),
            docker.Preview.Rows,
            docker.Snapshot.Inventory.Map(inventory => inventory.BuildCache.Sum(b => b.SizeBytes.ValueOr(0))),
            folders.Bind(a => a.Value.Find(FolderSizes.NpmCache) is { Measured: true } npm ? Reading.Of(npm.Bytes) : Reading.Missing<long>("~/.npm was not measured")));

    private static RunMetrics Metrics(RunDetail d)
    {
        var memory = d.Sample.Vm.Memory;
        var disk = d.Sample.Vm.Disk;
        var reclaimable = d.Docker.Totals.Available ? d.Docker.Totals.Types?.Sum(t => t.Reclaimable.Bytes ?? 0) : null;
        return new RunMetrics(
            memory?.AvailablePercent?.Value,
            memory?.MemAvailable?.Bytes,
            memory?.PageCache?.Bytes,
            memory?.SwapUsed?.Bytes,
            disk?.UsedPercent,
            reclaimable,
            d.ContainerStarts.Starts);
    }

    private static string Camel(Level level) => level switch
    {
        Level.Warn => "warn",
        Level.Critical => "critical",
        Level.Unknown => "unknown",
        _ => "ok",
    };
}
