using System.Text.Json;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Docker;
using WslCare.Core.Events;
using WslCare.Core.Files;
using WslCare.Core.Json;
using WslCare.FakeTool;
using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>
/// <c>wsl-care events follow [--once]</c> end to end (plan §4.3, §15b #0 and #8): the BUILT CLI with a fake
/// <c>docker</c> whose event lines are SYNTHETIC in the captured shape (<see cref="DockerEventLines"/> — Docker's buffer
/// held no start on 2026-10-02): a daemon restart that lost the buffer, a daemon that is down, and — on Linux, where
/// a signal can be sent — a socket down then up, a live start, and SIGTERM.
/// </summary>
public sealed class EventsFlows
{
    private const string Down = "failed to connect to the docker API at unix:///var/run/docker.sock; check if the path is correct and if the daemon is running";

    private static ContainerStartsStore Store(ScenarioHome home) => new(home.Paths, new PhysicalFileSystem(home.Paths));

    private static FakeAnswer Version(int upTo = 0, bool down = false) =>
        down
            ? new FakeAnswer(DockerCommands.Executable, DockerCommands.Version.Arguments, 1, string.Empty, Down) { UpTo = upTo }
            : new FakeAnswer(DockerCommands.Executable, DockerCommands.Version.Arguments, 0, ScenarioHome.Fixture($"docker/{DockerFixture.Name}/version.out"), string.Empty) { UpTo = upTo };

    [Fact]
    public async Task Once_after_a_daemon_restart_writes_ONE_unrecoverable_gap_and_status_counts_partial_naming_it()
    {
        using var home = new ScenarioHome("events-restart");
        var now = DateTimeOffset.UtcNow;
        Store(home).Append(new CoverageLine.Covered(now.AddHours(-1)));
        var buffer = home.WriteFile("buffer.jsonl", DockerEventLines.Text(DockerEventLines.Start(new string('a', 64), "after-restart", "redis:7", now.AddMinutes(-5))));
        home.Answer(Version()).Answer(new FakeAnswer(DockerCommands.Executable, ["events", "--since"], 0, buffer, string.Empty) { Prefix = true });

        var result = await home.RunAsync("events", "follow", "--once");

        result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
        result.Stdout.Should().Contain("1 start(s), 1 gap marker(s)");
        var lines = Store(home).ReadAll();
        lines.OfType<CoverageLine.Gap>().Should().ContainSingle().Which.Reason.Should().Contain("reaches back only to");
        lines.OfType<CoverageLine.Start>().Should().ContainSingle().Which.Should().Match<CoverageLine.Start>(s => s.Name == "after-restart" && s.Backfilled);
        home.Calls.Should().OnlyContain(c => c.Tool == DockerCommands.Executable && DockerCommands.IsReadVerb(c.Argv));

        var status = await home.RunAsync("status", "--json");

        var starts = JsonSerializer.Deserialize(status.Stdout, WslCareJsonContext.Default.StatusReport)!.ContainerStarts!;
        starts.Starts.Should().Be(1);
        starts.Complete.Should().BeFalse("a gap lies inside the last 24 h");
        starts.Gaps.Should().Contain(g => g.Reason.Contains("reaches back only to"));
    }

