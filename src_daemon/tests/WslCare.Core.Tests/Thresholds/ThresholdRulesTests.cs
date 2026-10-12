using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Config;
using WslCare.Core.Health;
using WslCare.Core.Preview;
using WslCare.Core.Records;
using WslCare.Core.Thresholds;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Thresholds;

/// <summary>
/// Plan §4's thresholds at their edges (plan §12: "each threshold … at its edge"), the 2026-10-01 18:36 state and a
/// fresh boot, the two-observation clock rule (§15 #10), the 36 GB <c>.wslconfig</c> recommendation, and an unread
/// figure is <c>unknown</c> — never <c>ok</c>. Memory snapshots are SYNTHETIC, in the collector's own types.
/// </summary>
public sealed class ThresholdRulesTests : IDisposable
{
    private const long Gib = 1L << 30;
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private readonly SandboxHost _sandbox = new("thresholds");

    public void Dispose() => _sandbox.Dispose();

    private EffectiveConfig Config(string userJson = "")
    {
        if (userJson.Length > 0)
        {
            _sandbox.WriteUserConfig(userJson);
        }

        return ConfigLoader.Load(_sandbox.Paths, _sandbox.Files).Config;
    }

    private static MemorySnapshot Memory(double availablePercent, long totalGib = 46, long order7 = 100, long order4 = 1000, long pageCacheGib = 5, long swapUsedGib = 0, double psi60 = 0) =>
        new(
            Reading.Of(totalGib * Gib),
            Reading.Of((long)(totalGib * Gib * availablePercent / 100)),
            Reading.Of(availablePercent),
            Reading.Of(1 * Gib),
            Reading.Of(10 * Gib),
            Reading.Of(2 * Gib),
            Reading.Of(1 * Gib),
            Reading.Of(pageCacheGib * Gib),
            Reading.Of(8 * Gib),
            Reading.Of(swapUsedGib * Gib),
            Reading.Of(new Fragmentation("Normal", order4, order7, order4 * 65536, order7 * 524288)),
            new PressureSet(Reading.Of(new Pressure(new PressureLine(psi60, psi60, psi60, 0), Reading.Missing<PressureLine>("none"))), Reading.Missing<Pressure>("n/a"), Reading.Missing<Pressure>("n/a")))
        {
            // E14 S5: what a calm machine promised (the captured 2026-10-02 tree: 46 % of MemTotal).
            Committed = Reading.Of(totalGib * Gib * 46 / 100),
        };

    private static HealthSample Health(int allocationFailures = 0, WindowsClockSample? clock = null)
    {
        var unread = HealthSample.Unavailable(Now.AddHours(-4), "not read in this test", clock ?? new WindowsClockSample(Now, 0, 0, "not read in this test"), Reading.Missing<string>("n/a"), Reading.Missing<WslConfigAudit>("not read in this test"));
        return unread with { Kernel = Reading.Of(new KernelSignals(allocationFailures, 0, [])) };
    }

    private IReadOnlyList<Verdict> Evaluate(Reading<MemorySnapshot> memory, HealthSample? health = null, Reading<WindowsClockSample>? previous = null, double rootPercent = 13, IReadOnlyList<CleanupRow>? rows = null, string config = "") =>
        ThresholdRules.Evaluate(
            new ThresholdInputs(
                memory,
                Reading.Of(Root(rootPercent)),
                health ?? Health(),
                TimeSpan.FromHours(4),
                previous ?? Reading.Missing<WindowsClockSample>("no earlier run"),
                rows ?? [],
                Reading.Missing<long>("not read"),
                Reading.Missing<long>("not read")),
            Config(config));

    private static VolumeUsage Root(double usedPercent) => new("/", 1000, (long)(usedPercent * 10), 1000 - (long)(usedPercent * 10));

    private static Verdict Find(IReadOnlyList<Verdict> verdicts, string id) => verdicts.Single(v => v.Id == id);

    [Theory]
    [InlineData(25.0, Level.Ok)]
    [InlineData(24.9, Level.Warn)]
    [InlineData(15.0, Level.Warn)]
    [InlineData(14.9, Level.Critical)]
    public void MemAvailable_is_judged_against_the_two_settings_at_their_edges(double percent, Level expected) =>
        Find(Evaluate(Reading.Of(Memory(percent))), "memory.available").Level.Should().Be(expected);

