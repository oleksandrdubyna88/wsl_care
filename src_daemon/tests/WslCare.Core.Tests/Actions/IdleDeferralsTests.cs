using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Archive;
using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Actions;

/// <summary>
/// The streak <c>archive.maxDeferHours</c> is measured over (2026-10-12): the action's LIVE timer deferrals, newest first, until
/// any other line that names it; lines that do not name it are ignored. And the one deferral it may override: the load alone.
/// </summary>
public sealed class IdleDeferralsTests
{
    private static readonly DateTimeOffset Now = FixedTimeProvider.DefaultNow;

    private static RunRecord Line(double hoursAgo, string status, string id = "A13", RunTrigger trigger = RunTrigger.Timer, bool? dryRun = false) =>
        MaxDeferTests.Line(hoursAgo, status, id, trigger, dryRun);

    private static DateTimeOffset? Start(params RunRecord[] newestLast) => IdleDeferrals.StreakStart(newestLast, "A13");

    [Fact]
    public void The_streak_starts_at_the_earliest_of_the_trailing_live_timer_deferrals()
    {
        Start(Line(30, ActionStatus.Deferred), Line(20, ActionStatus.Deferred), Line(10, ActionStatus.Deferred)).Should().Be(Now.AddHours(-30));
    }

    [Theory]
    [InlineData(ActionStatus.Ran)]
    [InlineData(ActionStatus.Failed)]
    [InlineData(ActionStatus.Refused)]
    [InlineData(ActionStatus.Interrupted)]
    [InlineData(ActionStatus.Skipped)]
    [InlineData(ActionStatus.DryRun)]
    public void Any_other_status_ends_the_walk(string status)
    {
        Start(Line(30, ActionStatus.Deferred), Line(20, status), Line(10, ActionStatus.Deferred)).Should().Be(Now.AddHours(-10));
        Start(Line(30, ActionStatus.Deferred), Line(10, status)).Should().BeNull("the newest line is not a deferral: no streak");
    }

    [Fact]
    public void A_dry_run_a_line_without_dryRun_or_status_and_a_button_run_are_never_evidence()
    {
        Start(Line(30, ActionStatus.Deferred), Line(20, ActionStatus.Deferred, dryRun: true), Line(10, ActionStatus.Deferred)).Should().Be(Now.AddHours(-10));
        Start(Line(30, ActionStatus.Deferred), Line(20, ActionStatus.Deferred, dryRun: null), Line(10, ActionStatus.Deferred)).Should().Be(Now.AddHours(-10));
        Start(Line(30, ActionStatus.Deferred), Line(20, ActionStatus.Deferred, trigger: RunTrigger.Manual), Line(10, ActionStatus.Deferred)).Should().Be(Now.AddHours(-10));
        var noStatus = Line(20, ActionStatus.Deferred) with { Actions = [new ActionRecord("A13", 0, 0)] };
        Start(Line(30, ActionStatus.Deferred), noStatus, Line(10, ActionStatus.Deferred)).Should().Be(Now.AddHours(-10));
    }

    [Fact]
    public void Lines_that_do_not_name_the_action_are_ignored_and_no_lines_is_no_streak()
    {
        Start(Line(30, ActionStatus.Deferred), Line(20, ActionStatus.Ran, id: "A1"), Line(10, ActionStatus.Deferred)).Should().Be(Now.AddHours(-30));
        Start().Should().BeNull();
        Start(Line(5, ActionStatus.Ran, id: "A1")).Should().BeNull();
    }

    [Fact]
    public void The_order_of_the_lines_is_their_start_not_their_place_in_the_file()
    {
        Start(Line(10, ActionStatus.Deferred), Line(20, ActionStatus.Ran), Line(30, ActionStatus.Deferred)).Should().Be(Now.AddHours(-10), "the ran line 20 h ago is newer than the 30-hour deferral");
    }

    // ---- the one deferral the limit overrides ----

    private static IdleVerdict Judge(Reading<double> busy, Reading<IReadOnlyList<string>> builds) =>
        IdleGate.Judge(new IdleSample(busy, "5 min", builds), ConfigLoader.Load([(ConfigLoader.DefaultsFile, new FileReadResult.Content(ConfigLoader.EmbeddedDefaults()))]).Config);

    [Fact]
    public void Only_a_busy_load_with_the_builds_read_and_none_running_is_load_only()
    {
        Judge(Reading.Of(60.0), Reading.Of<IReadOnlyList<string>>([])).LoadOnly.Should().BeTrue();
        Judge(Reading.Of(60.0), Reading.Of<IReadOnlyList<string>>(["dotnet build x"])).LoadOnly.Should().BeFalse("a build runs beside the load");
        Judge(Reading.Of(60.0), Reading.Missing<IReadOnlyList<string>>("no table")).LoadOnly.Should().BeFalse("the builds are unknown");
        Judge(Reading.Missing<double>("no loadavg"), Reading.Of<IReadOnlyList<string>>([])).LoadOnly.Should().BeFalse("the load is unknown");
        Judge(Reading.Of(5.0), Reading.Of<IReadOnlyList<string>>(["dotnet build x"])).LoadOnly.Should().BeFalse("a build alone");
        Judge(Reading.Of(5.0), Reading.Of<IReadOnlyList<string>>([])).LoadOnly.Should().BeFalse("idle");
    }

    /// <summary>The plan round's #0: at the history's retention floor the streak's first deferral could be pruned the moment it came due.</summary>
    [Fact]
    public void The_limit_stays_below_the_histories_retention_floor()
    {
        ConfigKeys.Archive.MaxDeferHours.Max.Should().BeLessThan(ConfigKeys.Runs.HistoryRetentionDays.Min * 24);
        ConfigKeys.Archive.MaxDeferHours.Min.Should().Be(0, "0 means never");
        ConfigKeys.Archive.MaxDeferHours.Trust.Should().Be(new KeyTrust(SafeDirection.Higher, ZeroIsUnbounded: true), "waiting longer is the safe direction; 0, never, the safest");
        new ArchiveAction().MaxDeferHours.Should().BeSameAs(ConfigKeys.Archive.MaxDeferHours);
    }
}
