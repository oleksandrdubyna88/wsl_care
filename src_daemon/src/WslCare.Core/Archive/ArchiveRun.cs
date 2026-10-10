using WslCare.Core.Actions.Engine;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Core.Archive;

/// <summary>One agent's part of a run (names no session: E9.S4's root detail reads these aggregates).</summary>
public sealed record AgentRunReport(
    string Id,
    bool Enabled,
    int Copied,
    int CopiedFiles,
    long CopiedBytes,
    int Removed,
    long RemovedBytes,
    int GoneAtSource,
    int Superseded,
    int Damaged,
    int Waiting,
    IReadOnlyList<SkipCount> Skipped,
    string Note);

/// <summary>
/// <c>archive run --json</c> (plan §15r E9.S2b): what one run of this side did. <see cref="Outcome"/> is a closed set —
/// <c>done</c>, <c>stopped</c> (the budget, a full base, a verification failure: <see cref="Stop"/> says which), <c>no-base</c>,
/// <c>refused</c> (the base's rules, its mount, the lease), <c>unreachable</c>, <c>busy</c>.
/// </summary>
public sealed record ArchiveRunReport(
    int SchemaVersion,
    string Side,
    string SideFolder,
    string RunId,
    DateTimeOffset StartedUtc,
    DateTimeOffset EndedUtc,
    string BaseFolder,
    string Outcome,
    string Stop,
    ArchiveReconcileReport Reconcile,
    InUseReport InUse,
    IReadOnlyList<AgentRunReport> Agents,
    double FilesPerSecond,
    double MegabytesPerSecond,
    IReadOnlyList<string> Notes)
{
    /// <summary>What <c>archive reconcile --scan</c> re-indexed (zero for a plain run).</summary>
    public ScanReport Scan { get; init; } = new(0, 0, 0, []);

    /// <summary>What <c>archive restore</c> did (empty for a run or a scan).</summary>
    public RestoreReport Restore { get; init; } = RestoreReport.Empty;

    /// <summary>Why a <c>stopped</c> run stopped (<see cref="StopKinds"/>): a limit or a fault — the exit code is decided by it.</summary>
    public string StopKind { get; init; } = StopKinds.None;
}

/// <summary>A progress line of <c>archive run --json</c>: no names, a kind (<c>file</c>, <c>heartbeat</c>), the counts so far.</summary>
public sealed record ArchiveProgressLine(string Progress, int Files, long Bytes, double Seconds);

/// <summary>What ends a run, a restore or a list before anything is touched (E9.S3 own review round C-7: a closed record, never a
/// marker word split off a sentence): its outcome (<see cref="RunOutcomes"/>) and why; <see cref="None"/> when nothing does.</summary>
public sealed record EarlyStop(string Outcome, string Why)
{
    public static EarlyStop None { get; } = new(string.Empty, string.Empty);

    public bool Stopped => Outcome.Length > 0;
}

public static class RunOutcomes
{
    public const string Done = "done";
    public const string Stopped = "stopped";
    public const string NoBase = "no-base";
    public const string Refused = "refused";
    public const string Unreachable = "unreachable";
    public const string Busy = "busy";
}

