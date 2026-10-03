using System.Text.Json;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core;
using WslCare.Core.Collect;
using WslCare.Core.Docker;
using WslCare.Core.Files;
using WslCare.Core.Health;
using WslCare.Core.Json;
using WslCare.Core.Records;
using WslCare.Core.Status;
using WslCare.Core.Systemd;
using WslCare.FakeTool;
using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>
/// <c>wsl-care collect [--json]</c> end to end (plan §6, §15b #1/#3/#5): the BUILT CLI over fakes that replay the
/// answers CAPTURED on 2026-10-02 — Docker (<see cref="DockerFixture"/>) and the health tools
/// (<see cref="HealthFixture"/>) — then <c>status</c> and <c>doctor</c> reading back what it recorded.
/// </summary>
public sealed class CollectFlows
{
    internal static ScenarioHome Captured(string purpose, Action<ScenarioHome>? first = null)
    {
        var home = new ScenarioHome(purpose);
        first?.Invoke(home);
        foreach (var (command, file) in DockerFixture.Answers)
        {
            home.Script(DockerCommands.Executable, command.Arguments, 0, $"docker/{DockerFixture.Name}/{file}");
        }

        home.Script(DockerCommands.Executable, DockerCommands.Stats.Arguments, 0, $"docker/{DockerFixture.Name}/stats.out");
        home.Script(SystemdCommands.Journalctl, SystemdCommands.JournalDiskUsage.Arguments, 0, $"docker/{DockerFixture.Name}/journalctl-disk-usage.out");
        foreach (var (argv, file) in HealthFixture.Answers)
        {
            home.Script(Tool(argv[0]), argv.Skip(1).ToList(), 0, $"health/{HealthFixture.Name}/{file}");
        }

        // The journal searches carry an instant: matched by prefix.
        home.Answer(new FakeAnswer(SystemdCommands.Journalctl, ["--since"], 0, ScenarioHome.Fixture($"health/{HealthFixture.Name}/journalctl-search-clock.out"), string.Empty) { Prefix = true });
        return home;
    }

    /// <summary>The fake's name for an executable: <c>powershell.exe</c> records as <c>powershell</c>.</summary>
    private static string Tool(string executable) => Path.GetFileNameWithoutExtension(executable);

    private static CollectReport Report(ChildResult result) =>
        JsonSerializer.Deserialize(result.Stdout, WslCareJsonContext.Default.CollectReport) ?? throw new InvalidOperationException($"collect printed no report: {result.Stderr}");

    [Fact]
    public async Task Collect_records_detail_then_history_and_status_shows_its_slow_parts_with_their_age()
    {
        using var home = Captured("collect");
        Directory.CreateDirectory(Path.GetDirectoryName(home.Paths.UserConfigFile)!);
        File.WriteAllText(home.Paths.UserConfigFile, """{ "volumes": { "anonymousOlderThanDays": 0 } }""");
        if (OperatingSystem.IsLinux())
        {
            ProcfsFixture.CopyTo(home.SandboxRoot);
        }

        var result = await home.RunAsync("collect", "--json");

        result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
        var report = Report(result);
        report.Recording.Should().Be("recorded");
        var files = new PhysicalFileSystem(home.Paths);
        var line = RunHistory.Read(home.Paths, files).Records.Should().ContainSingle().Subject;
        line.Detail.Should().Be(report.DetailFile);
        files.FileExists(RunDetailStore.Absolute(home.Paths, report.DetailFile!)).Should().BeTrue();
        report.Detail!.Docker.Rows.Single(r => r.Id == "A4").Count.Should().Be(3, "the full numbers are the captured Docker's (age limit 0, as the one-time cleanup chose)");
        report.Detail.Health.ClockJumps.Value.Should().Be(home.Paths.Side == Core.Hosting.HostSide.Wsl ? 875 : null);
        report.Detail.Thresholds.Should().Contain(v => v.Id == "wslconfig.memory" && v.Limit.Contains("memory=36GB"));
        home.Calls.Should().OnlyContain(c => IsReadCommand(c), "collect runs read commands only");

        var status = await home.RunAsync("status", "--json");

        status.Exit.Should().Be((int)ExitCode.Ok, status.Stderr);
        var slow = JsonSerializer.Deserialize(status.Stdout, WslCareJsonContext.Default.StatusReport)!.Slow;
        slow.ContainerStats.Should().Match<SlowPartReport>(p => p.Available && p.RunId == report.Detail.RunId.Text && p.AgeSeconds >= 0 && p.Containers!.Count > 0);
        if (home.Paths.Side == Core.Hosting.HostSide.Wsl)
        {
            slow.WindowsClock.Should().Match<SlowPartReport>(p => p.Available && p.RunId == report.Detail.RunId.Text && p.LaunchLatencySeconds > 0.7);
        }
        else
        {
            slow.WindowsClock.Reason.Should().Contain(HealthCollector.WindowsIsTheReference);
        }
    }

