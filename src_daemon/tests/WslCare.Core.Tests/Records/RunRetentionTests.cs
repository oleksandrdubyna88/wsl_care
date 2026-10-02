using FluentAssertions;

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
            new RunRecordWriter(_sandbox.Paths, _sandbox.Files).Append(new RunRecord(1, id, RunTrigger.Timer, started, started, RunOutcome.Completed, []) { Detail = relative });
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
        new RunRecordWriter(_sandbox.Paths, _sandbox.Files).Append(new RunRecord(1, id, RunTrigger.Timer, Now, Now, RunOutcome.Completed, []) { Detail = relative });

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
}
