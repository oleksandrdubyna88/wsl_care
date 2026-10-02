using FluentAssertions;

using WslCare.Core.Collectors;
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

    /// <summary>The engine answering now (<paramref name="id"/>, started <paramref name="startedAt"/>) and the one the last
    /// marker recorded (<paramref name="previousId"/>; the same engine unless told otherwise).</summary>
    private static EngineEvidence Engine(DateTimeOffset startedAt, string id = "bridge-a", string previousId = "bridge-a", DateTimeOffset? previousStartedAt = null) =>
        new(Reading.Of(new EngineMark(id, startedAt)), Reading.Of(new EngineMark(previousId, previousStartedAt ?? startedAt)));

    /// <summary><paramref name="count"/> healthcheck events, one a second, the newest a second before <see cref="Now"/>.</summary>
    private static IReadOnlyList<DockerEvent> Busy(int count) => [.. Enumerable.Range(1, count).Select(i => Exec(Now.AddSeconds(-i)))];

    [Fact]
    public void An_idle_engine_with_an_empty_buffer_and_no_restart_is_covered_with_zero_starts()
    {
        // Gate finding #2/#7/#9: an empty buffer is not by itself proof of loss. Nothing was dropped from a ring that is
        // not full, and the engine has run since three days before the marker: nothing happened.
        var anchor = Now.AddMinutes(-30);

        var plan = Coverage.Plan(anchor, [], Now, Engine(Now.AddDays(-3)));

        plan.Gap.Should().BeNull("an idle engine that did not restart dropped nothing");
        plan.Starts.Should().BeEmpty();
        plan.CoveredUntil.Should().Be(Now);
    }

    [Fact]
    public void A_host_that_slept_with_the_engine_suspended_and_the_same_engine_after_is_covered()
    {
        // Six hours asleep: the clock jumps, the buffer holds only what happened since waking, and the engine is the same
        // instance (same bridge, same start) - so its short buffer still holds everything since the marker.
        var anchor = Now.AddHours(-6);
        IReadOnlyList<DockerEvent> buffered = [Exec(Now.AddMinutes(-2)), Start("woke", Now.AddMinutes(-1))];

        var plan = Coverage.Plan(anchor, buffered, Now, Engine(Now.AddDays(-2)));

        plan.Gap.Should().BeNull("a suspended engine is not a restarted one");
        plan.Starts.Select(s => s.Id).Should().Equal("woke");
    }

    [Fact]
    public void A_full_buffer_whose_oldest_event_is_newer_than_the_marker_is_ONE_gap_up_to_that_event()
    {
        var anchor = Now.AddHours(-2);
        var buffered = Busy(Coverage.FullAt);

        var plan = Coverage.Plan(anchor, buffered, Now, Engine(Now.AddDays(-2)));

        plan.Gap.Should().NotBeNull("a full ring has dropped its oldest events");
        plan.Gap!.From.Should().Be(anchor);
        plan.Gap.To.Should().Be(buffered.Min(e => e.At));
        plan.Gap.Reason.Should().Contain("full");
    }

    [Fact]
    public void A_full_buffer_that_still_reaches_back_past_the_marker_is_covered()
    {
        var anchor = Now.AddSeconds(-Coverage.FullAt / 2);

        var plan = Coverage.Plan(anchor, Busy(Coverage.FullAt), Now, Engine(Now.AddDays(-2)));

        plan.Gap.Should().BeNull("its oldest event is older than the marker: nothing after the marker was dropped");
    }

    [Fact]
    public void An_engine_that_restarted_after_the_marker_is_a_gap_from_the_marker_to_its_start_and_the_starts_after_are_recorded()
    {
        var anchor = Now.AddHours(-2);
        var restartedAt = Now.AddMinutes(-20);
        IReadOnlyList<DockerEvent> buffered = [Start("a", Now.AddMinutes(-19)), Exec(Now.AddMinutes(-10)), Start("b", Now.AddMinutes(-5))];

        var plan = Coverage.Plan(anchor, buffered, Now, Engine(restartedAt, id: "bridge-b", previousId: "bridge-a", previousStartedAt: Now.AddDays(-2)));

        plan.Gap.Should().Be(new CoverageLine.Gap(anchor, restartedAt, plan.Gap!.Reason));
        plan.Gap.Reason.Should().Contain("restarted");
        plan.Starts.Select(s => s.Id).Should().Equal("a", "b");
    }

    [Fact]
    public void A_restart_into_a_still_empty_buffer_is_a_gap_too()
    {
        var anchor = Now.AddHours(-1);
        var restartedAt = Now.AddMinutes(-3);

        var plan = Coverage.Plan(anchor, [], Now, Engine(restartedAt, id: "bridge-b"));

        plan.Gap.Should().Be(new CoverageLine.Gap(anchor, restartedAt, plan.Gap!.Reason));
        plan.Gap.Reason.Should().Contain("restarted");
    }

    [Fact]
    public void A_different_engine_that_claims_to_have_started_before_the_marker_proves_nothing_beyond_its_oldest_event()
    {
        // Another engine on the socket (Docker Desktop instead of a dockerd inside the distro): its start says nothing
        // about the buffer the marker came from, so only the conservative rule is left.
        var anchor = Now.AddMinutes(-30);

        var plan = Coverage.Plan(anchor, [], Now, Engine(Now.AddDays(-5), id: "bridge-other", previousId: "bridge-a", previousStartedAt: Now.AddDays(-1)));

        plan.Gap.Should().Be(new CoverageLine.Gap(anchor, Now, plan.Gap!.Reason));
        plan.Gap.Reason.Should().Contain("different Docker engine");
    }

    [Fact]
    public void An_engine_whose_start_cannot_be_read_falls_back_to_the_oldest_buffered_event()
    {
        var anchor = Now.AddMinutes(-30);
        var unread = new EngineEvidence(Reading.Missing<EngineMark>("commandFailed: no bridge network"), Reading.Missing<EngineMark>("none"));

        var plan = Coverage.Plan(anchor, [], Now, unread);

        plan.Gap.Should().Be(new CoverageLine.Gap(anchor, Now, plan.Gap!.Reason));
        plan.Gap.Reason.Should().Contain("empty").And.Contain("no bridge network");
    }

    [Fact]
    public void The_first_start_on_an_engine_older_than_the_window_with_a_short_buffer_proves_the_whole_window()
    {
        var plan = Coverage.Plan(anchor: null, [Exec(Now.AddMinutes(-1))], Now, Engine(Now.AddDays(-3)));

        plan.Gap.Should().BeNull("the ring is not full and the engine ran the whole 24 h");
    }

    [Fact]
    public void The_first_start_on_an_engine_that_started_inside_the_window_is_a_gap_up_to_its_start()
    {
        var startedAt = Now.AddHours(-2);

        var plan = Coverage.Plan(anchor: null, [Exec(Now.AddMinutes(-1))], Now, Engine(startedAt));

        plan.Gap.Should().Be(new CoverageLine.Gap(Now - Coverage.BackfillWindow, startedAt, plan.Gap!.Reason));
        plan.Gap.Reason.Should().Contain("first start");
    }

    [Fact]
    public void The_buffer_capacity_is_the_engines_ring_and_full_errs_below_every_measured_full_answer()
    {
        Coverage.BufferCapacity.Should().Be(256, "moby's daemon/events eventsLimit");
        Coverage.FullAt.Should().BeLessThanOrEqualTo(248, "the smallest answer a full buffer gave on 2026-10-02 must read as full");
    }

    [Fact]
    public void A_covered_marker_round_trips_its_engine_through_the_file_shape()
    {
        var mark = new EngineMark("bridge-a", Now.AddDays(-1));
        var line = new CoverageLine.Covered(Now) { Engine = Reading.Of(mark) };

        var back = CoverageLineJson.Of(line).ToLine().Single();

        back.Should().BeOfType<CoverageLine.Covered>().Which.Engine.Should().Be(Reading.Of(mark));
        Coverage.LastEngine([new CoverageLine.Covered(Now.AddMinutes(-20)) { Engine = Reading.Of(new EngineMark("old", Now.AddDays(-9))) }, back, new CoverageLine.Covered(Now.AddMinutes(5))])
            .Should().Be(Reading.Of(mark), "the newest marker that RECORDED an engine");
    }

    [Fact]
    public void A_fresh_summary_answers_its_window_and_a_stale_one_is_partial_with_the_open_gap()
    {
        var window = Coverage.Last24h([new CoverageLine.Gap(Now.AddDays(-2), Now.AddDays(-1.5), "old"), new CoverageLine.Start(Now.AddHours(-1), "a", "a", "redis:7", false, false), new CoverageLine.Covered(Now)], Now);
        var summary = new StartsSummary(1, Now, Now, window);

        Coverage.AsOf(summary, Now.AddMinutes(5)).Should().Be(window, "five minutes after the last marker the follower is still on time");
        var stale = Coverage.AsOf(summary, Now.AddHours(1));
        stale.Complete.Should().BeFalse();
        stale.Starts.Should().Be(window.Starts, "the count is the follower's, as of when it wrote it");
        stale.Gaps.Should().ContainSingle(g => g.From == Now && g.To == Now.AddHours(1));
        Coverage.NoSummary(Now).Should().Match<StartsWindow>(w => !w.Complete && w.Starts == 0 && w.Gaps.Single().Reason == Coverage.NoSummaryReason);
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
