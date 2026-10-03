using System.Globalization;

using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Config;
using WslCare.Core.Hosting;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;

namespace WslCare.Core.Actions.Suspects;

/// <summary>One process A11 would end, as read twice across the CPU window: who it is (pid AND start) and its CPU ticks
/// at the second read — what the run compares with again just before the signal.</summary>
public sealed record SuspectSample(int Pid, long StartTicks, long CpuTicks, int Tty, int Uid);

/// <summary>
/// A11 (plan §5, §4.2): end the SUSPECT processes — reparented to init or to a user's <c>systemd --user</c>, in one of the
/// families of <c>processes.families</c>, older than <c>processes.idleOlderThanHours</c> — with <c>SIGTERM</c>, then
/// <c>SIGKILL</c> after <see cref="Grace"/>. OFF by default. Never a process with a terminal, never one that used CPU,
/// never root's, never this process; always by PID AND START TIME through <see cref="IProcessSignals"/> (a pid alone can be
/// reused), never by name.
/// </summary>
/// <remarks>
/// <para><b>"No recent CPU"</b> (plan §4.2: under one CPU-second in the last interval) is measured, not remembered: every
/// candidate's <c>/proc/[pid]/stat</c> is read, then again after <see cref="CpuWindow"/>; only a process whose CPU ticks did
/// not move AT ALL in that window — the clock tick is 10 ms, so far below a second — and whose start did not change is a
/// suspect. Just before its signal the run reads it a third time and refuses it if it used ANY CPU since, or is another
/// process now; the signal sender then pins it (<c>pidfd</c>) and compares its start once more.</para>
/// <para>A11 frees MEMORY, not disk: <see cref="ActionRun.FreedBytes"/> and the preview's bytes stay unknown (the memory held is the
/// <see cref="HeldMemoryFact"/> fact), and each ended process's item carries
/// the <c>RssAnon</c> + <c>RssShmem</c> it held when read.</para>
/// </remarks>
public sealed class SuspectTermination : ICleanupAction
{
    /// <summary>The window a suspect must use no CPU in.</summary>
    public static readonly TimeSpan CpuWindow = TimeSpan.FromSeconds(5);

    /// <summary>Plan §5 A11: <c>SIGKILL</c> after 10 s.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(10);

    private const string Kind = "process";

    /// <summary>The fact naming the memory the suspects hold (RssAnon + RssShmem) — memory, so never the preview's bytes.</summary>
    public const string HeldMemoryFact = "heldMemoryBytes";

    public ActionId Id { get; } = ActionId.Find("A11")!;

    public string Summary => "SIGTERM, then SIGKILL after 10 s, of suspect processes (orphaned, idle, old, in an allowed family) - by pid and start, never by name";

    public CommandScope Scope => CommandScope.Machine;

    public IdleRule Idle => IdleRule.Never;

    public IReadOnlyList<HostSide> Sides { get; } = [HostSide.Wsl];

    public IReadOnlyList<CommandTemplate> Commands { get; } = [];

    public async Task<ActionPreview> PreviewAsync(ActionContext context, ActionCommands commands, CancellationToken cancellationToken)
    {
        var families = context.Config.TextList(ConfigKeys.Processes.Families);
        var hours = context.Config.Int(ConfigKeys.Processes.IdleOlderThanHours);
        var what = string.Create(CultureInfo.InvariantCulture, $"suspect processes: orphaned, in the families {string.Join(", ", families)}, older than {hours} h, no terminal, not root's, no CPU in {CpuWindow.TotalSeconds:0} s - SIGTERM, then SIGKILL after {Grace.TotalSeconds:0} s");
        if (context.Paths is not LinuxHostPaths linux || context.Processes(cancellationToken) is not Reading<ProcessSnapshot>.Available { Value: var snapshot })
        {
            return ActionPreview.Unavailable(what, "the process table could not be read");
        }

        var candidates = Candidates(snapshot.All, families, TimeSpan.FromHours(hours), Environment.ProcessId);
        var first = candidates.Select(c => (Entry: c, Sample: Sample(context, linux, c.Pid))).Where(c => c.Sample is not null).ToList();
        if (first.Count > 0)
        {
            await context.Wait(CpuWindow, cancellationToken).ConfigureAwait(false);
        }

        var idle = first
            .Select(c => (c.Entry, Before: c.Sample!, After: Sample(context, linux, c.Entry.Pid)))
            .Where(c => StayedIdle(c.Before, c.After))
            .Select(c => Item(c.Entry, c.After!))
            .ToList();
        return ActionPreview.Of(what, idle.Count, null, "processes read twice from /proc; each item carries the memory it holds (RssAnon + RssShmem, also the heldMemoryBytes fact) - memory, not disk, so no bytes are counted", Held(idle), string.Empty, idle);
    }

