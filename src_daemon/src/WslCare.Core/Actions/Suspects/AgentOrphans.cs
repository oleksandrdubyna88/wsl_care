using System.Globalization;

using WslCare.Core.Actions.UserCaches;
using WslCare.Core.Agents;
using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Hosting;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;

namespace WslCare.Core.Actions.Suspects;

/// <summary>Why an AI-agent process of the target user is kept — or <see cref="Eligible"/>.</summary>
public sealed record OrphanJudgement(ProcessEntry Process, string Agent, TimeSpan IdleFor, string Kept)
{
    public bool Eligible => Kept.Length == 0;
}

/// <summary>
/// A18 (plan §15q E7.S2b, owner decision 2026-10-05): end the TARGET user's ORPHANED AI-agent processes — a button only, never
/// the timer (<see cref="ActionId.ButtonOnly"/>; its trigger never fires either). ALL of: the <c>ai-agents</c> family; the target
/// user's; re-parented (orphaned), no controlling terminal, not a zombie; attributable to exactly ONE catalogue agent whose
/// session layout is CONFIRMED; NO CPU for <c>processes.aiAgentsIdleHours</c> MEASURED by identity (<see cref="AgentCpuHistory"/>:
/// missing history is not idle); and no session file of that agent written within the same window (listing + stat only, a
/// listing cut short = "cannot tell" = kept). Signals as A11 (<see cref="SuspectSignals"/>): SIGTERM, SIGKILL after 10 s, by pid
/// AND start, each re-read just before.
/// </summary>
public sealed class AgentOrphans : ICleanupAction
{
    private const string Kind = "process";

    public const string NotOrphaned = "it has a live parent";
    public const string HasTerminal = "it has a terminal";
    public const string NoSingleAgent = "the catalogue cannot attribute it to exactly one agent";
    public const string Unconfirmed = "its agent's session layout is not confirmed (monitor only), so a live session cannot be ruled out";
    public const string LiveSession = "a session of its agent was written within the window";
    public const string CannotTell = "its agent's sessions could not be listed to the end";
    public const string UsedCpu = "it used CPU within the window (or has no measured history yet)";

    public ActionId Id { get; } = ActionId.Find("A18")!;

    public string Summary => "SIGTERM, then SIGKILL after 10 s, of the target user's orphaned AI-agent processes idle (no CPU, measured) with no live session — a button only";

    public CommandScope Scope => CommandScope.User;

    public IdleRule Idle => IdleRule.Never;

    public IReadOnlyList<HostSide> Sides { get; } = [HostSide.Wsl];

    public IReadOnlyList<CommandTemplate> Commands { get; } = [];

    public Task<ActionPreview> PreviewAsync(ActionContext context, ActionCommands commands, CancellationToken cancellationToken)
    {
        var hours = context.Config.Int(ConfigKeys.Processes.AiAgentsIdleHours);
        var what = string.Create(CultureInfo.InvariantCulture, $"orphaned AI-agent processes of the target user with no CPU for {hours} h (measured) and no session of their agent written in that time - SIGTERM, then SIGKILL after {SuspectTermination.Grace.TotalSeconds:0} s");
        return Task.FromResult(Preview(context, what, TimeSpan.FromHours(hours), cancellationToken));
    }

    private static ActionPreview Preview(ActionContext context, string what, TimeSpan window, CancellationToken cancellationToken)
    {
        if (context.TargetUser is not TargetUserResult.Found { User: var user })
        {
            return ActionPreview.Unavailable(what, $"A18 ends only the target user's processes, and there is no single target user ({context.TargetUser.Refusal})");
        }

        if (context.Paths is not LinuxHostPaths linux || context.Processes(cancellationToken) is not Reading<ProcessSnapshot>.Available { Value: var snapshot })
        {
            return ActionPreview.Unavailable(what, "the process table could not be read");
        }

        var boot = AgentCpuHistory.BootId(linux, context.Files);
        if (boot.Length == 0)
        {
            return ActionPreview.Unavailable(what, "the boot id cannot be read, so no process's idle time can be told");
        }

        var now = context.Clock.GetUtcNow();
        var samples = AgentCpuHistory.Sample(linux, context.Files, snapshot.All);
        // A preview writes no state: the history is the timer runs' (merged with now in memory, never written here).
        var history = AgentCpuHistory.Next(AgentCpuHistory.Read(linux, context.Files), boot, samples, now);
        var judged = Judge(context, snapshot.All, user.Name, samples, s => AgentCpuHistory.IdleFor(history, boot, s, now), window, now);
        var eligible = judged.Where(j => j.Eligible).ToList();
        var items = eligible.Select(j => Item(j, samples.Single(s => s.Pid == j.Process.Pid))).ToList();
        var basis = $"processes read from /proc; idle by the CPU history the timer's full runs record ({AgentCpuHistory.File(linux)}); {judged.Count - eligible.Count} AI-agent process(es) of {user.Name} kept: {Kept(judged)}";
        var facts = new Dictionary<string, long>(StringComparer.Ordinal) { [SuspectTermination.HeldMemoryFact] = items.Sum(i => i.Bytes ?? 0) };
        return ActionPreview.Of(what, items.Count, null, basis, facts, string.Empty, items);
    }

