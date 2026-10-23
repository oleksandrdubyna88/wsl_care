using System.Text.Json;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Collect;
using WslCare.Core.Collectors;
using WslCare.Core.Files;
using WslCare.Core.Health;
using WslCare.Core.Hosting;
using WslCare.Core.Json;
using WslCare.Core.Records;
using WslCare.Core.Status;
using WslCare.Core.Thresholds;
using WslCare.Core.Watch;
using WslCare.TestSupport;

namespace WslCare.Cli.Tests;

/// <summary>
/// E19.S2 through the CLI, in-process, over a Windows layout with a SCRIPTED drive table (nothing of this machine is read): the Windows
/// <c>status</c> judges <c>disk.volumeFree</c> from the watch's <c>volumes.json</c> with the sample's REAL age as its basis, and reads the drives
/// live — saying so — when that sample is missing, unreadable, from a version it does not know, in the future, or older than
/// <c>disk.volumeSampleMaxAgePeriods</c> × <c>mcpWatchdog.periodMinutes</c>. Status never writes the sample.
/// </summary>
public sealed class WindowsVolumeStatusTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private const long Gib = ScriptedVolumes.Gib;

    private readonly SandboxHost _sandbox = new("windows-volume-status");

    public void Dispose() => _sandbox.Dispose();

    private static void OnWindowsOnly() =>
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a CliHost over a Windows layout is built on Windows only");

    private VolumeSampleStore Store => new(_sandbox.Paths, _sandbox.Files);

    private CliHost Host(ScriptedVolumes live) => new(_sandbox.Paths, _sandbox.Files, new FixedTimeProvider(Now), new RecordingCommandRunner())
    {
        Probe = new FakeProbe(HostSide.Windows, new FixedTimeProvider(Now)),
        WindowsVolumes = live,
        Wait = (_, _) => Task.CompletedTask,
    };

    /// <summary>D: 195 GiB with 5 GiB free — critical under the defaults.</summary>
    private static ScriptedVolumes Critical() => ScriptedVolumes.Of(ScriptedVolumes.Volume("C", 953 * Gib, 400 * Gib), ScriptedVolumes.Volume("D", 195 * Gib, 5 * Gib, "ReFS", "Data"));

    /// <summary>Both at 40 % free — ok under the defaults.</summary>
    private static ScriptedVolumes Healthy() => ScriptedVolumes.Of(ScriptedVolumes.Volume("C", 953 * Gib, 400 * Gib), ScriptedVolumes.Volume("D", 195 * Gib, 80 * Gib, "ReFS", "Data"));

    private void WriteSample(DateTimeOffset at, ScriptedVolumes volumes) =>
        Store.TryWrite(VolumeSample.Of(at, volumes.Read().ValueOr([]))).Should().BeEmpty();

    private (StatusReport Report, string Stdout) Status(CliHost host)
    {
        var (exit, stdout, stderr) = CliRun.Over(host, "status", "--json");
        exit.Should().Be((int)ExitCode.Ok, stderr);
        return (JsonSerializer.Deserialize(stdout, WslCareJsonContext.Default.StatusReport)!, stdout);
    }

    private static DiskReport DiskOf(StatusReport report)
    {
        report.Disk.Should().NotBeNull("the Windows status carries a disk section (lettered volumes)");
        return report.Disk!;
    }

    private static Verdict VolumeFree(StatusReport report) => report.Verdicts!.Should().ContainSingle(v => v.Id == "disk.volumeFree", "one disk.volumeFree verdict, never a carried one beside the sampled one").Subject;

    [Fact]
    public void Status_judges_the_sample_with_its_real_age()
    {
        OnWindowsOnly();
        var sampledAt = Now.AddSeconds(-150);
        WriteSample(sampledAt, Critical());
        var live = Healthy();

        var (report, _) = Status(Host(live));

        var verdict = VolumeFree(report);
        verdict.Level.Should().Be(Level.Critical, "the sample's D: has 5 GiB free; the live table (never asked) would say ok");
        verdict.Basis.Should().Be(new VerdictBasis(VerdictSource.Sample, null, sampledAt, 150));
        verdict.Reason.Should().NotContain("read live by status");
        live.Calls.Should().Be(0, "a sample within three periods is trusted: the drives are not read");
        var lettered = DiskOf(report).Lettered!;
        lettered.Should().HaveCount(2);
        lettered.Single(v => v.Letter == "D").Should().Match<LetteredVolumeReport>(v => v.Level == Level.Critical && v.FreeBytes == 5 * Gib);
        DiskOf(report).Available.Should().BeFalse("the VIRTUAL disks stay the distro's");
        DiskOf(report).Reason.Should().Be(StatusDisk.ReadByTheDistro);
        DiskOf(report).Basis.Should().Be(new VerdictBasis(VerdictSource.Sample, null, sampledAt, 150));
    }

    [Theory]
    [InlineData(14, false)]
    [InlineData(15, false)]
    [InlineData(16, true)]
    public void A_sample_older_than_three_periods_is_replaced_by_a_live_read_and_says_so(int ageMinutes, bool replaced)
    {
        OnWindowsOnly();
        WriteSample(Now.AddMinutes(-ageMinutes), Critical());
        var live = Healthy();

        var (report, _) = Status(Host(live));

        var verdict = VolumeFree(report);
        live.Calls.Should().Be(replaced ? 1 : 0, "three watch periods of five minutes is the most a sample may be old: exactly that is still trusted");
        verdict.Level.Should().Be(replaced ? Level.Ok : Level.Critical);
        if (replaced)
        {
            verdict.Basis.Should().Be(new VerdictBasis(VerdictSource.Sample, null, Now, 0), "a live read IS the sample, age 0");
            verdict.Reason.Should().Contain("read live by status (no watch sample within 15 min)");
            verdict.Reason.Should().Contain($"{ageMinutes} min old", "and says why the sample was not used");
        }

        File.Exists(Store.File).Should().BeTrue();
        Store.Read().Should().BeOfType<Reading<VolumeSample>.Available>().Which.Value.SampledAt.Should().Be(Now.AddMinutes(-ageMinutes), "status never rewrites the watch's sample");
    }

    [Fact]
    public void The_window_follows_the_watch_period_and_the_periods_key()
    {
        OnWindowsOnly();
        _sandbox.WriteMachineConfig("""{ "mcpWatchdog": { "periodMinutes": 10 } }""");
        _sandbox.WriteUserConfig("""{ "disk": { "volumeSampleMaxAgePeriods": 2 } }""");
        WriteSample(Now.AddMinutes(-19), Critical());
        var trusted = Healthy();
        _ = Status(Host(trusted));
        WriteSample(Now.AddMinutes(-21), Critical());
        var replaced = Healthy();

        var (report, _) = Status(Host(replaced));

        trusted.Calls.Should().Be(0, "2 periods × 10 min = 20 min: 19 min old is trusted");
        replaced.Calls.Should().Be(1, "21 min old is not");
        VolumeFree(report).Reason.Should().Contain("no watch sample within 20 min");
    }

    [Fact]
    public void A_missing_sample_is_a_live_read_that_says_the_file_does_not_exist()
    {
        OnWindowsOnly();
        var live = Critical();

        var (report, _) = Status(Host(live));

        var verdict = VolumeFree(report);
        verdict.Level.Should().Be(Level.Critical);
        verdict.Reason.Should().Contain("read live by status (no watch sample within 15 min)").And.Contain("does not exist");
        verdict.Basis.Should().Be(new VerdictBasis(VerdictSource.Sample, null, Now, 0));
        DiskOf(report).Lettered.Should().NotBeNull();
        File.Exists(Store.File).Should().BeFalse("status writes no sample");
    }

    [Fact]
    public void An_unreadable_sample_is_a_live_read_that_says_why()
    {
        OnWindowsOnly();
        Directory.CreateDirectory(_sandbox.Paths.StateDirectory);
        File.WriteAllText(Store.File, """{ "schemaVersion": 99, "sampledAt": "2026-10-22T11:59:00+00:00", "volumes": [] }""");
        var live = Healthy();

        var (report, _) = Status(Host(live));

        live.Calls.Should().Be(1);
        VolumeFree(report).Reason.Should().Contain("read live by status").And.Contain("schema 99");
    }

    [Fact]
    public void A_sample_dated_in_the_future_is_not_trusted()
    {
        OnWindowsOnly();
        WriteSample(Now.AddMinutes(10), Critical());
        var live = Healthy();

        var (report, _) = Status(Host(live));

        live.Calls.Should().Be(1, "a clock that moved back would otherwise keep a stale sample young for ever");
        VolumeFree(report).Reason.Should().Contain("in the future");
    }

    [Fact]
    public void A_live_read_that_fails_too_is_unknown_never_ok_and_names_both_reasons()
    {
        OnWindowsOnly();

        var (report, _) = Status(Host(ScriptedVolumes.Unread("the drive list is not available (a test)")));

        var verdict = VolumeFree(report);
        verdict.Level.Should().Be(Level.Unknown);
        verdict.Reason.Should().Contain("does not exist").And.Contain("the drive list is not available (a test)");
        DiskOf(report).Lettered.Should().BeNull("a table that was not read lists no volume — absent is not an empty list");
    }

    [Fact]
    public void A_busy_run_lock_writes_no_sample_and_status_reads_live()
    {
        OnWindowsOnly();
        var watchVolumes = Healthy();
        var held = RunLock.TryTake(_sandbox.Paths, _sandbox.Files).Should().BeOfType<ExclusiveLock.Held>().Subject;
        (int Exit, string Stdout, string Stderr) watch;
        using (held.Handle)
        {
            watch = CliRun.Over(Host(watchVolumes), "watch", "--timer", "--json");
        }

        var live = Critical();
        var (report, _) = Status(Host(live));

        watch.Exit.Should().Be((int)ExitCode.Busy, watch.Stderr);
        watchVolumes.Calls.Should().Be(0);
        File.Exists(Store.File).Should().BeFalse("the busy watch wrote no sample");
        live.Calls.Should().Be(1, "so status read the drives itself");
        VolumeFree(report).Reason.Should().Contain("read live by status");
        VolumeFree(report).Level.Should().Be(Level.Critical);
    }

    /// <summary>The watch's answer names the sample: written (how many), or why not — in the text and in <c>--json</c> — and a failed write never changes the
    /// exit (0: the next period samples again).</summary>
    [Fact]
    public void A_failed_sample_write_is_reported_in_the_text_and_the_json_and_the_exit_stays_0()
    {
        OnWindowsOnly();
        var failing = Host(Healthy()) with { Files = new FailingVolumeWrites(_sandbox.Files, new IOException("the disk is full (a test)")) };

        var text = CliRun.Over(failing, "watch", "--timer");
        var json = CliRun.Over(failing, "watch", "--timer", "--json");
        var written = CliRun.Over(Host(Healthy()), "watch", "--timer");
        var writtenJson = JsonSerializer.Deserialize(CliRun.Over(Host(Healthy()), "watch", "--timer", "--json").Stdout, WslCareJsonContext.Default.WatchReport)!;

        text.Exit.Should().Be((int)ExitCode.Ok, text.Stderr);
        text.Stdout.Should().Contain("volumes not sampled").And.Contain("the disk is full (a test)");
        json.Exit.Should().Be((int)ExitCode.Ok, json.Stderr);
        var report = JsonSerializer.Deserialize(json.Stdout, WslCareJsonContext.Default.WatchReport)!;
        report.Outcome.Should().Be("sampled");
        report.Volumes.Should().Match<VolumeSampleReport>(v => !v.Written && v.Reason!.Contains("the disk is full (a test)", StringComparison.Ordinal));
        written.Stdout.Should().Contain("volumes sampled: 2");
        writtenJson.Volumes.Should().Be(new VolumeSampleReport(true, 2, null));
    }

    [Fact]
    public void The_distro_binary_reads_no_windows_volume()
    {
        using var distro = new LinuxSandbox("distro-no-volumes");
        var probed = CliHost.WindowsVolumesFor(distro.Paths).Read();

        probed.IsAvailable.Should().BeFalse();
        probed.ReasonOrEmpty.Should().Contain("distro");
        var scripted = Healthy();
        var host = new CliHost(distro.Paths, distro.Files, new FixedTimeProvider(Now), new RecordingCommandRunner()) { WindowsVolumes = scripted, Processes = new FakeProcessTable() };

        var (exit, stdout, stderr) = CliRun.Over(host, "status", "--json");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        scripted.Calls.Should().Be(0, "the distro's status carries disk.volumeFree from its full run, never a native read");
        JsonSerializer.Deserialize(stdout, WslCareJsonContext.Default.StatusReport)!.Verdicts!.Single(v => v.Id == "disk.volumeFree").Basis!.Source.Should().Be(VerdictSource.FullRun);
    }

    [Fact]
    public void The_windows_binary_wires_the_real_drive_table_and_no_other_does()
    {
        OnWindowsOnly();

        CliHost.WindowsVolumesFor(_sandbox.Paths).Should().BeOfType<WindowsVolumes>();
    }
}
