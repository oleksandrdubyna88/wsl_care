using System.Text.Json;

using FluentAssertions;

using WslCare.Core.Actions.Engine;
using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.Core.Json;
using WslCare.Core.Records;
using WslCare.Core.Status;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Status;

/// <summary>
/// <c>status --json</c>'s <c>running</c> block (plan §15j M3): every state staged over a sandbox — none, queued (from the
/// request files E6.S1 will write), live, wedged, dead, unknown, unreadable — read through a file system that FAILS the test
/// on any write, move or delete: status is unprivileged and never sweeps, so a dead run is reported, not cleaned.
/// </summary>
public sealed class RunningReportsTests : IDisposable
{
    private const int Pid = 4242;
    private static readonly DateTimeOffset Now = FixedTimeProvider.DefaultNow;
    private static readonly DateTimeOffset ProcessStart = Now.AddMinutes(-3);
    private static readonly RunningReadRetry NoWait = new(3, TimeSpan.Zero, _ => { });

    private readonly SandboxHost _sandbox = new("running-report");

    public void Dispose() => _sandbox.Dispose();

    private RunningReport Read(IProcessTable processes) => Read(processes, new NoWrites(_sandbox.Files));

    private RunningReport Read(IProcessTable processes, IFileSystem files) =>
        RunningReports.Read(_sandbox.Paths, files, processes, Now, NoWait, History());

    private HistoryRead History() => RunHistory.Read(_sandbox.Paths, _sandbox.Files);

    // ---------- the E6.S0 review round ----------

    /// <summary>S2: a live run whose pid cannot be inspected (/proc mounted hidepid) still names its run — runs show must be
    /// able to match it, never answer "never existed".</summary>
    [Fact]
    public void An_uninspectable_holder_keeps_its_run_id_and_publishes_no_pid()
    {
        Stage(Running(Now.AddSeconds(-2)));

        var report = Read(new FakeProcessTable().Uninspectable(Pid, "hidepid"));

        report.State.Should().Be(RunningStateName.Unknown);
        report.RunId.Should().Be(RunId.New(Now.AddMinutes(-2), Pid).Text);
        report.Pid.Should().BeNull("a pid is published only where a person may act on it: live or wedged (M4)");
    }

    /// <summary>S3: a dead run's pid is gone or is ANOTHER process now — publishing it invites stopping a stranger.</summary>
    [Fact]
    public void A_dead_run_publishes_no_pid()
    {
        Stage(Running(Now.AddSeconds(-2)));

        var report = Read(new FakeProcessTable().Alive(Pid, ProcessStart.AddHours(1)));

        report.State.Should().Be(RunningStateName.Dead);
        report.Pid.Should().BeNull();
    }

    /// <summary>D1: the wall clock stepped an hour (this machine logs hundreds of clock changes per 4 h; A16 itself steps it):
    /// .NET's Process.StartTime moved with it, but the boot id and the boot-relative start ticks did not — a live run stays
    /// live, and its heartbeat age is the MONOTONIC one, not the wall-clock one.</summary>
    [Fact]
    public void A_wall_clock_step_neither_kills_a_live_run_nor_ages_its_heartbeat_when_the_boot_and_its_ticks_match()
    {
        Stage(Running(Now.AddHours(-2)) with { StartTicks = 123_456, BootId = "boot-a", HeartbeatMonotonicMs = 1_000_000 });

        var report = Read(new FakeProcessTable().Alive(Pid, ProcessStart.AddHours(1), startTicks: 123_456).Booted("boot-a", 1_003_000));

        report.State.Should().Be(RunningStateName.Live, report.Reason);
        report.HeartbeatAgeSeconds.Should().Be(3, "1 003 000 − 1 000 000 ms on the monotonic clock; the wall clock says two hours");
    }

    [Fact]
    public void Within_one_boot_other_start_ticks_are_another_process_and_another_boot_is_a_dead_run()
    {
        Stage(Running(Now.AddSeconds(-2)) with { StartTicks = 123_456, BootId = "boot-a", HeartbeatMonotonicMs = 1_000_000 });

        Read(new FakeProcessTable().Alive(Pid, ProcessStart, startTicks: 999).Booted("boot-a", 1_001_000)).State.Should().Be(RunningStateName.Dead);
        Read(new FakeProcessTable().Alive(Pid, ProcessStart, startTicks: 123_456).Booted("boot-b", 5_000)).State.Should().Be(RunningStateName.Dead);
    }

