using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Health;

namespace WslCare.Core.Tests.Health;

/// <summary>
/// E15.S10: the PURE rule over Windows Defender's raw counters. The fixture is the MEASURED reading line of
/// research/2026-10-16_defender_cpu_cost.md (<see cref="Measured"/>) and a second read of the SAME process built from it: on this raw
/// class <c>ElapsedTime</c> is the process START instant and stays constant across reads while <c>Timestamp_Sys100NS</c> advances
/// (six reads measured 2026-10-16) — so the second line changes only the CPU ticks and the timestamp.
/// </summary>
public sealed class DefenderCpuTests
{
    /// <summary>The line the script printed on the owner's machine (research/2026-10-16_defender_cpu_cost.md): 99 351 s of CPU over a
    /// process that started 130 351 s earlier — 0.76 cores since it started.</summary>
    private const string Measured = "defender=993513593750|134366539776100740|6964|134365236258808755";

    private const long TenSeconds = 10 * DefenderCpu.TicksPerSecond;

    private static DefenderReading First => Read(Measured);

    /// <summary>Ten seconds later, 10.3 core-seconds more CPU, the same process (pid and start marker unchanged).</summary>
    private static DefenderReading Second => First with { CpuTicks100ns = First.CpuTicks100ns + (103 * DefenderCpu.TicksPerSecond / 10), Timestamp100ns = First.Timestamp100ns + TenSeconds };

    private static DefenderReading Read(string line) =>
        DefenderParsers.Parse(line + "\n").Should().BeOfType<Reading<DefenderReading>.Available>(line).Subject.Value;

    private static DefenderUsage Usage(Reading<DefenderUsage> reading) =>
        reading.Should().BeOfType<Reading<DefenderUsage>.Available>(reading.ReasonOrEmpty).Subject.Value;

    [Fact]
    public void The_measured_line_reads_as_the_owners_machine_printed_it()
    {
        First.Should().Be(new DefenderReading(993_513_593_750, 134_366_539_776_100_740, 6964, 134_365_236_258_808_755));
    }

    [Fact]
    public void Two_reads_of_one_process_ten_seconds_apart_that_added_10_3_core_seconds_are_1_03_cores_covering_ten_seconds()
    {
        var usage = Usage(DefenderCpu.Between(First, Second));

        usage.Cores.Should().BeApproximately(1.03, 1e-9, "CPU time summed over all cores over wall time is cores");
        usage.Covered.Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void A_pair_of_the_same_reading_is_no_interval()
    {
        DefenderCpu.Between(First, First).IsAvailable.Should().BeFalse("the same instant twice covers no time");
    }

    [Fact]
    public void A_pid_change_is_a_new_baseline_not_a_difference()
    {
        var restarted = Second with { Pid = 7040, StartMarker = First.StartMarker + (3600 * DefenderCpu.TicksPerSecond), CpuTicks100ns = 4_000_000_000 };

        var between = DefenderCpu.Between(First, restarted);

        between.IsAvailable.Should().BeFalse("another process is not a continuation of this one");
        between.ReasonOrEmpty.Should().Be("Defender restarted — a new baseline");
    }

    [Fact]
    public void The_same_pid_with_a_different_start_marker_is_a_restart_even_when_the_counter_is_larger()
    {
        // Windows reuses pids, and an engine update restarts MsMpEng without a reboot: pid 6964 is a NEW process whose counter happens to be larger.
        var reused = Second with { StartMarker = First.StartMarker + (7200 * DefenderCpu.TicksPerSecond), CpuTicks100ns = First.CpuTicks100ns + (500 * DefenderCpu.TicksPerSecond) };

        var between = DefenderCpu.Between(First, reused);

        between.IsAvailable.Should().BeFalse("the start marker tells two processes of one pid apart, whatever their counters say");
        between.ReasonOrEmpty.Should().Be(DefenderCpu.Restarted);
    }

    [Fact]
    public void A_decreasing_counter_gives_no_difference()
    {
        var wrapped = Second with { CpuTicks100ns = First.CpuTicks100ns - 1 };

        var between = DefenderCpu.Between(First, wrapped);

        between.IsAvailable.Should().BeFalse("a counter that went down is not negative usage");
        between.ReasonOrEmpty.Should().Contain("counter");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-3600)]
    public void A_non_positive_timestamp_interval_gives_no_difference(long seconds)
    {
        var outOfOrder = Second with { Timestamp100ns = First.Timestamp100ns + (seconds * DefenderCpu.TicksPerSecond) };

        var between = DefenderCpu.Between(First, outOfOrder);

        between.IsAvailable.Should().BeFalse("two reads at the same instant, or in the wrong order, cover no time (and dividing by it is infinity or a negative)");
        between.ReasonOrEmpty.Should().Contain("interval");
    }