/// <summary>Everything a run reads, and how it reports while it works.</summary>
public sealed record ArchiveRunInput(
    IHostPaths Paths,
    IFileSystem Files,
    IArchiveFiles Archive,
    EffectiveConfig Config,
    TimeProvider Clock,
    IProcessTable Processes,
    TimeZoneInfo Zone,
    BaseFolderReport JudgedBase,
    string RunId,
    TimeSpan Budget,
    Func<string, string?> Environment,
    CancellationToken Token)
{
    public string OnlyAgent { get; init; } = string.Empty;

    /// <summary><c>archive reconcile --scan</c>: under the same lock and lease, only re-index the archived files no line names.</summary>
    public bool ScanOnly { get; init; }

    /// <summary><c>archive restore</c>: under the same lock and lease, after the reconcile, only restore what it names (E9.S3).</summary>
    public RestoreRequest Restore { get; init; } = RestoreRequest.None;

    /// <summary><c>archive reach</c> (plan §15r D1, risk consult 9/9.4 #1): the side's lock taken, the base reached, nothing else —
    /// the SHORT child root starts before the long one, so a share that blocks the reader in the kernel holds a 10-second child and
    /// the side's lock, never the run.</summary>
    public bool ReachOnly { get; init; }

    /// <summary>How <see cref="JudgedBase"/> is judged (the S4 own review round S-M1): already, or late — inside the bounded window, after
    /// the side's lock for a reach. The command line judges late; a test hands its judged base.</summary>
    public BaseJudging Judging { get; init; } = BaseJudging.Already;

    /// <summary>The fault seam (<see cref="MoveSteps"/>); does nothing in a real run.</summary>
    public Action<string> Step { get; init; } = static _ => { };

    /// <summary>Told after every file copied or removed.</summary>
    public Action<ArchiveProgressLine> Progress { get; init; } = static _ => { };

    /// <summary>Whether the base answers within <c>archive.reachabilitySeconds</c> (D1: a share that stops answering blocks the reader in
    /// the kernel — the run asks first, in a task it can abandon).</summary>
    public Func<string, TimeSpan, bool> Reachable { get; init; } = static (folder, ceiling) => Task.Run(() => Directory.Exists(folder)).Wait(ceiling);

    /// <summary>The open-file check (D2.2) — the real scan; a test hands its own view.</summary>
    public Func<ArchiveRunInput, InUseView> InUseScan { get; init; } = static i => InUse.Scan(i.Paths, i.Files, i.Budget, i.Token, i.Windows);

    /// <summary>The side's folder name when a test plays several hosts over one base (E9.S5's disjoint-sides test); empty — always, from
    /// the command line — means this process's own (<see cref="SideName.OfThisProcess"/>).</summary>
    internal string SideFolder { get; init; } = string.Empty;

    /// <summary>What the Windows side's open-file check asks (E9.S5): the Restart Manager and the process table in the Windows binary;
    /// a check that did not run anywhere else.</summary>
    public IWindowsSide Windows { get; init; } = UncheckedWindowsSide.NotWindows;

    /// <summary>This process as the lease names it.</summary>
    public LeaseRecord Me { get; init; } = new(1, System.Environment.MachineName, string.Empty, System.Environment.ProcessId, 0, DateTimeOffset.UnixEpoch, string.Empty, DateTimeOffset.UnixEpoch);
}

/// <summary>
/// Plan §15r E9.S2b — one <c>archive run</c> of this side, as the user: the base judged and its mount compared with the one recorded,
/// the side's lock, the base reached, the side's lease, the reconcile (D3), phase 2 for every entry due (D2 steps 7–10), phase 1 for
/// the due units oldest first within the budget, the session cap and the base's free space; the lease and the lock released.
/// </summary>
public static class ArchiveRun
{
    public static ArchiveRunReport Run(ArchiveRunInput input)
    {
        var started = input.Clock.GetUtcNow();
        var state = new ArchiveState(input.Paths, input.Files);
        return NoBase(input) is { Stopped: true } none ? Stopped(new BaseWithin(input, none), started) : Locked(input, state, started);
    }

    /// <summary>This run's side: the one a test named, or this process's own.</summary>
    internal static string SideOf(ArchiveRunInput input) => input.SideFolder.Length > 0 ? input.SideFolder : SideName.OfThisProcess(input.Paths.Side);

    private static ArchiveRunReport Stopped(BaseWithin within, DateTimeOffset started) =>
        Answer(within.Input, started, within.Stop.Outcome, within.Stop.Why, ArchiveReconcileReport.Empty, NotChecked, []);

    private const string BusyWhy = "another archive run of this side holds its lock";

