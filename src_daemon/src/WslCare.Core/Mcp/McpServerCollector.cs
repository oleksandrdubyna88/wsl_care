using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Core.Mcp;

/// <summary>
/// The MCP server instances of the AI agents (plan §15q E7.S2d): READ-ONLY towards the servers — it holds no command runner and no
/// signal sender and opens no file under a log root. It takes the process snapshot the probe already read (one <c>/proc</c> walk),
/// measures each instance's CPU (<see cref="PidSamples.Read"/>, the sampler A11 and A18 share) over the interval since its previous
/// sample in the caller's CPU ledger (<see cref="McpCpuLedger"/>, plan E14 S1) — or, for an instance the ledger has no usable point
/// of, across <c>mcpServers.cpuWindowMilliseconds</c>, waiting only then — and reads the watched servers' run logs
/// (<see cref="McpRunLogs"/>). Its one write is that ledger, where the caller's place allows it.
/// </summary>
/// <remarks>Not inside the probe: the action engine takes the probe's sample several times per run, and each would pay the
/// window. Called by <c>status</c> and by <c>collect</c> after their probe.</remarks>
public sealed class McpServerCollector(IFileSystem files, LinuxHostPaths paths, TimeProvider clock, Func<TimeSpan, CancellationToken, Task> wait, McpCpuLedgerPlace ledger)
{
    /// <summary>Why the Windows binary answers no MCP servers.</summary>
    public const string WindowsNotYet = "the Windows binary has no process collector yet (E11): coai-mcp.exe is not counted on Windows";

