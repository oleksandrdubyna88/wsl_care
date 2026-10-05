using System.Text;
using System.Text.Json;

using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Collect;
using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.History;
using WslCare.Core.Json;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Records;
using WslCare.Core.Status;
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

    private FakeProcessTable _table = new();

    private Task<IReadOnlyList<string>> Sweep(RunId? own = null) =>
        RequestSweep.ApplyAsync(_sandbox.Paths, _sandbox.Files, _runner, _table, Now, own);

    private const string ThisBoot = "boot-of-the-test";

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
        new RunRecordWriter(_sandbox.Paths, _sandbox.Files).Append(new RunRecord(1, request.RunId, RunTrigger.Manual, Now, Now, RunOutcome.Completed, [], null));

        var notes = await Sweep();

        Queued(request).Should().BeFalse();
        History().Should().ContainSingle("history first: the run's own line is the only one");
        notes.Should().ContainSingle().Which.Should().Contain("the run recorded itself");
        _runner.Requests.Should().BeEmpty("a recorded run's unit is never asked about");
    }

    [Fact]
    public async Task A_request_younger_than_its_grace_is_left_alone()
    {
        var request = Plant(Request(4, RequestSweep.Grace - TimeSpan.FromSeconds(1)));

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

        new RunRecordWriter(_sandbox.Paths, _sandbox.Files).Append(new RunRecord(1, recorded, RunTrigger.Timer, Now, Now, RunOutcome.Interrupted, [], RunKind.Collect));

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


    // ---------- the E6.S1 review round ----------

    /// <summary>D3: within one boot the MONOTONIC clock ages a request — a wall clock stepped two hours forward never sweeps a
    /// request its unit has not taken yet.</summary>
    [Fact]
    public async Task A_stamped_request_is_aged_by_the_monotonic_clock_not_a_wall_clock_stepped_forward()
    {
        _table = new FakeProcessTable().Booted(ThisBoot, 10_000_000);
        var request = Plant(Request(20, TimeSpan.FromHours(2)) with { BootId = ThisBoot, CreatedMonotonicMs = 10_000_000 - 20_000 });
        UnitAnswers(request, "ActiveState=inactive\n");

        (await Sweep()).Should().BeEmpty();

        Queued(request).Should().BeTrue("20 s on the monotonic clock is inside the grace, whatever the wall clock says");
        History().Should().BeEmpty();
        _runner.Requests.Should().BeEmpty();
    }

    /// <summary>D3: a wall clock stepped BACK does not keep a stale request queued — two monotonic minutes are two minutes.</summary>
    [Fact]
    public async Task A_stamped_request_past_its_monotonic_grace_is_swept_though_the_wall_clock_says_it_is_new()
    {
        _table = new FakeProcessTable().Booted(ThisBoot, 10_000_000);
        var request = Plant(Request(21, TimeSpan.Zero) with { BootId = ThisBoot, CreatedMonotonicMs = 10_000_000 - 120_000 });
        UnitAnswers(request, "ActiveState=inactive\n");

        await Sweep();

        Queued(request).Should().BeFalse();
        History().Should().ContainSingle().Which.Reason.Should().Contain("its request is 2 min old");
    }

    /// <summary>D3: a request of an earlier boot is stale at once (its unit cannot be running since) — still asked of systemd.</summary>
    [Fact]
    public async Task A_request_of_an_earlier_boot_is_stale_at_once_and_still_asked_of_systemd()
    {
        _table = new FakeProcessTable().Booted(ThisBoot, 5_000);
        var request = Plant(Request(22, TimeSpan.Zero) with { BootId = "an-earlier-boot", CreatedMonotonicMs = 4_000 });
        UnitAnswers(request, "ActiveState=inactive\n");

        await Sweep();

        Queued(request).Should().BeFalse();
        History().Should().ContainSingle().Which.Reason.Should().Contain("written in an earlier boot");
        _runner.Commands.Should().ContainSingle();
    }

    /// <summary>S2: a request stamped in the future is never held forever — it is past any grace, and its unit decides.</summary>
    [Fact]
    public async Task An_unstamped_request_claiming_a_future_creation_is_stale_not_held_forever()
    {
        var request = Plant(Request(23, -TimeSpan.FromDays(1)));
        UnitAnswers(request, "ActiveState=inactive\n");

        await Sweep();

        Queued(request).Should().BeFalse();
        History().Should().ContainSingle().Which.Reason.Should().Contain("in the future");
    }

    /// <summary>D4: the run recorded itself (here: refused) between the sweep's history snapshot and its look at the unit — the
    /// sweep reads the history again and adds no second terminal line.</summary>
    [Fact]
    public async Task A_run_that_records_itself_while_the_sweep_looks_gets_no_second_terminal_line()
    {
        var request = Plant(Request(24, TimeSpan.FromHours(1)));
        _runner.ScriptEffect(argv => argv.SequenceEqual(UnitCommands.Show(request.RunId).Argv), _ =>
        {
            new RunRecordWriter(_sandbox.Paths, _sandbox.Files).Append(new RunRecord(1, request.RunId, RunTrigger.Manual, Now, Now, RunOutcome.Refused, [], null) { Reason = "busy: the timer" });
            return RecordingCommandRunner.Exited(0, "ActiveState=inactive\n");
        });

        await Sweep();

        History().Should().ContainSingle().Which.Outcome.Should().Be(RunOutcome.Refused, "one run id, one terminal line");
        Queued(request).Should().BeFalse();
    }

    /// <summary>D5: a request that cannot be used is recorded refused naming why, then removed — it held the state unreadable and
    /// refused every detach forever.</summary>
    [Fact]
    public async Task An_unusable_request_is_recorded_refused_with_its_reason_and_removed()
    {
        var runId = RunId.New(Now, 25);
        var path = RunRequests.File(_sandbox.Paths, runId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $$"""{"schemaVersion":1,"runId":"{{runId}}","kind":"act","actions":["A99"],"trigger":"manual","createdAt":"2026-10-02T12:00:00+00:00"}""");

        await Sweep();

        File.Exists(path).Should().BeFalse();
        var line = History().Should().ContainSingle().Subject;
        line.RunId.Should().Be(runId);
        line.Outcome.Should().Be(RunOutcome.Refused);
        line.Reason.Should().Contain("could not be used").And.Contain("action");
    }

    /// <summary>S2: a request root never writes — a collect marked timer (whose full run ACTS), an act marked timer — is refused
    /// by the reader.</summary>
    [Theory]
    [InlineData("collect", "collect", "timer")]
    [InlineData("collect", "collect", "cli")]
    [InlineData("act", "A10", "timer")]
    public void A_request_with_a_trigger_root_never_writes_is_refused_by_the_reader(string kind, string action, string trigger)
    {
        var runId = RunId.New(Now, 26);
        var path = RunRequests.File(_sandbox.Paths, runId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $$"""{"schemaVersion":1,"runId":"{{runId}}","kind":"{{kind}}","actions":["{{action}}"],"trigger":"{{trigger}}","createdAt":"2026-10-02T12:00:00+00:00"}""");

        RunRequests.Find(_sandbox.Paths, _sandbox.Files, runId).Should().BeOfType<RunRequestRead.Bad>().Which.Why.Should().Contain("trigger");
    }

    /// <summary>D2: a signal during the collect's request sweep used to leave ZERO lines (the sweep ran outside the cut-off guard).</summary>
    [Fact]
    public async Task A_collect_cut_off_while_it_sweeps_the_request_folder_still_leaves_one_interrupted_line()
    {
        var stale = Plant(Request(27, TimeSpan.FromHours(1)));
        var runner = new RecordingCommandRunner().ScriptEffect(argv => argv.SequenceEqual(UnitCommands.Show(stale.RunId).Argv), _ => throw new OperationCanceledException("a signal mid-sweep"));
        var context = CollectContextFor(runner) with { InterruptCause = () => "SIGTERM" };

        var run = async () => await CollectRun.RunAsync(context, CancellationToken.None);

        await run.Should().ThrowAsync<OperationCanceledException>();
        History().Should().ContainSingle().Which.Reason.Should().Be("interrupted by SIGTERM while it swept the request folder: nothing was recorded but this line");
    }

    /// <summary>S1: a temporary file is never group or world writable while it is filled — created 0600, made 0644 after.</summary>
    [Fact]
    public void A_temporary_file_is_created_owner_only_and_made_readable_only_once_written()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("mode bits are the distro's: run in WSL or on the Linux legs");
            return;
        }

        var seen = new List<UnixFileMode>();
        var files = new PhysicalFileSystem(_sandbox.Paths, PhysicalFileSystem.ReadLinkTarget, (step, path) =>
        {
            if (step == AtomicWriteStep.TempWritten && OperatingSystem.IsLinux())
            {
                seen.Add(File.GetUnixFileMode(path));
            }
        });
        Directory.CreateDirectory(_sandbox.Paths.StateDirectory);

        RunRequests.Create(_sandbox.Paths, files, Request(28, TimeSpan.Zero)).Should().BeOfType<ExclusiveCreate.Created>();
        RunningState.Write(_sandbox.Paths, files, new RunningFile(1, RunId.New(Now, 28), RunTrigger.Manual, ["A10"], "A10", Pid, OwnStart, Now, Now, RunKind.Act));

        seen.Should().Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.GetUnixFileMode(RunningState.File(_sandbox.Paths)).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead, "the unprivileged status reads it");
    }

    // ---------- the coai E6 code round ----------

    /// <summary>#7: status is polled — the running block needs the OLDEST request and the count, so it reads ONE file, ordered and
    /// counted by name, never all 32 (up to 32 MiB) on every poll.</summary>
    [Fact]
    public void The_running_block_reads_only_the_oldest_request_however_many_are_queued()
    {
        for (var pid = 1; pid <= RunRequests.MaxQueued; pid++)
        {
            Plant(Request(pid, TimeSpan.FromSeconds(pid)));
        }

        var counting = new CountingStateReads(_sandbox.Files);

        var report = RunningReports.Read(_sandbox.Paths, counting, new FakeProcessTable(), Now, RunningReadRetry.Default, RunHistory.Read(_sandbox.Paths, _sandbox.Files));

        report.State.Should().Be(RunningStateName.Queued);
        report.Queued.Should().Be(RunRequests.MaxQueued);
        report.RunId.Should().Be(RunId.New(Now - TimeSpan.FromSeconds(RunRequests.MaxQueued), RunRequests.MaxQueued).Text, "the oldest by name (the run id's time)");
        counting.Reads.Should().Be(1);
    }

    [Fact]
    public void When_the_oldest_request_cannot_be_used_the_next_one_is_read_and_no_more()
    {
        var bad = RunId.New(Now.AddMinutes(-9), 1);
        var path = RunRequests.File(_sandbox.Paths, bad);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");
        var next = Plant(Request(2, TimeSpan.FromMinutes(5)));
        Plant(Request(3, TimeSpan.FromMinutes(1)));
        var counting = new CountingStateReads(_sandbox.Files);

        var report = RunningReports.Read(_sandbox.Paths, counting, new FakeProcessTable(), Now, RunningReadRetry.Default, RunHistory.Read(_sandbox.Paths, _sandbox.Files));

        report.State.Should().Be(RunningStateName.Queued);
        report.RunId.Should().Be(next.RunId.Text);
        report.Queued.Should().Be(3);
        counting.Reads.Should().Be(2);
    }

    /// <summary>#6: a request written in an EARLIER boot can never start (its job died with that boot) — the unprivileged status
    /// reports it dead with the reason (it stays read-only; the next root run's sweep writes the line), and runs show answers it
    /// interrupted, as it answers a dead holder.</summary>
    [Fact]
    public void A_request_of_an_earlier_boot_reports_dead_to_status_and_interrupted_to_runs_show()
    {
        var table = new FakeProcessTable().Booted(ThisBoot, 5_000);
        var request = Plant(Request(30, TimeSpan.FromSeconds(5)) with { BootId = "an-earlier-boot", CreatedMonotonicMs = 4_000 });

        var report = RunningReports.Read(_sandbox.Paths, _sandbox.Files, table, Now, RunningReadRetry.Default, RunHistory.Read(_sandbox.Paths, _sandbox.Files));
        var show = RunShow.Read(_sandbox.Paths, _sandbox.Files, table, Now, RunningReadRetry.Default, request.RunId);

        report.State.Should().Be(RunningStateName.Dead);
        report.Reason.Should().Be(RunningReports.EarlierBootReason);
        report.RunId.Should().Be(request.RunId.Text);
        show.State.Should().Be(RunShowState.Interrupted);
        show.Reason.Should().Be(RunningReports.EarlierBootReason);
        Queued(request).Should().BeTrue("status and runs show never write");
    }

    [Fact]
    public void A_request_of_this_boot_stays_queued()
    {
        var table = new FakeProcessTable().Booted(ThisBoot, 5_000);
        Plant(Request(31, TimeSpan.FromSeconds(5)) with { BootId = ThisBoot, CreatedMonotonicMs = 4_000 });

        RunningReports.Read(_sandbox.Paths, _sandbox.Files, table, Now, RunningReadRetry.Default, RunHistory.Read(_sandbox.Paths, _sandbox.Files))
            .State.Should().Be(RunningStateName.Queued);
    }

    private sealed class CountingStateReads(IFileSystem inner) : DelegatingFileSystem(inner)
    {
        public int Reads { get; private set; }

        public override FileReadResult ReadStateFile(string path, int maxBytes)
        {
            if (path.Contains(RunRequests.Folder, StringComparison.Ordinal))
            {
                Reads++;
            }

            return base.ReadStateFile(path, maxBytes);
        }
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
        var file = new RunningFile(1, runId, RunTrigger.Manual, ["A10"], "A10", pid, OwnStart, Now.AddMinutes(-10), Now.AddMinutes(-3), RunKind.Act);
        Directory.CreateDirectory(_sandbox.Paths.StateDirectory);
        File.WriteAllText(RunningState.File(_sandbox.Paths), JsonSerializer.Serialize(file, WslCareJsonContext.Default.RunningFile), new UTF8Encoding(false));
    }
}
