using System.Text.Json;

using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Files;
using WslCare.Core.History;
using WslCare.Core.Json;
using WslCare.Core.Records;
using WslCare.Core.Status;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.History;

/// <summary>
/// <c>runs show &lt;runId&gt;</c> (plan §15j M3) over a sandbox: the history decides first (done with its full detail —
/// every removed and not-removed object, the commands and their exits — refused, interrupted), then <c>running.json</c>
/// (running; a dead process is interrupted, never "running"), then the request folder (queued); nothing naming the run is
/// unknown. Read-only: nothing is swept.
/// </summary>
public sealed class RunShowTests : IDisposable
{
    private const int Pid = 4343;
    private static readonly DateTimeOffset Now = FixedTimeProvider.DefaultNow;
    private static readonly RunningReadRetry NoWait = new(3, TimeSpan.Zero, _ => { });

    private readonly SandboxHost _sandbox = new("runs-show");

    public void Dispose() => _sandbox.Dispose();

    private RunShowReport Show(RunId runId, IProcessTable? processes = null) =>
        RunShow.Read(_sandbox.Paths, _sandbox.Files, processes ?? new FakeProcessTable(), Now, NoWait, runId);

    private static ActionOutcome A4Ran() =>
        new("A4", "A4 summary", ActionStatus.Ran, "ran", null, new ActionRun(
            2, 2_000, "measured", 9_000, 7_000,
            [new ActionItem("volume", "aaa", 1_000), new ActionItem("volume", "bbb", 1_000)],
            [new ActionCommandRecord("docker volume rm <names>", "docker volume rm aaa bbb ccc", "exited", 1, "Error: volume ccc is in use")],
            "one volume was in use")
        {
            NotRemoved = [new ActionItem("volume", "ccc", 500, "in use since the preview")],
            Notes = ["sightings recorded"],
        });

    private RunId ActRun(RunOutcome outcome, ActionOutcome action, DateTimeOffset at)
    {
        var id = RunId.New(at, 9);
        var detail = new ActRunDetail(1, id, RunTrigger.Manual, at, at.AddSeconds(30), false, "act", outcome, "wsl", "a button never dry-runs", new TargetUserReport(true, "me", "/home/me", "test"), [action], ["a note"]);
        RunDetailStore.Write(_sandbox.Paths, _sandbox.Files, id, JsonSerializer.SerializeToUtf8Bytes(detail, WslCareJsonContext.Default.ActRunDetail));
        Line(new RunRecord(1, id, RunTrigger.Manual, at, at.AddSeconds(30), outcome, [ActionRecords.Of(action)], null) { Detail = RunDetailStore.RelativePath(id), DryRun = false });
        return id;
    }

    private void Line(RunRecord record) => new RunRecordWriter(_sandbox.Paths, _sandbox.Files).Append(record);

    [Fact]
    public void A_recorded_run_is_done_with_its_history_line_and_its_full_detail_removed_not_removed_commands_and_exits()
    {
        var id = ActRun(RunOutcome.Failed, A4Ran(), Now.AddHours(-1));

        var show = Show(id);

        show.SchemaVersion.Should().Be(1);
        show.State.Should().Be(RunShowState.Done);
        show.Run!.Outcome.Should().Be("failed");
        show.Run.FreedBytes.Should().Be(2_000);
        show.DetailState.Should().Be("present");
        show.Detail!.Kind.Should().Be("act");
        var a4 = show.Detail.Actions.Should().ContainSingle().Subject;
        a4.Run!.Removed.Select(i => i.Name).Should().Equal("aaa", "bbb");
        a4.Run.NotRemoved.Should().ContainSingle().Which.Note.Should().Be("in use since the preview");
        a4.Run.Commands.Should().ContainSingle().Which.Exit.Should().Be(1);
        show.Detail.Notes.Should().Equal("a note");
    }

