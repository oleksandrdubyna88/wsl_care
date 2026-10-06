using WslCare.Core.Config;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Core.Collectors;

/// <summary>
/// One process of the distro's PID namespace, as the report shows it (plan §4.2 as amended by §15b #4).
/// </summary>
/// <param name="RssAnonBytes"><c>RssAnon</c>: private anonymous memory.</param>
/// <param name="RssShmemBytes"><c>RssShmem</c>: shared memory and tmpfs pages it maps.</param>
/// <param name="Age">Since the process started; unavailable when the boot time or the clock tick is.</param>
/// <param name="CpuSeconds">User + system CPU time used so far.</param>
/// <param name="Cwd">Its working directory; unreadable for another user's process unless run as root.</param>
/// <param name="CommandLine">The argv, secret-looking values redacted, cut to 200 characters.</param>
/// <param name="Orphaned">Suspect input (plan §4.2): its parent is pid 1 or a <c>systemd --user</c>.</param>
/// <param name="HasTty">Suspect input: a controlling terminal — A11 never targets such a process.</param>
/// <param name="UnderMnt">Its cwd or one of its arguments is under <c>/mnt/</c> — a 9p walker candidate.</param>
public sealed record ProcessEntry(
    int Pid,
    int ParentPid,
    string User,
    string Name,
    char State,
    long RssAnonBytes,
    long RssShmemBytes,
    Reading<TimeSpan> Age,
    Reading<double> CpuSeconds,
    Reading<string> Cwd,
    string CommandLine,
    string Family,
    bool Orphaned,
    bool HasTty,
    bool UnderMnt)
{
    /// <summary>What the process holds of <c>AnonPages</c> + <c>Shmem</c>: the ranking key.</summary>
    public long HeldBytes => RssAnonBytes + RssShmemBytes;
}

/// <summary>A family's total (plan §4.2): how many processes, how much they hold.</summary>
public sealed record FamilyTotal(string Name, int Count, long HeldBytes);

/// <summary>The process table of one sample.</summary>
/// <param name="ProcessCount">User processes read, container members excluded.</param>
/// <param name="KernelThreads">Kernel threads seen and left out (they own no user memory).</param>
/// <param name="Vanished">Listed, then gone before their status could be read — normal churn.</param>
/// <param name="ContainerProcesses">Processes recognised by cgroup as members of a container whose memory is
/// counted through its cgroup, and therefore NOT counted here again (plan §15b #4).</param>
/// <param name="HeldBytesTotal">Σ (<c>RssAnon</c> + <c>RssShmem</c>) over the counted processes.</param>
/// <param name="All">Every counted process, largest holder first — what the A11 suspect selection (E3) reads;
/// the report shows <see cref="Top"/>.</param>
public sealed record ProcessSnapshot(
    int ProcessCount,
    int KernelThreads,
    int Vanished,
    int ContainerProcesses,
    long HeldBytesTotal,
    IReadOnlyList<ProcessEntry> All,
    IReadOnlyList<FamilyTotal> Families)
{
    /// <summary>The <see cref="ProcessCollector.TopCount"/> largest holders, largest first.</summary>
    public IReadOnlyList<ProcessEntry> Top => [.. All.Take(ProcessCollector.TopCount)];

    /// <summary>Every counted process whose cwd or arguments are under <c>/mnt/</c>.</summary>
    public IReadOnlyList<ProcessEntry> MntWalkers => [.. All.Where(e => e.UnderMnt)];
}

/// <summary>
/// Reads <c>/proc/[pid]/{status,stat,cgroup,cmdline,cwd}</c> for every process through
/// <see cref="IFileSystem"/> — no <c>ps</c>, no process started (plan §15b #5).
/// </summary>
public sealed class ProcessCollector(IFileSystem files, LinuxHostPaths paths, TimeProvider clock)
{
    /// <summary>Plan §4.2: the top 30.</summary>
    public static int TopCount => Tuning.Current.Int(ConfigKeys.Processes.TopCount);

    private const string MntPrefix = "/mnt/";

    /// <summary>One process as read, before it is placed: the inputs the entry is built from.</summary>
    private sealed record Raw(int Pid, ProcStatus Status, Reading<ProcStat> Stat, string ContainerId, IReadOnlyList<string> Argv, Reading<string> Cwd);

    /// <summary>What turns ticks into time: the kernel's facts, the boot instant, now.</summary>
    private sealed record Clocks(Reading<KernelFacts> Kernel, Reading<DateTimeOffset> Boot, DateTimeOffset Now);

    public Reading<ProcessSnapshot> Read(Reading<KernelFacts> kernel, ContainerSet containers, CancellationToken cancellationToken)
    {
        if (!files.DirectoryExists(paths.ProcRoot))
        {
            return Reading.Missing<ProcessSnapshot>($"{paths.ProcRoot} does not exist: no procfs to read processes from");
        }

        var pids = files.ListDirectories(paths.ProcRoot).Select(ProcText.LastSegment).Where(IsPid).ToList();
        var raws = pids.Select(pid => ReadOne(int.Parse(pid, System.Globalization.CultureInfo.InvariantCulture), cancellationToken)).ToList();
        var clocks = new Clocks(kernel, ProcText.Read(files, $"{paths.ProcRoot}/stat").Bind(BootTime.Parse), clock.GetUtcNow());
        return Reading.Of(Snapshot(raws, pids.Count, containers, Users(), clocks));
    }