    [Fact]
    public async Task An_unwritable_state_directory_makes_collect_measure_print_and_record_nothing()
    {
        using var home = Captured("collect-ro");
        var state = home.Paths.StateDirectory;
        Directory.CreateDirectory(state);
        var logs = home.Paths.LogDirectory;
        Directory.CreateDirectory(logs);
        await using var denial = await AccessDenial.TryDenyAsync(state);
        Assert.SkipWhen(denial is null, "this account cannot be denied access (root, or an elevated Windows account)");
        await using var logDenial = OperatingSystem.IsLinux() ? await AccessDenial.TryDenyAsync(logs) : null;

        var result = await home.RunAsync("collect", "--json");

        result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
        Report(result).Recording.Should().Be("readOnly");
        CliStderr.Of(result).Messages.Should().Contain($"{CommandLine.BinaryName}: {CollectRun.ReadOnlyNote}");
        CliStderr.Of(result).Unexplained.Should().BeEmpty();
        await denial!.DisposeAsync();
        File.Exists(RunHistory.File(home.Paths)).Should().BeFalse("no history line");
        Directory.Exists(RunDetailStore.Root(home.Paths)).Should().BeFalse("no detail");
        File.Exists(new Core.Docker.VolumeSeenStore(home.Paths, new PhysicalFileSystem(home.Paths)).File).Should().BeFalse("no first sighting");
        if (OperatingSystem.IsLinux())
        {
            Directory.EnumerateFiles(home.Paths.UserLogDirectory, "*.log", SearchOption.AllDirectories).Should().NotBeEmpty("an unprivileged run logs to $XDG_STATE_HOME/wsl-care/logs (plan §15b #3)");
        }
    }

    [Fact]
    public async Task Doctor_after_a_collect_finds_the_last_run_recent_and_still_names_what_is_not_installed()
    {
        using var home = Captured("doctor");
        home.Script(DockerCommands.Executable, DockerCommands.Version.Arguments, 0, $"docker/{DockerFixture.Name}/version.out");
        (await home.RunAsync("collect")).Exit.Should().Be((int)ExitCode.Ok);

        var result = await home.RunAsync("doctor", "--json");

        result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
        var report = JsonSerializer.Deserialize(result.Stdout, WslCareJsonContext.Default.DoctorReport)!;
        report.SchemaVersion.Should().Be(SchemaVersion.Current);
        report.Checks.Single(c => c.Id == "lastRun").State.Should().Be(Core.Doctor.DoctorRun.Ok);
        report.Healthy.Should().BeFalse("the follower has never run in this home");
        report.Checks.Single(c => c.Id == "eventsFollower").State.Should().Be(Core.Doctor.DoctorRun.Problem);
        home.Calls.Should().OnlyContain(c => IsReadCommand(c));
    }

    internal static bool IsReadCommand(FakeCall call) => call.Tool switch
    {
        DockerCommands.Executable => DockerCommands.IsReadVerb(call.Argv),
        SystemdCommands.Systemctl or SystemdCommands.Journalctl or SystemdCommands.Timedatectl => SystemdCommands.IsReadVerb(call.Argv),
        "powershell" => call.Argv.SequenceEqual(HealthCommands.WindowsClock.Arguments),
        HealthCommands.Snap => call.Argv.SequenceEqual(HealthCommands.SnapList.Arguments),
        _ => false,
    };
}
