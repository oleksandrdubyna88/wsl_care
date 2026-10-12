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
/// A figure a road did not read is <c>unknown</c> with its reason, never <c>ok</c>. The defaults are PROVISIONAL starting values
/// (research/2026-10-12_host_memory.md).
/// </summary>
public static class HostRules
{
    private const double Gib = 1024d * 1024 * 1024;

    private const string CommitReason = "Windows' commit charge against its commit limit: when the charge reaches the limit, allocations fail";

    private const string CompressionReason = "memory Windows compressed instead of paging it out (the Memory Compression process's working set)";

    private const string VmmemReason = "the WSL VM's working set as Windows sees it (vmmemWSL)";

    public static IReadOnlyList<Verdict> Verdicts(HostMemoryFacts facts, EffectiveConfig config) =>
    [
        Commit(facts, config.Int(ConfigKeys.Thresholds.HostCommitWarnPercent), config.Int(ConfigKeys.Thresholds.HostCommitCriticalPercent)),
        Compression(facts.CompressionBytes, config.Int(ConfigKeys.Thresholds.HostCompressionWarnGb)),
        Vmmem(facts, config.Int(ConfigKeys.WslConfig.VmmemAdviceGb)),
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

    private static Verdict Unknown(string id, string limit, string reason) => new(id, Level.Unknown, string.Empty, limit, reason);

    private static string GibText(long bytes) => Invariant($"{bytes / Gib:0.00} GiB");

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
