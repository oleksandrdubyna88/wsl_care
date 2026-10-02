using System.Diagnostics;
using System.Text.Json;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core;
using WslCare.Core.Collectors;
using WslCare.Core.Docker;
using WslCare.Core.Json;
using WslCare.Core.Preview;
using WslCare.FakeTool;
using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>
/// <c>wsl-care preview --all [--json]</c> end to end (plan §6, §15b #2/#3/#7): the BUILT CLI with a fake
/// <c>docker</c> on its PATH that replays the answers the live contract CAPTURED from this machine on 2026-10-02
/// (<see cref="DockerFixture"/>) — for exactly the argv the product sends, built by the product's own
/// <see cref="DockerCommands"/> — and each way Docker fails to answer.
/// </summary>
public sealed class PreviewFlows
{
    /// <summary>Every age limit at 0: what a button pressed "for everything unused" would take — the one-time
    /// cleanup's own choice — and the only setting whose rows do not move as the fixture ages.</summary>
    private const string AllAges = """{ "volumes": { "anonymousOlderThanDays": 0 }, "containers": { "stoppedOlderThanDays": 0, "testcontainersOlderThanHours": 0 }, "images": { "unusedOlderThanDays": 0 }, "buildCache": { "olderThanDays": 0 } }""";

    private static ScenarioHome Captured(string purpose)
    {
        var home = new ScenarioHome(purpose);
        foreach (var (command, file) in DockerFixture.Answers)
        {
            home.Script(DockerCommands.Executable, command.Arguments, 0, $"docker/{DockerFixture.Name}/{file}");
        }

        return home;
    }

    private static PreviewReport Report(ChildResult result)
    {
        result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
        return JsonSerializer.Deserialize(result.Stdout, WslCareJsonContext.Default.PreviewReport) ?? throw new InvalidOperationException("preview printed null");
    }

    private static PreviewRowReport Row(PreviewReport report, string id) => report.Rows.Single(r => r.Id == id);

