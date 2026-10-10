using System.Globalization;

using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Config;
using WslCare.Core.Hosting;
using WslCare.Core.Mcp;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Records;

namespace WslCare.Core.Actions.Suspects;

/// <summary>Why one MCP server instance of the target user is kept — or <see cref="Eligible"/>.</summary>
public sealed record McpStopJudgement(McpFound Instance, PidSample? Sample, TimeSpan IdleFor, string Kept)
{
    public bool Eligible => Kept.Length == 0;

    /// <summary>How long it has been busy without a log write by root's ledger (plan E14 S2b); zero when it has not.</summary>
    public TimeSpan BusyFor { get; init; }

    /// <summary>Selected for its busy streak rather than for its idle time — its signal drops the CPU re-check.</summary>
    public bool Busy { get; init; }
}

/// <summary>
/// A19 (plan E14 S2a, the owner's decision of 2026-10-08): stop the TARGET user's IDLE MCP servers — a button AND automatic
/// (<c>auto.A19</c>, on by default; the daemon's dry-run rules govern what the timer actually does). ALL of: an instance of a
/// watched MCP server (<see cref="McpInstances"/>: the PROGRAM is the server, so an agent process is never one); the target
/// user's, never root's, never this process, not a zombie; no terminal (the shared signal path keeps a process with one); no
/// child process (a server waiting on a child it started spends no CPU of its own — the risk consultation of 2026-10-08); the
/// process the snapshot saw (its start ticks and account re-read); and NO CPU for <c>mcpWatchdog.idleMinutes</c> measured by
/// identity over a dense chain of the timer's sightings (<see cref="AgentCpuHistory"/> — missing history is not idle) — or, for a
/// server re-parented to INIT (its agent died), <c>mcpWatchdog.orphanIdleMinutes</c> — never an orphan of a program the USER listed
/// (<c>mcpServers.programs</c>, plan E14 S2c), whose name may then be another program's. Signals as A11 and A18
/// (<see cref="SuspectSignals"/>): SIGTERM, SIGKILL after the grace, by pid AND start, each re-read just before.
/// </summary>
/// <remarks>A BUTTON run is bound to what its modal showed, as A18's (<see cref="Shown"/>). What an AI agent does with an ended
/// stdio server is NOT measured (plan S2, owed): every item says the session may need <c>/mcp</c> to reconnect it.</remarks>
public sealed class McpServerStop : ICleanupAction, IBoundToShownList
{
    private const string Kind = "process";

    /// <summary>The kind of a BUSY target's item (plan E14 S2b): the signal path drops its CPU re-check for it alone.</summary>
    public const string BusyKind = "busy process";
    private const int InitPid = 1;

    public const string NotTargetUser = "it is not the target user's";
    public const string HasTerminal = "it has a terminal (the shared signal path keeps a process with one)";
    public const string HasChild = "it has a child process (it may be waiting on work it started)";
    public const string NotTheSnapshot = "its /proc entry is not the process the snapshot saw (another start, another account, or gone)";
    public const string UsedCpu = "it used CPU within its window, or its CPU history is missing, broken by a gap or shorter on the monotonic clock; and root's CPU ledger shows no busy-without-activity streak as long as mcpWatchdog.busyMinutes";
    public const string Reconnect = "the agent's session may need /mcp to reconnect it";

    public const string UserProgramOrphan =
        "it is an orphaned process of a user-added program (mcpServers.programs): once no agent holds it, a name the user chose may be another program";

    public const string ButtonNeedsShownProcesses =
        "a button run of A19 must pass the processes its preview SHOWED (--process <pid:start>): A19 ends only those, judged again (plan E14 S2a)";

    public ActionId Id { get; } = ActionId.Find("A19")!;

    public string Summary => $"SIGTERM, then SIGKILL after {Tuning.Current.Text(ConfigKeys.Processes.TermGraceSeconds)} s, of the target user's MCP servers with no CPU for {Tuning.Current.Text(ConfigKeys.McpWatchdog.IdleMinutes)} min (measured; {Tuning.Current.Text(ConfigKeys.McpWatchdog.OrphanIdleMinutes)} min when their agent died) or busy without a log write for {Tuning.Current.Text(ConfigKeys.McpWatchdog.BusyMinutes)} min - by pid and start, never by name";

    public CommandScope Scope => CommandScope.User;

    public IdleRule Idle => IdleRule.Never;