    private static ProcessSnapshot Snapshot(IReadOnlyList<Reading<Raw>> reads, int listed, ContainerSet containers, IReadOnlyDictionary<int, string> users, Clocks clocks)
    {
        var raws = reads.OfType<Reading<Raw>.Available>().Select(r => r.Value).ToList();
        var user = raws.Where(r => !r.Status.IsKernelThread).ToList();
        var inContainer = user.Where(r => containers.CountedIds.Contains(r.ContainerId)).ToList();
        var argvByPid = user.ToDictionary(r => r.Pid, r => r.Argv);
        var counted = user.Where(r => !containers.CountedIds.Contains(r.ContainerId)).Select(r => Entry(r, users, argvByPid, clocks)).OrderByDescending(e => e.HeldBytes).ThenBy(e => e.Pid).ToList();
        return new ProcessSnapshot(
            counted.Count,
            raws.Count - user.Count,
            listed - raws.Count,
            inContainer.Count,
            counted.Sum(e => e.HeldBytes),
            counted,
            [.. counted.GroupBy(e => e.Family).Select(g => new FamilyTotal(g.Key, g.Count(), g.Sum(e => e.HeldBytes))).OrderByDescending(f => f.HeldBytes)]);
    }

    private static ProcessEntry Entry(Raw raw, IReadOnlyDictionary<int, string> users, IReadOnlyDictionary<int, IReadOnlyList<string>> argvByPid, Clocks clocks) =>
        new(
            raw.Pid,
            raw.Status.ParentPid,
            users.GetValueOrDefault(raw.Status.Uid, raw.Status.Uid.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            raw.Status.Name,
            raw.Status.State,
            raw.Status.RssAnonBytes,
            raw.Status.RssShmemBytes,
            Age(raw.Stat, clocks),
            Reading.Combine(raw.Stat, clocks.Kernel, (s, k) => (double)s.CpuTicks / k.ClockTicksPerSecond),
            raw.Cwd,
            CommandLineText.Shown(raw.Argv),
            ProcessFamilies.Of(raw.Argv, raw.Status.Name),
            IsOrphaned(raw.Status.ParentPid, argvByPid),
            raw.Stat.Map(s => s.TtyNumber != 0).ValueOr(false),
            IsUnderMnt(raw));

    private static Reading<TimeSpan> Age(Reading<ProcStat> stat, Clocks clocks) =>
        Reading.Combine(stat, Reading.Combine(clocks.Kernel, clocks.Boot, (k, b) => (k, b)), (s, kb) =>
            clocks.Now - kb.b.AddSeconds((double)s.StartTicks / kb.k.ClockTicksPerSecond));

    /// <summary>Plan §4.2: reparented to init, or to the user's systemd manager.</summary>
    private static bool IsOrphaned(int parentPid, IReadOnlyDictionary<int, IReadOnlyList<string>> argvByPid) =>
        parentPid == 1 || (argvByPid.TryGetValue(parentPid, out var parent) && IsUserManager(parent));

    private static bool IsUserManager(IReadOnlyList<string> argv) =>
        argv.Count > 1 && argv[0].EndsWith("systemd", StringComparison.Ordinal) && argv.Contains("--user");

    private static bool IsUnderMnt(Raw raw) =>
        raw.Cwd.Map(c => c.StartsWith(MntPrefix, StringComparison.Ordinal)).ValueOr(false)
        || raw.Argv.Any(a => a.StartsWith(MntPrefix, StringComparison.Ordinal) || a.Contains("=" + MntPrefix, StringComparison.Ordinal));

    private static bool IsPid(string name) => name.Length > 0 && name.All(char.IsAsciiDigit);

    /// <summary>One process; unavailable when its status cannot be read — it exited between the listing
    /// and the read, which is normal churn, counted as <see cref="ProcessSnapshot.Vanished"/>.</summary>
    private Reading<Raw> ReadOne(int pid, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dir = $"{paths.ProcRoot}/{pid}";
        return ProcText.Read(files, $"{dir}/status")
            .Bind(text => ProcStatus.Parse(text, $"{dir}/status"))
            .Map(status => new Raw(
                pid,
                status,
                ProcText.Read(files, $"{dir}/stat").Bind(text => ProcStat.Parse(text, $"{dir}/stat")),
                ContainerCgroups.ContainerIdOf(ProcText.Read(files, $"{dir}/cgroup").Map(ProcCgroup.Parse).ValueOr(string.Empty)),
                ProcText.Bytes(files, $"{dir}/cmdline").Map(b => CommandLineText.Arguments(b)).ValueOr([]),
                Cwd($"{dir}/cwd")));
    }

    private Reading<string> Cwd(string link) => files.ReadLink(link) switch
    {
        LinkReadResult.Target target => Reading.Of(target.Path),
        LinkReadResult.NotALink => Reading.Missing<string>($"{link} is not a link"),
        LinkReadResult.Unreadable unreadable => Reading.Missing<string>($"{link} could not be read: {unreadable.Reason}"),
        _ => throw new System.Diagnostics.UnreachableException("LinkReadResult is a closed set"),
    };

    /// <summary>uid → name; an unreadable user database leaves owners shown as their uid.</summary>
    private IReadOnlyDictionary<int, string> Users() =>
        ProcText.Read(files, paths.PasswdFile).Map(Passwd.Parse).ValueOr(new Dictionary<int, string>());
}
