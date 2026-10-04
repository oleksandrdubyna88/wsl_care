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

    private RunningReport Read(IProcessTable processes) =>
        RunningReports.Read(_sandbox.Paths, new NoWrites(_sandbox.Files), processes, Now, NoWait);

    private RunningFile Running(DateTimeOffset heartbeat, int pid = Pid) =>
        new(1, RunId.New(Now.AddMinutes(-2), pid), RunTrigger.Manual, ["A5", "A4"], "A4", pid, ProcessStart, Now.AddMinutes(-2), heartbeat);

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
