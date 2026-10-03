using System.Text.Json;

using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Json;
using WslCare.Core.History;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.History;

/// <summary>
/// <c>logs</c> / <c>runs</c> (E3.S3, plan §7.4) over a SEEDED history: UTC day boundaries (23:59:59 belongs to yesterday,
/// 00:00:00 to today), sums per action and in total, runs with and without a cleanup, dry runs apart with what they would
/// have freed, timer / button / terminal counts, the run that freed the most and the least, each metric's max and min with
/// its time, every cleanup with the objects its detail lists (an <c>act</c> detail and a full run's timer pass), a lost
/// detail, an unparseable line, and the period parser.
/// </summary>
public sealed class RunLogsTests : IDisposable
{
    private static readonly DateTimeOffset Now = FixedTimeProvider.DefaultNow;
    private static readonly DateTimeOffset Today = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

    private readonly SandboxHost _sandbox = new("logs");

    public RunLogsTests()
    {
        // Yesterday's last second: a button's A4, three volumes, its act detail.
        Act(Today.AddSeconds(-1), RunTrigger.Manual, Removed("A4", 3_000, ("volume", "aaa", 1_000L), ("volume", "bbb", 1_000L), ("volume", "ccc", 1_000L)));

        // Today 00:00:00 — the timer, in its dry-run week: A10 would free 500.
        Line(Today, RunTrigger.Timer, dryRun: true, memAvailable: 20.0, swap: 1_000, actions: [new ActionRecord("A10", 4, 0) { Status = ActionStatus.DryRun, WouldFreeBytes = 500 }, new ActionRecord("A5", 0, 0) { Status = ActionStatus.Skipped }]);

        // 04:00 — the timer ran A10 (a full run with its timer pass in the detail).
        TimerPassRun(Today.AddHours(4), memAvailable: 35.5, swap: 3_000, Removed("A10", 200, ("journal file", "/var/log/journal/x/system@1.journal", 120L), ("journal file", "/var/log/journal/x/system@2.journal", 80L)));

        // 08:00 — a terminal's A5 that found nothing: a run WITHOUT a cleanup.
        Line(Today.AddHours(8), RunTrigger.Cli, dryRun: false, actions: [new ActionRecord("A5", 0, 0) { Status = ActionStatus.Ran }]);

        // 10:00 — a button's A4 whose detail is gone.
        Line(Today.AddHours(10), RunTrigger.Manual, dryRun: false, detail: "runs/2026-10-02/20261002T100000Z-5.json", actions: [new ActionRecord("A4", 1, 9_000) { Status = ActionStatus.Ran }]);

        // Two weeks ago.
        Line(new DateTimeOffset(2026, 9, 20, 6, 0, 0, TimeSpan.Zero), RunTrigger.Timer, dryRun: false, memAvailable: 50, actions: [new ActionRecord("A7", 1, 70_000) { Status = ActionStatus.Ran }]);
    }

    public void Dispose() => _sandbox.Dispose();

    private static LogPeriod Period(string text) => ((PeriodParse.Parsed)LogPeriod.Parse(text, Now)).Period;

    private static ActionOutcome Removed(string id, long freed, params (string Kind, string Name, long Bytes)[] items) =>
        new(id, $"{id} summary", ActionStatus.Ran, "ran", null, new ActionRun(items.Length, freed, "measured", null, null, [.. items.Select(i => new ActionItem(i.Kind, i.Name, i.Bytes))], [], string.Empty)
        {
            NotRemoved = [new ActionItem("volume", "ddd", 5, "in use since the preview")],
            Notes = ["a note"],
        });

    private void Line(DateTimeOffset at, RunTrigger trigger, bool dryRun, IReadOnlyList<ActionRecord> actions, double? memAvailable = null, long? swap = null, string? detail = null) =>
        new RunRecordWriter(_sandbox.Paths, _sandbox.Files).Append(new RunRecord(1, RunId.New(at, 5), trigger, at, at.AddMinutes(1), RunOutcome.Completed, actions)
        {
            DryRun = dryRun,
            Detail = detail,
            Metrics = memAvailable is null ? null : new RunMetrics(memAvailable, null, null, swap, 13.0, null, null),
        });

