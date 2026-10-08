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
}

/// <summary>
/// A19 (plan E14 S2a, the owner's decision of 2026-10-08): stop the TARGET user's IDLE MCP servers — a button AND automatic
/// (<c>auto.A19</c>, on by default; the daemon's dry-run rules govern what the timer actually does). ALL of: an instance of a
/// watched MCP server (<see cref="McpInstances"/>: the PROGRAM is the server, so an agent process is never one); the target
/// user's, never root's, never this process, not a zombie; no terminal (the shared signal path keeps a process with one); no
/// child process (a server waiting on a child it started spends no CPU of its own — the risk consultation of 2026-10-08); the
/// process the snapshot saw (its start ticks and account re-read); and NO CPU for <c>mcpWatchdog.idleMinutes</c> measured by
/// identity over a dense chain of the timer's sightings (<see cref="AgentCpuHistory"/> — missing history is not idle) — or, for a
/// server re-parented to INIT (its agent died), <c>mcpWatchdog.orphanIdleMinutes</c>. Signals as A11 and A18
/// (<see cref="SuspectSignals"/>): SIGTERM, SIGKILL after the grace, by pid AND start, each re-read just before.
/// </summary>
/// <remarks>A BUTTON run is bound to what its modal showed, as A18's (<see cref="Shown"/>). What an AI agent does with an ended
/// stdio server is NOT measured (plan S2, owed): every item says the session may need <c>/mcp</c> to reconnect it.</remarks>
public sealed class McpServerStop : ICleanupAction, IBoundToShownList
{
    private const string Kind = "process";
    private const int InitPid = 1;

    public const string NotTargetUser = "it is not the target user's";
    public const string HasTerminal = "it has a terminal (the shared signal path keeps a process with one)";
    public const string HasChild = "it has a child process (it may be waiting on work it started)";
    public const string NotTheSnapshot = "its /proc entry is not the process the snapshot saw (another start, another account, or gone)";
    public const string UsedCpu = "it used CPU within its window, or its CPU history is missing, broken by a gap or shorter on the monotonic clock";
    public const string Reconnect = "the agent's session may need /mcp to reconnect it";

    public const string ButtonNeedsShownProcesses =
        "a button run of A19 must pass the processes its preview SHOWED (--process <pid:start>): A19 ends only those, judged again (plan E14 S2a)";

    public ActionId Id { get; } = ActionId.Find("A19")!;

    public string Summary => $"SIGTERM, then SIGKILL after {Tuning.Current.Text(ConfigKeys.Processes.TermGraceSeconds)} s, of the target user's MCP servers with no CPU for {Tuning.Current.Text(ConfigKeys.McpWatchdog.IdleMinutes)} min (measured; {Tuning.Current.Text(ConfigKeys.McpWatchdog.OrphanIdleMinutes)} min when their agent died) - by pid and start, never by name";

    public CommandScope Scope => CommandScope.User;

    public IdleRule Idle => IdleRule.Never;

    public IReadOnlyList<HostSide> Sides { get; } = [HostSide.Wsl];

    public IReadOnlyList<CommandTemplate> Commands { get; } = [];

    /// <summary>Every process the preview selected, as <c>pid:start</c> — what a button run passes back.</summary>
    public IReadOnlyList<string> Shown(ActionPreview preview) => [.. preview.Targets.Select(t => SuspectSignals.Shown(t.Key)).Take(ShownList.MaxNames)];

    public Task<ActionPreview> PreviewAsync(ActionContext context, ActionCommands commands, CancellationToken cancellationToken)
    {
        var windows = new IdleWindows(
            TimeSpan.FromMinutes(context.Config.Int(ConfigKeys.McpWatchdog.IdleMinutes)),
            TimeSpan.FromMinutes(context.Config.Int(ConfigKeys.McpWatchdog.OrphanIdleMinutes)));
        var what = string.Create(CultureInfo.InvariantCulture, $"the target user's MCP servers with no CPU for {windows.Idle.TotalMinutes:0} min ({windows.Orphan.TotalMinutes:0} min when their agent died), measured - SIGTERM, then SIGKILL after {SuspectTermination.Grace.TotalSeconds:0} s; {Reconnect}");
        return Task.FromResult(Bound(Preview(context, what, windows, cancellationToken), context));
    }

    /// <summary>The two idle windows in force: an instance's, and one whose agent died.</summary>
    public sealed record IdleWindows(TimeSpan Idle, TimeSpan Orphan);