    [Fact]
    public void A_counter_that_did_not_move_is_zero_cores_over_the_interval_not_missing()
    {
        var idle = Usage(DefenderCpu.Between(First, Second with { CpuTicks100ns = First.CpuTicks100ns }));

        idle.Cores.Should().Be(0);
        idle.Covered.Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void A_missing_defender_line_is_unavailable_with_the_reason_never_zero()
    {
        var none = DefenderParsers.Parse("something else=1\n");
        var empty = DefenderParsers.Parse(string.Empty);

        none.IsAvailable.Should().BeFalse();
        none.ReasonOrEmpty.Should().Contain("printed no defender line");
        empty.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public void No_msmpeng_process_is_said_in_its_own_words()
    {
        var none = DefenderParsers.Parse("defender.none=\n");

        none.IsAvailable.Should().BeFalse();
        none.ReasonOrEmpty.Should().Be("MsMpEng is not running");
    }

    [Fact]
    public void A_failed_read_names_the_exception_type()
    {
        var failed = DefenderParsers.Parse("defender.error=CimException\n");

        failed.IsAvailable.Should().BeFalse();
        failed.ReasonOrEmpty.Should().Contain("CimException");
    }

    [Theory]
    [InlineData("defender=1|2|3")]
    [InlineData("defender=1|2|3|4|5")]
    [InlineData("defender=a|2|3|4")]
    [InlineData("defender=1|2|3.5|4")]
    [InlineData("defender=1|2|99999999999|4")]
    [InlineData("defender=||||")]
    [InlineData("defender=")]
    public void A_line_that_does_not_parse_is_unavailable_never_a_zero_reading(string line)
    {
        var parsed = DefenderParsers.Parse(line + "\n");

        parsed.IsAvailable.Should().BeFalse(line);
        parsed.ReasonOrEmpty.Should().Contain("does not parse");
    }

    [Fact]
    public void Crlf_line_ends_read_like_lf()
    {
        DefenderParsers.Parse(Measured + "\r\n").Should().BeOfType<Reading<DefenderReading>.Available>();
    }

    // ----- the week -----

    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private static DefenderSpan Span(TimeSpan agoStart, TimeSpan length, double cores) => new(Now - agoStart, Now - agoStart + length, cores);

    private static DefenderWeek Week(Reading<DefenderWeek> reading) =>
        reading.Should().BeOfType<Reading<DefenderWeek>.Available>(reading.ReasonOrEmpty).Subject.Value;

    [Fact]
    public void Two_spans_of_twelve_hours_each_over_seven_days_cover_24_hours_and_the_week_is_extrapolated()
    {
        var week = Week(DefenderCpu.Week(
            [
                Span(TimeSpan.FromDays(5), TimeSpan.FromHours(12), 0.5),
                Span(TimeSpan.FromDays(2), TimeSpan.FromHours(12), 1.0),
            ], Now));

        week.CoveredHours.Should().BeApproximately(24, 1e-9);
        week.Extrapolated.Should().BeTrue("24 covered hours of 168");
        week.CoreHoursPerWeek.Should().BeApproximately((6.0 + 12.0) / 24 * 168, 1e-9, "18 core-hours over 24 covered hours, scaled to a week");
    }

    [Fact]
    public void A_restart_gap_between_spans_adds_no_covered_hours()
    {
        // The engine restarted between the two spans: nothing was measured for the 30 hours in between, and none are counted.
        var week = Week(DefenderCpu.Week(
            [
                Span(TimeSpan.FromDays(6), TimeSpan.FromHours(12), 0.5),
                Span(TimeSpan.FromDays(6) - TimeSpan.FromHours(42), TimeSpan.FromHours(12), 0.5),
            ], Now));

        week.CoveredHours.Should().BeApproximately(24, 1e-9, "the gap is not a span");
    }

    [Fact]
    public void Spans_covering_the_whole_week_are_not_extrapolated()
    {
        var week = Week(DefenderCpu.Week([Span(TimeSpan.FromDays(7), TimeSpan.FromDays(7), 0.76)], Now));

        week.CoveredHours.Should().BeApproximately(168, 1e-9);
        week.Extrapolated.Should().BeFalse();
        week.CoreHoursPerWeek.Should().BeApproximately(0.76 * 168, 1e-9);
    }

    [Fact]
    public void A_span_that_began_before_the_window_counts_only_its_part_inside_it()
    {
        var week = Week(DefenderCpu.Week([Span(TimeSpan.FromDays(8), TimeSpan.FromDays(2), 1.0)], Now));

        week.CoveredHours.Should().BeApproximately(24, 1e-9, "the day inside the window, not the day before it");
    }

    [Fact]
    public void Spans_older_than_the_window_or_none_at_all_give_no_week_never_a_zero()
    {
        DefenderCpu.Week([], Now).IsAvailable.Should().BeFalse();
        DefenderCpu.Week([Span(TimeSpan.FromDays(20), TimeSpan.FromHours(12), 1.0)], Now).IsAvailable.Should().BeFalse("nothing in the last 7 days");
        DefenderCpu.Week([], Now).ReasonOrEmpty.Should().Contain("7 days");
    }
}