    private void Act(DateTimeOffset at, RunTrigger trigger, ActionOutcome outcome)
    {
        var id = RunId.New(at, 5);
        var detail = new ActRunDetail(1, id, trigger, at, at, false, "act", RunOutcome.Completed, "wsl", "a button never dry-runs", new TargetUserReport(true, "me", "/home/me", "test"), [outcome], []);
        RunDetailStore.Write(_sandbox.Paths, _sandbox.Files, id, JsonSerializer.SerializeToUtf8Bytes(detail, WslCareJsonContext.Default.ActRunDetail));
        Line(at, trigger, dryRun: false, detail: RunDetailStore.RelativePath(id), actions: [ActionRecords.Of(outcome)]);
    }

    private void TimerPassRun(DateTimeOffset at, double memAvailable, long swap, ActionOutcome outcome)
    {
        var id = RunId.New(at, 5);
        var pass = new TimerPass(true, string.Empty, false, "the week has passed", null, [outcome], [], RunOutcome.Completed);
        RunDetailStore.Write(_sandbox.Paths, _sandbox.Files, id, JsonSerializer.SerializeToUtf8Bytes(new TimerPassView(pass), WslCareJsonContext.Default.TimerPassView));
        Line(at, RunTrigger.Timer, dryRun: false, memAvailable: memAvailable, swap: swap, detail: RunDetailStore.RelativePath(id), actions: [ActionRecords.Of(outcome)]);
    }

    [Fact]
    public void Today_counts_from_00_00_00_utc_and_sums_runs_with_and_without_a_cleanup_dry_runs_apart()
    {
        var logs = RunLogs.Logs(_sandbox.Paths, _sandbox.Files, Period("today"), action: null);

        logs.Period.Should().Be(new PeriodReport("today", "2026-10-02", "2026-10-02"));
        logs.Runs.Should().Be(new RunCounts(Total: 4, WithCleanup: 2, WithoutCleanup: 2, DryRun: 1, WouldFreeBytes: 500, Timer: 2, Manual: 1, Cli: 1, Failed: 0, Interrupted: 0));
        logs.FreedBytes.Should().Be(9_200);
        logs.ObjectsRemoved.Should().Be(3, "A10 removed 2, A4 1, A5 none");
        logs.PerAction.Select(t => $"{t.Id}:{t.Runs}:{t.Count}:{t.FreedBytes}:{t.DryRuns}:{t.WouldFreeBytes}").Should().Equal("A5:1:0:0:0:0", "A4:1:1:9000:0:0", "A10:1:2:200:1:500");
        logs.MostFreed.Should().Be(new RunExtreme(RunId.New(Today.AddHours(10), 5).Text, Today.AddHours(10), 9_000));
        logs.LeastFreed.Should().Be(new RunExtreme(RunId.New(Today.AddHours(4), 5).Text, Today.AddHours(4), 200));
    }

    [Fact]
    public void Every_metric_has_its_max_and_min_with_the_time_it_occurred()
    {
        var metrics = RunLogs.Logs(_sandbox.Paths, _sandbox.Files, Period("today"), action: null).Metrics.ToDictionary(m => m.Name);

        metrics["memAvailablePercent"].Should().Match<MetricExtremes>(m => m.Samples == 2 && m.Max!.Value == 35.5 && m.Max.At == Today.AddHours(4) && m.Min!.Value == 20.0 && m.Min.At == Today);
        metrics["swapUsedBytes"].Max!.Value.Should().Be(3_000);
        metrics["dockerReclaimableBytes"].Should().Match<MetricExtremes>(m => m.Samples == 0 && m.Max == null && m.Min == null, "never recorded is absent, never 0");
    }

