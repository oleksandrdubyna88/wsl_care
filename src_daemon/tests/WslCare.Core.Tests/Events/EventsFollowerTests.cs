using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Docker;
using WslCare.Core.Events;
using WslCare.Core.Processes;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Events;

/// <summary>
/// The follower over a scripted docker (<see cref="RecordingCommandRunner"/>: the version probe, the backfill and the
/// stream segments) and a clock the test moves (<see cref="ManualTimeProvider"/>), so the backoff of plan §15b #8 is
/// asserted to the second without a real wait. Event lines are SYNTHETIC in the captured shape.
/// </summary>
public sealed class EventsFollowerTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private const string Down = "failed to connect to the docker API at unix:///var/run/docker.sock; check if the path is correct and if the daemon is running";

    private readonly SandboxHost _sandbox = new("follower");
    private readonly ManualTimeProvider _clock = new(Start);
    private readonly List<TimeSpan> _waits = [];
    private readonly List<string> _notes = [];

    public void Dispose() => _sandbox.Dispose();

    private ContainerStartsStore Store => new(_sandbox.Paths, _sandbox.Files);

    private EventsFollower Follower(RecordingCommandRunner docker) => new(docker, Store, _clock, _clock.RecordingWait(_waits), _notes.Add);

    private static bool IsVersion(IReadOnlyList<string> argv) => argv.SequenceEqual(DockerCommands.Version.Argv);

    private static bool IsBackfill(IReadOnlyList<string> argv) => argv is ["docker", "events", "--since", _, "--until", _, "--format", _];

    private static CommandOutcome Reachable() => RecordingCommandRunner.Exited(0, DockerFixture.Read("version.out"));

    private static CommandOutcome Unreachable() => RecordingCommandRunner.Exited(1, stderr: Down);

    private static CommandOutcome Buffer(params string[] lines) => RecordingCommandRunner.Exited(0, DockerEventLines.Text(lines));

    private static bool IsEngineStart(IReadOnlyList<string> argv) => argv.SequenceEqual(DockerCommands.EngineStart.Argv);

    private static CommandOutcome Bridge(string id, DateTimeOffset created) =>
        RecordingCommandRunner.Exited(0, "{\"id\":\"" + id + "\",\"created\":\"" + created.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + "\"}\n");

    [Fact]
    public async Task An_idle_engine_that_did_not_restart_answers_an_empty_buffer_and_the_catch_up_writes_no_gap_but_records_the_engine()
    {
        // Gate finding #2/#7/#9, through the follower: the same bridge (the same engine instance) as the last marker, an
        // empty buffer, nothing to fill - covered, and the new marker carries the engine for the next comparison.
        var engineStart = Start.AddDays(-3);
        Store.Append(new CoverageLine.Covered(Start.AddHours(-2)) { Engine = Reading.Of(new EngineMark("bridge-a", engineStart)) });
        var docker = new RecordingCommandRunner()
            .Script(IsVersion, Reachable())
            .Script(IsBackfill, Buffer())
            .Script(IsEngineStart, Bridge("bridge-a", engineStart));

        var result = await Follower(docker).RunAsync(once: true, processId: 7, TestContext.Current.CancellationToken);

        result.GapsRecorded.Should().Be(0);
        var lines = Store.ReadAll();
        lines.OfType<CoverageLine.Gap>().Should().BeEmpty();
        lines.OfType<CoverageLine.Covered>().Last().Engine.Should().Be(Reading.Of(new EngineMark("bridge-a", engineStart)));
        docker.Requests.Select(r => r.Argv).Should().ContainSingle(a => IsEngineStart(a), "the engine is read once, after the events");
        Store.ReadSummary(Start).Starts.Should().Be(0, "the follower wrote its 24-hour summary after the markers");
        File.Exists(Store.SummaryFile).Should().BeTrue();
    }

    [Fact]
    public async Task An_engine_restarted_since_the_last_marker_is_ONE_gap_up_to_its_start_even_with_an_empty_buffer()
    {
        var restarted = Start.AddMinutes(-10);
        Store.Append(new CoverageLine.Covered(Start.AddHours(-2)) { Engine = Reading.Of(new EngineMark("bridge-a", Start.AddDays(-3))) });
        var docker = new RecordingCommandRunner()
            .Script(IsVersion, Reachable())
            .Script(IsBackfill, Buffer())
            .Script(IsEngineStart, Bridge("bridge-b", restarted));

        await Follower(docker).RunAsync(once: true, processId: 7, TestContext.Current.CancellationToken);

        Store.ReadAll().OfType<CoverageLine.Gap>().Should().ContainSingle()
            .Which.Should().Match<CoverageLine.Gap>(g => g.From == Start.AddHours(-2) && g.To == restarted && g.Reason.Contains("restarted"));
    }

    [Fact]
    public async Task Docker_down_then_up_waits_in_process_with_backoff_and_writes_ONE_gap_marker_for_the_outage()
    {
        using var stop = new CancellationTokenSource();
        Store.Append(new CoverageLine.Covered(Start.AddHours(-1)));
        var docker = new RecordingCommandRunner()
            .Script(IsVersion, Unreachable(), Unreachable(), Unreachable(), Reachable())
            // The daemon restarted: its buffer begins after the last marker, so the gap cannot be filled.
            .Script(IsBackfill, Buffer(DockerEventLines.Start("a", "after-restart", "redis:7", Start.AddSeconds(20))))
            .Stream(new StreamScript([DockerEventLines.Start("b", "live", "postgres:17", Start.AddSeconds(60))], RecordingCommandRunner.Exited(0)) { Then = stop.Cancel });

        var run = () => Follower(docker).RunAsync(once: false, processId: 7, stop.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
        _waits.Select(w => w.TotalSeconds).Should().Equal(5, 10, 20);
        var lines = Store.ReadAll();
        lines.OfType<CoverageLine.Gap>().Should().ContainSingle("one outage, one gap marker — never one per retry")
            .Which.Should().Match<CoverageLine.Gap>(g => g.From == Start.AddHours(-1) && g.To == Start.AddSeconds(20));
        lines.OfType<CoverageLine.Start>().Select(s => (s.Id, s.Backfilled)).Should().Equal(("a", true), ("b", false));
        lines[^1].Should().BeOfType<CoverageLine.FollowerStopped>().Which.Reached.Should().Be(Start.AddSeconds(60), "the stop marker carries how far coverage reached");
    }

    [Fact]
    public async Task A_buffer_that_still_covers_the_outage_fills_it_and_writes_no_gap()
    {
        using var stop = new CancellationTokenSource();
        Store.Append(new CoverageLine.Covered(Start.AddMinutes(-5)));
        var docker = new RecordingCommandRunner()
            .Script(IsVersion, Unreachable(), Reachable())
            .Script(IsBackfill, Buffer(DockerEventLines.Exec("h", Start.AddMinutes(-20)), DockerEventLines.Start("a", "missed", "redis:7", Start.AddMinutes(-2))))
            .Stream(new StreamScript([], RecordingCommandRunner.Exited(0)) { Then = stop.Cancel });

        var run = () => Follower(docker).RunAsync(once: false, processId: 7, stop.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
        Store.ReadAll().OfType<CoverageLine.Gap>().Should().BeEmpty("the oldest buffered event is older than the last marker");
        Store.ReadAll().OfType<CoverageLine.Start>().Should().ContainSingle().Which.Id.Should().Be("a");
    }

    [Fact]
    public async Task A_segment_that_ends_at_its_until_is_a_covered_marker_and_the_next_segment_resumes_from_it()
    {
        using var stop = new CancellationTokenSource();
        var docker = new RecordingCommandRunner()
            .Script(IsVersion, Reachable())
            .Script(IsBackfill, Buffer(DockerEventLines.Exec("h", Start.AddHours(-25).AddMinutes(1))))
            .Stream(new StreamScript([], RecordingCommandRunner.Exited(0)) { Then = () => _clock.Advance(EventsFollower.SegmentLength) })
            .Stream(new StreamScript([], RecordingCommandRunner.Exited(0)) { Then = stop.Cancel });

        var run = () => Follower(docker).RunAsync(once: false, processId: 7, stop.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
        var segments = docker.Requests.Where(r => r.Argv.Contains("--filter")).Select(r => r.Argv).ToList();
        segments.Should().HaveCount(2);
        segments[1][3].Should().Be(segments[0][5], "the second segment's --since is the first one's --until");
        Store.ReadAll().OfType<CoverageLine.Covered>().Select(c => c.At).Should().Contain(Start + EventsFollower.SegmentLength);
    }

    [Fact]
    public async Task A_stream_that_breaks_goes_back_to_waiting_for_docker_and_still_writes_one_gap_per_outage()
    {
        using var stop = new CancellationTokenSource();
        var docker = new RecordingCommandRunner()
            .Script(IsVersion, Reachable(), Unreachable(), Unreachable(), Reachable())
            .Script(IsBackfill, Buffer(DockerEventLines.Exec("h", Start.AddHours(-23))), Buffer(DockerEventLines.Start("r", "after", "redis:7", Start.AddMinutes(2).AddSeconds(10))))
            .Stream(new StreamScript([DockerEventLines.Start("x", "live", "redis:7", Start.AddSeconds(30))], RecordingCommandRunner.Exited(1, stderr: "unexpected EOF")) { Then = () => _clock.Advance(TimeSpan.FromMinutes(2)) })
            .Stream(new StreamScript([], RecordingCommandRunner.Exited(0)) { Then = stop.Cancel });

        var run = () => Follower(docker).RunAsync(once: false, processId: 7, stop.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
        _waits.Select(w => w.TotalSeconds).Should().Equal(5, 10);
        var gaps = Store.ReadAll().OfType<CoverageLine.Gap>().ToList();
        gaps.Should().HaveCount(2, "the follower's first start is one gap, the broken stream's outage is the other");
        gaps[1].From.Should().Be(Start.AddSeconds(30), "the outage starts where the live stream had reached");
    }

    [Fact]
    public async Task Once_with_docker_unreachable_returns_at_once_without_waiting_and_writes_only_its_markers()
    {
        Store.Append(new CoverageLine.Covered(Start.AddHours(-1)));
        var docker = new RecordingCommandRunner().Script(IsVersion, Unreachable());

        var result = await Follower(docker).RunAsync(once: true, processId: 7, CancellationToken.None);

        _waits.Should().BeEmpty();
        result.Problem.Should().Contain("DaemonStopped");
        var lines = Store.ReadAll();
        lines.OfType<CoverageLine.Gap>().Should().BeEmpty("a gap is only known once Docker answers");
        lines[^1].Should().BeOfType<CoverageLine.FollowerStopped>().Which.Reached.Should().Be(Start.AddHours(-1));
        docker.Requests.Should().OnlyContain(r => DockerCommands.IsReadVerb(r.Argv.Skip(1).ToList()));
    }

    [Fact]
    public async Task Day_files_older_than_14_days_are_pruned_at_start_through_the_file_system_seam()
    {
        var old = Start.AddDays(-(ContainerStartsStore.RetentionDays + 1));
        Store.Append(new CoverageLine.Covered(old));
        Store.Append(new CoverageLine.Covered(Start.AddDays(-1)));
        var docker = new RecordingCommandRunner().Script(IsVersion, Unreachable());

        await Follower(docker).RunAsync(once: true, processId: 7, CancellationToken.None);

        _sandbox.Files.ListFiles(Store.Directory).Select(Path.GetFileName).Should().NotContain($"{old:yyyy-MM-dd}.jsonl").And.Contain($"{Start.AddDays(-1):yyyy-MM-dd}.jsonl");
    }
}
