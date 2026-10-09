using System.Globalization;

using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Config;

namespace WslCare.Core.Thresholds;

/// <summary>Whether heavy work may START now (E14 S6).</summary>
public enum BusyState
{
    /// <summary>Every pressure was read and none crosses its key: go.</summary>
    Calm,

    /// <summary>At least one read pressure crosses its key: wait.</summary>
    Busy,

    /// <summary>No pressure crosses, but at least one was not read: the signal cannot say — go, and say so.</summary>
    Unknown,
}

/// <summary>The three keys, in percent of wall time.</summary>
public sealed record BusyLimits(double Cpu, double Io, double Memory)
{
    public static BusyLimits From(EffectiveConfig config) => new(
        config.Int(ConfigKeys.Thresholds.CpuPressureWarnPercent),
        config.Int(ConfigKeys.Thresholds.IoPressureWarnPercent),
        config.Int(ConfigKeys.Thresholds.MemoryPressureWarn));
}

/// <summary>One pressure over its key: which, over which window, the value, the limit and the key that set it.</summary>
public sealed record BusyReason(string Resource, string Window, double Value, double Limit, string Key);

/// <summary>The answer, with every crossing named and every pressure that could not be read, as <c>resource: why</c>.</summary>
public sealed record BusyJudgement(BusyState State, IReadOnlyList<BusyReason> Reasons, IReadOnlyList<string> Unread);

/// <summary>
/// E14 S6: the ONE rule for "is the machine too busy to start heavy work now" — <c>wsl-care busy</c> and the
/// <c>pressure.cpu</c> / <c>pressure.io</c> verdicts of <c>status</c> both ask it. PSI <c>some avg60</c> of cpu, io and
/// memory, each against its key: a stable minute, where <c>avg10</c> would flap and <c>avg300</c> would hold work back for
/// minutes after the pressure ended. (<c>memory.pressure</c> is a different question — has the VM been struggling — and keeps
/// its avg60-or-avg300 rule.) PURE.
/// </summary>
public static class MachineBusy
{
    /// <summary>The PSI window judged.</summary>
    public const string Window = "avg60";

    private sealed record Resource(string Name, Reading<Pressure> Reading, double Limit, string Key);

    public static BusyJudgement Judge(PressureSet pressure, BusyLimits limits)
    {
        var resources = Resources(pressure, limits);
        var reasons = resources.SelectMany(Crossing).ToList();
        var unread = resources.Where(r => !r.Reading.IsAvailable).Select(r => $"{r.Name}: {r.Reading.ReasonOrEmpty}").ToList();
        var state = reasons.Count > 0 ? BusyState.Busy : unread.Count > 0 ? BusyState.Unknown : BusyState.Calm;
        return new BusyJudgement(state, reasons, unread);
    }

    /// <summary>The <c>pressure.cpu</c> and <c>pressure.io</c> verdicts over a sample's PSI — warn above the key, the same
    /// comparison <see cref="Judge"/> makes. Memory is <c>memory.pressure</c>'s own verdict.</summary>
    public static IReadOnlyList<Verdict> Verdicts(Reading<MemorySnapshot> memory, EffectiveConfig config)
    {
        var limits = BusyLimits.From(config);
        return
        [
            Verdict("pressure.cpu", memory, m => m.Pressure.Cpu, limits.Cpu, ConfigKeys.Thresholds.CpuPressureWarnPercent.Name, "cpu pressure (PSI): tasks waiting for a CPU — the machine is too busy to start heavy work (wsl-care busy)"),
            Verdict("pressure.io", memory, m => m.Pressure.Io, limits.Io, ConfigKeys.Thresholds.IoPressureWarnPercent.Name, "io pressure (PSI): tasks waiting for the disk — the machine is too busy to start heavy work (wsl-care busy)"),
        ];
    }

    private static Verdict Verdict(string id, Reading<MemorySnapshot> memory, Func<MemorySnapshot, Reading<Pressure>> pick, double limit, string key, string reason)
    {
        var limitText = Invariant($"warn: some {Window} > {limit:0.##} ({key})");
        return memory switch
        {
            Reading<MemorySnapshot>.Available { Value: var m } => FromPressure(id, pick(m), limit, limitText, reason),
            // coai plan round 2026-10-09, finding 1: say what was not read — the sample that carries the PSI.
            _ => new(id, Level.Unknown, string.Empty, limitText, $"the memory sample was not read ({memory.ReasonOrEmpty}), so its PSI was not either"),
        };
    }

    private static Verdict FromPressure(string id, Reading<Pressure> pressure, double limit, string limitText, string reason) => pressure switch
    {
        Reading<Pressure>.Available { Value.Some: var s } => new(id, s.Avg60 > limit ? Level.Warn : Level.Ok, Invariant($"some avg10 {s.Avg10:0.##}, avg60 {s.Avg60:0.##}"), limitText, reason),
        var unread => new(id, Level.Unknown, string.Empty, limitText, unread.ReasonOrEmpty),
    };

    private static IReadOnlyList<Resource> Resources(PressureSet pressure, BusyLimits limits) =>
    [
        new("cpu", pressure.Cpu, limits.Cpu, ConfigKeys.Thresholds.CpuPressureWarnPercent.Name),
        new("io", pressure.Io, limits.Io, ConfigKeys.Thresholds.IoPressureWarnPercent.Name),
        new("memory", pressure.Memory, limits.Memory, ConfigKeys.Thresholds.MemoryPressureWarn.Name),
    ];

    private static IEnumerable<BusyReason> Crossing(Resource resource) => resource.Reading switch
    {
        Reading<Pressure>.Available { Value.Some.Avg60: var avg60 } when avg60 > resource.Limit => [new BusyReason(resource.Name, Window, avg60, resource.Limit, resource.Key)],
        _ => [],
    };

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
