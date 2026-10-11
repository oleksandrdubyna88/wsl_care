using System.Globalization;

using WslCare.Core.Collectors;
using WslCare.Core.Docker;

namespace WslCare.Core.Health;

/// <summary>
/// E15.S2 (PLAN_disk_health.md, design §2–§3), PURE: one full run's Windows disk sample turned into <see cref="DiskFacts"/> — for each
/// virtual disk its hosting volume, what its guest uses, the gap, the growth per day and the possible growth; for each volume that
/// holds one, its free space against what its files can still grow. Read-only: it reads facts other parts already measured.
/// </summary>
/// <remarks>
/// <para><b>Mapping.</b> This distro's own disk is found by NAME (<see cref="DistroIdentity"/>), never by size: its guest usage is
/// <c>/</c>'s <c>statvfs</c> (<see cref="MappedBy.Mount"/>). Docker's data disk is mapped by the <c>docker system df</c> the full run
/// already ran (<see cref="MappedBy.DockerDf"/>) — Docker's own totals, an estimate. Every other distro is <see cref="MappedBy.None"/>:
/// its filesystem is not mounted here (D3), so a block device's size would identify it but say nothing about its usage.</para>
/// <para><b>Growth</b> is a difference of TWO measured sizes of the same path, divided by the days between them, and only when they
/// are at least <c>disk.growthMinHours</c> apart — 1 GiB ten minutes apart would read as 144 GiB a day.</para>
/// </remarks>
public static class VirtualDisks
{
    /// <summary>The note of a disk mapped by this distro's own <c>/</c>.</summary>
    public const string MountNote = "this distro's own / (statvfs): the guest's usage, and its size is the disk's virtual size";

    /// <summary>The note of Docker's data disk.</summary>
    public const string DockerNote = "Docker's own totals (docker system df: images, containers, local volumes, build cache) — an estimate, not the filesystem";

    public static Reading<DiskFacts> From(WindowsDiskSample sample, Reading<VolumeUsage> root, Reading<IReadOnlyList<DockerTotal>> dockerTotals, Reading<string> distro, Reading<VirtualDiskSizes> previous, TimeSpan growthMin) =>
        sample.Facts.Bind(facts => facts.Distros.Map(distros =>
        {
            var growth = new GrowthBasis(sample.SampledAt, previous, growthMin);
            var own = OwnDistro(distro, distros);
            IReadOnlyList<VirtualDiskFacts> disks =
            [
                .. distros.Select(d => Distro(d, own, root, growth)),
                .. Docker(facts.DockerDataDisk).Select(d => DockerDisk(d, dockerTotals, growth)),
            ];
            return new DiskFacts(sample.SampledAt, distro, disks, Volumes(disks, facts.Volumes));
        }));

    /// <summary>What a growth is measured against: this sample's instant, the previous sizes, and how far apart they must be.</summary>
    private sealed record GrowthBasis(DateTimeOffset Now, Reading<VirtualDiskSizes> Previous, TimeSpan Min);

    /// <summary>This distro's name when it is one the Lxss map lists; otherwise why no distro is this one.</summary>
    private static Reading<string> OwnDistro(Reading<string> distro, IReadOnlyList<WindowsVirtualDisk> distros) =>
        distro.Bind(name => distros.Any(d => Same(d.Name, name))
            ? Reading.Of(name)
            : Reading.Missing<string>($"this distro's recorded name, {name}, is not in the Lxss map ({string.Join(", ", distros.Select(d => d.Name))}), so no virtual disk is known to be this one"));

    private static IReadOnlyList<WindowsVirtualDisk> Docker(Reading<WindowsVirtualDisk> disk) =>
        disk is Reading<WindowsVirtualDisk>.Available { Value: var d } ? [d] : [];

    private static VirtualDiskFacts Distro(WindowsVirtualDisk disk, Reading<string> own, Reading<VolumeUsage> root, GrowthBasis growth) =>
        own is Reading<string>.Available { Value: var name } && Same(disk.Name, name)
            ? Facts(disk, MappedBy.Mount, MountNote, root.Map(r => r.UsedBytes), root.Bind(r => disk.SizeBytes.Map(size => Math.Max(0, r.TotalBytes - size))), growth)
            : Unmapped(disk, NotMounted(disk, own), growth);

    private static string NotMounted(WindowsVirtualDisk disk, Reading<string> own) =>
        own is Reading<string>.Available { Value: var name }
            ? $"{disk.Name} is not this distro ({name}): its file system is not mounted here, so what its guest uses is not known"
            : own.ReasonOrEmpty;

    private static VirtualDiskFacts Unmapped(WindowsVirtualDisk disk, string why, GrowthBasis growth) =>
        Facts(disk, MappedBy.None, why, Reading.Missing<long>(why), Reading.Missing<long>(why), growth);

    private static VirtualDiskFacts DockerDisk(WindowsVirtualDisk disk, Reading<IReadOnlyList<DockerTotal>> totals, GrowthBasis growth) =>
        Facts(disk, MappedBy.DockerDf, DockerNote, DockerUsed(totals), Reading.Missing<long>("Docker's totals say nothing about the size its data disk may reach"), growth);