    [Fact]
    public void The_settings_move_the_edge()
    {
        var verdict = Find(Evaluate(Reading.Of(Memory(35)), config: """{ "thresholds": { "memAvailableWarnPercent": 40 } }"""), "memory.available");

        verdict.Level.Should().Be(Level.Warn);
        verdict.Limit.Should().Contain("warn < 40 %");
    }

    [Fact]
    public void The_2026_10_01_18_36_state_is_critical_on_memory_fragmentation_and_the_vm_ceiling()
    {
        // 0.27 GB free of 46 GB and no free order >= 4 block (plan §1, §12).
        var verdicts = Evaluate(Reading.Of(Memory(availablePercent: 0.6, order7: 0, order4: 0)), Health(allocationFailures: 1));

        Find(verdicts, "memory.available").Level.Should().Be(Level.Critical);
        Find(verdicts, "memory.fragmentation").Level.Should().Be(Level.Critical);
        Find(verdicts, "wslconfig.memory").Level.Should().Be(Level.Critical);
        Find(verdicts, "kernel.allocationFailures").Level.Should().Be(Level.Critical);
    }

    [Fact]
    public void A_fresh_boot_is_ok_on_every_memory_threshold()
    {
        var verdicts = Evaluate(Reading.Of(Memory(availablePercent: 90)));

        verdicts.Where(v => v.Id.StartsWith("memory.", StringComparison.Ordinal) || v.Id == "wslconfig.memory").Should().OnlyContain(v => v.Level == Level.Ok);
    }

    [Theory]
    [InlineData(31, Level.Warn)]
    [InlineData(32, Level.Ok)]
    [InlineData(0, Level.Critical)]
    public void Free_order_7_blocks_warn_below_32_and_are_critical_at_none(long blocks, Level expected) =>
        Find(Evaluate(Reading.Of(Memory(50, order7: blocks))), "memory.fragmentation").Level.Should().Be(expected);

    [Theory]
    [InlineData(4, Level.Ok)]
    [InlineData(5, Level.Warn)]
    public void Swap_warns_above_the_setting(long usedGib, Level expected) =>
        Find(Evaluate(Reading.Of(Memory(50, swapUsedGib: usedGib))), "memory.swap").Level.Should().Be(expected);

    [Theory]
    [InlineData(80.0, Level.Ok)]
    [InlineData(80.1, Level.Warn)]
    public void The_root_filesystem_warns_above_80_percent(double percent, Level expected) =>
        Find(Evaluate(Reading.Of(Memory(50)), rootPercent: percent), "disk.root").Level.Should().Be(expected);

    [Fact]
    public void The_vm_ceiling_row_shows_the_36_GB_recommendation_and_is_red_only_above_90_percent()
    {
        var at89 = Find(Evaluate(Reading.Of(Memory(availablePercent: 11))), "wslconfig.memory");
        var at91 = Find(Evaluate(Reading.Of(Memory(availablePercent: 9))), "wslconfig.memory");

        at89.Level.Should().Be(Level.Ok);
        at91.Level.Should().Be(Level.Critical);
        at91.Limit.Should().Contain("memory=36GB").And.Contain("never written");
    }

    [Fact]
    public void A_single_clock_observation_above_the_limit_is_not_reported_as_drift()
    {
        var verdict = Find(Evaluate(Reading.Of(Memory(50)), Health(clock: new WindowsClockSample(Now, -9, 0.6, string.Empty))), "clock.drift");

        verdict.Level.Should().Be(Level.Ok);
        verdict.Reason.Should().Contain("a second, at least 5 minutes later");
    }

    [Theory]
    [InlineData(300, Level.Warn)]
    [InlineData(299, Level.Ok)]
    public void A_drift_needs_two_observations_at_least_5_minutes_apart(int secondsApart, Level expected)
    {
        var previous = Reading.Of(new WindowsClockSample(Now.AddSeconds(-secondsApart), -8, 0.6, string.Empty));

        Find(Evaluate(Reading.Of(Memory(50)), Health(clock: new WindowsClockSample(Now, -9, 0.6, string.Empty)), previous), "clock.drift").Level.Should().Be(expected);
    }