    public IReadOnlyList<HostSide> Sides { get; } = [HostSide.Wsl];

    public IReadOnlyList<CommandTemplate> Commands { get; } = [];

    /// <summary>Every process the preview selected, as <c>pid:start</c> — what a button run passes back.</summary>
    public IReadOnlyList<string> Shown(ActionPreview preview) => [.. preview.Targets.Select(t => SuspectSignals.Shown(t.Key)).Take(ShownList.MaxNames)];

    public Task<ActionPreview> PreviewAsync(ActionContext context, ActionCommands commands, CancellationToken cancellationToken)
    {
        var idle = TimeSpan.FromMinutes(context.Config.Int(ConfigKeys.McpWatchdog.IdleMinutes));
        // Own code review, finding 6: an orphan never waits longer than a server whose agent lives.
        var windows = new IdleWindows(idle, TimeSpan.FromMinutes(Math.Min(context.Config.Int(ConfigKeys.McpWatchdog.OrphanIdleMinutes), context.Config.Int(ConfigKeys.McpWatchdog.IdleMinutes))))
        {
            Busy = TimeSpan.FromMinutes(context.Config.Int(ConfigKeys.McpWatchdog.BusyMinutes)),
        };
        var what = string.Create(CultureInfo.InvariantCulture, $"the target user's MCP servers with no CPU for {windows.Idle.TotalMinutes:0} min ({windows.Orphan.TotalMinutes:0} min when their agent died), or busy without a log write for {windows.Busy.TotalMinutes:0} min, measured - SIGTERM, then SIGKILL after {SuspectTermination.Grace.TotalSeconds:0} s; {Reconnect}");
        return Task.FromResult(ShownProcessBinding.Bound(Preview(context, what, windows, cancellationToken), context, ButtonNeedsShownProcesses));
    }

    /// <summary>The two idle windows in force: an instance's, and one whose agent died — and the busy streak a target needs
    /// (plan E14 S2b; zero selects no busy target).</summary>
    public sealed record IdleWindows(TimeSpan Idle, TimeSpan Orphan)
    {
        public TimeSpan Busy { get; init; }
    }

    private static ActionPreview Preview(ActionContext context, string what, IdleWindows windows, CancellationToken cancellationToken)
    {
        if (context.TargetUser is not TargetUserResult.Found { User: var user })
        {
            return ActionPreview.Unavailable(what, $"A19 stops only the target user's MCP servers, and there is no single target user ({context.TargetUser.Refusal})");
        }

        if (context.Paths is not LinuxHostPaths linux || context.Processes(cancellationToken) is not Reading<ProcessSnapshot>.Available { Value: var snapshot })
        {
            return ActionPreview.Unavailable(what, "the process table could not be read");
        }

        var boot = BootIdentity.Read(linux, context.Files);
        if (boot.Length == 0)
        {
            return ActionPreview.Unavailable(what, "the boot id cannot be read, so no process's idle time can be told");
        }

        var at = SampleTime.Of(context.Clock);
        var watched = McpSettings.From(context.Config).Watched;
        var samples = AgentCpuHistory.Sample(linux, context.Files, snapshot.All, watched);
        // A preview writes no state: the history is the timer runs' (merged with now in memory, never written here).
        var history = AgentCpuHistory.Next(AgentCpuHistory.Read(linux, context.Files), boot, samples, at);
        // Plan E14 S2b: the busy evidence is ROOT's ledger only — written by the timer's and the watch's samples, never a user's.
        var place = McpCpuLedgerPlace.ForStatus(linux, root: true);
        var ledger = McpCpuLedger.Read(context.Files, place, context.Config.Int(ConfigKeys.Records.MaxStateFileBytes));
        var current = TimeSpan.FromMinutes(context.Config.Int(ConfigKeys.McpServers.CpuIntervalMaxMinutes));
        var now = new McpCpuPoint(0, at.Wall, at.MonotonicMs);
        var judged = Judge(new Judging(user, snapshot.All, watched, samples, s => AgentCpuHistory.IdleFor(history, boot, s, at), windows)
        {
            BusyFor = s => McpCpuLedger.BusyFor(ledger, boot, s.Pid, s.StartTicks, now, current),
        });
        var items = judged.Where(j => j.Eligible).Select(Item).ToList();
        var basis = $"MCP servers read from /proc; idle by the CPU history the timer's runs record ({AgentCpuHistory.File(linux)}), busy by root's CPU ledger ({place.FileOrEmpty}); {judged.Count - items.Count} MCP server(s) of {user.Name} kept: {Kept(judged)}";
        var facts = new Dictionary<string, long>(StringComparer.Ordinal) { [SuspectTermination.HeldMemoryFact] = items.Sum(i => i.Bytes ?? 0) };
        return ActionPreview.Of(what, items.Count, null, basis, facts, string.Empty, items);
    }

