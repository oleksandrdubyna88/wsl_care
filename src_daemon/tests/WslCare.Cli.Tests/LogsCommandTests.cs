using System.Text.Json;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Actions;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Files;
using WslCare.Core.Json;
using WslCare.Core.History;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Cli.Tests;

/// <summary>
/// <c>logs [--period …] [--action &lt;A#&gt;] [--json]</c> and <c>runs [--period …] [--json]</c> in-process (E3.S3): the parse,
/// today as the default, every exit code (2 a bad period, 4 an unreadable history), the JSON answers, read-only (nothing
/// written, no lock), and the text form.
/// </summary>
public sealed class LogsCommandTests : IDisposable
{
    private static readonly DateTimeOffset Today = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

    private readonly SandboxHost _sandbox = new("logs-cli");

    public LogsCommandTests()
    {
        Append(Today.AddHours(-1), [new ActionRecord("A4", 2, 2_000_000_000) { Status = ActionStatus.Ran }]);
        Append(Today.AddHours(3), [new ActionRecord("A10", 1, 500_000_000) { Status = ActionStatus.Ran }, new ActionRecord("A1", 0, 0) { Status = ActionStatus.Deferred }]);
    }

    public void Dispose() => _sandbox.Dispose();

    private void Append(DateTimeOffset at, IReadOnlyList<ActionRecord> actions) =>
        new RunRecordWriter(_sandbox.Paths, _sandbox.Files).Append(new RunRecord(1, RunId.New(at, 3), RunTrigger.Timer, at, at, RunOutcome.Completed, actions, RunKind.Collect) { DryRun = false });

    [Fact]
    public void The_verbs_parse_their_period_action_and_json_and_default_to_today()
    {
        CommandLine.Parse(["logs"]).Should().Be(new Request.Logs("today", null, Json: false));
        CommandLine.Parse(["logs", "--json", "--action", "A10", "--period", "2026-10-01..2026-10-02"]).Should().Be(new Request.Logs("2026-10-01..2026-10-02", ActionId.Find("A10"), Json: true));
        CommandLine.Parse(["runs", "--period", "yesterday"]).Should().Be(new Request.Runs("yesterday", Json: false));
        CommandLine.Parse(["logs", "--detail", "--json"]).Should().Be(new Request.Logs("today", null, Json: true, Detail: true));
    }

    [Theory]
    [InlineData("logs", "--period")]
    [InlineData("logs", "--period", "today", "--period", "yesterday")]
    [InlineData("logs", "--action", "A99")]
    [InlineData("logs", "--json", "--json")]
    [InlineData("logs", "--detail", "--detail")]
    [InlineData("runs", "--detail")]
    [InlineData("runs", "--action", "A10")]
    [InlineData("runs", "show")]
    public void Anything_else_is_a_usage_refusal(params string[] argv) =>
        CommandLine.Parse(argv).Should().BeOfType<Request.Failed>();

    [Fact]
    public void Logs_json_answers_today_with_its_sums_and_writes_nothing()
    {
        var before = Directory.GetFiles(_sandbox.Paths.StateDirectory, "*", SearchOption.AllDirectories).Length;

        var (exit, stdout, stderr) = CliRun.Over(_sandbox, "logs", "--json");

        exit.Should().Be(0, stderr);
        var report = JsonSerializer.Deserialize(stdout, WslCareJsonContext.Default.LogsReport)!;
        report.Period.Should().Be(new PeriodReport("today", "2026-10-02", "2026-10-02"));
        report.FreedBytes.Should().Be(500_000_000, "yesterday's 23:00 run is not today's");
        report.Runs.WithCleanup.Should().Be(1);
        Directory.GetFiles(_sandbox.Paths.StateDirectory, "*", SearchOption.AllDirectories).Length.Should().Be(before, "logs is read-only: no lock file, nothing written");
    }

    [Fact]
    public void Runs_text_lists_each_run_with_its_acting_statuses()
    {
        var (exit, stdout, _) = CliRun.Over(_sandbox, "runs", "--period", "2026-10-01..2026-10-02");

        exit.Should().Be(0);
        var lines = CliRun.Lines(stdout);
        lines[0].Should().Be("wsl-care runs 2026-10-01..2026-10-02 (2026-10-01..2026-10-02 UTC): 2");
        lines[1].Should().Contain("A4:ran");
        lines[2].Should().Contain("A10:ran").And.Contain("A1:deferred").And.Contain("0.50 GB");
    }

    [Fact]
    public void Logs_text_names_the_totals_and_each_action()
    {
        var (exit, stdout, _) = CliRun.Over(_sandbox, "logs", "--period", "yesterday");

        exit.Should().Be(0);
        stdout.Should().Contain("freed 2.00 GB, 2 objects").And.Contain("A4").And.Contain("1 with a cleanup");
    }

    [Fact]
    public void A_failed_actions_figures_count_and_its_failure_is_printed_beside_them()
    {
        Append(Today.AddHours(5), [new ActionRecord("A4", 386, 59_000_000_000) { Status = ActionStatus.Failed, Failure = "eeee: not confirmed" }]);

        var (exit, stdout, _) = CliRun.Over(_sandbox, "logs");

        exit.Should().Be(0);
        stdout.Should().Contain("freed 59.50 GB, 387 objects").And.Contain("failed 1x").And.Contain("FAILED: eeee: not confirmed");
    }

    [Fact]
    public void A_period_that_is_not_one_of_the_shapes_is_exit_2_naming_them()
    {
        var (exit, stdout, stderr) = CliRun.Over(_sandbox, "logs", "--period", "last-week");

        exit.Should().Be(2);
        stdout.Should().BeEmpty();
        stderr.Should().Contain("yyyy-MM-dd..yyyy-MM-dd");
    }

    [Fact]
    public void A_history_that_cannot_be_read_is_exit_4_with_the_reason()
    {
        var (exit, stdout, stderr) = CliRun.Over(_sandbox, new UnreadableHistory(_sandbox.Files), "runs", "--json");

        exit.Should().Be(4);
        JsonSerializer.Deserialize(stdout, WslCareJsonContext.Default.RunsReport)!.Problem.Should().Contain("permission denied (test)");
        stderr.Should().Contain("permission denied (test)");
    }

    private sealed class UnreadableHistory(IFileSystem inner) : DelegatingFileSystem(inner)
    {
        public override FileReadResult ReadFile(string path) =>
            path.EndsWith(RunRecordWriter.FileName, StringComparison.Ordinal) ? new FileReadResult.Unreadable("permission denied (test)") : base.ReadFile(path);
    }
}
