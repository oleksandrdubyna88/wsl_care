using System.Text.Json;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Files;
using WslCare.Core.History;
using WslCare.Core.Hosting;
using WslCare.Core.Json;
using WslCare.Core.Records;
using WslCare.Core.Status;
using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>
/// E6.S0's read contract end to end over the BUILT CLI (plan §15j): <c>status --json</c>'s <c>running</c> block in every state
/// a scenario can stage against the REAL process table — and never a sweep, not even with the state directory unwritable;
/// <c>runs show</c> of a confirmed act (done, with its commands and exits), of the run that act swept (interrupted) and of a
/// stranger (unknown); <c>logs</c> / <c>runs</c> over a local day that crosses UTC midnight; A4's preview carrying all 387
/// names it selected; SIGHUP during a confirm recorded as <c>interrupted</c>; <c>--manual</c> with <c>--timer</c> refused.
/// </summary>
public sealed class ReadContractFlows
{
    private const string LinuxOnly = "the distro's binary: its actions (A4, A10) and its signals run on the Linux legs (and by hand in WSL)";

    private static StatusReport Status(ChildResult result) =>
        JsonSerializer.Deserialize(result.Stdout, WslCareJsonContext.Default.StatusReport) ?? throw new InvalidOperationException($"status printed null: {result.Stderr}");

    private static RunShowReport Show(ChildResult result) =>
        JsonSerializer.Deserialize(result.Stdout, WslCareJsonContext.Default.RunShowReport) ?? throw new InvalidOperationException($"runs show printed null: {result.Stderr}");

    [Theory]
    [InlineData("live", RunningStateName.Live)]
    [InlineData("wedged", RunningStateName.Wedged)]
    [InlineData("dead", RunningStateName.Dead)]
    [InlineData("unreadable", RunningStateName.Unreadable)]
    [InlineData("queued", RunningStateName.Queued)]
    public async Task Status_reads_each_staged_running_state_through_the_real_process_table(string staged, string expected)
    {
        using var home = new ScenarioHome($"running-{staged}");
        ReadContractScenes.Stage(home, staged);

        var result = await home.RunAsync("status", "--json");

        result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
        var report = Status(result);
        report.Running!.State.Should().Be(expected, report.Running.Reason);
        report.SchemaVersion.Should().Be(1);
        report.Capabilities.Should().Equal(Capabilities.All);
        home.Calls.Should().BeEmpty("status starts no process");
    }

    [Fact]
    public async Task Status_with_nothing_in_flight_names_none_this_sides_actions_and_no_cleanup_yet()
    {
        using var home = new ScenarioHome("running-none");

        var report = Status(await home.RunAsync("status", "--json"));

        report.Running!.State.Should().Be(RunningStateName.None);
        report.LastCleanup!.Available.Should().BeFalse();
        report.Actions.Should().NotBeNull();
        if (home.Paths.Side == HostSide.Windows)
        {
            report.Actions.Should().BeEmpty("every built action is the distro's; the Windows binary's are E12's");
        }
        else
        {
            report.Actions.Should().Contain("A4").And.NotContain("A13", "A13 is not built");
        }
    }

    /// <summary>E6.S0 review D3: a run that recorded itself and died before removing its <c>running.json</c> is not "dead,
    /// nothing recorded it" — status reports none, naming the left-over file, and agrees with runs show (done).</summary>
    [Fact]
    public async Task A_left_over_running_json_of_a_recorded_run_is_none_in_status_and_done_in_runs_show()
    {
        using var home = new ScenarioHome("running-left-over");
        var dead = ReadContractScenes.Dead();
        ReadContractScenes.Running(home, dead);
        new RunRecordWriter(home.Paths, new PhysicalFileSystem(home.Paths)).Append(new RunRecord(1, dead.RunId, RunTrigger.Manual, dead.StartedAt, dead.HeartbeatAt, RunOutcome.Completed, []));

        var status = Status(await home.RunAsync("status", "--json"));
        var show = Show(await home.RunAsync("runs", "show", dead.RunId.Text, "--json"));

        status.Running!.State.Should().Be(RunningStateName.None);
        status.Running.Reason.Should().Contain("only its running.json is left");
        show.State.Should().Be(RunShowState.Done);
    }

