using System.Text.Json;

using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Actions.WindowsTemp;
using WslCare.Core.Hosting;
using WslCare.Core.Json;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Actions.WindowsTemp;

/// <summary>
/// WR-T2 (PLAN_windows_runner.md §3.12 "In the panel", the coai plan round 4663beda finding 3): <c>tasks preview cleanup</c>'s report built
/// from the SAME plan as the engine's preview, over an in-memory <c>%TEMP%</c> - it asks for no delete, leaves the cursor where it was,
/// writes nothing, goes through the same guards and bounds the exact paths it lists.
/// </summary>
public sealed class WindowsTempPreviewTests : IDisposable
{
    private static readonly DateTimeOffset Old = TempWorld.Now.AddDays(-10);

    private static readonly DryRunPeek NotStarted = new(true, true, DryWeek.NotStarted, null, "a test says so");

    private readonly TempWorld _world = new();

    public void Dispose() => _world.Dispose();

    private TempPreviewReport Report(string userConfig = "{}", RunTrigger trigger = RunTrigger.Cli)
    {
        var context = _world.Context(userConfig, trigger: trigger);
        return TempPreviews.Report(context, TempSettings.From(context.Config), NotStarted, TestContext.Current.CancellationToken);
    }

    /// <summary>WR-T2 own review, major 1: the preview shows what the TIMER run will do. A person-trigger plan spends the shared scan budget on
    /// names the allow-list does not cover, so its eligible and deferred counts would fall short of the task's; the counts, the groups and the
    /// eligible verdict come from a Timer-trigger plan, and "would be eligible if allowed" from a second plan, flagged when its budget ran out.</summary>
    [Fact]
    public void The_preview_counts_what_the_timer_run_decides_and_says_when_the_would_be_eligible_scan_ran_out_of_budget()
    {
        for (var i = 0; i < 1000; i++)
        {
            _world.Dir($"aaa-{i:0000}", Old).File($@"aaa-{i:0000}\f", Old);
        }

        for (var i = 0; i < 50; i++)
        {
            _world.Dir($"coai-{i:00}", Old).File($@"coai-{i:00}\f", Old);
        }

        var tight = _world.Context();
        var settings = TempSettings.From(tight.Config) with { ScanMaxNodes = 1200 };
        var timerPlan = Planning.Of(tight with { Trigger = RunTrigger.Timer }, settings, TestContext.Current.CancellationToken).Should().BeOfType<Planning.Planned>().Subject.Plan;

        var report = TempPreviews.Report(tight, settings, NotStarted, TestContext.Current.CancellationToken);

        timerPlan.Count(TempVerdict.Eligible).Should().Be(50, "the task will find all fifty: it spends one node on each name it does not scan");
        report.Counts.Eligible.Should().Be(50, "the preview says what the task will do");
        report.Counts.Deferred.Should().Be(timerPlan.Deferred);
        report.WouldBeEligibleLimited.Should().BeTrue("the person-trigger scan of the uncovered names ran out of the budget before it reached them all");
        report.Counts.WouldBeEligible.Should().BeLessThan(1000);
    }

    [Fact]
    public void The_preview_asks_for_no_delete_and_leaves_the_cursor_where_it_was()
    {
        _world.Dir("coai-a", Old).File(@"coai-a\f", Old).Dir("coai-b", Old).File(@"coai-b\f", Old).Dir("sometool-old", Old).File(@"sometool-old\f", Old).Dir("claude", Old).File(@"claude\log", Old);
        var planted = TempCursor.Fresh with { Last = "coai-a", PassStarted = Old, PassDecided = 4 };
        _world.WriteCursor(planted);
        var writes = _world.Writes.Count;

        var report = Report();

        _world.DeleteAsked.Should().BeEmpty("a preview deletes nothing");
        _world.Cursor().Should().Be(planted, "and moves nothing");
        report.Pass.Last.Should().Be("coai-a", "the cursor is said, not moved");
        _world.Writes.Count.Should().Be(writes, "a preview writes no cursor, no stamp, no state directory");
        report.Counts.Eligible.Should().Be(2, "coai-b is after the cursor, and coai-a is reached after the wrap");
    }

    [Fact]
    public void The_exact_paths_are_bounded_by_preview_max_items_per_group_with_the_remainder_counted()
    {
        for (var i = 0; i < 50; i++)
        {
            _world.Dir($"coai-{i:00}", Old).File($@"coai-{i:00}\f", Old);
        }

        for (var i = 0; i < 30; i++)
        {
            _world.Dir($"sometool-{i:00}", Old).File($@"sometool-{i:00}\f", Old);
        }

        for (var i = 0; i < 25; i++)
        {
            var name = $"{(char)('a' + (i / 5))}{(char)('a' + (i % 5))}tool-1";
            _world.Dir(name, Old).File($@"{name}\f", Old);
        }

        var report = Report("""{ "preview": { "maxItems": 5 } }""");

        report.MaxPathsPerGroup.Should().Be(5);
        var eligible = report.Groups.Where(g => g.Verdict == "eligible").Should().ContainSingle().Subject;
        (eligible.Pattern, eligible.Count, eligible.Paths.Count, eligible.More).Should().Be(("coai-*", 50, 5, 45));
        eligible.Bytes.Should().Be(5000);
        var would = report.Groups.Where(g => g.Verdict == "wouldBeEligible").ToList();
        would.Should().HaveCount(5);
        (would[0].Pattern, would[0].Count, would[0].Paths.Count, would[0].More).Should().Be(("sometool-*", 30, 5, 25));
        report.GroupsNotShown.WouldBeEligible.Should().Be(21);
        report.GroupsNotShown.Eligible.Should().Be(0);
        (report.Counts.Eligible, report.Counts.WouldBeEligible).Should().Be((50, 55));
        var json = JsonSerializer.Serialize(report, WslCareJsonContext.Default.TempPreviewReport);
        System.Text.RegularExpressions.Regex.Matches(json, @"\\\\Temp\\\\coai-").Count.Should().BeLessThanOrEqualTo(5, "a dirty temp folder never makes an unbounded answer");
    }

