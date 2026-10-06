using FluentAssertions;

using WslCare.Core.Config;
using WslCare.Core.Doctor;
using WslCare.Core.Docker;
using WslCare.Core.Events;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Processes;
using WslCare.Core.Records;
using WslCare.Core.Systemd;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Doctor;

/// <summary><c>doctor</c> (plan §6): healthy only when nothing is a problem; each part says what it found; it asks
/// the tools read-only questions and writes nothing.</summary>
public sealed class DoctorTests : IDisposable
{
    private static readonly DateTimeOffset Now = FixedTimeProvider.DefaultNow;
    private readonly TempRoot _root = new("doctor");

    public void Dispose() => _root.Dispose();

    private LinuxHostPaths Paths => new(LinuxEnvironment.Sandboxed(_root.Path));

    private static RecordingCommandRunner Runner(string unitState = "active")
    {
        var runner = new RecordingCommandRunner { Default = new CommandOutcome.FailedToStart("not scripted") }
            .Script(DockerCommands.Version.Argv, 0, DockerFixture.Read("version.out"))
            .Script(SystemdCommands.Version.Argv, 0, HealthFixture.Read("systemctl-version.out"));
        foreach (var unit in DoctorRun.Units)
        {
            runner.Script(SystemdCommands.ShowUnit(unit).Argv, 0, $"Id={unit}\nLoadState=loaded\nActiveState={unitState}\nSubState=running\nUnitFileState=enabled\n");
        }

        return runner;
    }

    private void Installed(DateTimeOffset lastRun, DateTimeOffset covered)
    {
        var paths = Paths;
        var files = new PhysicalFileSystem(paths);
        var id = RunId.New(lastRun, 1);
        RunDetailStore.Write(paths, files, id, """{"schemaVersion":1}"""u8);
        new RunRecordWriter(paths, files).Append(new RunRecord(1, id, RunTrigger.Timer, lastRun, lastRun, RunOutcome.Completed, [], RunKind.Collect) { Detail = RunDetailStore.RelativePath(id) });
        new ContainerStartsStore(paths, files).Append(new CoverageLine.Covered(covered));
        File.SetLastWriteTimeUtc(_root.File("var/log/sysstat/sa02", "x"), Now.AddMinutes(-10).UtcDateTime);
        File.SetLastWriteTimeUtc(_root.File("var/log/atop/atop_20261002", "x"), Now.AddMinutes(-10).UtcDateTime);
        _root.File("proc/sys/kernel/osrelease", "6.18.33.2-microsoft-standard-WSL2\n");
    }

    private Task<DoctorReport> RunAsync(RecordingCommandRunner runner) =>
        new DoctorRun(Paths, new PhysicalFileSystem(Paths), runner, new FixedTimeProvider(Now))
            .RunAsync(ConfigLoader.Load(Paths, new PhysicalFileSystem(Paths)), "0.0.0", CancellationToken.None);

    [Fact]
    public async Task An_installation_doing_its_job_is_healthy_and_names_its_versions()
    {
        Installed(lastRun: Now.AddHours(-1), covered: Now.AddMinutes(-3));

        var report = await RunAsync(Runner());

        report.Checks.Where(c => c.State == DoctorRun.Problem).Should().BeEmpty();
        report.Healthy.Should().BeTrue();
        report.Checks.Single(c => c.Id == "root").State.Should().Be(DoctorRun.NotChecked);
        report.Versions.Select(v => v.Component).Should().Contain(["wsl-care", "docker", "systemd", "kernel"]);
        report.Versions.Single(v => v.Component == "kernel").Version.Should().Be("6.18.33.2-microsoft-standard-WSL2");
    }

    [Fact]
    public async Task A_stale_last_run_a_stopped_unit_and_a_silent_follower_are_each_a_named_problem()
    {
        Installed(lastRun: Now.AddHours(-6), covered: Now.AddHours(-2));

        var report = await RunAsync(Runner(unitState: "inactive"));

        report.Healthy.Should().BeFalse();
        report.Checks.Where(c => c.State == DoctorRun.Problem).Select(c => c.Id).Should().Contain(["lastRun", "unit.wsl-care.timer", "unit.wsl-care-events.service", "eventsFollower"]);
    }

