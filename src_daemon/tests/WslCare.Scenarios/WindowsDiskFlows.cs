using System.Diagnostics;
using System.Text.Json;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Collect;
using WslCare.Core.Health;
using WslCare.Core.Json;
using WslCare.Core.Records;
using WslCare.FakeTool;
using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>
/// E15.S1 end to end over the BUILT CLI: <c>collect</c> starts the second fixed interop script through the fake PowerShell —
/// after the clock probe, once, under its own ceiling — and the run's detail records what it said (the answer captured on
/// 2026-10-10, <see cref="WindowsDiskFixture"/>); <c>status</c> starts no PowerShell at all. On the Windows binary no script runs
/// and the detail says the distro reads these facts.
/// </summary>
public sealed class WindowsDiskFlows
{
    private const string PowerShell = "powershell";

    private static CollectReport Report(ChildResult result) =>
        JsonSerializer.Deserialize(result.Stdout, WslCareJsonContext.Default.CollectReport) ?? throw new InvalidOperationException($"collect printed no report: {result.Stderr}");

    private static bool IsDiskScript(FakeCall call) => call.Tool == PowerShell && call.Argv.SequenceEqual(WindowsDiskCommands.WindowsDisk.Arguments);

    private static bool IsClockProbe(FakeCall call) => call.Tool == PowerShell && call.Argv.SequenceEqual(HealthCommands.WindowsClock.Arguments);

    [Fact]
    public async Task Collect_reads_the_windows_disk_facts_through_the_second_fixed_script_and_the_detail_records_them()
    {
        using var home = CollectFlows.Captured("collect-windows-disk", first: h =>
            h.Script(PowerShell, WindowsDiskCommands.WindowsDisk.Arguments, 0, $"health/{WindowsDiskFixture.Name}/{WindowsDiskFixture.AnswerFile}"));

        var result = await home.RunAsync("collect", "--json");

        result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
        var disk = Report(result).Detail!.Health.WindowsDisk;
        disk.Should().NotBeNull("every full run's detail carries the sample, measured or not");
        home.Calls.Should().OnlyContain(c => CollectFlows.IsReadCommand(c), "the disk script is one of the fixed read commands");
        if (home.Paths.Side == Core.Hosting.HostSide.Wsl)
        {
            disk!.Available.Should().BeTrue(disk.Reason);
            disk.MalformedLines.Should().Be(0);
            disk.Distros!.Disks!.Select(d => d.Name).Should().Equal("Ubuntu", "docker-desktop", "Ubuntu-26.04");
            disk.DockerDataDisk!.Disk!.Size.Bytes.Should().Be(132_338_679_808);
            disk.Volumes!.Volumes!.Select(v => v.Letter).Should().Equal("E", "D", "C", "F");
            disk.Volumes.Volumes!.Single(v => v.Letter == "D").DiskNumber.Value.Should().Be(0);
            disk.Reliability!.Available.Should().BeFalse();
            disk.Reliability.Reason.Should().Contain("elevation");
            disk.Events!.Read.Should().Be(63);
            disk.Events.Groups!.Should().OnlyContain(g => g.Disk.Value == 1 && g.Provider == "disk" && g.Id == 11);
            disk.Events.GroupsCut.Should().BeFalse("the capture's four groups are far under disk.eventGroupsMax");
            disk.Events.GroupsTotal.Should().Be(disk.Events.Groups!.Count);
            disk.CommitBytes!.Bytes.Should().Be(110_976_245_760);
            disk.Identity!.MachineGuid.Value.Should().Be("00000000-0000-4000-8000-000000000002");
            disk.Adapters!.Adapters!.Select(a => a.ProblemCode.Value).Should().Equal(0, 22);
            var powershell = home.Calls.Where(c => c.Tool == PowerShell).ToList();
            powershell.Count(IsDiskScript).Should().Be(1, "once per full run");
            powershell.FindIndex(IsDiskScript).Should().BeGreaterThan(powershell.FindIndex(IsClockProbe), "after the clock probe");
            var detailText = File.ReadAllText(RunDetailStore.Absolute(home.Paths, Report(result).DetailFile!));
            detailText.Should().Contain("\"windowsDisk\"", "the detail ON DISK records the sample, for the later stories and the statistics");
        }
        else
        {
            disk!.Available.Should().BeFalse();
            disk.Reason.Should().Be(HealthCollector.WindowsDiskIsTheDistros);
            home.Calls.Should().NotContain(c => IsDiskScript(c), "the Windows binary never runs the disk script");
        }

        var calls = home.Calls.Count;
        var status = await home.RunAsync("status", "--json");

        status.Exit.Should().Be((int)ExitCode.Ok, status.Stderr);
        home.Calls.Skip(calls).Should().NotContain(c => c.Tool == PowerShell, "status starts no PowerShell — the disk script is the full run's only");
    }

    [Fact]
    public async Task A_disk_script_that_overruns_its_ceiling_is_killed_and_the_run_records_why_and_still_completes()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the disk script runs in the distro: the Linux legs");
        using var home = CollectFlows.Captured("collect-windows-disk-timeout", first: h =>
            h.Script(PowerShell, WindowsDiskCommands.WindowsDisk.Arguments, 0, $"health/{WindowsDiskFixture.Name}/{WindowsDiskFixture.AnswerFile}", delayMilliseconds: 60_000));
        Directory.CreateDirectory(Path.GetDirectoryName(home.Paths.MachineConfigFile)!);
        File.WriteAllText(home.Paths.MachineConfigFile, """{ "health": { "windowsDiskTimeoutSeconds": 5 } }""");
        var clock = Stopwatch.StartNew();

        var result = await home.RunAsync("collect", "--json");

        result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(50), "the 60 s hang was cut at the 5 s ceiling, not waited out");
        var report = Report(result);
        report.Recording.Should().Be("recorded", "one failed part never ends a run");
        var disk = report.Detail!.Health.WindowsDisk!;
        disk.Available.Should().BeFalse();
        disk.Reason.Should().Contain("did not answer within 5 s; its process tree was killed");
        var fake = home.Calls.Single(IsDiskScript).ProcessId;
        ProcessGone(fake).Should().BeTrue("the fake that hung was killed with its tree (pid {0})", fake);
    }

    private static bool ProcessGone(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}
