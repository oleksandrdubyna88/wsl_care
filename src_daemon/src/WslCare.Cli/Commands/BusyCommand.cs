using System.Globalization;
using System.Text.Json;

using WslCare.Core;
using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Config;
using WslCare.Core.Hosting;
using WslCare.Core.Json;
using WslCare.Core.Status;
using WslCare.Core.Thresholds;

namespace WslCare.Cli.Commands;

/// <summary>
/// <c>busy [--json]</c> (E14 S6): is the machine too busy to START heavy work now? It reads <c>/proc/pressure/{cpu,io,memory}</c>
/// and <c>/proc/loadavg</c> and nothing else — no process walk, no MCP window, no history, no write but its own run log (as every verb); any user — and asks the ONE
/// rule (<see cref="MachineBusy.Judge"/>). Exit 0 for calm or unknown (go: an agent never waits on a kernel that cannot answer),
/// <see cref="ExitCode.MachineBusy"/> for busy (wait with a bounded backoff, then go). Advice only: nothing is started or stopped.
/// </summary>
internal static class BusyCommand
{
    public static int Run(Request.Busy request, CliHost host, ConfigLoadResult loaded, TextWriter stdout)
    {
        var (pressure, load) = Read(host);
        var judgement = MachineBusy.Judge(pressure, BusyLimits.From(loaded.Config));
        var report = BusyReport.From(judgement, pressure, load, host.Clock.GetUtcNow());
        Output.Answer(stdout, request.Json ? JsonSerializer.Serialize(report, WslCareJsonContext.Default.BusyReport) : Render(report));
        return judgement.State == BusyState.Busy ? (int)ExitCode.MachineBusy : (int)ExitCode.Ok;
    }

    private static (PressureSet Pressure, Reading<LoadAverages> Load) Read(CliHost host) => host.Paths switch
    {
        LinuxHostPaths linux => (PressureFile.ReadSet(host.Files, linux), LoadAverageFile.Read(host.Files, linux)),
        _ => (NoPressure(), Reading.Missing<LoadAverages>(WindowsHasNoProc)),
    };

    private const string WindowsHasNoProc = "the Windows binary has no /proc: ask the distro's wsl-care";

    private static PressureSet NoPressure() =>
        new(Reading.Missing<Pressure>(WindowsHasNoProc), Reading.Missing<Pressure>(WindowsHasNoProc), Reading.Missing<Pressure>(WindowsHasNoProc));

    /// <summary>One line: the state, every pressure read, the 1-minute load, and why when it is not calm.</summary>
    private static string Render(BusyReport report)
    {
        var read = new[] { ("cpu", report.Pressure.Cpu), ("io", report.Pressure.Io), ("memory", report.Pressure.Memory) }
            .Where(p => p.Item2.Available && p.Item2.Some is not null)
            .Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Item1} {p.Item2.Some!.Avg60:0.##}"));
        var load = report.Load.Available ? string.Create(CultureInfo.InvariantCulture, $"; load {report.Load.One:0.##}") : string.Empty;
        var why = report.Reasons.Select(r => string.Create(CultureInfo.InvariantCulture, $"{r.Resource} {r.Window} {r.Value:0.##} > {r.Limit:0.##} ({r.Key})")).Concat(report.Unread);
        var tail = report.State == "calm" ? string.Empty : $" — {string.Join("; ", why)}";
        var figures = read.ToList() is { Count: > 0 } named ? $"PSI some avg60 {string.Join(", ", named)}" : "PSI not read";
        return CommandLine.Printable($"{report.State}: {figures}{load}{tail}");
    }
}
