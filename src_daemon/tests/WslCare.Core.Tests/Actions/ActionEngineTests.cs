using System.Text;
using System.Text.Json;

using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Collect;
using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Json;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Actions;

/// <summary>
/// The action engine (E3.S1) over the distro's layout in a temporary root, with scripted actions: the execution order, the
/// gates (side, observe-only, auto switch, trigger, target user, refusal, idle, dry run), a failing action that does not end
/// the run, the 7-day dry-run window of the timer, the run records, <c>running.json</c> with its heartbeat and its sweep,
/// the lock shared with <c>collect</c>, and an interrupted run.
/// </summary>
public sealed class ActionEngineTests : IDisposable
{
    private const int Pid = 4242;
    private static readonly DateTimeOffset OwnStart = FixedTimeProvider.DefaultNow.AddMinutes(-1);

    private readonly LinuxSandbox _sandbox = new("engine");
    private readonly List<string> _journal = [];
    private readonly ManualTimeProvider _clock = new(FixedTimeProvider.DefaultNow);
    private readonly FakeProcessTable _processes = new FakeProcessTable().Alive(Pid, OwnStart);

    public ActionEngineTests()
    {
        _sandbox.Write("/etc/passwd", "root:x:0:0::/root:/bin/bash\nme:x:1000:1000::/home/me:/bin/bash\n");
        _sandbox.Load(0.1, 0.1, 0.1, cpus: 4);
    }

    public void Dispose() => _sandbox.Dispose();

    private ActionEngine Engine(params ICleanupAction[] actions) => new(Context(actions));

    private EngineContext Context(params ICleanupAction[] actions) =>
        new(_sandbox.Paths, _sandbox.Files, new RecordingCommandRunner { Policy = CommandPolicy.Product }, _clock, new LinuxProbe(_sandbox.Files, _sandbox.Paths, _clock),
            ConfigLoader.Load(_sandbox.Paths, _sandbox.Files), _processes, Pid, new ActionRegistry(actions));

    private ScriptedAction Action(string id) => new(id, _journal);

    private static ActRequest Run(RunTrigger trigger, params string[] ids) => new([.. ids.Select(i => ActionId.Find(i)!)], trigger, Execute: true);

    private void UserConfig(string json) => _sandbox.Write("/home/me/.config/wsl-care/config.json", json);

    private IReadOnlyList<RunRecord> History() => RunHistory.Read(_sandbox.Paths, _sandbox.Files).Records;

    private static ActResult.Done Done(ActResult result) => result.Should().BeOfType<ActResult.Done>().Subject;

    private static IReadOnlyList<string> Statuses(ActResult result) => [.. Done(result).Detail.Actions.Select(a => $"{a.Id}:{a.Status}")];

    [Fact]
    public async Task Actions_run_in_the_fixed_order_whatever_order_they_were_asked_in_and_running_json_names_each_while_it_runs()
    {
        var current = new List<string>();
        var kinds = new List<RunKind?>();
        Func<ActionContext, Task<ActionRun>> note = _ =>
        {
            var running = JsonSerializer.Deserialize(File.ReadAllBytes(RunningState.File(_sandbox.Paths)), WslCareJsonContext.Default.RunningFile)!;
            current.Add(running.Current);
            kinds.Add(running.Kind);
            return Task.FromResult(new ActionRun(1, 100, "scripted", 200, 100, [], [], string.Empty));
        };

        var result = await Engine(new ScriptedAction("A10", _journal) { OnRun = note }, new ScriptedAction("A5", _journal) { OnRun = note }, new ScriptedAction("A4", _journal) { OnRun = note })
            .ExecuteAsync(Run(RunTrigger.Cli, "A10", "A5", "A4"), CancellationToken.None);

        Statuses(result).Should().Equal("A5:ran", "A4:ran", "A10:ran");
        _journal.Should().Equal("preview A5", "run A5", "preview A4", "run A4", "preview A10", "run A10");
        current.Should().Equal("A5", "A4", "A10");
        File.Exists(RunningState.File(_sandbox.Paths)).Should().BeFalse("running.json goes when the run ends");
        var done = Done(result);
        done.Recording.Should().Be(Recording.Recorded);
        File.Exists(RunDetailStore.Absolute(_sandbox.Paths, done.DetailFile)).Should().BeTrue();
        var line = History().Last();
        line.RunId.Should().Be(done.Detail.RunId);
        line.Detail.Should().Be(done.DetailFile);
        line.Actions.Select(a => (a.Id, a.Status, a.FreedBytes)).Should().Equal(("A5", "ran", 100L), ("A4", "ran", 100L), ("A10", "ran", 100L));
        line.Kind.Should().Be(RunKind.Act, "plan §15o: an act's line names it");
        kinds.Should().AllBeEquivalentTo(RunKind.Act, "and so does its running.json");
    }