    private static void Holding(ArchiveRunInput input, ArchiveState state, DateTimeOffset started) =>
        _ = state.WriteHolder(new LockHolderRecord(ArchiveState.Version, input.Me.Pid, input.Me.StartTicks, input.Me.BootId, started, input.RunId));

    private static readonly InUseReport NotChecked = new("not-checked", 0, 0, string.Empty);

    /// <summary>What ends the run before anything is touched: no base, a refused base, a changed mount.</summary>
    private static EarlyStop Early(ArchiveRunInput input, ArchiveState state) =>
        BaseProblem(input) is { Stopped: true } early ? early : MountProblem(input.JudgedBase, state);

    /// <summary>No base, or a base its rules refuse — what stops a run, a restore and a list alike.</summary>
    internal static EarlyStop BaseProblem(ArchiveRunInput input) =>
        NoBase(input) is { Stopped: true } none ? none
        : !input.JudgedBase.Accepted ? new EarlyStop(RunOutcomes.Refused, $"the base is refused by its rules ({input.JudgedBase.Rule}: {input.JudgedBase.Refusal})")
        : EarlyStop.None;

    /// <summary>No base configured: nothing to judge, nothing to reach.</summary>
    private static EarlyStop NoBase(ArchiveRunInput input) =>
        input.Config.Text(ConfigKeys.Archive.BaseFolder).Length == 0 ? new EarlyStop(RunOutcomes.NoBase, $"no {ConfigKeys.Archive.BaseFolder.Name} is set; the archive is not configured") : EarlyStop.None;

    /// <summary>D7: the base's mount recorded at its first run and compared at every later one — an unmounted share leaves a plain folder
    /// on the distribution's own disk, which must never be written.</summary>
    private static EarlyStop MountProblem(BaseFolderReport judged, ArchiveState state)
    {
        state.EnsureFolder();
        var now = MountOf(judged);
        var recorded = state.Base();
        return recorded is null && state.BaseRecorded ? new EarlyStop(RunOutcomes.Refused, $"the recorded mount of the base ({state.BaseFile}) could not be read; nothing is written until it is — remove that file by hand only once you are sure the base is the storage you chose")
            : recorded is null || recorded.Folder != now.Folder ? Recorded(state, now)
            : MountChange(recorded, now) is { Length: > 0 } changed ? new EarlyStop(RunOutcomes.Refused, $"{changed}; mount it as before — or, if it really moved and is the same storage, remove {state.BaseFile} by hand and the next run records the new mount")
            : EarlyStop.None;
    }

    /// <summary>The base as judged now, in the record's shape.</summary>
    internal static BaseRecord MountOf(BaseFolderReport judged) => new(ArchiveState.Version, judged.Folder, judged.Mount.Type, judged.Mount.Source, judged.Mount.MountPoint);

    /// <summary>The sentence naming how the base's mount changed since it was recorded; empty when it did not (D7).</summary>
    internal static string MountChange(BaseRecord recorded, BaseRecord now) =>
        recorded with { V = now.V } == now
            ? string.Empty
            : $"the base is not mounted as when it was first used ({recorded.MountType} {recorded.MountSource} at {recorded.MountPoint}; now {now.MountType} {now.MountSource} at {now.MountPoint})";

    private static EarlyStop Recorded(ArchiveState state, BaseRecord now) =>
        state.WriteBase(now) is { Length: > 0 } unwritten ? new EarlyStop(RunOutcomes.Refused, $"the base's mount could not be recorded ({unwritten})") : EarlyStop.None;

