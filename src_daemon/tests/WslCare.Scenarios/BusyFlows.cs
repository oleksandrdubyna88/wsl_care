using System.Text.Json;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Json;
using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>
/// E14 S6 over the BUILT CLI: <c>wsl-care busy --json</c>. In the distro (the Linux legs) a sandboxed <c>/proc/pressure</c> with
/// cpu at the 2026-10-07 evening's level answers busy, exit 83, naming the cpu key; the Windows binary has no <c>/proc</c> and
/// answers unknown, exit 0 — an agent never waits on a signal that cannot answer.
/// </summary>
public sealed class BusyFlows
{
    [Fact]
    public async Task Busy_json_answers_83_naming_cpu_in_the_distro_and_unknown_0_on_windows()
    {
        using var home = new ScenarioHome("busy");
        var proc = Path.Combine(home.SandboxRoot, "proc");
        Directory.CreateDirectory(Path.Combine(proc, "pressure"));
        await File.WriteAllTextAsync(Path.Combine(proc, "pressure", "cpu"), "some avg10=31.00 avg60=28.50 avg300=12.00 total=1000\nfull avg10=0.00 avg60=0.00 avg300=0.00 total=0\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(proc, "pressure", "io"), "some avg10=5.00 avg60=1.20 avg300=1.00 total=1000\nfull avg10=0.00 avg60=0.00 avg300=0.00 total=0\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(proc, "pressure", "memory"), "some avg10=0.00 avg60=0.00 avg300=0.00 total=0\nfull avg10=0.00 avg60=0.00 avg300=0.00 total=0\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(proc, "loadavg"), "36.10 30.02 22.50 3/2101 99999\n", TestContext.Current.CancellationToken);

        var result = await home.RunAsync("busy", "--json");

        var report = JsonSerializer.Deserialize(result.Stdout, WslCareJsonContext.Default.BusyReport)!;
        if (OperatingSystem.IsLinux())
        {
            result.Exit.Should().Be((int)ExitCode.MachineBusy, result.Stderr);
            report.State.Should().Be("busy");
            report.Reasons.Should().ContainSingle().Which.Key.Should().Be("thresholds.cpuPressureWarnPercent");
            report.Load.One.Should().Be(36.10);
        }
        else
        {
            result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
            report.State.Should().Be("unknown");
            report.Unread.Should().HaveCount(3);
        }

        home.Calls.Should().BeEmpty("busy starts no process");
    }
}
