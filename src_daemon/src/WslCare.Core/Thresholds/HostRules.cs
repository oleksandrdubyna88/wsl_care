using System.Globalization;

using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Health;

namespace WslCare.Core.Thresholds;

/// <summary>
/// E15.S5: Windows memory pressure — ONE pure rule over <see cref="HostMemoryFacts"/>, whichever road read them (the Windows
/// binary's own sample, or the distro's full run through the Windows disk script). Read-only: nothing is acted on.
/// <list type="bullet">
/// <item><c>host.commit</c>: the commit charge's share of the commit limit; warn above <c>thresholds.hostCommitWarnPercent</c>,
/// critical above <c>thresholds.hostCommitCriticalPercent</c>.</item>
/// <item><c>host.compression</c>: the Memory Compression process's working set; warn above <c>thresholds.hostCompressionWarnGb</c> GiB.</item>
/// <item><c>host.vmmem</c>: the WSL VM's working set as Windows sees it; warn above the EXISTING <c>wslConfig.vmmemAdviceGb</c> (one
/// number for one idea), and its reason IS <see cref="VmmemAdvice"/>'s text, so the advice and the verdict cannot disagree.</item>
/// </list>
/// <item><c>host.defenderCpu</c> (E15.S10): Windows Defender's engine (<c>MsMpEng</c>) averaged more than <c>thresholds.defenderCoresWarnPercent</c> % of ONE
/// core between this full run's read and the previous one's (two reads of the same process, <see cref="DefenderCpu.Between"/>); unknown with the
/// reason when there is no span (the first read, a restart, a failed read) and on the Windows binary, which cannot read it.</item>
/// A figure a road did not read is <c>unknown</c> with its reason, never <c>ok</c>. The defaults are PROVISIONAL starting values
/// (research/2026-10-12_host_memory.md).
/// </summary>
public static class HostRules
{
    private const double Gib = 1024d * 1024 * 1024;

    private const string CommitReason = "Windows' commit charge against its commit limit: when the charge reaches the limit, allocations fail";

    private const string CompressionReason = "memory Windows compressed instead of paging it out (the Memory Compression process's working set)";

    private const string VmmemReason = "the WSL VM's working set as Windows sees it (vmmemWSL)";

    private const string DefenderReason = "Windows Defender's engine (MsMpEng): CPU time summed over all cores per second of wall time, between two reads of the same process a full run apart";

    /// <summary>The Windows binary's road, over its own sample — the SAME list as the full run's (E15.S10 §B): its vmmem is its own
    /// reading, and Defender's CPU, which it cannot read, is <c>unknown</c> saying the distro's full run reads it.</summary>
    public static IReadOnlyList<Verdict> Verdicts(HostMemoryFacts facts, EffectiveConfig config) => FullRunVerdicts(facts, config);

    /// <summary>The full run's road: commit, compression, vmmem (the Windows disk script reads it since E15.S10 §B, which closed E15.S5's open
    /// tail) and Defender's CPU (E15.S10) — one list on both roads, judged by the SAME functions.</summary>
    public static IReadOnlyList<Verdict> FullRunVerdicts(HostMemoryFacts facts, EffectiveConfig config) =>
    [
        Commit(facts, config.Int(ConfigKeys.Thresholds.HostCommitWarnPercent), config.Int(ConfigKeys.Thresholds.HostCommitCriticalPercent)),
        Compression(facts.CompressionBytes, config.Int(ConfigKeys.Thresholds.HostCompressionWarnGb)),
        Vmmem(facts, config.Int(ConfigKeys.WslConfig.VmmemAdviceGb)),
        Defender(facts.Defender, config.Int(ConfigKeys.Thresholds.DefenderCoresWarnPercent)),
    ];

    private static Verdict Commit(HostMemoryFacts facts, int warnPercent, int criticalPercent)
    {
        var limit = Invariant($"warn > {warnPercent} %, critical > {criticalPercent} % of the commit limit (thresholds.hostCommitWarnPercent, thresholds.hostCommitCriticalPercent)");
        return Reading.Combine(facts.CommitBytes, facts.CommitLimitBytes, (charge, max) => (charge, max)) switch
        {
            Reading<(long Charge, long Max)>.Available { Value: var c } when c.Max > 0 =>
                Share(c.Charge, c.Max, warnPercent, criticalPercent, limit),
            Reading<(long Charge, long Max)>.Available => Unknown("host.commit", limit, "Windows reports a commit limit of 0"),
            var unread => Unknown("host.commit", limit, unread.ReasonOrEmpty),
        };
    }

    private static Verdict Share(long charge, long max, int warnPercent, int criticalPercent, string limit)
    {
        var percent = 100.0 * charge / max;
        return new("host.commit", CommitLevel(percent, warnPercent, criticalPercent), Invariant($"{charge / Gib:0.0} of {max / Gib:0.0} GiB = {percent:0.0} %"), limit, CommitReason);
    }

    private static Level CommitLevel(double percent, int warnPercent, int criticalPercent) =>
        percent > criticalPercent ? Level.Critical : percent > warnPercent ? Level.Warn : Level.Ok;

    private static Verdict Compression(Reading<long> bytes, int warnGb)
    {
        var limit = Invariant($"warn > {warnGb} GiB (thresholds.hostCompressionWarnGb)");
        return bytes switch
        {
            Reading<long>.Available { Value: var b } => new("host.compression", b > warnGb * Gib ? Level.Warn : Level.Ok, GibText(b), limit, CompressionReason),
            var unread => Unknown("host.compression", limit, unread.ReasonOrEmpty),
        };
    }

    /// <summary>The level follows the advice: a vmmem the advice speaks about warns, and the advice is the reason.</summary>
    private static Verdict Vmmem(HostMemoryFacts facts, int adviceGb)
    {
        var limit = Invariant($"warn > {adviceGb} GiB (wslConfig.vmmemAdviceGb)");
        var advice = VmmemAdvice.For(facts.VmmemBytes, facts.HostTotalBytes, facts.WslConfig, adviceGb);
        return facts.VmmemBytes switch
        {
            Reading<long>.Available { Value: var b } when advice.Length > 0 => new("host.vmmem", Level.Warn, GibText(b), limit, advice),
            Reading<long>.Available { Value: var b } => new("host.vmmem", Level.Ok, GibText(b), limit, VmmemReason),
            var unread => Unknown("host.vmmem", limit, unread.ReasonOrEmpty),
        };
    }

    /// <summary>More than <paramref name="warnPercent"/> % of one core warns; exactly that is ok.</summary>
    private static Verdict Defender(Reading<DefenderUsage> usage, int warnPercent)
    {
        var limit = Invariant($"warn > {warnPercent} % of one core (thresholds.defenderCoresWarnPercent)");
        return usage switch
        {
            Reading<DefenderUsage>.Available { Value: var used } => new("host.defenderCpu", used.Cores > warnPercent / 100.0 ? Level.Warn : Level.Ok, CoresText(used), limit, DefenderReason),
            var unread => Unknown("host.defenderCpu", limit, unread.ReasonOrEmpty),
        };
    }

    private static string CoresText(DefenderUsage used) => Invariant($"{used.Cores:0.00} cores over {used.Covered.TotalHours:0.0} h");

    private static Verdict Unknown(string id, string limit, string reason) => new(id, Level.Unknown, string.Empty, limit, reason);

    private static string GibText(long bytes) => Invariant($"{bytes / Gib:0.00} GiB");

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