    public async Task<Reading<McpSample>> SampleAsync(Reading<ProcessSnapshot> processes, EffectiveConfig config, CancellationToken cancellationToken)
    {
        if (processes is not Reading<ProcessSnapshot>.Available { Value: var snapshot })
        {
            return Reading.Missing<McpSample>($"the process table could not be read: {processes.ReasonOrEmpty}");
        }

        var settings = McpSettings.From(config);
        var found = McpInstances.Find(snapshot.All, settings.Watched);
        var listed = found.Instances.Take(settings.MaxInstances).ToList();
        var agesAt = clock.GetUtcNow();
        var (cpu, baseline) = await CpuAsync(listed, settings, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        var logs = settings.Watched.ToDictionary(s => s.Name, s => McpRunLogs.Read(files, paths.Home, s, now, settings, clock, cancellationToken), StringComparer.Ordinal);
        var judge = new McpJudge(settings, now, agesAt, snapshot.All);
        return Reading.Of(new McpSample(
            (int)settings.Window.TotalMilliseconds,
            found.Instances.Count,
            found.NotUnderAgent,
            found.Instances.Sum(f => f.Process.HeldBytes),
            [.. listed.Select(f => judge.Instance(f, cpu[f.Process.Pid], logs[f.Server.Name])).OrderByDescending(i => i.CpuPercent.ValueOr(-1)).ThenBy(i => i.Process.Pid)],
            [.. settings.Watched.Select(s => judge.Summary(s, found.Instances, logs[s.Name]))])
        {
            Baseline = baseline,
        });
    }

    /// <summary>Each instance's CPU % of one core: over the interval since the ledger's point of its identity when there is one
    /// (plan E14 S1), else across the window; then the readings recorded for the next sample where the place allows.</summary>
    private async Task<(IReadOnlyDictionary<int, McpCpu> Cpu, McpCpuBaseline Baseline)> CpuAsync(IReadOnlyList<McpFound> listed, McpSettings settings, CancellationToken cancellationToken)
    {
        if (listed.Count == 0)
        {
            return (new Dictionary<int, McpCpu>(), McpCpuBaseline.NotRecorded(ledger.FileOrEmpty, "no MCP server instance to record"));
        }

        var kernel = ProcText.Bytes(files, $"{paths.ProcRoot}/self/auxv").Bind(bytes => KernelFacts.FromAuxVector(bytes));
        var boot = BootIdentity.Read(paths, files);
        var before = boot.Length == 0 ? McpCpuFile.Empty : McpCpuLedger.Read(files, ledger, settings.LedgerMaxBytes);
        var started = clock.GetTimestamp();
        var first = FirstReads(listed, SampleTime.Of(clock));
        var fromLedger = first.ToDictionary(m => m.Pid, m => FromLedger(m, before, boot, kernel, settings.Bounds));
        var windowed = await WindowAsync([.. first.Where(m => m.Read is not null && fromLedger[m.Pid] is null)], settings.Window, started, kernel, cancellationToken).ConfigureAwait(false);
        var measured = first.Select(m => Combine(m, fromLedger[m.Pid], windowed)).ToList();
        return (measured.ToDictionary(m => m.Pid, m => m.Cpu), RecordAll(boot, before, measured, settings));
    }

    /// <summary>One listed instance being measured: its first read, and that read as a ledger reading when it was taken.</summary>
    private sealed record Measuring(int Pid, Reading<PidSample> Sample, McpCpuReading? Read);

    /// <summary>One instance measured: its CPU, and the reading the ledger keeps for the next sample (none when it has none).</summary>
    private sealed record Measured(int Pid, McpCpu Cpu, McpCpuReading? Read);

    private List<Measuring> FirstReads(IReadOnlyList<McpFound> listed, SampleTime at) =>
    [
        .. listed.Select(f => FirstRead(f.Process) switch
        {
            Reading<PidSample>.Available { Value: var s } => new Measuring(f.Process.Pid, Reading.Of(s), ReadingOf(s, at)),
            var unread => new Measuring(f.Process.Pid, unread, null),
        }),
    ];

    /// <summary>The interval answer when there was one (its reading is the first read), else the window's (its second read),
    /// else unmeasured with why.</summary>
    private static Measured Combine(Measuring m, McpCpu? fromLedger, IReadOnlyDictionary<int, (McpCpu Cpu, McpCpuReading? Read)> windowed) =>
        fromLedger is { } interval ? new(m.Pid, interval, m.Read)
        : windowed.TryGetValue(m.Pid, out var w) ? new(m.Pid, w.Cpu, w.Read)
        : new(m.Pid, McpCpu.Unmeasured(m.Sample.ReasonOrEmpty), null);

    /// <summary>This sample's readings into the caller's ledger, by the two-point rule, capped to what its read cap holds.</summary>
    private McpCpuBaseline RecordAll(string boot, McpCpuFile before, IReadOnlyList<Measured> measured, McpSettings settings) =>
        boot.Length == 0
            ? McpCpuBaseline.NotRecorded(ledger.FileOrEmpty, "the boot id cannot be read, so no reading can name its process across samples")
            : McpCpuLedger.Record(files, ledger, before, McpCpuLedger.Next(before, boot, [.. measured.Select(m => m.Read).OfType<McpCpuReading>()], settings.Bounds, McpCpuLedger.MaxEntries(settings.LedgerMaxBytes)));

    private static McpCpuReading ReadingOf(PidSample sample, SampleTime at) => new(sample.Pid, sample.StartTicks, new McpCpuPoint(sample.CpuTicks, at.Wall, at.MonotonicMs));

    /// <summary>The CPU over the interval since the ledger's point of this identity; <c>null</c> when the ledger has none usable.</summary>
    private static McpCpu? FromLedger(Measuring m, McpCpuFile before, string boot, Reading<KernelFacts> kernel, McpCpuBounds bounds) =>
        m.Read is { } now && McpCpuLedger.Baseline(before, boot, now, bounds) is { } from
            ? IntervalCpu(now.At, from, kernel)
            : null;

    /// <summary>Unmeasured, with no basis, when the kernel's tick rate cannot be read (own code review, finding 2).</summary>
    private static McpCpu IntervalCpu(McpCpuPoint now, McpCpuPoint from, Reading<KernelFacts> kernel)
    {
        var over = TimeSpan.FromMilliseconds(now.MonotonicMs - from.MonotonicMs);
        return kernel is Reading<KernelFacts>.Available { Value: var k }
            ? new McpCpu(Reading.Of(Percent(now.CpuTicks - from.CpuTicks, k, over.TotalSeconds)), McpCpuBasis.Interval, over)
            : McpCpu.Unmeasured(kernel.ReasonOrEmpty);
    }

    /// <summary>The window fallback: the second read across <paramref name="window"/> for each instance with no baseline — the wait
    /// paid only when there is one — at 100 × (Δticks ÷ ticks per second) ÷ the window in seconds, the LONGER of the configured
    /// window and the measured elapsed time (plan round finding 0). Each answer carries the second read as a ledger reading when it
    /// is still the same process.</summary>
    private async Task<IReadOnlyDictionary<int, (McpCpu Cpu, McpCpuReading? Read)>> WindowAsync(IReadOnlyList<Measuring> pending, TimeSpan window, long started, Reading<KernelFacts> kernel, CancellationToken cancellationToken)
    {
        if (pending.Count == 0)
        {
            return new Dictionary<int, (McpCpu, McpCpuReading?)>();
        }

        await wait(window, cancellationToken).ConfigureAwait(false);
        var seconds = Math.Max(window.TotalSeconds, clock.GetElapsedTime(started).TotalSeconds);
        var secondAt = SampleTime.Of(clock);
        return pending.ToDictionary(m => m.Pid, m => Windowed(m.Sample, PidSamples.Read(files, paths, m.Pid), kernel, seconds, secondAt));
    }

    private static (McpCpu Cpu, McpCpuReading? Read) Windowed(Reading<PidSample> first, PidSample? after, Reading<KernelFacts> kernel, double seconds, SampleTime at)
    {
        var rate = Rate(first, after, kernel, seconds);
        var cpu = new McpCpu(rate, rate.IsAvailable ? McpCpuBasis.Window : McpCpuBasis.None, rate.IsAvailable ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero);
        return (cpu, rate.IsAvailable && after is not null ? ReadingOf(after, at) : null);
    }

    private static double Percent(long deltaTicks, KernelFacts kernel, double seconds) =>
        Math.Round(McpSample.PercentPerCore * deltaTicks / kernel.ClockTicksPerSecond / seconds, 1);

    /// <summary>The first read, and only when it is still the process the snapshot saw (final round 3 finding 6: a pid reused
    /// between the snapshot and this read would report another process's CPU as the server's).</summary>
    private Reading<PidSample> FirstRead(ProcessEntry process) => PidSamples.Read(files, paths, process.Pid) switch
    {
        null => Reading.Missing<PidSample>("its /proc stat could not be read"),
        var sample when process.StartTicks is Reading<long>.Available { Value: var ticks } && ticks != sample.StartTicks =>
            Reading.Missing<PidSample>("its pid was taken by another process after the snapshot"),
        var sample => Reading.Of(sample),
    };

    private static Reading<double> Rate(Reading<PidSample> first, PidSample? after, Reading<KernelFacts> kernel, double seconds) =>
        first.Bind(before => after is null ? Reading.Missing<double>("it exited during the window")
            : after.StartTicks != before.StartTicks ? Reading.Missing<double>("its pid was taken by another process during the window")
            : kernel.Map(k => Percent(after.CpuTicks - before.CpuTicks, k, seconds)));
}

/// <summary>The one road in for <c>status</c> and <c>collect</c>: the distro's MCP servers from the probe's own process table, or
/// unavailable on the Windows binary (<see cref="McpServerCollector.WindowsNotYet"/>, the E11 next step). <paramref name="ledger"/>
/// is the caller's CPU ledger (<see cref="McpCpuLedgerPlace.ForStatus"/>, <see cref="McpCpuLedgerPlace.ForCollect"/>).</summary>
public static class McpSampling
{
    public static Task<Reading<McpSample>> SampleAsync(IHostPaths paths, IFileSystem files, TimeProvider clock, Func<TimeSpan, CancellationToken, Task> wait, McpCpuLedgerPlace ledger, ProbeSample sample, EffectiveConfig config, CancellationToken cancellationToken) =>
        paths is LinuxHostPaths linux
            ? new McpServerCollector(files, linux, clock, wait, ledger).SampleAsync(sample.Vm.Bind(vm => vm.Processes), config, cancellationToken)
            : Task.FromResult(Reading.Missing<McpSample>(McpServerCollector.WindowsNotYet));
}

/// <summary>The decisions over one sample: an instance's kind and last log write, a server's starts (plan §15q E7.S2d, Decided
/// 6–8). Pure: everything it reads is handed to it.</summary>
/// <param name="agesAt">The instant the snapshot's ages hold at — taken BEFORE the CPU window (own review M1: a start computed
/// from a now after the window moved past the log's name, and a 3 s window lost every instance its own log). <paramref name="now"/>,
/// read after the window, still dates the activity and the starts windows.</param>
public sealed class McpJudge(McpSettings settings, DateTimeOffset now, DateTimeOffset agesAt, IReadOnlyList<ProcessEntry> processes)
{
    private readonly Dictionary<int, ProcessEntry> _byPid = processes.GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.First());

