using System.Text.Json;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core;
using WslCare.Core.Collectors;
using WslCare.Core.Files;
using WslCare.Core.Json;
using WslCare.Core.Records;
using WslCare.Core.Status;
using WslCare.TestSupport;

namespace WslCare.Cli.Tests;

/// <summary><c>status [--json]</c> in-process (plan §6): the Linux probe over the captured 2026-10-02 tree,
/// on every OS, with the history and configuration of a sandbox.</summary>
public sealed class StatusCommandTests
{
    private const long KiB = 1024;
    private const long GiB = 1024 * 1024 * KiB;

    /// <summary>The root volume the captured tree is measured on: half full, below <c>thresholds.rootUsedWarnPercent</c> (80).
    /// Fixed, because the probe would otherwise measure the REAL drive holding the fixture — on 2026-10-10 the owner's D: at
    /// 83 % turned <c>disk.root</c> warn and failed a test about the verdict line, for a reason that test does not guard.</summary>
    private static readonly VolumeReadResult.Measured HalfFullRoot = new(100 * GiB, 50 * GiB, 50 * GiB);

    private static (CliHost Host, RecordingCommandRunner Runner) FixtureHost(SandboxHost sandbox, DateTimeOffset now) =>
        FixtureHost(sandbox, now, HalfFullRoot);

    private static (CliHost Host, RecordingCommandRunner Runner) FixtureHost(SandboxHost sandbox, DateTimeOffset now, VolumeReadResult root)
    {
        var paths = ProcfsFixture.PathsAt(ProcfsFixture.Root);
        var clock = new FixedTimeProvider(now);
        var runner = new RecordingCommandRunner();
        var files = new FixedRootVolume(ProcfsFixture.LinkOverlay(new Core.Files.PhysicalFileSystem(paths), ProcfsFixture.Root), paths.FilesystemRoot, root);
        var probe = new LinuxProbe(files, paths, clock);
        return (new CliHost(sandbox.Paths, sandbox.Files, clock, runner) { Probe = probe }, runner);
    }

    /// <summary>Answers ONE path's volume — the distro root's — with a fixed reading, and hands everything else on.</summary>
    private sealed class FixedRootVolume(IFileSystem inner, string root, VolumeReadResult answer) : DelegatingFileSystem(inner)
    {
        public override VolumeReadResult MeasureVolume(string path) =>
            string.Equals(path, root, StringComparison.Ordinal) ? answer : base.MeasureVolume(path);
    }

    private static StatusReport Report(string stdout) =>
        JsonSerializer.Deserialize(stdout, WslCareJsonContext.Default.StatusReport) ?? throw new InvalidOperationException("status --json printed null");

    [Fact]
    public void Status_json_answers_the_fast_snapshot_with_the_schema_version_and_starts_no_process()
    {
        using var sandbox = new SandboxHost("status-json");
        var (host, runner) = FixtureHost(sandbox, ProcfsFixture.CapturedAt);

        var (exit, stdout, stderr) = CliRun.Over(host, "status", "--json");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        var report = Report(stdout);
        report.SchemaVersion.Should().Be(SchemaVersion.Current);
        report.Side.Should().Be("wsl");
        report.Vm.Memory!.Total!.Bytes.Should().Be(47_066_772 * KiB);
        report.Vm.Containers!.Count.Should().Be(10);
        report.Vm.Processes!.Top.Should().HaveCount(30).And.Subject.First().Pid.Should().Be(5949);
        report.Vm.Unattributed!.State.Should().Be("remainder");
        report.Host.Available.Should().BeFalse();
        report.Host.Reason.Should().Be(LinuxProbe.HostIsTheOtherBinary);
        report.Slow.ContainerStats.Reason.Should().Be(LastFullRun.NoFullRunYet);
        runner.Requests.Should().BeEmpty("status is a fast snapshot: no docker stats, no powershell.exe (plan §15b #5)");
    }

    [Fact]
    public void Status_reports_the_slow_parts_of_the_last_full_run_with_their_age()
    {
        using var sandbox = new SandboxHost("status-slow");
        var now = ProcfsFixture.CapturedAt;
        var sampled = now.AddMinutes(-150);
        new RunRecordWriter(sandbox.Paths, sandbox.Files).Append(
            new RunRecord(SchemaVersion.Current, RunId.New(sampled, 77), RunTrigger.Timer, sampled, sampled.AddSeconds(40), RunOutcome.Completed, [], RunKind.Collect)
            {
                Slow = new SlowParts
                {
                    ContainerStats = new ContainerStatsSample(sampled, [new ContainerStat("0e456d1dc8c0", "pg", 712_196_096, 0.4)], string.Empty),
                    WindowsClock = new WindowsClockSample(sampled, 0.8, 0.31, string.Empty),
                },
            });
        var (host, runner) = FixtureHost(sandbox, now);

        var report = Report(CliRun.Over(host, "status", "--json").Stdout);

        report.Slow.ContainerStats.Available.Should().BeTrue();
        report.Slow.ContainerStats.AgeSeconds.Should().Be(150 * 60);
        report.Slow.ContainerStats.RunId.Should().Be(RunId.New(sampled, 77).Text);
        report.Slow.ContainerStats.Containers.Should().ContainSingle().Which.Name.Should().Be("pg");
        report.Slow.WindowsClock.OffsetSeconds.Should().Be(0.8);
        runner.Requests.Should().BeEmpty("the slow parts are READ from the record, not sampled again");
    }