    [Fact]
    public void An_unread_figure_is_unknown_with_its_reason_never_ok()
    {
        var verdicts = Evaluate(Reading.Missing<MemorySnapshot>("/proc/meminfo does not exist"));

        Find(verdicts, "memory.available").Should().Match<Verdict>(v => v.Level == Level.Unknown && v.Reason.Contains("/proc/meminfo", StringComparison.Ordinal) && v.Value.Length == 0);
        Find(verdicts, "journal.size").Level.Should().Be(Level.Unknown);
    }

    [Fact]
    public void Too_many_old_anonymous_volumes_warn_as_A4_s_trigger()
    {
        IReadOnlyList<CleanupRow> rows = [new("A4", "A4", "anonymous volumes", ConfigKeys.Auto.A4, true, "sizes", Reading.Of(new RowFigures(101, 1, 0, [])), string.Empty)];

        Find(Evaluate(Reading.Of(Memory(50)), rows: rows), "docker.A4").Level.Should().Be(Level.Warn);
        Find(Evaluate(Reading.Of(Memory(50)), rows: [rows[0] with { Figures = Reading.Of(new RowFigures(100, 1, 0, [])) }]), "docker.A4").Level.Should().Be(Level.Ok);
    }

    // ---- research/2026-10-12_wsl_clock_fight.md: journal.history from the reading's instant; clock.jumps and Docker's floor ----

    /// <summary>The 2026-10-12 08:03:54Z reading: the previous run started 10-11 22:10Z, the oldest entry is 10-10 07:48Z.</summary>
    private static HealthSample Journal(DateTimeOffset since, DateTimeOffset readAt, DateTimeOffset oldest) =>
        Health() with { Since = since, JournalOldestEntry = Reading.Of(oldest), JournalReadAt = Reading.Of(readAt) };

    /// <summary>A sample whose boot is <paramref name="uptimeHours"/> old — 10 h by default, so the 4-h rotation count covers
    /// the whole 4-h window <see cref="Evaluate"/> judges the clock changes over.</summary>
    private static HealthSample Steps(int clockChanges, Reading<int> rotations, double uptimeHours = 10) =>
        Health() with { ClockJumps = Reading.Of(clockChanges), TimeJumpsBack = rotations, Uptime = Reading.Of(TimeSpan.FromHours(uptimeHours)) };

    [Fact]
    public void A_window_that_spans_a_reboot_applies_no_floor_because_the_rotations_cover_only_this_boot()
    {
        var spanning = Find(Evaluate(Reading.Of(Memory(50)), Steps(480, Reading.Of(2), uptimeHours: 3.15)), "clock.jumps");

        spanning.Level.Should().Be(Level.Warn, "the 2026-10-12 capture: a VM restarted 3.15 h ago, the clock changes counted over 4 h, the backward jumps over this boot only");
        spanning.Reason.Should().Contain("do not cover");
        Find(Evaluate(Reading.Of(Memory(50)), Steps(480, Reading.Of(2), uptimeHours: 4)), "clock.jumps").Level.Should().Be(Level.Ok, "a boot exactly as old as the window covers it");
        Find(Evaluate(Reading.Of(Memory(50)), Health() with { ClockJumps = Reading.Of(480), TimeJumpsBack = Reading.Of(2) }), "clock.jumps").Level
            .Should().Be(Level.Warn, "an unread uptime cannot show the rotations cover the window");
        Find(Evaluate(Reading.Of(Memory(50)), Steps(480, Reading.Of(11), uptimeHours: 1)), "clock.jumps").Reason
            .Should().Contain("clock.fight counts 11", "damage seen in part of the window is damage");
    }

    [Fact]
    public void Journal_history_counts_its_days_from_the_moment_the_journal_was_read_not_from_the_window_start()
    {
        var at = new DateTimeOffset(2026, 10, 12, 8, 3, 54, TimeSpan.Zero);
        var health = Journal(new DateTimeOffset(2026, 10, 11, 22, 10, 0, TimeSpan.Zero), at, new DateTimeOffset(2026, 10, 10, 7, 48, 0, TimeSpan.Zero));

        Find(Evaluate(Reading.Of(Memory(50)), health), "journal.history").Value.Should().StartWith("2.0 days", "08:03:54Z minus 10-10 07:48Z is 2.01 days; the previous run's start gave 1.6");
    }