    public McpInstance Instance(McpFound found, McpCpu cpu, Reading<McpLogs> logs)
    {
        var lastWrite = LastLogWrite(found.Process, logs);
        return new McpInstance(found.Process, found.Server.Name, found.Owner, cpu.Percent, lastWrite, Kind(found.Process, cpu.Percent, lastWrite))
        {
            CpuBasis = cpu.Basis,
            CpuOver = cpu.Over,
        };
    }

    /// <summary>The kind, in the plan's order: unknown, starting, idle, busy without activity, busy.</summary>
    public McpKind Kind(ProcessEntry process, Reading<double> cpu, Reading<DateTimeOffset> lastWrite) => cpu switch
    {
        Reading<double>.Available { Value: var percent } when percent < settings.IdleCpuPercent => Young(process) ? McpKind.Starting : McpKind.Idle,
        Reading<double>.Available => lastWrite is Reading<DateTimeOffset>.Available { Value: var at } && now - at > settings.ActivityWindow ? McpKind.BusyWithoutActivity : McpKind.Busy,
        _ => McpKind.Unknown,
    };

    /// <summary>Younger than the idle minimum age; an unknown age is not young (it cannot be told to be starting).</summary>
    private bool Young(ProcessEntry process) => process.Age is Reading<TimeSpan>.Available { Value: var age } && age < settings.IdleMinAge;

