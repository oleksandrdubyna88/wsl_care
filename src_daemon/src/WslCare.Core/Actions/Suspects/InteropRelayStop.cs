using System.Globalization;

using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Config;
using WslCare.Core.Hosting;
using WslCare.Core.Mcp;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Records;

namespace WslCare.Core.Actions.Suspects;

/// <summary>Why one interop relay of the target user is kept — or <see cref="Eligible"/>.</summary>
public sealed record RelayJudgement(ProcessEntry Process, RelayFacts Facts, PidSample? Sample, TimeSpan IdleFor, string Kept)
{
    public bool Eligible => Kept.Length == 0;
}

/// <summary>
/// A21 (plan E14 S7b.2, from the Q-S7b-3 measurement of 2026-10-10): end the target user's interop relays of catalogued Windows MCP
/// servers whose CLIENT IS GONE — the leak S7b exists for: a Windows <c>creds-mcp.exe</c> under VS Code's <c>wsl.exe</c> lives as
/// long as its distro relay, and SIGTERM to the relay ends both (measured: 26 ms and about 0.2 s). The client is shown gone by
/// ALL of: the relay is re-parented to a REAPER (root's pid 1, a root <c>Relay(n)</c> or <c>SessionLeader</c>); it was not BORN
/// there (its pid is not its <c>Relay(n)</c>'s <c>n</c> — <c>wsl.exe</c>'s top-level command, whose Windows caller may live); and
/// its stdio is PIPES nobody else holds (a socket, a file, a terminal or an unreadable link keeps it; an fd table the scan cannot
/// read keeps every relay). Its CPU unmoved for <c>mcpWatchdog.orphanIdleMinutes</c> is a freshness floor, not evidence. The
/// target user's, never root's, no terminal, the process the snapshot saw. SIGTERM ONLY, by pid AND start, each re-judged just
/// before — never SIGKILL. A button run is bound to what its modal showed; automatic under <c>auto.A21</c> (on by default) and
/// the daemon's dry-run rules.
/// </summary>
/// <remarks>Not measured yet (plan S7b.2): what a relay's stdio is — node gives a child socketpairs, and a socket's peer cannot
/// be told from <c>/proc</c>, so such a relay is KEPT with that reason; the dry-run preview shows how many.</remarks>
public sealed class InteropRelayStop : ICleanupAction, IBoundToShownList
{
    private const string Kind = "relay";

    public const string HasTerminal = "it has a terminal (the shared signal path keeps a process with one)";
    public const string CallerMayLive = "its parent is no reaper (root's pid 1, a root Relay(n) or SessionLeader): its caller may still talk to it";
    public const string BornThere = "it is its Relay(n)'s own command (its pid is n): wsl.exe started it, and its Windows-side caller may be alive";
    public const string StdioSocket = "its stdio is a socket: a socket's peer cannot be told from /proc, so its client may be alive";
    public const string StdioNotPiped = "its fd 0 or fd 1 is not a pipe (a file, a device, a terminal, closed or unreadable): who can still talk to it cannot be told";
    public const string StdioHeld = "another process holds its stdio pipe: a client may be alive";
    public const string NotTheSnapshot = "its /proc entry is not the process the snapshot saw (another start, another account, or gone)";
    public const string UsedCpu = "it used CPU within mcpWatchdog.orphanIdleMinutes, or its CPU history is missing, broken by a gap or shorter on the monotonic clock";
    public const string Reconnect = "the agent's session may need /mcp to reconnect it";

    public const string ButtonNeedsShownProcesses =
        "a button run of A21 must pass the relays its preview SHOWED (--process <pid:start>): A21 ends only those, judged again (plan E14 S7b.2)";

    public ActionId Id { get; } = ActionId.Find("A21")!;

    public string Summary => $"SIGTERM only (never SIGKILL) of the target user's interop relays of catalogued Windows MCP servers whose client is gone - re-parented, not born there, no other holder of their stdio pipes - with no CPU for {Tuning.Current.Text(ConfigKeys.McpWatchdog.OrphanIdleMinutes)} min; by pid and start, never by name";

    public CommandScope Scope => CommandScope.User;

    public IdleRule Idle => IdleRule.Never;

    public IReadOnlyList<HostSide> Sides { get; } = [HostSide.Wsl];

