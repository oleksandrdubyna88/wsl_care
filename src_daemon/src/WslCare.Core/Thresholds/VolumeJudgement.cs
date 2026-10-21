using WslCare.Core.Collectors;

namespace WslCare.Core.Thresholds;

/// <summary>
/// One lettered Windows volume as <c>disk.volumeFree</c> judged it (E19.S1) — the per-volume half of the answer, so the status
/// section <c>disk.lettered</c> can carry it and the extension never parses the verdict's value text.
/// </summary>
/// <param name="Letter">The drive letter, without the colon.</param>
/// <param name="SizeBytes">The volume's size — or why it was not read.</param>
/// <param name="FreeBytes">The volume's free space — or why it was not read.</param>
/// <param name="Level">The volume's own level. <see cref="Thresholds.Level.Unknown"/> for a volume that is not judged or whose figure was not read.</param>
/// <param name="JudgedBy"><c>gib-or-percent</c> (the GiB floors and the percent floors), <c>percent</c> (percent alone: a volume under the
/// absolute-size floor) or <c>not judged: &lt;why&gt;</c> (too small, an ignored letter, a figure not read).</param>
/// <param name="InVerdict">Whether the volume counts toward the verdict: judged, or a judged-size volume whose figure was not read (then
/// the verdict is <c>unknown</c> at best). A volume that is too small or ignored is listed but not counted.</param>
public sealed record VolumeJudgement(string Letter, Reading<long> SizeBytes, Reading<long> FreeBytes, Level Level, string JudgedBy, bool InVerdict)
{
    /// <summary>The share of the volume that is free, in percent — or why it cannot be said (a figure not read, a volume of no size).</summary>
    public Reading<double> FreePercent =>
        Reading.Combine(SizeBytes, FreeBytes, (size, free) => (size, free))
            .Bind(p => p.size > 0 ? Reading.Of(p.free * 100d / p.size) : Reading.Missing<double>("the volume reports no size"));
}

/// <summary>What <see cref="VolumeFreeRules"/> says: the verdict, and the judgement of every volume of the sample behind it.</summary>
/// <param name="Verdict"><c>disk.volumeFree</c>.</param>
/// <param name="Volumes">Every volume of the sample, judged or not, in the sample's order; empty when the sample was unavailable.</param>
public sealed record VolumeFreeAssessment(Verdict Verdict, IReadOnlyList<VolumeJudgement> Volumes);
