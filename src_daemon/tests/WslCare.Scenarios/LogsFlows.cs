using System.Text.Json;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Collect;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Json;
using WslCare.Core.History;
using WslCare.Core.Records;
using WslCare.Core.Systemd;
using WslCare.FakeTool;
using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>
/// E3.S3 end to end over the BUILT CLI: the TIMER's full run (<c>collect --timer</c>, as its unit starts it) measures,
/// then runs the action pass in its dry-run week and records <c>dryRun</c> results in its ONE run record — every fake call a
/// read; then <c>runs</c> and <c>logs</c> read that record back; and <c>logs</c> / <c>runs</c> over a seeded history (explicit
/// UTC dates, sums, a bad period). Nothing is written by <c>logs</c> / <c>runs</c>.
/// </summary>
public sealed class LogsFlows
{
    private static ScenarioHome TimerHome(string purpose)
    {
        // A journal above A10's 1 GiB trigger, scripted BEFORE the captured 407 MB answer (the first match wins).
        var home = CollectFlows.Captured(purpose, first: h =>
            h.Answer(new FakeAnswer(SystemdCommands.Journalctl, SystemdCommands.JournalDiskUsage.Arguments, 0, h.WriteFile("journal-1.5G.out", "Archived and active journals take up 1.5G in the file system.\n"), string.Empty)));
        if (OperatingSystem.IsLinux())
        {
            ProcfsFixture.CopyTo(home.SandboxRoot);
        }

        return home;
    }

    [Fact]
    public async Task The_timers_full_run_acts_after_measuring_in_its_dry_run_week_and_runs_and_logs_read_it_back()
    {
        using var home = TimerHome("timer-pass");

        var collect = await home.RunAsync("collect", "--timer", "--json");

        collect.Exit.Should().Be((int)ExitCode.Ok, collect.Stderr);
        var report = JsonSerializer.Deserialize(collect.Stdout, WslCareJsonContext.Default.CollectReport)!;
        var pass = report.Detail!.TimerPass!;
        pass.Ran.Should().BeTrue(pass.Reason);
        pass.DryRun.Should().BeTrue("the default dryRun and the timer's first week");
        home.Calls.Should().OnlyContain(
            c => CollectFlows.IsReadCommand(c) || (c.Tool == "docker" && c.Argv.SequenceEqual(Core.Actions.DockerCleanups.DockerCleanupCommands.BuilderPruneHelpCommand.Arguments)),
            "a dry run previews: read commands only (A7 asks the timer's cap from `builder prune --help`), never a vacuum, a prune or a sysctl");
        var line = RunHistory.Read(home.Paths, new PhysicalFileSystem(home.Paths)).Records.Should().ContainSingle("ONE record for the measurement and the actions").Subject;
        line.Trigger.Should().Be(RunTrigger.Timer);
        line.DryRun.Should().BeTrue();
        if (home.Paths.Side == HostSide.Wsl)
        {
            pass.Actions.Single(a => a.Id == "A10").Status.Should().Be(ActionStatus.DryRun, "the journal is above 1 GiB: the trigger fired, the week keeps it dry");
            line.Actions.Single(a => a.Id == "A10").Status.Should().Be(ActionStatus.DryRun);
        }
        else
        {
            pass.Actions.Should().OnlyContain(a => a.Status == ActionStatus.Skipped && a.Reason.Contains("WSL distro side"), "the Windows binary holds no Windows action yet (E12)");
        }

        var runs = await home.RunAsync("runs", "--json");
        runs.Exit.Should().Be((int)ExitCode.Ok, runs.Stderr);
        JsonSerializer.Deserialize(runs.Stdout, WslCareJsonContext.Default.RunsReport)!.Runs.Should().ContainSingle(r => r.RunId == line.RunId.Text && r.Trigger == "timer" && r.DetailState == "present");

        var logs = await home.RunAsync("logs", "--json");
        logs.Exit.Should().Be((int)ExitCode.Ok, logs.Stderr);
        var counts = JsonSerializer.Deserialize(logs.Stdout, WslCareJsonContext.Default.LogsReport)!.Runs;
        counts.Should().Match<RunCounts>(c => c.Total == 1 && c.Timer == 1 && c.WithCleanup == 0 && c.DryRun == (home.Paths.Side == HostSide.Wsl ? 1 : 0));
    }