    /// <summary>D3: a run that recorded itself and died before removing its file is not "dead, nothing recorded it yet" — its
    /// history line says how it ended; status reports none, naming the left-over file.</summary>
    [Fact]
    public void A_dead_holder_whose_run_has_a_history_line_is_none_naming_its_left_over_file()
    {
        var file = Running(Now.AddMinutes(-1));
        Stage(file);
        new RunRecordWriter(_sandbox.Paths, _sandbox.Files).Append(new RunRecord(1, file.RunId, RunTrigger.Manual, file.StartedAt, Now, RunOutcome.Completed, [], null));

        var report = Read(new FakeProcessTable());

        report.State.Should().Be(RunningStateName.None);
        report.Reason.Should().Contain("recorded itself as completed").And.Contain("only its running.json is left");
    }

    /// <summary>Retro round over PR #11 (consultation 9064487b): a fresh request beside a LEFT-OVER running.json — a dead holder,
    /// recorded or not — read <c>none</c> / <c>dead</c>, because the requests were asked only when nothing held running.json; a
    /// second <c>--detach</c> was then allowed and install.sh's wait saw an idle machine. A pending request is in flight; an idle
    /// or dead holder never hides it.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_fresh_request_beside_a_dead_holder_is_queued_whether_or_not_the_holder_recorded_itself(bool holderRecorded)
    {
        var file = Running(Now.AddMinutes(-1));
        Stage(file);
        if (holderRecorded)
        {
            new RunRecordWriter(_sandbox.Paths, _sandbox.Files).Append(new RunRecord(1, file.RunId, RunTrigger.Manual, file.StartedAt, Now, RunOutcome.Completed, [], null));
        }

        var request = RequestFor(Now, 77);
        Request(request);

        var report = Read(new FakeProcessTable());

        report.State.Should().Be(RunningStateName.Queued, report.Reason);
        report.RunId.Should().Be(request.RunId.Text);
    }

    /// <summary>The companion: a LIVE holder still comes first — a request queued behind an acting run reads <c>live</c>.</summary>
    [Fact]
    public void A_request_behind_a_live_holder_still_reads_live()
    {
        Stage(Running(Now.AddSeconds(-1)));
        Request(RequestFor(Now, 77));

        Read(new FakeProcessTable().Alive(Pid, ProcessStart)).State.Should().Be(RunningStateName.Live);
    }

    /// <summary>D4: a request that vanished as status read it has just become a run — running.json is read again, and the run
    /// it now names is reported, never "none".</summary>
    [Fact]
    public void A_request_that_vanished_while_status_read_it_sends_status_back_to_running_json()
    {
        var live = Running(Now.AddSeconds(-1));
        var racing = new RaceFileSystem(_sandbox.Files, RunningState.File(_sandbox.Paths), () => Stage(live));

        var report = Read(new FakeProcessTable().Alive(Pid, ProcessStart), racing);

        report.State.Should().Be(RunningStateName.Live);
    }

    /// <summary>The real file system — except that the FIRST read of <paramref name="watched"/> finds nothing and, right after
    /// it, <paramref name="move"/> runs: the request became a run between the two reads.</summary>
    private sealed class RaceFileSystem(IFileSystem inner, string watched, Action move) : DelegatingFileSystem(inner)
    {
        private bool _moved;

        public override FileReadResult ReadFile(string path)
        {
            if (path != watched || _moved)
            {
                return base.ReadFile(path);
            }

            _moved = true;
            move();
            return new FileReadResult.Missing();
        }
    }

    private RunningFile Running(DateTimeOffset heartbeat, int pid = Pid) =>
        new(1, RunId.New(Now.AddMinutes(-2), pid), RunTrigger.Manual, ["A5", "A4"], "A4", pid, ProcessStart, Now.AddMinutes(-2), heartbeat, RunKind.Act);

    private void Stage(RunningFile file) => RunningState.Write(_sandbox.Paths, _sandbox.Files, file);

    private void Request(RunRequestFile request) => Write(RunRequests.File(_sandbox.Paths, request.RunId), JsonSerializer.SerializeToUtf8Bytes(request, WslCareJsonContext.Default.RunRequestFile));

    private static void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static RunRequestFile RequestFor(DateTimeOffset created, int pid) =>
        new(1, RunId.New(created, pid), "act", ["A4"], RunTrigger.Manual, created) { Shown = [new string('a', 64)] };