    [Fact]
    public async Task A_failing_action_is_recorded_and_the_run_goes_on_with_the_next()
    {
        var engine = Engine(
            new ScriptedAction("A5", _journal) { OnRun = _ => throw new InvalidOperationException("docker answered nonsense") },
            new ScriptedAction("A4", _journal) { OnRun = _ => Task.FromResult(new ActionRun(0, 0, "x", null, null, [], [], "docker volume rm exited 1")) },
            Action("A10"));

        var result = await engine.ExecuteAsync(Run(RunTrigger.Cli, "A4", "A5", "A10"), CancellationToken.None);

        Statuses(result).Should().Equal("A5:failed", "A4:failed", "A10:ran");
        Done(result).Detail.Actions[0].Reason.Should().Contain("docker answered nonsense");
        Done(result).Detail.Actions[1].Reason.Should().Be("docker volume rm exited 1");
        History().Last().Outcome.Should().Be(RunOutcome.Completed, "a failing action is logged and the run continues (plan §5)");
    }

    /// <summary>§15o review O2: an <c>act</c> is an act whatever started it — <c>act --timer</c> holds ids like the timer's own pass
    /// inside a full check, and only <c>kind</c> tells the two apart, in <c>running.json</c> and on the line.</summary>
    [Theory]
    [InlineData(RunTrigger.Cli)]
    [InlineData(RunTrigger.Manual)]
    [InlineData(RunTrigger.Timer)]
    public async Task An_act_names_itself_act_in_running_json_and_on_its_line_whatever_its_trigger(RunTrigger trigger)
    {
        UserConfig("""{ "dryRun": false }""");
        Directory.CreateDirectory(_sandbox.Paths.StateDirectory);
        File.WriteAllText(DryRunWindow.File(_sandbox.Paths), $$"""{ "schemaVersion": 1, "at": "{{FixedTimeProvider.DefaultNow.AddDays(-30):O}}" }""");
        var kinds = new List<RunKind?>();
        var action = new ScriptedAction("A10", _journal)
        {
            OnRun = _ =>
            {
                kinds.Add(JsonSerializer.Deserialize(File.ReadAllBytes(RunningState.File(_sandbox.Paths)), WslCareJsonContext.Default.RunningFile)!.Kind);
                return Task.FromResult(new ActionRun(1, 100, "scripted", 200, 100, [], [], string.Empty));
            },
        };

        var result = await Engine(action).ExecuteAsync(Run(trigger, "A10"), CancellationToken.None);

        Statuses(result).Should().Equal("A10:ran");
        kinds.Should().Equal([RunKind.Act], "running.json was read while the act ran");
        History().Single().Kind.Should().Be(RunKind.Act);
    }

    [Fact]
    public async Task The_timer_honours_each_auto_switch_and_each_trigger_and_a_button_runs_regardless()
    {
        UserConfig("""{ "dryRun": false, "auto": { "A5": false } }""");
        Directory.CreateDirectory(_sandbox.Paths.StateDirectory);
        File.WriteAllText(DryRunWindow.File(_sandbox.Paths), $$"""{ "schemaVersion": 1, "at": "{{FixedTimeProvider.DefaultNow.AddDays(-30):O}}" }""");
        ICleanupAction[] actions = [Action("A5"), new ScriptedAction("A4", _journal) { Fires = false }, Action("A10")];

        var timer = await Engine(actions).ExecuteAsync(Run(RunTrigger.Timer, "A5", "A4", "A10"), CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(1));
        var button = await Engine(actions).ExecuteAsync(Run(RunTrigger.Cli, "A5", "A4", "A10"), CancellationToken.None);

        Statuses(timer).Should().Equal("A5:skipped", "A4:skipped", "A10:ran");
        Done(timer).Detail.Actions[0].Reason.Should().Contain("auto.A5 is off");
        Done(timer).Detail.Actions[1].Reason.Should().Contain("trigger not reached");
        Statuses(button).Should().Equal("A5:ran", "A4:ran", "A10:ran");
    }