    /// <summary>The side's LOCK first — for every verb (the S4 own review round S-M1 for the reach, its gate round's finding 4 for the
    /// rest): a side another run holds answers <c>busy</c> before any base I/O. Then the base — judged, its mount compared, reached —
    /// inside the bounded window. A base check that timed out is left behind, blocked in the kernel; the lock is then KEPT for as long
    /// as this process lives (finding 2), so the next verb answers busy and stuck checks never pile up on one side.</summary>
    private static ArchiveRunReport Locked(ArchiveRunInput input, ArchiveState state, DateTimeOffset started)
    {
        if (input.Files.TryLockExclusive(state.LockFile) is not ExclusiveLock.Held held)
        {
            return Answer(input, started, RunOutcomes.Busy, BusyWhy, ArchiveReconcileReport.Empty, NotChecked, []);
        }

        var abandoned = false;
        try
        {
            Holding(input, state, started);
            var report = Judged(input, state, started);
            abandoned = report.Outcome == RunOutcomes.Unreachable;
            return report;
        }
        finally
        {
            Released(held.Handle, abandoned);
        }
    }

    /// <summary>Under the lock: the base judged and reached in the window, then — for a reach — done; for every other verb its work.</summary>
    private static ArchiveRunReport Judged(ArchiveRunInput input, ArchiveState state, DateTimeOffset started)
    {
        var within = BaseWindow.Judged(input, judged => Early(judged, state) is { Stopped: true } early ? early : BaseWindow.Reachability(judged));
        return within.Stop.Stopped ? Stopped(within, started)
            : input.ReachOnly ? Answer(within.Input, started, RunOutcomes.Done, string.Empty, ArchiveReconcileReport.Empty, NotChecked, [])
            : Keyed(within.Input, state, started);
    }

    /// <summary>The locks whose base check was left behind (finding 2): held for the life of the process, never collected.</summary>
    private static readonly List<IDisposable> KeptLocks = [];

    private static readonly Lock KeptGate = new();

    /// <summary>The side's lock released — unless a base check was left behind, blocked in the kernel: then it is kept, so the side
    /// stays held while the check does, and the operating system releases it only when this process is gone.</summary>
    private static void Released(IDisposable handle, bool abandoned)
    {
        if (!abandoned)
        {
            handle.Dispose();
            return;
        }

        lock (KeptGate)
        {
            KeptLocks.Add(handle);
        }
    }

    private static ArchiveRunReport Keyed(ArchiveRunInput input, ArchiveState state, DateTimeOffset started)
    {
        var key = state.IndexKey();
        return key.Length == 0
            ? Answer(input, started, RunOutcomes.Refused, "the side's index key could not be read or made; nothing is written without it", ArchiveReconcileReport.Empty, NotChecked, [])
            : Leased(input, state, started, key);
    }

    private static ArchiveRunReport Leased(ArchiveRunInput input, ArchiveState state, DateTimeOffset started, byte[] key)
    {
        var side = SideOf(input);
        var taken = SideLease.Take(input.Archive, input.JudgedBase.Folder, side, input.Me with { RunId = input.RunId, SinceUtc = started }, input.Processes);
        if (taken is not LeaseTaken.Held held)
        {
            return Answer(input, started, RunOutcomes.Refused, ((LeaseTaken.Refused)taken).Why, ArchiveReconcileReport.Empty, NotChecked, []);
        }

        try
        {
            var notes = held.Note.Length > 0 ? new List<string> { held.Note } : [];
            return input.Restore.Asked ? Restored(input, state, started, key, notes)
                : input.ScanOnly ? Scanned(input, state, started, key, side, notes)
                : Moved(input, state, started, key, side, notes);
        }
        finally
        {
            SideLease.Release(input.Archive, held, input.JudgedBase.Folder);
        }
    }