    /// <summary>The newest last write of THIS process's run logs — a file named before the process started (beyond the start
    /// tolerance) belongs to an earlier process with the same pid and is not its activity.</summary>
    public Reading<DateTimeOffset> LastLogWrite(ProcessEntry process, Reading<McpLogs> logs) =>
        logs.Bind(l => l.ByPid[process.Pid].Where(f => IsOf(f, process)).Select(f => f.LastWrite).ToList() is { Count: > 0 } writes
            ? Reading.Of(writes.Max())
            : Reading.Missing<DateTimeOffset>($"no log file of pid {process.Pid} in {l.Root}"));

    private bool IsOf(McpLogFile file, ProcessEntry process) =>
        StartOf(process) is not { } start || file.NamedAt >= start - PidSamples.StartTolerance;

    private DateTimeOffset? StartOf(ProcessEntry process) =>
        process.Age is Reading<TimeSpan>.Available { Value: var age } ? agesAt - age : null;

    public McpServerSummary Summary(McpServerEntry server, IReadOnlyList<McpFound> instances, Reading<McpLogs> logs)
    {
        var mine = instances.Where(i => i.Server.Name == server.Name).ToList();
        var minutes = (int)settings.StartsWindow.TotalMinutes;
        // Final round 3 finding 5: an exhaustive match — a new layout must say how its starts are counted, never fall back silently.
        return new McpServerSummary(server.Name, mine.Count, server.Logs switch
        {
            McpLogLayout.FamilyRunLogs => FromLogs(server, logs, minutes),
            McpLogLayout.None => FromLive(mine, minutes),
            _ => throw new System.Diagnostics.UnreachableException("McpLogLayout is a closed set"),
        });
    }

