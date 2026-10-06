using WslCare.Core.Actions.Engine;
using WslCare.Core.Actions.Suspects;
using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Core.Mcp;

/// <summary>
/// The MCP server instances of the AI agents (plan §15q E7.S2d): READ-ONLY — it holds no command runner and no signal sender,
/// opens no file under a log root and writes nothing. It takes the process snapshot the probe already read (one <c>/proc</c>
/// walk), reads each instance's <c>stat</c> twice across <c>mcpServers.cpuWindowMilliseconds</c> (<see cref="SuspectTermination.Sample(IFileSystem, LinuxHostPaths, int)"/>,
/// reused as is) — waiting only when an instance exists — and the watched servers' run logs (<see cref="McpRunLogs"/>).
/// </summary>
/// <remarks>Not inside the probe: the action engine takes the probe's sample several times per run, and each would pay the
/// window. Called by <c>status</c> and by <c>collect</c> after their probe.</remarks>
public sealed class McpServerCollector(IFileSystem files, LinuxHostPaths paths, TimeProvider clock, Func<TimeSpan, CancellationToken, Task> wait)
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
        var cpu = await CpuAsync(listed, settings.Window, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        var logs = settings.Watched.ToDictionary(s => s.Name, s => McpRunLogs.Read(files, paths.Home, s, now, settings, clock, cancellationToken), StringComparer.Ordinal);
        var judge = new McpJudge(settings, now, snapshot.All);
        return Reading.Of(new McpSample(
            (int)settings.Window.TotalMilliseconds,
            found.Instances.Count,
            found.NotUnderAgent,
            found.Instances.Sum(f => f.Process.HeldBytes),
            [.. listed.Select(f => judge.Instance(f, cpu[f.Process.Pid], logs[f.Server.Name])).OrderByDescending(i => i.CpuPercent.ValueOr(-1)).ThenBy(i => i.Process.Pid)],
            [.. settings.Watched.Select(s => judge.Summary(s, found.Instances, logs[s.Name]))]));
    }

    /// <summary>Each instance's CPU % of one core across the window: 100 × (Δticks ÷ ticks per second) ÷ the window in seconds — the
    /// LONGER of the configured window and the measured elapsed time (plan round finding 0).</summary>
    private async Task<IReadOnlyDictionary<int, Reading<double>>> CpuAsync(IReadOnlyList<McpFound> listed, TimeSpan window, CancellationToken cancellationToken)
    {
        if (listed.Count == 0)
        {
            return new Dictionary<int, Reading<double>>();
        }

        var kernel = ProcText.Bytes(files, $"{paths.ProcRoot}/self/auxv").Bind(bytes => KernelFacts.FromAuxVector(bytes));
        var started = clock.GetTimestamp();
        var first = listed.Select(f => (f.Process.Pid, Sample: SuspectTermination.Sample(files, paths, f.Process.Pid))).ToList();
        await wait(window, cancellationToken).ConfigureAwait(false);
        var seconds = Math.Max(window.TotalSeconds, clock.GetElapsedTime(started).TotalSeconds);
        return first.ToDictionary(f => f.Pid, f => Rate(f.Sample, SuspectTermination.Sample(files, paths, f.Pid), kernel, seconds));
    }

    private static Reading<double> Rate(SuspectSample? before, SuspectSample? after, Reading<KernelFacts> kernel, double seconds) =>
        before is null ? Reading.Missing<double>("its /proc stat could not be read")
        : after is null ? Reading.Missing<double>("it exited during the window")
        : after.StartTicks != before.StartTicks ? Reading.Missing<double>("its pid was taken by another process during the window")
        : kernel.Map(k => Math.Round(McpSample.PercentPerCore * (after.CpuTicks - before.CpuTicks) / k.ClockTicksPerSecond / seconds, 1));
}