    public IReadOnlyList<CommandTemplate> Commands { get; } = [];

    /// <summary>Every relay the preview selected, as <c>pid:start</c> — what a button run passes back.</summary>
    public IReadOnlyList<string> Shown(ActionPreview preview) => [.. preview.Targets.Select(t => SuspectSignals.Shown(t.Key)).Take(ShownList.MaxNames)];

    public Task<ActionPreview> PreviewAsync(ActionContext context, ActionCommands commands, CancellationToken cancellationToken) =>
        Task.FromResult(ShownProcessBinding.Bound(Judged(context, cancellationToken).Preview, context, ButtonNeedsShownProcesses));

    /// <summary>What the judgement produced: the preview and every judged relay (the run's re-check reads the reasons).</summary>
    private sealed record Judgement(ActionPreview Preview, IReadOnlyList<RelayJudgement> Relays);

    private static string What(EffectiveConfig config) => string.Create(CultureInfo.InvariantCulture,
        $"the target user's interop relays of catalogued Windows MCP servers ({string.Join(", ", InteropRelays.Servers(config).Select(s => s.Name))}) whose client is gone - re-parented, not born there, no other holder of their stdio pipes - with no CPU for {config.Int(ConfigKeys.McpWatchdog.OrphanIdleMinutes)} min: SIGTERM only, never SIGKILL; ending a relay ends its Windows process; {Reconnect}");

    private static Judgement Judged(ActionContext context, CancellationToken cancellationToken)
    {
        var what = What(context.Config);
        if (context.TargetUser is not TargetUserResult.Found { User: var user })
        {
            return new(ActionPreview.Unavailable(what, $"A21 stops only the target user's relays, and there is no single target user ({context.TargetUser.Refusal})"), []);
        }

        if (context.Paths is not LinuxHostPaths linux || context.Processes(cancellationToken) is not Reading<ProcessSnapshot>.Available { Value: var snapshot })
        {
            return new(ActionPreview.Unavailable(what, "the process table could not be read"), []);
        }

        var boot = BootIdentity.Read(linux, context.Files);
        if (boot.Length == 0)
        {
            return new(ActionPreview.Unavailable(what, "the boot id cannot be read, so no relay's idle time can be told"), []);
        }

        var servers = InteropRelays.Servers(context.Config);
        var relays = snapshot.All
            .Where(p => string.Equals(p.User, user.Name, StringComparison.Ordinal) && p.User != "root" && p.Pid > 1 && p.Pid != Environment.ProcessId && p.State != 'Z')
            .Where(p => InteropRelays.ServerOf(p, servers) is not null)
            .Select(p => (Process: p, Facts: InteropRelays.Read(context.Files, linux, p, servers)))
            .Where(r => r.Facts is not null)
            .Select(r => (r.Process, Facts: r.Facts!))
            .ToList();
        var at = SampleTime.Of(context.Clock);
        var samples = AgentCpuHistory.Sample(linux, context.Files, relays.Select(r => r.Process), [], servers);
        // A preview writes no state: the history is the timer's and the watch's (merged with now in memory, never written here).
        var history = AgentCpuHistory.Next(AgentCpuHistory.Read(linux, context.Files), boot, samples, at);
        var scan = StdioHolders.Scan(context.Files, linux, Pipes(relays.Select(r => r.Facts)), context.Clock, TimeSpan.FromMilliseconds(context.Config.Int(ConfigKeys.Processes.FdScanMilliseconds)), cancellationToken);
        var judged = Judge(new Judging(user, relays, samples, s => AgentCpuHistory.IdleFor(history, boot, s, at), TimeSpan.FromMinutes(context.Config.Int(ConfigKeys.McpWatchdog.OrphanIdleMinutes)), scan));
        var items = judged.Where(j => j.Eligible).Select(Item).ToList();
        var basis = $"interop relays read from /proc (exe /init, the raw argv, the parent, fd 0-2, every process's fd table{ScanNote(scan)}); idle by the CPU history the timer's and the watch's runs record ({AgentCpuHistory.File(linux)}); {judged.Count - items.Count} relay(s) of {user.Name} kept: {Kept(judged)}";
        var facts = new Dictionary<string, long>(StringComparer.Ordinal) { [SuspectTermination.HeldMemoryFact] = items.Sum(i => i.Bytes ?? 0) };
        return new(ActionPreview.Of(what, items.Count, null, basis, facts, string.Empty, items), judged);
    }

