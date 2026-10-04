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
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Records;
using WslCare.Core.Systemd;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Actions;

/// <summary>
/// The Core half of a DETACHED run (E6.S1, plan §15j B2, §15k #1 / #2 / #8 / #15 / #18): the request written exclusively, the
/// ownership-checked request sweep (history first; a young request left alone; a unit with a queued job or an active state
/// still pending; otherwise ONE <c>interrupted</c> line and the request gone; the run's own request never swept), the stop
/// marker that turns a SIGKILLed run's sweep reason into the truth, and the engine running under a pre-allocated run id with
/// the request removed only once <c>running.json</c> stands.
/// </summary>
public sealed class DetachedRunTests : IDisposable
{
    private const int Pid = 4242;
    private static readonly DateTimeOffset Now = FixedTimeProvider.DefaultNow;
    private static readonly DateTimeOffset OwnStart = Now.AddMinutes(-1);

    private readonly LinuxSandbox _sandbox = new("detached");
    private readonly RecordingCommandRunner _runner = new() { Policy = CommandPolicy.Product };
    private readonly List<string> _journal = [];

    public DetachedRunTests()
    {
        _sandbox.Write("/etc/passwd", "root:x:0:0::/root:/bin/bash\nme:x:1000:1000::/home/me:/bin/bash\n");
        _sandbox.Load(0.1, 0.1, 0.1, cpus: 4);
    }

    public void Dispose() => _sandbox.Dispose();

    private static RunRequestFile Request(int pid, TimeSpan age) =>
        new(1, RunId.New(Now - age, pid), "act", ["A10"], RunTrigger.Manual, Now - age);

    private RunRequestFile Plant(RunRequestFile request)
    {
        RunRequests.Create(_sandbox.Paths, _sandbox.Files, request).Should().BeOfType<ExclusiveCreate.Created>();
        return request;
    }

    private bool Queued(RunRequestFile request) => File.Exists(RunRequests.File(_sandbox.Paths, request.RunId));

    private IReadOnlyList<RunRecord> History() => RunHistory.Read(_sandbox.Paths, _sandbox.Files).Records;

    private void UnitAnswers(RunRequestFile request, string showOutput, int exit = 0) =>
        _runner.Script(UnitCommands.Show(request.RunId).Argv, exit, showOutput, exit == 0 ? string.Empty : "Failed to connect to bus");

    private Task<IReadOnlyList<string>> Sweep(RunId? own = null) =>
        RequestSweep.ApplyAsync(_sandbox.Paths, _sandbox.Files, _runner, Now, own, CancellationToken.None);

    // ---------- the request, written exclusively (§15k #1 / #14) ----------

    [Fact]
    public void A_request_is_written_whole_and_a_second_writer_of_the_same_run_never_replaces_it()
    {
        var first = Plant(Request(1, TimeSpan.Zero));
        var original = File.ReadAllBytes(RunRequests.File(_sandbox.Paths, first.RunId));

        var second = RunRequests.Create(_sandbox.Paths, _sandbox.Files, first with { Actions = ["A9"] });

        second.Should().BeOfType<ExclusiveCreate.AlreadyExists>("the final name is linked, never renamed over");
        File.ReadAllBytes(RunRequests.File(_sandbox.Paths, first.RunId)).Should().Equal(original, "the first request stands byte for byte");
        RunRequests.Find(_sandbox.Paths, _sandbox.Files, first.RunId).Should().BeOfType<RunRequestRead.Parsed>().Which.File.Actions.Should().Equal("A10");
        Directory.EnumerateFiles(RunRequests.Directory(_sandbox.Paths)).Should().ContainSingle("no temporary sibling is left behind");
    }