    [Fact]
    public void Each_cleanup_lists_what_its_detail_says_was_removed_and_a_lost_detail_says_so()
    {
        var cleanups = RunLogs.Logs(_sandbox.Paths, _sandbox.Files, Period("today"), action: null, detail: true).Cleanups;

        cleanups.Select(c => $"{c.Action}:{c.DetailState}").Should().Equal("A10:present", "A4:lost");
        cleanups[0].Removed.Select(r => r.Name).Should().Equal("/var/log/journal/x/system@1.journal", "/var/log/journal/x/system@2.journal");
        cleanups[0].NotRemoved.Should().ContainSingle(n => n.Note == "in use since the preview");
        cleanups[0].FreedBasis.Should().Be("measured");
        cleanups[1].Should().Match<CleanupDetail>(c => c.Count == 1 && c.FreedBytes == 9_000 && c.Removed.Count == 0, "the history line still counts it");
    }

    [Fact]
    public void Yesterday_ends_at_23_59_59_utc_and_reads_an_act_detail()
    {
        var logs = RunLogs.Logs(_sandbox.Paths, _sandbox.Files, Period("yesterday"), action: null, detail: true);

        logs.Runs.Total.Should().Be(1);
        logs.Cleanups.Single().Removed.Select(r => r.Name).Should().Equal("aaa", "bbb", "ccc");
        logs.Cleanups.Single().Trigger.Should().Be("manual");
    }

    [Fact]
    public void A_range_and_a_single_date_cover_their_utc_days_both_ends_included()
    {
        RunLogs.Runs(_sandbox.Paths, _sandbox.Files, Period("2026-09-20..2026-10-01")).Runs.Select(r => r.StartedAt).Should().Equal(new DateTimeOffset(2026, 9, 20, 6, 0, 0, TimeSpan.Zero), Today.AddSeconds(-1));
        RunLogs.Runs(_sandbox.Paths, _sandbox.Files, Period("2026-09-21")).Count.Should().Be(0);
        RunLogs.Logs(_sandbox.Paths, _sandbox.Files, Period("2026-09-20..2026-10-02"), action: null).Runs.Total.Should().Be(6);
    }

    [Fact]
    public void One_action_narrows_the_totals_the_cleanups_and_the_run_counts()
    {
        var logs = RunLogs.Logs(_sandbox.Paths, _sandbox.Files, Period("today"), ActionId.Find("A10"));

        logs.Action.Should().Be("A10");
        logs.FreedBytes.Should().Be(200);
        logs.PerAction.Should().ContainSingle(t => t.Id == "A10");
        logs.Runs.WithCleanup.Should().Be(1);
        logs.Cleanups.Should().ContainSingle(c => c.Action == "A10");
    }

    [Fact]
    public void Runs_lists_every_run_oldest_first_with_its_detail_state_and_cleanup_flag()
    {
        var runs = RunLogs.Runs(_sandbox.Paths, _sandbox.Files, Period("today")).Runs;

        runs.Select(r => $"{r.StartedAt:HH:mm}:{r.Trigger}:{r.DetailState}:{r.Cleanup}:{r.FreedBytes}").Should().Equal(
            "00:00:timer:none:False:0", "04:00:timer:present:True:200", "08:00:cli:none:False:0", "10:00:manual:lost:True:9000");
        runs[0].WouldFreeBytes.Should().Be(500);
        runs[0].DryRun.Should().BeTrue();
    }

