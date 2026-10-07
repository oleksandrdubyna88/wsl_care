using System.Text.Json;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core;
using WslCare.Core.Actions;
using WslCare.Core.Actions.Engine;
using WslCare.Core.History;
using WslCare.Core.Json;
using WslCare.Core.Records;
using WslCare.Core.Status;
using WslCare.TestSupport;

namespace WslCare.Cli.Tests;

/// <summary>
/// E6.S0's read contract in-process (plan §15j): <c>status --json</c>'s <c>actions</c>, <c>capabilities</c>, <c>running</c>
/// and <c>lastCleanup</c>; <c>runs show &lt;runId&gt; [--json]</c>; the instant range of <c>logs</c> / <c>runs</c> — all
/// read-only, all <c>schemaVersion</c> 1. The distro's layout over a temporary root, on every OS.
/// </summary>
public sealed class ReadContractCommandTests : IDisposable
{
    private static readonly DateTimeOffset Now = FixedTimeProvider.DefaultNow;

    private readonly LinuxSandbox _sandbox = new("read-contract");

    public void Dispose() => _sandbox.Dispose();

    private CliHost Host(IProcessTable? processes = null) =>
        new(_sandbox.Paths, _sandbox.Files, new FixedTimeProvider(Now), new RecordingCommandRunner()) { Processes = processes ?? new FakeProcessTable() };

    private static StatusReport Status(string stdout) => JsonSerializer.Deserialize(stdout, WslCareJsonContext.Default.StatusReport)!;

    private static RunShowReport Show(string stdout) => JsonSerializer.Deserialize(stdout, WslCareJsonContext.Default.RunShowReport)!;

    private void Line(RunRecord record) => new RunRecordWriter(_sandbox.Paths, _sandbox.Files).Append(record);

    // ---------- status ----------

    [Fact]
    public void Status_names_this_sides_actions_in_execution_order_its_capabilities_no_running_run_and_no_cleanup_yet_at_schema_version_1()
    {
        var (exit, stdout, stderr) = CliRun.Over(Host(), "status", "--json");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        var report = Status(stdout);
        report.SchemaVersion.Should().Be(1, "every E6.S0 addition is additive (plan §15j m1)");
        report.Actions.Should().Equal(ActionId.ExecutionOrder.Where(id => ActionRegistry.Product.Find(id) is not null).Select(id => id.Text), "the distro's binary holds every built action, A13 not yet");
        report.Capabilities.Should().Equal(Capabilities.All);
        report.Running.Should().Be(new RunningReport(RunningStateName.None, null));
        report.LastCleanup!.Available.Should().BeFalse();
        report.LastCleanup.Reason.Should().Be(LastCleanups.NoneYet);
    }

    [Fact]
    public void Status_reports_a_dead_run_and_the_last_cleanup_without_sweeping_or_writing_anything()
    {
        var file = new RunningFile(1, RunId.New(Now.AddMinutes(-9), 555), RunTrigger.Manual, ["A4"], "A4", 555, Now.AddMinutes(-10), Now.AddMinutes(-9), Now.AddMinutes(-8), RunKind.Act);
        RunningState.Write(_sandbox.Paths, _sandbox.Files, file);
        Line(new RunRecord(1, RunId.New(Now.AddHours(-1), 7), RunTrigger.Timer, Now.AddHours(-1), Now.AddHours(-1), RunOutcome.Completed, [new ActionRecord("A10", 4, 4_000) { Status = ActionStatus.Ran }], RunKind.Collect));
        var before = Directory.GetFiles(_sandbox.Paths.StateDirectory, "*", SearchOption.AllDirectories).Select(f => (f, File.GetLastWriteTimeUtc(f), new FileInfo(f).Length)).ToList();

        var (exit, stdout, stderr) = CliRun.Over(Host(), "status", "--json");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        var report = Status(stdout);
        report.Running!.State.Should().Be(RunningStateName.Dead);
        report.Running.RunId.Should().Be(file.RunId.Text);
        report.LastCleanup.Should().Be(new LastCleanupReport(true, null, RunId.New(Now.AddHours(-1), 7).Text, Now.AddHours(-1), "timer", 4_000, 4));
        Directory.GetFiles(_sandbox.Paths.StateDirectory, "*", SearchOption.AllDirectories).Select(f => (f, File.GetLastWriteTimeUtc(f), new FileInfo(f).Length))
            .Should().Equal(before, "status never sweeps a dead run and writes nothing (plan §15j M3)");
    }

