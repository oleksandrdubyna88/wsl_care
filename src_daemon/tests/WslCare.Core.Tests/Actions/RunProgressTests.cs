using FluentAssertions;

using WslCare.Core.Actions.Engine;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Actions;

/// <summary>
/// E7.S2b/S2c review C-H2: the heartbeat stamps PROGRESS only when the run made a step since the last beat — a command, a stretch
/// of a walk, an action begun — so a hung run's fresh heartbeat no longer hides it.
/// </summary>
public sealed class RunProgressTests : IDisposable
{
    private readonly SandboxHost _sandbox = new("run-progress");

    public void Dispose() => _sandbox.Dispose();

    private RunningFile Read() => RunningState.Read(_sandbox.Paths, _sandbox.Files, new FakeProcessTable().Alive(4242, Started), DateTimeOffset.UtcNow) switch
    {
        RunningStatus.Live live => live.File,
        RunningStatus.Wedged wedged => wedged.File,
        var other => throw new InvalidOperationException($"running.json reads {other}"),
    };

    private static readonly DateTimeOffset Started = DateTimeOffset.UtcNow.AddMinutes(-1);

    /// <summary>How long a beat the test caused may take to reach the file — a bound on a loaded machine, not a timing.</summary>
    private static readonly TimeSpan BeatCeiling = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_beat_without_a_step_keeps_the_progress_time_and_a_step_moves_it()
    {
        // Each beat is a tick the TEST takes on a manual clock, and each read waits for exactly that beat: the old form slept
        // 300 ms on the real clock and read whatever was there, which on a loaded win-x64 runner (2026-10-06) was the file as
        // first written — the heartbeat's loop had not been given a thread yet.
        var begun = DateTimeOffset.UtcNow.AddMinutes(-10);
        var file = new RunningFile(1, RunId.New(begun, 4242), RunTrigger.Timer, ["A10"], "A10", 4242, Started, begun, begun, RunKind.Act) { ProgressAt = begun };
        RunningState.Write(_sandbox.Paths, _sandbox.Files, file);
        var clock = new ManualTimeProvider(WholeSeconds(DateTimeOffset.UtcNow)) { DrivesTimers = true };
        var period = TimeSpan.FromSeconds(5);

        var heartbeat = new Heartbeat(_sandbox.Paths, _sandbox.Files, clock, new FakeProcessTable().Alive(4242, Started), file, period);
        await using (heartbeat.ConfigureAwait(false))
        {
            (await RunningChild.WaitUntilAsync(() => clock.ActiveTimers == 1, BeatCeiling)).Should().BeTrue("the heartbeat's loop made its timer on the run's clock");

            var idleBeat = clock.GetUtcNow() + period;
            clock.Advance(period);
            (await RunningChild.WaitUntilAsync(() => Read().HeartbeatAt == idleBeat, BeatCeiling)).Should().BeTrue("the heartbeat beats on its timer");
            Read().ProgressAt.Should().Be(begun, "no step was made: progress is not a heartbeat");

            RunProgress.Mark();
            var movedBeat = idleBeat + period;
            clock.Advance(period);
            (await RunningChild.WaitUntilAsync(() => Read().HeartbeatAt == movedBeat, BeatCeiling)).Should().BeTrue("the heartbeat beats again a period later");
            Read().ProgressAt.Should().Be(movedBeat, "a step was made since the last beat: this beat stamps progress");
        }
    }

    private static DateTimeOffset WholeSeconds(DateTimeOffset at) => new(at.Ticks - (at.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero);

    [Fact]
    public void A_mark_outside_a_run_counts_nothing_and_a_run_counts_its_own()
    {
        RunProgress.Mark();
        var run = RunProgress.Begin();

        RunProgress.Mark();
        RunProgress.Mark();

        run.Steps.Should().Be(2);
    }
}