    [Fact]
    public void A_full_runs_detail_answers_its_timer_pass_actions()
    {
        var at = Now.AddHours(-2);
        var id = RunId.New(at, 9);
        var pass = new TimerPass(true, string.Empty, true, "the dry-run week", null, [new ActionOutcome("A10", "A10 summary", ActionStatus.DryRun, "dry run", null, null)], ["pass note"], RunOutcome.Completed);
        RunDetailStore.Write(_sandbox.Paths, _sandbox.Files, id, JsonSerializer.SerializeToUtf8Bytes(new TimerPassView(pass), WslCareJsonContext.Default.TimerPassView));
        Line(new RunRecord(1, id, RunTrigger.Timer, at, at, RunOutcome.Completed, [], RunKind.Collect) { Detail = RunDetailStore.RelativePath(id) });

        var show = Show(id);

        show.State.Should().Be(RunShowState.Done);
        show.Detail!.Kind.Should().Be("collect");
        show.Detail.DryRun.Should().BeTrue();
        show.Detail.Actions.Select(a => $"{a.Id}:{a.Status}").Should().Equal("A10:dryRun");
        show.Detail.Notes.Should().Equal("pass note");
    }

    [Fact]
    public void A_line_whose_detail_is_gone_is_done_with_the_detail_lost_and_no_detail()
    {
        var at = Now.AddHours(-3);
        var id = RunId.New(at, 9);
        Line(new RunRecord(1, id, RunTrigger.Manual, at, at, RunOutcome.Completed, [new ActionRecord("A4", 1, 5) { Status = ActionStatus.Ran }], RunKind.Act) { Detail = RunDetailStore.RelativePath(id) });

        var show = Show(id);

        show.State.Should().Be(RunShowState.Done);
        show.DetailState.Should().Be("lost");
        show.Detail.Should().BeNull();
    }

    [Fact]
    public void An_interrupted_line_is_interrupted_and_a_refused_line_is_refused_each_with_its_reason()
    {
        var at = Now.AddHours(-4);
        var swept = RunId.New(at, 21);
        var refused = RunId.New(at, 22);
        Line(new RunRecord(1, swept, RunTrigger.Manual, at, at, RunOutcome.Interrupted, [], null) { Reason = "swept: pid 21 is gone" });
        Line(new RunRecord(1, refused, RunTrigger.Manual, at, at, RunOutcome.Refused, [], null) { Reason = "another run holds the run lock" });

        Show(swept).Should().Match<RunShowReport>(s => s.State == RunShowState.Interrupted && s.Reason == "swept: pid 21 is gone" && s.DetailState == "none");
        Show(refused).Should().Match<RunShowReport>(s => s.State == RunShowState.Refused && s.Reason == "another run holds the run lock");
    }

    [Fact]
    public void A_run_holding_running_json_with_a_live_process_is_running_with_its_running_block()
    {
        var file = new RunningFile(1, RunId.New(Now.AddMinutes(-1), Pid), RunTrigger.Manual, ["A4"], "A4", Pid, Now.AddMinutes(-2), Now.AddMinutes(-1), Now.AddSeconds(-3), RunKind.Act);
        RunningState.Write(_sandbox.Paths, _sandbox.Files, file);

        var show = Show(file.RunId, new FakeProcessTable().Alive(Pid, Now.AddMinutes(-2)));

        show.State.Should().Be(RunShowState.Running);
        show.Running!.State.Should().Be(RunningStateName.Live);
        show.Running.Current.Should().Be("A4");
    }

    [Fact]
    public void A_run_holding_running_json_whose_process_is_gone_is_interrupted_never_running_and_is_not_swept()
    {
        var file = new RunningFile(1, RunId.New(Now.AddMinutes(-1), Pid), RunTrigger.Manual, ["A4"], "A4", Pid, Now.AddMinutes(-2), Now.AddMinutes(-1), Now.AddMinutes(-1), RunKind.Act);
        RunningState.Write(_sandbox.Paths, _sandbox.Files, file);

        var show = Show(file.RunId);

        show.State.Should().Be(RunShowState.Interrupted);
        show.Running!.State.Should().Be(RunningStateName.Dead);
        File.Exists(RunningState.File(_sandbox.Paths)).Should().BeTrue("runs show is a reader: the next root run sweeps it");
        File.Exists(RunHistory.File(_sandbox.Paths)).Should().BeFalse();
    }