    [Fact]
    public void A_request_folder_and_file_are_made_world_readable_and_only_owner_writable()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("mode bits are the distro's: run in WSL or on the Linux legs");
            return;
        }

        var request = Plant(Request(2, TimeSpan.Zero));

        File.GetUnixFileMode(RunRequests.Directory(_sandbox.Paths)).Should().Be(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute,
            "requests/ is 0755 (plan §15k #14)");
        File.GetUnixFileMode(RunRequests.File(_sandbox.Paths, request.RunId)).Should().Be(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead, "a request is 0644 — the unprivileged status reads it");
    }

    // ---------- the request sweep (§15k #2 / #15) ----------

    [Fact]
    public async Task A_request_whose_run_already_recorded_itself_only_loses_its_file_never_a_second_line()
    {
        var request = Plant(Request(3, TimeSpan.FromHours(1)));
        new RunRecordWriter(_sandbox.Paths, _sandbox.Files).Append(new RunRecord(1, request.RunId, RunTrigger.Manual, Now, Now, RunOutcome.Completed, []));

        var notes = await Sweep();

        Queued(request).Should().BeFalse();
        History().Should().ContainSingle("history first: the run's own line is the only one");
        notes.Should().ContainSingle().Which.Should().Contain("the run recorded itself");
        _runner.Requests.Should().BeEmpty("a recorded run's unit is never asked about");
    }

    [Fact]
    public async Task A_request_younger_than_fifteen_minutes_is_left_alone()
    {
        var request = Plant(Request(4, RequestSweep.StaleAfter - TimeSpan.FromSeconds(1)));

        (await Sweep()).Should().BeEmpty();

        Queued(request).Should().BeTrue();
        History().Should().BeEmpty();
        _runner.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("ActiveState=inactive\nJob=91\n")]
    [InlineData("ActiveState=activating\nJob=\n")]
    [InlineData("ActiveState=active\n")]
    [InlineData("ActiveState=deactivating\n")]
    public async Task An_old_request_whose_unit_has_a_queued_job_or_is_still_active_is_pending_and_kept(string showOutput)
    {
        var request = Plant(Request(5, TimeSpan.FromHours(1)));
        UnitAnswers(request, showOutput);

        (await Sweep()).Should().BeEmpty();

        Queued(request).Should().BeTrue("a queued start has no active state yet (plan §15k #2)");
        History().Should().BeEmpty();
        _runner.Commands.Should().Equal($"systemctl show --property=ActiveState --property=Job wsl-care-act@{request.RunId}.service");
    }

    [Theory]
    [InlineData("ActiveState=failed\nJob=\n", "failed")]
    [InlineData("ActiveState=inactive\nJob=0\n", "inactive")]
    [InlineData("", "unknown to systemd")]
    public async Task An_old_request_whose_unit_is_done_gets_one_interrupted_line_naming_why_then_goes(string showOutput, string state)
    {
        var request = Plant(Request(6, TimeSpan.FromMinutes(40)));
        UnitAnswers(request, showOutput);

        var notes = await Sweep();

        Queued(request).Should().BeFalse();
        var line = History().Should().ContainSingle().Subject;
        line.RunId.Should().Be(request.RunId);
        line.Outcome.Should().Be(RunOutcome.Interrupted);
        line.Reason.Should().Contain("swept: the detached run never recorded itself").And.Contain($"is {state} with no queued job").And.Contain("40 min old");
        line.Actions.Should().ContainSingle().Which.Should().Match<ActionRecord>(a => a.Id == "A10" && a.Status == ActionStatus.Interrupted);
        notes.Should().ContainSingle().Which.Should().Contain("recorded as interrupted");
    }

    [Fact]
    public async Task A_unit_whose_state_cannot_be_read_keeps_its_request_and_says_why()
    {
        var request = Plant(Request(7, TimeSpan.FromHours(1)));
        UnitAnswers(request, string.Empty, exit: 1);

        var notes = await Sweep();

        Queued(request).Should().BeTrue("it may still run");
        History().Should().BeEmpty();
        notes.Should().ContainSingle().Which.Should().Contain("could not be read").And.Contain("Failed to connect to bus");
    }

    [Fact]
    public async Task The_run_s_own_request_is_never_swept_however_old()
    {
        var request = Plant(Request(8, TimeSpan.FromHours(2)));
        UnitAnswers(request, "ActiveState=failed\n");

        (await Sweep(own: request.RunId)).Should().BeEmpty();

        Queued(request).Should().BeTrue();
        _runner.Requests.Should().BeEmpty();
    }

    // ---------- the stop marker (§15k #18) ----------

    [Fact]
    public void A_dead_run_whose_stop_was_asked_is_swept_with_the_stop_as_its_reason_and_the_marker_goes()
    {
        var runId = RunId.New(Now.AddMinutes(-10), 999);
        PlantRunning(runId, pid: 999);
        StopMarkers.Mark(_sandbox.Paths, _sandbox.Files, runId, "wsl-care-act@" + runId.Text + ".service", Now.AddMinutes(-2));

        RunningSweep.Apply(_sandbox.Paths, _sandbox.Files, new FakeProcessTable(), Now, RunningReadRetry.Default, RunId.New(Now, Pid), Pid);

        var line = History().Should().ContainSingle().Subject;
        line.Outcome.Should().Be(RunOutcome.Interrupted);
        line.Reason.Should().StartWith("stopped: act --stop asked systemd to stop it").And.Contain("did not exit within 90 s of SIGTERM").And.Contain("pid 999 is gone");
        StopMarkers.List(_sandbox.Paths, _sandbox.Files).Should().BeEmpty();
    }

    [Fact]
    public void A_dead_run_without_a_stop_marker_keeps_the_plain_swept_reason()
    {
        var runId = RunId.New(Now.AddMinutes(-10), 998);
        PlantRunning(runId, pid: 998);

        RunningSweep.Apply(_sandbox.Paths, _sandbox.Files, new FakeProcessTable(), Now, RunningReadRetry.Default, RunId.New(Now, Pid), Pid);

        History().Should().ContainSingle().Which.Reason.Should().StartWith("swept:");
    }

    [Fact]
    public async Task A_stop_marker_goes_once_its_run_has_a_line_or_after_a_day_and_a_fresh_one_stays()
    {
        var recorded = RunId.New(Now.AddMinutes(-5), 1);
        var old = RunId.New(Now.AddDays(-2), 2);
        var fresh = RunId.New(Now.AddMinutes(-5), 3);
        foreach (var id in new[] { recorded, old, fresh })
        {
            StopMarkers.Mark(_sandbox.Paths, _sandbox.Files, id, UnitCommands.TimerService, Now);
        }

        new RunRecordWriter(_sandbox.Paths, _sandbox.Files).Append(new RunRecord(1, recorded, RunTrigger.Timer, Now, Now, RunOutcome.Interrupted, []));

        var notes = await Sweep();

        StopMarkers.List(_sandbox.Paths, _sandbox.Files).Should().Equal(fresh);
        notes.Should().HaveCount(2);
    }

    // ---------- the engine under a pre-allocated run id ----------

    [Fact]
    public async Task A_detached_act_records_under_the_run_id_its_request_was_filed_under_and_hears_running_json_first()
    {
        var runId = RunId.New(Now.AddSeconds(-3), 77);
        var runningAtCallback = false;
        var sweptUnderLock = new List<RunId>();
        var request = new ActRequest([ActionId.Find("A10")!], RunTrigger.Manual, Execute: true)
        {
            RunId = runId,
            OnRunningWritten = () => runningAtCallback = RunningState.Read(_sandbox.Paths, _sandbox.Files, Processes(), Now) is RunningStatus.Live live && live.File.RunId == runId,
            UnderLock = (own, _) =>
            {
                sweptUnderLock.Add(own);
                return Task.FromResult<IReadOnlyList<string>>(["a sweep note"]);
            },
        };

        var result = await Engine().ExecuteAsync(request, CancellationToken.None);

        var done = result.Should().BeOfType<ActResult.Done>().Subject;
        done.Detail.RunId.Should().Be(runId, "the panel follows the run id --detach answered");
        History().Should().ContainSingle().Which.RunId.Should().Be(runId);
        runningAtCallback.Should().BeTrue("the request goes only once running.json stands (E6.S0 review round)");
        sweptUnderLock.Should().Equal(runId);
        done.Detail.Notes.Should().Contain("a sweep note");
    }

    [Fact]
    public async Task A_detached_act_that_meets_the_lock_never_hears_running_json_and_sweeps_nothing()
    {
        var heard = false;
        var swept = false;
        var request = new ActRequest([ActionId.Find("A10")!], RunTrigger.Manual, Execute: true)
        {
            RunId = RunId.New(Now, 78),
            OnRunningWritten = () => heard = true,
            UnderLock = (_, _) =>
            {
                swept = true;
                return Task.FromResult<IReadOnlyList<string>>([]);
            },
        };
        var held = (ExclusiveLock.Held)RunLock.TryTake(_sandbox.Paths, _sandbox.Files);
        ActResult result;
        using (held.Handle)
        {
            result = await Engine().ExecuteAsync(request, CancellationToken.None);
        }

        result.Should().BeOfType<ActResult.Busy>();
        heard.Should().BeFalse();
        swept.Should().BeFalse("the sweep runs under the lock only");
    }

    // ---------- a detached collect ----------

    [Fact]
    public async Task A_detached_collect_records_under_its_request_s_run_id_and_hears_running_json_first()
    {
        var runId = RunId.New(Now.AddSeconds(-2), 79);
        var heard = false;
        var context = CollectContextFor(new RecordingCommandRunner { Default = new CommandOutcome.FailedToStart("not installed in this test") }) with
        {
            RunId = runId,
            OnRunningWritten = () => heard = RunningState.Read(_sandbox.Paths, _sandbox.Files, Processes(), Now) is RunningStatus.Live live && live.File.RunId == runId,
        };

        var result = await CollectRun.RunAsync(context, CancellationToken.None);

        result.Recording.Should().Be(Recording.Recorded, result.Reason);
        result.Detail!.RunId.Should().Be(runId);
        History().Should().ContainSingle().Which.RunId.Should().Be(runId);
        heard.Should().BeTrue();
    }

    /// <summary>The E6.S0 durable review's item, built in E6.S1: a full run cut off by a signal DURING the measurement used to
    /// leave nothing — <c>running.json</c> went in the <c>finally</c> and <c>runs show</c> answered "unknown".</summary>
    [Fact]
    public async Task A_collect_cancelled_during_the_measurement_leaves_one_interrupted_line_naming_the_cause()
    {
        using var cancel = new CancellationTokenSource();
        var runner = new RecordingCommandRunner().ScriptEffect(_ => true, _ =>
        {
            cancel.Cancel();
            return new CommandOutcome.FailedToStart("not installed in this test");
        });
        var context = CollectContextFor(runner) with { InterruptCause = () => "SIGTERM" };

        var run = async () => await CollectRun.RunAsync(context, cancel.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
        var line = History().Should().ContainSingle().Subject;
        line.Outcome.Should().Be(RunOutcome.Interrupted);
        line.Reason.Should().Be("interrupted by SIGTERM during the measurement: nothing was recorded but this line");
        File.Exists(RunningState.File(_sandbox.Paths)).Should().BeFalse("its running.json still goes in the finally");
    }

    private CollectContext CollectContextFor(ICommandRunner runner) =>
        new(_sandbox.Paths, _sandbox.Files, runner, new FixedTimeProvider(), new FakeProbe(_sandbox.Paths.Side, new FixedTimeProvider()), ConfigLoader.Load(_sandbox.Paths, _sandbox.Files), Pid, RunTrigger.Manual)
        {
            Processes = Processes(),
        };

    private FakeProcessTable Processes() => new FakeProcessTable().Alive(Pid, OwnStart);

    private ActionEngine Engine() =>
        new(new EngineContext(_sandbox.Paths, _sandbox.Files, _runner, new FixedTimeProvider(), new LinuxProbe(_sandbox.Files, _sandbox.Paths, new FixedTimeProvider()),
            ConfigLoader.Load(_sandbox.Paths, _sandbox.Files), Processes(), Pid, new ActionRegistry([new ScriptedAction("A10", _journal)])));

    private void PlantRunning(RunId runId, int pid)
    {
        var file = new RunningFile(1, runId, RunTrigger.Manual, ["A10"], "A10", pid, OwnStart, Now.AddMinutes(-10), Now.AddMinutes(-3));
        Directory.CreateDirectory(_sandbox.Paths.StateDirectory);
        File.WriteAllText(RunningState.File(_sandbox.Paths), JsonSerializer.Serialize(file, WslCareJsonContext.Default.RunningFile), new UTF8Encoding(false));
    }
}
