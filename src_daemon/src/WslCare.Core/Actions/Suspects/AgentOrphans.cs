using System.Globalization;

using WslCare.Core.Actions.UserCaches;
using WslCare.Core.Agents;
using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Records;

namespace WslCare.Core.Actions.Suspects;

/// <summary>Why an AI-agent process of the target user is kept — or <see cref="Eligible"/>.</summary>
public sealed record OrphanJudgement(ProcessEntry Process, string Agent, TimeSpan IdleFor, string Kept)
{
    public bool Eligible => Kept.Length == 0;
}

/// <summary>
/// A18 (plan §15q E7.S2b, owner decision 2026-10-05): end the TARGET user's ORPHANED AI-agent processes — a button only, never
/// the timer (<see cref="ActionId.ButtonOnly"/>; its trigger never fires either). ALL of: the <c>ai-agents</c> family; the target
/// user's; re-parented to init (pid 1 — never a service the user's systemd manager started), no controlling terminal, not a
/// zombie, no child process; attributable to exactly ONE catalogue agent whose session layout is CONFIRMED and whose program
/// resolves into that agent's own install; NO CPU for <c>processes.aiAgentsIdleHours</c> MEASURED by identity on both clocks over
/// a dense chain (<see cref="AgentCpuHistory"/>: missing history is not idle); its environment moves no agent home; and at least
/// one session of that agent found and none written within the same window (listing + stat only; none found, or a listing cut
/// short = "cannot tell" = kept). Signals as A11 (<see cref="SuspectSignals"/>): SIGTERM, SIGKILL after the grace, by pid AND
/// start, each re-read just before.
/// </summary>
/// <remarks>A BUTTON run is bound to what its modal showed (E7.S2b review A-H1, like A4): the preview answers its processes as
/// <c>pid:start</c> keys (<see cref="Shown"/>), a manual run without them is refused, and a run with them ends only the processes
/// that are BOTH still eligible now and among those keys.</remarks>
public sealed class AgentOrphans : ICleanupAction, IBoundToShownList
{
    private const string Kind = "process";
    private const int InitPid = 1;

    public const string NotOrphaned = "its parent is not init (a live parent, or the user's systemd manager that started it)";
    public const string HasTerminal = "it has a terminal";
    public const string HasChild = "it has a child process (a wrapper whose child may be working)";
    public const string NoSingleAgent = "the catalogue cannot attribute it to exactly one agent";
    public const string NotInstalled = "its program does not resolve into the agent's own install (a native versions folder or the agent's npm package)";
    public const string Unconfirmed = "its agent's session layout is not confirmed (monitor only), so a live session cannot be ruled out";
    public const string LiveSession = "a session of its agent was written within the window";
    public const string CannotTell = "its agent's sessions could not be listed to the end";
    public const string NoSession = "no session of its agent was found, so where it writes its sessions cannot be told";
    public const string UnreadableEnvironment = "its environment could not be read, so where it keeps its sessions cannot be told";
    public const string OtherHome = "it runs with its own agent home";
    public const string UsedCpu = "it used CPU within the window, or its CPU history is missing, broken by a gap or shorter on the monotonic clock";

    public const string ButtonNeedsShownProcesses =
        "a button run of A18 must pass the processes its preview SHOWED (--process <pid:start>): A18 ends only those, judged again (plan §15q E7.S2b review A-H1)";

    /// <summary>The variables that move where any agent keeps its files.</summary>
    private static readonly string[] HomeVariables = ["HOME", "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_STATE_HOME", "XDG_CACHE_HOME"];

    public ActionId Id { get; } = ActionId.Find("A18")!;

    public string Summary => $"SIGTERM, then SIGKILL after {Tuning.Current.Text(ConfigKeys.Processes.TermGraceSeconds)} s, of the target user's orphaned AI-agent processes idle (no CPU, measured) with no live session — a button only";

    public CommandScope Scope => CommandScope.User;

    public IdleRule Idle => IdleRule.Never;

    public IReadOnlyList<HostSide> Sides { get; } = [HostSide.Wsl];

    public IReadOnlyList<CommandTemplate> Commands { get; } = [];

    /// <summary>Every process the preview selected, as <c>pid:start</c> — what a button run passes back (review A-H1).</summary>
    public IReadOnlyList<string> Shown(ActionPreview preview) => [.. preview.Targets.Select(t => SuspectSignals.Shown(t.Key)).Take(ShownList.MaxNames)];

    public Task<ActionPreview> PreviewAsync(ActionContext context, ActionCommands commands, CancellationToken cancellationToken)
    {
        var hours = context.Config.Int(ConfigKeys.Processes.AiAgentsIdleHours);
        var what = string.Create(CultureInfo.InvariantCulture, $"orphaned AI-agent processes of the target user with no CPU for {hours} h (measured) and no session of their agent written in that time - SIGTERM, then SIGKILL after {SuspectTermination.Grace.TotalSeconds:0} s");
        return Task.FromResult(Bound(Preview(context, what, TimeSpan.FromHours(hours), cancellationToken), context));
    }