    /// <summary>What one judgement reads: the target user, the whole process table, the watched servers, the samples, the idle
    /// clock and the two windows.</summary>
    public sealed record Judging(TargetUser User, IReadOnlyList<ProcessEntry> Processes, IReadOnlyList<McpServerEntry> Watched, IReadOnlyList<PidSample> Samples, Func<PidSample, TimeSpan> IdleFor, IdleWindows Windows)
    {
        /// <summary>How long a sample's identity has been busy without a log write (plan E14 S2b); none by default.</summary>
        public Func<PidSample, TimeSpan> BusyFor { get; init; } = static _ => TimeSpan.Zero;
    }

    /// <summary>Every instance of a watched MCP server owned by the target user, each judged (plan E14 S2a item 2).</summary>
    public static IReadOnlyList<McpStopJudgement> Judge(Judging judging)
    {
        var parents = Parents(judging.Processes);
        var samples = judging.Samples.ToDictionary(s => s.Pid);
        return [.. McpInstances.Find(judging.Processes, judging.Watched).Instances
            .Where(f => string.Equals(f.Process.User, judging.User.Name, StringComparison.Ordinal) && f.Process.User != "root")
            .Where(f => f.Process.Pid > InitPid && f.Process.Pid != System.Environment.ProcessId && f.Process.State != 'Z')
            .Select(f => JudgeOne(judging, f, parents.Contains(f.Process.Pid), samples.GetValueOrDefault(f.Process.Pid)))];
    }

    /// <summary>Every pid that is some process's parent in <paramref name="processes"/>.</summary>
    private static HashSet<int> Parents(IReadOnlyList<ProcessEntry> processes) => [.. processes.Select(p => p.ParentPid)];

    private static McpStopJudgement JudgeOne(Judging judging, McpFound instance, bool hasChild, PidSample? sample)
    {
        var idle = sample is null ? TimeSpan.Zero : judging.IdleFor(sample);
        var busyFor = sample is null ? TimeSpan.Zero : judging.BusyFor(sample);
        var idleEnough = idle >= WindowOf(instance, judging.Windows);
        var busy = !idleEnough && BusyEnough(busyFor, judging.Windows);
        var kept = (OrphanOfUserProgram(instance), instance.Process.HasTty, hasChild, SameProcess(instance.Process, sample, judging.User), idleEnough || busy) switch
        {
            (true, _, _, _, _) => UserProgramOrphan,
            (_, true, _, _, _) => HasTerminal,
            (_, _, true, _, _) => HasChild,
            (_, _, _, false, _) => NotTheSnapshot,
            (_, _, _, _, false) => UsedCpu,
            _ => string.Empty,
        };
        return new McpStopJudgement(instance, sample, idle, kept) { BusyFor = busyFor, Busy = busy };
    }

    /// <summary>A busy streak at least the window's busy minutes — never with no busy window.</summary>
    private static bool BusyEnough(TimeSpan busyFor, IdleWindows windows) => windows.Busy > TimeSpan.Zero && busyFor >= windows.Busy;

    /// <summary>A user program's instance with no agent above it (plan E14 S2c; coai plan round 2026-10-08): a file name the user
    /// chose is not as specific as a catalogue server's, so it is a target only while an agent still holds it.</summary>
    private static bool OrphanOfUserProgram(McpFound instance) =>
        instance.Server.Origin == McpServerOrigin.UserProgram && instance.Owner is McpOwner.Orphaned;

    /// <summary>The /proc entry read now is the process the snapshot saw — the same start ticks — and runs as the target user (the
    /// risk consultation: pidfd protects the identity it is given, not an attribution made from a stale snapshot).</summary>
    private static bool SameProcess(ProcessEntry process, PidSample? sample, TargetUser user) =>
        sample is not null
        && process.StartTicks is Reading<long>.Available { Value: var start } && start == sample.StartTicks
        && sample.Uid == user.Uid && sample.Tty == 0;

