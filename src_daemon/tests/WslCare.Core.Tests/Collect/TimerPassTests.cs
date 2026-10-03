using System.Text.Json;

using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Collect;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Json;
using WslCare.Core.Processes;
using WslCare.Core.Records;
using WslCare.Core.Tests.Actions;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Collect;

/// <summary>
/// The TIMER PASS of a full run (E3.S3): <c>collect</c> started by the timer measures, then runs the action engine under the
/// SAME lock and records the outcomes in the SAME run record; the engine's own gates decide each action (the dry-run week
/// records <c>dryRun</c> with what it would free); a <c>collect</c> from a terminal or the panel does not act; a live
/// <c>running.json</c> of another run stops the pass, never the measurement. Scripted actions log what the engine asked.
/// </summary>
public sealed class TimerPassTests : IDisposable
{
    private const int Pid = 4242;

    private readonly SandboxHost _sandbox = new("timer-pass");
    private readonly FixedTimeProvider _clock = new();
    private readonly List<string> _journal = [];

    public void Dispose() => _sandbox.Dispose();

    private CollectContext Context(RunTrigger trigger, params ICleanupAction[] actions) =>
        new(_sandbox.Paths, _sandbox.Files, new RecordingCommandRunner { Default = new CommandOutcome.FailedToStart("not installed in this test") }, _clock,
            new FakeProbe(_sandbox.Paths.Side, _clock), ConfigLoader.Load(_sandbox.Paths, _sandbox.Files), Pid, trigger)
        {
            Actions = new ActionRegistry(actions),
            Processes = new FakeProcessTable().Alive(Pid, FixedTimeProvider.DefaultNow.AddMinutes(-1)),
        };

    private IReadOnlyList<RunRecord> History() => RunHistory.Read(_sandbox.Paths, _sandbox.Files).Records;

    private RunDetail Detail(CollectResult result) =>
        JsonSerializer.Deserialize(File.ReadAllBytes(RunDetailStore.Absolute(_sandbox.Paths, result.DetailFile)), WslCareJsonContext.Default.RunDetail)!;

    [Fact]
    public async Task The_timer_measures_then_acts_under_the_same_lock_and_the_dry_run_week_is_recorded_in_the_one_run_record()
    {
        var lockHeldDuringPreview = false;
        var action = new ScriptedAction("A10", _journal)
        {
            PreviewBytes = 5_000,
            OnPreview = _ => lockHeldDuringPreview = RunLock.TryTake(_sandbox.Paths, _sandbox.Files) is ExclusiveLock.Busy,
        };

        var result = await CollectRun.RunAsync(Context(RunTrigger.Timer, action), CancellationToken.None);

        result.Recording.Should().Be(Recording.Recorded, result.Reason);
        lockHeldDuringPreview.Should().BeTrue("the pass runs under the full run's own lock");
        _journal.Should().Equal("preview A10");
        var line = History().Should().ContainSingle("ONE record for the measurement and the actions").Subject;
        line.DryRun.Should().BeTrue();
        line.Actions.Should().ContainSingle().Which.Should().Match<ActionRecord>(a => a.Id == "A10" && a.Status == ActionStatus.DryRun && a.WouldFreeBytes == 5_000);
        line.Metrics.Should().NotBeNull("the measurement is in the same line");
        var pass = Detail(result).TimerPass!;
        pass.Ran.Should().BeTrue();
        pass.DryRunReason.Should().Contain("dryRun is on");
        pass.Actions.Single().Preview!.Bytes.Should().Be(5_000);
        File.Exists(RunningState.File(_sandbox.Paths)).Should().BeFalse("the pass's running.json goes once the run is recorded");
        File.Exists(DryRunWindow.File(_sandbox.Paths)).Should().BeTrue("the first timer pass starts the dry-run week");
    }

    [Fact]
    public async Task After_the_dry_run_week_the_timer_pass_runs_the_action_and_the_history_line_carries_its_measured_result()
    {
        _sandbox.WriteUserConfig("{ \"dryRun\": false }");
        Directory.CreateDirectory(_sandbox.Paths.StateDirectory);
        File.WriteAllText(DryRunWindow.File(_sandbox.Paths), "{\"schemaVersion\":1,\"at\":\"2026-09-01T00:00:00+00:00\"}");

        var result = await CollectRun.RunAsync(Context(RunTrigger.Timer, new ScriptedAction("A10", _journal)), CancellationToken.None);

        _journal.Should().Equal("preview A10", "run A10");
        History().Single().Actions.Single().Should().Match<ActionRecord>(a => a.Status == ActionStatus.Ran && a.FreedBytes == 100 && a.Count == 1);
        Detail(result).TimerPass!.Actions.Single().Run!.FreedBytes.Should().Be(100);
    }

    [Theory]
    [InlineData(RunTrigger.Cli)]
    [InlineData(RunTrigger.Manual)]
    public async Task A_full_run_not_started_by_the_timer_measures_and_never_acts(RunTrigger trigger)
    {
        var result = await CollectRun.RunAsync(Context(trigger, new ScriptedAction("A10", _journal)), CancellationToken.None);

        result.Recording.Should().Be(Recording.Recorded);
        _journal.Should().BeEmpty("a button's act is a separate run");
        Detail(result).TimerPass.Should().BeNull();
        History().Single().Actions.Should().BeEmpty();
    }

    [Fact]
    public async Task A_live_run_in_running_json_stops_the_pass_and_the_measurement_is_still_recorded()
    {
        var other = 777;
        Directory.CreateDirectory(_sandbox.Paths.StateDirectory);
        var running = new RunningFile(1, RunId.New(FixedTimeProvider.DefaultNow, other), RunTrigger.Manual, ["A5"], "A5", other, FixedTimeProvider.DefaultNow.AddMinutes(-2), FixedTimeProvider.DefaultNow, FixedTimeProvider.DefaultNow);
        File.WriteAllBytes(RunningState.File(_sandbox.Paths), JsonSerializer.SerializeToUtf8Bytes(running, WslCareJsonContext.Default.RunningFile));
        var context = Context(RunTrigger.Timer, new ScriptedAction("A10", _journal)) with
        {
            Processes = new FakeProcessTable().Alive(Pid, FixedTimeProvider.DefaultNow.AddMinutes(-1)).Alive(other, FixedTimeProvider.DefaultNow.AddMinutes(-2)),
        };

        var result = await CollectRun.RunAsync(context, CancellationToken.None);

        result.Recording.Should().Be(Recording.Recorded);
        _journal.Should().BeEmpty();
        Detail(result).TimerPass.Should().Match<TimerPass>(p => !p.Ran && p.Reason.Contains("is acting"));
        File.Exists(RunningState.File(_sandbox.Paths)).Should().BeTrue("another live run's running.json is never removed");
    }
}