    [Fact]
    public async Task A_fresh_machine_says_nothing_was_ever_recorded_and_writes_nothing()
    {
        var report = await RunAsync(Runner());

        report.Healthy.Should().BeFalse();
        report.Checks.Single(c => c.Id == "stateDirectory").Detail.Should().Contain("does not exist");
        report.Checks.Single(c => c.Id == "lastRun").Detail.Should().Contain("no full run");
        Directory.Exists(Paths.StateDirectory).Should().BeFalse("doctor does not create the state directory");
    }

    [Fact]
    public async Task An_invalid_configuration_layer_is_a_problem_and_doctor_still_answers()
    {
        Installed(lastRun: Now.AddHours(-1), covered: Now.AddMinutes(-3));
        _root.File("home/me/.config/wsl-care/config.json", "{ not json");

        var report = await RunAsync(Runner());

        report.ObserveOnly.Should().BeTrue();
        report.Checks.Single(c => c.Id == "config").State.Should().Be(DoctorRun.Problem);
    }

    /// <summary>E7.S2c: the timer's period is ONE key; a machine layer that changes it after the install leaves the installed
    /// drop-in saying the old period — doctor names that, and what the configuration wants.</summary>
    [Fact]
    public async Task A_timer_drop_in_that_no_longer_matches_the_machine_configuration_is_a_named_problem()
    {
        Installed(lastRun: Now.AddHours(-1), covered: Now.AddMinutes(-3));
        WriteDropIns();
        _root.File("etc/wsl-care/config.json", """{ "timer": { "periodHours": 6 } }""");

        var report = await TunedRunAsync(Runner());

        var check = report.Checks.Single(c => c.Id == "unitConfig");
        check.State.Should().Be(DoctorRun.Problem);
        check.Detail.Should().Contain("wsl-care.timer").And.Contain("OnCalendar=*-*-* 00/6:00:00").And.Contain("run install.sh again");
        report.Healthy.Should().BeFalse();
    }

    [Fact]
    public async Task Drop_ins_that_match_and_none_at_all_under_the_defaults_are_both_fine()
    {
        Installed(lastRun: Now.AddHours(-1), covered: Now.AddMinutes(-3));

        (await TunedRunAsync(Runner())).Checks.Single(c => c.Id == "unitConfig").State.Should().Be(DoctorRun.Ok, "no drop-in, and the defaults change nothing");
        WriteDropIns();
        (await TunedRunAsync(Runner())).Checks.Single(c => c.Id == "unitConfig").State.Should().Be(DoctorRun.Ok);
    }

    /// <summary>What install.sh writes under the defaults.</summary>
    private void WriteDropIns()
    {
        foreach (var unit in UnitDropIns.Units)
        {
            _root.File($"etc/systemd/system/{unit}.d/{UnitDropIns.FileName}", UnitDropIns.Defaults(unit));
        }
    }

    /// <summary>The run as <c>Program.Main</c> makes it: the loaded configuration is the process's tuning.</summary>
    private async Task<DoctorReport> TunedRunAsync(RecordingCommandRunner runner)
    {
        // The machine layer is root's file on Linux: this sandbox's own files stand in for it, as SandboxHost's do.
        var trusted = new PhysicalFileSystem(Paths) { TrustedStateOwner = RegularFiles.EffectiveUid(), OwnersAreThisProcess = true };
        var loaded = ConfigLoader.Load(Paths, trusted);
        loaded.Errors.Should().BeEmpty();
        using (Tuning.Use(loaded.Config))
        {
            return await new DoctorRun(Paths, new PhysicalFileSystem(Paths), runner, new FixedTimeProvider(Now)).RunAsync(loaded, "0.0.0", CancellationToken.None);
        }
    }

    [Fact]
    public async Task Doctor_asks_the_tools_read_only_questions()
    {
        var runner = Runner();

        await RunAsync(runner);

        runner.Requests.Select(r => r.Argv).Should().OnlyContain(a =>
            a[0] == "docker" ? DockerCommands.IsReadVerb(a.Skip(1).ToList()) : SystemdCommands.IsReadVerb(a.Skip(1).ToList()));
    }
}