    private static ArchiveRunReport Moved(ArchiveRunInput input, ArchiveState state, DateTimeOffset started, byte[] key, string side, List<string> notes)
    {
        var meter = new RunMeter(input, started);
        // The E9.S5 amendment: the Windows side's view judges whether a Claude Code session is idle, from times read at each question.
        var inUse = input.InUseScan(input).WithIdle(WindowsIdle.Of(input.Files, input.Config, input.Clock));
        var context = ContextOf(input, state, key, inUse, meter.Moved);
        var reconcile = ArchiveReconcile.FromInflight(context);
        var selectionInput = new SelectionInput(input.Paths, input.Files, input.Config, input.Clock.GetUtcNow(), input.Zone, inUse, input.Environment) { OnlyAgent = input.OnlyAgent, Token = input.Token };
        var selected = Selection.Select(selectionInput).Where(s => s.Enabled).ToList();
        foreach (var agent in selected)
        {
            reconcile = ArchiveReconcile.RenameBack(context, agent.Entry.Id, agent.Under, agent.QuarantinedFiles, reconcile);
        }

        var tally = new RunTally(selected.Select(s => s.Entry.Id));
        var removal = RemoveDue(input, context, selected, tally, meter);
        var stop = removal.Stop.Stopped ? removal.Stop : CopyDue(input, context, selected, tally, meter);
        var report = Answer(input, started, stop.Stopped ? RunOutcomes.Stopped : RunOutcomes.Done, stop.Why, reconcile with { Notes = [.. reconcile.Notes, .. notes, .. removal.Notes] }, ArchivePreview.InUseOf(selectionInput.InUse), tally.Reports(selected, context.Book)) with { StopKind = stop.Kind };
        _ = state.WriteLastRun(new LastRunRecord(ArchiveState.Version, input.RunId, started, report.EndedUtc, report.Outcome, report.Stop, report.Agents.Sum(a => a.Copied), report.Agents.Sum(a => a.Removed), report.Agents.Sum(a => a.CopiedBytes)));
        return report with { FilesPerSecond = meter.FilesPerSecond, MegabytesPerSecond = meter.MegabytesPerSecond };
    }

    private static ArchiveRunReport Scanned(ArchiveRunInput input, ArchiveState state, DateTimeOffset started, byte[] key, string side, List<string> notes)
    {
        var context = ContextOf(input, state, key, InUseView.NotChecked("a scan removes nothing"), static (_, _) => { });
        var agents = ArchiveTargets.Of(input.Paths, input.Files, input.Config, input.OnlyAgent, input.Environment).Where(t => t.Enabled).Select(t => t.Entry.Id);
        var scan = ArchiveScan.Scan(context, input.Files, agents, input.Token);
        return Answer(input, started, RunOutcomes.Done, string.Empty, ArchiveReconcileReport.Empty with { Notes = notes }, NotChecked, []) with { Scan = scan };
    }

    /// <summary>The context every verb of a side's archive acts in: the seam, the base, the side, the run, the key, the book.</summary>
    internal static MoveContext ContextOf(ArchiveRunInput input, ArchiveState state, byte[] key, InUseView inUse, Action<long, bool> progress) =>
        new(input.Archive, input.JudgedBase.Folder, SideOf(input), input.RunId, key, input.Clock, input.Zone, new InflightBook(state), input.Step, progress)
        {
            Stats = input.Files,
            Home = input.Paths.Home,
            InUse = inUse,
            Distro = DistroOf(input.Paths),
        };

    /// <summary>E9.S3: the reconcile first (an interrupted run is finished before anything is restored), then the restore.</summary>
    private static ArchiveRunReport Restored(ArchiveRunInput input, ArchiveState state, DateTimeOffset started, byte[] key, List<string> notes)
    {
        var meter = new RunMeter(input, started);
        var context = ContextOf(input, state, key, InUseView.NotChecked("a restore removes nothing"), meter.Moved);
        var reconcile = ArchiveReconcile.FromInflight(context);
        var targets = ArchiveTargets.Of(input.Paths, input.Files, input.Config, input.Restore.Agent, input.Environment);
        var selection = ArchiveRestore.Select(context, input.Files, input.Restore, targets);
        var restore = ArchiveRestore.Restore(context, selection, input.Restore.AcceptUnverified, meter.WouldOverrun);
        return Answer(input, started, RunOutcomes.Done, string.Empty, reconcile with { Notes = [.. reconcile.Notes, .. notes] }, NotChecked, []) with { Restore = restore };
    }