    [Fact]
    public void The_preview_never_lists_claude_and_excludes_another_owner_and_a_reparse_candidate_through_the_same_judge()
    {
        _world.Dir("claude", Old).File(@"claude\log", Old).Dir("coai-theirs", Old, owner: TempWorld.OtherSid).File(@"coai-theirs\f", Old, owner: TempWorld.OtherSid);
        _world.Link("coai-link", Old).Dir("coai-old", Old).File(@"coai-old\f", Old);

        var report = Report();

        report.ExcludedByReason.Should().Contain(new TempExcludedReason(TempJudge.ClaudeReason, 1)).And.Contain(new TempExcludedReason(TempJudge.OwnerReason, 1));
        report.Counts.Unknown.Should().Be(1);
        report.Counts.Eligible.Should().Be(1);
        _world.Listed.Should().NotContain(p => p.EndsWith(@"\claude", StringComparison.OrdinalIgnoreCase) || p.EndsWith("coai-link", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_preview_scans_the_names_the_allow_list_does_not_cover_so_the_would_be_eligible_groups_are_named()
    {
        _world.Dir("sometool-old", Old).File(@"sometool-old\f", Old);

        var cli = Report();
        var timer = Report(trigger: RunTrigger.Timer);

        cli.Groups.Should().ContainSingle(g => g.Verdict == "wouldBeEligible").Which.Pattern.Should().Be("sometool-*");
        cli.Counts.WouldBeEligible.Should().Be(1);
        cli.WouldBeEligibleLimited.Should().BeFalse("the budget was not exhausted");
        timer.Counts.WouldBeEligible.Should().Be(1, "the report plans both ways itself, whatever trigger its context carries: it is a person's preview of the timer's task");
    }

    [Fact]
    public void A_refused_root_answers_the_refusal_with_zero_counts()
    {
        var folders = TempWorld.Folders with { Temp = @"C:\Users\user\AppData\Local\Temp\2" };
        var context = new ActionContext(new WindowsHostPaths(folders), _world, new FixedTimeProvider(TempWorld.Now), TempWorld.Load("{}"), RunTrigger.Cli, new TargetUserResult.None("none")) { UserSid = TempWorld.Sid };

        var report = TempPreviews.Report(context, TempSettings.From(context.Config), NotStarted, TestContext.Current.CancellationToken);

        report.Refusal.Should().Contain("different folders");
        report.Counts.Should().Be(new TempPreviewCounts(0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
        report.Groups.Should().BeEmpty();
    }

    [Fact]
    public async Task The_report_says_what_the_engine_s_preview_says()
    {
        _world.Dir("coai-a", Old).File(@"coai-a\f", Old, 300).Dir("sometool-x", Old).File(@"sometool-x\f", Old).Dir("coai-young", TempWorld.Now.AddDays(-1)).File(@"coai-young\f", TempWorld.Now.AddDays(-1));
        var action = new WindowsTempAction();
        var context = _world.Context();
        var preview = await action.PreviewAsync(context, _world.Commands(action, context), TestContext.Current.CancellationToken);

        var report = Report();

        report.Counts.Eligible.Should().Be(preview.Facts[TempFacts.Eligible]);
        report.Counts.Young.Should().Be(preview.Facts[TempFacts.Young]);
        report.Counts.WouldBeEligible.Should().Be(preview.Facts[TempFacts.WouldBeEligible]);
        report.Counts.Total.Should().Be(preview.Facts[TempFacts.Total]);
        report.EligibleBytes.Should().Be(preview.Bytes);
    }

    [Fact]
    public void The_report_names_the_numbers_in_force()
    {
        var report = Report();

        report.OlderThanDays.Should().Be(7);
        report.AllowGlobs.Should().Equal("coai-*", "wsl-care-test-*", "stage-*", "mindex-*", "git-commit-*", "docrag-*", "bge-seed-*");
        report.CutoffUtc.Should().StartWith(TempWorld.Now.AddDays(-7).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm", System.Globalization.CultureInfo.InvariantCulture));
        (report.ScanMaxNodes, report.ScanMaxSeconds, report.MaxItemsPerRun, report.MaxPathsPerGroup).Should().Be((200000, 120, 5000, 20));
        (report.Task, report.Action).Should().Be(("cleanup", "A23"));
        report.Dry.Should().Be(NotStarted);
    }
}
