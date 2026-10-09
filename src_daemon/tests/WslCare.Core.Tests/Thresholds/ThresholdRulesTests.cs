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

    [Fact]
    public void Every_threshold_has_one_verdict_under_its_own_id()
    {
        var verdicts = Evaluate(Reading.Of(Memory(50)));

        verdicts.Select(v => v.Id).Should().OnlyHaveUniqueItems();
        verdicts.Should().HaveCountGreaterThan(20);
    }
}