    [Fact]
    public async Task Once_over_an_idle_engine_that_did_not_restart_writes_no_gap_and_records_the_engine_on_its_marker()
    {
        // The continuity rule end to end (gate finding #2/#7/#9): the same bridge network as the last marker recorded, an
        // EMPTY buffer — nothing happened, nothing was lost. E2.S3 wrote an unrecoverable gap here.
        using var home = new ScenarioHome("events-idle");
        var now = DateTimeOffset.UtcNow;
        var engine = new EngineMark(new string('b', 64), now.AddDays(-3));
        Store(home).Append(new CoverageLine.Covered(now.AddHours(-1)) { Engine = Core.Collectors.Reading.Of(engine) });
        var empty = home.WriteFile("empty.jsonl", string.Empty);
        var bridge = home.WriteFile("bridge.json", "{\"id\":\"" + engine.Id + "\",\"created\":\"" + engine.StartedAt.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + "\"}\n");
        home.Answer(Version())
            .Answer(new FakeAnswer(DockerCommands.Executable, ["events", "--since"], 0, empty, string.Empty) { Prefix = true })
            .Answer(new FakeAnswer(DockerCommands.Executable, DockerCommands.EngineStart.Arguments, 0, bridge, string.Empty));

        var result = await home.RunAsync("events", "follow", "--once");

        result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
        result.Stdout.Should().Contain("0 start(s), 0 gap marker(s)");
        var lines = Store(home).ReadAll();
        lines.OfType<CoverageLine.Gap>().Should().BeEmpty("an idle engine that did not restart dropped nothing");
        lines.OfType<CoverageLine.Covered>().Last().Engine.Should().Be(Core.Collectors.Reading.Of(engine));
        home.Calls.Should().OnlyContain(c => c.Tool == DockerCommands.Executable && DockerCommands.IsReadVerb(c.Argv));
        home.Calls.Should().Contain(c => c.Matches(DockerCommands.Executable, DockerCommands.EngineStart.Arguments));
        File.Exists(Store(home).SummaryFile).Should().BeTrue("the follower keeps the 24-hour summary status reads");
    }

    [Fact]
    public async Task Once_with_the_daemon_down_exits_zero_names_why_and_writes_no_gap()
    {
        using var home = new ScenarioHome("events-down");
        home.Answer(Version(down: true));

        var result = await home.RunAsync("events", "follow", "--once");

        result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
        result.Stdout.Should().Contain("Docker could not be read").And.Contain("DaemonStopped");
        Store(home).ReadAll().Select(l => l.GetType().Name).Should().Equal(nameof(CoverageLine.FollowerStarted), nameof(CoverageLine.FollowerStopped));
        home.Calls.Should().ContainSingle("nothing is started after the probe says no daemon answers").Which.Display.Should().Be(DockerCommands.Version.Display);
    }

    [Fact]
    public async Task Followed_live_it_waits_for_the_socket_in_process_records_a_start_and_stops_clean_on_SIGTERM()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "SIGTERM is sent on Linux (the wsl-care-events unit's stop); the backoff and the stop are proved in-process on every OS (EventsFollowerTests, FullRunCommandTests)");
        using var home = new ScenarioHome("events-live");
        var now = DateTimeOffset.UtcNow;
        Store(home).Append(new CoverageLine.Covered(now.AddHours(-1)));
        var buffer = home.WriteFile("buffer.jsonl", DockerEventLines.Text(DockerEventLines.Exec("h", now)));
        var live = home.WriteFile("live.jsonl", DockerEventLines.Text(DockerEventLines.Start(new string('b', 64), "live-one", "postgres:17", now.AddSeconds(30))));
        home.Answer(Version(upTo: 1, down: true)).Answer(Version())
            .Answer(new FakeAnswer(DockerCommands.Executable, ["events", "--since"], 0, buffer, string.Empty) { Prefix = true, UpTo = 1 })
            .Answer(new FakeAnswer(DockerCommands.Executable, ["events", "--since"], 0, live, string.Empty) { Prefix = true, HangAfterMilliseconds = 120_000 });

        using var follower = home.Start("events", "follow");
        var recorded = await RunningChild.WaitUntilAsync(() => Store(home).ReadAll().OfType<CoverageLine.Start>().Any(s => s.Name == "live-one"), TimeSpan.FromSeconds(30));
        recorded.Should().BeTrue("the live start reaches the day file while the stream is still open");
        follower.Terminate();
        var result = await follower.WaitAsync(TimeSpan.FromSeconds(20));

        result.Exit.Should().Be((int)ExitCode.Ok, $"SIGTERM is the follower's normal end; stderr: {result.Stderr}");
        result.Stderr.Should().Contain("waiting 5 s", "the first wait for the socket is 5 s, in the process");
        var lines = Store(home).ReadAll();
        lines.OfType<CoverageLine.Gap>().Should().ContainSingle("one outage, one gap marker");
        lines[^1].Should().BeOfType<CoverageLine.FollowerStopped>().Which.Reached.Should().Be(now.AddSeconds(30));
        home.Calls.Count(c => c.Argv.SequenceEqual(DockerCommands.Version.Arguments)).Should().Be(2, "down once, then up");
    }
}
