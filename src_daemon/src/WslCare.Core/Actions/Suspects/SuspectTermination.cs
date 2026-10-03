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
/// <para>A11 frees MEMORY, not disk: <see cref="ActionRun.FreedBytes"/> stays unknown, and each ended process's item carries
/// the <c>RssAnon</c> + <c>RssShmem</c> it held when read.</para>
/// </remarks>
public sealed class SuspectTermination : ICleanupAction
{
    /// <summary>The window a suspect must use no CPU in.</summary>
    public static readonly TimeSpan CpuWindow = TimeSpan.FromSeconds(5);

    /// <summary>Plan §5 A11: <c>SIGKILL</c> after 10 s.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(10);

    private const string Kind = "process";

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
            .Where(c => c.After is { } after && after.StartTicks == c.Before.StartTicks && after.CpuTicks == c.Before.CpuTicks && after.Tty == 0 && after.Uid != 0)
            .Select(c => Item(c.Entry, c.After!))
            .ToList();
        return ActionPreview.Of(what, idle.Count, idle.Sum(i => i.Bytes ?? 0), "processes read twice from /proc; the bytes are the memory they hold (RssAnon + RssShmem), not disk", new Dictionary<string, long>(StringComparer.Ordinal), string.Empty, idle);
    }

    /// <summary>Plan §5 A11: suspects exist.</summary>
    public TriggerDecision Trigger(ActionPreview preview, EffectiveConfig config) =>
        new(preview.Count > 0, string.Create(CultureInfo.InvariantCulture, $"{preview.Count} suspect process(es); the trigger is any"));

    public async Task<ActionRun> RunAsync(ActionContext context, ActionPreview preview, ActionCommands commands, CancellationToken cancellationToken)
    {
        if (preview.Targets.Count == 0 || context.Paths is not LinuxHostPaths linux)
        {
            return ActionRun.Nothing(commands.Ran, "no suspect process");
        }

        var ended = new List<ActionItem>();
        var kept = new List<ActionItem>();
        var failures = new List<string>();
        foreach (var target in preview.Targets)
        {
            var (item, outcome) = await EndAsync(context, linux, target, cancellationToken).ConfigureAwait(false);
            Sort(item, outcome, ended, kept, failures);
        }

        return new ActionRun(ended.Count, null, "A11 frees memory, not disk: each ended process's item carries what it held", null, null, ended, commands.Ran, string.Join("; ", failures.Take(5)))
        {
            NotRemoved = kept,
        };
    }

    /// <summary>The candidates of plan §4.2, before the CPU window: orphaned, in an allowed family, old enough, no terminal,
    /// not a zombie, not root's, not this process.</summary>
    public static IReadOnlyList<ProcessEntry> Candidates(IEnumerable<ProcessEntry> processes, IReadOnlyList<string> families, TimeSpan olderThan, int ownPid) =>
        [.. processes.Where(p =>
            p.Orphaned && !p.HasTty && p.State != 'Z' && p.Pid != ownPid && p.Pid > 1 && p.User != "root"
            && families.Contains(p.Family, StringComparer.Ordinal)
            && p.Age is Reading<TimeSpan>.Available { Value: var age } && age >= olderThan)];

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

    /// <summary>One target: read again — the same process, still no CPU, still no terminal — then signalled by identity.</summary>
    private static async Task<(ActionItem Item, SignalOutcome Outcome)> EndAsync(ActionContext context, LinuxHostPaths linux, ActionItem target, CancellationToken cancellationToken)
    {
        var parts = target.Key.Split(':');
        var (pid, start, cpu) = (int.Parse(parts[0], CultureInfo.InvariantCulture), long.Parse(parts[1], CultureInfo.InvariantCulture), long.Parse(parts[2], CultureInfo.InvariantCulture));
        var now = Sample(context, linux, pid);
        if (now is null)
        {
            return (target, new SignalOutcome.AlreadyGone());
        }

        if (now.StartTicks != start)
        {
            return (target, new SignalOutcome.NotTheSame($"pid {pid} started at tick {now.StartTicks}, not {start}: another process now"));
        }

        if (now.CpuTicks != cpu || now.Tty != 0 || now.Uid == 0)
        {
            return (target, new SignalOutcome.NotTheSame($"pid {pid} used CPU, gained a terminal or changed owner since the preview: kept"));
        }

        return (target, await context.Signals.TerminateAsync(new ProcessIdentity(pid, start), Grace, cancellationToken).ConfigureAwait(false));
    }

    private static void Sort(ActionItem item, SignalOutcome outcome, List<ActionItem> ended, List<ActionItem> kept, List<string> failures)
    {
        switch (outcome)
        {
            case SignalOutcome.Ended e:
                ended.Add(item with { Note = (e.NeededKill ? "ended on SIGKILL: " : "ended on SIGTERM: ") + item.Note });
                break;
            case SignalOutcome.AlreadyGone:
                kept.Add(item with { Note = "already gone" });
                break;
            case SignalOutcome.NotTheSame n:
                kept.Add(item with { Note = n.Reason });
                break;
            case SignalOutcome.StillRunning s:
                kept.Add(item with { Note = s.Reason });
                failures.Add(s.Reason);
                break;
            case SignalOutcome.Refused r:
                kept.Add(item with { Note = "not signalled: " + r.Reason });
                failures.Add("not signalled: " + r.Reason);
                break;
            case SignalOutcome.Failed f:
                kept.Add(item with { Note = f.Reason });
                failures.Add(f.Reason);
                break;
            default:
                throw new System.Diagnostics.UnreachableException("SignalOutcome is a closed set");
        }
    }
}
