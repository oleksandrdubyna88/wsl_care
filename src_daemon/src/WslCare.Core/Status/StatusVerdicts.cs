using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Health;
using WslCare.Core.Thresholds;

namespace WslCare.Core.Status;

/// <summary>The verdicts the newest full run recorded in its detail, with the run and the instant they were evaluated
/// (the end of that run).</summary>
public sealed record RecordedVerdicts(Records.RunId RunId, DateTimeOffset EvaluatedAt, IReadOnlyList<Verdict> Verdicts)
{
    /// <summary>E15.S2: the run's disk section (virtual disks and their hosting volumes), read from the SAME detail file — or why
    /// there is none (a detail written before E15.S2).</summary>
    public Reading<Collect.DiskReport> Disk { get; init; } = Reading.Missing<Collect.DiskReport>("not read");
}

/// <summary>
/// The verdicts of <c>status --json</c> (plan §15g B1) — the SAME <see cref="Verdict"/> records and ids a full run writes,
/// in its order, from ONE evaluator, <see cref="ThresholdRules"/>, never a second copy of a rule:
/// <list type="bullet">
/// <item>the thresholds a fast sample decides (<see cref="ThresholdRules.FromSample"/>) are evaluated NOW, over this sample,
/// with the effective configuration — a threshold changed in a configuration layer changes them at once;</item>
/// <item>every threshold only a full run can judge (it needs a tool, the journal, Docker, the Windows clock, the folder walk)
/// is carried exactly as the newest full run recorded it, with that run and its age — or is <c>unknown</c> with the reason
/// there is none, under the limit the configuration puts in force. Never an invented figure.</item>
/// </list>
/// </summary>
/// <remarks>A carried verdict keeps the limit its run applied (its <c>limit</c> says which); a setting changed since reaches it
/// at the next full run — the honest alternative to re-judging a figure status never read.</remarks>
public static class StatusVerdicts
{
    /// <summary>Why the VM-ceiling verdict of <c>status</c> names no <c>.wslconfig</c> value: the file is read through the
    /// Windows profile the clock probe prints, a slow process <c>status</c> never starts (plan §15b #5).</summary>
    public const string WslConfigReadByFullRun = "status does not read it; the full run (collect) does";

    public static IReadOnlyList<Verdict> From(ProbeSample sample, Reading<RecordedVerdicts> fullRun, EffectiveConfig config, DateTimeOffset now) =>
        From(sample, fullRun, config, now, Reading.Missing<WslConfigAudit>(WslConfigReadByFullRun));

    public static IReadOnlyList<Verdict> From(ProbeSample sample, Reading<RecordedVerdicts> fullRun, EffectiveConfig config, DateTimeOffset now, Reading<WslConfigAudit> hostWslConfig)
    {
        var basis = new VerdictBasis(VerdictSource.Sample, null, sample.SampledAt, 0);
        var fromSample = ThresholdRules.FromSample(
            sample.Vm.Bind(vm => vm.Memory),
            sample.Vm.Bind(vm => vm.RootVolume),
            Reading.Missing<WslConfigAudit>(WslConfigReadByFullRun),
            config);
        var carried = ThresholdRules.UnreadFullRun(config).Select(unread => Carried(unread, fullRun, now)).ToList();
        var sampledHost = SampledHost(sample.Host, hostWslConfig, config).Select(v => v with { Basis = basis }).ToList();
        return [.. fromSample.Select(v => v with { Basis = basis }), .. Replaced(carried, sampledHost)];
    }

    /// <summary>E15.S5: the Windows binary judges its OWN host sample by the full run's rule (<see cref="HostRules"/>); the distro's
    /// binary has no host sample and judges nothing here, so it keeps the carried verdicts.</summary>
    private static IReadOnlyList<Verdict> SampledHost(Reading<HostSample> host, Reading<WslConfigAudit> wslConfig, EffectiveConfig config) => host switch
    {
        Reading<HostSample>.Available { Value: var h } => HostRules.Verdicts(HostMemoryFacts.FromProbe(h, wslConfig), config),
        _ => [],
    };

    /// <summary>E15.S5: a SAMPLED verdict replaces the carried full-run verdict of the same id, in that verdict's place — the newer,
    /// direct reading wins, the order stays the full run's, and no id is listed twice. A sampled id the full run does not judge is
    /// added after them.</summary>
    public static IReadOnlyList<Verdict> Replaced(IReadOnlyList<Verdict> carried, IReadOnlyList<Verdict> sampled) =>
        [.. carried.Select(c => sampled.FirstOrDefault(s => s.Id == c.Id) ?? c), .. sampled.Where(s => carried.All(c => c.Id != s.Id))];

    /// <summary>Verdicts evaluated over THIS answer's own sample (the MCP servers', plan §15q E7.S2d), with the sample's basis.</summary>
    public static IReadOnlyList<Verdict> Sampled(IReadOnlyList<Verdict> verdicts, DateTimeOffset sampledAt) =>
        [.. verdicts.Select(v => v with { Basis = new VerdictBasis(VerdictSource.Sample, null, sampledAt, 0) })];

    /// <summary>The full run's record of <paramref name="unread"/>'s id, or <c>unknown</c> under its limit with the reason.</summary>
    private static Verdict Carried(Verdict unread, Reading<RecordedVerdicts> fullRun, DateTimeOffset now) => fullRun switch
    {
        Reading<RecordedVerdicts>.Available { Value: var run } => Recorded(unread, run, now),
        _ => Unknown(unread, fullRun.ReasonOrEmpty, new VerdictBasis(VerdictSource.FullRun, null, null, null)),
    };

    private static Verdict Recorded(Verdict unread, RecordedVerdicts run, DateTimeOffset now)
    {
        var basis = new VerdictBasis(VerdictSource.FullRun, run.RunId.Text, run.EvaluatedAt, Math.Round((now - run.EvaluatedAt).TotalSeconds));
        return run.Verdicts.FirstOrDefault(v => v.Id == unread.Id) is { } recorded
            ? recorded with { Basis = basis }
            : Unknown(unread, $"the newest full run ({run.RunId}) recorded no {unread.Id} verdict", basis);
    }

    private static Verdict Unknown(Verdict unread, string reason, VerdictBasis basis) =>
        new(unread.Id, Level.Unknown, string.Empty, unread.Limit, reason) { Basis = basis };
}
