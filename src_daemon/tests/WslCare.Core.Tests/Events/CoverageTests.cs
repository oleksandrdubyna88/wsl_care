using FluentAssertions;

using WslCare.Core.Docker;
using WslCare.Core.Events;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Events;

/// <summary>Plan §15b #0 and #8 as pure rules: what a backfill can prove, what a 24-hour count may claim, how long
/// the next wait for Docker is. Event lines are SYNTHETIC in the captured shape (<see cref="DockerEventLines"/>).</summary>
public sealed class CoverageTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private static DockerEvent Start(string id, DateTimeOffset at, bool tc = false) =>
        DockerEventLines.Parsed(DockerEventLines.Start(id, "c-" + id, tc ? "testcontainers/ryuk" : "postgres:17", at, tc));

    private static DockerEvent Exec(DateTimeOffset at) => DockerEventLines.Parsed(DockerEventLines.Exec("e", at));

    [Fact]
    public void The_wait_for_docker_starts_at_5_s_doubles_and_never_passes_5_minutes()
    {
        var waits = new List<TimeSpan>();
        TimeSpan? last = null;
        for (var i = 0; i < 9; i++)
        {
            last = Coverage.NextBackoff(last);
            waits.Add(last.Value);
        }

        waits.Select(w => w.TotalSeconds).Should().Equal(5, 10, 20, 40, 80, 160, 300, 300, 300);
    }

    [Fact]
    public void A_buffer_that_reaches_back_past_the_last_marker_fills_the_gap_and_records_only_newer_starts()
    {
        var anchor = Now.AddMinutes(-30);
        IReadOnlyList<DockerEvent> buffered = [Exec(Now.AddMinutes(-50)), Start("old", Now.AddMinutes(-40)), Start("new", Now.AddMinutes(-10)), Exec(Now.AddMinutes(-5))];

        var plan = Coverage.Plan(anchor, buffered, Now);

        plan.Gap.Should().BeNull("the oldest buffered event (50 min ago) is before the marker (30 min ago): the buffer proves the gap");
        plan.Starts.Select(s => s.Id).Should().Equal("new");
        plan.CoveredUntil.Should().Be(Now);
    }

    [Fact]
    public void A_daemon_restart_that_lost_the_buffer_is_ONE_unrecoverable_gap_from_the_marker_to_the_oldest_buffered_event()
    {
        var anchor = Now.AddHours(-2);
        var restartedAt = Now.AddMinutes(-20);
        IReadOnlyList<DockerEvent> buffered = [Start("a", restartedAt), Exec(Now.AddMinutes(-15)), Start("b", Now.AddMinutes(-5))];

        var plan = Coverage.Plan(anchor, buffered, Now);

        plan.Gap.Should().NotBeNull();
        plan.Gap!.From.Should().Be(anchor);
        plan.Gap.To.Should().Be(restartedAt);
        plan.Gap.Reason.Should().Contain("reaches back only to");
        plan.Starts.Select(s => s.Id).Should().Equal("a", "b");
    }

    [Fact]
    public void An_empty_buffer_cannot_prove_anything_so_the_whole_stretch_is_one_gap_up_to_now()
    {
        var anchor = Now.AddMinutes(-30);

        var plan = Coverage.Plan(anchor, [], Now);

        plan.Gap.Should().Be(new CoverageLine.Gap(anchor, Now, plan.Gap!.Reason));
        plan.Gap.Reason.Should().Contain("empty");
        plan.Starts.Should().BeEmpty();
    }

    [Fact]
    public void A_marker_older_than_the_24_hour_window_is_a_gap_whatever_the_buffer_holds()
    {
        var anchor = Now.AddHours(-30);
        IReadOnlyList<DockerEvent> buffered = [Start("x", Now.AddHours(-23))];

        var plan = Coverage.Plan(anchor, buffered, Now);

        plan.Gap!.From.Should().Be(anchor);
        plan.Gap.To.Should().Be(Now.AddHours(-23));
        plan.Gap.Reason.Should().Contain("24 h");
    }

    [Fact]
    public void The_first_start_ever_is_a_gap_from_the_window_start_to_the_oldest_buffered_event()
    {
        IReadOnlyList<DockerEvent> buffered = [Exec(Now.AddHours(-3)), Start("s", Now.AddHours(-1))];

        var plan = Coverage.Plan(anchor: null, buffered, Now);

        plan.Gap!.From.Should().Be(Now - Coverage.BackfillWindow);
        plan.Gap.To.Should().Be(Now.AddHours(-3));
        plan.Gap.Reason.Should().Contain("first start");
        plan.Starts.Select(s => s.Id).Should().Equal("s");
    }

    [Fact]
    public void A_full_day_of_coverage_counts_complete_and_names_the_top_images()
    {
        IReadOnlyList<CoverageLine> lines =
        [
            new CoverageLine.Covered(Now.AddHours(-30)),
            Line(Now.AddHours(-25), "too-old"),
            Line(Now.AddHours(-20), "a"),
            Line(Now.AddHours(-10), "b", tc: true),
            Line(Now.AddHours(-1), "c"),
            new CoverageLine.Covered(Now.AddMinutes(-2)),
        ];

        var window = Coverage.Last24h(lines, Now);

        window.Complete.Should().BeTrue();
        window.Gaps.Should().BeEmpty();
        window.Starts.Should().Be(3);
        window.Testcontainers.Should().Be(1);
        window.TopImages.Should().Contain(new ImageCount("postgres:17", 2));
    }

    [Fact]
    public void A_count_overlapping_a_gap_is_partial_and_names_it_until_a_whole_24_h_lies_after_its_end()
    {
        var gapEnd = Now.AddHours(-23);
        IReadOnlyList<CoverageLine> lines =
        [
            new CoverageLine.Covered(Now.AddHours(-48)),
            new CoverageLine.Gap(Now.AddHours(-26), gapEnd, "the daemon restarted"),
            Line(Now.AddHours(-2), "a"),
            new CoverageLine.Covered(Now.AddMinutes(-1)),
        ];

        var partial = Coverage.Last24h(lines, Now);
        var later = Coverage.Last24h([.. lines, new CoverageLine.Covered(gapEnd.AddHours(24).AddMinutes(1))], gapEnd.AddHours(24).AddMinutes(1));

        partial.Complete.Should().BeFalse();
        partial.Gaps.Should().ContainSingle().Which.Reason.Should().Be("the daemon restarted");
        later.Complete.Should().BeTrue("a whole 24 h now lies after the gap's end");
    }

    [Fact]
    public void A_follower_that_stopped_recording_makes_the_count_partial_with_an_open_gap_up_to_now()
    {
        IReadOnlyList<CoverageLine> lines =
        [
            new CoverageLine.Covered(Now.AddHours(-30)),
            new CoverageLine.FollowerStopped(Now.AddHours(-3), 42, Now.AddHours(-3)),
        ];

        var window = Coverage.Last24h(lines, Now);

        window.Complete.Should().BeFalse();
        window.Gaps.Should().ContainSingle().Which.Should().Match<WindowGap>(g => g.From == Now.AddHours(-3) && g.To == Now);
    }

    [Fact]
    public void Nothing_recorded_before_the_window_is_partial_with_the_gap_before_the_first_record()
    {
        IReadOnlyList<CoverageLine> lines = [new CoverageLine.Covered(Now.AddHours(-5)), new CoverageLine.Covered(Now.AddMinutes(-1))];

        var window = Coverage.Last24h(lines, Now);

        window.Complete.Should().BeFalse();
        window.Gaps.Should().ContainSingle().Which.Should().Match<WindowGap>(g => g.From == Now - Coverage.CountWindow && g.To == Now.AddHours(-5));
    }

    [Fact]
    public void The_last_coverage_is_the_newest_instant_a_line_proves_and_a_start_marker_proves_none()
    {
        IReadOnlyList<CoverageLine> lines =
        [
            new CoverageLine.Covered(Now.AddHours(-2)),
            new CoverageLine.FollowerStopped(Now.AddHours(-1), 1, Now.AddHours(-2)),
            new CoverageLine.FollowerStarted(Now, 2),
        ];

        Coverage.LastCovered(lines).Should().Be(Now.AddHours(-2));
        Coverage.LastCovered([new CoverageLine.FollowerStarted(Now, 2)]).Should().BeNull();
    }

    private static CoverageLine.Start Line(DateTimeOffset at, string id, bool tc = false) =>
        new(at, id, "c-" + id, tc ? "testcontainers/ryuk" : "postgres:17", tc, false);
}