    /// <summary>A path as the open-file scan spells it: the distribution's spelling on Linux, the path itself elsewhere.</summary>
    private static Func<string, string> DistroOf(IHostPaths paths) => paths is LinuxHostPaths linux ? linux.ToDistro : static p => p;

    /// <summary>What phase 2 did: why it stopped (if it did) and what it let go.</summary>
    private sealed record RemovalPass(RunStop Stop, IReadOnlyList<string> Notes);

    /// <summary>Phase 2 for every entry whose <c>archived</c> is at least <c>archive.removeAfterHours</c> old, within the budget (correctness
    /// M4: both phases share <c>--budget-seconds</c>). An entry of an agent <c>archive.agents</c> no longer names is let go (m2: never
    /// stranded); one of an agent this run was not asked for (<c>--agent</c>) waits.</summary>
    private static RemovalPass RemoveDue(ArchiveRunInput input, MoveContext context, IReadOnlyList<AgentSelection> selected, RunTally tally, RunMeter meter)
    {
        var after = TimeSpan.FromHours(input.Config.Int(ConfigKeys.Archive.RemoveAfterHours));
        var pass = new RemovalScope(
            selected.Select(s => s.Entry.Id).ToHashSet(StringComparer.Ordinal),
            ArchiveTargets.Of(input.Paths, input.Files, input.Config, string.Empty, input.Environment).Where(t => t.Enabled).Select(t => t.Entry.Id).ToHashSet(StringComparer.Ordinal),
            []);
        foreach (var entry in context.Book.Entries.Where(e => e.State == InflightStates.Archived && input.Clock.GetUtcNow() - e.ArchivedAtUtc >= after).ToList())
        {
            var stop = RemoveOne(input, context, tally, meter, entry, pass);
            if (stop.Stopped)
            {
                return new RemovalPass(stop, pass.Notes);
            }
        }

        return new RemovalPass(RunStop.None, pass.Notes);
    }

    /// <summary>The agents this run was asked for, those <c>archive.agents</c> names, and what phase 2 let go.</summary>
    private sealed record RemovalScope(IReadOnlySet<string> Asked, IReadOnlySet<string> Archived, List<string> Notes);

    private static RunStop RemoveOne(ArchiveRunInput input, MoveContext context, RunTally tally, RunMeter meter, InflightEntry entry, RemovalScope pass)
    {
        if (input.Token.IsCancellationRequested)
        {
            return new RunStop(StopKinds.Cancelled, "the run was stopped");
        }

        if (!pass.Archived.Contains(entry.Agent))
        {
            context.Book.Drop(entry.EntryId);
            pass.Notes.Add($"{entry.Key}: its agent {entry.Agent} is no longer in {ConfigKeys.Archive.Agents.Name}; the archived session is let go and its source stays");
            return RunStop.None;
        }

        return pass.Asked.Contains(entry.Agent) ? Removed(input, context, tally, meter, entry) : RunStop.None;
    }

    private static RunStop Removed(ArchiveRunInput input, MoveContext context, RunTally tally, RunMeter meter, InflightEntry entry)
    {
        var indexed = MonthIndex.Entries(context, entry.Agent, entry.Month).FirstOrDefault(e => e.EntryId == entry.EntryId);
        var bytes = indexed?.Files.Sum(f => f.Bytes) ?? 0;
        if (meter.WouldOverrun(bytes * 2))
        {
            return new RunStop(StopKinds.Budget, $"the budget ({input.Budget.TotalSeconds:0} s) would not fit the next removal (its copies and its source hashed: {bytes * 2} bytes at the rate measured); the rest wait for the next run");
        }

        var outcome = indexed is null ? new RemoveOutcome.Kept("its index line is not readable") : ArchiveRemove.Remove(context, entry, indexed);
        tally.Removal(entry.Agent, ArchiveRemove.LetGo(context, entry, outcome));
        return RunStop.None;
    }