    /// <summary>Every AI-agent process of <paramref name="user"/>, each judged (plan §15q E7.S2b item 2).</summary>
    public static IReadOnlyList<OrphanJudgement> Judge(ActionContext context, IEnumerable<ProcessEntry> processes, string user, IReadOnlyList<SuspectSample> samples, Func<SuspectSample, TimeSpan> idleFor, TimeSpan window, DateTimeOffset now) =>
        [.. processes
            .Where(p => p.Family == ProcessFamilies.AiAgents && string.Equals(p.User, user, StringComparison.Ordinal) && p.User != "root" && p.Pid > 1 && p.Pid != Environment.ProcessId && p.State != 'Z')
            .Select(p => JudgeOne(context, p, AgentOf(p), samples.FirstOrDefault(s => s.Pid == p.Pid), idleFor, window, now))];

    private static OrphanJudgement JudgeOne(ActionContext context, ProcessEntry process, AgentEntry? agent, SuspectSample? sample, Func<SuspectSample, TimeSpan> idleFor, TimeSpan window, DateTimeOffset now)
    {
        var idle = sample is null ? TimeSpan.Zero : idleFor(sample);
        var kept = Shape(process, agent) is { Length: > 0 } shape ? shape
            : sample is null || sample.Tty != 0 || idle < window ? UsedCpu
            : Sessions(context, agent!, window, now);
        return new OrphanJudgement(process, agent?.Name ?? string.Empty, idle, kept);
    }

    private static string Shape(ProcessEntry process, AgentEntry? agent) =>
        !process.Orphaned ? NotOrphaned
        : process.HasTty ? HasTerminal
        : agent is null ? NoSingleAgent
        : agent.Sessions is null ? Unconfirmed
        : string.Empty;

    /// <summary>Empty when no session of <paramref name="agent"/> under the target home was written within <paramref name="window"/>;
    /// otherwise why the process is kept. Listing and stat only (plan §15q H3).</summary>
    private static string Sessions(ActionContext context, AgentEntry agent, TimeSpan window, DateTimeOffset now)
    {
        var home = CacheFolders.Home(context);
        var layout = agent.Sessions!;
        if (home.Length == 0 || layout.LinuxUnder.Length == 0)
        {
            return Unconfirmed;
        }

        var started = context.Clock.GetTimestamp();
        var scan = SessionGlob.Find(context.Files, AgentCatalogue.LinuxFolder(layout.LinuxUnder, home), layout.Glob, AgentWalk.NeverEnterOf(agent), () => context.Clock.GetElapsedTime(started) >= AgentWalk.MeasureNowBudget);
        return !scan.Complete ? CannotTell
            : scan.Sessions.Any(s => s.LastWrite >= now - window) ? LiveSession
            : string.Empty;
    }

    /// <summary>The ONE catalogue agent whose binary the process runs (its program, or the script node runs); <c>null</c> for none
    /// or more than one.</summary>
    public static AgentEntry? AgentOf(ProcessEntry process)
    {
        var names = process.CommandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2)
            .Select(word => Path.GetFileName(word.Replace('\\', '/')))
            .Select(name => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name)
            .ToList();
        var agents = AgentCatalogue.Agents.Where(a => a.Binaries.Any(b => names.Contains(b, StringComparer.Ordinal))).ToList();
        return agents.Count == 1 ? agents[0] : null;
    }

    private static string Kept(IReadOnlyList<OrphanJudgement> judged) =>
        string.Join("; ", judged.Where(j => !j.Eligible).GroupBy(j => j.Kept).Select(g => string.Create(CultureInfo.InvariantCulture, $"{g.Count()} because {g.Key}")));

    private static ActionItem Item(OrphanJudgement judged, SuspectSample sample) =>
        new(Kind, string.Create(CultureInfo.InvariantCulture, $"{judged.Process.Pid} {judged.Process.Name}"), judged.Process.HeldBytes,
            string.Create(CultureInfo.InvariantCulture, $"{judged.Agent}, pid {judged.Process.Pid}, no CPU for {judged.IdleFor.TotalHours:0.0} h, in {judged.Process.Cwd.ValueOr("an unknown folder")}"))
        {
            Key = SuspectSignals.Key(sample),
        };

    /// <summary>A button only (plan §15q E7.S2b): the timer's trigger never fires — and the timer never selects it at all.</summary>
    public TriggerDecision Trigger(ActionPreview preview, EffectiveConfig config) =>
        new(false, "A18 is a button only (plan §15q E7.S2b): the timer never ends an AI agent's process");

    public async Task<ActionRun> RunAsync(ActionContext context, ActionPreview preview, ActionCommands commands, CancellationToken cancellationToken)
    {
        if (preview.Targets.Count == 0 || context.Paths is not LinuxHostPaths linux)
        {
            return ActionRun.Nothing(commands.Ran, "no orphaned AI-agent process is idle");
        }

        var judged = await SuspectSignals.EndAllAsync(context, linux, preview.Targets, SuspectTermination.Grace, cancellationToken).ConfigureAwait(false);
        var verdicts = judged.Select(j => SuspectSignals.Verdict(j.Item, j.Outcome)).ToList();
        var ended = verdicts.Where(v => v.Ended).Select(v => v.Item).ToList();
        return new ActionRun(ended.Count, null, "A18 frees memory, not disk: each ended process's item carries what it held", null, null, ended, commands.Ran, string.Join("; ", verdicts.Where(v => v.Failure.Length > 0).Select(v => v.Failure).Take(5)))
        {
            NotRemoved = [.. verdicts.Where(v => !v.Ended).Select(v => v.Item)],
        };
    }
}