    /// <summary>The same process (its start), no CPU tick used, still no terminal, not root's — across the window.</summary>
    private static bool StayedIdle(SuspectSample before, SuspectSample? after) =>
        after is { } now
        && Checks.All(now, n => n.StartTicks == before.StartTicks, n => n.CpuTicks == before.CpuTicks, n => n.Tty == 0, n => n.Uid != 0);

    /// <summary>Memory is not disk (gate finding #4): what the suspects hold is a fact and each item's size, never the preview's
    /// bytes — which a dry run records as "would free" and the logs sum as disk.</summary>
    private static Dictionary<string, long> Held(IReadOnlyList<ActionItem> idle) =>
        new(StringComparer.Ordinal) { [HeldMemoryFact] = idle.Sum(i => i.Bytes ?? 0) };

    /// <summary>Plan §5 A11: suspects exist.</summary>
    public TriggerDecision Trigger(ActionPreview preview, EffectiveConfig config) =>
        new(preview.Count > 0, string.Create(CultureInfo.InvariantCulture, $"{preview.Count} suspect process(es); the trigger is any"));

    public async Task<ActionRun> RunAsync(ActionContext context, ActionPreview preview, ActionCommands commands, CancellationToken cancellationToken)
    {
        if (preview.Targets.Count == 0 || context.Paths is not LinuxHostPaths linux)
        {
            return ActionRun.Nothing(commands.Ran, "no suspect process");
        }

        var judged = await EndAllAsync(context, linux, preview.Targets, cancellationToken).ConfigureAwait(false);
        var verdicts = judged.Select(j => Verdict(j.Item, j.Outcome)).ToList();
        var ended = verdicts.Where(v => v.Ended).Select(v => v.Item).ToList();
        return new ActionRun(ended.Count, null, "A11 frees memory, not disk: each ended process's item carries what it held", null, null, ended, commands.Ran, string.Join("; ", verdicts.Where(v => v.Failure.Length > 0).Select(v => v.Failure).Take(5)))
        {
            NotRemoved = [.. verdicts.Where(v => !v.Ended).Select(v => v.Item)],
        };
    }

    /// <summary>The candidates of plan §4.2, before the CPU window: orphaned, in an allowed family, old enough, no terminal,
    /// not a zombie, not root's, not this process.</summary>
    public static IReadOnlyList<ProcessEntry> Candidates(IEnumerable<ProcessEntry> processes, IReadOnlyList<string> families, TimeSpan olderThan, int ownPid) =>
        [.. processes.Where(p => Checks.All(
            p,
            x => x.Orphaned,
            x => !x.HasTty,
            x => x.State != 'Z',
            x => x.Pid != ownPid,
            x => x.Pid > 1,
            x => x.User != "root",
            x => families.Contains(x.Family, StringComparer.Ordinal),
            x => IsOlderThan(x, olderThan)))];

    private static bool IsOlderThan(ProcessEntry process, TimeSpan olderThan) =>
        process.Age is Reading<TimeSpan>.Available { Value: var age } && age >= olderThan;

    /// <summary>The pid's start, CPU ticks, terminal and owner as <c>/proc</c> answers now; <c>null</c> when it cannot be read
    /// (gone — or never a target: an unread process is not signalled).</summary>
    public static SuspectSample? Sample(ActionContext context, LinuxHostPaths linux, int pid)
    {
        var dir = $"{linux.ProcRoot}/{pid.ToString(CultureInfo.InvariantCulture)}";
        var stat = ProcText.Read(context.Files, $"{dir}/stat").Bind(t => ProcStat.Parse(t, $"{dir}/stat"));
        var status = ProcText.Read(context.Files, $"{dir}/status").Bind(t => ProcStatus.Parse(t, $"{dir}/status"));
        return Reading.Combine(stat, status, (s, st) => new SuspectSample(pid, s.StartTicks, s.CpuTicks, s.TtyNumber, st.Uid)) is Reading<SuspectSample>.Available { Value: var sample } ? sample : null;
    }

