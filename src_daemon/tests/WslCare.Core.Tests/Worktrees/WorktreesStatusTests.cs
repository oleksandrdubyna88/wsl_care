using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Thresholds;
using WslCare.Core.Worktrees;

namespace WslCare.Core.Tests.Worktrees;

/// <summary>
/// E19.S3 (PLAN_disk_hygiene_windows.md §3.3, E19 stories §3 decision 10 and the story's brief): what the Windows <c>status</c> says of the
/// worktrees — the section with its basis (<c>scan</c>, when, how old) and the verdict <c>worktrees.reclaimable</c>, which in this story is
/// ALWAYS <c>unknown</c>: git has not confirmed the candidates, so no byte figure is called reclaimable and absent is not zero.
/// </summary>
public sealed class WorktreesStatusTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static readonly WorktreeSettings Settings = WorktreeWorld.DefaultSettings() with { ReportMaxAgeDays = 7 };

    private static WorktreeReport Report(DateTimeOffset scannedAt) =>
        new(Core.SchemaVersion.Current, scannedAt, 3.5, true,
        [
            new RootReport(@"D:\w", null, 6, 3, 1, 1, 0, 1, 1, 0, 0, 5000, new IdleOver(3, 2, 4000), new BuildOutputTotals(new Tally(4, 3_221_225_472), new Tally(1, 1000), new Tally(1, 2000), new Tally(1, 3000), []), new UnknownTotals(1, "budget"), []),
            new RootReport(@"E:\x", null, 2, 2, 0, 0, 0, 0, 0, 0, 0, 100, new IdleOver(3, 0, 0), new BuildOutputTotals(new Tally(0, 0), new Tally(0, 0), new Tally(0, 0), new Tally(0, 0), []), new UnknownTotals(0, string.Empty), []),
        ],
        [], new ReclaimableReport(false, "git has not confirmed the candidates yet (E19.S4)"));

    [Fact]
    public void With_no_root_the_verdict_is_unknown_and_says_how_to_choose_folders()
    {
        var (section, verdict) = WorktreesStatus.From([], Reading.Missing<WorktreeReport>("no scan"), Settings, Now);

        verdict.Id.Should().Be("worktrees.reclaimable");
        verdict.Level.Should().Be(Level.Unknown);
        verdict.Reason.Should().Be("no root configured — choose folders (wsl-care.exe worktrees roots set)");
        section.Available.Should().BeFalse();
        section.Roots.Should().BeEmpty();
    }

    [Fact]
    public void With_roots_and_no_scan_yet_the_verdict_is_unknown_and_says_so()
    {
        var (section, verdict) = WorktreesStatus.From([@"D:\w"], Reading.Missing<WorktreeReport>("C:\\x\\worktrees.json does not exist"), Settings, Now);

        verdict.Level.Should().Be(Level.Unknown);
        verdict.Reason.Should().Contain("no scan").And.Contain("worktrees scan");
        section.Available.Should().BeFalse();
        section.Roots.Should().Equal(@"D:\w");
        section.Basis.Source.Should().Be("scan");
        section.Basis.ScannedAt.Should().BeNull();
        section.Basis.AgeSeconds.Should().BeNull();
    }

    [Fact]
    public void A_fresh_scan_is_unknown_until_git_confirms_with_the_structural_counts_in_the_value()
    {
        var (section, verdict) = WorktreesStatus.From([@"D:\w", @"E:\x"], Reading.Of(Report(Now.AddHours(-2))), Settings, Now);

        verdict.Level.Should().Be(Level.Unknown, "never ok, never a byte figure called reclaimable");
        verdict.Reason.Should().Contain("git has not confirmed the candidates yet (E19.S4)");
        verdict.Value.Should().Contain("5 worktrees").And.Contain("1 orphan").And.Contain("1 locked").And.Contain("4 build-output folders");
        verdict.Limit.Should().Contain("20 GiB", "the warn limit in force, for when git can confirm");
        section.Available.Should().BeTrue();
        section.Stale.Should().BeFalse();
        section.Basis.Should().Be(new ScanBasis("scan", Now.AddHours(-2), 7200));
        section.Report!.Reclaimable.Available.Should().BeFalse();
    }

    [Fact]
    public void Every_verdict_carries_the_scan_as_its_basis_so_status_never_shows_one_without()
    {
        var (_, none) = WorktreesStatus.From([], Reading.Missing<WorktreeReport>("no scan"), Settings, Now);
        var (_, fresh) = WorktreesStatus.From([@"D:w"], Reading.Of(Report(Now.AddHours(-2))), Settings, Now);

        none.Basis.Should().Be(new VerdictBasis(VerdictSource.Scan, null, null, null));
        fresh.Basis.Should().Be(new VerdictBasis(VerdictSource.Scan, null, Now.AddHours(-2), 7200));
    }

    [Fact]
    public void The_verdict_value_never_calls_a_byte_figure_reclaimable()
    {
        var (_, verdict) = WorktreesStatus.From([@"D:\w"], Reading.Of(Report(Now.AddHours(-2))), Settings, Now);

        (verdict.Value + verdict.Reason + verdict.Limit).ToLowerInvariant().Should().NotContain("reclaimable");
    }

    [Fact]
    public void A_scan_older_than_the_setting_is_stale_and_says_so()
    {
        var (section, verdict) = WorktreesStatus.From([@"D:\w"], Reading.Of(Report(Now.AddDays(-9))), Settings, Now);

        section.Stale.Should().BeTrue();
        verdict.Level.Should().Be(Level.Unknown);
        verdict.Reason.Should().Contain("9 days old").And.Contain("worktrees.reportMaxAgeDays");
        section.Basis.AgeSeconds.Should().Be(9 * 86400);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(10)]
    [InlineData(400)]
    public void No_scan_age_ever_makes_the_verdict_anything_but_unknown(int days)
    {
        var (_, verdict) = WorktreesStatus.From([@"D:\w"], Reading.Of(Report(Now.AddDays(-days))), Settings, Now);

        verdict.Level.Should().Be(Level.Unknown);
    }

    [Fact]
    public void A_report_that_cannot_be_read_is_unknown_with_the_reason()
    {
        var (section, verdict) = WorktreesStatus.From([@"D:\w"], Reading.Missing<WorktreeReport>("worktrees.json is not a worktree report: x"), Settings, Now);

        verdict.Level.Should().Be(Level.Unknown);
        verdict.Reason.Should().Contain("not a worktree report");
        section.Available.Should().BeFalse();
    }
}