    [Fact]
    public async Task The_timer_runs_dry_for_seven_days_from_its_first_action_pass_even_with_dry_run_switched_off()
    {
        UserConfig("""{ "dryRun": false }""");

        var first = await Engine(Action("A10")).ExecuteAsync(Run(RunTrigger.Timer, "A10"), CancellationToken.None);
        _clock.Advance(TimeSpan.FromDays(7) - TimeSpan.FromMinutes(1));
        var lastDryDay = await Engine(Action("A10")).ExecuteAsync(Run(RunTrigger.Timer, "A10"), CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(2));
        var after = await Engine(Action("A10")).ExecuteAsync(Run(RunTrigger.Timer, "A10"), CancellationToken.None);

        Statuses(first).Should().Equal("A10:dryRun");
        Done(first).Detail.DryRunReason.Should().Contain("first 7 days").And.Contain("the first timer run starts the dry-run week");
        Statuses(lastDryDay).Should().Equal("A10:dryRun");
        Statuses(after).Should().Equal("A10:ran");
        _journal.Count(j => j == "run A10").Should().Be(1, "a dry run previews and records, it never runs");
        History().First().Actions.Single().Should().Match<ActionRecord>(a => a.Status == "dryRun" && a.WouldFreeBytes == 100 && a.FreedBytes == 0);
        History().First().DryRun.Should().BeTrue();
    }