/// <summary>The one road in for <c>status</c> and <c>collect</c>: the distro's MCP servers from the probe's own process table, or
/// unavailable on the Windows binary (<see cref="McpServerCollector.WindowsNotYet"/>, the E11 next step).</summary>
public static class McpSampling
{
    public static Task<Reading<McpSample>> SampleAsync(IHostPaths paths, IFileSystem files, TimeProvider clock, Func<TimeSpan, CancellationToken, Task> wait, ProbeSample sample, EffectiveConfig config, CancellationToken cancellationToken) =>
        paths is LinuxHostPaths linux
            ? new McpServerCollector(files, linux, clock, wait).SampleAsync(sample.Vm.Bind(vm => vm.Processes), config, cancellationToken)
            : Task.FromResult(Reading.Missing<McpSample>(McpServerCollector.WindowsNotYet));
}

/// <summary>The decisions over one sample: an instance's kind and last log write, a server's starts (plan §15q E7.S2d, Decided
/// 6–8). Pure: everything it reads is handed to it.</summary>
public sealed class McpJudge(McpSettings settings, DateTimeOffset now, IReadOnlyList<ProcessEntry> processes)
{
    private readonly Dictionary<int, ProcessEntry> _byPid = processes.GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.First());

    public McpInstance Instance(McpFound found, Reading<double> cpu, Reading<McpLogs> logs)
    {
        var lastWrite = LastLogWrite(found.Process, logs);
        return new McpInstance(found.Process, found.Server.Name, found.Owner, cpu, lastWrite, Kind(found.Process, cpu, lastWrite));
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
        logs.Bind(l => l.Files.Where(f => f.Pid == process.Pid && IsOf(f, process)).Select(f => f.LastWrite).ToList() is { Count: > 0 } writes
            ? Reading.Of(writes.Max())
            : Reading.Missing<DateTimeOffset>($"no log file of pid {process.Pid} in {l.Root}"));

    private bool IsOf(McpLogFile file, ProcessEntry process) =>
        StartOf(process) is not { } start || file.NamedAt >= start - RunningState.StartTolerance;

    private DateTimeOffset? StartOf(ProcessEntry process) =>
        process.Age is Reading<TimeSpan>.Available { Value: var age } ? now - age : null;

    public McpServerSummary Summary(McpServerEntry server, IReadOnlyList<McpFound> instances, Reading<McpLogs> logs)
    {
        var mine = instances.Where(i => i.Server.Name == server.Name).ToList();
        var minutes = (int)settings.StartsWindow.TotalMinutes;
        return new McpServerSummary(server.Name, mine.Count, server.Logs is McpLogLayout.FamilyRunLogs
            ? new McpStarts(logs.Map(l => StartsFromLogs(l.Files)), McpStartsBasis.LogNames, minutes)
            : new McpStarts(Reading.Of(mine.Count(m => Young(m.Process, settings.StartsWindow))), McpStartsBasis.LiveYounger, minutes));
    }

    private static bool Young(ProcessEntry process, TimeSpan window) => process.Age is Reading<TimeSpan>.Available { Value: var age } && age < window;

    /// <summary>The run logs named within the window, a continuation of a run that outlived the day excepted (Decided 8).</summary>
    private int StartsFromLogs(IReadOnlyList<McpLogFile> files) =>
        files.Count(f => f.NamedAt >= now - settings.StartsWindow && f.NamedAt <= now && !IsContinuation(f, files));

    /// <summary>A <c>00-00-00</c> file is a continuation when its pid runs now and started before that midnight; when the pid no
    /// longer runs, when the day before holds a file of the same pid (plan round finding 1 — a reused pid must not hide a
    /// start). Residual: an exited server that started at exactly 00:00:00 on a pid an earlier run also used reads as a
    /// continuation — one start missed, never one invented.</summary>
    private bool IsContinuation(McpLogFile file, IReadOnlyList<McpLogFile> files) =>
        file.AtMidnight && (_byPid.TryGetValue(file.Pid, out var running) && StartOf(running) is { } start
            ? start < file.NamedAt - RunningState.StartTolerance
            : files.Any(f => f.Pid == file.Pid && f.NamedAt < file.NamedAt));
}