    private static string ScanNote(HolderScan scan) => scan is HolderScan.Inconclusive inconclusive ? $" - INCONCLUSIVE: {inconclusive.Reason}" : string.Empty;

    /// <summary>Every pipe on fd 0-2 of <paramref name="relays"/>: what the holder scan looks for.</summary>
    private static HashSet<string> Pipes(IEnumerable<RelayFacts> relays) =>
        [.. relays.SelectMany(r => r.Stdio).Where(s => s.IsPipe).Select(s => s.Target)];

    /// <summary>What one judgement reads: the target user, the relays with their facts, the samples, the idle clock, the window and
    /// the holder scan.</summary>
    public sealed record Judging(TargetUser User, IReadOnlyList<(ProcessEntry Process, RelayFacts Facts)> Relays, IReadOnlyList<PidSample> Samples, Func<PidSample, TimeSpan> IdleFor, TimeSpan Window, HolderScan Scan);

    /// <summary>Every relay judged (plan E14 S7b.2 items 2 and 3), in pid order.</summary>
    public static IReadOnlyList<RelayJudgement> Judge(Judging judging)
    {
        var samples = judging.Samples.ToDictionary(s => s.Pid);
        return [.. judging.Relays.OrderBy(r => r.Process.Pid).Select(r => JudgeOne(judging, r.Process, r.Facts, samples.GetValueOrDefault(r.Process.Pid)))];
    }

    private static RelayJudgement JudgeOne(Judging judging, ProcessEntry process, RelayFacts facts, PidSample? sample)
    {
        var idle = sample is null ? TimeSpan.Zero : judging.IdleFor(sample);
        var kept = process.HasTty ? HasTerminal
            : !InteropRelays.IsReaper(facts.Parent) ? CallerMayLive
            : InteropRelays.BornThere(facts) ? BornThere
            : StdioProblem(facts, judging.Scan) is { Length: > 0 } stdio ? stdio
            : !SameProcess(process, sample, judging.User) ? NotTheSnapshot
            : idle < judging.Window ? UsedCpu
            : string.Empty;
        return new RelayJudgement(process, facts, sample, idle, kept);
    }

    /// <summary>Why the relay's stdio does not show its client gone; empty when fd 0 and 1 are pipes, fd 2 is a pipe or no socket,
    /// and no other process holds any of those pipes.</summary>
    private static string StdioProblem(RelayFacts facts, HolderScan scan)
    {
        var stdio = facts.Stdio;
        var main = stdio.Where(s => s.Fd <= 1).ToList();
        return stdio.Any(s => s.IsSocket) ? StdioSocket
            : main.Count < 2 || main.Any(s => !s.IsPipe) ? StdioNotPiped
            : scan is HolderScan.Inconclusive inconclusive ? $"the holder scan is inconclusive: {inconclusive.Reason}"
            : Holders(facts, (HolderScan.Done)scan) is { Count: > 0 } others ? string.Create(CultureInfo.InvariantCulture, $"{StdioHeld} (pid {string.Join(", ", others)})")
            : string.Empty;
    }

    private static List<int> Holders(RelayFacts facts, HolderScan.Done scan) =>
        [.. facts.Stdio.Where(s => s.IsPipe).SelectMany(s => scan.Of(s.Target)).Where(pid => pid != facts.Pid).Distinct().Order()];

    /// <summary>The /proc entry read now is the process the snapshot saw — the same start ticks — and runs as the target user with no
    /// terminal.</summary>
    private static bool SameProcess(ProcessEntry process, PidSample? sample, TargetUser user) =>
        sample is not null
        && process.StartTicks is Reading<long>.Available { Value: var start } && start == sample.StartTicks
        && sample.Uid == user.Uid && sample.Tty == 0;

    private static string Kept(IReadOnlyList<RelayJudgement> judged) =>
        string.Join("; ", judged.Where(j => !j.Eligible).GroupBy(j => j.Kept).Select(g => string.Create(CultureInfo.InvariantCulture, $"{g.Count()} because {g.Key}")));

