using System.Globalization;
using System.Text;
using System.Text.Json;

using WslCare.Core.Collect;
using WslCare.Core.Config;
using WslCare.Core.Events;
using WslCare.Core.Json;
using WslCare.Core.Mcp;
using WslCare.Core.Records;
using WslCare.Core.Status;
using WslCare.Core.Thresholds;

namespace WslCare.Cli.Commands;

/// <summary>
/// <c>status [--json]</c> (plan §6): the fast snapshot of this binary's side, plus the slow parts from
/// the last full run with their age. It reads files and asks the OS for counters; it starts no process
/// (plan §15b #5) — it is handed the probe and the history, never the command runner.
/// </summary>
internal static class StatusCommand
{
    public static int Run(Request.Status request, CliHost host, ConfigLoadResult loaded, TextWriter stdout, CancellationToken cancellationToken)
    {
        var sample = host.Probe.Sample(cancellationToken);
        // A console program has no synchronisation context; the MCP servers' CPU window is the one wait status has (plan §15q E7.S2d).
        var mcp = McpSampling.SampleAsync(host.Paths, host.Files, host.Clock, host.Wait, sample, loaded.Config, cancellationToken).GetAwaiter().GetResult();
        var now = host.Clock.GetUtcNow();
        var history = RunHistory.Read(host.Paths, host.Files);
        var last = LastFullRun.From(history, now);
        var report = StatusReports.From(sample, last, loaded) with
        {
            // The follower's summary, never the raw day files: status's cost must not grow with the starts recorded (gate finding #8).
            ContainerStarts = new ContainerStartsStore(host.Paths, host.Files).ReadSummary(now),
            Folders = FoldersReports.From(last.Folders, last.PreviousFolders, measuredThisRun: false),
            Verdicts = [.. StatusVerdicts.From(sample, FullRunVerdicts.Read(host.Paths, host.Files, history), loaded.Config, now), .. StatusVerdicts.Sampled(McpVerdicts.From(mcp, loaded.Config), sample.SampledAt)],
            ProductVersion = Program.VersionText,
            Actions = ThisSidesActions(host),
            Capabilities = Capabilities.All,
            // Read-only: judged, never swept — status is unprivileged (plan §15b #3, §15j M3).
            Running = RunningReports.Read(host.Paths, host.Files, host.Processes, now, Core.Actions.Engine.RunningReadRetry.Default, history),
            LastCleanup = LastCleanups.From(history),
            Limits = StatusLimits.From(loaded.Config),
            McpServers = McpServersReport.From(mcp),
        };
        return Output.Answer(stdout, request.Json ? JsonSerializer.Serialize(report, WslCareJsonContext.Default.StatusReport) : StatusText.Render(report));
    }

    /// <summary>The ids this binary's registry holds for its own side, in the order a run takes them (plan §15f #3).</summary>
    private static IReadOnlyList<string> ThisSidesActions(CliHost host) =>
        [.. Core.Actions.ActionId.ExecutionOrder.Where(id => host.Actions.Find(id)?.Sides.Contains(host.Paths.Side) == true).Select(id => id.Text)];
}

/// <summary>The human form: a few lines a person reads at a terminal. The extension reads the JSON.</summary>
internal static class StatusText
{
    private const double BytesPerGibibyte = 1024d * 1024 * 1024;
    private const int TopShown = 5;

    public static string Render(StatusReport report)
    {
        var text = new StringBuilder()
            .AppendLine(Invariant($"wsl-care status ({report.Side}), sampled {report.SampledAt.UtcDateTime:yyyy-MM-dd HH:mm:ss}Z in {report.SampleMilliseconds} ms"));
        if (report.ObserveOnly)
        {
            text.AppendLine("observe-only: a configuration layer is invalid (\"wsl-care config get\" names it)");
        }

        AppendVm(text, report.Vm);
        AppendHost(text, report.Host);
        text.AppendLine($"docker stats: {Slow(report.Slow.ContainerStats)}");
        text.AppendLine($"windows clock: {Slow(report.Slow.WindowsClock)}");
        text.AppendLine(McpServers(report.McpServers));
        text.AppendLine(Verdicts(report.Verdicts ?? []));
        text.AppendLine(Running(report.Running));
        text.AppendLine(LastCleanup(report.LastCleanup));
        text.Append(Starts(report.ContainerStarts));
        return text.ToString();
    }