    /// <summary>The sum of <c>docker system df</c>'s sizes — unknown when any type's size is (absent is never 0).</summary>
    private static Reading<long> DockerUsed(Reading<IReadOnlyList<DockerTotal>> totals) =>
        totals switch
        {
            Reading<IReadOnlyList<DockerTotal>>.Available { Value.Count: 0 } => Reading.Missing<long>("docker system df listed no type"),
            Reading<IReadOnlyList<DockerTotal>>.Available { Value: var list } => list.Aggregate(Reading.Of(0L), (sum, t) => Reading.Combine(sum, t.SizeBytes, (a, b) => a + b)),
            var unread => Reading.Missing<long>($"docker system df: {unread.ReasonOrEmpty}"),
        };

    private static VirtualDiskFacts Facts(WindowsVirtualDisk disk, MappedBy by, string note, Reading<long> used, Reading<long> possible, GrowthBasis growth) =>
        new(
            disk.Name,
            disk.Path,
            disk.SizeBytes,
            disk.Sparse,
            Letter(disk.Path),
            by,
            note,
            used,
            Reading.Combine(disk.SizeBytes, used, (size, u) => size - u),
            Growth(disk, growth),
            possible);

    /// <summary>The drive letter of a path like <c>C:\…</c>.</summary>
    private static Reading<string> Letter(string path) =>
        path is [var letter, ':', '\\', ..] && char.IsAsciiLetter(letter)
            ? Reading.Of(char.ToUpperInvariant(letter).ToString())
            : Reading.Missing<string>($"{path} is not on a lettered volume");

    private static Reading<double> Growth(WindowsVirtualDisk disk, GrowthBasis basis) =>
        basis.Previous switch
        {
            Reading<VirtualDiskSizes>.Available { Value: var previous } => disk.SizeBytes.Bind(size => Between(disk.Path, size, previous, basis)),
            var none => Reading.Missing<double>($"one measurement so far: growth needs two measured sizes ({none.ReasonOrEmpty})"),
        };

    /// <summary>(size now − size then) ÷ days — for the same path, measured both times, far enough apart.</summary>
    private static Reading<double> Between(string path, long size, VirtualDiskSizes previous, GrowthBasis basis)
    {
        var elapsed = basis.Now - previous.SampledAt;
        var before = (previous.Disks ?? []).FirstOrDefault(d => d is not null && Same(d.Path, path));
        return before switch
        {
            null => Reading.Missing<double>(string.Create(CultureInfo.InvariantCulture, $"the previous measurement ({previous.SampledAt.UtcDateTime:yyyy-MM-dd HH:mm}Z) did not measure {path}: growth needs two measured sizes of the same file")),
            _ when elapsed < basis.Min => Reading.Missing<double>(string.Create(CultureInfo.InvariantCulture, $"too close to the previous measurement ({elapsed.TotalMinutes:0} min apart; disk.growthMinHours is {basis.Min.TotalHours:0} h)")),
            { SizeBytes: var then } => Reading.Of((size - then) / elapsed.TotalDays),
        };
    }

    private static IReadOnlyList<HostingVolumeFacts> Volumes(IReadOnlyList<VirtualDiskFacts> disks, Reading<IReadOnlyList<WindowsVolume>> volumes) =>
        [.. disks
            .SelectMany(d => d.Volume is Reading<string>.Available { Value: var letter } ? [(Letter: letter, Disk: d)] : Array.Empty<(string Letter, VirtualDiskFacts Disk)>())
            .GroupBy(x => x.Letter, StringComparer.Ordinal)
            .Select(g => Hosting(g.Key, [.. g.Select(x => x.Disk)], volumes))];

    private static HostingVolumeFacts Hosting(string letter, IReadOnlyList<VirtualDiskFacts> disks, Reading<IReadOnlyList<WindowsVolume>> volumes)
    {
        var volume = volumes.Bind(list => list.FirstOrDefault(v => Same(v.Letter, letter)) is { } found
            ? Reading.Of(found)
            : Reading.Missing<WindowsVolume>($"the Windows disk script listed no volume {letter}:"));
        return new(
            letter,
            volume.Bind(v => v.SizeBytes),
            volume.Bind(v => v.FreeBytes),
            [.. disks.Select(d => d.Name)],
            SummedGrowth(disks),
            disks.Sum(d => d.PossibleGrowthBytes.ValueOr(0)),
            [.. disks.Where(d => !d.PossibleGrowthBytes.IsAvailable).Select(d => d.Name)]);
    }

    /// <summary>The disks' summed growth per day — only when every one of them has one.</summary>
    private static Reading<double> SummedGrowth(IReadOnlyList<VirtualDiskFacts> disks) =>
        disks.Aggregate(Reading.Of(0d), (sum, d) => Reading.Combine(sum, d.GrowthBytesPerDay, (a, b) => a + b)) switch
        {
            Reading<double>.Available summed => summed,
            _ => Reading.Missing<double>($"the growth of {string.Join(", ", disks.Where(d => !d.GrowthBytesPerDay.IsAvailable).Select(d => d.Name))} is not known yet"),
        };

    /// <summary>Windows names — a distro, a path, a drive letter — compare without case.</summary>
    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
