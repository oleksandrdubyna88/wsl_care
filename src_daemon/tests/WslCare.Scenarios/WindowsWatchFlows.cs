using System.Text.Json;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Json;
using WslCare.Core.Mcp;
using WslCare.Core.Records;
using WslCare.Core.Watch;

namespace WslCare.Scenarios;

/// <summary>
/// WR-D4 of PLAN_windows_runner.md §3.2 over the BUILT Windows binary, in a <see cref="ScenarioHome"/> (every path under
/// <c>WSL_CARE_ROOT</c>): <c>watch --timer --json</c> samples the Windows MCP servers into the SANDBOXED ledger and writes no history
/// line — A22 is not built, so the timer branch acts on nothing and the dry-run week is not started. The servers it counts are this
/// test runner itself (the sandbox's user layer lists its program name), so one reading exists on any machine; the process table is
/// only read. An elevated process — here the sandbox's own privilege claim — is refused with exit 81 before anything is written.
/// </summary>
public sealed class WindowsWatchFlows
{
    /// <summary>This test runner's program name (<c>WslCare.Scenarios</c> when run as its executable).</summary>
    private static string Runner => Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? string.Empty);

    /// <summary>The Windows legs, run as the test executable — or skipped, naming why.</summary>
    internal static void OnWindowsAsTheTestExecutable()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the Windows binary's watch: the Windows legs");
        Assert.SkipWhen(Runner is "" or "dotnet", "the flow counts this runner's own process as the MCP server; run the test executable");
    }

    /// <summary>The same, unelevated: skipped when the runner is elevated (CI's Windows runner is), because an elevated test run
    /// starts an elevated CLI, which the Windows watch refuses (81) — what <see cref="An_elevated_windows_watch_is_refused_with_81_and_writes_nothing"/>
    /// and <c>WindowsTasksRunFlows</c>' elevated leg prove there instead.</summary>
    internal static void OnTheWindowsLegs()
    {
        OnWindowsAsTheTestExecutable();
        Assert.SkipWhen(Environment.IsPrivilegedProcess, "skipped when the runner is elevated (CI): an elevated CLI's Windows watch is refused with 81, which the elevated flows prove");
    }

    /// <summary>A sandbox whose user layer has the Windows watch count this test runner as an MCP server.</summary>
    internal static ScenarioHome Home(string purpose)
    {
        var home = new ScenarioHome(purpose);
        Directory.CreateDirectory(Path.GetDirectoryName(home.Paths.UserConfigFile)!);
        File.WriteAllText(home.Paths.UserConfigFile, $$"""{ "mcpServers": { "programs": ["{{Runner}}"] } }""");
        return home;
    }

    /// <summary>The sandbox's ledger, read as the Windows binary reads it.</summary>
    internal static McpCpuFile Ledger(ScenarioHome home)
    {
        var file = home.Paths.Rules.Join(home.Paths.StateDirectory, McpCpuLedger.FileName);
        return JsonSerializer.Deserialize(File.ReadAllBytes(file), WslCareJsonContext.Compact.McpCpuFile)!;
    }

    /// <summary>What every sampling-only Windows watch leaves: the ledger with this runner's reading, no history line, no detail, no
    /// dry-run stamp.</summary>
    internal static void SampledOnly(ScenarioHome home, WatchReport report)
    {
        report.Outcome.Should().Be("sampled", report.Reason);
        report.Reason.Should().Be(WatchRun.NoWindowsAct);
        report.LedgerRecorded.Should().BeTrue(report.LedgerReason);
        report.Act.Should().BeNull();
        Ledger(home).Entries.Select(e => e.Pid).Should().Contain(Environment.ProcessId, "this runner is the one server the sandbox lists");
        File.Exists(RunHistory.File(home.Paths)).Should().BeFalse("until A22 the Windows watch writes no history line");
        Directory.Exists(RunDetailStore.Root(home.Paths)).Should().BeFalse();
        File.Exists(DryRunWindow.File(home.Paths)).Should().BeFalse("a watch with nothing to act on never starts the dry-run week");
    }

    [Fact]
    public async Task The_built_windows_binary_s_watch_samples_into_the_sandboxed_ledger_and_writes_no_history_line()
    {
        OnTheWindowsLegs();
        using var home = Home("windows-watch");

        var result = await home.RunAsync("watch", "--timer", "--json");

        result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
        SampledOnly(home, JsonSerializer.Deserialize(result.Stdout, WslCareJsonContext.Default.WatchReport)!);
        home.Calls.Should().BeEmpty("the watch starts no process");
    }

    private static string VolumesFile(ScenarioHome home) => home.Paths.Rules.Join(home.Paths.StateDirectory, "volumes.json");

    /// <summary>A <c>volumes.json</c> as the watch writes it, by hand: D: of 195 GiB with <paramref name="freeGib"/> free, sampled <paramref name="minutesAgo"/> ago.</summary>
    private static void PlantSample(ScenarioHome home, int minutesAgo, int freeGib)
    {
        Directory.CreateDirectory(home.Paths.StateDirectory);
        var at = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var size = 195L * (1L << 30);
        var free = freeGib * (1L << 30);
        File.WriteAllText(VolumesFile(home), $$"""
            { "schemaVersion": 1, "sampledAt": "{{at}}", "volumes": [ { "letter": "D", "fileSystem": "ReFS", "label": "Data", "size": { "available": true, "bytes": {{size}} }, "free": { "available": true, "bytes": {{free}} } } ] }
            """);
    }

    private static Core.Status.StatusReport StatusOf(WslCare.TestSupport.ChildResult result) =>
        JsonSerializer.Deserialize(result.Stdout, WslCareJsonContext.Default.StatusReport)!;

    /// <summary>E19.S2 over the BUILT Windows binary: the watch takes the native volume reading into <c>volumes.json</c> and starts no process for it. This
    /// is the one flow that reads THIS machine's drives, so it asserts shape alone: the system drive is in the file, with figures that fit.</summary>
    [Fact]
    public async Task The_windows_watch_writes_the_volume_sample_without_a_process()
    {
        OnTheWindowsLegs();
        using var home = Home("windows-watch-volumes");

        var result = await home.RunAsync("watch", "--timer", "--json");

        result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
        var report = JsonSerializer.Deserialize(result.Stdout, WslCareJsonContext.Default.WatchReport)!;
        report.Volumes.Should().NotBeNull().And.Match<VolumeSampleReport>(v => v.Written && v.Volumes >= 1, report.Volumes?.Reason);
        var sample = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(VolumesFile(home)))!.AsObject();
        sample["schemaVersion"]!.GetValue<int>().Should().Be(1);
        DateTimeOffset.Parse(sample["sampledAt"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture).Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
        sample["volumes"]!.AsArray().Select(v => v!["letter"]!.GetValue<string>()).Should().Contain("C");
        home.Calls.Should().BeEmpty("the watch starts no process");
    }

    /// <summary>The sample is judged with its real age: a status two minutes after the watch says <c>critical</c> for D: and <c>ageSeconds</c> about 120, from the
    /// file — the live table (this machine's) is not what it judged.</summary>
    [Fact]
    public async Task Status_judges_the_sample_with_its_real_age()
    {
        OnWindowsAsTheTestExecutable();
        using var home = Home("windows-status-sample");
        PlantSample(home, minutesAgo: 2, freeGib: 5);

        var result = await home.RunAsync("status", "--json");

        result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
        var report = StatusOf(result);
        var verdict = report.Verdicts!.Single(v => v.Id == "disk.volumeFree");
        verdict.Level.Should().Be(Core.Thresholds.Level.Critical);
        verdict.Basis!.Source.Should().Be(Core.Thresholds.VerdictSource.Sample);
        verdict.Basis.AgeSeconds.Should().BeInRange(100, 400, "two minutes old at the planting, a few seconds of process start on top");
        report.Disk!.Lettered.Should().ContainSingle().Which.Letter.Should().Be("D");
        File.ReadAllText(VolumesFile(home)).Should().Contain("\"letter\": \"D\"", "status leaves the watch's sample alone");
    }

    /// <summary>A sample older than three watch periods is not trusted: status reads the drives itself (this machine's — shape only) and says so.</summary>
    [Fact]
    public async Task A_sample_older_than_three_periods_is_replaced_by_a_live_read_and_says_so()
    {
        OnWindowsAsTheTestExecutable();
        using var home = Home("windows-status-old-sample");
        PlantSample(home, minutesAgo: 30, freeGib: 5);

        var result = await home.RunAsync("status", "--json");

        result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
        var report = StatusOf(result);
        var verdict = report.Verdicts!.Single(v => v.Id == "disk.volumeFree");
        verdict.Reason.Should().Contain("read live by status (no watch sample within 15 min)");
        verdict.Basis!.Source.Should().Be(Core.Thresholds.VerdictSource.Sample);
        verdict.Basis.AgeSeconds.Should().Be(0);
        report.Disk!.Lettered.Should().Contain(v => v.Letter == "C", "this machine's system drive, read natively");
        home.Calls.Should().BeEmpty("a live read starts no process either");
    }

    [Fact]
    public async Task An_elevated_windows_watch_is_refused_with_81_and_writes_nothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the Windows binary's watch: the Windows legs");
        using var home = Home("windows-watch-elevated");
        home.ClaimsRoot = true;

        var result = await home.RunAsync("watch", "--timer", "--json");

        result.Exit.Should().Be((int)ExitCode.NotAsRoot, result.Stderr);
        result.Stderr.Should().Contain("NOT elevated");
        result.Stdout.Should().BeEmpty();
        File.Exists(home.Paths.Rules.Join(home.Paths.StateDirectory, McpCpuLedger.FileName)).Should().BeFalse("refused before the lock or any state");
    }
}
