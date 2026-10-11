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
}

/// <summary>The domain facts turned into their wire shape — the one place they become available + value or reason.</summary>
public static class DiskReports
{
    public static DiskReport From(Reading<DiskFacts> facts) => facts switch
    {
        Reading<DiskFacts>.Available { Value: var f } => new(true, null, f.SampledAt, StatusReports.Text(f.DistroName), [.. f.Disks.Select(Vhdx)], [.. f.Volumes.Select(Volume)]),
        var unread => Unavailable(unread.ReasonOrEmpty),
    };

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