    private static ActionItem Item(ProcessEntry entry, SuspectSample sample) =>
        new(Kind, string.Create(CultureInfo.InvariantCulture, $"{entry.Pid} {entry.Name}"), entry.HeldBytes,
            string.Create(CultureInfo.InvariantCulture, $"{entry.Family}, {entry.User}, {entry.Age.Map(a => a.TotalHours).ValueOr(0):0.0} h old: {entry.CommandLine}"))
        {
            Key = string.Create(CultureInfo.InvariantCulture, $"{sample.Pid}:{sample.StartTicks}:{sample.CpuTicks}"),
        };

    /// <summary>Every target read again — the same process, still no CPU, still no terminal — and the ones that pass signalled
    /// TOGETHER by identity: one <c>SIGTERM</c> each, ONE shared grace, then <c>SIGKILL</c> to the survivors (gate finding #9:
    /// three that ignore <c>SIGTERM</c> take one grace, not three).</summary>
    private static async Task<IReadOnlyList<(ActionItem Item, SignalOutcome Outcome)>> EndAllAsync(ActionContext context, LinuxHostPaths linux, IReadOnlyList<ActionItem> targets, CancellationToken cancellationToken)
    {
        var checkedTargets = targets.Select(t => (Item: t, Identity: Identity(t), Refusal: Recheck(Sample(context, linux, Identity(t).Pid), t))).ToList();
        var passing = checkedTargets.Where(c => c.Refusal is null).ToList();
        var outcomes = passing.Count == 0 ? [] : await context.Signals.TerminateAllAsync([.. passing.Select(c => c.Identity)], Grace, cancellationToken).ConfigureAwait(false);
        return [.. checkedTargets.Where(c => c.Refusal is not null).Select(c => (c.Item, c.Refusal!)), .. passing.Zip(outcomes, (c, o) => (c.Item, o))];
    }

    /// <summary>The pid and start a preview item's key names.</summary>
    private static ProcessIdentity Identity(ActionItem target)
    {
        var parts = target.Key.Split(':');
        return new ProcessIdentity(int.Parse(parts[0], CultureInfo.InvariantCulture), long.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    /// <summary>Why a target is not signalled after all — gone, another process now, or it used CPU / gained a terminal /
    /// became root's since the preview; <c>null</c> when it is still the idle suspect the preview saw.</summary>
    private static SignalOutcome? Recheck(SuspectSample? now, ActionItem target)
    {
        var (identity, cpu) = (Identity(target), long.Parse(target.Key.Split(':')[2], CultureInfo.InvariantCulture));
        return now switch
        {
            null => new SignalOutcome.AlreadyGone(),
            { StartTicks: var start } when start != identity.StartTicks => new SignalOutcome.NotTheSame($"pid {identity.Pid} started at tick {start}, not {identity.StartTicks}: another process now"),
            { } changed when Changed(changed, cpu) => new SignalOutcome.NotTheSame($"pid {identity.Pid} used CPU, gained a terminal or changed owner since the preview: kept"),
            _ => null,
        };
    }

    private static bool Changed(SuspectSample now, long cpu) => now.CpuTicks != cpu || now.Tty != 0 || now.Uid == 0;

    /// <summary>What one outcome means for the record: ended (and how), kept (and why), and the failure it counts as, if any.</summary>
    private static (bool Ended, ActionItem Item, string Failure) Verdict(ActionItem item, SignalOutcome outcome) => outcome switch
    {
        SignalOutcome.Ended e => (true, item with { Note = (e.NeededKill ? "ended on SIGKILL: " : "ended on SIGTERM: ") + item.Note }, string.Empty),
        SignalOutcome.AlreadyGone => (false, item with { Note = "already gone" }, string.Empty),
        SignalOutcome.NotTheSame n => (false, item with { Note = n.Reason }, string.Empty),
        SignalOutcome.StillRunning s => (false, item with { Note = s.Reason }, s.Reason),
        SignalOutcome.Refused r => (false, item with { Note = "not signalled: " + r.Reason }, "not signalled: " + r.Reason),
        SignalOutcome.Failed f => (false, item with { Note = f.Reason }, f.Reason),
        _ => throw new System.Diagnostics.UnreachableException("SignalOutcome is a closed set"),
    };
}
