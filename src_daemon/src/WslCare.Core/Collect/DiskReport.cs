using WslCare.Core.Collectors;
using WslCare.Core.Health;
using WslCare.Core.Status;
using WslCare.Core.Thresholds;

namespace WslCare.Core.Collect;

// The wire shape of E15.S2's disk facts (PLAN_disk_health.md): in the run detail (`disk`) and, with its basis, in `status --json`
// (`disk`). As everywhere at the JSON edge, a figure is "available": true with its value or "available": false with a reason; the
// whole section unavailable carries one reason and no list. Its own records, so a change to the domain cannot change a saved detail.

/// <summary>One virtual disk: the file, its volume, how its guest usage was found, the gap, the growth and the possible growth.</summary>
/// <param name="MappedBy"><c>mount</c> (this distro's own <c>/</c>), <c>dockerDf</c> (Docker's own totals — an estimate) or
/// <c>none</c>.</param>
/// <param name="Note">How the guest usage was found, or why it was not.</param>
public sealed record VhdxReport(
    string Name,
    string Path,
    ByteFigure Size,
    FlagFigure Sparse,
    TextFigure Volume,
    string MappedBy,
    string Note,
    ByteFigure GuestUsed,
    ByteFigure Gap,
    NumberFigure GrowthBytesPerDay,
    ByteFigure PossibleGrowth);

/// <summary>A Windows volume that holds virtual disks: its free space, the disks on it, their summed growth per day, their known
/// possible growth and the disks whose possible growth is not known.</summary>
public sealed record HostingVolumeReport(
    string Letter,
    ByteFigure Size,
    ByteFigure Free,
    IReadOnlyList<string> Disks,
    NumberFigure GrowthBytesPerDay,
    long KnownPossibleGrowthBytes,
    IReadOnlyList<string> UnknownGrowth);

/// <summary>The disk section of a run detail and of <c>status --json</c> (E15.S2).</summary>
/// <param name="DistroName">This distro's Lxss name, or why it is not known.</param>
public sealed record DiskReport(
    bool Available,
    string? Reason,
    DateTimeOffset? SampledAt,
    TextFigure? DistroName,
    IReadOnlyList<VhdxReport>? Vhdx,
    IReadOnlyList<HostingVolumeReport>? Volumes)
{
    /// <summary>In <c>status --json</c>: the full run the section was carried from, when it ended and how old it is; absent in the
    /// run detail, whose run IS the basis.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public VerdictBasis? Basis { get; init; }

    /// <summary>E19.S1: the per-volume judgements of <c>disk.volumeFree</c> — every lettered Windows volume of the run's Windows disk sample,
    /// judged or not, from the same evaluation as the verdict, so a reader never parses the verdict's value text. Independent of the virtual
    /// disks: it is present even when <see cref="Available"/> is false. Absent in a detail written before E19.S1 and when the Windows disk
    /// sample listed no volume.</summary>
    public IReadOnlyList<LetteredVolumeReport>? Lettered { get; init; }
}

/// <summary>One lettered Windows volume as <c>disk.volumeFree</c> judged it. A figure that was not read is OMITTED (never 0); the reason rides in
/// <c>judgedBy</c>.</summary>
/// <param name="Letter">The drive letter, without the colon.</param>
/// <param name="SizeBytes">The volume size in bytes, or absent when it was not read.</param>
/// <param name="FreeBytes">The free space in bytes, or absent when it was not read.</param>
/// <param name="FreePercent">The free share in percent, to one decimal; absent when either figure was not read.</param>
/// <param name="Level">The volume's own level; <c>unknown</c> when it is not judged or a figure was not read.</param>
/// <param name="JudgedBy"><c>gib-or-percent</c>, <c>percent</c>, or <c>not judged: &lt;why&gt;</c>.</param>
public sealed record LetteredVolumeReport(string Letter, long? SizeBytes, long? FreeBytes, double? FreePercent, Level Level, string JudgedBy);

/// <summary>The domain facts turned into their wire shape — the one place they become available + value or reason.</summary>
public static class DiskReports
{
    public static DiskReport From(Reading<DiskFacts> facts) => facts switch
    {
        Reading<DiskFacts>.Available { Value: var f } => new(true, null, f.SampledAt, StatusReports.Text(f.DistroName), [.. f.Disks.Select(Vhdx)], [.. f.Volumes.Select(Volume)]),
        var unread => Unavailable(unread.ReasonOrEmpty),
    };

    /// <summary>The section with the volumes of <paramref name="volumes"/> listed as <see cref="DiskReport.Lettered"/> — omitted when the sample listed none
    /// (an unreadable Windows disk sample: absent is not an empty list).</summary>
    public static DiskReport From(Reading<DiskFacts> facts, VolumeFreeAssessment volumes) =>
        From(facts) with { Lettered = volumes.Volumes.Count > 0 ? Lettered(volumes.Volumes) : null };

    public static IReadOnlyList<LetteredVolumeReport> Lettered(IReadOnlyList<VolumeJudgement> judgements) => [.. judgements.Select(Lettered)];

    private static LetteredVolumeReport Lettered(VolumeJudgement v) =>
        new(v.Letter, Known(v.SizeBytes), Known(v.FreeBytes), v.FreePercent is Reading<double>.Available { Value: var percent } ? Math.Round(percent, 1) : null, v.Level, v.JudgedBy);

    private static long? Known(Reading<long> figure) => figure is Reading<long>.Available { Value: var value } ? value : null;

    public static DiskReport Unavailable(string reason) => new(false, reason, null, null, null, null);

    /// <summary>The wire spelling of a mapping: <c>mount</c>, <c>dockerDf</c>, <c>none</c>.</summary>
    public static string Spelling(MappedBy by) => by switch
    {
        MappedBy.Mount => "mount",
        MappedBy.DockerDf => "dockerDf",
        _ => "none",
    };

    private static VhdxReport Vhdx(VirtualDiskFacts d) =>
        new(
            d.Name,
            d.Path,
            StatusReports.Bytes(d.SizeBytes),
            HealthReports.Flag(d.Sparse),
            StatusReports.Text(d.Volume),
            Spelling(d.MappedBy),
            d.Note,
            StatusReports.Bytes(d.GuestUsedBytes),
            StatusReports.Bytes(d.GapBytes),
            StatusReports.Number(d.GrowthBytesPerDay.Map(Math.Round)),
            StatusReports.Bytes(d.PossibleGrowthBytes));

    private static HostingVolumeReport Volume(HostingVolumeFacts v) =>
        new(v.Letter, StatusReports.Bytes(v.SizeBytes), StatusReports.Bytes(v.FreeBytes), v.Disks, StatusReports.Number(v.GrowthBytesPerDay.Map(Math.Round)), v.KnownPossibleGrowthBytes, v.UnknownGrowth);
}