    [Fact]
    public void Status_as_text_says_which_run_is_in_flight_and_the_last_cleanup()
    {
        var (_, stdout, _) = CliRun.Over(Host(), "status");

        stdout.Should().Contain("running: none").And.Contain("last cleanup: none yet");
    }

    // ---------- runs show ----------

    [Fact]
    public void Runs_show_parses_a_run_id_and_json_and_refuses_anything_that_is_not_one()
    {
        CommandLine.Parse(["runs", "show", "20261002T120000Z-123", "--json"]).Should().Be(new Request.RunsShow(RunId.TryParse("20261002T120000Z-123")!, Json: true));
        CommandLine.Parse(["runs", "show", "20261002T120000Z-123"]).Should().Be(new Request.RunsShow(RunId.TryParse("20261002T120000Z-123")!, Json: false));
        CommandLine.Parse(["runs", "show"]).Should().BeOfType<Request.Failed>().Which.Message.Should().Contain("<runId>");
        CommandLine.Parse(["runs", "show", "../../etc/passwd"]).Should().BeOfType<Request.Failed>().Which.Message.Should().Contain("is not a run id");
        CommandLine.Parse(["runs", "show", "20261002T120000Z-123", "--period", "today"]).Should().BeOfType<Request.Failed>();
        CommandLine.Parse(["runs", "show", "20261002T120000Z-1", "20261002T120000Z-2"]).Should().BeOfType<Request.Failed>();
    }

    [Fact]
    public void Runs_show_of_a_recorded_run_answers_done_with_its_line_and_of_an_unknown_one_answers_unknown_both_exit_0()
    {
        var id = RunId.New(Now.AddHours(-2), 8);
        Line(new RunRecord(1, id, RunTrigger.Manual, Now.AddHours(-2), Now.AddHours(-2), RunOutcome.Completed, [new ActionRecord("A4", 3, 3_000) { Status = ActionStatus.Ran }], RunKind.Act));

        var (doneExit, done, doneErr) = CliRun.Over(Host(), "runs", "show", id.Text, "--json");
        var (unknownExit, unknown, _) = CliRun.Over(Host(), "runs", "show", RunId.New(Now, 9).Text, "--json");

        doneExit.Should().Be((int)ExitCode.Ok, doneErr);
        Show(done).Should().Match<RunShowReport>(s => s.SchemaVersion == 1 && s.State == RunShowState.Done && s.Run!.FreedBytes == 3_000 && s.DetailState == "none");
        unknownExit.Should().Be((int)ExitCode.Ok);
        Show(unknown).State.Should().Be(RunShowState.Unknown);
    }

    [Fact]
    public void Runs_show_as_text_names_the_state_and_writes_nothing()
    {
        var (exit, stdout, _) = CliRun.Over(Host(), "runs", "show", RunId.New(Now, 9).Text);

        exit.Should().Be((int)ExitCode.Ok);
        stdout.Should().Contain("wsl-care runs show 20261002T120000Z-9: unknown");
        Directory.Exists(_sandbox.Paths.StateDirectory).Should().BeFalse("runs show is read-only");
    }