    [Fact]
    public async Task The_dry_run_setting_keeps_the_timer_dry_after_the_week_and_never_holds_back_a_button()
    {
        Directory.CreateDirectory(_sandbox.Paths.StateDirectory);
        File.WriteAllText(DryRunWindow.File(_sandbox.Paths), $$"""{ "schemaVersion": 1, "at": "{{FixedTimeProvider.DefaultNow.AddDays(-30):O}}" }""");

        var timer = await Engine(Action("A10")).ExecuteAsync(Run(RunTrigger.Timer, "A10"), CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(1));
        var button = await Engine(Action("A10")).ExecuteAsync(Run(RunTrigger.Cli, "A10"), CancellationToken.None);

        Statuses(timer).Should().Equal("A10:dryRun");
        Done(timer).Detail.DryRunReason.Should().Contain("the setting dryRun is on");
        Statuses(button).Should().Equal("A10:ran");
        Done(button).Detail.DryRun.Should().BeFalse();
        File.ReadAllText(DryRunWindow.File(_sandbox.Paths)).Should().Contain(FixedTimeProvider.DefaultNow.AddDays(-30).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), "a recorded start is never moved");
    }

    [Fact]
    public async Task An_unreadable_dry_run_stamp_restarts_the_week_rather_than_ending_it()
    {
        UserConfig("""{ "dryRun": false }""");
        Directory.CreateDirectory(_sandbox.Paths.StateDirectory);
        File.WriteAllText(DryRunWindow.File(_sandbox.Paths), "{ not json");

        var result = await Engine(Action("A10")).ExecuteAsync(Run(RunTrigger.Timer, "A10"), CancellationToken.None);

        Statuses(result).Should().Equal("A10:dryRun");
        Done(result).Detail.DryRunReason.Should().Contain("could not be read, so the week restarts");
    }

    [Fact]
    public async Task A_heavy_action_is_deferred_while_the_cpu_is_busy_or_a_build_runs_and_the_deferral_says_why()
    {
        UserConfig("""{ "dryRun": false }""");
        var heavy = new ScriptedAction("A7", _journal) { Idle = IdleRule.Always };

        _sandbox.Load(3.9, 3.5, 3.0, cpus: 4);
        var busyCpu = await Engine(heavy).ExecuteAsync(Run(RunTrigger.Cli, "A7"), CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(1));
        _sandbox.Load(0.2, 0.2, 0.2, cpus: 4);
        Process(321, "dotnet test src/x.csproj");
        var building = await Engine(heavy).ExecuteAsync(Run(RunTrigger.Cli, "A7"), CancellationToken.None);
        Directory.Delete(_sandbox.Paths.DistroPath("/proc/321"), recursive: true);
        _clock.Advance(TimeSpan.FromSeconds(1));
        var idle = await Engine(heavy).ExecuteAsync(Run(RunTrigger.Cli, "A7"), CancellationToken.None);

        Statuses(busyCpu).Should().Equal("A7:deferred");
        Done(busyCpu).Detail.Actions[0].Reason.Should().Contain("CPU 97.5 % over the last 5 min is not below idle.cpuPercent 20 %", "busy is the highest average up to the window (E7.S0 review C5): load1 3.9 on 4 CPUs");
        Statuses(building).Should().Equal("A7:deferred");
        Done(building).Detail.Actions[0].Reason.Should().Contain("a build is running (dotnet test src/x.csproj)");
        Statuses(idle).Should().Equal("A7:ran");
        History().Select(h => h.Actions.Single().Status).Should().Equal("deferred", "deferred", "ran");
    }

    [Fact]
    public async Task An_urgent_preview_skips_the_idle_gate_and_nothing_else_while_its_neighbour_is_deferred()
    {
        UserConfig("""{ "dryRun": false }""");
        _sandbox.Load(3.9, 3.9, 3.9, cpus: 4);
        _sandbox.Write("/var/lib/wsl-care/first-timer-run.json", "{\"schemaVersion\":1,\"at\":\"2026-09-01T00:00:00+00:00\"}");
        var urgent = new ScriptedAction("A2", _journal) { Idle = IdleRule.Always, Urgent = "event: scripted shortage" };
        var heavy = new ScriptedAction("A15", _journal) { Idle = IdleRule.Always };

        var result = await Engine(urgent, heavy).ExecuteAsync(Run(RunTrigger.Timer, "A15", "A2"), CancellationToken.None);
        var refused = await Engine(new ScriptedAction("A2", _journal) { Idle = IdleRule.Always, Urgent = "event", Refusal = "scripted refusal" })
            .ExecuteAsync(Run(RunTrigger.Cli, "A2"), CancellationToken.None);

        Statuses(result).Should().Equal("A15:deferred", "A2:ran");
        Done(result).Detail.Actions[1].Reason.Should().Be("ran at once, without waiting for idle: event: scripted shortage");
        Statuses(refused).Should().Equal(["A2:refused"], "an event skips the idle gate only, never a refusal");
    }

    [Fact]
    public async Task An_action_sees_which_earlier_actions_of_the_same_run_ran()
    {
        var seen = new List<string>();
        var first = new ScriptedAction("A1", _journal);
        var skipped = new ScriptedAction("A3", _journal) { Fires = false };
        var second = new ScriptedAction("A2", _journal) { OnPreview = c => seen.AddRange(new[] { "A1", "A3" }.Where(id => c.RanEarlier(ActionId.Find(id)!))) };
        UserConfig("""{ "dryRun": false }""");
        _sandbox.Write("/var/lib/wsl-care/first-timer-run.json", "{\"schemaVersion\":1,\"at\":\"2026-09-01T00:00:00+00:00\"}");

        await Engine(first, skipped, second).ExecuteAsync(Run(RunTrigger.Timer, "A1", "A2", "A3"), CancellationToken.None);

        seen.Should().Equal(["A1"], "A1 ran before A2; A3's trigger did not fire (and it runs before A1 anyway)");
    }

    [Fact]
    public async Task A_timer_only_idle_rule_does_not_hold_back_a_button_and_an_unread_cpu_figure_defers()
    {
        UserConfig("""{ "dryRun": false }""");
        _sandbox.Load(3.9, 3.9, 3.9, cpus: 4);
        var timerOnly = new ScriptedAction("A1", _journal) { Idle = IdleRule.TimerOnly };

        var button = await Engine(timerOnly).ExecuteAsync(Run(RunTrigger.Cli, "A1"), CancellationToken.None);
        File.Delete(_sandbox.Paths.DistroPath("/proc/loadavg"));
        _clock.Advance(TimeSpan.FromSeconds(1));
        var unread = await Engine(new ScriptedAction("A15", _journal) { Idle = IdleRule.Always }).ExecuteAsync(Run(RunTrigger.Cli, "A15"), CancellationToken.None);

        Statuses(button).Should().Equal("A1:ran");
        Statuses(unread).Should().Equal("A15:deferred");
        Done(unread).Detail.Actions[0].Reason.Should().Contain("CPU use is unknown");
    }

    [Fact]
    public async Task A_dead_run_s_running_json_is_swept_with_an_interrupted_record_and_the_new_run_goes_ahead()
    {
        PlantRunning(pid: 999, heartbeatAge: TimeSpan.FromMinutes(3));

        var result = await Engine(Action("A10")).ExecuteAsync(Run(RunTrigger.Cli, "A10"), CancellationToken.None);

        Statuses(result).Should().Equal("A10:ran");
        Done(result).Detail.Notes.Should().Contain(n => n.Contains("swept the running.json of run", StringComparison.Ordinal) && n.Contains("pid 999 is gone", StringComparison.Ordinal));
        var swept = History().First();
        swept.Outcome.Should().Be(RunOutcome.Interrupted);
        swept.Reason.Should().Contain("pid 999 is gone").And.Contain("it was on A4").And.Contain("last heartbeat");
        swept.Actions.Select(a => a.Id).Should().Equal("A5", "A4");
    }

    [Fact]
    public async Task A_reused_pid_with_a_different_start_is_mismatched_and_swept_never_mistaken_for_the_run()
    {
        _processes.Alive(999, OwnStart.AddHours(-5));
        PlantRunning(pid: 999, heartbeatAge: TimeSpan.FromSeconds(1), processStart: OwnStart.AddDays(-1));

        var result = await Engine(Action("A10")).ExecuteAsync(Run(RunTrigger.Cli, "A10"), CancellationToken.None);

        Statuses(result).Should().Equal("A10:ran");
        History().First().Reason.Should().Contain("pid 999 is a different process now");
    }

    [Fact]
    public async Task A_live_run_with_a_stale_heartbeat_is_wedged_nothing_runs_and_nothing_is_touched()
    {
        _processes.Alive(999, OwnStart.AddHours(-1));
        var planted = PlantRunning(pid: 999, heartbeatAge: TimeSpan.FromSeconds(31), processStart: OwnStart.AddHours(-1));

        var result = await Engine(Action("A10")).ExecuteAsync(Run(RunTrigger.Cli, "A10"), CancellationToken.None);

        result.Should().BeOfType<ActResult.Wedged>().Which.Reason.Should().Contain("is wedged: pid 999 is alive but its heartbeat is 31 s old").And.Contain("nothing was killed");
        _journal.Should().BeEmpty();
        File.ReadAllText(RunningState.File(_sandbox.Paths)).Should().Be(planted, "a wedged run's state is left exactly as it is");
        History().Should().BeEmpty();
    }

    [Fact]
    public async Task A_live_run_with_a_fresh_heartbeat_is_busy_even_when_the_lock_was_free()
    {
        _processes.Alive(999, OwnStart.AddHours(-1));
        PlantRunning(pid: 999, heartbeatAge: TimeSpan.FromSeconds(4), processStart: OwnStart.AddHours(-1));

        var result = await Engine(Action("A10")).ExecuteAsync(Run(RunTrigger.Cli, "A10"), CancellationToken.None);

        result.Should().BeOfType<ActResult.Busy>().Which.Reason.Should().Contain("is acting although the run lock was free");
        _journal.Should().BeEmpty();
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{ "schemaVersion": 1 }""")]
    public async Task A_running_json_that_never_parses_is_its_own_unreadable_state_never_a_wedged_run_and_nothing_runs(string content)
    {
        Directory.CreateDirectory(_sandbox.Paths.StateDirectory);
        File.WriteAllText(RunningState.File(_sandbox.Paths), content);
        var pauses = new List<TimeSpan>();

        var result = await new ActionEngine(Context(Action("A10")) with { RunningRetry = RunningReadRetry.Default with { Pause = pauses.Add } })
            .ExecuteAsync(Run(RunTrigger.Cli, "A10"), CancellationToken.None);

        result.Should().BeOfType<ActResult.StateUnreadable>().Which.Reason.Should().Contain("does not parse").And.Contain(RunningState.FileName);
        pauses.Should().Equal(Enumerable.Repeat(TimeSpan.FromMilliseconds(100), 3), "three more reads, 100 ms apart, before the verdict");
        _journal.Should().BeEmpty();
    }

    [Fact]
    public async Task A_running_json_caught_mid_replace_is_read_again_and_the_dead_run_it_names_is_swept()
    {
        var planted = PlantRunning(pid: 999, heartbeatAge: TimeSpan.FromMinutes(3));
        var files = new FirstReadTorn(_sandbox.Files, RunningState.File(_sandbox.Paths), planted[..(planted.Length / 2)]);
        var context = Context(Action("A10")) with { Files = files, RunningRetry = RunningReadRetry.Default with { Pause = _ => { } } };

        var result = await new ActionEngine(context).ExecuteAsync(Run(RunTrigger.Cli, "A10"), CancellationToken.None);

        Done(result).Detail.Notes.Should().Contain(n => n.Contains("swept the running.json of run", StringComparison.Ordinal), "one torn read decides nothing");
        Statuses(result).Should().Equal("A10:ran");
    }

    /// <summary>The first read of one file returns half of it — a reader racing the writer's replace — and every later read the truth.</summary>
    private sealed class FirstReadTorn(IFileSystem inner, string path, string torn) : DelegatingFileSystem(inner)
    {
        private bool _torn;

        public override FileReadResult ReadFile(string file)
        {
            if (file == path && !_torn)
            {
                _torn = true;
                return new FileReadResult.Content(Encoding.UTF8.GetBytes(torn));
            }

            return base.ReadFile(file);
        }
    }

    [Fact]
    public async Task A_dead_run_that_had_already_recorded_itself_is_removed_without_a_second_history_line()
    {
        var first = Done(await Engine(Action("A10")).ExecuteAsync(Run(RunTrigger.Cli, "A10"), CancellationToken.None));
        PlantRunning(pid: 999, heartbeatAge: TimeSpan.FromMinutes(1), runId: first.Detail.RunId);
        _clock.Advance(TimeSpan.FromSeconds(5));

        var second = Done(await Engine(Action("A10")).ExecuteAsync(Run(RunTrigger.Cli, "A10"), CancellationToken.None));

        second.Detail.Notes.Should().Contain(n => n.Contains("had already recorded itself", StringComparison.Ordinal));
        History().Count(h => h.RunId == first.Detail.RunId).Should().Be(1);
    }

    [Fact]
    public async Task While_another_run_holds_the_lock_an_act_refuses_at_once_and_writes_nothing()
    {
        var held = (ExclusiveLock.Held)RunLock.TryTake(_sandbox.Paths, _sandbox.Files);
        using (held.Handle)
        {
            var result = await Engine(Action("A10")).ExecuteAsync(Run(RunTrigger.Cli, "A10"), CancellationToken.None);

            result.Should().BeOfType<ActResult.Busy>().Which.Reason.Should().Contain("another run holds the run lock");
        }

        _journal.Should().BeEmpty();
        File.Exists(RunningState.File(_sandbox.Paths)).Should().BeFalse();
        History().Should().BeEmpty();
    }

    [Fact]
    public async Task A_full_run_started_while_an_act_holds_the_lock_is_busy_one_lock_for_both()
    {
        CollectResult? collect = null;
        var probing = new ScriptedAction("A10", _journal)
        {
            OnRun = async _ =>
            {
                var context = new CollectContext(_sandbox.Paths, _sandbox.Files, new RecordingCommandRunner(), _clock, new FakeProbe(_sandbox.Paths.Side, _clock), ConfigLoader.Load(_sandbox.Paths, _sandbox.Files), 77, RunTrigger.Timer);
                collect = await CollectRun.RunAsync(context, CancellationToken.None);
                return new ActionRun(0, 0, "x", null, null, [], [], string.Empty);
            },
        };

        await Engine(probing).ExecuteAsync(Run(RunTrigger.Cli, "A10"), CancellationToken.None);

        collect!.Recording.Should().Be(Recording.Busy, "collect and act take the SAME lock; the second one refuses");
        collect.Reason.Should().Contain("another run is in progress");
    }

    [Fact]
    public async Task A_busy_lock_holder_that_is_wedged_is_reported_as_wedged()
    {
        _processes.Alive(999, OwnStart.AddHours(-1));
        PlantRunning(pid: 999, heartbeatAge: TimeSpan.FromMinutes(2), processStart: OwnStart.AddHours(-1));
        var held = (ExclusiveLock.Held)RunLock.TryTake(_sandbox.Paths, _sandbox.Files);
        using (held.Handle)
        {
            var result = await Engine(Action("A10")).ExecuteAsync(Run(RunTrigger.Cli, "A10"), CancellationToken.None);

            result.Should().BeOfType<ActResult.Wedged>();
        }
    }

    [Fact]
    public async Task A_signal_mid_run_records_the_run_as_interrupted_and_removes_running_json()
    {
        using var signal = new CancellationTokenSource();
        var stopping = new ScriptedAction("A5", _journal)
        {
            OnRun = async _ =>
            {
                await signal.CancelAsync();
                signal.Token.ThrowIfCancellationRequested();
                return new ActionRun(0, 0, "x", null, null, [], [], string.Empty);
            },
        };

        var act = () => Engine(stopping, Action("A10")).ExecuteAsync(Run(RunTrigger.Cli, "A5", "A10"), signal.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _journal.Should().NotContain("run A10");
        History().Single().Outcome.Should().Be(RunOutcome.Interrupted);
        File.Exists(RunningState.File(_sandbox.Paths)).Should().BeFalse();
    }

    private ActRunDetail RecordedDetail() =>
        JsonSerializer.Deserialize(File.ReadAllBytes(RunDetailStore.Absolute(_sandbox.Paths, History().Single().DetailPath)), WslCareJsonContext.Default.ActRunDetail)!;

    /// <summary>E6.S0 review D2: the interrupted record names the action that was IN FLIGHT and every requested action that
    /// never ran — before, it held neither, so nobody could tell what a cut-off run had been doing.</summary>
    [Fact]
    public async Task A_signal_mid_action_records_the_action_in_flight_and_the_ones_never_run_as_interrupted()
    {
        using var signal = new CancellationTokenSource();
        var stopping = new ScriptedAction("A5", _journal)
        {
            OnRun = async _ =>
            {
                await signal.CancelAsync();
                signal.Token.ThrowIfCancellationRequested();
                return new ActionRun(0, 0, "x", null, null, [], [], string.Empty);
            },
        };

        var act = () => Engine(stopping, Action("A10")).ExecuteAsync(Run(RunTrigger.Cli, "A5", "A10"), signal.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        var detail = RecordedDetail();
        detail.Actions.Select(a => $"{a.Id}:{a.Status}").Should().Equal("A5:interrupted", "A10:interrupted");
        detail.Actions[0].Reason.Should().Contain("cut off by a signal while it ran");
        detail.Actions[1].Reason.Should().Contain("not run");
        History().Single().Actions.Select(a => $"{a.Id}:{a.Status}").Should().Equal("A5:interrupted", "A10:interrupted");
    }

    /// <summary>E6.S0 review D2: an action that was cut off but knows what it had already removed (A4 / A5's confirmed
    /// batches) records it — the deletions are real, the history counts them, and the run stops there.</summary>
    [Fact]
    public async Task An_action_cut_off_with_confirmed_removals_records_them_as_interrupted_and_the_run_stops()
    {
        using var signal = new CancellationTokenSource();
        var partial = new ScriptedAction("A5", _journal)
        {
            OnRun = async _ =>
            {
                await signal.CancelAsync();
                return new ActionRun(2, 300, "measured", null, null, [new ActionItem("container", "a", 100), new ActionItem("container", "b", 200)], [], string.Empty)
                {
                    Interrupted = true,
                    NotRemoved = [new ActionItem("container", "c", 50, "unknown: cut off mid-command")],
                };
            },
        };

        var act = () => Engine(partial, Action("A10")).ExecuteAsync(Run(RunTrigger.Cli, "A5", "A10"), signal.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _journal.Should().NotContain("run A10");
        var a5 = RecordedDetail().Actions[0];
        a5.Status.Should().Be(ActionStatus.Interrupted);
        a5.Run!.Removed.Should().HaveCount(2);
        History().Single().Actions[0].Should().Match<ActionRecord>(r => r.Status == ActionStatus.Interrupted && r.Count == 2 && r.FreedBytes == 300);
    }

    [Fact]
    public async Task The_heartbeat_keeps_running_json_fresh_while_an_action_runs()
    {
        var beaten = DateTimeOffset.MinValue;
        var slow = new ScriptedAction("A10", _journal)
        {
            OnRun = async _ =>
            {
                _clock.Advance(TimeSpan.FromSeconds(10));
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (beaten < _clock.GetUtcNow() && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(20);

                    // Through the product's reader, which shares write and delete: a raw File.ReadAllBytes denied the
                    // heartbeat's atomic replace on Windows and, read mid-replace, threw out of this action under load —
                    // the flaky failure "beaten was 0001-01-01" (2026-10-03). A read that lands mid-replace is retried.
                    if (_sandbox.Files.ReadFile(RunningState.File(_sandbox.Paths)) is FileReadResult.Content content)
                    {
                        beaten = JsonSerializer.Deserialize(content.Bytes, WslCareJsonContext.Default.RunningFile)!.HeartbeatAt;
                    }
                }

                return new ActionRun(0, 0, "x", null, null, [], [], string.Empty);
            },
        };

        await new ActionEngine(Context(slow) with { HeartbeatPeriod = TimeSpan.FromMilliseconds(20) }).ExecuteAsync(Run(RunTrigger.Cli, "A10"), CancellationToken.None);

        beaten.Should().Be(FixedTimeProvider.DefaultNow.AddSeconds(10), "the heartbeat rewrites running.json on its own while the action is busy");
    }

    [Fact]
    public async Task A_preview_takes_no_lock_and_writes_nothing()
    {
        var result = await Engine(Action("A5"), Action("A10")).PreviewAsync(new ActRequest([ActionId.Find("A10")!, ActionId.Find("A5")!], RunTrigger.Cli, Execute: false), CancellationToken.None);

        result.Should().BeOfType<ActResult.Previewed>().Which.Actions.Select(a => $"{a.Id}:{a.Status}").Should().Equal("A5:previewed", "A10:previewed");
        _journal.Should().Equal("preview A5", "preview A10");
        Directory.Exists(_sandbox.Paths.StateDirectory).Should().BeFalse("a preview touches no state");
        File.Exists(_sandbox.Paths.RunLockFile).Should().BeFalse("a preview takes no lock");
    }

    [Fact]
    public async Task A_user_scoped_action_refuses_when_the_target_user_is_ambiguous_and_the_machine_scoped_ones_still_run()
    {
        _sandbox.Write("/etc/passwd", "root:x:0:0::/root:/bin/bash\nme:x:1000:1000::/home/me:/bin/bash\nann:x:1001:1001::/home/ann:/bin/bash\n");

        var result = await Engine(new ScriptedAction("A8", _journal) { Scope = CommandScope.User }, Action("A10")).ExecuteAsync(Run(RunTrigger.Cli, "A8", "A10"), CancellationToken.None);

        Statuses(result).Should().Equal("A8:refused", "A10:ran");
        Done(result).Detail.Actions[0].Reason.Should().Contain("no target user: 2 login accounts (me, ann)");
        Done(result).Detail.TargetUser.Found.Should().BeFalse();
    }

    [Fact]
    public async Task An_action_of_the_other_side_and_a_preview_refusal_are_named_and_skipped()
    {
        var windowsOnly = new ScriptedAction("A12", _journal) { Sides = [Core.Hosting.HostSide.Windows] };
        var refusing = new ScriptedAction("A4", _journal) { Refusal = "Docker 22 is older than 23: A4 refuses" };

        var result = await Engine(windowsOnly, refusing).ExecuteAsync(Run(RunTrigger.Cli, "A12", "A4"), CancellationToken.None);

        Statuses(result).Should().Equal("A4:refused", "A12:skipped");
        Done(result).Detail.Actions[0].Reason.Should().Be("Docker 22 is older than 23: A4 refuses");
        Done(result).Detail.Actions[1].Reason.Should().Contain("A12 runs on the Windows host side; this is the WSL binary");
    }

    [Fact]
    public async Task An_invalid_configuration_layer_skips_every_action_as_observe_only()
    {
        UserConfig("{ \"journal\": { \"keepDays\": 0 } }");

        var result = await Engine(Action("A10")).ExecuteAsync(Run(RunTrigger.Cli, "A10"), CancellationToken.None);

        Statuses(result).Should().Equal("A10:skipped");
        Done(result).Detail.Outcome.Should().Be(RunOutcome.ObserveOnly);
        _journal.Should().BeEmpty();
    }

    /// <summary>A <c>running.json</c> another run left; returns what was written.</summary>
    private string PlantRunning(int pid, TimeSpan heartbeatAge, DateTimeOffset? processStart = null, RunId? runId = null)
    {
        var now = _clock.GetUtcNow();
        var file = new RunningFile(1, runId ?? RunId.New(now.AddMinutes(-10), pid), RunTrigger.Timer, ["A5", "A4"], "A4", pid, processStart ?? OwnStart, now.AddMinutes(-10), now - heartbeatAge, RunKind.Act);
        Directory.CreateDirectory(_sandbox.Paths.StateDirectory);
        var json = JsonSerializer.Serialize(file, WslCareJsonContext.Default.RunningFile);
        File.WriteAllText(RunningState.File(_sandbox.Paths), json, new UTF8Encoding(false));
        return json;
    }

    /// <summary>A process in the sandbox's /proc that the fast probe reads (status, stat, cmdline).</summary>
    private void Process(int pid, string commandLine)
    {
        var name = commandLine.Split(' ')[0];
        _sandbox.Write($"/proc/{pid}/status", $"Name:\t{name}\nState:\tS (sleeping)\nPPid:\t1\nUid:\t1000\t1000\t1000\t1000\nRssAnon:\t100 kB\nRssShmem:\t0 kB\n");
        _sandbox.Write($"/proc/{pid}/stat", $"{pid} ({name}) S 1 {pid} {pid} 0 -1 0 0 0 0 0 50 50 0 0 20 0 1 0 100 0 0\n");
        _sandbox.Write($"/proc/{pid}/cgroup", "0::/user.slice\n");
        File.WriteAllBytes(_sandbox.Paths.DistroPath($"/proc/{pid}/cmdline"), Encoding.UTF8.GetBytes(commandLine.Replace(' ', '\0') + "\0"));
    }

    [Fact]
    public async Task An_action_whose_tool_is_not_installed_is_skipped_with_the_reason_never_run_and_never_failed()
    {
        // E3.S2: a skip (A8 / A17 without their tool) is neither a refusal nor a failure, on a button and on the timer alike.
        var result = await Engine(new ScriptedAction("A8", _journal) { Skip = "npm is not installed for me" }).ExecuteAsync(Run(RunTrigger.Manual, "A8"), CancellationToken.None);

        Statuses(result).Should().Equal("A8:skipped");
        Done(result).Detail.Actions.Single().Reason.Should().Be("npm is not installed for me");
        _journal.Should().Equal("preview A8");
    }
}
