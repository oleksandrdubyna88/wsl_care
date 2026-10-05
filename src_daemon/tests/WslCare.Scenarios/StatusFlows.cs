using System.Diagnostics;
using System.Text.Json;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core;
using WslCare.Core.Json;
using WslCare.Core.Records;
using WslCare.Core.Status;
using WslCare.Core.Thresholds;
using WslCare.FakeTool;
using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>
/// <c>wsl-care status [--json]</c> end to end (plan §6, §15b #5): the BUILT CLI, its <c>WSL_CARE_ROOT</c>
/// holding the captured 2026-10-02 procfs tree on Linux, fake <c>docker</c> and <c>powershell</c> on its
/// <c>PATH</c> that would record any call, and the wall-clock budget of 2 s measured around the process.
/// </summary>
/// <remarks>In <see cref="WallClock"/>, which runs alone: a budget measured while the suite's other tests start
/// their own children measures the suite, not the verb — observed 2026-10-02 on Linux, 2.64 s once
/// <c>PreviewFlows</c> (a 10 s hang flow among them) ran beside it, under 2 s alone.</remarks>
[Collection(WallClock.Name)]
public sealed class StatusFlows
{
    /// <summary>Plan §6: <c>status --json</c> answers in under 2 s.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    private static StatusReport Report(ChildResult result)
    {
        result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
        return JsonSerializer.Deserialize(result.Stdout, WslCareJsonContext.Default.StatusReport)
            ?? throw new InvalidOperationException("status --json printed null");
    }

    private static async Task<(ChildResult Result, TimeSpan Elapsed)> TimedAsync(ScenarioHome home, params string[] args)
    {
        // One unmeasured run first: the first start of a JIT apphost on a cold disk pays for loading the
        // runtime, which is the machine's cost, not the verb's. The budget is then held by the second run.
        await home.RunAsync(args);
        var watch = Stopwatch.StartNew();
        var result = await home.RunAsync(args);
        return (result, watch.Elapsed);
    }

    [Fact]
    public async Task Status_json_over_the_captured_procfs_answers_within_the_budget_and_starts_no_slow_process()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the Linux binary reads the procfs tree; the Windows binary answers for the host, which the next flow covers");
        using var home = new ScenarioHome("status-procfs");
        var links = ProcfsFixture.CopyTo(home.SandboxRoot);
        links.Should().Be(ProcfsFixture.Links.Count, "Linux lets the harness make the cwd symlinks the capture recorded");
        FakeToolProtocol.Tools.Should().Contain(["docker", "powershell"], "the two slow tools of plan §15b #5 are on the PATH and would record a call");

        var (result, elapsed) = await TimedAsync(home, "status", "--json");