    private static void WriteUserLayer(ScenarioHome home, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(home.Paths.UserConfigFile)!);
        File.WriteAllText(home.Paths.UserConfigFile, json);
    }

    [Fact]
    public async Task Preview_over_the_captured_docker_at_limit_zero_reproduces_its_rows_and_starts_docker_read_verbs_only()
    {
        using var home = Captured("preview-captured");
        WriteUserLayer(home, AllAges);
        var totals = DockerTotal.Parse(DockerFixture.Read("system-df.out")).ValueOr([]).ToDictionary(t => t.Type);

        var report = Report(await home.RunAsync("preview", "--all", "--json"));

        report.SchemaVersion.Should().Be(SchemaVersion.Current);
        report.Docker.Available.Should().BeTrue();
        Row(report, "A4").Should().BeEquivalentTo(new { Count = 3, ReclaimableBytes = 641_400_000L, Available = true });
        Row(report, "A5").Count.Should().Be(13);
        Row(report, "A5Testcontainers").Count.Should().Be(0);
        Row(report, "A6").Count.Should().Be(0);
        Row(report, "A6Unused").Count.Should().Be(10);
        Row(report, "A6Unused").ReclaimableBytes!.Value.Should().BeCloseTo(totals[DockerTotal.Images].ReclaimableBytes.ValueOr(-1), 5_000_000, "Docker's own images reclaimable");
        Row(report, "A7").ReclaimableBytes!.Value.Should().BeCloseTo(totals[DockerTotal.BuildCache].ReclaimableBytes.ValueOr(-1), 100_000, "Docker's own build-cache reclaimable");
        (Row(report, "A4").ReclaimableBytes!.Value + report.Kept.Bytes!.Value).Should().BeCloseTo(totals[DockerTotal.Volumes].ReclaimableBytes.ValueOr(-1), 50_000_000, "A4 + the kept named volumes = Docker's volumes reclaimable");
        report.Kept.Count.Should().Be(13);
        report.Totals.Types!.Single(t => t.Type == DockerTotal.Containers).TotalCount.Should().Be(29);
        report.Hygiene.UnboundedLogs.Count.Should().Be(28);
        report.VolumeSeen.Recorded.Should().BeTrue("the sandbox's state directory is writable by its owner — the privileged case");
        File.Exists(new VolumeSeenStore(home.Paths, new Core.Files.PhysicalFileSystem(home.Paths)).File).Should().BeTrue();

        home.Calls.Should().OnlyContain(c => c.Tool == DockerCommands.Executable && DockerCommands.IsReadVerb(c.Argv), "preview runs Docker READ commands only");
        home.Calls.Select(c => c.Argv).Should().Equal(DockerFixture.Answers.Select(a => a.Command.Arguments), (a, b) => a.SequenceEqual(b));
    }

    [Fact]
    public async Task At_the_shipped_limits_a_volume_first_seen_now_is_left_and_one_first_seen_two_days_ago_is_counted()
    {
        using var fresh = Captured("preview-fresh");
        using var seenBefore = Captured("preview-seen");
        var names = DanglingVolumes.Parse(DockerFixture.Read("volume-ls-dangling.out")).Where(DockerJson.IsFullId).ToList();
        var twoDaysAgo = DateTimeOffset.UtcNow.AddDays(-2);
        var store = new VolumeSeenStore(seenBefore.Paths, new Core.Files.PhysicalFileSystem(seenBefore.Paths));
        store.TryWrite(new VolumeSeenRecord(SchemaVersion.Current, twoDaysAgo, [.. names.Select(n => new VolumeSighting(n, twoDaysAgo))])).Should().BeOfType<VolumeSeenWrite.Written>();

        var first = Row(Report(await fresh.RunAsync("preview", "--all", "--json")), "A4");
        var aged = Row(Report(await seenBefore.RunAsync("preview", "--all", "--json")), "A4");

        first.Count.Should().Be(0, "a first sighting is now, and volumes.anonymousOlderThanDays is 1");
        first.Notes![0].Count.Should().Be(3);
        aged.Count.Should().Be(3);
        aged.ReclaimableBytes.Should().Be(641_400_000L);
        store.Read().Record.FirstSeen(names[0]).Should().Be(Reading.Of(twoDaysAgo), "the first sighting survives the next look");
    }

    [Fact]
    public async Task Without_docker_on_the_path_every_docker_figure_is_unavailable_as_not_installed_and_never_zero()
    {
        using var home = new ScenarioHome("preview-no-docker", [.. FakeToolProtocol.Tools.Where(t => t != DockerCommands.Executable)]);

        var result = await home.RunAsync("preview", "--all", "--json");

        var report = Report(result);
        report.Docker.Should().BeEquivalentTo(new { Available = false, Kind = "notInstalled" });
        report.Rows.Should().OnlyContain(r => !r.Available && r.Reason!.Length > 0);
        using var json = JsonDocument.Parse(result.Stdout);
        json.RootElement.GetProperty("rows").EnumerateArray().Where(r => r.TryGetProperty("count", out _) || r.TryGetProperty("reclaimableBytes", out _)).Should().BeEmpty("an unread figure is absent, never 0");
        json.RootElement.GetProperty("kept").TryGetProperty("bytes", out _).Should().BeFalse();
        report.VolumeSeen.Recorded.Should().BeFalse();
        home.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_stopped_daemon_is_daemon_stopped_and_only_the_version_probe_ran()
    {
        using var home = new ScenarioHome("preview-stopped");
        // Docker 29.6.1's own words for a socket that does not exist (measured 2026-10-02).
        home.Script(DockerCommands.Executable, DockerCommands.Version.Arguments, 1, stderr: "failed to connect to the docker API at unix:///var/run/docker.sock; check if the path is correct and if the daemon is running: dial unix /var/run/docker.sock: connect: no such file or directory\n");

        var report = Report(await home.RunAsync("preview", "--all", "--json"));

        report.Docker.Kind.Should().Be("daemonStopped");
        report.Docker.Reason.Should().Contain("dial unix /var/run/docker.sock");
        Row(report, "A4").Reason.Should().Be(report.Docker.Reason);
        home.Calls.Should().ContainSingle().Which.Matches(DockerCommands.Executable, DockerCommands.Version.Arguments).Should().BeTrue();
    }

    [Fact]
    public async Task A_docker_that_hangs_is_cut_at_the_probe_ceiling_and_reported_timed_out()
    {
        using var home = new ScenarioHome("preview-hang");
        home.Script(DockerCommands.Executable, DockerCommands.Version.Arguments, 0, delayMilliseconds: 120_000);
        var watch = Stopwatch.StartNew();

        var report = Report(await home.RunAsync("preview", "--all", "--json"));

        watch.Elapsed.Should().BeLessThan(DockerCommands.ProbeCeiling + TimeSpan.FromSeconds(15), "the ceiling, not the fake's two minutes");
        report.Docker.Kind.Should().Be("timedOut");
        report.Docker.Reason.Should().Contain("process tree was killed");
        home.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task An_unwritable_state_directory_makes_preview_read_only_and_it_still_answers()
    {
        using var home = Captured("preview-readonly");
        Directory.CreateDirectory(home.Paths.StateDirectory);
        await using var denial = await AccessDenial.TryDenyAsync(home.Paths.StateDirectory);
        Assert.SkipWhen(denial is null, "this account is not bound by a directory denial (root or elevated)");

        var report = Report(await home.RunAsync("preview", "--all", "--json"));

        report.VolumeSeen.Recorded.Should().BeFalse();
        report.VolumeSeen.Reason.Should().StartWith("read-only:");
        Row(report, "A4").Available.Should().BeTrue();
        Row(report, "A4").Notes![0].Count.Should().Be(3, "unrecorded volumes count as first seen now");
        await denial!.DisposeAsync();
        File.Exists(new VolumeSeenStore(home.Paths, new Core.Files.PhysicalFileSystem(home.Paths)).File).Should().BeFalse();
    }

    [Fact]
    public async Task Preview_without_json_prints_the_rows_and_preview_without_all_is_refused()
    {
        using var home = Captured("preview-text");

        var text = await home.RunAsync("preview", "--all");
        var refused = await home.RunAsync("preview");

        text.Exit.Should().Be((int)ExitCode.Ok);
        text.StdoutLines[0].Should().StartWith("wsl-care preview (");
        text.StdoutLines.Should().Contain(l => l.StartsWith("A4 ", StringComparison.Ordinal));
        refused.Exit.Should().Be((int)ExitCode.Usage);
        CliStderr.Of(refused).Messages.Should().ContainSingle().Which.Should().Contain("needs --all");
    }
}
