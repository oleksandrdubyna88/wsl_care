using WslCare.Core.Collectors;

namespace WslCare.Core.Health;

/// <summary>How a virtual disk's guest usage was found (E15.S2, PLAN_disk_health.md).</summary>
public enum MappedBy
{
    /// <summary>This distro's own <c>/</c>: its <c>statvfs</c> is the guest's usage, and its size is the disk's virtual size.</summary>
    Mount,

    /// <summary>Docker's data disk: the sum of <c>docker system df</c>'s sizes — Docker's own totals, an estimate, not the filesystem.</summary>
    DockerDf,

    /// <summary>Not mapped: another distro, whose filesystem is not mounted here — or this distro's name is not known.</summary>
    None,
}

/// <summary>One virtual disk's measured size, as the history line keeps it — what the next full run's growth is measured
/// against.</summary>
public sealed record VirtualDiskSize(string Name, string Path, long SizeBytes);

/// <summary>The measured sizes of one full run (<c>SlowParts.VirtualDisks</c>, about 600 bytes a line).</summary>
public sealed record VirtualDiskSizes(DateTimeOffset SampledAt, IReadOnlyList<VirtualDiskSize> Disks);

/// <summary>
/// One virtual disk as E15.S2 judges it: the file, its hosting volume, and — each a <see cref="Reading{T}"/>, so an unknown is
/// never 0 — what its guest uses, the gap between the two, its growth per day since the previous full run, and how much more it
/// can still grow.
/// </summary>
/// <param name="Volume">The letter of the volume that holds the file (from its path), or why there is none.</param>
/// <param name="Note">How the guest usage was found, in a sentence; for <see cref="MappedBy.None"/>, why it was not.</param>
/// <param name="GapBytes">File size − guest used: what the file holds that the guest does not use.</param>
/// <param name="GrowthBytesPerDay">(size now − size then) ÷ days between two MEASURED sizes of the same path; negative is a shrink.</param>
/// <param name="PossibleGrowthBytes">How much more the file can grow: for <see cref="MappedBy.Mount"/> the guest filesystem's size
/// (the disk's virtual size) − the file size; unknown otherwise.</param>
public sealed record VirtualDiskFacts(
    string Name,
    string Path,
    Reading<long> SizeBytes,
    Reading<bool> Sparse,
    Reading<string> Volume,
    MappedBy MappedBy,
    string Note,
    Reading<long> GuestUsedBytes,
    Reading<long> GapBytes,
    Reading<double> GrowthBytesPerDay,
    Reading<long> PossibleGrowthBytes);

/// <summary>A Windows volume that holds at least one virtual disk.</summary>
/// <param name="Disks">The names of the virtual disks on it.</param>
/// <param name="GrowthBytesPerDay">Their summed growth per day — available only when every one of them has a growth.</param>
/// <param name="KnownPossibleGrowthBytes">The summed possible growth of the disks whose possible growth is known.</param>
/// <param name="UnknownGrowth">The disks whose possible growth is NOT known — while any is, free space is never judged <c>ok</c>.</param>
public sealed record HostingVolumeFacts(
    string Letter,
    Reading<long> SizeBytes,
    Reading<long> FreeBytes,
    IReadOnlyList<string> Disks,
    Reading<double> GrowthBytesPerDay,
    long KnownPossibleGrowthBytes,
    IReadOnlyList<string> UnknownGrowth);

/// <summary>The virtual disks and their hosting volumes of one full run (E15.S2).</summary>
/// <param name="SampledAt">When the Windows disk script was started.</param>
/// <param name="DistroName">This distro's Lxss name (<see cref="DistroIdentity"/>), or why it is not known.</param>
public sealed record DiskFacts(DateTimeOffset SampledAt, Reading<string> DistroName, IReadOnlyList<VirtualDiskFacts> Disks, IReadOnlyList<HostingVolumeFacts> Volumes)
{
    /// <summary>The measured sizes, for the history line.</summary>
    public VirtualDiskSizes Sizes =>
        new(SampledAt, [.. Disks.SelectMany(d => d.SizeBytes is Reading<long>.Available { Value: var size } ? [new VirtualDiskSize(d.Name, d.Path, size)] : Array.Empty<VirtualDiskSize>())]);
}