    /// <summary>The orphan window only for a server re-parented to INIT — a child of the user's systemd manager is "orphaned" by the
    /// snapshot's rule but may have a live client (the risk consultation), so it gets the ordinary window.</summary>
    private static TimeSpan WindowOf(McpFound instance, IdleWindows windows) =>
        instance.Owner is McpOwner.Orphaned && instance.Process.ParentPid == InitPid ? windows.Orphan : windows.Idle;

    private static string Kept(IReadOnlyList<McpStopJudgement> judged) =>
        string.Join("; ", judged.Where(j => !j.Eligible).GroupBy(j => j.Kept).Select(g => string.Create(CultureInfo.InvariantCulture, $"{g.Count()} because {g.Key}")));

    private static ActionItem Item(McpStopJudgement judged) =>
        new(judged.Busy ? BusyKind : Kind, string.Create(CultureInfo.InvariantCulture, $"{judged.Instance.Process.Pid} {judged.Instance.Server.Name}"), judged.Instance.Process.HeldBytes,
            string.Create(CultureInfo.InvariantCulture, $"{judged.Instance.Server.Name}, pid {judged.Instance.Process.Pid}, {OwnerText(judged.Instance.Owner)}, {Why(judged)}; {Reconnect}"))
        {
            Key = SuspectSignals.Key(judged.Sample!),
        };

    private static string Why(McpStopJudgement judged) => judged.Busy
        ? string.Create(CultureInfo.InvariantCulture, $"busy without a log write for {judged.BusyFor.TotalMinutes:0} min")
        : string.Create(CultureInfo.InvariantCulture, $"no CPU for {judged.IdleFor.TotalMinutes:0} min");

    private static string OwnerText(McpOwner owner) => owner switch
    {
        McpOwner.Agent agent => string.Create(CultureInfo.InvariantCulture, $"under {agent.Name} (pid {agent.Pid})"),
        _ => "its agent is gone (orphaned)",
    };

    /// <summary>The timer runs it when its <c>auto</c> switch is on (default ON) and any server is a target.</summary>
    public TriggerDecision Trigger(ActionPreview preview, EffectiveConfig config) =>
        new(preview.Count > 0, string.Create(CultureInfo.InvariantCulture, $"{preview.Count} idle or busy-without-activity MCP server(s); the trigger is any"));

    private static int SignalPid(ActionItem target) => int.Parse(target.Key.Split(':')[0], CultureInfo.InvariantCulture);

    public async Task<ActionRun> RunAsync(ActionContext context, ActionPreview preview, ActionCommands commands, CancellationToken cancellationToken)
    {
        if (preview.Targets.Count == 0 || context.Paths is not LinuxHostPaths linux)
        {
            return ActionRun.Nothing(commands.Ran, "no MCP server is idle or busy without activity");
        }

        // coai code round 2026-10-08, finding 2: the child guard again, on the process table as it is NOW — a server that
        // started work since the preview may be waiting on it (the signal path re-checks identity, CPU, terminal and owner only).
        var parents = context.Processes(cancellationToken) is Reading<ProcessSnapshot>.Available { Value: var table } ? Parents(table.All) : null;
        var withChild = preview.Targets.Where(t => parents is null || parents.Contains(SignalPid(t))).ToList();
        // Plan E14 S2b: a BUSY target moves its CPU by definition — its re-check keeps the identity, account and terminal, not the CPU.
        var judged = await SuspectSignals.EndAllAsync(context, linux, [.. preview.Targets.Except(withChild)], SuspectTermination.Grace, static item => item.Kind == BusyKind, cancellationToken).ConfigureAwait(false);
        var verdicts = judged.Select(j => SuspectSignals.Verdict(j.Item, j.Outcome)).ToList();
        var keptForChild = withChild.Select(t => t with { Note = parents is null ? "not signalled: the process table could not be read again just before the signal" : $"kept: {HasChild}" });
        var ended = verdicts.Where(v => v.Ended).Select(v => v.Item).ToList();
        return new ActionRun(ended.Count, null, "A19 frees memory, not disk: each stopped server's item carries what it held", null, null, ended, commands.Ran, string.Join("; ", verdicts.Where(v => v.Failure.Length > 0).Select(v => v.Failure).Take(5)))
        {
            NotRemoved = [.. verdicts.Where(v => !v.Ended).Select(v => v.Item), .. keptForChild],
        };
    }
}