    private McpStarts FromLogs(McpServerEntry server, Reading<McpLogs> logs, int minutes)
    {
        var started = logs.Map(l => StartsFromLogs(server, l.Files));
        return new McpStarts(started.Map(s => s.Count), McpStartsBasis.LogNames, minutes)
        {
            Recent = [.. started.ValueOr([]).OrderByDescending(f => f.NamedAt).ThenBy(f => f.Pid).Take(settings.MaxStartsListed).Select(f => new McpStart(f.NamedAt, f.Pid, f.LastWrite, IsRunningRun(server, f)))],
        };
    }

    /// <summary>The live-younger fallback: each young instance is a start at its own start time, running.</summary>
    private McpStarts FromLive(IReadOnlyList<McpFound> mine, int minutes)
    {
        var young = mine.Where(m => Young(m.Process, settings.StartsWindow) && StartOf(m.Process) is not null).ToList();
        return new McpStarts(Reading.Of(young.Count), McpStartsBasis.LiveYounger, minutes)
        {
            Recent = [.. young.Select(m => new McpStart(StartOf(m.Process)!.Value, m.Process.Pid, StartOf(m.Process)!.Value, true)).OrderByDescending(s => s.At).ThenBy(s => s.Pid).Take(settings.MaxStartsListed)],
        };
    }

    /// <summary>This start's run still runs: its pid is this server now, and that process is the one the file was named for.</summary>
    private bool IsRunningRun(McpServerEntry server, McpLogFile file) =>
        _byPid.TryGetValue(file.Pid, out var running) && McpInstances.ServerOf(running, [server]) is not null && IsOf(file, running);

    private static bool Young(ProcessEntry process, TimeSpan window) => process.Age is Reading<TimeSpan>.Available { Value: var age } && age < window;

    /// <summary>The run logs named within the window, a continuation of a run that outlived the day excepted (Decided 8).</summary>
    private List<McpLogFile> StartsFromLogs(McpServerEntry server, IReadOnlyList<McpLogFile> files) =>
        [.. files.Where(f => f.NamedAt >= now - settings.StartsWindow && f.NamedAt <= now && !IsContinuation(server, f, files))];

    /// <summary>A <c>00-00-00</c> file is a continuation when its pid runs now AS THIS SERVER and started before that midnight;
    /// otherwise — the pid no longer runs, or now belongs to another program (own review m1) — when the day before holds a file
    /// of the same pid (plan round finding 1 — a reused pid must not hide a start). Residual: an exited server that started at
    /// exactly 00:00:00 on a pid an earlier run of the day before also used reads as a continuation (one start missed).</summary>
    private bool IsContinuation(McpServerEntry server, McpLogFile file, IReadOnlyList<McpLogFile> files) =>
        file.AtMidnight && (LiveStartOf(server, file.Pid) is { } start
            ? start < file.NamedAt - PidSamples.StartTolerance
            : files.Any(f => f.Pid == file.Pid && f.NamedAt < file.NamedAt));

    /// <summary>The start of the process running at <paramref name="pid"/> when it is <paramref name="server"/> itself.</summary>
    private DateTimeOffset? LiveStartOf(McpServerEntry server, int pid) =>
        _byPid.TryGetValue(pid, out var running) && McpInstances.ServerOf(running, [server]) is not null ? StartOf(running) : null;
}