    /// <summary>Phase 1 for the due units, oldest first per agent, never one already on its way; stopped by the budget, the session
    /// cap, the base's free space or a <see cref="CopyOutcome.Stop"/>.</summary>
    private static RunStop CopyDue(ArchiveRunInput input, MoveContext context, IReadOnlyList<AgentSelection> selected, RunTally tally, RunMeter meter)
    {
        var cap = input.Config.Int(ConfigKeys.Archive.MaxSessionsPerRun);
        foreach (var (agent, unit) in selected.SelectMany(s => s.Due.Select(u => (s, u))).Where(p => !OnItsWay(context.Book, p.s.Entry.Id, p.u.Key)))
        {
            if (Limit(input, context, tally, meter, unit, cap) is { Stopped: true } stop)
            {
                return stop;
            }

            var copied = ArchiveCopy.Copy(context, agent.Entry.Id, agent.Under, unit);
            tally.Copy(agent.Entry.Id, copied);
            if (copied is CopyOutcome.Archived { EventOnly: false } archived)
            {
                _ = new ArchiveState(input.Paths, input.Files).Count(agent.Entry.Id, archived.Entry.Month, archived.Files, archived.Bytes, archived.Entry.ArchivedAtUtc);
            }
            if (copied is CopyOutcome.Stop stopped)
            {
                return new RunStop(stopped.Kind, stopped.Why);
            }
        }

        return RunStop.None;
    }

    private static bool OnItsWay(InflightBook book, string agent, string key) => book.Entries.Any(e => e.Agent == agent && e.Key == key);

    /// <summary>Why the next unit is not started; empty when it may be.</summary>
    private static RunStop Limit(ArchiveRunInput input, MoveContext context, RunTally tally, RunMeter meter, UnitFound unit, int cap) =>
        input.Token.IsCancellationRequested ? new RunStop(StopKinds.Cancelled, "the run was stopped")
        : tally.Started >= cap ? new RunStop(StopKinds.SessionCap, $"{ConfigKeys.Archive.MaxSessionsPerRun.Name} ({cap}) sessions this run; the rest go next run")
        : meter.WouldOverrun(unit.Bytes) ? new RunStop(StopKinds.Budget, $"the budget ({input.Budget.TotalSeconds:0} s) would not fit the next session ({unit.Bytes} bytes at the rate measured); the rest go next run, oldest first")
        : FreeSpaceProblem(input, context, unit);

    private static RunStop FreeSpaceProblem(ArchiveRunInput input, MoveContext context, UnitFound unit)
    {
        var keep = (long)input.Config.Int(ConfigKeys.Archive.MinFreeGb) << 30;
        return input.Files.MeasureVolume(context.BaseFolder) is VolumeReadResult.Measured volume && volume.AvailableBytes - unit.Bytes < keep
            ? new RunStop(StopKinds.FreeSpace, $"the base would keep less than {ConfigKeys.Archive.MinFreeGb.Name} free; nothing more is copied")
            : RunStop.None;
    }

    private static ArchiveRunReport Answer(ArchiveRunInput input, DateTimeOffset started, string outcome, string stop, ArchiveReconcileReport reconcile, InUseReport inUse, IReadOnlyList<AgentRunReport> agents) =>
        new(
            SchemaVersion.Current,
            input.Paths.Side == HostSide.Wsl ? "wsl" : "windows",
            SideOf(input),
            input.RunId,
            started,
            input.Clock.GetUtcNow(),
            input.JudgedBase.Folder,
            outcome,
            stop,
            reconcile,
            inUse,
            agents,
            0,
            0,
            []);
}

/// <summary>What one run counted per agent.</summary>
internal sealed class RunTally(IEnumerable<string> agents)
{
    private readonly Dictionary<string, AgentRunReport> _agents = agents.Distinct(StringComparer.Ordinal)
        .ToDictionary(a => a, a => new AgentRunReport(a, true, 0, 0, 0, 0, 0, 0, 0, 0, 0, [], string.Empty), StringComparer.Ordinal);

