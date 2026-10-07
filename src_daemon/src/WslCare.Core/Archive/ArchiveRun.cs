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
}

/// <summary>A progress line of <c>archive run --json</c>: no names, a kind (<c>file</c>, <c>heartbeat</c>), the counts so far.</summary>
public sealed record ArchiveProgressLine(string Progress, int Files, long Bytes, double Seconds);

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

    /// <summary>The fault seam (<see cref="MoveSteps"/>); does nothing in a real run.</summary>
    public Action<string> Step { get; init; } = static _ => { };

    /// <summary>Told after every file copied or removed.</summary>
    public Action<ArchiveProgressLine> Progress { get; init; } = static _ => { };

    /// <summary>Whether the base answers within <c>archive.reachabilitySeconds</c> (D1: a share that stops answering blocks the reader in
    /// the kernel — the run asks first, in a task it can abandon).</summary>
    public Func<string, TimeSpan, bool> Reachable { get; init; } = static (folder, ceiling) => Task.Run(() => Directory.Exists(folder)).Wait(ceiling);

    /// <summary>The open-file check (D2.2) — the real scan; a test hands its own view.</summary>
    public Func<ArchiveRunInput, InUseView> InUseScan { get; init; } = static i => InUse.Scan(i.Paths, i.Files, i.Token);

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
        var early = Early(input, state);
        return early.Length > 0 ? Answer(input, started, Outcome(early), early, ArchiveReconcileReport.Empty, NotChecked, []) : Locked(input, state, started);
    }

    private static readonly InUseReport NotChecked = new("not-checked", 0, 0, string.Empty);

    /// <summary>What ends the run before anything is touched: no base, a refused base, a changed mount. A marker word leads.</summary>
    private static string Early(ArchiveRunInput input, ArchiveState state) =>
        input.Config.Text(ConfigKeys.Archive.BaseFolder).Length == 0 ? $"{RunOutcomes.NoBase}|no {ConfigKeys.Archive.BaseFolder.Name} is set; the archive is not configured"
        : !input.JudgedBase.Accepted ? $"{RunOutcomes.Refused}|the base is refused by its rules ({input.JudgedBase.Rule}: {input.JudgedBase.Refusal})"
        : MountProblem(input.JudgedBase, state);

    private static string Outcome(string early) => early[..early.IndexOf('|', StringComparison.Ordinal)];

    /// <summary>D7: the base's mount recorded at its first run and compared at every later one — an unmounted share leaves a plain folder
    /// on the distribution's own disk, which must never be written.</summary>
    private static string MountProblem(BaseFolderReport judged, ArchiveState state)
    {
        state.EnsureFolder();
        var now = new BaseRecord(ArchiveState.Version, judged.Folder, judged.Mount.Type, judged.Mount.Source, judged.Mount.MountPoint);
        var recorded = state.Base();
        return recorded is null || recorded.Folder != now.Folder ? Recorded(state, now)
            : recorded with { V = now.V } == now ? string.Empty
            : $"{RunOutcomes.Refused}|the base is not mounted as when it was first used ({recorded.MountType} {recorded.MountSource} at {recorded.MountPoint}; now {now.MountType} {now.MountSource} at {now.MountPoint})";
    }

    private static string Recorded(ArchiveState state, BaseRecord now) =>
        state.WriteBase(now) is { Length: > 0 } unwritten ? $"{RunOutcomes.Refused}|the base's mount could not be recorded ({unwritten})" : string.Empty;

    private static ArchiveRunReport Locked(ArchiveRunInput input, ArchiveState state, DateTimeOffset started)
    {
        if (input.Files.TryLockExclusive(state.LockFile) is not ExclusiveLock.Held held)
        {
            return Answer(input, started, RunOutcomes.Busy, "another archive run of this side holds its lock", ArchiveReconcileReport.Empty, NotChecked, []);
        }

        using (held.Handle)
        {
            _ = state.WriteHolder(new LockHolderRecord(ArchiveState.Version, input.Me.Pid, input.Me.StartTicks, input.Me.BootId, started, input.RunId));
            return Reached(input, state, started);
        }
    }

    private static ArchiveRunReport Reached(ArchiveRunInput input, ArchiveState state, DateTimeOffset started)
    {
        var baseFolder = input.JudgedBase.Folder;
        if (!input.Reachable(baseFolder, TimeSpan.FromSeconds(input.Config.Int(ConfigKeys.Archive.ReachabilitySeconds))))
        {
            return Answer(input, started, RunOutcomes.Unreachable, $"the base did not answer within {ConfigKeys.Archive.ReachabilitySeconds.Name}; nothing was touched (the reconcile waits too)", ArchiveReconcileReport.Empty, NotChecked, []);
        }

        var key = state.IndexKey();
        return key.Length == 0
            ? Answer(input, started, RunOutcomes.Refused, "the side's index key could not be read or made; nothing is written without it", ArchiveReconcileReport.Empty, NotChecked, [])
            : Leased(input, state, started, key);
    }

    private static ArchiveRunReport Leased(ArchiveRunInput input, ArchiveState state, DateTimeOffset started, byte[] key)
    {
        var side = SideName.OfThisProcess(input.Paths.Side);
        var taken = SideLease.Take(input.Archive, input.JudgedBase.Folder, side, input.Me with { RunId = input.RunId, SinceUtc = started }, input.Processes);
        if (taken is not LeaseTaken.Held held)
        {
            return Answer(input, started, RunOutcomes.Refused, ((LeaseTaken.Refused)taken).Why, ArchiveReconcileReport.Empty, NotChecked, []);
        }

        try
        {
            var notes = held.Note.Length > 0 ? new List<string> { held.Note } : [];
            return input.ScanOnly ? Scanned(input, state, started, key, side, notes) : Moved(input, state, started, key, side, notes);
        }
        finally
        {
            SideLease.Release(input.Archive, held, input.JudgedBase.Folder);
        }
    }

    private static ArchiveRunReport Moved(ArchiveRunInput input, ArchiveState state, DateTimeOffset started, byte[] key, string side, List<string> notes)
    {
        var meter = new RunMeter(input, started);
        var context = new MoveContext(input.Archive, input.JudgedBase.Folder, side, input.RunId, key, input.Clock, input.Zone, new InflightBook(state), input.Step, meter.Moved) { Stats = input.Files };
        var reconcile = ArchiveReconcile.FromInflight(context);
        var selectionInput = new SelectionInput(input.Paths, input.Files, input.Config, input.Clock.GetUtcNow(), input.Zone, input.InUseScan(input), input.Environment) { OnlyAgent = input.OnlyAgent, Token = input.Token };
        var selected = Selection.Select(selectionInput).Where(s => s.Enabled).ToList();
        foreach (var agent in selected)
        {
            reconcile = ArchiveReconcile.RenameBack(context, agent.Entry.Id, agent.Under, agent.QuarantinedFiles, reconcile);
        }

        var tally = new RunTally(selected.Select(s => s.Entry.Id));
        var stop = RemoveDue(input, context, selected, tally) is { Length: > 0 } removalStop ? removalStop : CopyDue(input, context, selected, tally, meter);
        var report = Answer(input, started, stop.Length == 0 ? RunOutcomes.Done : RunOutcomes.Stopped, stop, reconcile with { Notes = [.. reconcile.Notes, .. notes] }, ArchivePreview.InUseOf(selectionInput.InUse), tally.Reports(selected, context.Book));
        _ = state.WriteLastRun(new LastRunRecord(ArchiveState.Version, input.RunId, started, report.EndedUtc, report.Outcome, report.Stop, report.Agents.Sum(a => a.Copied), report.Agents.Sum(a => a.Removed), report.Agents.Sum(a => a.CopiedBytes)));
        return report with { FilesPerSecond = meter.FilesPerSecond, MegabytesPerSecond = meter.MegabytesPerSecond };
    }

    private static ArchiveRunReport Scanned(ArchiveRunInput input, ArchiveState state, DateTimeOffset started, byte[] key, string side, List<string> notes)
    {
        var context = new MoveContext(input.Archive, input.JudgedBase.Folder, side, input.RunId, key, input.Clock, input.Zone, new InflightBook(state), input.Step, static (_, _) => { }) { Stats = input.Files };
        var agents = ArchiveTargets.Of(input.Paths, input.Files, input.Config, input.OnlyAgent, input.Environment).Where(t => t.Enabled).Select(t => t.Entry.Id);
        var scan = ArchiveScan.Scan(context, input.Files, agents, input.Token);
        return Answer(input, started, RunOutcomes.Done, string.Empty, ArchiveReconcileReport.Empty with { Notes = notes }, NotChecked, []) with { Scan = scan };
    }

    /// <summary>Phase 2 for every entry of a selected agent whose <c>archived</c> is at least <c>archive.removeAfterHours</c> old.</summary>
    private static string RemoveDue(ArchiveRunInput input, MoveContext context, IReadOnlyList<AgentSelection> selected, RunTally tally)
    {
        var after = TimeSpan.FromHours(input.Config.Int(ConfigKeys.Archive.RemoveAfterHours));
        var agents = selected.Select(s => s.Entry.Id).ToHashSet(StringComparer.Ordinal);
        var due = context.Book.Entries.Where(e => e.State == InflightStates.Archived && agents.Contains(e.Agent) && input.Clock.GetUtcNow() - e.ArchivedAtUtc >= after).ToList();
        foreach (var entry in due.TakeWhile(_ => !input.Token.IsCancellationRequested))
        {
            var indexed = MonthIndex.Entries(context, entry.Agent, entry.Month).FirstOrDefault(e => e.EntryId == entry.EntryId);
            tally.Removal(entry.Agent, indexed is null ? new RemoveOutcome.Kept("its index line is not readable") : ArchiveRemove.Remove(context, entry, indexed));
        }

        return string.Empty;
    }

    /// <summary>Phase 1 for the due units, oldest first per agent, never one already on its way; stopped by the budget, the session
    /// cap, the base's free space or a <see cref="CopyOutcome.Stop"/>.</summary>
    private static string CopyDue(ArchiveRunInput input, MoveContext context, IReadOnlyList<AgentSelection> selected, RunTally tally, RunMeter meter)
    {
        var cap = input.Config.Int(ConfigKeys.Archive.MaxSessionsPerRun);
        foreach (var (agent, unit) in selected.SelectMany(s => s.Due.Select(u => (s, u))).Where(p => !OnItsWay(context.Book, p.s.Entry.Id, p.u.Key)))
        {
            if (Limit(input, context, tally, meter, unit, cap) is { Length: > 0 } stop)
            {
                return stop;
            }

            var copied = ArchiveCopy.Copy(context, agent.Entry.Id, agent.Under, unit);
            tally.Copy(agent.Entry.Id, copied);
            if (copied is CopyOutcome.Archived archived)
            {
                _ = new ArchiveState(input.Paths, input.Files).Count(agent.Entry.Id, unit.Month, archived.Files, archived.Bytes, archived.Entry.ArchivedAtUtc);
            }
            if (copied is CopyOutcome.Stop stopped)
            {
                return stopped.Why;
            }
        }

        return string.Empty;
    }

    private static bool OnItsWay(InflightBook book, string agent, string key) => book.Entries.Any(e => e.Agent == agent && e.Key == key);

    /// <summary>Why the next unit is not started; empty when it may be.</summary>
    private static string Limit(ArchiveRunInput input, MoveContext context, RunTally tally, RunMeter meter, UnitFound unit, int cap) =>
        input.Token.IsCancellationRequested ? "the run was stopped"
        : tally.Started >= cap ? $"{ConfigKeys.Archive.MaxSessionsPerRun.Name} ({cap}) sessions this run; the rest go next run"
        : meter.WouldOverrun(unit.Bytes) ? $"the budget ({input.Budget.TotalSeconds:0} s) would not fit the next session ({unit.Bytes} bytes at the rate measured); the rest go next run, oldest first"
        : FreeSpaceProblem(input, context, unit);

    private static string FreeSpaceProblem(ArchiveRunInput input, MoveContext context, UnitFound unit)
    {
        var keep = (long)input.Config.Int(ConfigKeys.Archive.MinFreeGb) << 30;
        return input.Files.MeasureVolume(context.BaseFolder) is VolumeReadResult.Measured volume && volume.AvailableBytes - unit.Bytes < keep
            ? $"the base would keep less than {ConfigKeys.Archive.MinFreeGb.Name} free; nothing more is copied"
            : string.Empty;
    }

    private static ArchiveRunReport Answer(ArchiveRunInput input, DateTimeOffset started, string outcome, string stop, ArchiveReconcileReport reconcile, InUseReport inUse, IReadOnlyList<AgentRunReport> agents) =>
        new(
            SchemaVersion.Current,
            input.Paths.Side == HostSide.Wsl ? "wsl" : "windows",
            SideName.OfThisProcess(input.Paths.Side),
            input.RunId,
            started,
            input.Clock.GetUtcNow(),
            input.JudgedBase.Folder,
            outcome,
            stop.Contains('|', StringComparison.Ordinal) ? stop[(stop.IndexOf('|', StringComparison.Ordinal) + 1)..] : stop,
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
            CopyOutcome.Stop stopped => a with { Skipped = Added(a.Skipped, "verification-failed", stopped.Why) },
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
            RemoveOutcome.Kept kept => a with { Skipped = Added(a.Skipped, "removal-waits", kept.Why) },
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