    [Fact]
    public void An_act_detail_written_before_not_removed_and_notes_existed_reads_them_as_empty_never_null()
    {
        // E3.S1's act details carry no "notRemoved" / "notes": the source generator reads them as null (C# doctrine §4a).
        var at = new DateTimeOffset(2026, 9, 25, 9, 0, 0, TimeSpan.Zero);
        var id = RunId.New(at, 5);
        var old = $$$"""{"schemaVersion":1,"runId":"{{{id.Text}}}","trigger":"cli","startedAt":"2026-09-25T09:00:00+00:00","endedAt":"2026-09-25T09:00:01+00:00","dryRun":false,"kind":"act","outcome":"completed","side":"wsl","dryRunReason":"x","targetUser":{"found":false,"source":"none"},"actions":[{"id":"A10","summary":"s","status":"ran","reason":"ran","run":{"count":1,"freedBytes":10,"freedBasis":"measured","removed":[{"kind":"journal file","name":"/var/log/journal/old.journal","bytes":10}],"commands":[],"failure":""}}],"notes":[]}""";
        RunDetailStore.Write(_sandbox.Paths, _sandbox.Files, id, System.Text.Encoding.UTF8.GetBytes(old));
        Line(at, RunTrigger.Cli, dryRun: false, detail: RunDetailStore.RelativePath(id), actions: [new ActionRecord("A10", 1, 10) { Status = ActionStatus.Ran }]);

        var cleanup = RunLogs.Logs(_sandbox.Paths, _sandbox.Files, Period("2026-09-25"), action: null, detail: true).Cleanups.Single();

        cleanup.Removed.Single().Name.Should().Be("/var/log/journal/old.journal");
        cleanup.NotRemoved.Should().NotBeNull().And.BeEmpty();
        cleanup.Notes.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void A_failed_actions_real_deletions_are_counted_and_its_failure_is_shown_beside_the_figures()
    {
        // A4 removed 386 of 387 volumes and could not confirm one: the action FAILED, and the 386 are gone all the same.
        var at = new DateTimeOffset(2026, 9, 26, 9, 0, 0, TimeSpan.Zero);
        const string failure = "eeee: \"something Docker said\"";
        var outcome = new ActionOutcome("A4", "A4 summary", ActionStatus.Failed, failure, null, new ActionRun(386, 59_000_000_000, "measured", null, null, [new ActionItem("volume", "aaaa", 59_000_000_000)], [], failure)
        {
            NotRemoved = [new ActionItem("volume", "eeee", 1, "not confirmed: something Docker said")],
        });
        Act(at, RunTrigger.Manual, outcome);

        var logs = RunLogs.Logs(_sandbox.Paths, _sandbox.Files, Period("2026-09-26"), action: null, detail: true);

        logs.FreedBytes.Should().Be(59_000_000_000, "what a failed action measurably removed was removed");
        logs.ObjectsRemoved.Should().Be(386);
        logs.Runs.WithCleanup.Should().Be(1);
        logs.PerAction.Single().Should().Match<ActionTotal>(t => t.Count == 386 && t.FreedBytes == 59_000_000_000 && t.Failed == 1);
        logs.Cleanups.Single().Should().Match<CleanupDetail>(c => c.Status == ActionStatus.Failed && c.Count == 386 && c.Failure == failure);
        RunLogs.Runs(_sandbox.Paths, _sandbox.Files, Period("2026-09-26")).Runs.Single().Actions.Single().Failure.Should().Be(failure);
    }

    [Fact]
    public void Totals_counts_and_extremes_come_from_the_history_lines_alone_and_open_no_detail_file()
    {
        // Gate finding #10: a period of a year is thousands of runs; the totals must not open a detail file per run.
        var files = new ReadRecordingFileSystem(_sandbox.Files);

        var logs = RunLogs.Logs(_sandbox.Paths, files, Period("2026-09-20..2026-10-02"), action: null);

        logs.FreedBytes.Should().Be(3_000 + 200 + 9_000 + 70_000);
        logs.Runs.Total.Should().Be(6);
        logs.Cleanups.Should().HaveCount(4, "every cleanup is listed from its history line");
        logs.Cleanups.Should().OnlyContain(c => c.Removed.Count == 0, "the objects come from the details, which were not asked for");
        files.Read.Should().NotContain(p => p.Contains(RunDetailStore.Folder, StringComparison.Ordinal), "no run detail is opened for the totals");
    }

    [Fact]
    public void Detail_asked_or_one_action_reads_the_objects_and_at_most_the_newest_bounded_number_of_details()
    {
        var files = new ReadRecordingFileSystem(_sandbox.Files);

        var asked = RunLogs.Logs(_sandbox.Paths, files, Period("2026-09-20..2026-10-02"), action: null, detail: true);
        var one = RunLogs.Logs(_sandbox.Paths, _sandbox.Files, Period("2026-09-20..2026-10-02"), ActionId.Find("A4"));

        asked.DetailsRead.Should().Be(4);
        asked.DetailsNotRead.Should().Be(0);
        asked.Cleanups.Single(c => c.Action == "A10").Removed.Should().HaveCount(2);
        files.Read.Should().Contain(p => p.Contains(RunDetailStore.Folder, StringComparison.Ordinal));
        one.Cleanups.Select(c => c.DetailState).Should().Equal("present", "lost");
        RunLogs.MaxDetailsRead.Should().BeInRange(1, 1000, "the bound is a bound");
    }

    [Fact]
    public void Past_the_bound_the_older_cleanups_are_listed_from_their_lines_with_their_objects_not_read()
    {
        for (var i = 0; i < RunLogs.MaxDetailsRead + 3; i++)
        {
            Act(new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero).AddMinutes(i), RunTrigger.Manual, Removed("A4", 10, ("volume", $"v{i}", 10L)));
        }

        var logs = RunLogs.Logs(_sandbox.Paths, _sandbox.Files, Period("2026-09-27"), action: null, detail: true);

        logs.DetailsRead.Should().Be(RunLogs.MaxDetailsRead);
        logs.DetailsNotRead.Should().Be(3);
        logs.Cleanups.Take(3).Should().OnlyContain(c => c.DetailState == RunLogs.NotRead && c.Removed.Count == 0, "the OLDEST are the ones left unread");
        logs.Cleanups.Skip(3).Should().OnlyContain(c => c.DetailState == "present");
        logs.ObjectsRemoved.Should().Be(RunLogs.MaxDetailsRead + 3, "the totals count every run, read or not");
    }