    /// <summary>One line (plan §15q E7.S2d): <c>mcp servers: 7 (0 idle, 7 busy without a log write), 2.60 cores, 0.40 GiB; starts
    /// coai-mcp 34 in 10 min</c>.</summary>
    private static string McpServers(McpServersReport? mcp) => mcp switch
    {
        null => "mcp servers: not read",
        { Available: true } => Invariant($"mcp servers: {mcp.Count} ({(mcp.Listed < mcp.Count ? Invariant($"of the {mcp.Listed} listed: ") : string.Empty)}{mcp.IdleCount} idle, {mcp.BusyWithoutActivityCount} busy without a log write), {Number(mcp.CpuCores!)} cores, {mcp.HeldBytes / BytesPerGibibyte:0.00} GiB; starts ") + string.Join(", ", mcp.Servers!.Select(s => Invariant($"{s.Name} {(s.Starts.Available ? Invariant($"{s.Starts.Value:0}") : "?")} in {s.StartsWindowMinutes} min"))),
        _ => $"mcp servers: unavailable ({mcp.Reason})",
    };

    /// <summary><c>running: none</c>, or the state and what it means (<c>running: wedged - run … is wedged: …</c>).</summary>
    private static string Running(RunningReport? running) => running switch
    {
        null => "running: not read",
        { Reason: null } => $"running: {running.State}",
        _ => $"running: {running.State} - {running.Reason}",
    };

    private static string LastCleanup(LastCleanupReport? last) => last switch
    {
        null => "last cleanup: not read",
        { Available: true } => Invariant($"last cleanup: run {last.RunId} ({last.Trigger}), {last.Count} removed, freed {last.FreedBytes / BytesPerGibibyte:0.00} GiB"),
        { Reason: LastCleanups.NoneYet } => "last cleanup: none yet",
        _ => $"last cleanup: unavailable ({last.Reason})",
    };

    /// <summary>One line: how many verdicts stand at each level, worst first, naming the ones that need a look
    /// (<c>verdicts: 1 critical (memory.fragmentation), 2 warn (…), 12 ok, 8 unknown</c>).</summary>
    private static string Verdicts(IReadOnlyList<Core.Thresholds.Verdict> verdicts)
    {
        Core.Thresholds.Level[] order = [Core.Thresholds.Level.Critical, Core.Thresholds.Level.Warn, Core.Thresholds.Level.Ok, Core.Thresholds.Level.Unknown];
        var groups = order
            .Select(level => (Level: level, Ids: verdicts.Where(v => v.Level == level).Select(v => v.Id).ToList()))
            .Where(g => g.Ids.Count > 0)
            .Select(g => VerdictGroup(g.Level, g.Ids));
        return $"verdicts: {(verdicts.Count == 0 ? "none" : string.Join(", ", groups))}";
    }

    private static string VerdictGroup(Core.Thresholds.Level level, IReadOnlyList<string> ids) => level switch
    {
        Core.Thresholds.Level.Critical => Invariant($"{ids.Count} critical ({string.Join(", ", ids)})"),
        Core.Thresholds.Level.Warn => Invariant($"{ids.Count} warn ({string.Join(", ", ids)})"),
        Core.Thresholds.Level.Ok => Invariant($"{ids.Count} ok"),
        _ => Invariant($"{ids.Count} unknown"),
    };