        var report = Report(result);
        elapsed.Should().BeLessThan(Budget, "plan §6: status --json is a fast snapshot");
        report.SampleMilliseconds.Should().BeLessThan((long)Budget.TotalMilliseconds);
        report.SchemaVersion.Should().Be(SchemaVersion.Current);
        report.Side.Should().Be("wsl");
        report.Vm.Memory!.Total!.Bytes.Should().Be(47_066_772L * 1024);
        report.Vm.Containers!.Count.Should().Be(10);
        report.Vm.Processes!.Count.Should().Be(51);
        report.Vm.Processes.MntWalkers!.First(p => p.Pid == 560).Cwd.Value.Should().Be(ProcfsFixture.Links["proc/560/cwd"], "the cwd is read through the real symlink");
        report.Vm.Disk!.Available.Should().BeTrue();
        home.Calls.Should().BeEmpty("status launches no docker stats and no powershell.exe Get-Date; FakeToolFlows proves this log fills when a tool IS started");
    }

    [Fact]
    public async Task Status_json_on_this_binarys_side_answers_within_the_budget_names_what_it_cannot_read_and_starts_nothing()
    {
        using var home = new ScenarioHome("status-side");

        var (result, elapsed) = await TimedAsync(home, "status", "--json");

        var report = Report(result);
        elapsed.Should().BeLessThan(Budget);
        report.SchemaVersion.Should().Be(SchemaVersion.Current);
        if (OperatingSystem.IsWindows())
        {
            report.Side.Should().Be("windows");
            report.Vm.Available.Should().BeFalse();
            report.Host.Memory!.Available.Should().BeTrue("GlobalMemoryStatusEx answers on every Windows");
        }
        else
        {
            report.Side.Should().Be("wsl");
            report.Vm.Memory!.Available.Should().BeFalse("the sandbox holds no procfs");
            report.Vm.Memory.Reason.Should().Contain("meminfo");
            using var json = JsonDocument.Parse(result.Stdout);
            json.RootElement.GetProperty("vm").GetProperty("memory").TryGetProperty("total", out _).Should().BeFalse("an unread figure is absent, never 0");
        }

        report.Slow.ContainerStats.Reason.Should().Be(LastFullRun.NoFullRunYet);
        home.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Status_reads_the_slow_parts_back_from_the_last_full_run_with_their_age()
    {
        using var home = new ScenarioHome("status-slow");
        var sampled = DateTimeOffset.UtcNow.AddHours(-2);
        new RunRecordWriter(home.Paths, new Core.Files.PhysicalFileSystem(home.Paths)).Append(
            new RunRecord(SchemaVersion.Current, RunId.New(sampled, 4242), RunTrigger.Timer, sampled, sampled.AddSeconds(30), RunOutcome.Completed, [], RunKind.Collect)
            {
                Slow = new SlowParts { ContainerStats = new ContainerStatsSample(sampled, [new ContainerStat("0e456d1dc8c0", "pg", 712_196_096, 0.4)], string.Empty) },
            });

        var report = Report(await home.RunAsync("status", "--json"));

        report.Slow.ContainerStats.Available.Should().BeTrue();
        report.Slow.ContainerStats.RunId.Should().Be(RunId.New(sampled, 4242).Text);
        report.Slow.ContainerStats.AgeSeconds.Should().BeInRange(2 * 3600 - 5, 2 * 3600 + 120);
        report.Slow.WindowsClock.Available.Should().BeFalse();
        home.Calls.Should().BeEmpty("read from the record, not sampled again");
    }

    [Fact]
    public async Task Status_without_json_prints_text_and_a_stray_argument_is_refused_with_the_usage_code()
    {
        using var home = new ScenarioHome("status-text");

        var text = await home.RunAsync("status");
        var refused = await home.RunAsync("status", "--all");

        text.Exit.Should().Be((int)ExitCode.Ok);
        text.StdoutLines[0].Should().StartWith("wsl-care status (");
        refused.Exit.Should().Be((int)ExitCode.Usage);
        CliStderr.Of(refused).Messages.Should().ContainSingle().Which.Should().Contain("--all");
        home.Calls.Should().BeEmpty();
    }

    private static Verdict VerdictOf(StatusReport report, string id) =>
        (report.Verdicts ?? throw new InvalidOperationException("status --json carried no verdicts")).Single(v => v.Id == id);

    /// <summary>The captured tree with one of <see cref="ProcfsVariants"/>' memory states laid over it.</summary>
    private static ScenarioHome VariantHome(string purpose, Action<string> variant)
    {
        var home = new ScenarioHome(purpose);
        ProcfsFixture.CopyTo(home.SandboxRoot);
        variant(home.SandboxRoot);
        return home;
    }

    [Fact]
    public async Task Status_json_over_the_2026_10_01_evening_is_critical_on_fragmentation_and_warns_on_cache_and_inactive_anon()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the Linux binary reads the procfs tree");
        using var home = VariantHome("status-oct1", ProcfsVariants.October1Evening);

        var report = Report(await home.RunAsync("status", "--json"));

        VerdictOf(report, "memory.fragmentation").Level.Should().Be(Level.Critical, "no free block of 64 KiB or larger in zone Normal");
        VerdictOf(report, "memory.pageCache").Level.Should().Be(Level.Warn, "19 GB of page cache");
        VerdictOf(report, "memory.inactiveAnon").Level.Should().Be(Level.Warn, "21 GB of inactive anonymous memory");
        VerdictOf(report, "memory.available").Should().Match<Verdict>(v => v.Level == Level.Unknown && v.Reason.Contains("MemAvailable"), "the dump did not record it; it is not invented");
        VerdictOf(report, "memory.fragmentation").Basis.Should().Be(new VerdictBasis(VerdictSource.Sample, null, report.SampledAt, 0));
        home.Calls.Should().BeEmpty("the verdicts start no process");
    }

    [Fact]
    public async Task Status_json_over_a_fresh_boot_is_ok_on_every_memory_verdict()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the Linux binary reads the procfs tree");
        using var home = VariantHome("status-boot", ProcfsVariants.FreshBoot);

        var report = Report(await home.RunAsync("status", "--json"));

        (report.Verdicts ?? throw new InvalidOperationException("status --json carried no verdicts"))
            .Where(v => v.Id.StartsWith("memory.", StringComparison.Ordinal) || v.Id == "wslconfig.memory")
            .Should().HaveCount(7).And.OnlyContain(v => v.Level == Level.Ok);
    }

    [Fact]
    public async Task A_threshold_set_in_the_user_layer_moves_the_verdict_status_answers()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the Linux binary reads the procfs tree");
        using var home = new ScenarioHome("status-threshold");
        ProcfsFixture.CopyTo(home.SandboxRoot);

        var shipped = VerdictOf(Report(await home.RunAsync("status", "--json")), "memory.available");
        (await home.RunAsync("config", "set", "thresholds.memAvailableWarnPercent", "70")).Exit.Should().Be((int)ExitCode.Ok);
        var raised = VerdictOf(Report(await home.RunAsync("status", "--json")), "memory.available");

        shipped.Level.Should().Be(Level.Ok, "the captured tree holds 66.8 % available");
        raised.Level.Should().Be(Level.Warn, "66.8 % is below the user's warn threshold of 70 %");
        raised.Limit.Should().Contain("warn < 70 %");
    }

    [Fact]
    public async Task Status_json_names_the_product_version_exactly_as_version_prints_it()
    {
        using var home = new ScenarioHome("status-version");

        var version = (await home.RunAsync("--version")).StdoutLines.Should().ContainSingle().Subject;
        var report = Report(await home.RunAsync("status", "--json"));

        report.ProductVersion.Should().Be(version);
    }
}