    /// <summary>The real file system, with every <see cref="Core.Files.IFileSystem.ReadFile"/> path kept.</summary>
    private sealed class ReadRecordingFileSystem(Core.Files.IFileSystem inner) : DelegatingFileSystem(inner)
    {
        public List<string> Read { get; } = [];

        public override Core.Files.FileReadResult ReadFile(string path)
        {
            Read.Add(path);
            return base.ReadFile(path);
        }
    }

    [Fact]
    public void An_unparseable_line_is_counted_and_a_missing_history_is_an_empty_answer()
    {
        File.AppendAllText(RunHistory.File(_sandbox.Paths), "{ torn\n");
        RunLogs.Runs(_sandbox.Paths, _sandbox.Files, Period("today")).UnparseableLines.Should().Be(1);

        File.Delete(RunHistory.File(_sandbox.Paths));
        var empty = RunLogs.Logs(_sandbox.Paths, _sandbox.Files, Period("today"), action: null);
        empty.Runs.Total.Should().Be(0);
        empty.Problem.Should().BeNull();
        empty.MostFreed.Should().BeNull();
    }

    [Theory]
    [InlineData("today", "2026-10-02", "2026-10-02")]
    [InlineData("yesterday", "2026-10-01", "2026-10-01")]
    [InlineData("2026-02-28", "2026-02-28", "2026-02-28")]
    [InlineData("2026-09-30..2026-10-02", "2026-09-30", "2026-10-02")]
    public void A_period_is_today_yesterday_a_date_or_a_range_in_utc_days(string text, string from, string to)
    {
        var period = Period(text);

        (period.FromText, period.ToText).Should().Be((from, to));
    }

    [Theory]
    [InlineData("tomorrow", "is not today")]
    [InlineData("2026-13-01", "is not today")]
    [InlineData("2026-10-02..2026-10-01", "ends before it starts")]
    [InlineData("2025-01-01..2026-10-02", "longer than 366 days")]
    [InlineData("2026-10-01..", "is not today")]
    [InlineData("date:2026-10-01", "is not today")]
    public void Any_other_period_is_refused_with_the_reason(string text, string reason) =>
        LogPeriod.Parse(text, Now).Should().BeOfType<PeriodParse.Refused>().Which.Reason.Should().Contain(reason);
}