    private static void AppendVm(StringBuilder text, VmReport vm)
    {
        if (!vm.Available)
        {
            text.AppendLine($"vm: unavailable ({vm.Reason})");
            return;
        }

        text.AppendLine($"memory: {Memory(vm.Memory!)}");
        text.AppendLine($"disk /: {Volume(vm.Disk!)}");
        text.AppendLine($"processes: {Processes(vm.Processes!)}");
        text.AppendLine($"containers: {Containers(vm.Containers!)}");
        text.AppendLine($"unattributed: {Unattributed(vm.Unattributed!)}");
    }

    private static void AppendHost(StringBuilder text, HostReport host)
    {
        if (!host.Available)
        {
            text.AppendLine($"host: unavailable ({host.Reason})");
            return;
        }

        text.AppendLine($"host memory: {HostMemory(host.Memory!)}");
        text.AppendLine($"host system drive: {Volume(host.SystemDrive!)}");
        text.AppendLine($"vmmemWSL working set: {Gib(host.VmmemWorkingSet!)}");
    }

    private static string Memory(MemoryReport m) =>
        m.Available
            ? $"{Gib(m.MemAvailable!)} available of {Gib(m.Total!)} ({Number(m.AvailablePercent!)} %); page cache {Gib(m.PageCache!)}; anon {Gib(m.AnonPages!)} (inactive {Gib(m.InactiveAnon!)}); swap {Gib(m.SwapUsed!)} of {Gib(m.SwapTotal!)}"
            : $"unavailable ({m.Reason})";

    private static string Volume(VolumeReport v) =>
        v.Available ? Invariant($"{v.UsedBytes / BytesPerGibibyte:0.0} GiB used of {v.TotalBytes / BytesPerGibibyte:0.0} GiB ({v.UsedPercent:0.0} %)") : $"unavailable ({v.Reason})";

    private static string Processes(ProcessesReport p) =>
        p.Available
            ? Invariant($"{p.Count} counted, {p.ContainerProcesses} in containers; top: ") + string.Join(", ", p.Top!.Take(TopShown).Select(e => Invariant($"{e.Name} {e.Pid} {e.HeldBytes / BytesPerGibibyte:0.00} GiB")))
            : $"unavailable ({p.Reason})";

    private static string Containers(ContainersReport c) =>
        c.Available ? Invariant($"{c.Count}, {c.AnonShmemTotal / BytesPerGibibyte:0.00} GiB anon+shmem ({c.MemoryCurrentTotal / BytesPerGibibyte:0.00} GiB memory.current)") : $"unavailable ({c.Reason})";

    private static string Unattributed(UnattributedReport u) => u.State switch
    {
        "remainder" => Invariant($"{u.Bytes / BytesPerGibibyte:0.00} GiB"),
        "inconsistentSample" => $"inconsistent sample ({u.Reason})",
        _ => $"unavailable ({u.Reason})",
    };

    private static string HostMemory(HostMemoryReport m) =>
        m.Available ? Invariant($"{m.AvailableBytes / BytesPerGibibyte:0.0} GiB available of {m.TotalBytes / BytesPerGibibyte:0.0} GiB") : $"unavailable ({m.Reason})";

    private static string Starts(Core.Events.StartsWindow? starts) => starts switch
    {
        null => "container starts, last 24 h: not read",
        { Complete: true } s => Invariant($"container starts, last 24 h: {s.Starts} ({s.Testcontainers} Testcontainers)"),
        var s => Invariant($"container starts, last 24 h: {s.Starts}, partial: {string.Join("; ", s.Gaps.Select(g => Invariant($"{g.From.UtcDateTime:MM-dd HH:mm}Z..{g.To.UtcDateTime:MM-dd HH:mm}Z {g.Reason}")))}"),
    };

    private static string Slow(SlowPartReport part) =>
        part.Available ? Invariant($"from run {part.RunId}, {part.AgeSeconds:0} s old") : $"unavailable ({part.Reason})";

    private static string Gib(ByteFigure figure) =>
        figure.Available ? Invariant($"{figure.Bytes / BytesPerGibibyte:0.0} GiB") : "unavailable";

    private static string Number(NumberFigure figure) =>
        figure.Available ? Invariant($"{figure.Value:0.0}") : "?";

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