    /// <summary>A button run narrowed to what its modal showed — or refused when it showed nothing (as A18's).</summary>
    private static ActionPreview Bound(ActionPreview preview, ActionContext context)
    {
        if (context.ShownProcesses.Given)
        {
            var kept = preview.Targets.Where(t => context.ShownProcesses.Names.Contains(SuspectSignals.Shown(t.Key))).ToList();
            var what = string.Create(CultureInfo.InvariantCulture, $"{preview.What}; of the {context.ShownProcesses.Names.Count} process(es) the panel showed, the {kept.Count} still eligible");
            return RowPreviews.Narrowed(preview, kept, what, preview.Facts);
        }

        return context.Trigger == RunTrigger.Manual && preview.Available
            ? preview with { Refusal = preview.Refusal.Length > 0 ? preview.Refusal : ButtonNeedsShownProcesses }
            : preview;
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
        var samples = AgentCpuHistory.Sample(linux, context.Files, snapshot.All);
        // A preview writes no state: the history is the timer runs' (merged with now in memory, never written here).
        var history = AgentCpuHistory.Next(AgentCpuHistory.Read(linux, context.Files), boot, samples, at);
        var judged = Judge(new Judging(user, snapshot.All, McpSettings.From(context.Config).Watched, samples, s => AgentCpuHistory.IdleFor(history, boot, s, at), windows));
        var items = judged.Where(j => j.Eligible).Select(Item).ToList();
        var basis = $"MCP servers read from /proc; idle by the CPU history the timer's full runs record ({AgentCpuHistory.File(linux)}); {judged.Count - items.Count} MCP server(s) of {user.Name} kept: {Kept(judged)}";
        var facts = new Dictionary<string, long>(StringComparer.Ordinal) { [SuspectTermination.HeldMemoryFact] = items.Sum(i => i.Bytes ?? 0) };
        return ActionPreview.Of(what, items.Count, null, basis, facts, string.Empty, items);
    }

    /// <summary>What one judgement reads: the target user, the whole process table, the watched servers, the samples, the idle
    /// clock and the two windows.</summary>
    public sealed record Judging(TargetUser User, IReadOnlyList<ProcessEntry> Processes, IReadOnlyList<McpServerEntry> Watched, IReadOnlyList<PidSample> Samples, Func<PidSample, TimeSpan> IdleFor, IdleWindows Windows);

    /// <summary>Every instance of a watched MCP server owned by the target user, each judged (plan E14 S2a item 2).</summary>
    public static IReadOnlyList<McpStopJudgement> Judge(Judging judging)
    {
        var parents = judging.Processes.Select(p => p.ParentPid).ToHashSet();
        return [.. McpInstances.Find(judging.Processes, judging.Watched).Instances
            .Where(f => string.Equals(f.Process.User, judging.User.Name, StringComparison.Ordinal) && f.Process.User != "root")
            .Where(f => f.Process.Pid > InitPid && f.Process.Pid != System.Environment.ProcessId && f.Process.State != 'Z')
            .Select(f => JudgeOne(judging, f, parents.Contains(f.Process.Pid)))];
    }

    private static McpStopJudgement JudgeOne(Judging judging, McpFound instance, bool hasChild)
    {
        var sample = judging.Samples.FirstOrDefault(s => s.Pid == instance.Process.Pid);
        var idle = sample is null ? TimeSpan.Zero : judging.IdleFor(sample);
        var kept = instance.Process.HasTty ? HasTerminal
            : hasChild ? HasChild
            : !SameProcess(instance.Process, sample, judging.User) ? NotTheSnapshot
            : idle < WindowOf(instance, judging.Windows) ? UsedCpu
            : string.Empty;
        return new McpStopJudgement(instance, sample, idle, kept);
    }

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
        new(Kind, string.Create(CultureInfo.InvariantCulture, $"{judged.Instance.Process.Pid} {judged.Instance.Server.Name}"), judged.Instance.Process.HeldBytes,
            string.Create(CultureInfo.InvariantCulture, $"{judged.Instance.Server.Name}, pid {judged.Instance.Process.Pid}, {OwnerText(judged.Instance.Owner)}, no CPU for {judged.IdleFor.TotalMinutes:0} min; {Reconnect}"))
        {
            Key = SuspectSignals.Key(judged.Sample!),
        };

    private static string OwnerText(McpOwner owner) => owner switch
    {
        McpOwner.Agent agent => string.Create(CultureInfo.InvariantCulture, $"under {agent.Name} (pid {agent.Pid})"),
        _ => "its agent is gone (orphaned)",
    };

    /// <summary>The timer runs it when its <c>auto</c> switch is on (default ON) and any server is a target.</summary>
    public TriggerDecision Trigger(ActionPreview preview, EffectiveConfig config) =>
        new(preview.Count > 0, string.Create(CultureInfo.InvariantCulture, $"{preview.Count} idle MCP server(s); the trigger is any"));

    public async Task<ActionRun> RunAsync(ActionContext context, ActionPreview preview, ActionCommands commands, CancellationToken cancellationToken)
    {
        if (preview.Targets.Count == 0 || context.Paths is not LinuxHostPaths linux)
        {
            return ActionRun.Nothing(commands.Ran, "no MCP server is idle");
        }

        var judged = await SuspectSignals.EndAllAsync(context, linux, preview.Targets, SuspectTermination.Grace, cancellationToken).ConfigureAwait(false);
        var verdicts = judged.Select(j => SuspectSignals.Verdict(j.Item, j.Outcome)).ToList();
        var ended = verdicts.Where(v => v.Ended).Select(v => v.Item).ToList();
        return new ActionRun(ended.Count, null, "A19 frees memory, not disk: each stopped server's item carries what it held", null, null, ended, commands.Ran, string.Join("; ", verdicts.Where(v => v.Failure.Length > 0).Select(v => v.Failure).Take(5)))
        {
            NotRemoved = [.. verdicts.Where(v => !v.Ended).Select(v => v.Item)],
        };
    }
}