    /// <summary>A button run narrowed to what its modal showed — or refused when it showed nothing (review A-H1).</summary>
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

        var boot = BootIdentity.Read(linux, context.Files);
        if (boot.Length == 0)
        {
            return ActionPreview.Unavailable(what, "the boot id cannot be read, so no process's idle time can be told");
        }

        var at = SampleTime.Of(context.Clock);
        var samples = AgentCpuHistory.Sample(linux, context.Files, snapshot.All);
        // A preview writes no state: the history is the timer runs' (merged with now in memory, never written here).
        var history = AgentCpuHistory.Next(AgentCpuHistory.Read(linux, context.Files), boot, samples, at);
        var judged = Judge(new Judging(context, linux, user, snapshot.All, samples, s => AgentCpuHistory.IdleFor(history, boot, s, at), window, at.Wall));
        var eligible = judged.Where(j => j.Eligible).ToList();
        var items = eligible.Select(j => Item(j, samples.Single(s => s.Pid == j.Process.Pid))).ToList();
        var basis = $"processes read from /proc; idle by the CPU history the timer's full runs record ({AgentCpuHistory.File(linux)}); {judged.Count - eligible.Count} AI-agent process(es) of {user.Name} kept: {Kept(judged)}";
        var facts = new Dictionary<string, long>(StringComparer.Ordinal) { [SuspectTermination.HeldMemoryFact] = items.Sum(i => i.Bytes ?? 0) };
        return ActionPreview.Of(what, items.Count, null, basis, facts, string.Empty, items);
    }

    /// <summary>What one judgement reads: the context, the target user, the whole process table, the samples, the idle clock.</summary>
    public sealed record Judging(ActionContext Context, LinuxHostPaths Linux, TargetUser User, IReadOnlyList<ProcessEntry> Processes, IReadOnlyList<PidSample> Samples, Func<PidSample, TimeSpan> IdleFor, TimeSpan Window, DateTimeOffset Now);

    /// <summary>Every AI-agent process of the target user, each judged (plan §15q E7.S2b item 2).</summary>
    public static IReadOnlyList<OrphanJudgement> Judge(Judging judging)
    {
        var parents = judging.Processes.Select(p => p.ParentPid).ToHashSet();
        return [.. judging.Processes
            .Where(p => p.Family == ProcessFamilies.AiAgents && string.Equals(p.User, judging.User.Name, StringComparison.Ordinal) && p.User != "root" && p.Pid > InitPid && p.Pid != System.Environment.ProcessId && p.State != 'Z')
            .Select(p => JudgeOne(judging, p, AgentOf(p), parents.Contains(p.Pid)))];
    }

    private static OrphanJudgement JudgeOne(Judging judging, ProcessEntry process, AgentEntry? agent, bool hasChild)
    {
        var sample = judging.Samples.FirstOrDefault(s => s.Pid == process.Pid);
        var idle = sample is null ? TimeSpan.Zero : judging.IdleFor(sample);
        var kept = Shape(process, agent, hasChild) is { Length: > 0 } shape ? shape
            : !IsInstalled(judging, process, agent!) ? NotInstalled
            : sample is null || sample.Tty != 0 || idle < judging.Window ? UsedCpu
            : Environment(judging, process, agent!) is { Length: > 0 } moved ? moved
            : Sessions(judging.Context, agent!, judging.Window, judging.Now);
        return new OrphanJudgement(process, agent?.Name ?? string.Empty, idle, kept);
    }

    private static string Shape(ProcessEntry process, AgentEntry? agent, bool hasChild) =>
        !process.Orphaned || process.ParentPid != InitPid ? NotOrphaned
        : process.HasTty ? HasTerminal
        : hasChild ? HasChild
        : agent is null ? NoSingleAgent
        : agent.Sessions is null ? Unconfirmed
        : string.Empty;

    /// <summary>Review A-M3: the process's program (<c>/proc/&lt;pid&gt;/exe</c>), or the script node runs, leads along its links into
    /// <paramref name="agent"/>'s own install; a name alone is not enough.</summary>
    private static bool IsInstalled(Judging judging, ProcessEntry process, AgentEntry agent)
    {
        var files = judging.Context.Files;
        if (files.ReadLink($"{judging.Linux.ProcRoot}/{process.Pid.ToString(CultureInfo.InvariantCulture)}/exe") is not LinkReadResult.Target exe)
        {
            return false;
        }

        return AgentDiscovery.IsInstallOf(agent, AgentDiscovery.Chain(judging.Linux.DistroPath(exe.Path), files))
            || (IsNode(exe.Path) && Script(process) is { Length: > 0 } script && AgentDiscovery.IsInstallOf(agent, AgentDiscovery.Chain(judging.Linux.DistroPath(script), files)));
    }

    private static bool IsNode(string program) => Path.GetFileName(program) is "node" or "nodejs";

    /// <summary>The script a node process runs: its first argument, when absolute.</summary>
    private static string Script(ProcessEntry process) =>
        process.CommandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault() is { } word && word.StartsWith('/') ? word : string.Empty;

    /// <summary>Review A-M5: a process whose environment moves where an agent keeps its files — <c>HOME</c> or an XDG folder off
    /// their default, or an agent's own home variable (<c>CLAUDE_CONFIG_DIR</c>, <c>CODEX_HOME</c>, <c>GEMINI_…</c>: any variable
    /// named after a catalogue binary that holds a path outside the agent's own folders) — writes its sessions where A18 does not
    /// look: kept. Empty when nothing is moved.</summary>
    private static string Environment(Judging judging, ProcessEntry process, AgentEntry agent)
    {
        var read = ProcText.Read(judging.Context.Files, $"{judging.Linux.ProcRoot}/{process.Pid.ToString(CultureInfo.InvariantCulture)}/environ");
        if (read is not Reading<string>.Available { Value: var text })
        {
            return UnreadableEnvironment;
        }

        var variables = text.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(v => v.Split('=', 2)).Where(v => v.Length == 2).ToList();
        var home = judging.User.Home.TrimEnd('/');
        var moved = variables.FirstOrDefault(v => Moves(v[0], v[1], home, agent));
        return moved is null ? string.Empty : $"{OtherHome} ({moved[0]}), which is not checked";
    }

    private static bool Moves(string name, string value, string home, AgentEntry agent) =>
        name switch
        {
            "HOME" => value.TrimEnd('/') != home,
            "XDG_CONFIG_HOME" => value.TrimEnd('/') != home + "/.config",
            "XDG_DATA_HOME" => value.TrimEnd('/') != home + "/.local/share",
            "XDG_STATE_HOME" => value.TrimEnd('/') != home + "/.local/state",
            "XDG_CACHE_HOME" => value.TrimEnd('/') != home + "/.cache",
            _ => !HomeVariables.Contains(name) && IsAgentVariable(name) && value.StartsWith('/') && !IsAgentFolder(value, home, agent),
        };

    private static bool IsAgentVariable(string name) =>
        AgentCatalogue.Agents.SelectMany(a => a.Binaries).Any(b => name.StartsWith(b.ToUpperInvariant().Replace('-', '_') + "_", StringComparison.Ordinal));

    private static bool IsAgentFolder(string value, string home, AgentEntry agent) =>
        agent.Linux.Select(f => AgentCatalogue.LinuxFolder(f, home).TrimEnd('/')).Contains(value.TrimEnd('/'), StringComparer.Ordinal);

    /// <summary>Empty when sessions of <paramref name="agent"/> under the target home were found and none was written within
    /// <paramref name="window"/>; otherwise why the process is kept. Listing and stat only (plan §15q H3). Review A-M5: NO session
    /// found is "cannot tell", never "no live session".</summary>
    private static string Sessions(ActionContext context, AgentEntry agent, TimeSpan window, DateTimeOffset now)
    {
        var home = CacheFolders.Home(context);
        var layout = agent.Sessions!;
        if (home.Length == 0 || layout.LinuxUnder.Length == 0)
        {
            return Unconfirmed;
        }

        var under = AgentCatalogue.LinuxFolder(layout.LinuxUnder, home);
        if (AgentWalk.PlaceProblem(context.Files, home, under).Length > 0)
        {
            return CannotTell;
        }

        var started = context.Clock.GetTimestamp();
        var listing = new SessionListing(context.Files, AgentWalk.NeverEnterOf(agent), () => context.Clock.GetElapsedTime(started) >= AgentWalk.MeasureNowBudget, CancellationToken.None) { Device = context.Files.DeviceOf(under) };
        var scan = SessionGlob.Find(listing, under, layout.Glob);
        return !scan.Complete ? CannotTell
            : scan.Sessions.Count == 0 ? NoSession
            : scan.Sessions.Any(s => s.LastWrite >= now - window) ? LiveSession
            : string.Empty;
    }

    /// <summary>The ONE catalogue agent whose binary the process runs (<see cref="AgentProcesses.AgentOf"/>, the one attribution
    /// A18 and the MCP servers' owner walk share); <c>null</c> for none or more than one.</summary>
    public static AgentEntry? AgentOf(ProcessEntry process) => AgentProcesses.AgentOf(process);

    private static string Kept(IReadOnlyList<OrphanJudgement> judged) =>
        string.Join("; ", judged.Where(j => !j.Eligible).GroupBy(j => j.Kept).Select(g => string.Create(CultureInfo.InvariantCulture, $"{g.Count()} because {g.Key}")));

    private static ActionItem Item(OrphanJudgement judged, PidSample sample) =>
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