    [Fact]
    public void Clock_fight_and_clock_jumps_judge_the_backward_jumps_by_the_same_effective_limit()
    {
        var verdicts = Evaluate(Reading.Of(Memory(50)), Steps(480, Reading.Of(7)), config: """{ "thresholds": { "timeJumpsBackWarnPer4h": 5 } }""");

        Find(verdicts, "clock.jumps").Level.Should().Be(Level.Warn, "7 backward jumps are above the effective limit of 5");
        Find(verdicts, "clock.fight").Level.Should().Be(Level.Warn, "the same 7 against the same effective limit (code round 2: it read the process's tuning instead)");
    }

    [Fact]
    public void Journal_history_without_the_instant_it_was_read_is_unknown_never_a_value_from_another_instant()
    {
        var health = Health() with { JournalOldestEntry = Reading.Of(Now.AddDays(-9)) };

        Find(Evaluate(Reading.Of(Memory(50)), health), "journal.history").Should().Match<Verdict>(v => v.Level == Level.Unknown && v.Value.Length == 0 && v.Reason.Contains("instant"));
    }

    [Theory]
    [InlineData(100, 0, Level.Ok)] // at the ordinary limit
    [InlineData(480, 3, Level.Ok)] // Docker Desktop's 30-s step, no journal damage (the measured 2026-10-12 steady state)
    [InlineData(600, 10, Level.Ok)] // at both edges: the floor and thresholds.timeJumpsBackWarnPer4h
    [InlineData(480, 11, Level.Warn)] // the journal is being rotated
    [InlineData(601, 0, Level.Warn)] // above the floor
    [InlineData(638, 95, Level.Warn)] // the 2026-10-12 10:02Z run
    [InlineData(100, 11, Level.Warn)] // within the ordinary limit, but the journal is being rotated (code round 2)
    public void Clock_jumps_at_the_rate_of_a_periodic_stepper_warn_only_with_journal_damage_or_above_the_floor(int perFourHours, int rotations, Level expected) =>
        Find(Evaluate(Reading.Of(Memory(50)), Steps(perFourHours, Reading.Of(rotations))), "clock.jumps").Level.Should().Be(expected);

    [Fact]
    public void Clock_jumps_above_the_limit_with_the_rotations_unread_still_warn() =>
        Find(Evaluate(Reading.Of(Memory(50)), Steps(480, Reading.Missing<int>("journal unread"))), "clock.jumps").Level
            .Should().Be(Level.Warn, "no journal-damage reading is no proof of none");

    [Fact]
    public void Clock_jumps_name_docker_desktop_s_step_and_both_settings()
    {
        var floor = Find(Evaluate(Reading.Of(Memory(50)), Steps(480, Reading.Of(3))), "clock.jumps");
        var warn = Find(Evaluate(Reading.Of(Memory(50)), Steps(638, Reading.Of(95))), "clock.jumps");

        floor.Reason.Should().Contain("Docker Desktop's /initd").And.Contain("clock.fight");
        warn.Reason.Should().Contain("Docker Desktop's /initd").And.Contain("clock.fight");
        floor.Limit.Should().Contain("thresholds.clockJumpsWarnPer4h").And.Contain("thresholds.clockJumpsStepperFloorPer4h").And.Contain("600");
    }

    [Fact]
    public void The_stepper_floor_is_a_setting()
    {
        var verdict = Find(Evaluate(Reading.Of(Memory(50)), Steps(480, Reading.Of(0)), config: """{ "thresholds": { "clockJumpsStepperFloorPer4h": 400 } }"""), "clock.jumps");

        verdict.Level.Should().Be(Level.Warn);
        verdict.Limit.Should().Contain("400");
    }

    [Fact]
    public void Every_threshold_has_one_verdict_under_its_own_id()
    {
        var verdicts = Evaluate(Reading.Of(Memory(50)));

        verdicts.Select(v => v.Id).Should().OnlyHaveUniqueItems();
        verdicts.Should().HaveCountGreaterThan(20);
    }
}