    [Fact]
    public void A_run_named_only_by_a_request_is_queued_with_its_request_counted_not_repeated()
    {
        var id = RunId.New(Now, 31);
        var request = new RunRequestFile(1, id, "act", ["A4"], RunTrigger.Manual, Now) { Shown = [new string('a', 64), new string('b', 64)] };
        var path = RunRequests.File(_sandbox.Paths, id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(request, WslCareJsonContext.Default.RunRequestFile));

        var show = Show(id);

        show.State.Should().Be(RunShowState.Queued);
        show.Request.Should().BeEquivalentTo(new RunShowRequest("act", ["A4"], "manual", Now, 2));
    }

    [Fact]
    public void The_history_wins_a_recorded_run_whose_running_json_was_left_behind_is_done()
    {
        var id = ActRun(RunOutcome.Completed, A4Ran(), Now.AddMinutes(-1));
        RunningState.Write(_sandbox.Paths, _sandbox.Files, new RunningFile(1, id, RunTrigger.Manual, ["A4"], "A4", 9, Now.AddMinutes(-2), Now.AddMinutes(-1), Now.AddMinutes(-1), RunKind.Act));

        Show(id).State.Should().Be(RunShowState.Done);
    }

    /// <summary>E6.S0 review S2: a live run whose pid cannot be inspected (/proc mounted hidepid) is still THIS run's — runs
    /// show answers running with the unknown block, never "unknown / never existed".</summary>
    [Fact]
    public void A_holder_whose_pid_cannot_be_inspected_is_running_with_an_unknown_block_never_a_stranger()
    {
        var file = new RunningFile(1, RunId.New(Now.AddMinutes(-1), Pid), RunTrigger.Manual, ["A4"], "A4", Pid, Now.AddMinutes(-2), Now.AddMinutes(-1), Now.AddSeconds(-3), RunKind.Act);
        RunningState.Write(_sandbox.Paths, _sandbox.Files, file);

        var show = Show(file.RunId, new FakeProcessTable().Uninspectable(Pid, "hidepid"));

        show.State.Should().Be(RunShowState.Running);
        show.Running!.State.Should().Be(RunningStateName.Unknown);
    }

    /// <summary>E6.S0 review D4: states move request → running.json → history line, so they are READ in that order — a run
    /// that finished between the reads is found done, never "queued" from a stale request nor "unknown".</summary>
    [Fact]
    public void A_run_that_finishes_while_runs_show_reads_is_found_done_because_the_reads_follow_the_states()
    {
        var id = RunId.New(Now, 41);
        var request = RunRequests.File(_sandbox.Paths, id);
        Directory.CreateDirectory(Path.GetDirectoryName(request)!);
        File.WriteAllBytes(request, JsonSerializer.SerializeToUtf8Bytes(new RunRequestFile(1, id, "act", ["A10"], RunTrigger.Manual, Now), WslCareJsonContext.Default.RunRequestFile));
        var finishing = new FinishingFileSystem(_sandbox.Files, request, () =>
        {
            File.Delete(request);
            Line(new RunRecord(1, id, RunTrigger.Manual, Now, Now, RunOutcome.Completed, [], null));
        });

        var show = RunShow.Read(_sandbox.Paths, finishing, new FakeProcessTable(), Now, NoWait, id);

        show.State.Should().Be(RunShowState.Done);
    }

    /// <summary>The real file system — except that right after the request is read, the run finishes (<paramref name="finish"/>).</summary>
    private sealed class FinishingFileSystem(IFileSystem inner, string request, Action finish) : DelegatingFileSystem(inner)
    {
        private bool _finished;

        public override FileReadResult ReadStateFile(string path, int maxBytes)
        {
            var read = base.ReadStateFile(path, maxBytes);
            if (path == request && !_finished)
            {
                _finished = true;
                finish();
            }

            return read;
        }
    }

    [Fact]
    public void Nothing_naming_the_run_is_unknown_with_a_reason()
    {
        var show = Show(RunId.New(Now.AddDays(-200), 1));

        show.State.Should().Be(RunShowState.Unknown);
        show.Reason.Should().Contain("90-day");
        show.Run.Should().BeNull();
    }
}