    [Fact]
    public void Nothing_in_flight_is_none_with_no_reason()
    {
        var report = Read(new FakeProcessTable());

        report.State.Should().Be(RunningStateName.None);
        report.Reason.Should().BeNull();
        report.RunId.Should().BeNull();
    }

    [Fact]
    public void A_live_run_names_its_run_actions_current_action_trigger_pid_and_a_fresh_heartbeat_age()
    {
        Stage(Running(Now.AddSeconds(-4)));

        var report = Read(new FakeProcessTable().Alive(Pid, ProcessStart));

        report.State.Should().Be(RunningStateName.Live);
        report.RunId.Should().Be(RunId.New(Now.AddMinutes(-2), Pid).Text);
        report.Actions.Should().Equal("A5", "A4");
        report.Current.Should().Be("A4");
        report.Trigger.Should().Be("manual");
        report.Pid.Should().Be(Pid);
        report.HeartbeatAgeSeconds.Should().Be(4);
        report.Reason.Should().Contain("is acting (A4)");
    }

    /// <summary>E7.S2b/S2c review C-H2: a heartbeat beats on a timer whether or not the run moves — a hung 9p read keeps it fresh
    /// forever. A run that made no step for running.noProgressMinutes reads WEDGED with a fresh heartbeat, so act --stop can end it.</summary>
    [Fact]
    public void A_beating_run_that_made_no_step_for_the_watchdog_window_is_wedged()
    {
        Stage(Running(Now.AddSeconds(-2)) with { ProgressAt = Now.AddMinutes(-25) });
        var stuck = Read(new FakeProcessTable().Alive(Pid, ProcessStart));
        Stage(Running(Now.AddSeconds(-2)) with { ProgressAt = Now.AddMinutes(-19) });
        var moving = Read(new FakeProcessTable().Alive(Pid, ProcessStart));
        Stage(Running(Now.AddSeconds(-2)) with { ProgressAt = null });
        var older = Read(new FakeProcessTable().Alive(Pid, ProcessStart));

        stuck.State.Should().Be(RunningStateName.Wedged);
        stuck.Reason.Should().Contain("made no step for 25 min").And.Contain("act --stop");
        moving.State.Should().Be(RunningStateName.Live, "19 min without a step is inside the default 20");
        older.State.Should().Be(RunningStateName.Live, "a file from a writer before the watchdog is judged by its heartbeat alone");
    }

    [Fact]
    public void A_live_process_whose_heartbeat_is_stale_is_wedged_and_nothing_is_killed()
    {
        Stage(Running(Now.AddMinutes(-5)));

        var report = Read(new FakeProcessTable().Alive(Pid, ProcessStart));

        report.State.Should().Be(RunningStateName.Wedged);
        report.HeartbeatAgeSeconds.Should().Be(300);
        report.Reason.Should().Contain("nothing was killed");
    }

    [Fact]
    public void A_gone_process_is_dead_REPORTED_and_its_running_json_is_left_exactly_where_it_was_with_no_history_line()
    {
        Stage(Running(Now.AddMinutes(-5)));
        var before = File.ReadAllBytes(RunningState.File(_sandbox.Paths));

        var report = Read(new FakeProcessTable());

        report.State.Should().Be(RunningStateName.Dead);
        report.Reason.Should().Contain("pid 4242 is gone").And.Contain("next root run");
        File.ReadAllBytes(RunningState.File(_sandbox.Paths)).Should().Equal(before, "status never sweeps (plan §15j M3)");
        File.Exists(RunHistory.File(_sandbox.Paths)).Should().BeFalse("no interrupted line is written by a reader");
    }

    [Fact]
    public void A_pid_that_is_another_process_now_is_dead_too()
    {
        Stage(Running(Now.AddSeconds(-2)));

        var report = Read(new FakeProcessTable().Alive(Pid, ProcessStart.AddHours(1)));

        report.State.Should().Be(RunningStateName.Dead);
        report.Reason.Should().Contain("different process");
    }

    [Fact]
    public void An_uninspectable_pid_is_unknown_with_the_reason()
    {
        Stage(Running(Now.AddSeconds(-2)));

        var report = Read(new FakeProcessTable().Uninspectable(Pid, "access denied"));

        report.State.Should().Be(RunningStateName.Unknown);
        report.Reason.Should().Contain("access denied");
    }

