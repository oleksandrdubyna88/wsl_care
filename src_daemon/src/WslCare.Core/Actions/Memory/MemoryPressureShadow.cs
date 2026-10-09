using System.Globalization;

using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Config;
using WslCare.Core.Thresholds;

namespace WslCare.Core.Actions.Memory;

/// <summary>
/// E14 S5, SHADOW only: A1 and A2 record the memory pressure (PSI some avg60) as a fact and their trigger REASON says whether the
/// pressure rule — the S6 rule, memory some avg60 above <c>thresholds.memoryPressureWarn</c> (<see cref="MachineBusy.Crosses"/>) —
/// WOULD have fired. Whether they fire does not change: the plan asks for a measurement against the baseline before pressure may
/// trigger them, and the timer's run records are that measurement (the S8 soak decides).
/// </summary>
public static class MemoryPressureShadow
{
    /// <summary>The fact: memory PSI some avg60 in hundredths of a percent.</summary>
    public const string Fact = "memoryPressureAvg60Hundredths";

    public const string WouldFire = "the memory-pressure rule WOULD fire";

    public const string WouldNotFire = "the memory-pressure rule would not fire";

    public const string NotRead = "memory PSI not read";

    /// <summary>A fact is a whole number; avg60 is kept to two decimals.</summary>
    private const double Hundredths = 100;

    /// <summary>The fact to add, or none when the pressure was not read.</summary>
    public static IReadOnlyList<KeyValuePair<string, long>> Facts(Reading<MemorySnapshot> memory) =>
        memory.Bind(m => m.Pressure.Memory) is Reading<Pressure>.Available { Value.Some.Avg60: var avg60 }
            ? [new(Fact, (long)Math.Round(avg60 * Hundredths))]
            : [];

    /// <summary>The sentence a trigger reason ends with.</summary>
    public static string Sentence(IReadOnlyDictionary<string, long> facts, EffectiveConfig config)
    {
        if (!facts.TryGetValue(Fact, out var hundredths))
        {
            return $"; report only (E14 S5): {NotRead}";
        }

        var avg60 = hundredths / Hundredths;
        var limit = config.Int(ConfigKeys.Thresholds.MemoryPressureWarn);
        var verdict = MachineBusy.Crosses(avg60, limit) ? WouldFire : WouldNotFire;
        return string.Create(CultureInfo.InvariantCulture, $"; report only (E14 S5): memory PSI some avg60 {avg60:0.##} — {verdict} (above {limit}, thresholds.memoryPressureWarn)");
    }
}