    [Fact]
    public async Task A_collect_under_an_inherited_INVOCATION_ID_without_timer_is_a_cli_run_and_acts_on_nothing()
    {
        // Every descendant of a systemd unit carries INVOCATION_ID (ScenarioHome sets it for every scenario): a runner job, a
        // VS Code Server user service. Only the timer's own --timer makes a full run act; anything else is the person's run.
        using var home = TimerHome("inherited-invocation-id");

        var collect = await home.RunAsync("collect", "--json");

        collect.Exit.Should().Be((int)ExitCode.Ok, collect.Stderr);
        JsonSerializer.Deserialize(collect.Stdout, WslCareJsonContext.Default.CollectReport)!.Detail!.TimerPass
            .Should().BeNull("no --timer: the run measures and records, it does not run the timer's action pass");
        RunHistory.Read(home.Paths, new PhysicalFileSystem(home.Paths)).Records.Single().Trigger.Should().Be(RunTrigger.Cli);
    }

    [Fact]
    public async Task Logs_and_runs_over_a_seeded_history_answer_a_utc_date_and_a_range_and_write_nothing()
    {
        using var home = new ScenarioHome("logs-seeded");
        var files = new PhysicalFileSystem(home.Paths);
        var writer = new RunRecordWriter(home.Paths, files);
        var late = new DateTimeOffset(2026, 10, 1, 23, 59, 59, TimeSpan.Zero);
        var early = new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);
        writer.Append(new RunRecord(1, RunId.New(late, 11), RunTrigger.Manual, late, late, RunOutcome.Completed, [new ActionRecord("A4", 3, 3_000_000_000) { Status = ActionStatus.Ran }], RunKind.Act));
        writer.Append(new RunRecord(1, RunId.New(early, 12), RunTrigger.Timer, early, early, RunOutcome.Completed, [new ActionRecord("A10", 0, 0) { Status = ActionStatus.DryRun, WouldFreeBytes = 700 }], RunKind.Collect) { DryRun = true });
        var before = StateFiles(home);

        var day = await home.RunAsync("logs", "--period", "2026-10-01", "--json");
        var range = await home.RunAsync("runs", "--period", "2026-10-01..2026-10-02", "--json");
        var bad = await home.RunAsync("logs", "--period", "2026-10-02..2026-10-01");

        day.Exit.Should().Be((int)ExitCode.Ok, day.Stderr);
        JsonSerializer.Deserialize(day.Stdout, WslCareJsonContext.Default.LogsReport)!.Should().Match<LogsReport>(r =>
            r.FreedBytes == 3_000_000_000 && r.ObjectsRemoved == 3 && r.Runs.Total == 1 && r.Runs.WithCleanup == 1 && r.Runs.Manual == 1, "23:59:59 UTC is 2026-10-01's");
        range.Exit.Should().Be((int)ExitCode.Ok, range.Stderr);
        JsonSerializer.Deserialize(range.Stdout, WslCareJsonContext.Default.RunsReport)!.Runs.Select(r => r.WouldFreeBytes).Should().Equal(null, 700);
        bad.Exit.Should().Be((int)ExitCode.Usage);
        CliStderr.Of(bad).Messages.Should().ContainSingle(m => m.Contains("ends before it starts", StringComparison.Ordinal));
        StateFiles(home).Should().Equal(before, "logs and runs only read (their own run logs aside)");
    }

    /// <summary>Every file under the state directory but the run logs (on Windows the log folder lives inside it).</summary>
    private static IReadOnlyList<string> StateFiles(ScenarioHome home) =>
        [.. Directory.GetFiles(home.Paths.StateDirectory, "*", SearchOption.AllDirectories)
            .Where(f => !f.StartsWith(home.Paths.LogDirectory, StringComparison.Ordinal) && !f.Replace('\\', '/').Contains("/logs/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)];
}