    [Fact]
    public void A_running_json_that_does_not_parse_is_unreadable_naming_the_file_never_wedged()
    {
        Write(RunningState.File(_sandbox.Paths), "{ not json"u8.ToArray());

        var report = Read(new FakeProcessTable());

        report.State.Should().Be(RunningStateName.Unreadable);
        report.Reason.Should().Contain(RunningState.FileName).And.Contain("does not parse");
    }

    [Fact]
    public void A_request_with_nothing_running_is_queued_naming_the_oldest_and_how_many_wait()
    {
        var older = RequestFor(Now.AddSeconds(-20), 11);
        var newer = RequestFor(Now.AddSeconds(-5), 12);
        Request(newer);
        Request(older);

        var report = Read(new FakeProcessTable());

        report.State.Should().Be(RunningStateName.Queued);
        report.RunId.Should().Be(older.RunId.Text);
        report.Actions.Should().Equal("A4");
        report.Trigger.Should().Be("manual");
        report.QueuedAt.Should().Be(older.CreatedAt);
        report.Queued.Should().Be(2);
        report.Reason.Should().Contain("2 requests are waiting");
    }

    [Fact]
    public void A_live_run_wins_over_a_waiting_request()
    {
        Stage(Running(Now.AddSeconds(-1)));
        Request(RequestFor(Now.AddSeconds(-1), 13));

        Read(new FakeProcessTable().Alive(Pid, ProcessStart)).State.Should().Be(RunningStateName.Live);
    }

    [Fact]
    public void A_request_that_does_not_parse_with_nothing_else_in_flight_is_unreadable_naming_it_and_is_left_in_place()
    {
        var path = RunRequests.File(_sandbox.Paths, RunId.New(Now, 14));
        Write(path, "[]"u8.ToArray());

        var report = Read(new FakeProcessTable());

        report.State.Should().Be(RunningStateName.Unreadable);
        report.Reason.Should().Contain(path);
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public void A_request_filed_under_another_runs_name_is_not_taken_for_that_run()
    {
        var request = RequestFor(Now, 15);
        Write(RunRequests.File(_sandbox.Paths, RunId.New(Now, 16)), JsonSerializer.SerializeToUtf8Bytes(request, WslCareJsonContext.Default.RunRequestFile));

        var report = Read(new FakeProcessTable());

        report.State.Should().Be(RunningStateName.Unreadable);
        report.Reason.Should().Contain("is filed as");
    }

    [Fact]
    public void Files_in_the_request_folder_that_are_not_named_for_a_run_are_not_requests()
    {
        Write(Path.Combine(RunRequests.Directory(_sandbox.Paths), "notes.txt"), "x"u8.ToArray());
        Write(Path.Combine(RunRequests.Directory(_sandbox.Paths), ".tmp-123.json"), "x"u8.ToArray());

        Read(new FakeProcessTable()).State.Should().Be(RunningStateName.None);
    }

    /// <summary>The real file system for reads; any write, move, delete, lock or probe fails the test.</summary>
    private sealed class NoWrites(IFileSystem inner) : DelegatingFileSystem(inner)
    {
        public override void CreateDirectory(string path) => throw Refused(path);

        public override DeletionVerdict WriteFileAtomically(string path, ReadOnlySpan<byte> content, DeletionScope scope) => throw Refused(path);

        public override void AppendLine(string path, string line, TimeSpan lockTimeout) => throw Refused(path);

        public override DeletionVerdict DeleteFile(string path, DeletionScope scope) => throw Refused(path);

        public override DeletionVerdict DeleteDirectory(string path, DeletionScope scope) => throw Refused(path);

        public override DeletionVerdict MoveFile(string from, string to, DeletionScope scope) => throw Refused(from);

        public override DeletionVerdict MoveDirectory(string from, string to, DeletionScope scope) => throw Refused(from);

        public override DeletionVerdict RewriteLines(string path, Func<IReadOnlyList<string>, IReadOnlyList<string>> keep, DeletionScope scope, TimeSpan lockTimeout) => throw Refused(path);

        public override ExclusiveLock TryLockExclusive(string lockPath) => throw Refused(lockPath);

        public override WriteAccess ProbeWriteAccess(string directory) => throw Refused(directory);

        private static InvalidOperationException Refused(string path) => new($"a reader wrote, moved, removed or locked {path}");
    }
}