    /// <summary>The item names the program's BASE name only: the Windows path carries the Windows user name (review finding 14).</summary>
    private static ActionItem Item(RelayJudgement judged) =>
        new(Kind, string.Create(CultureInfo.InvariantCulture, $"{judged.Process.Pid} {judged.Facts.Program}"), judged.Process.HeldBytes,
            string.Create(CultureInfo.InvariantCulture, $"the {judged.Facts.Program}.exe relay, pid {judged.Process.Pid}, under {judged.Facts.Parent.Comm} (pid {judged.Facts.Parent.Pid}), no CPU for {judged.IdleFor.TotalMinutes:0} min; its client is gone: re-parented, not born there, no other holder of its stdio pipes; SIGTERM ends it and its Windows process {judged.Facts.Program}.exe; {Reconnect}"))
        {
            Key = SuspectSignals.Key(judged.Sample!),
        };

    /// <summary>The timer runs it when its <c>auto</c> switch is on (default ON) and any relay is a target.</summary>
    public TriggerDecision Trigger(ActionPreview preview, EffectiveConfig config) =>
        new(preview.Count > 0, string.Create(CultureInfo.InvariantCulture, $"{preview.Count} client-gone interop relay(s); the trigger is any"));

    public async Task<ActionRun> RunAsync(ActionContext context, ActionPreview preview, ActionCommands commands, CancellationToken cancellationToken)
    {
        if (preview.Targets.Count == 0 || context.Paths is not LinuxHostPaths linux)
        {
            return ActionRun.Nothing(commands.Ran, "no interop relay's client is shown gone");
        }

        // Plan E14 S7b.2 item 4: every condition judged AGAIN on a fresh table and fresh /proc reads, just before the signal — a
        // relay whose client came back, that used CPU, or whose pid is another process now is not signalled.
        var fresh = Judged(context, cancellationToken);
        var stillEligible = fresh.Preview.Targets.Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
        var still = preview.Targets.Where(t => stillEligible.Contains(t.Key)).ToList();
        var keptNow = preview.Targets.Except(still).Select(t => t with { Note = $"not signalled: the re-check just before the signal kept it ({Reason(fresh, t)})" }).ToList();
        var judged = await SuspectSignals.TermOnlyAsync(context, linux, still, SuspectTermination.Grace, cancellationToken).ConfigureAwait(false);
        var verdicts = judged.Select(j => Verdict(j.Item, j.Outcome)).ToList();
        var ended = verdicts.Where(v => v.Ended).Select(v => v.Item).ToList();
        return new ActionRun(ended.Count, null, "A21 frees memory, not disk: each ended relay's item carries what it held; its Windows process ends with it", null, null, ended, commands.Ran, string.Join("; ", verdicts.Where(v => v.Failure.Length > 0).Select(v => v.Failure).Take(5)))
        {
            NotRemoved = [.. verdicts.Where(v => !v.Ended).Select(v => v.Item), .. keptNow],
        };
    }

    /// <summary>Why the fresh judgement no longer selects <paramref name="target"/>.</summary>
    private static string Reason(Judgement fresh, ActionItem target)
    {
        var pid = int.Parse(target.Key.Split(':')[0], CultureInfo.InvariantCulture);
        return fresh.Relays.FirstOrDefault(r => r.Process.Pid == pid) is { Eligible: false } kept ? kept.Kept
            : fresh.Preview.Available ? "gone, or another process or CPU reading than the preview's" : fresh.Preview.Refusal;
    }

    /// <summary>The shared verdict — and a survivor of SIGTERM names the Windows process that stays alive with it (plan E14 S7b.2 item 4).</summary>
    private static (bool Ended, ActionItem Item, string Failure) Verdict(ActionItem item, SignalOutcome outcome)
    {
        var verdict = SuspectSignals.Verdict(item, outcome);
        if (outcome is not SignalOutcome.StillRunning)
        {
            return verdict;
        }

        var program = item.Name.Split(' ', 2) is [_, var name] ? name : "its program";
        var note = $"{verdict.Item.Note}; no SIGKILL is sent to a relay; its Windows process {program}.exe is still running - S7b.3 (A22)";
        return (false, verdict.Item with { Note = note }, note);
    }
}