    /// <summary>Retro round over PR #11 (G0): the human form of a recorded run with its detail, line by line — pinned before
    /// <c>LogsCommand.ShowText</c> was split into helpers, so the refactor is held to the text it printed.</summary>
    [Fact]
    public void Runs_show_as_text_lists_each_action_with_what_it_removed_and_every_command_with_its_exit()
    {
        var id = RunId.New(Now.AddHours(-1), 12);
        static ActionCommandRecord Command(string display, string outcome, int? exit) => new("template", display, outcome, exit, string.Empty);
        var ran = new ActionRun(2, 1_500_000_000, "measured", null, null, [], [Command("journalctl --vacuum-time=30d", "exited", 0), Command("docker volume rm x", "timedOut", null)], string.Empty)
        {
            NotRemoved = [new ActionItem("volume", "in-use", null)],
        };
        var nothing = new ActionRun(0, null, "nothing to remove", null, null, [], [], string.Empty);
        var detail = new ActRunDetail(1, id, RunTrigger.Manual, Now.AddHours(-1), Now.AddHours(-1), false, "act", RunOutcome.Completed, "wsl", string.Empty, new TargetUserReport(true, "me", "/home/me", "test"),
            [
                new ActionOutcome("A10", "the journal", ActionStatus.Ran, "ran", null, ran),
                new ActionOutcome("A9", "apt's cache", ActionStatus.Refused, "no target user", null, null),
                new ActionOutcome("A4", "volumes", ActionStatus.Ran, "ran", null, nothing),
            ],
            []);
        RunDetailStore.Write(_sandbox.Paths, _sandbox.Files, id, JsonSerializer.SerializeToUtf8Bytes(detail, WslCareJsonContext.Default.ActRunDetail));
        Line(new RunRecord(1, id, RunTrigger.Manual, Now.AddHours(-1), Now.AddHours(-1), RunOutcome.Completed, [], RunKind.Act) { Detail = RunDetailStore.RelativePath(id) });

        var (exit, stdout, stderr) = CliRun.Over(Host(), "runs", "show", id.Text);

        exit.Should().Be((int)ExitCode.Ok, stderr);
        stdout.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).Should().Equal(
            $"wsl-care runs show {id}: done",
            "  A10               ran       2 removed, 1 not removed, freed 1.50 GB",
            "      journalctl --vacuum-time=30d -> exited 0",
            "      docker volume rm x -> timedOut",
            "  A9                refused   no target user",
            "  A4                ran       0 removed, 0 not removed, freed 0.00 GB");
    }

    // ---------- the instant range ----------

    [Fact]
    public void Logs_and_runs_parse_an_instant_range_and_refuse_it_beside_a_period_or_half_given()
    {
        CommandLine.Parse(["logs", "--from", "2026-10-02T00:00:00+03:00", "--to", "2026-10-03T00:00:00+03:00", "--json"]).Should().Be(
            new Request.Logs(string.Empty, null, Json: true) { From = "2026-10-02T00:00:00+03:00", To = "2026-10-03T00:00:00+03:00" });
        CommandLine.Parse(["runs", "--to", "2026-10-03T00:00:00Z", "--from", "2026-10-02T00:00:00Z"]).Should().Be(
            new Request.Runs(string.Empty, Json: false) { From = "2026-10-02T00:00:00Z", To = "2026-10-03T00:00:00Z" });
        CommandLine.Parse(["logs", "--period", "today", "--from", "2026-10-02T00:00:00Z", "--to", "2026-10-03T00:00:00Z"]).Should().BeOfType<Request.Failed>().Which.Message.Should().Contain("not both");
        CommandLine.Parse(["logs", "--from", "2026-10-02T00:00:00Z"]).Should().BeOfType<Request.Failed>().Which.Message.Should().Contain("--to");
        CommandLine.Parse(["runs", "--to", "2026-10-02T00:00:00Z"]).Should().BeOfType<Request.Failed>().Which.Message.Should().Contain("--from");
    }

    [Fact]
    public void Runs_over_an_instant_range_answers_the_runs_started_inside_it_and_a_bad_instant_is_a_usage_refusal()
    {
        Line(new RunRecord(1, RunId.New(Now.AddHours(-12).AddMinutes(-1), 1), RunTrigger.Timer, Now.AddHours(-12).AddMinutes(-1), Now, RunOutcome.Completed, [], RunKind.Collect));
        Line(new RunRecord(1, RunId.New(Now.AddHours(-11), 2), RunTrigger.Timer, Now.AddHours(-11), Now, RunOutcome.Completed, [], RunKind.Collect));

        var (exit, stdout, stderr) = CliRun.Over(Host(), "runs", "--from", "2026-10-02T00:00:00Z", "--to", "2026-10-02T12:00:00Z", "--json");
        var (badExit, _, badErr) = CliRun.Over(Host(), "runs", "--from", "2026-10-02T00:00:00", "--to", "2026-10-02T12:00:00Z", "--json");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        var runs = JsonSerializer.Deserialize(stdout, WslCareJsonContext.Default.RunsReport)!;
        runs.Runs.Select(r => r.RunId).Should().Equal(RunId.New(Now.AddHours(-11), 2).Text);
        runs.Period.FromInstant.Should().Be(new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero));
        badExit.Should().Be((int)ExitCode.Usage);
        badErr.Should().Contain("offset");
    }
}
