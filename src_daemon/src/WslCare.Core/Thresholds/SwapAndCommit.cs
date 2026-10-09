using System.Globalization;

using WslCare.Core.Collectors;
using WslCare.Core.Config;

namespace WslCare.Core.Thresholds;

/// <summary>
/// E14 S5: memory and swap BEFORE the evening — two report verdicts after <c>memory.swap</c>, nothing acts on them.
/// <c>memory.swapFree</c>: the swap LEFT under <c>thresholds.swapFreeWarnGb</c> (a swap smaller than the key is judged by
/// <c>memory.swap</c> only — coai plan round 2026-10-09, finding 1). <c>memory.committed</c>: what the kernel PROMISED
/// (<c>Committed_AS</c>) above <c>thresholds.committedWarnPercent</c> of <c>MemTotal</c> — Linux over-commits, so a warning of
/// promises, never of use. PURE.
/// </summary>
public static class SwapAndCommit
{
    private const double Gib = 1024d * 1024 * 1024;

    public static IReadOnlyList<Verdict> Verdicts(Reading<MemorySnapshot> memory, EffectiveConfig config) =>
        [SwapFree(memory, config.Int(ConfigKeys.Thresholds.SwapFreeWarnGb)), Committed(memory, config.Int(ConfigKeys.Thresholds.CommittedWarnPercent))];

    private static Verdict SwapFree(Reading<MemorySnapshot> memory, int warnGb)
    {
        var limit = Invariant($"warn < {warnGb} GiB of swap left (thresholds.swapFreeWarnGb)");
        const string Reason = "swap LEFT: when it runs out the kernel kills processes (the 2026-10-07 evening had 2.4 GB of 12)";
        return Reading.Combine(memory.Bind(m => m.SwapTotal), memory.Bind(m => m.SwapUsed), (total, used) => (total, free: total - used)) switch
        {
            Reading<(long Total, long Free)>.Available { Value.Total: 0 } => new("memory.swapFree", Level.Ok, "no swap configured", limit, Reason),
            Reading<(long Total, long Free)>.Available { Value: var s } when s.Total < warnGb * Gib =>
                new("memory.swapFree", Level.Ok, Invariant($"{s.Free / Gib:0.0} GiB free of {s.Total / Gib:0.0} GiB: a swap smaller than the key is judged by memory.swap only"), limit, Reason),
            Reading<(long Total, long Free)>.Available { Value: var s } =>
                new("memory.swapFree", s.Free < warnGb * Gib ? Level.Warn : Level.Ok, Invariant($"{s.Free / Gib:0.0} GiB free of {s.Total / Gib:0.0} GiB"), limit, Reason),
            var unread => new("memory.swapFree", Level.Unknown, string.Empty, limit, unread.ReasonOrEmpty),
        };
    }

    private static Verdict Committed(Reading<MemorySnapshot> memory, int warnPercent)
    {
        var limit = Invariant($"warn > {warnPercent} % of MemTotal promised (thresholds.committedWarnPercent)");
        const string Reason = "memory the kernel has PROMISED (Committed_AS) — Linux over-commits, so this warns of promises, never of use (the evening: 104 %)";
        return Reading.Combine(memory.Bind(m => m.Committed), memory.Bind(m => m.Total), (committed, total) => (committed, total)) switch
        {
            Reading<(long Committed, long Total)>.Available { Value: var c } when c.Total > 0 => Share(c.Committed, c.Total, warnPercent, limit, Reason),
            Reading<(long Committed, long Total)>.Available => new("memory.committed", Level.Unknown, string.Empty, limit, "/proc/meminfo reports MemTotal 0"),
            var unread => new("memory.committed", Level.Unknown, string.Empty, limit, unread.ReasonOrEmpty),
        };
    }

    private static Verdict Share(long committed, long total, int warnPercent, string limit, string reason)
    {
        var percent = 100.0 * committed / total;
        return new("memory.committed", percent > warnPercent ? Level.Warn : Level.Ok, Invariant($"{percent:0.0} % of MemTotal ({committed / Gib:0.0} of {total / Gib:0.0} GiB committed)"), limit, reason);
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
