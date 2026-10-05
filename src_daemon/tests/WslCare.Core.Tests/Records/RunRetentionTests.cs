using FluentAssertions;

using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Records;

/// <summary>Plan §6's retention of the run records (90 days) with plan §15b #1's rule: a detail is removed only once
/// its history line has aged out — through the file system seam, inside <c>runs/</c>.</summary>
public sealed class RunRetentionTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private readonly SandboxHost _sandbox = new("retention");

    public void Dispose() => _sandbox.Dispose();

    private string Run(DateTimeOffset started, bool withLine = true, bool withDetail = true)
    {
        var id = RunId.New(started, 100);
        var relative = RunDetailStore.RelativePath(id);
        if (withDetail)
        {
            RunDetailStore.Write(_sandbox.Paths, _sandbox.Files, id, """{"schemaVersion":1}"""u8);
        }

        if (withLine)
        {
            new RunRecordWriter(_sandbox.Paths, _sandbox.Files).Append(new RunRecord(1, id, RunTrigger.Timer, started, started, RunOutcome.Completed, [], RunKind.Collect) { Detail = relative });
        }

        return relative;
    }

    private bool DetailExists(string relative) => File.Exists(RunDetailStore.Absolute(_sandbox.Paths, relative));

    [Fact]
    public void A_line_older_than_90_days_goes_and_its_detail_with_it_while_a_young_one_stays()
    {
        var old = Run(Now.AddDays(-RunRetention.RetentionDays - 1));
        var young = Run(Now.AddDays(-RunRetention.RetentionDays + 1));

        var report = RunRetention.Sweep(_sandbox.Paths, _sandbox.Files, Now);

        report.HistoryLinesRemoved.Should().Be(1);
        report.DetailsRemoved.Should().Contain(old);
        DetailExists(old).Should().BeFalse();
        DetailExists(young).Should().BeTrue();
        RunHistory.Read(_sandbox.Paths, _sandbox.Files).Records.Select(r => r.DetailPath).Should().Equal(young);
        report.Problems.Should().BeEmpty();
    }

    [Fact]
    public void A_line_that_does_not_parse_is_kept_because_it_cannot_be_aged()
    {
        // A torn line has no start to age it by: it stays. The parsed old line goes, and its detail with it.
        var kept = Run(Now.AddDays(-RunRetention.RetentionDays - 5));
        File.AppendAllText(RunHistory.File(_sandbox.Paths), "{not a record\n");
        var linesBefore = File.ReadAllLines(RunHistory.File(_sandbox.Paths)).Length;

        RunRetention.Sweep(_sandbox.Paths, _sandbox.Files, Now);

        File.ReadAllLines(RunHistory.File(_sandbox.Paths)).Should().HaveCount(linesBefore - 1).And.Contain("{not a record");
        DetailExists(kept).Should().BeFalse("its line aged out, so the detail followed it");
    }

    [Fact]
    public void A_detail_is_never_removed_while_a_line_names_it_even_in_an_old_day_folder()
    {
        // The line's start is recent although the folder is old: an impossible-looking state the rule must still hold.
        var id = RunId.New(Now.AddDays(-200), 7);
        var relative = RunDetailStore.RelativePath(id);
        RunDetailStore.Write(_sandbox.Paths, _sandbox.Files, id, """{"schemaVersion":1}"""u8);
        new RunRecordWriter(_sandbox.Paths, _sandbox.Files).Append(new RunRecord(1, id, RunTrigger.Timer, Now, Now, RunOutcome.Completed, [], RunKind.Collect) { Detail = relative });

        RunRetention.Sweep(_sandbox.Paths, _sandbox.Files, Now);

        DetailExists(relative).Should().BeTrue();
    }

    [Fact]
    public void A_leftover_temporary_file_of_a_dead_atomic_write_is_removed()
    {
        var young = Run(Now.AddDays(-1));
        var leftover = RunDetailStore.Absolute(_sandbox.Paths, young) + ".0123456789abcdef.tmp";
        File.WriteAllText(leftover, "{");

        RunRetention.Sweep(_sandbox.Paths, _sandbox.Files, Now);

        File.Exists(leftover).Should().BeFalse();
        DetailExists(young).Should().BeTrue();
    }

    [Fact]
    public async Task An_append_that_arrives_while_the_history_is_being_rewritten_lands_in_the_new_file()
    {
        // Gate finding #0/#4: read, filter and rewrite must be ONE acquisition of history.jsonl.lock. The hook runs inside
        // the rewrite, between the read and the write — where a racing run would append — and starts that append on
        // another thread, waiting up to half a second for it. Under one lock the append waits and lands afterwards; with
        // the read outside the lock it completes at once and the rewrite throws it away.
        Run(Now.AddDays(-RunRetention.RetentionDays - 3));
        var young = Run(Now.AddDays(-1));
        var racing = new RunRecord(1, RunId.New(Now, 4242), RunTrigger.Timer, Now, Now, RunOutcome.Completed, [], RunKind.Collect);
        Task? append = null;
        var files = new InterleavingFileSystem(_sandbox.Files, () =>
        {
            append = Task.Run(() => new RunRecordWriter(_sandbox.Paths, _sandbox.Files).Append(racing));
            append.Wait(TimeSpan.FromMilliseconds(500));
        });

        var report = RunRetention.Sweep(_sandbox.Paths, files, Now);
        append.Should().NotBeNull("the hook ran inside the rewrite");
        (await Task.WhenAny(append!, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken))).Should().BeSameAs(append, "the racing append finishes once the lock is released");
        await append!;

        report.HistoryLinesRemoved.Should().Be(1);
        RunHistory.Read(_sandbox.Paths, _sandbox.Files).Records.Select(r => r.RunId.Text).Should().Equal(
            [RunHistory.Read(_sandbox.Paths, _sandbox.Files).Records.Single(r => r.DetailPath == young).RunId.Text, racing.RunId.Text],
            "the young line stays and the racing append survives the rewrite");
    }

    [Fact]
    public void A_line_and_its_detail_age_out_together_so_the_next_reconcile_never_resurrects_the_run()
    {
        // Now is 12:00 UTC: a run at 11:00 on the boundary day is older than the window by the clock, but its detail lives
        // in a day folder that is not. Ageing the line by the instant and the folder by the day removed the line and kept
        // the detail — and the next reconcile made that detail an "interrupted" run (gate finding #0/#4, #11).
        var boundary = Run(Now.AddDays(-RunRetention.RetentionDays).AddHours(-1));

        RunRetention.Sweep(_sandbox.Paths, _sandbox.Files, Now);
        var reconcile = RunReconcile.Apply(_sandbox.Paths, _sandbox.Files, Now);

        reconcile.Interrupted.Should().BeEmpty("a run retention pruned is not a run that died");
        var records = RunHistory.Read(_sandbox.Paths, _sandbox.Files).Records;
        records.Should().NotContain(r => r.Outcome == RunOutcome.Interrupted);
        records.Any(r => r.DetailPath == boundary).Should().Be(DetailExists(boundary), "a line and its detail are kept or removed together");
    }

    [Fact]
    public void A_detail_older_than_the_window_without_a_line_is_never_made_an_interrupted_run_while_a_young_one_is()
    {
        var aged = Run(Now.AddDays(-RunRetention.RetentionDays - 30), withLine: false);
        var young = Run(Now.AddDays(-2), withLine: false);

        var reconcile = RunReconcile.Apply(_sandbox.Paths, _sandbox.Files, Now);

        var records = RunHistory.Read(_sandbox.Paths, _sandbox.Files).Records;
        records.Select(r => r.DetailPath).Should().Equal([young], "only the detail inside the window is a run that died before its line");
        reconcile.Interrupted.Should().ContainSingle();
        DetailExists(aged).Should().BeTrue("the reconcile deletes nothing; retention does");

        RunRetention.Sweep(_sandbox.Paths, _sandbox.Files, Now);

        DetailExists(aged).Should().BeFalse();
        Directory.Exists(Path.GetDirectoryName(RunDetailStore.Absolute(_sandbox.Paths, aged))).Should().BeFalse("its day folder went with it");
    }

    [Fact]
    public void A_day_folder_older_than_the_window_goes_whole_once_no_line_names_it_and_a_day_inside_the_window_is_untouched()
    {
        // Gate finding #11: prune at the runs/{yyyy-MM-dd}/ level by the date boundary. A stray folder or file in an aged
        // day used to keep the day folder forever (only an EMPTY folder was removed).
        var aged = Run(Now.AddDays(-RunRetention.RetentionDays - 2), withLine: false);
        var agedDay = Path.GetDirectoryName(RunDetailStore.Absolute(_sandbox.Paths, aged))!;
        Directory.CreateDirectory(Path.Combine(agedDay, "stray-folder"));
        File.WriteAllText(Path.Combine(agedDay, "stray-folder", "inside.txt"), "x");
        File.WriteAllText(Path.Combine(agedDay, "notes.txt"), "x");
        var young = Run(Now.AddDays(-RunRetention.RetentionDays + 2), withLine: false);
        var youngDay = Path.GetDirectoryName(RunDetailStore.Absolute(_sandbox.Paths, young))!;
        File.WriteAllText(Path.Combine(youngDay, "notes.txt"), "x");

        var report = RunRetention.Sweep(_sandbox.Paths, _sandbox.Files, Now);

        Directory.Exists(agedDay).Should().BeFalse("an aged day no line names is removed whole");
        report.DetailsRemoved.Should().Contain($"{RunDetailStore.Folder}/{Path.GetFileName(agedDay)}/");
        DetailExists(young).Should().BeTrue();
        File.Exists(Path.Combine(youngDay, "notes.txt")).Should().BeTrue("a day inside the window is untouched");
        report.Problems.Should().BeEmpty();
    }

    /// <summary>The real file system, with a hook run INSIDE <see cref="IFileSystem.RewriteLines"/> — after the current lines
    /// were read and before the kept ones are written: the moment a racing appender would act.</summary>
    private sealed class InterleavingFileSystem(IFileSystem inner, Action betweenReadAndWrite) : DelegatingFileSystem(inner)
    {
        public override DeletionVerdict RewriteLines(string path, Func<IReadOnlyList<string>, IReadOnlyList<string>> keep, DeletionScope scope, TimeSpan lockTimeout) =>
            base.RewriteLines(path, lines =>
            {
                betweenReadAndWrite();
                return keep(lines);
            }, scope, lockTimeout);
    }
}