    /// <summary>§15j M3 / §15b #3: status is unprivileged and NEVER sweeps — a dead run is reported and its file left exactly
    /// as it was, with no history line; and with the state directory made unwritable status still answers the same.</summary>
    [Fact]
    public async Task Status_reports_a_dead_run_and_never_sweeps_it_even_with_the_state_directory_unwritable()
    {
        using var home = new ScenarioHome("running-dead-unwritable");
        ReadContractScenes.Stage(home, "dead");
        var running = RunningState.File(home.Paths);
        var before = await File.ReadAllBytesAsync(running, TestContext.Current.CancellationToken);

        var writable = Status(await home.RunAsync("status", "--json"));

        writable.Running!.State.Should().Be(RunningStateName.Dead);
        (await File.ReadAllBytesAsync(running, TestContext.Current.CancellationToken)).Should().Equal(before, "a reader never sweeps");
        File.Exists(RunHistory.File(home.Paths)).Should().BeFalse("no interrupted line is written by status");

        if (!OperatingSystem.IsLinux() || Environment.IsPrivilegedProcess)
        {
            Assert.Skip("a read-only directory is a mode bit on the Linux legs, and root writes through it; the writable half above ran");
            return;
        }

        var state = home.Paths.StateDirectory;
        File.SetUnixFileMode(state, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        try
        {
            var listing = Directory.GetFileSystemEntries(state, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();
            var result = await home.RunAsync("status", "--json");

            result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
            Status(result).Running!.State.Should().Be(RunningStateName.Dead);
            Directory.GetFileSystemEntries(state, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Should().Equal(listing, "nothing was written, swept or removed");
            (await File.ReadAllBytesAsync(running, TestContext.Current.CancellationToken)).Should().Equal(before);
        }
        finally
        {
            File.SetUnixFileMode(state, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task Runs_show_answers_a_confirmed_act_done_the_run_it_swept_interrupted_and_a_stranger_unknown()
    {
        using var home = ReadContractScenes.Journal("runs-show");
        Assert.SkipWhen(home.Paths.Side == HostSide.Windows, LinuxOnly);
        ReadContractScenes.Running(home, ReadContractScenes.Dead());

        var act = await home.RunAsync("act", "A10", "--confirm", "--json");
        act.Exit.Should().Be((int)ExitCode.Ok, act.Stderr);
        var runId = JsonSerializer.Deserialize(act.Stdout, WslCareJsonContext.Default.ActReport)!.RunId!;
        var done = await home.RunAsync("runs", "show", runId, "--json");
        var interrupted = await home.RunAsync("runs", "show", ReadContractScenes.Dead().RunId.Text, "--json");
        var unknown = await home.RunAsync("runs", "show", ReadContractScenes.StrangerRunId, "--json");

        var shown = Show(done);
        shown.State.Should().Be(RunShowState.Done);
        shown.DetailState.Should().Be("present");
        var a10 = shown.Detail!.Actions.Should().ContainSingle().Subject;
        a10.Run!.Commands.Should().ContainSingle(c => c.Display == "journalctl --vacuum-time=30d").Which.Exit.Should().Be(0);
        shown.Detail.Notes.Should().Contain(n => n.Contains("recorded as interrupted", StringComparison.Ordinal));
        Show(interrupted).Should().Match<RunShowReport>(s => s.State == RunShowState.Interrupted && s.Reason!.Contains("pid 2147483647 is gone"));
        Show(unknown).State.Should().Be(RunShowState.Unknown);
        new[] { done, interrupted, unknown }.Should().OnlyContain(r => r.Exit == (int)ExitCode.Ok);
    }

    [Fact]
    public async Task Logs_and_runs_over_a_local_day_sent_as_two_instants_hold_the_runs_on_both_sides_of_utc_midnight()
    {
        using var home = new ScenarioHome("local-day");
        ReadContractScenes.LocalDayHistory(home);

        var runs = await home.RunAsync(["runs", .. ReadContractScenes.LocalDay, "--json"]);
        var logs = await home.RunAsync(["logs", .. ReadContractScenes.LocalDay, "--detail", "--json"]);

        runs.Exit.Should().Be((int)ExitCode.Ok, runs.Stderr);
        var lines = JsonSerializer.Deserialize(runs.Stdout, WslCareJsonContext.Default.RunsReport)!.Runs;
        lines.Select(r => r.StartedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture))
            .Should().Equal("2026-10-01T21:00:00", "2026-10-01T23:59:59", "2026-10-02T00:00:00");
        lines[0].Metrics!.MemAvailablePercent.Should().Be(28.5);
        lines[1].Metrics.Should().BeNull("an act line records no metrics");
        var report = JsonSerializer.Deserialize(logs.Stdout, WslCareJsonContext.Default.LogsReport)!;
        report.FreedBytes.Should().Be(308_003_000, "the button's A4 at 23:59:59Z and the timer's A10 at 00:00Z — neither UTC day holds both");
        report.Cleanups.Should().Contain(c => c.Action == "A4" && c.Removed.Count == 2);
    }

    [Fact]
    public async Task A4s_preview_over_387_volumes_carries_all_387_names_it_selected_and_writes_nothing()
    {
        var (home, names) = ReadContractScenes.A4Morning("a4-shown");
        using (home)
        {
            Assert.SkipWhen(home.Paths.Side == HostSide.Windows, LinuxOnly);

            var result = await home.RunAsync("act", "A4", "--preview", "--json");

            result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
            var a4 = JsonSerializer.Deserialize(result.Stdout, WslCareJsonContext.Default.ActReport)!.Actions.Single();
            a4.Preview!.Count.Should().Be(ReadContractScenes.A4Volumes);
            a4.Preview.Items.Should().HaveCount(20, "the items stop at 20 — why shown exists (§15j B1)");
            a4.Shown.Should().HaveCount(a4.Preview.Count).And.BeEquivalentTo(names);
            a4.Shown.Should().OnlyContain(n => Core.Docker.DockerJson.IsFullId(n));
            Directory.Exists(home.Paths.StateDirectory).Should().BeFalse("a preview writes no state (§15b #3)");
        }
    }

    /// <summary>§15j B2: SIGHUP — what a terminal or the wsl.exe relay delivers when it goes away — is a cancellation: the
    /// confirm stops, kills its child, and records itself <c>interrupted</c> with a detail naming SIGHUP.</summary>
    [Fact]
    public async Task A_confirm_cut_off_by_SIGHUP_records_itself_interrupted_with_a_detail_naming_the_signal()
    {
        using var home = ReadContractScenes.Journal("act-sighup", vacuumDelayMilliseconds: 60_000);
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);
        using var child = home.Start("act", "A10", "--confirm", "--json");
        (await RunningChild.WaitUntilAsync(() => home.Calls.Any(c => c.Argv.Contains("--vacuum-time=30d")), TimeSpan.FromSeconds(30))).Should().BeTrue("the vacuum started");

        child.Hangup();
        var result = await child.WaitAsync(TimeSpan.FromSeconds(30));

        result.Exit.Should().Be((int)ExitCode.Interrupted, result.Stderr);
        var files = new PhysicalFileSystem(home.Paths);
        var line = RunHistory.Read(home.Paths, files).Records.Should().ContainSingle().Subject;
        line.Outcome.Should().Be(RunOutcome.Interrupted);
        line.Reason.Should().Contain("SIGHUP");
        File.Exists(RunDetailStore.Absolute(home.Paths, line.DetailPath)).Should().BeTrue("the run's detail was written");
        // E6.S0 review D2: the detail names the action IN FLIGHT, interrupted — it held no action at all before.
        var detail = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(RunDetailStore.Absolute(home.Paths, line.DetailPath), TestContext.Current.CancellationToken), WslCareJsonContext.Default.ActRunDetail)!;
        detail.Actions.Should().ContainSingle(a => a.Id == "A10").Which.Status.Should().Be(ActionStatus.Interrupted);
        line.Actions.Should().ContainSingle(a => a.Id == "A10").Which.Status.Should().Be(ActionStatus.Interrupted);
        File.Exists(RunningState.File(home.Paths)).Should().BeFalse("running.json goes when the run ends");
    }

    [Fact]
    public async Task Manual_and_timer_together_are_refused_before_anything_is_touched()
    {
        using var home = ReadContractScenes.Journal("act-manual-timer");

        var result = await home.RunAsync("act", "A10", "--confirm", "--manual", "--timer", "--json");

        result.Exit.Should().Be((int)ExitCode.Usage);
        CliStderr.Of(result).Messages.Should().ContainSingle(m => m.Contains("not both", StringComparison.Ordinal));
        home.Calls.Should().BeEmpty();
        Directory.Exists(home.Paths.StateDirectory).Should().BeFalse();
    }
}
