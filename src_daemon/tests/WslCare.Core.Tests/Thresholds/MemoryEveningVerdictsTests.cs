using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Config;
using WslCare.Core.Health;
using WslCare.Core.Thresholds;

namespace WslCare.Core.Tests.Thresholds;

/// <summary>
/// E14 S5: memory and swap BEFORE the evening — a report. <c>memory.swapFree</c> warns when the swap LEFT is under
/// <c>thresholds.swapFreeWarnGb</c> (the 2026-10-07 evening had 2.4 GB of 12 left); <c>memory.committed</c> warns when what the
/// kernel PROMISED (<c>Committed_AS</c>) passes <c>thresholds.committedWarnPercent</c> of <c>MemTotal</c> (the evening: 104 %;
/// the captured calm tree: 46 %). Nothing acts on them.
/// </summary>
public sealed class MemoryEveningVerdictsTests
{
    private const long Gib = 1L << 30;

    private static EffectiveConfig Defaults() =>
        ConfigLoader.Load([(ConfigLoader.DefaultsFile, new Core.Files.FileReadResult.Content(ConfigLoader.EmbeddedDefaults()))]).Config;

    private static MemorySnapshot Memory(double totalGib = 44.9, double swapTotalGib = 12, double swapUsedGib = 0, double? committedGib = 20.7) =>
        new MemorySnapshot(
            Reading.Of((long)(totalGib * Gib)),
            Reading.Of((long)(totalGib * Gib / 2)),
            Reading.Of(50.0),
            Reading.Of(1 * Gib),
            Reading.Of(2 * Gib),
            Reading.Of(1 * Gib),
            Reading.Of(1 * Gib),
            Reading.Of(2 * Gib),
            Reading.Of((long)(swapTotalGib * Gib)),
            Reading.Of((long)(swapUsedGib * Gib)),
            Reading.Of(new Fragmentation("Normal", 4000, 3900, 4000 * 65536, 3900 * 524288)),
            new PressureSet(Reading.Missing<Pressure>("n/a"), Reading.Missing<Pressure>("n/a"), Reading.Missing<Pressure>("n/a")))
        {
            Committed = committedGib is { } c ? Reading.Of((long)(c * Gib)) : Reading.Missing<long>("Committed_AS is not in meminfo"),
        };

    private static Verdict Judge(MemorySnapshot memory, string id) =>
        ThresholdRules.FromSample(Reading.Of(memory), Reading.Missing<VolumeUsage>("n/a"), Reading.Missing<WslConfigAudit>("n/a"), Defaults()).Single(v => v.Id == id);

    [Fact]
    public void Swap_left_below_its_key_warns_and_no_swap_is_ok()
    {
        var evening = Judge(Memory(swapUsedGib: 9.6), "memory.swapFree");
        var calm = Judge(Memory(), "memory.swapFree");
        var none = Judge(Memory(swapTotalGib: 0), "memory.swapFree");
        var small = Judge(Memory(swapTotalGib: 2), "memory.swapFree");

        evening.Should().Match<Verdict>(v => v.Level == Level.Warn && v.Value == "2.4 GiB free of 12.0 GiB" && v.Limit.Contains("thresholds.swapFreeWarnGb"));
        calm.Level.Should().Be(Level.Ok);
        none.Should().Match<Verdict>(v => v.Level == Level.Ok && v.Value.Contains("no swap configured"));
        // coai plan round 2026-10-09 (session 42b21ed8), finding 1: a swap smaller than the key is not "low" when it is all free.
        small.Should().Match<Verdict>(v => v.Level == Level.Ok && v.Value.Contains("smaller than the key"));
    }

    [Fact]
    public void Committed_above_its_share_of_MemTotal_warns_and_the_calm_tree_is_ok()
    {
        var evening = Judge(Memory(totalGib: 44.9, committedGib: 46.7), "memory.committed");
        var calm = Judge(Memory(), "memory.committed");
        var unread = Judge(Memory(committedGib: null), "memory.committed");

        evening.Should().Match<Verdict>(v => v.Level == Level.Warn && v.Value.StartsWith("104.0 % of MemTotal") && v.Limit.Contains("thresholds.committedWarnPercent"));
        calm.Should().Match<Verdict>(v => v.Level == Level.Ok && v.Value.StartsWith("46.1 % of MemTotal"));
        unread.Should().Match<Verdict>(v => v.Level == Level.Unknown && v.Reason.Contains("Committed_AS"));
    }

    [Fact]
    public void The_new_keys_have_their_defaults_and_ranges()
    {
        var config = Defaults();

        config.Int(ConfigKeys.Thresholds.SwapFreeWarnGb).Should().Be(4);
        config.Int(ConfigKeys.Thresholds.CommittedWarnPercent).Should().Be(80);
        config.Int(ConfigKeys.WslConfig.RecommendedSwapGb).Should().Be(16);
        ConfigValidation.Parse(ConfigKeys.Thresholds.CommittedWarnPercent, "0").Should().BeOfType<ValueCheck.Invalid>();
    }
}