    [Fact]
    public void Status_json_carries_the_verdicts_of_the_sample_and_the_product_version_that_version_prints()
    {
        using var sandbox = new SandboxHost("status-verdicts");
        var (host, runner) = FixtureHost(sandbox, ProcfsFixture.CapturedAt);

        var report = Report(CliRun.Over(host, "status", "--json").Stdout);
        var version = CliRun.Over(host, "--version").Stdout.Trim();

        report.ProductVersion.Should().NotBeNullOrEmpty().And.Be(version, "the same text --version prints (plan §15g B1)");
        var verdicts = report.Verdicts.Should().NotBeNull().And.Subject;
        verdicts.Single(v => v.Id == "memory.available").Should().Match<Core.Thresholds.Verdict>(
            v => v.Level == Core.Thresholds.Level.Ok && v.Value == "66.8 %" && v.Basis!.Source == Core.Thresholds.VerdictSource.Sample,
            "the captured tree holds 66.8 % available, evaluated over this sample");
        verdicts.Single(v => v.Id == "clock.jumps").Should().Match<Core.Thresholds.Verdict>(
            v => v.Level == Core.Thresholds.Level.Unknown && v.Reason == LastFullRun.NoFullRunYet, "only a full run counts clock jumps, and none is recorded");
        runner.Requests.Should().BeEmpty("the verdicts start nothing either");
    }

    [Fact]
    public void A_threshold_set_in_the_user_layer_changes_the_verdict_status_answers()
    {
        using var sandbox = new SandboxHost("status-threshold");
        var (host, _) = FixtureHost(sandbox, ProcfsFixture.CapturedAt);
        CliRun.Over(host, "config", "set", "thresholds.memAvailableWarnPercent", "70").Exit.Should().Be((int)ExitCode.Ok);

        var verdict = Report(CliRun.Over(host, "status", "--json").Stdout).Verdicts!.Single(v => v.Id == "memory.available");

        verdict.Level.Should().Be(Core.Thresholds.Level.Warn, "66.8 % is below a warn threshold of 70 %");
        verdict.Limit.Should().Contain("warn < 70 %");
    }

    [Fact]
    public void Status_without_json_names_the_verdict_levels_in_one_line()
    {
        using var sandbox = new SandboxHost("status-text-verdicts");
        var (host, _) = FixtureHost(sandbox, ProcfsFixture.CapturedAt);
        CliRun.Over(host, "config", "set", "thresholds.memAvailableWarnPercent", "70").Exit.Should().Be((int)ExitCode.Ok);

        var lines = CliRun.Lines(CliRun.Over(host, "status").Stdout);

        var line = lines.Should().ContainSingle(l => l.StartsWith("verdicts: ", StringComparison.Ordinal)).Subject;
        line.Should().Contain("warn (memory.available)").And.Contain(" ok").And.Contain(" unknown");
    }

    [Fact]
    public void Status_without_json_names_every_warn_verdict_in_the_line_a_full_root_disk_too()
    {
        using var sandbox = new SandboxHost("status-text-verdicts-full-root");
        var (host, _) = FixtureHost(sandbox, ProcfsFixture.CapturedAt, new VolumeReadResult.Measured(100 * GiB, 10 * GiB, 10 * GiB));
        CliRun.Over(host, "config", "set", "thresholds.memAvailableWarnPercent", "70").Exit.Should().Be((int)ExitCode.Ok);

        var lines = CliRun.Lines(CliRun.Over(host, "status").Stdout);

        var line = lines.Should().ContainSingle(l => l.StartsWith("verdicts: ", StringComparison.Ordinal)).Subject;
        line.Should().Contain("2 warn (memory.available, disk.root)", "a root volume 90 % used is above the 80 % warn, and the line names every warn verdict");
    }

    [Fact]
    public void Status_without_json_prints_the_summary_lines_a_person_reads()
    {
        using var sandbox = new SandboxHost("status-text");
        var (host, _) = FixtureHost(sandbox, ProcfsFixture.CapturedAt);

        var (exit, stdout, _) = CliRun.Over(host, "status");

        exit.Should().Be((int)ExitCode.Ok);
        var lines = CliRun.Lines(stdout);
        lines[0].Should().StartWith("wsl-care status (wsl), sampled 2026-10-02 13:41:57Z");
        lines.Should().Contain(l => l.StartsWith("memory: 30.0 GiB available of 44.9 GiB (66.8 %)", StringComparison.Ordinal));
        lines.Should().Contain(l => l.StartsWith("processes: 51 counted, 0 in containers; top: MainThread 5949", StringComparison.Ordinal));
        lines.Should().Contain(l => l.StartsWith("containers: 10, ", StringComparison.Ordinal));
        lines.Should().Contain(l => l.StartsWith("unattributed: ", StringComparison.Ordinal) && l.EndsWith(" GiB", StringComparison.Ordinal));
        lines.Should().Contain("host: unavailable (" + LinuxProbe.HostIsTheOtherBinary + ")");
        lines.Should().OnlyContain(l => l.All(c => c < 128), "a Windows console on an OEM code page mangles anything else");
    }

    [Fact]
    public void A_broken_configuration_layer_is_named_in_the_status_answer_and_status_still_answers()
    {
        using var sandbox = new SandboxHost("status-observe");
        sandbox.WriteUserConfig("{ not json");
        var (host, _) = FixtureHost(sandbox, ProcfsFixture.CapturedAt);

        var (exit, stdout, _) = CliRun.Over(host, "status", "--json");

        exit.Should().Be((int)ExitCode.Ok, "plan §15a #1: status and doctor carry configError and still answer");
        var report = Report(stdout);
        report.ObserveOnly.Should().BeTrue();
        report.ConfigError.Should().ContainSingle().Which.File.Should().Be(sandbox.Paths.UserConfigFile);
    }
}
