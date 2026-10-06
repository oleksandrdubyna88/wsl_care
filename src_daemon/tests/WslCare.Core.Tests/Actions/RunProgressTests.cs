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

    [Fact]
    public async Task A_beat_without_a_step_keeps_the_progress_time_and_a_step_moves_it()
    {
        var begun = DateTimeOffset.UtcNow.AddMinutes(-10);
        var file = new RunningFile(1, RunId.New(begun, 4242), RunTrigger.Timer, ["A10"], "A10", 4242, Started, begun, begun, RunKind.Act) { ProgressAt = begun };
        RunningState.Write(_sandbox.Paths, _sandbox.Files, file);

        var heartbeat = new Heartbeat(_sandbox.Paths, _sandbox.Files, TimeProvider.System, new FakeProcessTable().Alive(4242, Started), file, TimeSpan.FromMilliseconds(40));
        await using (heartbeat.ConfigureAwait(false))
        {
            await Task.Delay(300, TestContext.Current.CancellationToken);
            var idle = Read();
            RunProgress.Mark();
            await Task.Delay(300, TestContext.Current.CancellationToken);
            var moved = Read();

            idle.HeartbeatAt.Should().BeAfter(begun, "the heartbeat beats on its timer");
            idle.ProgressAt.Should().Be(begun, "no step was made: progress is not a heartbeat");
            moved.ProgressAt.Should().BeAfter(begun, "a step was made");
        }
    }

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
