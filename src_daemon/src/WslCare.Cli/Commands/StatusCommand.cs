using System.Globalization;
using System.Text;
using System.Text.Json;

using WslCare.Core.Collect;
using WslCare.Core.Config;
using WslCare.Core.Events;
using WslCare.Core.Json;
using WslCare.Core.Records;
using WslCare.Core.Status;

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
        var last = LastFullRun.Read(host.Paths, host.Files, host.Clock);
        var report = StatusReports.From(sample, last, loaded) with
        {
            ContainerStarts = Coverage.Last24h(new ContainerStartsStore(host.Paths, host.Files).ReadAll(), host.Clock.GetUtcNow()),
            Folders = FoldersReports.From(last.Folders, last.PreviousFolders, measuredThisRun: false),
        };
        return Output.Answer(stdout, request.Json ? JsonSerializer.Serialize(report, WslCareJsonContext.Default.StatusReport) : StatusText.Render(report));
    }
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
        text.Append(Starts(report.ContainerStarts));
        return text.ToString();
    }

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