    public int Started { get; private set; }

    public void Copy(string agent, CopyOutcome outcome)
    {
        Started++;
        var a = _agents[agent];
        _agents[agent] = outcome switch
        {
            CopyOutcome.Archived archived => a with { Copied = a.Copied + 1, CopiedFiles = a.CopiedFiles + archived.Files, CopiedBytes = a.CopiedBytes + archived.Bytes },
            CopyOutcome.GoneAtSource => a with { GoneAtSource = a.GoneAtSource + 1 },
            CopyOutcome.Skipped skipped => a with { Skipped = Added(a.Skipped, "copy-skipped", skipped.Why) },
            CopyOutcome.Stop stopped => a with { Skipped = Added(a.Skipped, stopped.Kind, stopped.Why) },
            _ => a,
        };
    }

    public void Removal(string agent, RemoveOutcome outcome)
    {
        var a = _agents[agent];
        _agents[agent] = outcome switch
        {
            RemoveOutcome.Removed removed => a with { Removed = a.Removed + 1, RemovedBytes = a.RemovedBytes + removed.Bytes },
            RemoveOutcome.Superseded => a with { Superseded = a.Superseded + 1 },
            RemoveOutcome.Damaged => a with { Damaged = a.Damaged + 1 },
            RemoveOutcome.Kept kept => a with { Skipped = Added(a.Skipped, SkipRule.RemovalWaits, kept.Why) },
            RemoveOutcome.Dropped dropped => a with { Skipped = Added(a.Skipped, SkipRule.Dropped, dropped.Why) },
            _ => a,
        };
    }

    private static IReadOnlyList<SkipCount> Added(IReadOnlyList<SkipCount> counts, string rule, string why) =>
        counts.Any(c => c.Rule == rule)
            ? [.. counts.Select(c => c.Rule == rule ? c with { Count = c.Count + 1 } : c)]
            : [.. counts, new SkipCount(rule, 1, string.Empty, why)];

    public IReadOnlyList<AgentRunReport> Reports(IReadOnlyList<AgentSelection> selected, InflightBook book) =>
        [.. selected.Select(s => _agents[s.Entry.Id] with
        {
            Waiting = book.Entries.Count(e => e.Agent == s.Entry.Id),
            Skipped = [.. _agents[s.Entry.Id].Skipped, .. s.Skipped.GroupBy(u => u.SkipRule, StringComparer.Ordinal).Select(g => new SkipCount(g.Key, g.Count(), string.Empty, g.First().Skip))],
            Note = s.Note,
        })];
}

/// <summary>The run's clock and rate: bytes moved, files done, the progress line after each, the budget's estimate.</summary>
internal sealed class RunMeter(ArchiveRunInput input, DateTimeOffset started)
{
    private long _bytes;
    private int _files;

    /// <summary>Bytes copied (a chunk) or a file done — a file done is a progress line (it names nothing).</summary>
    public void Moved(long bytes, bool fileDone)
    {
        _bytes += bytes;
        if (fileDone)
        {
            _files++;
            input.Progress(new ArchiveProgressLine("file", _files, _bytes, Elapsed.TotalSeconds));
        }
    }

    private TimeSpan Elapsed => input.Clock.GetUtcNow() - started;

    public double MegabytesPerSecond => Elapsed.TotalSeconds <= 0 ? 0 : _bytes / 1048576.0 / Elapsed.TotalSeconds;

    public double FilesPerSecond => Elapsed.TotalSeconds <= 0 ? 0 : _files / Elapsed.TotalSeconds;

    /// <summary>Whether <paramref name="bytes"/> more at the rate measured so far would pass the budget (no estimate before the first byte).</summary>
    public bool WouldOverrun(long bytes) =>
        Elapsed >= input.Budget || (_bytes > 0 && Elapsed + TimeSpan.FromSeconds(bytes / (_bytes / Math.Max(Elapsed.TotalSeconds, 0.001))) > input.Budget);
}
