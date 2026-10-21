using System.Diagnostics;
using System.Text.Json;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Collect;
using WslCare.Core.Health;
using WslCare.Core.Json;
using WslCare.Core.Status;
using WslCare.Core.Thresholds;
using WslCare.FakeTool;
using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>
/// E15.S10 end to end over the BUILT CLI: <c>collect</c> starts the THIRD fixed interop script through the fake PowerShell — after the
/// disk script, once, under its own ceiling — measures the cores against the ledger's newest line (the same process, the same start
/// marker), appends its own line to <c>{state}/defender-cpu.jsonl</c> and records the section in its detail; <c>status</c> carries it
/// with its run and age and starts no PowerShell. A stall costs only itself. On the Windows binary no script runs.
/// </summary>
public sealed class DefenderFlows
{
    private const string PowerShell = "powershell";

    private static CollectReport Report(ChildResult result) =>
        JsonSerializer.Deserialize(result.Stdout, WslCareJsonContext.Default.CollectReport) ?? throw new InvalidOperationException($"collect printed no report: {result.Stderr}");

    private static bool IsDefenderScript(FakeCall call) => call.Tool == PowerShell && call.Argv.SequenceEqual(DefenderCpuCommands.DefenderCpu.Arguments);

    private static bool IsDiskScript(FakeCall call) => call.Tool == PowerShell && call.Argv.SequenceEqual(WindowsDiskCommands.WindowsDisk.Arguments);

    /// <summary>The measured process (pid 6964, started at the measured instant) read at <paramref name="when"/> with <paramref name="cpuSeconds"/> of CPU in all.</summary>
    private static string Line(DateTimeOffset when, double cpuSeconds) =>
        $"defender={(long)(cpuSeconds * DefenderCpu.TicksPerSecond)}|{when.ToFileTime()}|6964|134365236258808755\n";

    [Fact]
    public async Task Two_collects_measure_the_cores_between_the_two_reads_and_status_carries_the_figure_with_its_run_and_age()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the Defender script runs in the distro: the Linux legs");
        var now = DateTimeOffset.UtcNow;
        string answer = string.Empty;
        using var home = CollectFlows.Captured("collect-defender", first: h =>
        {
            answer = h.WriteFile("defender-now.out", Line(now.AddHours(-4), 99_351));
            h.Answer(new FakeAnswer(PowerShell, DefenderCpuCommands.DefenderCpu.Arguments, 0, answer, string.Empty));
        });

        var first = await home.RunAsync("collect", "--json");
        first.Exit.Should().Be((int)ExitCode.Ok, first.Stderr);
        var baseline = Report(first).Detail!.Defender!;
        baseline.Available.Should().BeTrue(baseline.Reason);
        baseline.CoresAverage!.Available.Should().BeFalse("the first read has no baseline");
        baseline.CoresAverage.Reason.Should().Be(DefenderLedgerRead.NoBaseline);

        File.WriteAllText(answer, Line(now, 99_351 + (0.76 * 4 * 3600)));
        var second = await home.RunAsync("collect", "--json");

        second.Exit.Should().Be((int)ExitCode.Ok, second.Stderr);
        var report = Report(second);
        var defender = report.Detail!.Defender!;
        defender.CoresAverage!.Value.Should().BeApproximately(0.76, 1e-4);
        defender.CoveredHours!.Value.Should().BeApproximately(4, 1e-6);
        defender.Extrapolated.Should().BeTrue();
        report.Detail.Thresholds.Single(v => v.Id == "host.defenderCpu").Level.Should().Be(Level.Ok);
        var powershell = home.Calls.Where(c => c.Tool == PowerShell).ToList();
        powershell.Count(IsDefenderScript).Should().Be(2, "once per full run");
        powershell.FindIndex(IsDefenderScript).Should().BeGreaterThan(powershell.FindIndex(IsDiskScript), "the Defender read runs after the disk script");
        home.Calls.Should().OnlyContain(c => CollectFlows.IsReadCommand(c), "the Defender script is one of the fixed read commands");
        File.ReadAllLines(Path.Combine(home.Paths.StateDirectory, DefenderCpuLedger.FileName)).Should().HaveCount(2, "one line per full run");

        var calls = home.Calls.Count;
        var status = await home.RunAsync("status", "--json");

        status.Exit.Should().Be((int)ExitCode.Ok, status.Stderr);
        var carried = JsonSerializer.Deserialize(status.Stdout, WslCareJsonContext.Default.StatusReport)!.Defender!;
        carried.Available.Should().BeTrue(carried.Reason);
        carried.CoresAverage!.Value.Should().BeApproximately(0.76, 1e-4);
        carried.Basis!.Source.Should().Be(VerdictSource.FullRun);
        carried.Basis.RunId.Should().Be(report.Detail.RunId.Text);
        carried.Basis.AgeSeconds.Should().BeGreaterThanOrEqualTo(0);
        home.Calls.Skip(calls).Should().NotContain(c => c.Tool == PowerShell || c.Tool == "pwsh", "status starts no PowerShell — the Defender script is the full run's only");
    }

    [Fact]
    public async Task A_defender_script_that_overruns_its_ceiling_is_killed_the_disk_facts_stay_and_the_run_still_completes()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the Defender script runs in the distro: the Linux legs");
        using var home = CollectFlows.Captured("collect-defender-timeout", first: h =>
            h.Script(PowerShell, DefenderCpuCommands.DefenderCpu.Arguments, 0, $"health/{DefenderCpuFixture.Name}/{DefenderCpuFixture.AnswerFile}", delayMilliseconds: 60_000));
        Directory.CreateDirectory(Path.GetDirectoryName(home.Paths.MachineConfigFile)!);
        File.WriteAllText(home.Paths.MachineConfigFile, """{ "health": { "defenderCpuTimeoutSeconds": 5 } }""");
        var clock = Stopwatch.StartNew();

        var result = await home.RunAsync("collect", "--json");

        result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(50), "the 60 s hang was cut at the 5 s ceiling, not waited out");
        var report = Report(result);
        report.Recording.Should().Be("recorded", "one failed part never ends a run");
        report.Detail!.Health.WindowsDisk!.Available.Should().BeTrue("the disk script answered before the stall and is untouched by it");
        var defender = report.Detail.Defender!;
        defender.Available.Should().BeFalse();
        defender.Reason.Should().Contain("did not answer within 5 s; its process tree was killed");
        var fake = home.Calls.Single(IsDefenderScript).ProcessId;
        WindowsDiskFlows.ProcessGone(fake).Should().BeTrue("the fake that hung was killed with its tree (pid {0})", fake);
        File.Exists(Path.Combine(home.Paths.StateDirectory, DefenderCpuLedger.FileName)).Should().BeFalse("a read that did not answer is no line");
    }

    [Fact]
    public async Task The_windows_binary_runs_no_defender_script_and_says_the_distro_reads_it()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the Windows binary: the Windows leg");
        using var home = CollectFlows.Captured("collect-defender-windows");

        var result = await home.RunAsync("collect", "--json");

        result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
        var defender = Report(result).Detail!.Defender!;
        defender.Available.Should().BeFalse();
        defender.Reason.Should().Be(HealthCollector.DefenderCpuIsTheDistros);
        home.Calls.Should().NotContain(c => IsDefenderScript(c), "the Windows binary never runs the Defender script");

        var status = await home.RunAsync("status", "--json");
        JsonSerializer.Deserialize(status.Stdout, WslCareJsonContext.Default.StatusReport)!.Defender!.Reason.Should().Be(StatusDefender.ReadByTheDistro);
    }
}
